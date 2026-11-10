using System.Text.Json;

namespace RobotStudioOffline.Core;

public abstract record EngineeringRequest;

public record ConfigurationRequest(string? Robot, string? Domain, string? Type, string? Instance, int Offset, int Limit,
    string Action, string? Attribute, JsonElement? Value, JsonElement? Expected) : EngineeringRequest;

public sealed record IoProjectRequest(string? Robot, string? Type, string? Instance, int Offset, int Limit,
    string Action, string? Attribute, JsonElement? Value, JsonElement? Expected)
    : ConfigurationRequest(Robot, "EIO", Type, Instance, Offset, Limit, Action, Attribute, Value, Expected);

public sealed record SimulationSetupRequest(string Action, string Section = "overview", string? Configuration = null,
    int Offset = 0, int Limit = 50, bool? FloorEnabled = null, double? FloorHeightMm = null, double? GravityMps2 = null,
    bool? DataRecordingEnabled = null, string? VirtualTimeMode = null, string? InitialState = null, string? Part = null,
    string? MotionControl = null, bool? UseOriginalGeometry = null, double? DensityKgPerM3 = null,
    double? SurfaceRoughness = null, double? Restitution = null, double? Damping = null,
    bool? SurfaceVelocityEnabled = null, double? SurfaceSpeedMps = null, string? Participant = null,
    bool? Included = null, Vector3D? SurfaceDirection = null, bool? DetectableBySensors = null) : EngineeringRequest;

public sealed record SafeMoveZoneEdit(string OutputName, int DriveModuleId, int ZoneId, double? BottomM, double? TopM,
    double[][]? PointsM);
public sealed record SafeMoveRequest(string File, int Offset, int Limit, SafeMoveZoneEdit? Edit = null) : EngineeringRequest;

internal static class EngineeringRequests
{
    internal static readonly string[] Operations = ["configuration", "io_project", "simulation_setup", "safemove"];

    internal static EngineeringRequest Parse(string operation, JsonElement args) => operation switch
    {
        "configuration" => Parameters(args, false),
        "io_project" => Parameters(args, true),
        "simulation_setup" => SimulationSetup(args),
        "safemove" => SafeMove(args),
        _ => throw Invalid("Unknown engineering operation.")
    };

    private static ConfigurationRequest Parameters(JsonElement args, bool ioProject)
    {
        var action = Name(args, "action") ?? "read";
        string[] selectors = ioProject ? ["robot", "type", "instance"] : ["robot", "domain", "type", "instance"];
        string[] options = action switch
        {
            "read" => ["offset", "limit"],
            "set" => ["attribute", "value", "expected"],
            _ => throw Invalid("action must be read or set.")
        };
        Properties(args, ["action", .. selectors, .. options]);
        var robot = Name(args, "robot");
        var domain = ioProject ? "EIO" : Name(args, "domain");
        var type = Name(args, "type");
        var instance = Name(args, "instance");
        if (type != null && domain == null) throw Invalid("type requires domain.");
        if (instance != null && type == null) throw Invalid("instance requires type.");
        if (action == "set" && instance == null) throw Invalid("set requires an exact instance and its parent selectors.");
        var attribute = action == "set" ? Required(args, "attribute") : null;
        JsonElement? value = action == "set" ? Scalar(args, "value") : null;
        JsonElement? expected = action == "set" ? Scalar(args, "expected") : null;
        if (ioProject) return new IoProjectRequest(robot, type, instance, Offset(args), Limit(args), action, attribute, value, expected);
        return new ConfigurationRequest(robot, domain, type, instance, Offset(args), Limit(args), action, attribute, value, expected);
    }

