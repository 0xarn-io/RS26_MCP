using System.Text.Json;

namespace RobotStudioOffline.Core;

public sealed record LogicEndpoint(string? Component, string? Robot, string Member);
public sealed record LogicRequest(string Action, string? Kind = null, LogicEndpoint? Source = null,
    LogicEndpoint? Target = null, int? Value = null, int Offset = 0, int Limit = 50);

public static class LogicRequests
{
    public static LogicRequest Parse(JsonElement args)
    {
        RequestParser.RejectControllerAccess(args);
        var action = RequestParser.ReadName(args, "action") ?? "list";
        if (action == "list")
        {
            RequestParser.CheckProperties(args, ["action", "offset", "limit"], "arguments");
            return new(action, Offset: SceneRequests.Integer(args, "offset", 0, int.MaxValue, 0),
                Limit: SceneRequests.Integer(args, "limit", 1, 200, 50));
        }
        if (action is "set_input" or "pulse")
        {
            string[] fields = action == "pulse" ? ["action", "target"] : ["action", "target", "value"];
            RequestParser.CheckProperties(args, fields, "arguments");
            var target = Endpoint(args, "target");
            if (target.Component == null) throw Invalid("Select a station Smart Component input.");
            if (action == "pulse") return new(action, Target: target);
            if (!args.TryGetProperty("value", out _)) throw Invalid("set_input requires value (0 or 1).");
            return new(action, Target: target, Value: SceneRequests.Integer(args, "value", 0, 1, 0));
        }
        if (action is not ("connect" or "disconnect")) throw Invalid("Unknown station_logic action.");
        RequestParser.CheckProperties(args, ["action", "kind", "source", "target"], "arguments");
        var kind = RequestParser.ReadName(args, "kind") ?? "signal";
        if (kind is not ("signal" or "property")) throw Invalid("kind must be signal or property.");
        var from = Endpoint(args, "source");
        var to = Endpoint(args, "target");
        if (kind == "property" && (from.Component == null || to.Component == null))
            throw Invalid("Property bindings require two station Smart Component endpoints.");
        if (from.Robot != null && to.Robot != null) throw Invalid("A signal connection requires at least one Smart Component.");
        if (from == to) throw Invalid("Select distinct source and target members.");
        return new(action, kind, from, to);
    }

    private static LogicEndpoint Endpoint(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var node)) throw Invalid(key + " is required.");
        RequestParser.CheckProperties(node, ["component", "robot", "member"], key);
        var component = RequestParser.ReadName(node, "component");
        var robot = RequestParser.ReadName(node, "robot");
        if ((component == null) == (robot == null)) throw Invalid(key + " requires exactly one component or station robot.");
        return new(component, robot, RequestParser.ReadName(node, "member") ?? throw Invalid(key + ".member is required."));
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
