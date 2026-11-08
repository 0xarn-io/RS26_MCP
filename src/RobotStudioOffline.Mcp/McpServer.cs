using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Mcp;

public sealed class McpServer(BridgeClient bridge)
{
    private const int MaximumLineCharacters = 1024 * 1024;
    private static readonly string ServerVersion = typeof(McpServer).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private bool initializeReplied;
    private bool initialized;

    public async Task RunAsync(TextReader input, TextWriter output)
    {
        while (true)
        {
            string? line;
            try { line = await ReadBoundedLineAsync(input); }
            catch (InvalidDataException)
            {
                await WriteAsync(output, Error(null, -32700, "MCP message exceeds 1 MiB characters."));
                return;
            }
            if (line is null) return;
            var reply = await ProcessAsync(line);
            if (reply is not null) await WriteAsync(output, reply);
        }
    }

    public async Task<JsonObject?> ProcessAsync(string line)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return Error(null, -32700, "Invalid JSON."); }
        using (document)
        {
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
                return Error(null, -32600, "Expected a JSON-RPC 2.0 object; batches are not supported.");
            var hasId = request.TryGetProperty("id", out var rawId);
            if (hasId && rawId.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
                return Error(null, -32600, "Request id must be a string or number.");
            var id = hasId ? JsonNode.Parse(rawId.GetRawText()) : null;
            if (!request.TryGetProperty("method", out var rawMethod) || rawMethod.ValueKind != JsonValueKind.String)
                return Error(id, -32600, "Request method must be a string.");
            var method = rawMethod.GetString()!;
            if (!hasId)
            {
                if (method == "notifications/initialized" && initializeReplied) initialized = true;
                // All notifications have no response. No request is processed as a notification.
                return null;
            }
            var parameters = request.TryGetProperty("params", out var supplied) ? supplied : JsonSerializer.SerializeToElement(new { });
            if (parameters.ValueKind != JsonValueKind.Object)
                return Error(id, -32602, "params must be an object.");
            try
            {
                if (method == "initialize")
                {
                    if (initializeReplied) return Error(id, -32600, "Already initialized.");
                    if (!parameters.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
                        !parameters.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
                        !parameters.TryGetProperty("clientInfo", out var info) || info.ValueKind != JsonValueKind.Object ||
                        !info.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                        !info.TryGetProperty("version", out var clientVersion) || clientVersion.ValueKind != JsonValueKind.String)
                        return Error(id, -32602, "initialize requires protocolVersion, capabilities and clientInfo with name/version.");
                    initializeReplied = true;
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersions.Contains(protocol.GetString()) ? protocol.GetString() : ProtocolVersions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "robotstudio-offline", ["version"] = ServerVersion },
                        ["instructions"] = "Use the open RobotStudio station and its mapped virtual controller for development. To compare modules with the owner-configured real controller, call rapid_diff with includeReal set to true. To load clone-verified module files onto that stopped real controller, call rapid_sync_real. Both operations require the owner's explicit approval in RobotStudio for each real-controller request. Reach and collision results apply to the station model. Get names from station before requesting poses. Positions use millimetres, joints use degrees, and quaternions use [w,x,y,z]. Preview and compare before editing or synchronizing selected data."
                    });
                }
                if (method == "ping") return Result(id, new JsonObject());
                if (!initialized) return Error(id, -32002, "Send initialize and notifications/initialized first.");
                if (method == "tools/list")
                {
                    if (parameters.TryGetProperty("cursor", out _)) return Error(id, -32602, "This tool list is not paginated; omit cursor.");
                    return Result(id, new JsonObject { ["tools"] = ToolCatalog.Create() });
                }
                if (method == "tools/call")
                {
                    if (!parameters.TryGetProperty("name", out var tool) || tool.ValueKind != JsonValueKind.String || !ToolCatalog.Names.Contains(tool.GetString()))
                        return Error(id, -32602, "Unknown tool.");
                    var arguments = parameters.TryGetProperty("arguments", out var provided) ? provided : JsonSerializer.SerializeToElement(new { });
                    try
                    {
                        var response = await bridge.CallAsync(tool.GetString()!, arguments);
                        if (tool.GetString() is ("screenshot" or "run_view") && !response.IsError)
                            return ScreenshotResult(id, response.Json);
                        return ToolResult(id, response.Json, response.IsError);
                    }
                    catch (BridgeException exception)
                    {
                        return ToolError(id, exception.Code, exception.Message);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
                    {
                        var code = exception is OperationCanceledException ? "bridge_timeout" : "bridge_unavailable";
                        return ToolError(id, code, exception is OperationCanceledException
                            ? "The localhost bridge timed out. A timed-out write may have completed; inspect station state before retrying."
                            : "The localhost bridge is unavailable or returned an invalid response. Check that the owner has loaded the add-in and that its token is readable.");
                    }
                }
                return Error(id, -32601, "Method not found.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                return Error(id, -32602, "Invalid request parameters.");
            }
        }
    }

    private static JsonObject ToolError(JsonNode? id, string code, string message) =>
        ToolResult(id, JsonSerializer.Serialize(new { ok = false, error = new { code, message } }), true);
    private static JsonObject ScreenshotResult(JsonNode? id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.GetProperty("data");
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("mimeType", out var mimeType) || mimeType.ValueKind != JsonValueKind.String || mimeType.GetString() != "image/png" ||
            !root.TryGetProperty("data", out var imageData) || imageData.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("width", out var width) || width.ValueKind != JsonValueKind.Number || !width.TryGetInt32(out var widthValue) || widthValue is < 1 or > 4096 ||
            !root.TryGetProperty("height", out var height) || height.ValueKind != JsonValueKind.Number || !height.TryGetInt32(out var heightValue) || heightValue is < 1 or > 4096)
            throw new BridgeException("bridge_invalid_response", "The screenshot response must contain PNG base64 data and bounded positive image dimensions.");
        byte[] png;
        try { png = Convert.FromBase64String(imageData.GetString()!); }
        catch (FormatException)
        {
            throw new BridgeException("bridge_invalid_response", "The screenshot response contains invalid base64 data.");
        }
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new BridgeException("bridge_invalid_response", "The screenshot response does not contain a PNG signature.");
        var metadata = new JsonObject { ["width"] = widthValue, ["height"] = heightValue, ["scope"] = "station_only" };
        foreach (var key in new[] { "file", "runId", "view" })
            if (root.TryGetProperty(key, out var entry) && entry.ValueKind == JsonValueKind.String) metadata[key] = entry.GetString();
        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
                new JsonObject { ["type"] = "text", ["text"] = metadata.ToJsonString() }),
            ["isError"] = false
        });
    }

    private static JsonObject ToolResult(JsonNode? id, string content, bool failed) => Result(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content }), ["isError"] = failed
    });
    private static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
    private static async Task WriteAsync(TextWriter output, JsonObject response)
    {
        await output.WriteLineAsync(response.ToJsonString());
        await output.FlushAsync();
    }
    private static async Task<string?> ReadBoundedLineAsync(TextReader input)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (await input.ReadAsync(character, 0, 1) != 0)
        {
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= MaximumLineCharacters) throw new InvalidDataException();
            line.Append(character[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }
}
