using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RobotStudioOffline.Core;

public sealed partial class RunCaptureStore
{
    private readonly SemaphoreSlim exporting = new(1, 1);
    public string GetArtifactDirectory(string runId) => Path.Combine(artifactRoot, RunRequests.ValidateId(runId));

    public RunReviewResult? ReadReview(string runId)
    {
        var id = RunRequests.ValidateId(runId);
        try
        {
            using var file = File.OpenRead(Path.Combine(GetArtifactDirectory(id), "review.json"));
            var result = JsonSerializer.Deserialize<RunReviewResult>(file, JsonDefaults.Options);
            if (result is null || result.RunId != id || result.Assertions is null || result.Rules is null || result.Clearances is null)
                throw new BridgeException("invalid_run_artifact", "The saved review has inconsistent metadata.", 409);
            return result;
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (JsonException) { throw new BridgeException("invalid_run_artifact", "The saved review is invalid.", 409); }
    }

    public string SaveView(string runId, string view, byte[] png)
    {
        if (view is not ("top" or "side")) throw new BridgeException("invalid_request", "View must be top or side.");
        if (!png.AsSpan().StartsWith((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]))
            throw new BridgeException("invalid_view", "The renderer must provide a PNG image.", 409);
        var directory = GetArtifactDirectory(runId);
        if (!File.Exists(Path.Combine(directory, "run.json")))
        {
            lock (gate) _ = Find(runId);
            Directory.CreateDirectory(directory);
        }
        var file = Path.Combine(directory, view + ".png");
        Atomic(file, output => output.Write(png));
        return file;
    }

    public async Task<object> ReviewAsync(RunReviewRequest request)
    {
        var snapshot = await Task.Run(() => Snapshot(request.RunId)).ConfigureAwait(false);
        if (request.ReuseRules)
            request = request with { Assertions = ReadReview(request.RunId)?.Rules ?? snapshot.Request.Assertions };
        var verdict = Evaluate(snapshot, request);
        var artifacts = request.Export ? await ExportAsync(snapshot, verdict).ConfigureAwait(false) : null;
        return new { verdict, artifacts };
    }

    public async Task<RunArtifacts> ExportAsync(RunSnapshot snapshot, RunReviewResult? review = null)
    {
        if (snapshot.Status.State != "stopped" || snapshot.Status.RunId is null)
            throw new BridgeException("recording_active", "Stop the recording to export its completed bundle.", 409);
        if (review is not null && review.RunId != snapshot.Status.RunId)
            throw new BridgeException("run_mismatch", "The review belongs to another capture.");
        await exporting.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => WriteBundle(snapshot, review)).ConfigureAwait(false); }
        finally { exporting.Release(); }
    }

    private RunArtifacts WriteBundle(RunSnapshot snapshot, RunReviewResult? review)
    {
        var directory = GetArtifactDirectory(snapshot.Status.RunId!);
        Directory.CreateDirectory(directory);
        var paths = new RunArtifacts(directory, Path.Combine(directory, "run.json"), Path.Combine(directory, "samples.csv"),
            Path.Combine(directory, "events.csv"), Path.Combine(directory, "clearances.csv"),
            Path.Combine(directory, "review.json"));
        // run.json is published last. A failed first export can be retried from the retained capture.
        if (!File.Exists(paths.RunJson) || !File.Exists(paths.SamplesCsv) || !File.Exists(paths.EventsCsv) || !File.Exists(paths.ClearancesCsv))
        {
            Atomic(paths.SamplesCsv, output => WriteSamples(output, snapshot));
            Atomic(paths.EventsCsv, output => WriteEvents(output, snapshot));
            Atomic(paths.ClearancesCsv, output => WriteClearances(output, snapshot));
            Atomic(paths.RunJson, output => JsonSerializer.Serialize(output, snapshot, JsonDefaults.Options));
        }
        if (review is not null || !File.Exists(paths.ReviewJson))
        {
            review ??= Evaluate(snapshot, new(snapshot.Status.RunId!, snapshot.Request.Assertions));
            Atomic(paths.ReviewJson!, output => JsonSerializer.Serialize(output, review, JsonDefaults.Options));
        }
        return paths;
    }

    private static void Atomic(string file, Action<Stream> write)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(output);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static StreamWriter CsvWriter(Stream output) => new(output, new UTF8Encoding(false), 65536, leaveOpen: true);
    private static void WriteSamples(Stream output, RunSnapshot run)
    {
        using var writer = CsvWriter(output);
        var header = new List<object?> { "sample", "elapsedMs", "simulationMs", "utc", "phase", "manualPhase", "liveFrame", "robot", "tool", "workObject" };
        for (var axis = 1; axis <= 6; axis++) header.Add("j" + axis + "Deg");
        foreach (var prefix in new[] { "worldTcp", "baseTcp", "workObjectTcp", "worldFromBase", "worldFromWorkObject" })
            foreach (var suffix in new[] { "Xmm", "Ymm", "Zmm", "Qw", "Qx", "Qy", "Qz" }) header.Add(prefix + suffix);
        PointerHeaders(header);
        Row(writer, header);
        for (var i = 0; i < run.Samples.Length; i++)
        {
            var s = run.Samples[i];
            var values = new List<object?> { i, s.TimeMs, s.SimulationTimeMs, s.Utc, s.Phase, s.ManualPhase, s.LiveFrame,
                run.Status.Robot, run.Request.Tool, run.Request.WorkObject };
            foreach (var axis in s.Joints) values.Add(axis);
            AddFrame(values, s.WorldTcp);
            AddFrame(values, s.BaseTcp);
            AddFrame(values, s.WorkObjectTcp);
            AddFrame(values, s.WorldFromBase);
            AddFrame(values, s.WorldFromWorkObject);
            AddPointer(values, s.ProgramPointer);
            AddPointer(values, s.MotionPointer);
            Row(writer, values);
        }
    }
    private static void WriteEvents(Stream output, RunSnapshot run)
    {
        using var writer = CsvWriter(output);
        var header = new List<object?> { "elapsedMs", "simulationMs", "utc", "kind", "phase", "message", "eventNumber", "sequenceNumber", "sourceUtc" };
        PointerHeaders(header);
        Row(writer, header);
        if (run.Request.Command is JsonElement command)
        {
            var commandRow = new List<object?> { 0, null, run.Status.StartedUtc, "command", run.Request.Phase,
                command.ValueKind == JsonValueKind.String ? command.GetString() : command.GetRawText(), null, null, null };
            AddPointer(commandRow, null);
            AddPointer(commandRow, null);
            Row(writer, commandRow);
        }
        foreach (var e in run.Events)
        {
            var values = new List<object?> { e.TimeMs, e.SimulationTimeMs, e.Utc, e.Kind, e.Phase, e.Message,
                e.EventNumber, e.SequenceNumber, e.SourceUtc };
            AddPointer(values, e.ProgramPointer);
            AddPointer(values, e.MotionPointer);
            Row(writer, values);
        }
    }
    private static void WriteClearances(Stream output, RunSnapshot run)
    {
        using var writer = CsvWriter(output);
        Row(writer, ["sample", "elapsedMs", "simulationMs", "utc", "phase", "manualPhase", "liveFrame", "movingObject", "movingPart",
            "obstacle", "obstaclePart", "distanceMm", "movingXmm", "movingYmm", "movingZmm", "obstacleXmm", "obstacleYmm", "obstacleZmm",
            "contact", "contactEvidence", "error"]);
        for (var i = 0; i < run.Samples.Length; i++)
        {
            var s = run.Samples[i];
            foreach (var c in s.Clearances)
                Row(writer, [i, s.TimeMs, s.SimulationTimeMs, s.Utc, s.Phase, s.ManualPhase, s.LiveFrame, c.MovingObject, c.MovingPart,
                    c.Obstacle, c.ObstaclePart, c.DistanceMm, c.MovingPointWorldMm?.X, c.MovingPointWorldMm?.Y, c.MovingPointWorldMm?.Z,
                    c.ObstaclePointWorldMm?.X, c.ObstaclePointWorldMm?.Y, c.ObstaclePointWorldMm?.Z, c.Contact, c.ContactEvidence, c.Error]);
        }
    }
    private static void AddFrame(List<object?> row, RigidFrame? f) => row.AddRange(
        [f?.Position.X, f?.Position.Y, f?.Position.Z, f?.Orientation.W, f?.Orientation.X, f?.Orientation.Y, f?.Orientation.Z]);
    private static void AddPointer(List<object?> row, RunPointer? p) =>
        row.AddRange([p?.Task, p?.Module, p?.Routine, p?.Line, p?.Column, p?.Instruction]);
    private static void PointerHeaders(List<object?> row)
    {
        foreach (var pointer in new[] { "program", "motion" })
            foreach (var field in new[] { "Task", "Module", "Routine", "Line", "Column", "Instruction" }) row.Add(pointer + field);
    }
    private static void Row(StreamWriter writer, IEnumerable<object?> values) => writer.WriteLine(string.Join(",", values.Select(Cell)));
    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => "", DateTimeOffset time => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture), bool bit => bit ? "true" : "false",
            string s => s, IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "", _ => value.ToString()!
        };
        // Keep free text literal when the CSV is opened by a spreadsheet. JSON preserves the original.
        if (value is string && text.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
