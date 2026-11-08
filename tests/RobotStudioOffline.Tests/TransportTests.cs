using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    static partial void AddTransportTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("HTTP literal loopback, auth, JSON envelope and refusal boundaries", () => TransportAsync().GetAwaiter().GetResult()));
        tests.Add(("Main-thread queue cancellation prevents late execution", () => QueueCancellationAsync().GetAwaiter().GetResult()));
        tests.Add(("Main-thread requests stay serialized through async completion", () => SerializationAsync().GetAwaiter().GetResult()));
        tests.Add(("SDK cancellation after execution begins never claims request did not run", () => SdkCancellationAsync().GetAwaiter().GetResult()));
        tests.Add(("Graceful stop drains active work, cancels queued writes and returns known outcomes", () => GracefulStopAsync().GetAwaiter().GetResult()));
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static async Task TransportAsync()
    {
        const string token = "0123456789ABCDEF0123456789ABCDEF";
        var calls = 0;
        using var server = new LoopbackServer(token, (operation, args, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<object>(new { name = "Offline test station", operation });
        }, 0);
        server.Start();
        Require(server.LocalEndpoint.Address.Equals(IPAddress.Loopback), "Listener escaped IPv4 loopback.");
        var port = server.LocalEndpoint.Port;
        async Task<string> Send(string path = "/station", string body = "{}", string? authority = null,
            string? auth = token, string extra = "", string length = "", string method = "POST")
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var request = $"{method} {path} HTTP/1.1\r\nHost: {authority ?? $"127.0.0.1:{port}"}\r\n" +
                (auth is null ? "" : $"Authorization: Bearer {auth}\r\n") +
                $"Content-Type: application/json\r\nContent-Length: {(length.Length == 0 ? Encoding.UTF8.GetByteCount(body).ToString() : length)}\r\n{extra}\r\n{body}";
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(request));
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        async Task<string> SendTruncatedBody()
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var request = $"POST /station HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {token}\r\n" +
                          "Content-Type: application/json\r\nContent-Length: 10\r\n\r\n{}";
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
            client.Client.Shutdown(SocketShutdown.Send);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        var success = await Send();
        Require(success.StartsWith("HTTP/1.1 200"), success);
        using (var json = JsonDocument.Parse(success.Split("\r\n\r\n")[1]))
        {
            Require(json.RootElement.GetProperty("ok").GetBoolean(), "Missing success envelope.");
            Require(json.RootElement.GetProperty("data").GetProperty("operation").GetString() == "station", "Bad endpoint dispatch.");
        }
        Require((await Send(auth: null)).Contains("unauthorized"), "Missing auth accepted.");
        Require((await Send(auth: "wrong")).Contains("unauthorized"), "Wrong auth accepted.");
        Require((await Send(authority: "attacker.test")).Contains("invalid_host"), "DNS rebinding authority accepted.");
        Require((await Send(extra: "Origin: https://attacker.test\r\n")).Contains("browser_origin_forbidden"), "Browser request accepted.");
        Require((await Send(extra: "Content-Length: 2\r\n")).Contains("invalid_http"), "Duplicate length accepted.");
        Require((await Send(extra: "Transfer-Encoding: chunked\r\n")).Contains("unsupported_encoding"), "Ambiguous HTTP framing accepted.");
        Require((await Send(length: "65537")).Contains("body_too_large"), "Oversized body accepted.");
        Require((await Send(path: "/station?x=1")).Contains("not_found"), "Unknown URL accepted.");
        Require((await Send(method: "GET")).Contains("method_not_allowed"), "GET accepted.");
        Require((await Send(body: "{bad}")).Contains("invalid_json"), "Malformed JSON accepted.");
        Require((await SendTruncatedBody()).Contains("invalid_http"), "Incomplete body was not classified as invalid HTTP.");
        Require((await Send(body: "{\"controller\":\"192.0.2.42\"}")).Contains("real_controller_permission_required"), "Hardware destination accepted without permission.");
        Require(calls == 1, $"Refused requests reached handler ({calls} calls).");
        foreach (var (operation, arguments) in new[]
        {
            ("io_read", "{\"names\":[\"diTestPresent\"]}"),
            ("io_write", "{\"name\":\"diTestPresent\",\"value\":1,\"simulateInput\":true}"),
            ("io_pulse", "{\"name\":\"doTest\",\"durationMs\":100}")
        })
        {
            var reply = await Send(path: "/" + operation, body: arguments);
            Require(reply.StartsWith("HTTP/1.1 200"), reply);
            using var payload = JsonDocument.Parse(reply.Split("\r\n\r\n")[1]);
            Require(payload.RootElement.GetProperty("data").GetProperty("operation").GetString() == operation, "I/O endpoint dispatched incorrectly.");
        }
        Require(calls == 4, "I/O endpoints did not reach the handler exactly once.");
        Require((await Send(path: "/io_write", body: "{\"name\":\"diTest\",\"value\":1,\"force\":true}")).Contains("invalid_request"), "Unsupported force flag accepted.");
        Require((await Send(path: "/io_pulse", body: "{\"name\":\"diTest\",\"controller\":\"192.0.2.42\"}")).Contains("real_controller_permission_required"), "I/O hardware destination accepted without permission.");
        Require(calls == 4, "Invalid I/O requests reached the handler.");
        var create = await Send(path: "/geometry_create", body: """{"name":"Test carton","shape":"box","size":[400,300,200]}""");
        var import = await Send(path: "/geometry_import", body: """{"name":"Guide","file":"C:\\CAD\\guide.step"}""");
        foreach (var (reply, operation) in new[] { (create, "geometry_create"), (import, "geometry_import") })
        {
            Require(reply.StartsWith("HTTP/1.1 200"), reply);
            using var payload = JsonDocument.Parse(reply.Split("\r\n\r\n")[1]);
            Require(payload.RootElement.GetProperty("data").GetProperty("operation").GetString() == operation, "Incorrect geometry route.");
        }
        var refusedImport = await Send(path: "/geometry_import", body: """{"name":"Guide","file":"\\\\host\\share\\guide.step"}""");
        Require(refusedImport.StartsWith("HTTP/1.1 400") && calls == 6, "Nonlocal import reached the handler.");
    }

    private static async Task QueueCancellationAsync()
    {
        Action? queued = null;
        var calls = 0;
        var dispatcher = new MainThreadDispatcher(action => queued = action,
            (_, _) => { calls++; return Task.FromResult<object>(new { }); });
        using var json = JsonDocument.Parse("{}");
        using var cancellation = new CancellationTokenSource();
        var pending = dispatcher.ExecuteAsync("station", json.RootElement, cancellation.Token);
        cancellation.Cancel();
        try { await pending; throw new Exception("Canceled request completed successfully."); }
        catch (OperationCanceledException) { }
        queued!();
        Require(calls == 0, "Timed-out request executed after timeout.");
    }

    private static async Task GracefulStopAsync()
    {
        const string token = "0123456789ABCDEF0123456789ABCDEF";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = 0;
        var executed = 0;
        var dispatcher = new MainThreadDispatcher(action => action(), async (_, arguments) =>
        {
            Interlocked.Increment(ref executed);
            entered.TrySetResult();
            await release.Task;
            return new { appliedValue = arguments.GetProperty("value").GetInt32() };
        });
        using var server = new LoopbackServer(token, (operation, arguments, cancellation) =>
        {
            var pending = dispatcher.ExecuteAsync(operation, arguments, cancellation);
            if (Interlocked.Increment(ref submitted) == 2) secondSubmitted.TrySetResult();
            return pending;
        }, 0);
        server.Start();
        var port = server.LocalEndpoint.Port;

        async Task<string> SendWrite(int value)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var body = $"{{\"name\":\"doTest\",\"value\":{value}}}";
            var request = $"POST /io_write HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {token}\r\n" +
                          $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
            client.Client.Shutdown(SocketShutdown.Send);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        try
        {
            var first = SendWrite(1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = SendWrite(0);
            await secondSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = server.StopAsync();
            Require(!stop.IsCompleted, "Stop abandoned an executing operation.");
            Require(ReferenceEquals(stop, server.StopAsync()), "Repeated stop did not return the same completion task.");
            server.Dispose(); // Nonblocking, including when a graceful stop is already pending.
            Require(!stop.IsCompleted, "Dispose abandoned an executing operation.");
            using (var rejected = new TcpClient())
            {
                try
                {
                    await rejected.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
                    throw new Exception("Stopped listener accepted a new connection.");
                }
                catch (SocketException) { }
            }
            var canceled = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Require(canceled.StartsWith("HTTP/1.1 408") && canceled.Contains("request_timeout"), "Queued request did not receive its cancellation outcome.");
            Require(executed == 1, "A queued write executed after stop.");
            release.TrySetResult();
            var completed = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Require(completed.StartsWith("HTTP/1.1 200"), "Executing write lost its success response during stop.");
            using (var payload = JsonDocument.Parse(completed.Split("\r\n\r\n")[1]))
                Require(payload.RootElement.GetProperty("data").GetProperty("appliedValue").GetInt32() == 1, "Known write outcome was lost.");
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            server.Dispose();
            Require(executed == 1, "Queued write executed after graceful stop completed.");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task SerializationAsync()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var dispatcher = new MainThreadDispatcher(action => action(), async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await released.Task; }
            return new { };
        });
        using var json = JsonDocument.Parse("{}");
        using var cancellation = new CancellationTokenSource();
        var first = dispatcher.ExecuteAsync("station", json.RootElement, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        var second = dispatcher.ExecuteAsync("station", json.RootElement, CancellationToken.None);
        Require(!first.IsCompleted && !second.IsCompleted && calls == 1, "Running request was abandoned or overlapped.");
        released.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Require(calls == 2, "Queued second request never executed.");
    }

    private static async Task SdkCancellationAsync()
    {
        var dispatcher = new MainThreadDispatcher(action => action(),
            (_, _) => Task.FromException<object>(new OperationCanceledException("SDK canceled.")));
        using var json = JsonDocument.Parse("{}");
        try
        {
            await dispatcher.ExecuteAsync("jump", json.RootElement, CancellationToken.None);
            throw new Exception("Expected SDK cancellation error.");
        }
        catch (BridgeException error)
        {
            Require(error.Code == "sdk_canceled" && error.StatusCode == 409, "SDK cancellation looked like an unexecuted queue timeout.");
        }
    }
}
