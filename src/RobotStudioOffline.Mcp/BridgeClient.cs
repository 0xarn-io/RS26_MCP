using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Mcp;

public sealed record BridgeResult(string Json, bool IsError);

public sealed class BridgeClient : IDisposable
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly string tokenFile;

    public BridgeClient(McpOptions options)
    {
        if (options.Port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options));
        tokenFile = McpOptions.ValidateTokenFile(options.TokenFile);
        client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{options.Port}/"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan // The request deadline below covers both headers and body.
        };
    }

    public async Task<BridgeResult> CallAsync(string operation, JsonElement arguments)
    {
        // Validate even before opening the local connection. In particular no controller
        // selection, address or arbitrary route can ever become an HTTP destination.
        RequestParser.Parse(operation, arguments);
        var info = new FileInfo(tokenFile);
        if (!info.Exists)
            throw new BridgeException("bridge_not_loaded", "The bridge token is missing. Ask the owner to install and load the RobotStudio add-in.");
        if (info.Length > 4096)
            throw new BridgeException("invalid_token", "The bridge token file is too large.");
        var token = (await File.ReadAllTextAsync(tokenFile)).Trim();
        if (token.Length is < 32 or > 512 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '+' and not '/' and not '='))
            throw new BridgeException("invalid_token", "The bridge token file is invalid.");

        using var request = new HttpRequestMessage(HttpMethod.Post, operation)
        {
            Content = new StringContent(arguments.GetRawText(), Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var deadline = operation == "geometry_import" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(120);
        using var timeout = new CancellationTokenSource(deadline);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        // A redirect is an error, never a reason to follow an external controller URL.
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new BridgeException("redirect_forbidden", "The localhost bridge returned a redirect, which is forbidden.");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new BridgeException("bridge_response_too_large", "The bridge response exceeds 16 MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, timeout.Token);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new BridgeException("bridge_response_too_large", "The bridge response exceeds 16 MiB.");
            buffer.Write(chunk, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("ok", out var ok) ||
            ok.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new BridgeException("bridge_invalid_response", "The bridge did not return the expected JSON envelope with a boolean ok field.");
        if (ok.ValueKind == JsonValueKind.True && !document.RootElement.TryGetProperty("data", out _))
            throw new BridgeException("bridge_invalid_response", "The bridge success envelope is missing data.");
        var failed = !response.IsSuccessStatusCode || ok.ValueKind == JsonValueKind.False;
        return new BridgeResult(document.RootElement.GetRawText(), failed);
    }

    public void Dispose() => client.Dispose();
}
