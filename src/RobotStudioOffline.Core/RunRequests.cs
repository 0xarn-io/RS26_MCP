using System.Text.Json;

namespace RobotStudioOffline.Core;

public abstract record RunRequest;
public sealed record TcpRecordRequest(string Action, string? RunId = null, string? Robot = null, string? Tool = null,
    string? WorkObject = null, int RateHz = 25, int MaxSamples = 15000, string? Phase = null,
    string? PhaseSignal = null, string? LiveFrameSignal = null, JsonElement? Command = null) : RunRequest
{
    public RunAssertion[] Assertions { get; init; } = [];
    public string[] MovingObjects { get; init; } = [];
    public string[] Obstacles { get; init; } = [];
}
public sealed record RunReviewRequest(string RunId, RunAssertion[] Assertions, bool Export = true) : RunRequest
{
    public bool ReuseRules { get; init; }
}
public sealed record RunAssertion(string Id, string Type, string Frame, string? Phase,
    double[]? Min = null, double[]? Max = null, double? MinZ = null,
    double[]? Position = null, double? Tolerance = null);

public static class RunRequests
{
    public static readonly string[] Operations = ["tcp_record", "run_review"];
    public static RunRequest Parse(string operation, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw Invalid("Run arguments must be an object.");
        RequestParser.RejectControllerAccess(args);
        return operation switch
        {
            "tcp_record" => Record(args), "run_review" => Review(args),
            _ => throw new BridgeException("unknown_operation", "Unknown run operation.", 404)
        };
    }

    private static TcpRecordRequest Record(JsonElement args)
    {
        var action = Required(args, "action");
        if (action == "start")
        {
            Check(args, ["action", "robot", "tool", "workObject", "rateHz", "maxSamples", "phase", "phaseSignal",
                "liveFrameSignal", "movingObjects", "obstacles", "command", "assertions"]);
            JsonElement? command = null;
            if (args.TryGetProperty("command", out var entry))
            {
                if (entry.ValueKind is not (JsonValueKind.String or JsonValueKind.Object) || entry.GetRawText().Length > 8192)
                    throw Invalid("command must be a string or JSON object of at most 8192 characters.");
                command = entry.Clone();
            }
            return new(action, Robot: Name(args, "robot"), Tool: Required(args, "tool"), WorkObject: Required(args, "workObject"),
                RateHz: Integer(args, "rateHz", 20, 50, 25), MaxSamples: Integer(args, "maxSamples", 1, 36000, 15000),
                Phase: Name(args, "phase"), PhaseSignal: Name(args, "phaseSignal"), LiveFrameSignal: Name(args, "liveFrameSignal"),
                Command: command) { MovingObjects = Names(args, "movingObjects"), Obstacles = Names(args, "obstacles"), Assertions = ReadAssertions(args) };
        }
        if (action == "mark")
        {
            Check(args, ["action", "runId", "phase"]);
            return new(action, Id(args, false), Phase: Required(args, "phase"));
        }
        if (action is not ("status" or "stop")) throw Invalid("tcp_record.action must be start, status, mark, or stop.");
        Check(args, ["action", "runId"]);
        return new(action, Id(args, false));
    }

    private static RunReviewRequest Review(JsonElement args)
    {
        Check(args, ["runId", "assertions", "export"]);
        bool export = true;
        if (args.TryGetProperty("export", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("export must be a boolean.");
            export = flag.GetBoolean();
        }
        return new(Id(args, true)!, ReadAssertions(args), export) { ReuseRules = !args.TryGetProperty("assertions", out _) };
    }

    private static RunAssertion[] ReadAssertions(JsonElement args)
    {
        var assertions = new List<RunAssertion>();
        if (args.TryGetProperty("assertions", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 64)
                throw Invalid("assertions must be an array of up to 64 rules.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object) throw Invalid("Each assertion must be an object.");
                var id = Required(value, "id");
                if (!ids.Add(id)) throw Invalid("Each assertion needs a unique id.");
                var type = Required(value, "type");
                var frame = Name(value, "frame") ?? "world";
                if (frame is not ("world" or "base" or "workObject")) throw Invalid("frame must be world, base, or workObject.");
                var phase = Name(value, "phase");
                switch (type)
                {
                    case "min_height":
                    case "forbidden_box":
                        var height = type == "min_height";
                        var lower = height ? "minXY" : "min";
                        var upper = height ? "maxXY" : "max";
                        Check(value, height ? ["id", "type", "frame", "phase", "minXY", "maxXY", "minZ"]
                            : ["id", "type", "frame", "phase", "min", "max"]);
                        var min = RequestParser.ReadNumbers(value, lower, height ? 2 : 3);
                        var max = RequestParser.ReadNumbers(value, upper, height ? 2 : 3);
                        if (min.Where((v, i) => v > max[i]).Any()) throw Invalid("Region minimums must not exceed their maximums.");
                        assertions.Add(new(id, type, frame, phase, min, max, height ? Number(value, "minZ") : null));
                        break;
                    case "waypoint":
                        Check(value, ["id", "type", "frame", "phase", "position", "tolerance"]);
                        var tolerance = Number(value, "tolerance");
                        if (tolerance < 0) throw Invalid("Waypoint tolerance must be nonnegative millimetres.");
                        assertions.Add(new(id, type, frame, phase, Position: RequestParser.ReadNumbers(value, "position", 3), Tolerance: tolerance));
                        break;
                    default: throw Invalid("Assertion type must be min_height, forbidden_box, or waypoint.");
                }
            }
        }
        return assertions.ToArray();
    }

    internal static string ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var parsed) || id.Length != 32)
            throw Invalid("runId must be the 32 hexadecimal characters returned by tcp_record start.");
        return parsed.ToString("N");
    }
    private static string? Id(JsonElement args, bool required)
    {
        var id = required ? Required(args, "runId") : Name(args, "runId");
        return id is null ? null : ValidateId(id);
    }
    private static string[] Names(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var array)) return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 8)
            throw Invalid(key + " must contain up to eight distinct station selectors.");
        var items = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string name ||
                string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl) || items.Contains(name))
                throw Invalid(key + " must contain distinct nonempty station selector names.");
            items.Add(name);
        }
        return items.ToArray();
    }
    private static int Integer(JsonElement args, string key, int min, int max, int fallback)
    {
        if (!args.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n < min || n > max)
            throw Invalid($"{key} must be an integer in {min}..{max}.");
        return n;
    }
    private static double Number(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var n) || !double.IsFinite(n)) throw Invalid(key + " must be a finite number.");
        return n;
    }
    private static string? Name(JsonElement args, string key) => RequestParser.ReadName(args, key);
    private static string Required(JsonElement args, string key) => Name(args, key) ?? throw Invalid(key + " is required.");
    private static void Check(JsonElement args, string[] keys) => RequestParser.CheckProperties(args, keys, "arguments");
    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
