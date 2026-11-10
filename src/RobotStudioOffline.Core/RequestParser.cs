using System.Net;
using System.Text.Json;

namespace RobotStudioOffline.Core;

/// <summary>Single strict input boundary used by both transports before any SDK call.</summary>
public static class RequestParser
{
    private static readonly HashSet<string> ForbiddenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "controller", "controllerId", "controllerName", "controllerAddress", "controllerIp",
        "ip", "ipAddress", "address", "host", "hostname", "url", "uri", "endpoint", "port",
        "network", "remote", "connection", "connectionString"
    };

    public static BridgeRequest ParseJson(string operation, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return Parse(operation, document.RootElement);
        }
        catch (JsonException error)
        {
            throw new BridgeException("invalid_json", $"Request must be valid JSON: {error.Message}");
        }
    }

    public static BridgeRequest Parse(string operation, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw Invalid("Arguments must be a JSON object.");
        RejectControllerAccess(arguments);
        if (BodyRequests.Operations.Contains(operation)) return new() { Operation = operation, BodyEdit = BodyRequests.Parse(operation, arguments) };
        if (MotionRequests.Operations.Contains(operation)) return new() { Operation = operation, Motion = MotionRequests.Parse(operation, arguments) };
        if (SmartRequests.Operations.Contains(operation)) return new() { Operation = operation, Smart = SmartRequests.Parse(operation, arguments) };
        if (operation == "station_logic") return new() { Operation = operation, Logic = LogicRequests.Parse(arguments) };
        if (EngineeringRequests.Operations.Contains(operation))
            return new() { Operation = operation, Engineering = EngineeringRequests.Parse(operation, arguments) };
        if (SceneRequests.Operations.Contains(operation)) { _ = SceneRequests.Parse(operation, arguments); return new() { Operation = operation }; }
        if (RapidModuleRequests.Operations.Contains(operation)) { _ = RapidModuleRequests.Parse(operation, arguments); return new() { Operation = operation }; }
        if (RunRequests.Operations.Contains(operation))
        {
            _ = RunRequests.Parse(operation, arguments);
            return new BridgeRequest { Operation = operation };
        }
        if (GeometryRequests.Operations.Contains(operation))
        {
            _ = GeometryRequests.Parse(operation, arguments);
            return new BridgeRequest { Operation = operation };
        }
        if (IoRequests.Operations.Contains(operation))
        {
            _ = IoRequests.Parse(operation, arguments);
            return new BridgeRequest { Operation = operation };
        }
        if (DevelopmentRequests.Operations.Contains(operation))
        {
            _ = DevelopmentRequests.Parse(operation, arguments);
            return new BridgeRequest { Operation = operation };
        }
        string[] allowed = operation switch
        {
            "station" or "screenshot" => [],
            "robot_pose" => ["robot", "tool", "workObject"],
            "reach" => ["robot", "tool", "workObject", "target", "robtarget"],
            "jump" => ["robot", "tool", "workObject", "target", "robtarget", "jointtarget", "applyTo"],
            "collisions" => ["robot", "tool", "workObject", "target", "robtarget", "jointtarget"],
            "simulation" => ["action"],
            _ => throw new BridgeException("unknown_operation", $"Unknown operation '{operation}'.", 404)
        };
        CheckProperties(arguments, allowed, "arguments");
        var request = new BridgeRequest
        {
            Operation = operation,
            Robot = ReadName(arguments, "robot"),
            Tool = ReadName(arguments, "tool"),
            WorkObject = ReadName(arguments, "workObject"),
            Target = ReadName(arguments, "target"),
            RobTarget = arguments.TryGetProperty("robtarget", out var target) ? ReadRobTarget(target) : null,
            JointTarget = arguments.TryGetProperty("jointtarget", out var joints) ? ReadJointTarget(joints) : null,
            ApplyTo = ReadName(arguments, "applyTo"),
            Action = ReadName(arguments, "action")
        };
        if (request.ApplyTo is not (null or "station" or "virtual")) throw Invalid("jump.applyTo must be station or virtual.");
        if (operation == "simulation")
        {
            if (request.Action is not ("state" or "start" or "stop" or "reset"))
                throw Invalid("simulation.action must be state, start, stop, or reset.");
        }
        var poseCount = (request.Target is not null ? 1 : 0) + (request.RobTarget is not null ? 1 : 0) +
                        (request.JointTarget is not null ? 1 : 0);
        if (poseCount > 1)
            throw Invalid("Specify exactly one of target, robtarget, or jointtarget.");
        if (operation is "reach" or "jump" && poseCount != 1)
            throw Invalid($"{operation} requires a target or pose.");
        if (operation == "robot_pose" || request.RobTarget is not null)
        {
            RequireName(request.Tool, "tool");
            RequireName(request.WorkObject, "workObject");
        }
        if (request.Target is not null)
            RequireName(request.Tool, "tool");
        if (operation == "collisions" && poseCount == 0 && (request.Tool is not null || request.WorkObject is not null))
            throw Invalid("tool and workObject apply only when checking a collision pose.");
        return request;
    }

    internal static RobTarget ReadRobTarget(JsonElement value)
    {
        CheckProperties(value, ["position", "quaternion", "configuration", "externalAxes"], "robtarget");
        var position = ReadNumbers(value, "position", 3);
        var quaternion = ReadUnitQuaternion(value).ToArray();
        if (!value.TryGetProperty("configuration", out var configuration) ||
            configuration.ValueKind != JsonValueKind.Array || configuration.GetArrayLength() != 4)
            throw Invalid("robtarget.configuration must be four integers [cf1,cf4,cf6,cfx].");
        var config = new int[4];
        var index = 0;
        foreach (var component in configuration.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number || !component.TryGetInt32(out config[index++]))
                throw Invalid("robtarget.configuration must contain 32-bit integers.");
        }
        return new(position, quaternion, config,
            value.TryGetProperty("externalAxes", out _) ? ReadNumbers(value, "externalAxes", 6) : null);
    }

    private static JointTarget ReadJointTarget(JsonElement value)
    {
        CheckProperties(value, ["robotAxes", "externalAxes"], "jointtarget");
        return new(ReadNumbers(value, "robotAxes", 6),
            value.TryGetProperty("externalAxes", out _) ? ReadNumbers(value, "externalAxes", 6) : null);
    }

    internal static QuaternionD ReadUnitQuaternion(JsonElement obj)
    {
        var components = ReadNumbers(obj, "quaternion", 4);
        var norm = Math.Sqrt(components.Sum(component => component * component));
        if (!double.IsFinite(norm) || Math.Abs(norm - 1.0) > 1e-6)
            throw Invalid("quaternion must be a unit quaternion [w,x,y,z] (norm tolerance 1e-6).");
        return FrameMath.Normalize(new(components[0], components[1], components[2], components[3]));
    }

    internal static double[] ReadNumbers(JsonElement obj, string property, int length)
    {
        if (!obj.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != length)
            throw Invalid($"{property} must be an array of {length} finite numbers.");
        var values = new double[length];
        var index = 0;
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw Invalid($"{property} must contain only finite numbers.");
            values[index++] = number;
        }
        return values;
    }

    internal static string? ReadName(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"{property} must be a nonempty string.");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl))
            throw Invalid($"{property} must be a nonempty name of at most 256 characters, with no control characters.");
        return text;
    }

    internal static void CheckProperties(JsonElement value, string[] allowed, string path)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid($"{path} must be a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw Invalid($"Duplicate property '{path}.{property.Name}'.");
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid($"Unknown property '{path}.{property.Name}'.");
        }
    }

    internal static void RejectControllerAccess(JsonElement value, int depth = 0)
    {
        if (depth > 32) throw Invalid("Request exceeds maximum nesting depth.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (ForbiddenProperties.Contains(property.Name) || property.Name.Contains("controller", StringComparison.OrdinalIgnoreCase))
                        throw ControllerForbidden();
                    RejectControllerAccess(property.Value, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in value.EnumerateArray()) RejectControllerAccess(child, depth + 1);
                break;
            case JsonValueKind.String:
                var host = value.GetString()!;
                if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                    host = uri.DnsSafeHost;
                if (IPAddress.TryParse(host, out var address) && address.ToString().Equals(host, StringComparison.OrdinalIgnoreCase) &&
                    !IPAddress.IsLoopback(address)) throw ControllerForbidden();
                break;
        }
    }

    private static void RequireName(string? value, string name)
    {
        if (value is null) throw Invalid($"{name} is required for this operation.");
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
    private static BridgeException ControllerForbidden() => new("real_controller_permission_required",
        "No access was attempted. Real-controller operations require explicit permission from the owner in RobotStudio.", 403);
}
