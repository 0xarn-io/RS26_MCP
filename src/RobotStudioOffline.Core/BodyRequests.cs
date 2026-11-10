using System.Text.Json;

namespace RobotStudioOffline.Core;

public sealed record BodyRequest(string Action, string Selector, string? Parent = null, string Frame = "world",
    string? Robot = null, Vector3D? Position = null, QuaternionD? Orientation = null)
{
    public RigidFrame MovePose(RigidFrame worldFromFrame, RigidFrame currentWorld)
    {
        var prior = FrameMath.RelativeTo(worldFromFrame, currentWorld);
        return FrameMath.Compose(worldFromFrame, new(Position ?? prior.Position, Orientation ?? prior.Orientation));
    }
}

public static class BodyRequests
{
    public static readonly string[] Operations = ["body_edit", "collision_set_delete"];

    public static BodyRequest Parse(string operation, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw Invalid("Arguments must be an object.");
        RequestParser.RejectControllerAccess(args);
        string Required(string key) => RequestParser.ReadName(args, key) ?? throw Invalid(key + " is required.");
        if (operation == "collision_set_delete")
        {
            RequestParser.CheckProperties(args, ["set"], "arguments");
            return new("delete_collision_set", Required("set"));
        }
        if (operation != "body_edit") throw new BridgeException("unknown_operation", "Unknown body operation.", 404);
        var action = Required("action");
        var selector = Required("object");
        switch (action)
        {
            case "state":
            case "delete":
                RequestParser.CheckProperties(args, ["action", "object"], "arguments");
                return new(action, selector);
            case "attach":
            case "release":
                RequestParser.CheckProperties(args, ["action", "object", "parent"], "arguments");
                return new(action, selector, action == "attach" ? Required("parent") : RequestParser.ReadName(args, "parent"));
            case "move":
                RequestParser.CheckProperties(args, ["action", "object", "frame", "robot", "position", "quaternion"], "arguments");
                var frame = RequestParser.ReadName(args, "frame") ?? "world";
                var robot = RequestParser.ReadName(args, "robot");
                if (frame is not ("world" or "base") || (frame != "base" && robot != null))
                    throw Invalid("Use world or base. A robot selector applies only to the base frame.");
                Vector3D? position = null;
                if (args.TryGetProperty("position", out _))
                {
                    var xyz = RequestParser.ReadNumbers(args, "position", 3);
                    position = new(xyz[0], xyz[1], xyz[2]);
                }
                QuaternionD? orientation = args.TryGetProperty("quaternion", out _) ? RequestParser.ReadUnitQuaternion(args) : null;
                if (position == null && orientation == null) throw Invalid("Provide position and/or quaternion to move the object.");
                return new(action, selector, Frame: frame, Robot: robot, Position: position, Orientation: orientation);
            default: throw Invalid("Unknown body_edit action.");
        }
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
