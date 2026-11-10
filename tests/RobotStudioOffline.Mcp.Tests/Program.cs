using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RobotStudioOffline.Core;
using RobotStudioOffline.Mcp;

var passed = 0;
var failed = 0;
var tokenPath = Path.Combine(Path.GetTempPath(), $"rs-mcp-test-{Guid.NewGuid():N}.token");
var token = new string('a', 64);
await File.WriteAllTextAsync(tokenPath, token);
try
{
    await Check("fixed-loopback CLI rejects controller URLs and bad ports", () =>
    {
        Throws<ArgumentException>(() => McpOptions.Parse(["--host", "192.0.2.42"]));
        Throws<ArgumentException>(() => McpOptions.Parse(["--port", "80"]));
        Throws<ArgumentException>(() => McpOptions.Parse(["--port", "65536"]));
        Assert(McpOptions.Parse([]).Port == 18426, "Default port changed.");
        return Task.CompletedTask;
    });
    await Check("token paths reject UNC and device access before filesystem reads", () =>
    {
        foreach (var path in new[] { @"\\192.0.2.42\share\token", @"\\?\C:\token", @"\\.\C:\token", "//192.0.2.42/share/token", "file://192.0.2.42/share/token" })
        {
            Throws<ArgumentException>(() => McpOptions.Parse(["--token-file", path]));
            Throws<ArgumentException>(() => new BridgeClient(new McpOptions(18426, path)));
        }
        return Task.CompletedTask;
    });
    await Check("tool schemas expose all station and regression operations", () =>
    {
        var catalog = ToolCatalog.Create();
        Assert(catalog.Count == 38, "Expected 38 tools.");
        Assert(catalog.Select(tool => tool!["name"]!.GetValue<string>()).Order().SequenceEqual(ToolCatalog.Names.Order()), "Tool dispatch names and catalog differ.");
        foreach (var tool in catalog)
        {
            var schema = tool!["inputSchema"]!;
            Assert(schema["type"]!.GetValue<string>() == "object", "Schema root is not object.");
            Assert(!schema["additionalProperties"]!.GetValue<bool>(), "Unknown arguments are allowed.");
            Assert(schema["oneOf"] is null && schema["anyOf"] is null, "Claude-incompatible schema root.");
        }
        Assert(catalog.Single(tool => tool!["name"]!.GetValue<string>() == "robot_pose")!["inputSchema"]!["required"]!.AsArray().Count == 2,
            "robot_pose frame requirements missing.");
        return Task.CompletedTask;
    });
    await Check("subprocess handshake, tool list, ping and clean EOF", async () =>
    {
        using var process = StartWrapper(tokenPath);
        await Send(process, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        Assert((await Read(process))["error"]!["code"]!.GetValue<int>() == -32002, "Missing init was accepted.");
        await Initialize(process, "2025-11-25");
        await Send(process, "{\"jsonrpc\":\"2.0\",\"id\":\"list\",\"method\":\"tools/list\"}");
        var tools = await Read(process);
        Assert(tools["id"]!.GetValue<string>() == "list", "String id changed.");
        Assert(tools["result"]!["tools"]!.AsArray().Count == ToolCatalog.Names.Length, "Wrong tools count.");
        await Send(process, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/unknown\"}");
        await Send(process, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
        Assert((await Read(process))["id"]!.GetValue<int>() == 3, "Notification produced a reply.");
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(process.ExitCode == 0, "Wrapper exited with error.");
        Assert((await process.StandardError.ReadToEndAsync()).Length == 0, "Unexpected stderr.");
        Assert((await process.StandardOutput.ReadToEndAsync()).Length == 0, "Unexpected stdout after EOF.");
    });
    await Check("parse errors, unsupported version negotiation and unknown methods", async () =>
    {
        using var client = new BridgeClient(new McpOptions(18426, tokenPath));
        var server = new McpServer(client);
        Assert((await server.ProcessAsync("{"))!["error"]!["code"]!.GetValue<int>() == -32700, "Bad JSON not detected.");
        Assert((await server.ProcessAsync("[]"))!["error"]!["code"]!.GetValue<int>() == -32600, "Batch not refused.");
        var response = await server.ProcessAsync(Initialization("future"));
        Assert(response!["result"]!["protocolVersion"]!.GetValue<string>() == "2025-11-25", "Version negotiation failed.");
        await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        Assert((await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"bogus\"}"))!["error"]!["code"]!.GetValue<int>() == -32601,
            "Unknown method not rejected.");
    });
    await Check("bounded subprocess input fails without corrupt stdout", async () =>
    {
        using var process = StartWrapper(tokenPath);
        await process.StandardInput.WriteLineAsync(new string(' ', 1024 * 1024 + 1));
        await process.StandardInput.FlushAsync();
        Assert((await Read(process))["error"]!["code"]!.GetValue<int>() == -32700, "Oversize message accepted.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(process.ExitCode == 0, "Oversize close was not clean.");
    });
    await Check("subprocess forwards JSON to authenticated loopback HTTP", async () =>
    {
        await using var mock = new MockBridge();
        var handling = mock.ServeOnce("{\"ok\":true,\"data\":{\"name\":\"MockStation\"}}");
        using var process = StartWrapper(tokenPath, mock.Port);
        await Initialize(process, "2024-11-05");
        await Send(process, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"station\",\"arguments\":{}}}");
        var response = await Read(process);
        var received = await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(received.Headers.StartsWith("POST /station HTTP/1.1\r\n", StringComparison.Ordinal), "Wrong route/method/version.");
        Assert(received.Headers.Contains($"Host: 127.0.0.1:{mock.Port}\r\n", StringComparison.Ordinal), "Non-loopback Host.");
        Assert(received.Headers.Contains($"Authorization: Bearer {token}\r\n", StringComparison.Ordinal), "Token not forwarded.");
        Assert(received.Body == "{}", "Body changed.");
        Assert(!response["result"]!["isError"]!.GetValue<bool>(), "Successful result marked failed.");
        Assert(response["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("MockStation", StringComparison.Ordinal), "Response missing.");
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    });
    await Check("controller arguments are refused before any HTTP request", async () =>
    {
        await using var mock = new MockBridge();
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
        try
        {
            await bridge.CallAsync("station", JsonSerializer.SerializeToElement(new { controller = "192.0.2.42" }));
            throw new Exception("Controller request accepted.");
        }
        catch (BridgeException exception)
        {
            Assert(exception.Code == "real_controller_permission_required", "Wrong controller refusal code.");
            Assert(!mock.HasPending, "Forbidden call reached HTTP.");
        }
    });
    await Check("HTTP redirect cannot redirect bridge traffic", async () =>
    {
        await using var mock = new MockBridge();
        await using var destination = new MockBridge();
        var handling = mock.ServeOnce("{}", "302 Found", $"Location: http://127.0.0.1:{destination.Port}/forbidden\r\n");
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
        try
        {
            await bridge.CallAsync("station", JsonSerializer.SerializeToElement(new { }));
            throw new Exception("Redirect accepted.");
        }
        catch (BridgeException exception)
        {
            Assert(exception.Code == "redirect_forbidden", "Wrong redirect refusal.");
            Assert(!destination.HasPending, "Redirect was followed.");
        }
        await handling.WaitAsync(TimeSpan.FromSeconds(5));
    });
    await Check("bridge error JSON becomes MCP tool error", async () =>
    {
        await using var mock = new MockBridge();
        var handling = mock.ServeOnce("{\"ok\":false,\"error\":{\"code\":\"no_station\",\"message\":\"No station.\"}}", "409 Conflict");
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
        var server = new McpServer(bridge);
        await server.ProcessAsync(Initialization("2025-11-25"));
        await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var response = await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"station\"}}");
        Assert(response!["result"]!["isError"]!.GetValue<bool>(), "HTTP failure not marked error.");
        Assert(response["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("no_station", StringComparison.Ordinal), "Bridge error lost.");
        await handling.WaitAsync(TimeSpan.FromSeconds(5));
    });
    await Check("missing token reports owner load step without connecting", async () =>
    {
        await using var mock = new MockBridge();
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath + ".missing"));
        var server = new McpServer(bridge);
        await server.ProcessAsync(Initialization("2025-11-25"));
        await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var response = await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"station\"}}");
        Assert(response!["result"]!["isError"]!.GetValue<bool>(), "Missing token succeeded.");
        Assert(response["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("bridge_not_loaded", StringComparison.Ordinal), "Missing owner instruction.");
        Assert(!mock.HasPending, "Missing token connected.");
    });
    await Check("HTTP success without bridge JSON envelope is rejected", async () =>
    {
        await using var mock = new MockBridge();
        var handling = mock.ServeOnce("{\"unrelatedService\":true}");
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
        try
        {
            await bridge.CallAsync("station", JsonSerializer.SerializeToElement(new { }));
            throw new Exception("Invalid envelope was accepted.");
        }
        catch (BridgeException exception)
        {
            Assert(exception.Code == "bridge_invalid_response", "Wrong envelope error.");
        }
        await handling.WaitAsync(TimeSpan.FromSeconds(5));
    });
    await Check("token is refreshed after add-in reload", async () =>
    {
        await using var mock = new MockBridge();
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
        var rotated = new string('b', 64);
        await File.WriteAllTextAsync(tokenPath, rotated);
        try
        {
            var handling = mock.ServeOnce("{\"ok\":true,\"data\":{}}");
            await bridge.CallAsync("station", JsonSerializer.SerializeToElement(new { }));
            var request = await handling.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(request.Headers.Contains($"Authorization: Bearer {rotated}\r\n", StringComparison.Ordinal), "Rotated token was not read.");
        }
        finally { await File.WriteAllTextAsync(tokenPath, token); }
    });
    await Check("screenshot is returned as an MCP PNG image", async () =>
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2PZsAAAAASUVORK5CYII=";
        await using var mock = new MockBridge();
        var handling = mock.ServeOnce(JsonSerializer.Serialize(new { ok = true, data = new { mimeType = "image/png", data = png, width = 1, height = 1, scope = "station_only" } }));
        using var process = StartWrapper(tokenPath, mock.Port);
        await Initialize(process, "2025-11-25");
        await Send(process, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"screenshot\",\"arguments\":{}}}");
        var response = await Read(process);
        var received = await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(received.Headers.StartsWith("POST /screenshot HTTP/1.1\r\n", StringComparison.Ordinal), "Wrong screenshot endpoint.");
        Assert(!response["result"]!["isError"]!.GetValue<bool>(), "PNG capture returned error.");
        var image = response["result"]!["content"]![0]!;
        Assert(image["type"]!.GetValue<string>() == "image" && image["mimeType"]!.GetValue<string>() == "image/png", "Not an MCP PNG image.");
        Assert(image["data"]!.GetValue<string>() == png, "PNG data changed.");
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    });
    await Check("invalid screenshot MIME, base64 and signature are rejected", async () =>
    {
        foreach (var invalid in new[]
        {
            new { mimeType = "text/html", data = "aGVsbG8=", width = 1, height = 1 },
            new { mimeType = "image/png", data = "not base64", width = 1, height = 1 },
            new { mimeType = "image/png", data = "aGVsbG8=", width = 1, height = 1 }
        })
        {
            await using var mock = new MockBridge();
            var handling = mock.ServeOnce(JsonSerializer.Serialize(new { ok = true, data = invalid }));
            using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
            var server = new McpServer(bridge);
            await server.ProcessAsync(Initialization("2025-11-25"));
            await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var response = await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"screenshot\"}}");
            Assert(response!["result"]!["isError"]!.GetValue<bool>(), "Invalid image was accepted.");
            Assert(response["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("bridge_invalid_response", StringComparison.Ordinal), "Wrong image validation error.");
            await handling.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });
    await Check("development schemas preserve batch and explicit-write boundaries", () =>
    {
        var tools = ToolCatalog.Create().ToDictionary(tool => tool!["name"]!.GetValue<string>(), tool => tool!["inputSchema"]!);
        var preview = tools["preview_targets"]["properties"]!;
        Assert(preview["tool"] is null && preview["targets"]!["items"]!["properties"]!["tool"] is null, "Preview unexpectedly requires tool data.");
        Assert(preview["targets"]!["items"]!["properties"]!["jointtarget"] is null, "Preview accepts non-Cartesian targets.");
        Assert(tools["validate_targets"]["properties"]!["targets"]!["items"]!["properties"]!["robot"] is null, "Batch permits robot selection per item.");
        Assert(tools["data_sync"]["required"]!.AsArray().Any(item => item!.GetValue<string>() == "direction"), "Sync direction is not explicit.");
        Assert(tools["edit_targets"]["properties"]!["changes"]!["maxItems"]!.GetValue<int>() == 50, "Edit batch limit is wrong.");
        return Task.CompletedTask;
    });
    await Check("all development operations forward validated JSON to their exact endpoints", async () =>
    {
        var examples = new Dictionary<string, string>
        {
            ["path_details"] = "{}",
            ["validate_targets"] = "{\"targets\":[{\"jointtarget\":{\"robotAxes\":[36,-18,7,0,0,24]}}]}",
            ["preview_targets"] = "{\"action\":\"clear\"}",
            ["edit_targets"] = "{\"changes\":[{\"name\":\"Module1/pNew\",\"create\":true,\"workObject\":\"wobj0\",\"robtarget\":{\"position\":[100,200,300],\"quaternion\":[1,0,0,0],\"configuration\":[0,0,0,0]}}]}",
            ["data_compare"] = "{\"data\":[{\"name\":\"Module1/pReference\",\"kind\":\"target\"}]}",
            ["data_sync"] = "{\"direction\":\"to_station\",\"data\":[{\"name\":\"Module1/pReference\",\"kind\":\"target\"}]}"
        };
        foreach (var (operation, arguments) in examples)
        {
            await using var mock = new MockBridge();
            var handling = mock.ServeOnce("{\"ok\":true,\"data\":{\"mock\":true}}");
            using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
            using var parsed = JsonDocument.Parse(arguments);
            var result = await bridge.CallAsync(operation, parsed.RootElement);
            var received = await handling.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(received.Headers.StartsWith($"POST /{operation} HTTP/1.1\r\n", StringComparison.Ordinal), "Wrong development route.");
            Assert(received.Body == arguments && !result.IsError, "Development request or response changed.");
        }
    });
    await Check("regression tools validate and forward only their named localhost routes", async () =>
    {
        const string runId = "67ea03997ca246229c4ca615f51c6663";
        var files = new[] { @"C:\Work\Main.modx" };
        var examples = new Dictionary<string, object>
        {
            ["rapid_load"] = new { task="T_ROB1", files }, ["rapid_check"] = new { },
            ["rapid_diff"] = new { files, includeReal=false }, ["rapid_sync_real"] = new { task="T_ROB1", files },
            ["tcp_record"] = new { action="start", tool="tProbe", workObject="wobj0", rateHz=50, phaseSignal="goTestPhase" },
            ["run_review"] = new { runId, export=false }, ["run_view"] = new { runId, view="side" },
            ["geometry"] = new { limit=2 }, ["collision_set"] = new { name="barrier", first=new[]{"robot"}, second=new[]{"beam"} },
            ["simulation_speed"] = new { factor=10 }, ["raycast"] = new { tool="tip", origin=new[]{0,0,0}, direction=new[]{0,0,1}, maxDistance=3000 }
        };
        foreach (var (operation, payload) in examples)
        {
            await using var mock = new MockBridge();
            var handling = mock.ServeOnce("""{"ok":true,"data":{"mock":true}}""");
            using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
            var json = JsonSerializer.SerializeToElement(payload);
            var result = await bridge.CallAsync(operation,json);
            var received = await handling.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(received.Headers.StartsWith($"POST /{operation} HTTP/1.1\r\n", StringComparison.Ordinal) &&
                received.Body == json.GetRawText() && !result.IsError, "Incorrect regression tool forwarding.");
        }
    });
    await Check("I/O schemas describe paging, bounded pulses and explicit input simulation", () =>
    {
        var catalog = ToolCatalog.Create().ToDictionary(tool => tool!["name"]!.GetValue<string>(), tool => tool!);
        var read = catalog["io_read"]["inputSchema"]!["properties"]!;
        Assert(read["names"]!["minItems"]!.GetValue<int>() == 1 && read["names"]!["maxItems"]!.GetValue<int>() == 100
            && read["names"]!["uniqueItems"]!.GetValue<bool>(), "Named-read bounds are missing.");
        Assert(read["offset"]!["minimum"]!.GetValue<int>() == 0 && read["offset"]!["default"]!.GetValue<int>() == 0,
            "Read offset bounds/default are wrong.");
        Assert(read["limit"]!["minimum"]!.GetValue<int>() == 1 && read["limit"]!["maximum"]!.GetValue<int>() == 500
            && read["limit"]!["default"]!.GetValue<int>() == 100, "Read page bounds/default are wrong.");
        var pulse = catalog["io_pulse"]["inputSchema"]!["properties"]!;
        Assert(pulse["durationMs"]!["minimum"]!.GetValue<int>() == 1 && pulse["durationMs"]!["maximum"]!.GetValue<int>() == 5000
            && pulse["durationMs"]!["default"]!.GetValue<int>() == 100, "Pulse duration bounds/default are wrong.");
        foreach (var operation in new[] { "io_read", "io_write", "io_pulse" })
        {
            var tool = catalog[operation];
            var schema = tool["inputSchema"]!;
            var properties = schema["properties"]!;
            Assert(tool["annotations"]!["readOnlyHint"]!.GetValue<bool>() == (operation == "io_read"), "Wrong I/O read-only annotation.");
            Assert(properties["force"] is null && properties["release"] is null && properties["controller"] is null, "Unexpected force/controller API.");
            if (operation == "io_read") continue;
            Assert(!properties["simulateInput"]!["default"]!.GetValue<bool>(), "Input simulation is implicit.");
            Assert(schema["required"]!.AsArray().Any(item => item!.GetValue<string>() == "name"), "Write signal selection is optional.");
        }
        Assert(catalog["io_write"]["inputSchema"]!["required"]!.AsArray().Any(item => item!.GetValue<string>() == "value"), "Write value is optional.");
        return Task.CompletedTask;
    });
    await Check("I/O and engineering MCP tools forward named reads and writes unchanged", async () =>
    {
        (string Operation, string Arguments)[] examples =
        [
            ("configuration", """{"domain":"MOC","limit":2}"""),
            ("body_edit", """{"action":"state","object":"Box ID"}"""),
            ("body_edit", """{"action":"attach","object":"Box ID","parent":"Gripper ID"}"""),
            ("collision_set_delete", """{"set":"Trial clearance"}"""),
            ("body_motion", """{"action":"start","part":"Carrier","deltaMm":[100,0,0],"durationMs":1000}"""),
            ("smart_component", """{"action":"catalog"}"""),
            ("station_logic", """{"action":"connect","kind":"property","source":{"component":"Entry eye","member":"SensedPart"},"target":{"component":"Pickup","member":"Child"}}"""),
            ("configuration", """{"domain":"MOC","type":"ARM_LOAD","instance":"example","action":"set","attribute":"mass","expected":1,"value":2}"""),
            ("io_project", "{}"),
            ("io_project", """{"type":"EIO_SIGNAL","instance":"diTest","action":"set","attribute":"Label","expected":"","value":"Part present"}"""),
            ("simulation_setup", """{"section":"participants"}"""),
            ("simulation_setup", """{"action":"set_world","floorHeightMm":0}"""),
            ("safemove", """{"file":"C:\\Exports\\geometry.xml"}"""),
            ("safemove", """{"action":"edit_zone_copy","file":"C:\\Exports\\geometry.xml","outputName":"review.xml","driveModuleId":1,"zoneId":1,"topM":2}"""),
            ("io_read", "{}"),
            ("io_read", "{\"robot\":\"Robot1\",\"names\":[\"diTestPresent\",\"doTestValve\"]}"),
            ("io_read", "{\"offset\":5,\"limit\":500}"),
            ("io_write", "{\"name\":\"doTestValve\",\"value\":1}"),
            ("io_write", "{\"name\":\"diTestPresent\",\"value\":0,\"simulateInput\":true}"),
            ("io_write", "{\"name\":\"diTestPresent\",\"value\":1,\"simulateInput\":false}"),
            ("io_write", "{\"name\":\"goStatus\",\"value\":4294967295,\"simulateInput\":false}"),
            ("io_write", "{\"name\":\"aoPressure\",\"value\":-0.125}"),
            ("io_pulse", "{\"name\":\"doTestValve\"}"),
            ("io_pulse", "{\"name\":\"diTestPresent\",\"durationMs\":5000,\"simulateInput\":true}")
        ];
        foreach (var (operation, arguments) in examples)
        {
            await using var mock = new MockBridge();
            var handling = mock.ServeOnce("{\"ok\":true,\"data\":{\"mock\":true}}");
            using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
            var server = new McpServer(bridge);
            await server.ProcessAsync(Initialization("2025-11-25"));
            await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"" + operation + "\",\"arguments\":" + arguments + "}}";
            var response = await server.ProcessAsync(request);
            var received = await handling.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(received.Headers.StartsWith($"POST /{operation} HTTP/1.1\r\n", StringComparison.Ordinal), "Wrong I/O route.");
            Assert(received.Body == arguments, "I/O arguments changed.");
            Assert(!response!["result"]!["isError"]!.GetValue<bool>(), "Valid I/O forwarding returned an error.");
            Assert(JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!["data"]!["mock"]!.GetValue<bool>(), "I/O response was lost.");
        }
    });
    await Check("invalid I/O arguments are rejected before any HTTP request", async () =>
    {
        (string Operation, string Arguments)[] examples =
        [
            ("io_read", "{\"names\":[]}"),
            ("io_read", "{\"names\":[\"diTestPresent\",\"diTestPresent\"]}"),
            ("io_read", "{\"names\":[\"diTestPresent\"],\"offset\":0}"),
            ("io_read", "{\"names\":[\"diTestPresent\"],\"limit\":100}"),
            ("io_read", "{\"offset\":-1}"),
            ("io_read", "{\"offset\":1.5}"),
            ("io_read", "{\"limit\":0}"),
            ("io_read", "{\"limit\":501}"),
            ("io_read", JsonSerializer.Serialize(new { names = Enumerable.Range(0, 101).Select(index => $"di{index}").ToArray() })),
            ("io_write", "{\"name\":\"doTestValve\"}"),
            ("io_write", "{\"name\":\"doTestValve\",\"value\":\"1\"}"),
            ("io_write", "{\"name\":\"doTestValve\",\"value\":1e9999}"),
            ("io_write", "{\"name\":\"\",\"value\":1}"),
            ("io_write", "{\"name\":\"doTestValve\",\"value\":1,\"simulateInput\":1}"),
            ("io_write", "{\"name\":\"doTestValve\",\"value\":1,\"force\":true}"),
            ("io_write", "{\"name\":\"doTestValve\",\"value\":1,\"value\":0}"),
            ("io_pulse", "{}"),
            ("io_pulse", "{\"name\":\"doTestValve\",\"durationMs\":0}"),
            ("io_pulse", "{\"name\":\"doTestValve\",\"durationMs\":5001}"),
            ("io_pulse", "{\"name\":\"doTestValve\",\"durationMs\":1.5}"),
            ("io_pulse", "{\"name\":\"doTestValve\",\"release\":true}")
        ];
        await using var mock = new MockBridge();
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
        var server = new McpServer(bridge);
        await server.ProcessAsync(Initialization("2025-11-25"));
        await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        foreach (var (operation, arguments) in examples)
        {
            var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"" + operation + "\",\"arguments\":" + arguments + "}}";
            var response = await server.ProcessAsync(request);
            Assert(response!["result"]!["isError"]!.GetValue<bool>(), $"Invalid {operation} arguments were accepted: {arguments}");
            Assert(response["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("invalid_request", StringComparison.Ordinal), "Wrong I/O validation error.");
            Assert(!mock.HasPending, "Invalid I/O arguments reached HTTP.");
        }
    });
    await Check("I/O tools refuse real-controller selectors before HTTP", async () =>
    {
        await using var mock = new MockBridge();
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
        var server = new McpServer(bridge);
        await server.ProcessAsync(Initialization("2025-11-25"));
        await server.ProcessAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        foreach (var operation in new[] { "io_read", "io_write", "io_pulse" })
        {
            var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"" + operation + "\",\"arguments\":{\"controller\":\"192.0.2.42\"}}}";
            var response = await server.ProcessAsync(request);
            Assert(response!["result"]!["isError"]!.GetValue<bool>(), "I/O controller selector was accepted.");
            Assert(response["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("real_controller_permission_required", StringComparison.Ordinal), "Wrong I/O controller permission error.");
            Assert(!mock.HasPending, "I/O controller selector reached HTTP.");
        }
    });
    await Check("geometry tools publish dimensions, placement and station write hints", () =>
    {
        var tools = ToolCatalog.Create().ToDictionary(tool => tool!["name"]!.GetValue<string>(), tool => tool!);
        foreach (var operation in new[] { "geometry_create", "geometry_import" })
        {
            var tool = tools[operation];
            var properties = tool["inputSchema"]!["properties"]!;
            Assert(!tool["annotations"]!["readOnlyHint"]!.GetValue<bool>(), "Geometry mutation advertised as read-only.");
            Assert(!tool["annotations"]!["idempotentHint"]!.GetValue<bool>(), "Geometry mutation advertised as idempotent.");
            Assert(properties["position"]!["minItems"]!.GetValue<int>() == 3 && properties["position"]!["maxItems"]!.GetValue<int>() == 3, "World position must have three coordinates.");
            Assert(properties["quaternion"]!["minItems"]!.GetValue<int>() == 4 && properties["quaternion"]!["maxItems"]!.GetValue<int>() == 4, "World orientation must have four quaternion components.");
        }
        var create = tools["geometry_create"]["inputSchema"]!;
        var dimensions = create["properties"]!;
        Assert(create["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToHashSet().SetEquals(["name", "shape"]), "Required geometry creation arguments differ.");
        Assert(dimensions["shape"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ToHashSet().SetEquals(["box", "cylinder", "sphere"]), "Unexpected primitive set.");
        Assert(dimensions["size"]!["maxItems"]!.GetValue<int>() == 3 && dimensions["size"]!["items"]!["exclusiveMinimum"]!.GetValue<int>() == 0, "Box dimensions lack positive bounds.");
        Assert(dimensions["height"]!["exclusiveMinimum"]!.GetValue<int>() == 0 && dimensions["radius"]!["exclusiveMinimum"]!.GetValue<int>() == 0, "Radius and height lack positive bounds.");
        var import = tools["geometry_import"]["inputSchema"]!;
        Assert(import["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToHashSet().SetEquals(["name", "file"]), "Required geometry import arguments differ.");
        return Task.CompletedTask;
    });
    await Check("MCP forwards primitive geometry and local CAD import to their HTTP routes", async () =>
    {
        await using var mock = new MockBridge();
        using var process = StartWrapper(tokenPath, mock.Port);
        await Initialize(process, "2025-11-25");
        (string Operation, string Arguments)[] examples =
        [
            ("geometry_create", """{"name":"Fixture box","shape":"box","size":[30,50,75],"position":[-20,100,250],"quaternion":[0,0,0,1]}"""),
            ("geometry_create", """{"name":"Post","shape":"cylinder","radius":5,"height":120}"""),
            ("geometry_create", """{"name":"Probe","shape":"sphere","radius":2.5}"""),
            ("geometry_import", """{"name":"Base plate","file":"C:\\CAD\\Base plate.step","position":[0,10,20],"quaternion":[1,0,0,0]}""")
        ];
        for (var index = 0; index < examples.Length; index++)
        {
            var (operation, arguments) = examples[index];
            var handling = mock.ServeOnce("""{"ok":true,"data":{"mockGeometry":true}}""");
            var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = index + 2, method = "tools/call", @params = new { name = operation, arguments = JsonNode.Parse(arguments) } });
            await Send(process, request);
            var response = await Read(process);
            Assert(!response["result"]!["isError"]!.GetValue<bool>(), $"{operation} was rejected.");
            var received = await handling.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(received.Headers.StartsWith($"POST /{operation} HTTP/1.1\r\n", StringComparison.Ordinal), "Geometry request forwarded to wrong operation.");
            Assert(JsonNode.DeepEquals(JsonNode.Parse(arguments), JsonNode.Parse(received.Body)), "Geometry arguments changed during forwarding.");
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert(JsonNode.Parse(text)!["data"]!["mockGeometry"]!.GetValue<bool>(), "Geometry response was lost.");
        }
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    });
    await Check("MCP rejects incompatible dimensions and remote CAD paths before HTTP", async () =>
    {
        await using var mock = new MockBridge();
        using var bridge = new BridgeClient(new McpOptions(mock.Port, tokenPath));
        var server = new McpServer(bridge);
        await server.ProcessAsync(Initialization("2025-11-25"));
        await server.ProcessAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        (string Operation, string Arguments)[] invalid =
        [
            ("geometry_create", """{"name":"A","shape":"sphere","radius":1,"height":1}"""),
            ("geometry_create", """{"name":"A","shape":"box","size":[1,0,1]}"""),
            ("geometry_import", """{"name":"A","file":"\\\\example.invalid\\share\\part.stp"}""")
        ];
        foreach (var (operation, arguments) in invalid)
        {
            var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = operation, arguments = JsonNode.Parse(arguments) } });
            var response = await server.ProcessAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
            Assert(response!["result"]!["isError"]!.GetValue<bool>(), "Invalid geometry arguments were accepted.");
            Assert(!mock.HasPending, "Invalid geometry request was sent to HTTP.");
        }
    });
    await Check("data comparison handles quaternion sign and float conversion noise", () =>
    {
        using var station = JsonDocument.Parse("{\"frame\":{\"position\":[1000.123456,0,50],\"quaternion\":[0.7071067811865476,0,0,0.7071067811865476]},\"configuration\":[1,0,0,0]}");
        using var virtualValue = JsonDocument.Parse("{\"configuration\":[1,0,0,0],\"frame\":{\"quaternion\":[-0.7071067811865476,0,0,-0.7071067811865476],\"position\":[1000.1234741210938,0,50]}}");
        Assert(DataValueComparison.DifferencePaths(station.RootElement, virtualValue.RootElement).Length == 0, "Equivalent poses differ.");
        return Task.CompletedTask;
    });
    await Check("data comparison reports meaningful drift, missing values and invalid orientation", () =>
    {
        using var station = JsonDocument.Parse("{\"frame\":{\"position\":[1000,0,50],\"quaternion\":[1,0,0,0]},\"configuration\":[1,0,0,0],\"robotHold\":true}");
        using var virtualValue = JsonDocument.Parse("{\"frame\":{\"position\":[1001,0,50],\"quaternion\":[0,0,0,0]},\"configuration\":[2,0,0,0]}");
        var paths = DataValueComparison.DifferencePaths(station.RootElement, virtualValue.RootElement);
        Assert(paths.Contains("$.frame.position[0]") && paths.Contains("$.frame.quaternion") && paths.Contains("$.configuration[0]") && paths.Contains("$.robotHold"), "Differences were lost.");
        return Task.CompletedTask;
    });
}
finally { File.Delete(tokenPath); }
Console.WriteLine($"MCP tests: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {name}: {exception}"); }
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static Process StartWrapper(string tokenFile, int port = 18426)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("exec");
    start.ArgumentList.Add("--runtimeconfig");
    start.ArgumentList.Add(Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".runtimeconfig.json"));
    start.ArgumentList.Add(typeof(McpServer).Assembly.Location);
    start.ArgumentList.Add("--token-file");
    start.ArgumentList.Add(tokenFile);
    start.ArgumentList.Add("--port");
    start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return Process.Start(start) ?? throw new Exception("Could not start MCP process.");
}
static string Initialization(string version) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = version, capabilities = new { }, clientInfo = new { name = "test", version = "1" } } });
static async Task Initialize(Process process, string version)
{
    await Send(process, Initialization(version));
    Assert((await Read(process))["result"]!["protocolVersion"]!.GetValue<string>() == version, "Wrong negotiated version.");
    await Send(process, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
}
static async Task Send(Process process, string json)
{
    await process.StandardInput.WriteLineAsync(json);
    await process.StandardInput.FlushAsync();
}
static async Task<JsonNode> Read(Process process)
{
    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    return JsonNode.Parse(line ?? throw new Exception("Unexpected EOF.")) ?? throw new Exception("Null JSON response.");
}

sealed class MockBridge : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public MockBridge() { listener.Start(); }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool HasPending => listener.Pending();
    public async Task<(string Headers, string Body)> ServeOnce(string body, string status = "200 OK", string extraHeaders = "")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = client.GetStream();
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, timeout.Token) == 0) throw new IOException("Truncated request.");
            bytes.Add(one[0]);
            if (bytes.Count > 8192) throw new IOException("Headers too large.");
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
        }
        var headers = Encoding.ASCII.GetString(bytes.ToArray());
        var lengthLine = headers.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var length = int.Parse(lengthLine.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, timeout.Token);
        var payload = Encoding.UTF8.GetBytes(body);
        var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n{extraHeaders}Connection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders, timeout.Token);
        await stream.WriteAsync(payload, timeout.Token);
        return (headers, Encoding.UTF8.GetString(data));
    }
    public ValueTask DisposeAsync() { listener.Stop(); return ValueTask.CompletedTask; }
}
