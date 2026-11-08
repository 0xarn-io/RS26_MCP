using System.Reflection;
using System.Text;
using System.Text.Json;
using RobotStudioOffline.Core;
using RobotStudioOffline.Mcp;

Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false);
try
{
    if (args.Length > 0 && args[0] == "--check-files")
    {
        var request = RapidModuleRequests.Parse("rapid_check", JsonSerializer.SerializeToElement(new { files = args[1..] }));
        var installRoot = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "RobotStudioDir").Value!;
        var parser = Assembly.LoadFrom(Path.Combine(installRoot, "Bin", "RobotStudio.Services.RapidLanguage.dll"));
        var checks = request.Files.Select(path => RapidSourceDiagnostics.Inspect(RapidSource.Read(path, allowUnidentifiedModule: true), parser)).ToArray();
        var failed = checks.Any(check => check.Correct == false);
        var verified = checks.All(check => check.Correct == true);
        Console.WriteLine(JsonSerializer.Serialize(new { scope = "local_source_only", sourceChecks = checks }, JsonDefaults.Options));
        return failed ? 2 : verified ? 0 : 3;
    }
    if (args is ["--review-run"])
    {
        // One run_review request on stdin. Works directly on an exported run with RobotStudio closed.
        var line = await Console.In.ReadLineAsync() ?? throw new ArgumentException("Expected one run_review JSON object on stdin.");
        if (line.Length > 65536) throw new ArgumentException("Maximum request size is 65536 characters.");
        using var payload = JsonDocument.Parse(line);
        var request = (RunReviewRequest)RunRequests.Parse("run_review", payload.RootElement);
        var result = await new RunCaptureStore().ReviewAsync(request);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
        return 0;
    }
    var options = McpOptions.Parse(args);
    using var bridge = new BridgeClient(options);
    await new McpServer(bridge).RunAsync(Console.In, Console.Out);
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or DecoderFallbackException or BridgeException or JsonException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
