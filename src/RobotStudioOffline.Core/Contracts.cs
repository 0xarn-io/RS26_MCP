using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobotStudioOffline.Core;

public sealed class BridgeException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record BridgeRequest
{
    [JsonIgnore]
    public string Operation { get; init; } = string.Empty;
    public string? Robot { get; init; }
    public string? Tool { get; init; }
    public string? WorkObject { get; init; }
    public string? Target { get; init; }
    [JsonPropertyName("robtarget")]
    public RobTarget? RobTarget { get; init; }
    [JsonPropertyName("jointtarget")]
    public JointTarget? JointTarget { get; init; }
    public string? Action { get; init; }
    public string? ApplyTo { get; init; }
}

/// <summary>RAPID units: millimetres, scalar-first unit quaternion, cf1/cf4/cf6/cfx.</summary>
public sealed record RobTarget(double[] Position, double[] Quaternion, int[] Configuration, double[]? ExternalAxes);

/// <summary>Robot axes in degrees. External-axis units depend on the mechanical unit.</summary>
public sealed record JointTarget(double[] RobotAxes, double[]? ExternalAxes);

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
}

public sealed record BridgeError(string Code, string Message);

public sealed record BridgeResponse(bool Ok, object? Data, BridgeError? Error)
{
    public static BridgeResponse Success(object data) => new(true, data, null);
    public static BridgeResponse Failure(BridgeException error) => new(false, null, new(error.Code, error.Message));
}
