using System.Text.Json;

namespace RobotStudioOffline.Core;

public sealed record RunPointer(string? Task, string? Module, string? Routine, int? Line, int? Column = null, string? Instruction = null);
public sealed record RunClearance(string MovingObject, string Obstacle, double? DistanceMm,
    Vector3D? MovingPointWorldMm = null, Vector3D? ObstaclePointWorldMm = null, string? Error = null,
    bool? Contact = null, string? ContactEvidence = null, string? MovingPart = null, string? ObstaclePart = null);
public sealed record RunSample(double TimeMs, double? SimulationTimeMs, DateTimeOffset Utc, string? Phase, string? LiveFrame,
    double[] Joints, RigidFrame WorldTcp, RigidFrame BaseTcp, RigidFrame WorkObjectTcp,
    RunPointer? ProgramPointer, RunPointer? MotionPointer, RunClearance[] Clearances,
    RigidFrame? WorldFromBase = null, RigidFrame? WorldFromWorkObject = null, string? ManualPhase = null);
public sealed record RunEvent(double TimeMs, DateTimeOffset Utc, string Kind, string Message, string? Phase = null,
    double? SimulationTimeMs = null, RunPointer? ProgramPointer = null, RunPointer? MotionPointer = null,
    int? EventNumber = null, int? SequenceNumber = null, DateTimeOffset? SourceUtc = null);
public sealed record RunStatus(string? RunId, string State, string? Station, string? Robot, string? Task,
    DateTimeOffset? StartedUtc, DateTimeOffset? EndedUtc, string? StopReason, int RateHz, int MaxSamples,
    int SampleCount, long DroppedSamples, int EventCount, long DroppedEvents, double DurationMs,
    double MaxGapMs, double? ObservedRateHz, bool CompletedNormally, bool CoverageComplete,
    string? StationId = null, string? StationFile = null);
public sealed record RunSnapshot(RunStatus Status, TcpRecordRequest Request, RunSample[] Samples, RunEvent[] Events)
{
    public string Schema => "robotstudio.capture/1";
    public string Units => "TCP coordinates and distances in mm, joints in RAPID degrees, quaternion WXYZ, elapsed and simulator times in ms.";
}
public sealed record RunArtifacts(string Directory, string RunJson, string SamplesCsv, string EventsCsv,
    string ClearancesCsv, string? ReviewJson);

/// <summary>One active capture. Completed captures can be reopened by ID from their exported bundles.</summary>
public sealed partial class RunCaptureStore
{
    private const int RetainedRuns = 4;
    private const int MaxEvents = 4096;
    private const int MaxDistanceObservations = 250000;
    private readonly object gate = new();
    private readonly List<Entry> runs = [];
    private readonly string artifactRoot;

    public RunCaptureStore(string? artifactRoot = null)
    {
        this.artifactRoot = Path.GetFullPath(artifactRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobotStudioOfflineBridge", "runs"));
    }

    public string Begin(TcpRecordRequest request, string station, string robot, string task, string? stationId = null, string? stationFile = null)
    {
        if (request.Action != "start" || request.RateHz is < 20 or > 50 || request.MaxSamples is < 1 or > 36000)
            throw new BridgeException("invalid_request", "Expected a validated recording start request.");
        lock (gate)
        {
            if (runs.Any(run => run.Ended is null))
                throw new BridgeException("recording_active", "Stop the active recording before starting another.", 409);
            if (runs.Count == RetainedRuns) runs.RemoveAt(0);
            var id = Guid.NewGuid().ToString("N");
            runs.Add(new(id, Copy(request), station, robot, task, stationId, stationFile));
            return id;
        }
    }

    public bool AddSample(string runId, RunSample sample)
    {
        lock (gate)
        {
            var run = Active(runId);
            if (run.Samples.Count >= run.Request.MaxSamples || sample.Clearances.Length > 64 ||
                run.DistanceCount + sample.Clearances.Length > MaxDistanceObservations)
            {
                run.DroppedSamples++;
                return false;
            }
            var previous = run.Samples.Count == 0 ? 0 : run.Samples[^1].TimeMs;
            if (!ValidSample(sample, previous))
            {
                run.DroppedSamples++;
                throw new BridgeException("invalid_sample", "A sample has invalid coordinates, joints, timing, or distance.", 409);
            }
            run.MaxGapMs = Math.Max(run.MaxGapMs, sample.TimeMs - previous);
            run.DistanceCount += sample.Clearances.Length;
            run.Samples.Add(Copy(sample));
            return true;
        }
    }

    public void AddEvent(string runId, RunEvent entry)
    {
        if (!double.IsFinite(entry.TimeMs) || entry.TimeMs < 0 ||
            (entry.SimulationTimeMs is double sim && !double.IsFinite(sim)))
            throw new BridgeException("invalid_event", "Event timestamps must be finite.");
        lock (gate)
        {
            var run = Active(runId);
            if (run.Events.Count == MaxEvents) { run.DroppedEvents++; return; }
            run.Events.Add(entry);
        }
    }

    public void NoteDropped(string runId, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate) Active(runId).DroppedSamples += count;
    }

