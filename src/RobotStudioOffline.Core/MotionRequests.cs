using System.Text.Json;

namespace RobotStudioOffline.Core;

public sealed record BodyMotionRequest(string Action, string? Part, Vector3D DeltaMm, int DurationMs)
{
    public double TravelFraction(double elapsedMs)
    {
        if (!double.IsFinite(elapsedMs) || elapsedMs < 0 || DurationMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMs));
        return Math.Min(1, elapsedMs / DurationMs);
    }
}

public static class MotionRequests
{
    public static readonly string[] Operations = ["body_motion"];
    public const int MaxDurationMs = 3_600_000;
    public const int MaxTravelMmPerAxis = 1_000_000;

    public static BodyMotionRequest Parse(string operation, JsonElement args)
    {
        if (operation != "body_motion") throw new BridgeException("unknown_operation", "Unknown body motion operation.", 404);
        RequestParser.RejectControllerAccess(args);
        var action = RequestParser.ReadName(args, "action") ?? "status";
        RequestParser.CheckProperties(args, action == "start" ? ["action", "part", "deltaMm", "durationMs"] : ["action", "part"], "arguments");
        var part = RequestParser.ReadName(args, "part");
        if (action == "status") return new(action, part, default, 0);
        if (part == null) throw Invalid("part is required.");
        if (action == "stop") return new(action, part, default, 0);
        if (action != "start") throw Invalid("action must be start, stop or status.");
        var delta = RequestParser.ReadNumbers(args, "deltaMm", 3);
        if (delta.All(v => v == 0) || delta.Any(v => Math.Abs(v) > MaxTravelMmPerAxis))
            throw Invalid("deltaMm must be a nonzero displacement within ±1,000,000 mm per axis.");
        var duration = SceneRequests.Integer(args, "durationMs", 1, MaxDurationMs, 0);
        if (duration == 0) throw Invalid("durationMs is required.");
        return new(action, part, new(delta[0], delta[1], delta[2]), duration);
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
