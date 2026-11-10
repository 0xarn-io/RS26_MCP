using System.Text.Json;
using System.Text.Json.Nodes;

namespace RobotStudioOffline.Core;

public abstract record SceneRequest;
public sealed record GeometryQuery(string? Match, int Offset, int Limit) : SceneRequest;
public sealed record RayQuery(string Tool, string? Robot, Vector3D Origin, Vector3D Direction, double MaxDistance,
    string[] Parts, string[] Exclude) : SceneRequest;
public sealed record SetCollisionRequest(string Name, string[] First, string[] Second, double NearMissMm) : SceneRequest;
public sealed record SimulationSpeedRequest(double? Factor, double? StepMs) : SceneRequest;
public sealed record RunViewRequest(string RunId, string View, RunAssertion[] Assertions) : SceneRequest;

public static class SceneRequests
{
    public static readonly string[] Operations = ["geometry", "raycast", "collision_set", "simulation_speed", "run_view"];

    public static SceneRequest Parse(string operation, JsonElement args)
    {
        RequestParser.RejectControllerAccess(args);
        string? Name(string key) => RequestParser.ReadName(args, key);
        string Required(string key) => Name(key) ?? throw Invalid(key + " is required.");
        switch (operation)
        {
            case "geometry":
                RequestParser.CheckProperties(args, ["match", "offset", "limit"], "arguments");
                return new GeometryQuery(Name("match"), Integer(args, "offset", 0, int.MaxValue, 0), Integer(args, "limit", 1, 500, 100));
            case "raycast":
                RequestParser.CheckProperties(args, ["robot", "tool", "origin", "direction", "maxDistance", "parts", "exclude"], "arguments");
                var origin = Vector(args, "origin");
                var direction = Vector(args, "direction");
                var scale = direction.ToArray().Max(Math.Abs);
                if (scale == 0) throw Invalid("direction cannot be zero.");
                var scaled = new Vector3D(direction.X / scale, direction.Y / scale, direction.Z / scale);
                var length = Math.Sqrt(scaled.ToArray().Sum(x => x * x));
                return new RayQuery(Required("tool"), Name("robot"), origin,
                    new(scaled.X / length, scaled.Y / length, scaled.Z / length),
                    Number(args, "maxDistance", 0, 1_000_000, false), Names(args, "parts"), Names(args, "exclude"));
            case "collision_set":
                RequestParser.CheckProperties(args, ["name", "first", "second", "nearMissMm"], "arguments");
                return new SetCollisionRequest(Required("name"), Names(args, "first", true), Names(args, "second", true),
                    args.TryGetProperty("nearMissMm", out _) ? Number(args, "nearMissMm", 0, 1_000_000, true) : 0);
            case "simulation_speed":
                RequestParser.CheckProperties(args, ["factor", "stepMs"], "arguments");
                return new SimulationSpeedRequest(args.TryGetProperty("factor", out _) ? Number(args, "factor", 0, 10, false) : null,
                    args.TryGetProperty("stepMs", out _) ? Number(args, "stepMs", 1, 100, true) : null);
            case "run_view":
                RequestParser.CheckProperties(args, ["runId", "view", "assertions"], "arguments");
                var view = Required("view");
                if (view is not ("top" or "side")) throw Invalid("view must be top or side.");
                var reviewArgs = JsonNode.Parse(args.GetRawText())!.AsObject();
                reviewArgs.Remove("view");
                var review = (RunReviewRequest)RunRequests.Parse("run_review", JsonSerializer.SerializeToElement(reviewArgs));
                return new RunViewRequest(review.RunId, view, review.Assertions);
            default: throw new BridgeException("unknown_operation", "Unknown scene operation.", 404);
        }
    }

    private static Vector3D Vector(JsonElement args, string key)
    {
        var v = RequestParser.ReadNumbers(args, key, 3);
        return new(v[0], v[1], v[2]);
    }

    private static double Number(JsonElement args, string key, double min, double max, bool inclusive)
    {
        if (!args.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var n) ||
            !double.IsFinite(n) || (inclusive ? n < min : n <= min) || n > max) throw Invalid("Invalid " + key + ".");
        return n;
    }

    internal static int Integer(JsonElement args, string key, int min, int max, int fallback)
    {
        if (!args.TryGetProperty(key, out var v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max) throw Invalid("Invalid " + key + ".");
        return n;
    }

    private static string[] Names(JsonElement args, string key, bool required = false)
    {
        if (!required && !args.TryGetProperty(key, out _)) return [];
        if (!args.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 64)
            throw Invalid(key + " must contain 1 to 64 selectors.");
        var result = list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
        if (result.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256 || s.Any(char.IsControl)) || result.Distinct().Count() != result.Length)
            throw Invalid(key + " requires distinct nonempty selectors.");
        return result!;
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
