using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RobotStudioOffline.Core;

/// <summary>A deliberately narrow HTTP/1.1 transport: one bounded JSON POST per connection.</summary>
public sealed class LoopbackServer : IDisposable
{
    public const int DefaultPort = 18426;
    public const int MaxBodyBytes = 65536;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim clients = new(8, 8);
    private readonly Func<string, JsonElement, CancellationToken, Task<object>> execute;
    private readonly byte[] authorization;
    private readonly object lifecycle = new();
    private readonly HashSet<Task> activeRequests = [];
    private Task? accepting;
    private Task? stopped;

    public LoopbackServer(string token, Func<string, JsonElement, CancellationToken, Task<object>> execute, int port = DefaultPort)
    {
        if (token.Length < 32 || token.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Token must contain at least 32 ASCII letters/digits.", nameof(token));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        authorization = Encoding.ASCII.GetBytes("Bearer " + token);
        this.execute = execute;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)listener.LocalEndpoint;

    public void Start()
    {
        lock (lifecycle)
        {
            if (accepting is not null || stopped is not null) throw new InvalidOperationException("Server already started or stopped.");
            listener.Start(8);
            accepting = AcceptAsync();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
                if (!clients.Wait(0)) { client.Dispose(); continue; }
                var serving = ServeAsync(client);
                lock (activeRequests) activeRequests.Add(serving);
                _ = ForgetCompletedAsync(serving);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (SocketException) when (stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ForgetCompletedAsync(Task serving)
    {
        try { await serving.ConfigureAwait(false); }
        catch (Exception) { /* Observe any final socket-disposal failure as well as the normal handled response path. */ }
        finally { lock (activeRequests) activeRequests.Remove(serving); }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !remote.Address.Equals(IPAddress.Loopback)) return;
                using var receiveDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                receiveDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                int status;
                object response;
                try
                {
                    var request = await ReadAsync(stream, receiveDeadline.Token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 24 });
                    _ = RequestParser.Parse(request.Operation, document.RootElement);
                    using var queueDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    queueDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    var result = await execute(request.Operation, document.RootElement.Clone(), queueDeadline.Token).ConfigureAwait(false);
                    status = 200;
                    response = new { ok = true, data = result };
                }
                catch (BridgeException ex)
                {
                    status = ex.StatusCode;
                    response = Error(ex.Code, ex.Message);
                }
                catch (JsonException)
                {
                    status = 400;
                    response = Error("invalid_json", "Expected a JSON object (maximum depth 24).");
                }
                catch (OperationCanceledException)
                {
                    status = 408;
                    response = Error("request_timeout", "Request expired before execution; no queued operation will run.");
                }
                catch (Exception ex)
                {
                    status = 500;
                    response = Error("sdk_error", ex.Message);
                }
                var body = JsonSerializer.SerializeToUtf8Bytes(response, JsonDefaults.Options);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n");
                // Stop cancels unstarted work, but an already-running SDK operation completes.
                // Preserve its known result instead of turning a successful write into an unknown outcome.
                using var sendDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await stream.WriteAsync(header, sendDeadline.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, sendDeadline.Token).ConfigureAwait(false);
                // Send FIN before disposing. On Windows, closing a socket with unread request
                // bytes otherwise sends RST and can discard the JSON error response (e.g. 401).
                client.Client.Shutdown(SocketShutdown.Send);
                using var drainDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                drainDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                var drain = new byte[4096];
                var drained = 0;
                while (drained <= MaxBodyBytes)
                {
                    var received = await stream.ReadAsync(drain, drainDeadline.Token).ConfigureAwait(false);
                    if (received == 0) break;
                    drained += received;
                }
            }
            catch (Exception) { /* Client disconnect / shutdown: no unobserved task fault in RobotStudio. */ }
            finally { clients.Release(); }
        }
    }

    private async Task<(string Operation, byte[] Body)> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(1024);
        var one = new byte[1];
        while (true)
        {
            if (header.Count >= 8192) throw new BridgeException("headers_too_large", "HTTP headers exceed 8192 bytes.", 431);
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                throw new BridgeException("invalid_http", "Incomplete HTTP headers.");
            var value = one[0];
            if (value > 126 || (value < 32 && value is not (9 or 10 or 13)))
                throw new BridgeException("invalid_http", "HTTP headers must be ASCII.");
            header.Add(value);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1") throw new BridgeException("invalid_http", "Expected HTTP/1.1.");
        if (first[0] != "POST") throw new BridgeException("method_not_allowed", "Only JSON POST is supported.", 405);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(x => x.Length != 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new BridgeException("invalid_http", "Invalid header name.");
            if (!headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                throw new BridgeException("invalid_http", "Duplicate HTTP headers are refused.");
        }
        if (!headers.TryGetValue("Host", out var host) || host != $"127.0.0.1:{LocalEndpoint.Port}")
            throw new BridgeException("invalid_host", "Only the literal 127.0.0.1 loopback host is accepted.", 403);
        if (headers.ContainsKey("Origin"))
            throw new BridgeException("browser_origin_forbidden", "Browser-origin requests are refused.", 403);
        if (!headers.TryGetValue("Authorization", out var auth) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(auth), authorization))
            throw new BridgeException("unauthorized", "Valid local bearer token required.", 401);
        if (headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Content-Encoding") || headers.ContainsKey("Expect"))
            throw new BridgeException("unsupported_encoding", "Use an uncompressed Content-Length JSON body without Expect.");
        if (!headers.TryGetValue("Content-Type", out var contentType) ||
            !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("unsupported_media_type", "Content-Type must be application/json.", 415);
        if (!headers.TryGetValue("Content-Length", out var size) ||
            !int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 2)
            throw new BridgeException("invalid_length", "Content-Length is required and must be at least 2.");
        if (length > MaxBodyBytes) throw new BridgeException("body_too_large", "JSON body exceeds 65536 bytes.", 413);
        var operation = first[1].StartsWith('/') ? first[1][1..] : "";
        if (!BridgeOperations.All.Contains(operation, StringComparer.Ordinal))
            throw new BridgeException("not_found", "Unknown bridge endpoint.", 404);
        var body = new byte[length];
        try { await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException)
        {
            throw new BridgeException("invalid_http", "HTTP body ended before Content-Length bytes were received.");
        }
        return (operation, body);
    }

    private static object Error(string code, string message) => new { ok = false, error = new { code, message } };
    private static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 408 => "Request Timeout", 409 => "Conflict", 413 => "Content Too Large",
        415 => "Unsupported Media Type", 431 => "Request Header Fields Too Large", _ => "Internal Server Error"
    };

    /// <summary>Stops admission, cancels queued work, and asynchronously drains started requests.</summary>
    public Task StopAsync()
    {
        lock (lifecycle)
        {
            if (stopped is not null) return stopped;
            stopping.Cancel();
            listener.Stop();
            stopped = DrainAsync(accepting ?? Task.CompletedTask);
            return stopped;
        }
    }

    private async Task DrainAsync(Task acceptLoop)
    {
        // Once acceptance has ended, no new task can enter activeRequests.
        try { await acceptLoop.ConfigureAwait(false); }
        finally
        {
            Task[] pending;
            lock (activeRequests) pending = activeRequests.ToArray();
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        // Host shutdown must never synchronously wait for work that needs its UI thread.
        // Observe an unexpected accept-loop failure even when no caller awaits graceful stop.
        _ = StopAsync().ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