    public void NoteDroppedEvents(string runId, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate) Active(runId).DroppedEvents += count;
    }

    public RunSnapshot Stop(string runId, string reason, double? durationMs = null, bool completedNormally = true)
    {
        if (durationMs is double duration && (!double.IsFinite(duration) || duration < 0))
            throw new BridgeException("invalid_duration", "Run duration must be finite and nonnegative.");
        lock (gate)
        {
            var run = Find(runId);
            if (run.Ended is null)
            {
                var last = run.Samples.Count == 0 ? 0 : run.Samples[^1].TimeMs;
                run.DurationMs = Math.Max(last, durationMs ?? last);
                run.MaxGapMs = Math.Max(run.MaxGapMs, run.DurationMs - last);
                run.Ended = DateTimeOffset.UtcNow;
                run.Reason = reason;
                run.CompletedNormally = completedNormally;
            }
            return CopySnapshot(run);
        }
    }

    public RunStatus Status(string? runId = null)
    {
        lock (gate)
        {
            if (runId is null) return runs.Count == 0
                ? new(null, "idle", null, null, null, null, null, null, 0, 0, 0, 0, 0, 0, 0, 0, null, false, false)
                : Describe(runs[^1]);
            var id = RunRequests.ValidateId(runId);
            var current = runs.Find(run => run.Id == id);
            if (current is not null) return Describe(current);
        }
        return Snapshot(runId).Status;
    }

    public RunSnapshot Snapshot(string runId)
    {
        var id = RunRequests.ValidateId(runId);
        lock (gate)
        {
            var current = runs.Find(run => run.Id == id);
            if (current is not null) return CopySnapshot(current);
        }
        var path = Path.Combine(artifactRoot, id, "run.json");
        try
        {
            using var input = File.OpenRead(path);
            var saved = JsonSerializer.Deserialize<RunSnapshot>(input, JsonDefaults.Options);
            if (saved is null || !ValidSavedCapture(saved, id))
                throw new BridgeException("invalid_run_artifact", "Run artifact has inconsistent metadata.", 409);
            return saved;
        }
        catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { throw new BridgeException("run_not_found", "No captured or exported run matches this runId.", 404); }
        catch (JsonException) { throw new BridgeException("invalid_run_artifact", "The saved capture is invalid.", 409); }
    }

    private Entry Find(string runId)
    {
        var id = RunRequests.ValidateId(runId);
        return runs.Find(run => run.Id == id) ?? throw new BridgeException("run_not_found", "Run is not in the recent captures.", 404);
    }
    private Entry Active(string runId)
    {
        var run = Find(runId);
        if (run.Ended is not null) throw new BridgeException("recording_stopped", "The recording has stopped.", 409);
        return run;
    }
    private static RunStatus Describe(Entry run)
    {
        var n = run.Samples.Count;
        var duration = run.Ended is null ? (n > 0 ? run.Samples[^1].TimeMs : 0) : run.DurationMs;
        var span = n < 2 ? 0 : run.Samples[^1].TimeMs - run.Samples[0].TimeMs;
        return new(run.Id, run.Ended is null ? "recording" : "stopped", run.Station, run.Robot, run.Task, run.Started, run.Ended,
            run.Reason, run.Request.RateHz, run.Request.MaxSamples, n, run.DroppedSamples, run.Events.Count, run.DroppedEvents,
            duration, run.MaxGapMs, span > 0 ? 1000 * (n - 1) / span : null, run.CompletedNormally,
            run.CompletedNormally && n >= 2 && span > 0 && run.DroppedSamples == 0 && run.DroppedEvents == 0 &&
            run.MaxGapMs <= 2000.0 / run.Request.RateHz, run.StationId, run.StationFile);
    }
    private static RunSnapshot CopySnapshot(Entry r) =>
        new(Describe(r), Copy(r.Request), r.Samples.Select(Copy).ToArray(), r.Events.ToArray());
    private static RunSample Copy(RunSample s) => s with { Joints = (double[])s.Joints.Clone(), Clearances = (RunClearance[])s.Clearances.Clone() };
    private static TcpRecordRequest Copy(TcpRecordRequest r) => r with
    {
        MovingObjects = (string[])r.MovingObjects.Clone(), Obstacles = (string[])r.Obstacles.Clone(), Command = r.Command?.Clone(),
        Assertions = r.Assertions.Select(a => a with { Min = a.Min?.ToArray(), Max = a.Max?.ToArray(), Position = a.Position?.ToArray() }).ToArray()
    };
    private static bool Finite(Vector3D p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static bool ValidSample(RunSample s, double previous) =>
        s is not null && double.IsFinite(s.TimeMs) && s.TimeMs >= previous &&
        (s.SimulationTimeMs is not double sim || double.IsFinite(sim)) &&
        s.Joints is { Length: 6 } && s.Joints.All(double.IsFinite) &&
        Valid(s.WorldTcp) && Valid(s.BaseTcp) && Valid(s.WorkObjectTcp) &&
        (s.WorldFromBase is not RigidFrame b || Valid(b)) && (s.WorldFromWorkObject is not RigidFrame w || Valid(w)) &&
        s.Clearances is { Length: <= 64 } && s.Clearances.All(c => c is not null &&
            (c.DistanceMm is not double d || double.IsFinite(d)) &&
            (c.MovingPointWorldMm is not Vector3D p || Finite(p)) && (c.ObstaclePointWorldMm is not Vector3D q || Finite(q)));

    private static bool ValidSavedCapture(RunSnapshot run, string id)
    {
        var status = run.Status;
        if (status is null || run.Request is null || run.Samples is null || run.Events is null ||
            status.RunId != id || status.State != "stopped" || status.StartedUtc is null || status.EndedUtc is null ||
            status.RateHz is < 20 or > 50 || status.MaxSamples is < 1 or > 36000 ||
            status.RateHz != run.Request.RateHz || status.MaxSamples != run.Request.MaxSamples ||
            run.Samples.Length > status.MaxSamples || status.SampleCount != run.Samples.Length ||
            run.Events.Length > MaxEvents || status.EventCount != run.Events.Length || status.DroppedEvents < 0 || status.DroppedSamples < 0 ||
            !double.IsFinite(status.DurationMs) || status.DurationMs < 0 || run.Request.Action != "start" ||
            run.Request.MovingObjects is null || run.Request.Obstacles is null || run.Request.Assertions is null)
            return false;
        double previous = 0, maxGap = 0;
        var distances = 0;
        foreach (var sample in run.Samples)
        {
            if (!ValidSample(sample, previous)) return false;
            distances += sample.Clearances.Length;
            maxGap = Math.Max(maxGap, sample.TimeMs - previous);
            previous = sample.TimeMs;
        }
        if (status.DurationMs < previous || distances > MaxDistanceObservations) return false;
        maxGap = Math.Max(maxGap, status.DurationMs - previous);
        var complete = status.CompletedNormally && run.Samples.Length >= 2 && previous > run.Samples[0].TimeMs &&
            status.DroppedSamples == 0 && status.DroppedEvents == 0 && maxGap <= 2000.0 / status.RateHz;
        return maxGap == status.MaxGapMs && complete == status.CoverageComplete &&
            run.Events.All(e => e is not null && double.IsFinite(e.TimeMs) && e.TimeMs >= 0 &&
                (e.SimulationTimeMs is not double sim || double.IsFinite(sim)));
    }
    private static bool Valid(RigidFrame f)
    {
        var q = f.Orientation;
        var normSquared = q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z;
        return Finite(f.Position) && double.IsFinite(normSquared) && Math.Abs(normSquared - 1) <= 2.1e-6;
    }

    private sealed class Entry(string id, TcpRecordRequest request, string station, string robot, string task, string? stationId, string? stationFile)
    {
        public readonly string Id = id, Station = station, Robot = robot, Task = task;
        public readonly string? StationId = stationId, StationFile = stationFile;
        public readonly TcpRecordRequest Request = request;
        public readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
        public readonly List<RunSample> Samples = [];
        public readonly List<RunEvent> Events = [];
        public DateTimeOffset? Ended;
        public string? Reason;
        public double DurationMs, MaxGapMs;
        public long DroppedSamples, DroppedEvents;
        public int DistanceCount;
        public bool CompletedNormally;
    }
}