    private static JsonElement Scalar(JsonElement args, string key)
    {
        if (args.TryGetProperty(key, out var v))
        {
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.Clone();
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)) return v.Clone();
            if (v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 4096 && !v.GetString()!.Any(char.IsControl))
                return v.Clone();
        }
        throw Invalid(key + " must be a boolean, finite number or string up to 4096 characters without control characters.");
    }

    private static SimulationSetupRequest SimulationSetup(JsonElement args)
    {
        var action = Name(args, "action") ?? "read";
        switch (action)
        {
            case "read":
                var section = Name(args, "section") ?? "overview";
                if (section is not ("overview" or "configurations" or "participants" or "eligibleParticipants" or "savedStates" or "parts"))
                    throw Invalid("Unknown simulation_setup section.");
                string[] listFields = section == "overview" ? [] : ["offset", "limit"];
                string[] selection = section is "participants" or "eligibleParticipants" ? ["configuration"] : [];
                Properties(args, ["action", "section", .. listFields, .. selection]);
                return new(action, section, Name(args, "configuration"), Offset(args), Limit(args));
            case "set_world":
                Changes(args, ["floorEnabled", "floorHeightMm", "gravityMps2"]);
                return new(action, FloorEnabled: Bool(args, "floorEnabled"), FloorHeightMm: Number(args, "floorHeightMm"),
                    GravityMps2: Number(args, "gravityMps2"));
            case "set_configuration":
                Changes(args, ["dataRecordingEnabled", "virtualTimeMode", "initialState"], "configuration");
                var mode = Name(args, "virtualTimeMode");
                if (mode is not (null or "VirtualFreeRun" or "VirtualTimeSlice")) throw Invalid("Unknown virtualTimeMode.");
                return new(action, Configuration: Name(args, "configuration"), DataRecordingEnabled: Bool(args, "dataRecordingEnabled"),
                    VirtualTimeMode: mode, InitialState: Name(args, "initialState"));
            case "set_part":
                Changes(args, ["motionControl", "useOriginalGeometry", "densityKgPerM3", "surfaceRoughness",
                    "restitution", "damping", "surfaceVelocityEnabled", "surfaceSpeedMps", "surfaceDirection", "detectableBySensors"], "part");
                var motion = Name(args, "motionControl");
                if (motion is not (null or "None" or "Static" or "Kinematic" or "Dynamic")) throw Invalid("Unknown motionControl.");
                return new(action, Part: Required(args, "part"), MotionControl: motion, UseOriginalGeometry: Bool(args, "useOriginalGeometry"),
                    DensityKgPerM3: Number(args, "densityKgPerM3", double.Epsilon), SurfaceRoughness: Number(args, "surfaceRoughness", 0),
                    Restitution: Number(args, "restitution", 0, 1), Damping: Number(args, "damping", 0),
                    SurfaceVelocityEnabled: Bool(args, "surfaceVelocityEnabled"), SurfaceSpeedMps: Number(args, "surfaceSpeedMps"),
                    SurfaceDirection: args.TryGetProperty("surfaceDirection", out _) ? ReadSurfaceDirection(args) : null,
                    DetectableBySensors: Bool(args, "detectableBySensors"));
            case "set_participant":
                Properties(args, ["action", "configuration", "participant", "included"]);
                return new(action, Configuration: Name(args, "configuration"), Participant: Required(args, "participant"),
                    Included: Bool(args, "included") ?? throw Invalid("included must be true or false."));
            default:
                throw Invalid("Unknown simulation_setup action.");
        }
    }

    private static Vector3D ReadSurfaceDirection(JsonElement args)
    {
        var v = RequestParser.ReadNumbers(args, "surfaceDirection", 3);
        var length = Math.Sqrt(v.Sum(x => x * x));
        if (Math.Abs(length - 1) > 1e-6) throw Invalid("surfaceDirection must be a unit XYZ vector.");
        return new(v[0] / length, v[1] / length, v[2] / length);
    }

    private static SafeMoveRequest SafeMove(JsonElement args)
    {
        var action = Name(args, "action") ?? "inspect";
        if (!args.TryGetProperty("file", out var source) || source.ValueKind != JsonValueKind.String)
            throw Invalid("file must be a fully qualified local XML path.");
        var file = LocalInputFile.CanonicalPath(source.GetString()!);
        if (!Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase)) throw Invalid("Select a SafeMove geometry .xml file.");
        if (action == "inspect")
        {
            Properties(args, ["action", "file", "offset", "limit"]);
            return new(file, Offset(args), Limit(args));
        }
        if (action != "edit_zone_copy") throw Invalid("safemove.action must be inspect or edit_zone_copy.");
        Changes(args, ["bottomM", "topM", "pointsM"], "file", "outputName", "driveModuleId", "zoneId");
        var output = Required(args, "outputName");
        if (output.Contains('/') || output.Contains('\\') || !output.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw Invalid("outputName must be a sibling .xml filename.");
        _ = LocalInputFile.CanonicalPath(file[..(file.LastIndexOf('\\') + 1)] + output);
        var module = SceneRequests.Integer(args, "driveModuleId", 1, 4, 0);
        var zone = SceneRequests.Integer(args, "zoneId", 1, 16, 0);
        if (module == 0 || zone == 0) throw Invalid("driveModuleId and zoneId are required.");
        var bottom = Number(args, "bottomM", -30, 30);
        var top = Number(args, "topM", -30, 30);
        if (bottom >= top) throw Invalid("bottomM must be below topM.");
        double[][]? points = null;
        if (args.TryGetProperty("pointsM", out var vertices))
        {
            if (vertices.ValueKind != JsonValueKind.Array || vertices.GetArrayLength() is < 4 or > 24)
                throw Invalid("pointsM must contain 4 to 24 XY vertices.");
            points = vertices.EnumerateArray().Select(vertex =>
            {
                if (vertex.ValueKind != JsonValueKind.Array || vertex.GetArrayLength() != 2) throw Invalid("Each vertex needs two coordinates.");
                return vertex.EnumerateArray().Select(v => Finite(v, "pointsM", -1000, 1000)).ToArray();
            }).ToArray();
        }
        return new(file, 0, 50, new(output, module, zone, bottom, top, points));
    }

    private static void Changes(JsonElement args, string[] editable, params string[] selectors)
    {
        Properties(args, ["action", .. selectors, .. editable]);
        if (!editable.Any(key => args.TryGetProperty(key, out _))) throw Invalid("Provide at least one field to change.");
    }

    private static double? Number(JsonElement args, string key, double min = double.MinValue, double max = double.MaxValue)
        => args.TryGetProperty(key, out var v) ? Finite(v, key, min, max) : null;

    private static double Finite(JsonElement value, string key, double min, double max)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n >= min && n <= max
            ? n : throw Invalid(key + " is outside the supported numeric range.");

    private static bool? Bool(JsonElement args, string key)
        => !args.TryGetProperty(key, out var v) ? null : v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : throw Invalid(key + " must be a boolean.");

    private static int Offset(JsonElement args) => SceneRequests.Integer(args, "offset", 0, int.MaxValue, 0);
    private static int Limit(JsonElement args) => SceneRequests.Integer(args, "limit", 1, 200, 50);
    private static string? Name(JsonElement args, string key) => RequestParser.ReadName(args, key);
    private static string Required(JsonElement args, string key) => Name(args, key) ?? throw Invalid(key + " is required.");
    private static void Properties(JsonElement args, string[] allowed) => RequestParser.CheckProperties(args, allowed, "arguments");
    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
