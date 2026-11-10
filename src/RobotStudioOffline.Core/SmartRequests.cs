using System.Text.Json;

namespace RobotStudioOffline.Core;

public sealed record SmartPropertyChange(string Name, JsonElement Value);
public sealed record SmartRequest(string Action, string? Template = null, string? Name = null,
    string? Component = null, SmartPropertyChange[]? Properties = null, Vector3D? Position = null,
    QuaternionD? Quaternion = null, int Offset = 0, int Limit = 50);

public static class SmartRequests
{
    public static readonly string[] Operations = ["smart_component"];
    public static readonly IReadOnlyList<string> Templates = Array.AsReadOnly(new[] {
        "LinearMover", "LinearMover2", "Rotator", "Rotator2", "ConveyorControl",
        "LineSensor", "PlaneSensor", "VolumeSensor", "Attacher", "Detacher" });

    public static bool IsSensorGraphic(string template, string name, bool visibleInTree) =>
        !visibleInTree && name == "_sensorPart_" && template is "LineSensor" or "PlaneSensor" or "VolumeSensor";

    public static SmartRequest Parse(string operation, JsonElement args)
    {
        if (operation != "smart_component") throw new BridgeException("unknown_operation", "Unknown smart component operation.", 404);
        if (args.ValueKind != JsonValueKind.Object) throw Invalid("Arguments must be an object.");
        RequestParser.RejectControllerAccess(args);
        var action = RequestParser.ReadName(args, "action") ?? "catalog";
        string Required(string field) => RequestParser.ReadName(args, field) ?? throw Invalid(field + " is required.");
        string Template()
        {
            var template = Required("template");
            return Templates.Contains(template, StringComparer.Ordinal) ? template : throw Invalid("Select an exact ABB template name from catalog.");
        }
        switch (action)
        {
            case "catalog":
                RequestParser.CheckProperties(args, ["action", "template"], "arguments");
                return new(action, args.TryGetProperty("template", out _) ? Template() : null);
            case "list":
                RequestParser.CheckProperties(args, ["action", "offset", "limit"], "arguments");
                return new(action, Offset: SceneRequests.Integer(args, "offset", 0, int.MaxValue, 0),
                    Limit: SceneRequests.Integer(args, "limit", 1, 200, 50));
            case "inspect":
            case "delete":
                RequestParser.CheckProperties(args, ["action", "component"], "arguments");
                return new(action, Component: Required("component"));
            case "create":
                RequestParser.CheckProperties(args, ["action", "template", "name", "properties", "position", "quaternion"], "arguments");
                break;
            case "configure":
                RequestParser.CheckProperties(args, ["action", "component", "properties", "position", "quaternion"], "arguments");
                if (!args.TryGetProperty("properties", out _) && !args.TryGetProperty("position", out _) && !args.TryGetProperty("quaternion", out _))
                    throw Invalid("Specify properties, position or quaternion to configure.");
                break;
            default: throw Invalid("Unknown smart_component action.");
        }
        Vector3D? position = null;
        if (args.TryGetProperty("position", out _))
        {
            var p = RequestParser.ReadNumbers(args, "position", 3);
            position = new(p[0], p[1], p[2]);
        }
        return new(action, action == "create" ? Template() : null, action == "create" ? Required("name") : null,
            action == "configure" ? Required("component") : null, ParseProperties(args), position,
            args.TryGetProperty("quaternion", out _) ? RequestParser.ReadUnitQuaternion(args) : null);
    }

    private static SmartPropertyChange[] ParseProperties(JsonElement args)
    {
        if (!args.TryGetProperty("properties", out var changes)) return [];
        if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() is < 1 or > 32)
            throw Invalid("properties must contain 1 to 32 named changes.");
        var parsed = new List<SmartPropertyChange>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes.EnumerateArray())
        {
            RequestParser.CheckProperties(change, ["name", "value"], "property");
            var name = RequestParser.ReadName(change, "name") ?? throw Invalid("name is required for each property.");
            if (!names.Add(name) || !change.TryGetProperty("value", out var value)) throw Invalid("Supply one value per distinct property name.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                RequestParser.CheckProperties(value, ["object"], "value");
                _ = RequestParser.ReadName(value, "object") ?? throw Invalid("A station object reference requires an object name or ID.");
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                _ = RequestParser.ReadNumbers(change, "value", 3);
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                _ = RequestParser.ReadName(change, "value");
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number)) throw Invalid("Property numbers must be finite.");
            }
            else if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw Invalid("Unsupported smart component property value.");
            parsed.Add(new(name, value.Clone()));
        }
        return parsed.ToArray();
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
