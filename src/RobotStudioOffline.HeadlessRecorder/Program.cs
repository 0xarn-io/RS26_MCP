using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.HeadlessRecorder;

internal sealed record Options(TcpRecordRequest Request, string TaskName, int Seconds);

internal static class Program
{
    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = ["--tool", "--work-object", "--robot", "--task", "--rate-hz", "--seconds", "--max-samples",
            "--phase-signal", "--live-frame-signal", "--command"];
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !allowed.Contains(args[i]) || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown, repeated or incomplete recorder option. Use --help.");
        }
        string Identifier(string key, string? fallback = null)
        {
            var value = values.TryGetValue(key, out var supplied) ? supplied : fallback ?? throw new ArgumentException("Required option " + key);
            if (value.Length is < 1 or > 128 || !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
                value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new ArgumentException(key + " requires an unqualified controller identifier.");
            return value;
        }
        int Number(string key, int min, int max, int fallback)
        {
            if (!values.TryGetValue(key, out var text)) return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
                throw new ArgumentException($"{key} must be an integer in {min}..{max}.");
            return number;
        }
        var robot = Identifier("--robot", "ROB_1");
        var task = Identifier("--task", "T_ROB1");
        var seconds = Number("--seconds", 1, 1800, 60);
        var hz = Number("--rate-hz", 20, 50, 25);
        var cap = Number("--max-samples", 1, 36000, Math.Min(hz * seconds, 36000));
        JsonElement? command = null;
        if (values.TryGetValue("--command", out var description))
        {
            if (description.Length > 8192) throw new ArgumentException("--command exceeds 8192 characters.");
            command = JsonSerializer.SerializeToElement(description);
        }
        return new(new("start", Robot: robot, Tool: Identifier("--tool"), WorkObject: Identifier("--work-object"),
            RateHz: hz, MaxSamples: cap, PhaseSignal: values.ContainsKey("--phase-signal") ? Identifier("--phase-signal") : null,
            LiveFrameSignal: values.ContainsKey("--live-frame-signal") ? Identifier("--live-frame-signal") : null, Command: command), task, seconds);
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--help"])
            {
                Console.WriteLine("RobotStudioOffline.HeadlessRecorder --tool NAME --work-object NAME [--seconds 60] [--rate-hz 25] [--task T_ROB1] [--robot ROB_1] [--max-samples N] [--phase-signal NAME] [--live-frame-signal NAME] [--command LABEL]");
                Console.WriteLine("Start headless-vc.ps1 first. Ctrl+C finalizes the trace. --self-test runs offline.");
                return 0;
            }
            var selfTest = args is ["--self-test"];
            var options = selfTest ? null : Parse(args);
            var sdk = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RobotStudioDir").Value!;
            var bin = Path.Combine(sdk, "Bin");
            if (!File.Exists(Path.Combine(bin, "ABB.Robotics.Controllers.PC.dll"))) throw new FileNotFoundException("RobotStudio PC SDK is missing.");
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                if (string.IsNullOrEmpty(name.Name) || Path.GetFileName(name.Name) != name.Name) return null;
                var path = Path.Combine(bin, name.Name + ".dll");
                return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
            };
            // The installed SDK resolves its native dependencies from its own Bin directory.
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            if (selfTest) return SelfTest();
            using var ownership = CloneOwnership.Open();
            return Capture(options!, ownership);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    // Install the assembly resolver and validate the local host before JIT-resolving PC SDK.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Capture(Options options, CloneOwnership ownership) => Recorder.Capture(options, ownership);

    internal static int MissedSlots(double actualMs, long nextSlot, double periodMs) =>
        Math.Max(0, checked((int)Math.Floor(actualMs / periodMs) - (int)nextSlot));

    private static int SelfTest()
    {
        var options = Parse(["--tool", "tool0", "--work-object", "wobj0", "--rate-hz", "50", "--seconds", "1"]);
        if (options.Request.MaxSamples != 50 || options.TaskName != "T_ROB1" || options.Request.PhaseSignal is not null || options.Request.LiveFrameSignal is not null ||
            MissedSlots(139, 2, 20) != 4 || MissedSlots(21, 1, 20) != 0) throw new Exception("Recorder schedule check failed.");
        foreach (string[] invalid in new string[][] { ["--tool", "t"], ["--host", "192.0.2.42"],
            ["--tool", "t", "--work-object", "w", "--rate-hz", "51"], ["--tool", "t", "--tool", "t", "--work-object", "w"] })
        {
            try { Parse(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid recorder arguments accepted.");
        }
        Recorder.CheckFourAxisLayout();
        Console.WriteLine("PASS: recorder options, missed-sample scheduling, and four-axis robot RAPID 1/2/3/6 joint slots. No controller access.");
        return 0;
    }
}
