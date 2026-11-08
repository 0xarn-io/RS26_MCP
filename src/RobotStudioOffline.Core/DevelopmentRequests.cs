using System.Text.Json;
using System.Text.Json.Nodes;

namespace RobotStudioOffline.Core;

public abstract record DevelopmentRequest;
public sealed record PathDetailsRequest(string? Robot, string? Path) : DevelopmentRequest;
public sealed record BatchTargetRequest(string Id, BridgeRequest Request);
public sealed record ValidateTargetsRequest(BatchTargetRequest[] Targets, bool CheckCollisions) : DevelopmentRequest;
public sealed record PreviewTargetRequest(string Id, string? Target, RobTarget? RobTarget, string? WorkObject);
public sealed record PreviewTargetsRequest(string Action, string? Robot, PreviewTargetRequest[] Targets) : DevelopmentRequest;
public sealed record TargetEdit(string Name, RobTarget RobTarget, string? WorkObject, bool Create);
public sealed record EditTargetsRequest(string? Robot, TargetEdit[] Changes) : DevelopmentRequest;
public sealed record DataSelection(string Name, string Kind);
public sealed record DataCompareRequest(string? Robot, DataSelection[] Data) : DevelopmentRequest;
public sealed record DataSyncRequest(string? Robot, string Direction, DataSelection[] Data) : DevelopmentRequest;

/// <summary>Strict, SDK-independent validation for bounded station development operations.</summary>
public static class DevelopmentRequests
{
    public static readonly string[] Operations = ["path_details", "validate_targets", "preview_targets", "edit_targets", "data_compare", "data_sync"];

    public static DevelopmentRequest Parse(string operation, JsonElement args)
    {
        RequestParser.RejectControllerAccess(args);
        return operation switch
        {
            "path_details" => Path(args),
            "validate_targets" => Batch(args),
            "preview_targets" => Preview(args),
            "edit_targets" => Edit(args),
            "data_compare" => Compare(args),
            "data_sync" => Sync(args),
            _ => throw new BridgeException("unknown_operation", "Unknown development operation.", 404)
        };
    }

    private static PathDetailsRequest Path(JsonElement args)
    {
        Check(args, ["robot", "path"]);
        return new(Name(args, "robot"), Name(args, "path"));
    }

    private static ValidateTargetsRequest Batch(JsonElement args)
    {
        Check(args, ["robot", "tool", "workObject", "checkCollisions", "targets"]);
        var robot = Name(args, "robot");
        var tool = Name(args, "tool");
        var workObject = Name(args, "workObject");
        var collisions = Bool(args, "checkCollisions");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = Items(args, "targets", 100).Select((item, index) =>
        {
            Check(item, ["id", "tool", "workObject", "target", "robtarget", "jointtarget"]);
            var id = Id(item, index, ids);
            var merged = new JsonObject();
            if (robot is not null) merged["robot"] = robot;
            if (tool is not null) merged["tool"] = tool;
            if (workObject is not null) merged["workObject"] = workObject;
            foreach (var field in item.EnumerateObject())
                if (field.Name != "id") merged[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            return new BatchTargetRequest(id, RequestParser.ParseJson("jump", merged.ToJsonString()));
        }).ToArray();
        return new(items, collisions);
    }

    private static PreviewTargetsRequest Preview(JsonElement args)
    {
        Check(args, ["action", "robot", "workObject", "targets"]);
        var action = Name(args, "action") ?? throw Invalid("preview_targets.action is required.");
        if (action == "clear")
        {
            Check(args, ["action"]);
            return new(action, null, []);
        }
        if (action != "show") throw Invalid("preview_targets.action must be show or clear.");
        var robot = Name(args, "robot");
        var workObject = Name(args, "workObject");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = Items(args, "targets", 100).Select((item, index) =>
        {
            Check(item, ["id", "target", "robtarget", "workObject"]);
            var id = Id(item, index, ids);
            var target = Name(item, "target");
            var pose = item.TryGetProperty("robtarget", out var raw) ? RequestParser.ReadRobTarget(raw) : null;
            if ((target is null) == (pose is null)) throw Invalid("Each preview needs exactly one target or robtarget.");
            var frame = Name(item, "workObject") ?? workObject;
            if (pose is not null && frame is null) throw Invalid("An explicit preview robtarget requires workObject.");
            return new PreviewTargetRequest(id, target, pose, frame);
        }).ToArray();
        return new(action, robot, items);
    }

    private static EditTargetsRequest Edit(JsonElement args)
    {
        Check(args, ["robot", "workObject", "changes"]);
        var robot = Name(args, "robot");
        var workObject = Name(args, "workObject");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var changes = Items(args, "changes", 50).Select(item =>
        {
            Check(item, ["name", "robtarget", "workObject", "create"]);
            var name = Name(item, "name") ?? throw Invalid("Each edit requires name.");
            if (!names.Add(name)) throw Invalid("Duplicate edit name: " + name);
            if (!item.TryGetProperty("robtarget", out var raw)) throw Invalid("Each edit requires robtarget.");
            var pose = RequestParser.ReadRobTarget(raw);
            var frame = Name(item, "workObject") ?? workObject;
            var create = Bool(item, "create");
            if (create && frame is null) throw Invalid("Creating a target requires workObject.");
            return new TargetEdit(name, pose, frame, create);
        }).ToArray();
        return new(robot, changes);
    }

    private static DataCompareRequest Compare(JsonElement args)
    {
        Check(args, ["robot", "data"]);
        return new(Name(args, "robot"), Selections(args));
    }

    private static DataSyncRequest Sync(JsonElement args)
    {
        Check(args, ["robot", "direction", "data"]);
        var direction = Name(args, "direction");
        if (direction is not ("to_station" or "to_virtual_controller"))
            throw Invalid("data_sync.direction must explicitly be to_station or to_virtual_controller.");
        return new(Name(args, "robot"), direction, Selections(args));
    }

    private static DataSelection[] Selections(JsonElement args)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return Items(args, "data", 100).Select(item =>
        {
            Check(item, ["name", "kind"]);
            var name = Name(item, "name") ?? throw Invalid("Each selected data item requires name.");
            var kind = Name(item, "kind");
            if (kind is not ("target" or "tool" or "workObject"))
                throw Invalid("Data kind must be target, tool or workObject.");
            if (!keys.Add(kind + "/" + name)) throw Invalid("Duplicate selected data item.");
            return new DataSelection(name, kind);
        }).ToArray();
    }

    private static JsonElement[] Items(JsonElement args, string name, int maximum)
    {
        if (!args.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 || items.GetArrayLength() > maximum)
            throw Invalid($"{name} must contain 1..{maximum} items.");
        return items.EnumerateArray().ToArray();
    }

    private static string Id(JsonElement item, int index, HashSet<string> used)
    {
        var id = Name(item, "id") ?? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!used.Add(id)) throw Invalid("Duplicate item id: " + id);
        return id;
    }
    private static bool Bool(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid(property + " must be a boolean.");
        return value.GetBoolean();
    }
    private static void Check(JsonElement value, string[] names) => RequestParser.CheckProperties(value, names, "arguments");
    private static string? Name(JsonElement value, string name) => RequestParser.ReadName(value, name);
    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
