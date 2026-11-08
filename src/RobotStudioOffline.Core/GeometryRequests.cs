using System.Text.Json;

namespace RobotStudioOffline.Core;

public abstract record GeometryRequest(string Name, RigidFrame Pose)
{
    public string PlacementFrame { get; init; } = "world";
    public string? Robot { get; init; }
}
public sealed record GeometryCreateRequest(string Name, string Shape, double[]? Size, double? Radius,
    double? Height, RigidFrame Pose) : GeometryRequest(Name, Pose);
public sealed record GeometryImportRequest(string Name, string File, RigidFrame Pose) : GeometryRequest(Name, Pose);

public static class GeometryRequests
{
    public static readonly string[] Operations = ["geometry_create", "geometry_import"];

    public static GeometryRequest Parse(string operation, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Invalid("Geometry arguments must be a JSON object.");
        RequestParser.RejectControllerAccess(arguments);
        var frame = RequestParser.ReadName(arguments, "frame") ?? "world";
        var robot = RequestParser.ReadName(arguments, "robot");
        if (frame is not ("world" or "base") || (robot is not null && frame != "base"))
            throw Invalid("Use frame world or base. A robot selector applies only to base placement.");
        GeometryRequest geometry = operation switch
        {
            "geometry_create" => Create(arguments),
            "geometry_import" => Import(arguments),
            _ => throw new BridgeException("unknown_operation", "Unknown geometry operation.", 404)
        };
        return geometry with { PlacementFrame = frame, Robot = robot };
    }

    private static GeometryCreateRequest Create(JsonElement arguments)
    {
        var shape = RequestParser.ReadName(arguments, "shape") ?? throw Invalid("geometry_create.shape is required.");
        string[] allowed = shape switch
        {
            "box" => ["name", "shape", "size", "position", "quaternion"],
            "cylinder" => ["name", "shape", "radius", "height", "position", "quaternion"],
            "sphere" => ["name", "shape", "radius", "position", "quaternion"],
            _ => throw Invalid("geometry_create.shape must be box, cylinder, or sphere.")
        };
        RequestParser.CheckProperties(arguments, [..allowed, "frame", "robot"], "arguments");
        var size = shape == "box" ? RequestParser.ReadNumbers(arguments, "size", 3) : null;
        if (size is not null && size.Any(length => length <= 0))
            throw Invalid("All three box dimensions must be positive millimetres.");
        double? radius = shape is "sphere" or "cylinder" ? PositiveNumber(arguments, "radius") : null;
        double? height = shape == "cylinder" ? PositiveNumber(arguments, "height") : null;
        return new(RequiredName(arguments), shape, size, radius, height, ReadPose(arguments));
    }

    private static GeometryImportRequest Import(JsonElement arguments)
    {
        RequestParser.CheckProperties(arguments, ["name", "file", "position", "quaternion", "frame", "robot"], "arguments");
        if (!arguments.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String)
            throw Invalid("geometry_import.file must be a fully qualified local Windows CAD file path.");
        return new(RequiredName(arguments), ReadLocalPath(file.GetString()!), ReadPose(arguments));
    }

    private static string RequiredName(JsonElement arguments) => RequestParser.ReadName(arguments, "name")
        ?? throw Invalid("A geometry name is required.");

    private static RigidFrame ReadPose(JsonElement arguments)
    {
        var position = new Vector3D();
        if (arguments.TryGetProperty("position", out _))
        {
            var xyz = RequestParser.ReadNumbers(arguments, "position", 3);
            position = new(xyz[0], xyz[1], xyz[2]);
        }
        var orientation = arguments.TryGetProperty("quaternion", out _)
            ? RequestParser.ReadUnitQuaternion(arguments)
            : QuaternionD.Identity;
        return new(position, orientation);
    }

    private static double PositiveNumber(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number) || !double.IsFinite(number) || number <= 0)
            throw Invalid($"{property} must be a positive finite number in millimetres.");
        return number;
    }

    // Syntax only. The add-in checks the actual drive, file and every directory before import.
    private static string ReadLocalPath(string path)
    {
        var normalized = LocalInputFile.CanonicalPath(path);
        var extension = Path.GetExtension(normalized).ToLowerInvariant();
        if (extension is not (".sat" or ".step" or ".stp" or ".stl"))
            throw Invalid("CAD imports support .sat, .step, .stp, and .stl files.");
        return normalized;
    }

    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
