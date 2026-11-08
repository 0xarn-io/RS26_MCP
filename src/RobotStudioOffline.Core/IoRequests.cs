using System.Text.Json;

namespace RobotStudioOffline.Core;

public abstract record IoRequest;
public sealed record IoReadRequest(string? Robot, string[]? Names, int Offset, int Limit) : IoRequest;
public sealed record IoWriteRequest(string? Robot, string Name, double Value, bool SimulateInput) : IoRequest;
public sealed record IoPulseRequest(string? Robot, string Name, int DurationMs, bool SimulateInput) : IoRequest;

/// <summary>Bounded I/O requests with no controller/address selection or implicit forcing.</summary>
public static class IoRequests
{
    public static readonly string[] Operations = ["io_read", "io_write", "io_pulse"];

    public static IoRequest Parse(string operation, JsonElement arguments)
    {
        RequestParser.RejectControllerAccess(arguments);
        return operation switch
        {
            "io_read" => Read(arguments),
            "io_write" => Write(arguments),
            "io_pulse" => Pulse(arguments),
            _ => throw new BridgeException("unknown_operation", "Unknown I/O operation.", 404)
        };
    }

    private static IoReadRequest Read(JsonElement arguments)
    {
        Check(arguments, ["robot", "names", "offset", "limit"]);
        var robot = RequestParser.ReadName(arguments, "robot");
        if (arguments.TryGetProperty("names", out var names))
        {
            if (arguments.TryGetProperty("offset", out _) || arguments.TryGetProperty("limit", out _))
                throw Invalid("io_read.names cannot be combined with offset or limit.");
            if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() is < 1 or > 100)
                throw Invalid("io_read.names must contain 1..100 signal names.");
            var selected = new List<string>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String)
                    throw Invalid("Every signal name must be a nonempty string.");
                var text = name.GetString()!;
                if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl))
                    throw Invalid("Signal names must be nonempty, at most 256 characters, and contain no control characters.");
                if (!unique.Add(text)) throw Invalid("Duplicate signal name: " + text);
                selected.Add(text);
            }
            return new(robot, selected.ToArray(), 0, 100);
        }
        return new(robot, null, Integer(arguments, "offset", 0, int.MaxValue, 0),
            Integer(arguments, "limit", 1, 500, 100));
    }

    private static IoWriteRequest Write(JsonElement arguments)
    {
        Check(arguments, ["robot", "name", "value", "simulateInput"]);
        if (!arguments.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Invalid("io_write.value must be a finite JSON number.");
        return new(RequestParser.ReadName(arguments, "robot"), RequiredName(arguments), number, Boolean(arguments, "simulateInput"));
    }

    private static IoPulseRequest Pulse(JsonElement arguments)
    {
        Check(arguments, ["robot", "name", "durationMs", "simulateInput"]);
        return new(RequestParser.ReadName(arguments, "robot"), RequiredName(arguments),
            Integer(arguments, "durationMs", 1, 5000, 100), Boolean(arguments, "simulateInput"));
    }

    private static string RequiredName(JsonElement arguments) =>
        RequestParser.ReadName(arguments, "name") ?? throw Invalid("An exact signal name is required.");

    private static int Integer(JsonElement arguments, string property, int minimum, int maximum, int defaultValue)
    {
        if (!arguments.TryGetProperty(property, out var value)) return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw Invalid($"{property} must be an integer in {minimum}..{maximum}.");
        return number;
    }

    private static bool Boolean(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid(property + " must be a boolean.");
        return value.GetBoolean();
    }

    private static void Check(JsonElement arguments, string[] allowed) => RequestParser.CheckProperties(arguments, allowed, "arguments");
    private static BridgeException Invalid(string message) => new("invalid_request", message);
}

/// <summary>Checks numeric values against the selected signal's SDK type and exact bounds.</summary>
public static class IoValueValidation
{
    public static void Validate(string kind, double value, double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new BridgeException("invalid_signal_metadata", "The signal must report finite, ordered minimum and maximum values.", 409);
        if (!double.IsFinite(value)) throw Invalid("Signal values must be finite.");
        double represented;
        switch (kind)
        {
            case "digital":
                if (value is not (0.0 or 1.0)) throw Invalid("Digital signals accept only 0 or 1.");
                represented = value;
                break;
            case "analog":
                represented = ToAnalogValue(value);
                break;
            case "group":
                represented = ToGroupValue(value);
                break;
            default:
                throw new BridgeException("unsupported_signal_type", "Expected a digital, analog or group signal.", 409);
        }
        if (value < minimum || value > maximum || represented < minimum || represented > maximum)
            throw Invalid($"Signal value must be within its configured range {minimum}..{maximum}, including after SDK numeric conversion.");
    }

    /// <summary>Groups remain exact unsigned 32-bit integers and never pass through a float.</summary>
    public static uint ToGroupValue(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > uint.MaxValue || value != Math.Truncate(value))
            throw Invalid("Group signals accept integral values in 0..4294967295.");
        return checked((uint)value);
    }

    /// <summary>ABB analog values use single precision; reject finite doubles that overflow that type.</summary>
    public static float ToAnalogValue(double value)
    {
        if (!double.IsFinite(value)) throw Invalid("Analog signal values must be finite.");
        var converted = (float)value;
        if (!float.IsFinite(converted)) throw Invalid("Analog signal value exceeds the finite single-precision range.");
        return converted;
    }

    private static BridgeException Invalid(string message) => new("invalid_io_value", message);
}
