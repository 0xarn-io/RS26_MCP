using System.Globalization;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;
using Events = ABB.Robotics.Controllers.EventLogDomain;
using Io = ABB.Robotics.Controllers.IOSystemDomain;
using Pc = ABB.Robotics.Controllers;
using Rapid = ABB.Robotics.Controllers.RapidDomain;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private readonly RunCaptureStore captureStore = new();
    internal RunCaptureStore CaptureStore => captureStore;
    private RecordingSession? recording;
    private readonly Dictionary<string, Task<RunArtifacts>> runExports = new(StringComparer.Ordinal);

    private sealed record GeometryPair(string MovingName, ProjectObject Moving, string ObstacleName, ProjectObject Obstacle);

    private sealed class RecordingSession(Station station, VirtualTaskContext context, RsToolData tool, RsWorkObject workObject,
        Flange flange, GeometryPair[] pairs, TcpRecordRequest request, SynchronizationContext mainThread)
    {
        internal readonly Station Station = station;
        internal readonly VirtualTaskContext Context = context;
        internal readonly RsToolData Tool = tool;
        internal readonly RsWorkObject WorkObject = workObject;
        internal readonly Flange Flange = flange;
        internal readonly GeometryPair[] Pairs = pairs;
        internal readonly TcpRecordRequest Request = request;
        internal readonly SynchronizationContext MainThread = mainThread;
        internal readonly List<IDisposable> Handles = [];
        internal readonly HashSet<string> Warnings = new(StringComparer.Ordinal);
        internal string Id = "";
        internal Pc.Controller? Controller;
        internal Rapid.Task? RapidTask;
        internal Io.Signal? PhaseSignal, LiveFrameSignal;
        internal EventHandler? Tick, StateChanged, ProjectChanged, Closed;
        internal EventHandler<Events.MessageWrittenEventArgs>? LogMessage;
        internal string? ManualPhase = request.Phase;
        internal string? LastPhase, LastLiveFrame;
        internal RunPointer? LastProgram, LastMotion;
        internal double? Origin, AbsoluteMs;
        internal double NextSampleMs;
        internal int QueuedTick;
        internal bool Acquiring;
        internal double ElapsedMs => Origin.HasValue && AbsoluteMs.HasValue ? Math.Max(0, AbsoluteMs.Value - Origin.Value) : 0;
    }

    private async Task<object> TcpRecordAsync(Station station, TcpRecordRequest request)
    {
        RequireMainThread();
        if (request.Action == "start") return BeginRecording(station, request);
        if (request.Action == "status") return captureStore.Status(request.RunId);
        var id = request.RunId ?? recording?.Id ?? captureStore.Status().RunId
            ?? throw Error("no_recording", "Start a run or provide a recorded runId.");
        var session = recording;
        if (request.Action == "mark")
        {
            if (session?.Id != id) throw Error("recording_stopped", "Markers can only be added to the active run.");
            EnsureCurrent(session.Station);
            session.ManualPhase = request.Phase;
            RecordingEvent(session, "phase_mark", request.Phase!, request.Phase);
            return captureStore.Status(id);
        }
        if (request.Action != "stop") throw Error("invalid_request", "Unknown capture action.");
        if (session?.Id == id)
        {
            ObserveRecordingClockAtEnd(session);
            FinishRecording(session, "explicit_stop", true);
        }
        var status = captureStore.Status(id);
        var export = runExports.TryGetValue(id, out var existing) && !existing.IsFaulted
            ? existing : QueueRunExport(captureStore.Snapshot(id));
        try { return new { status, artifacts = await export }; }
        catch (Exception ex) { throw Error("run_export_failed", "The capture stopped but export failed: " + ex.Message); }
    }

    private object BeginRecording(Station station, TcpRecordRequest request)
    {
        if (recording is not null) throw Error("recording_active", "Stop the active recording before starting another.");
        var context = OwnedVirtualTask(station, request.Robot);
        var tool = Select(Data<RsToolData>(context.Task), request.Tool, "tool");
        var workObject = Select(Data<RsWorkObject>(context.Task), request.WorkObject, "work object");
        ValidateFrames(context.Robot, tool, workObject);
        var flanges = context.Robot.GetFlanges();
        if (flanges.Length != 1) throw Error("unsupported_mechanism", "Recording requires one robot flange.");
        _ = RobotBaseWorld(context.Robot);
        var pairs = RecordingPairs(station, context.Robot, request);
        var mainThread = RobotStudioAPI.SyncContext ?? SynchronizationContext.Current
            ?? throw Error("main_thread_required", "RobotStudio's UI synchronization context is unavailable.");
        var session = new RecordingSession(station, context, tool, workObject, flanges[0], pairs, request, mainThread);
        session.Id = captureStore.Begin(request, station.Name, context.Robot.Name, context.Task.Name,
            station.UniqueId, station.FileInfo?.FullName);
        recording = session;
        try
        {
            // Bind once to RobotStudio's existing station-owned virtual controller. Missing optional
            // signals/pointers are recorded as unavailable; a failed ownership guard is fatal.
            try
            {
                session.Controller = OpenOwnedVirtualConnection(station, context);
                session.Handles.Add(session.Controller);
            }
            catch (BridgeException e) when (e.Code == "virtual_connection_required")
            { WarnRecording(session, "virtual_telemetry", e.Message); }
            if (session.Controller is { } controller)
            {
                session.RapidTask = OpenTelemetryHandle(session, "rapid_task", () => controller.Rapid.GetTask(context.Task.Name));
                session.LogMessage = (_, args) => OnRecordingEvent(session, false, () =>
                {
                    EnsureOwnedVirtualContext(station, context, controller);
                    try
                    {
                        var message = args.Message;
                        var sourceUtc = new DateTimeOffset(message.Timestamp).ToUniversalTime();
                        var simulationTime = session.Origin.HasValue ? Simulator.CurrentTime : (double?)null;
                        var elapsed = simulationTime is double t && session.Origin is double origin
                            ? Math.Max(session.ElapsedMs, t - origin) : session.ElapsedMs;
                        captureStore.AddEvent(session.Id, new(elapsed, DateTimeOffset.UtcNow, "vc_event",
                            message.Title, session.LastPhase, simulationTime, ReadRecordingPointer(session, true),
                            ReadRecordingPointer(session, false), message.Number, args.SequenceNumber, sourceUtc));
                    }
                    catch (Exception ex) { WarnRecording(session, "event_log_read", ex.Message); }
                });
                try { controller.EventLog.MessageWritten += session.LogMessage; }
                catch (Exception ex) { WarnRecording(session, "event_log_unavailable", ex.Message); }
                if (request.PhaseSignal is string phaseName)
                    session.PhaseSignal = OpenTelemetryHandle(session, "phase_signal", () => RequireIoSignal(controller, phaseName));
                if (request.LiveFrameSignal is string frameName)
                    session.LiveFrameSignal = OpenTelemetryHandle(session, "live_frame_signal", () => RequireIoSignal(controller, frameName));
            }
            session.Tick = (_, _) => OnRecordingEvent(session, true, () => SampleRecording(session));
            session.StateChanged = (_, _) => OnRecordingEvent(session, false, () => ObserveRecordingState(session));
            session.ProjectChanged = (_, _) => OnRecordingEvent(session, false, () =>
            {
                if (!ReferenceEquals(Station.ActiveStation, station)) FinishRecording(session, "station_changed", false);
            });
            session.Closed = (_, _) => OnRecordingEvent(session, false, () => FinishRecording(session, "station_closed", false));
            Simulator.Tick += session.Tick;
            Simulator.StateChanged += session.StateChanged;
            Project.ActiveProjectChanged += session.ProjectChanged;
            station.Closed += session.Closed;
            RecordingEvent(session, "capture_armed", "Sampling on RobotStudio simulation ticks.");
            if (Simulator.State == SimulationState.Running) SampleRecording(session);
            return new
            {
                runId = session.Id, status = captureStore.Status(session.Id), armed = !session.Origin.HasValue,
                simulationTimeUnits = "ms", intervalMs = 1000.0 / request.RateHz,
                tool = Qualified(tool), workObject = Qualified(workObject),
                request.PhaseSignal, request.LiveFrameSignal,
                geometryPairs = pairs.Select(p => new { movingObject = p.MovingName, movingId = p.Moving.UniqueId,
                    obstacle = p.ObstacleName, obstacleId = p.Obstacle.UniqueId }).ToArray(),
                scope = "station_owned_virtual_controller", warnings = session.Warnings.ToArray()
            };
        }
        catch
        {
            FinishRecording(session, "start_failed", false);
            throw;
        }
    }

    private static GeometryPair[] RecordingPairs(Station station, Mechanism robot, TcpRecordRequest request)
    {
        if (request.Obstacles.Length == 0 && request.MovingObjects.Length == 0) return [];
        if (request.Obstacles.Length == 0)
            throw Error("missing_obstacles", "Supply at least one obstacle when selecting moving geometry.");
        var moving = (request.MovingObjects.Length == 0 ? new[] { "$robot" } : request.MovingObjects)
            .Select(s => (Name: s, Object: s == "$robot" ? robot : ResolveSceneObject(station, s))).ToArray();
        var obstacles = request.Obstacles.Select(s => (Name: s, Object: ResolveSceneObject(station, s))).ToArray();
        if (moving.Select(v => v.Object.UniqueId).Distinct().Count() != moving.Length ||
            obstacles.Select(v => v.Object.UniqueId).Distinct().Count() != obstacles.Length ||
            moving.Any(m => obstacles.Any(o => o.Object.UniqueId == m.Object.UniqueId)))
            throw Error("invalid_geometry_pair", "Select distinct moving objects and obstacles without overlapping selectors.");
        return (from m in moving from o in obstacles select new GeometryPair(m.Name, m.Object, o.Name, o.Object)).ToArray();
    }

    private T? OpenTelemetryHandle<T>(RecordingSession s, string key, Func<T?> open) where T : class, IDisposable
    {
        try
        {
            var handle = open() ?? throw Error("telemetry_missing", "Requested virtual-controller object is unavailable.");
            s.Handles.Add(handle);
            return handle;
        }
        catch (Exception ex) { WarnRecording(s, key, ex.Message); return null; }
    }

    private void OnRecordingEvent(RecordingSession s, bool tick, Action action)
    {
        // The SDK does not promise a thread for every event. Coalesce off-thread ticks and
        // count missed requested virtual-time slots when the UI next observes the real pose.
        void Run()
        {
            if (tick) Interlocked.Exchange(ref s.QueuedTick, 0);
            if (!ReferenceEquals(recording, s)) return;
            try { action(); }
            catch (Exception ex)
            {
                if (!ReferenceEquals(recording, s)) return;
                WarnRecording(s, "sample_failed", ex.Message);
                FinishRecording(s, "sample_failed", false);
            }
        }
        if (Thread.CurrentThread == RobotStudioAPI.MainThread) Run();
        else if (!tick || Interlocked.Exchange(ref s.QueuedTick, 1) == 0) s.MainThread.Post(_ => Run(), null);
    }

    private void ObserveRecordingState(RecordingSession s)
    {
        var state = Simulator.State;
        if (state == SimulationState.Running) SampleRecording(s);
        else if (state == SimulationState.Shutdown) FinishRecording(s, "simulator_shutdown", false);
        else if (s.Origin.HasValue && (state == SimulationState.Ready || state == SimulationState.Stopped))
        {
            ObserveRecordingClockAtEnd(s);
            FinishRecording(s, "simulation_stopped", true);
        }
    }

    private static void ObserveRecordingClockAtEnd(RecordingSession s)
    {
        // Some RS states reset the simulator clock before delivering StateChanged. Never
        // replace the last observed timestamp by a reset value or invent a terminal pose.
        if (s.Origin.HasValue && ReferenceEquals(Station.ActiveStation, s.Station))
        {
            double now = Simulator.CurrentTime;
            if (double.IsFinite(now) && now >= s.AbsoluteMs) s.AbsoluteMs = now;
        }
    }

    private void SampleRecording(RecordingSession s)
    {
        RequireMainThread();
        if (s.Acquiring || Simulator.State != SimulationState.Running) return;
        s.Acquiring = true;
        try
        {
            EnsureCurrent(s.Station);
            double now = Simulator.CurrentTime;
            if (!double.IsFinite(now)) throw Error("invalid_simulator_clock", "Simulation clock returned a non-finite timestamp.");
            if (now < s.AbsoluteMs) { FinishRecording(s, "simulation_clock_rewound", false); return; }
            s.Origin ??= now;
            s.AbsoluteMs = now;
            double time = s.ElapsedMs;
            const double epsilonMs = 0.000001;
            if (time + epsilonMs < s.NextSampleMs) return;
            var period = 1000.0 / s.Request.RateHz;
            var missed = Math.Floor((time + epsilonMs - s.NextSampleMs) / period);
            if (missed > 0) captureStore.NoteDropped(s.Id, (int)Math.Min(int.MaxValue, missed));
            s.NextSampleMs += (missed + 1) * period;
            var robot = s.Context.Robot;
            if (!ReferenceEquals(robot.Task, s.Context.Task) || !ReferenceEquals(robot.ContainingProject, s.Station) ||
                !ReferenceEquals(s.Tool.ContainingProject, s.Station) || !ReferenceEquals(s.WorkObject.ContainingProject, s.Station))
                throw Error("model_changed", "A selected robot or frame left the recorded station.");
            ValidateFrames(robot, s.Tool, s.WorkObject);
            var baseWorld = ToFrame(RobotBaseWorld(robot));
            var workObjectWorld = ToFrame(s.WorkObject.ObjectFrame.GlobalMatrix);
            var worldTcp = FrameMath.Compose(ToFrame(s.Flange.GlobalMatrix), ToFrame(s.Tool.Frame.Matrix));
            var joints = RapidAxes(robot, robot.GetJointValues());
            var utc = DateTimeOffset.UtcNow;
            if (s.Controller is { } controller) EnsureOwnedVirtualContext(s.Station, s.Context, controller);
            var phase = ReadRecordingSignal(s, s.PhaseSignal, "phase_read") ?? s.ManualPhase;
            var liveFrame = ReadRecordingSignal(s, s.LiveFrameSignal, "live_frame_read");
            var pp = ReadRecordingPointer(s, true);
            var mp = ReadRecordingPointer(s, false);
            var distances = s.Pairs.Select(pair => ObserveClearance(s.Station, pair)).ToArray();
            var sample = new RunSample(time, now, utc, phase, liveFrame, joints, worldTcp,
                FrameMath.RelativeTo(baseWorld, worldTcp), FrameMath.RelativeTo(workObjectWorld, worldTcp),
                pp, mp, distances, baseWorld, workObjectWorld, s.ManualPhase);
            if (!captureStore.AddSample(s.Id, sample))
            { FinishRecording(s, "capture_capacity", false); return; }
            if (s.LastPhase != phase) RecordingEvent(s, "phase_change", phase ?? "unavailable", phase, pp, mp);
            if (s.LastLiveFrame != liveFrame) RecordingEvent(s, "live_frame_change", liveFrame ?? "unavailable", phase, pp, mp);
            if (s.LastProgram != pp || s.LastMotion != mp) RecordingEvent(s, "pointer_change", "Observed RAPID pointer change.", phase, pp, mp);
            s.LastPhase = phase;
            s.LastLiveFrame = liveFrame;
            s.LastProgram = pp;
            s.LastMotion = mp;
        }
        finally { s.Acquiring = false; }
    }

    private string? ReadRecordingSignal(RecordingSession s, Io.Signal? signal, string key)
    {
        if (signal is null) return null;
        try
        {
            var state = signal.State;
            if (state.Quality != Io.SignalQuality.Good)
            {
                WarnRecording(s, key + "_quality", "Signal quality is " + state.Quality + ".");
                return null;
            }
            if (signal is Io.GroupSignal group) return group.GroupValue.ToString(CultureInfo.InvariantCulture);
            return float.IsFinite(state.Value) ? state.Value.ToString("R", CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) { WarnRecording(s, key, ex.Message); return null; }
    }

    private RunPointer? ReadRecordingPointer(RecordingSession s, bool program)
    {
        if (s.RapidTask is null) return null;
        try
        {
            var pointer = program ? s.RapidTask.ProgramPointer : s.RapidTask.MotionPointer;
            if (pointer is null || string.IsNullOrEmpty(pointer.Module)) return null;
            var begin = pointer.Range.Begin;
            return new(s.Context.Task.Name, pointer.Module, pointer.Routine, begin.Row, begin.Column);
        }
        catch (Exception ex) { WarnRecording(s, program ? "program_pointer" : "motion_pointer", ex.Message); return null; }
    }

    private static RunClearance ObserveClearance(Station station, GeometryPair pair)
    {
        var result = new RunClearance(pair.MovingName, pair.ObstacleName, null);
        try
        {
            if (!ReferenceEquals(pair.Moving.ContainingProject, station) || !ReferenceEquals(pair.Obstacle.ContainingProject, station))
                return result with { Error = "A selected geometry object left the recorded station." };
            var distance = CollisionDetector.MinimumDistance(pair.Moving, pair.Obstacle, out var a, out var b,
                out var movingPart, out var obstaclePart, DetectableUsage.Ignore);
            double mm = distance * 1000;
            if (double.IsFinite(mm) && mm >= 0)
                result = result with { DistanceMm = mm, MovingPointWorldMm = RecordingPoint(a), ObstaclePointWorldMm = RecordingPoint(b),
                    MovingPart = movingPart?.Name, ObstaclePart = obstaclePart?.Name };
            else result = result with { Error = "RobotStudio could not resolve the closest graphical points." };
            if (pair.Moving is GraphicComponent moving && pair.Obstacle is GraphicComponent obstacle)
            {
                var type = CollisionDetector.CheckCollision(moving, obstacle, 0, DetectableUsage.Ignore, false, false,
                    out var contactMoving, out var contactObstacle);
                return result with { Contact = type == CollisionType.Collision, ContactEvidence = "sdk_component_collision",
                    MovingPart = result.MovingPart ?? contactMoving?.Name, ObstaclePart = result.ObstaclePart ?? contactObstacle?.Name };
            }
            return result with { Contact = result.DistanceMm.HasValue ? result.DistanceMm == 0 : null,
                ContactEvidence = "body_zero_graphical_distance" };
        }
        catch (Exception ex) { return result with { Error = ex.Message }; }
    }

    private static Vector3D? RecordingPoint(ABB.Robotics.Math.Vector3 point)
    {
        var result = new Vector3D(point.x * 1000, point.y * 1000, point.z * 1000);
        return double.IsFinite(result.X) && double.IsFinite(result.Y) && double.IsFinite(result.Z) ? result : null;
    }

    private void RecordingEvent(RecordingSession s, string kind, string message, string? phase = null,
        RunPointer? program = null, RunPointer? motion = null) =>
        captureStore.AddEvent(s.Id, new(s.ElapsedMs, DateTimeOffset.UtcNow, kind, message, phase ?? s.LastPhase,
            s.AbsoluteMs, program ?? s.LastProgram, motion ?? s.LastMotion));

    private void WarnRecording(RecordingSession s, string key, string message)
    {
        if (s.Warnings.Add(key)) RecordingEvent(s, "telemetry_warning", key + ": " + message);
    }

    private void FinishRecording(RecordingSession s, string reason, bool normal)
    {
        if (!ReferenceEquals(recording, s)) return;
        recording = null;
        // Release all subscriptions even if one SDK wrapper is already invalid at station close.
        void Release(Action action)
        {
            try { action(); }
            catch (Exception ex) { WarnRecording(s, "cleanup", ex.Message); }
        }
        Release(() => Simulator.Tick -= s.Tick);
        Release(() => Simulator.StateChanged -= s.StateChanged);
        Release(() => Project.ActiveProjectChanged -= s.ProjectChanged);
        Release(() => s.Station.Closed -= s.Closed);
        if (s.Controller is { } controller && s.LogMessage != null)
            Release(() => controller.EventLog.MessageWritten -= s.LogMessage);
        for (int i = s.Handles.Count - 1; i >= 0; i--) Release(s.Handles[i].Dispose);
        RecordingEvent(s, "capture_stopped", reason);
        QueueRunExport(captureStore.Stop(s.Id, reason, s.ElapsedMs, normal));
    }

    private Task<RunArtifacts> QueueRunExport(RunSnapshot snapshot)
    {
        // Only a detached Core snapshot crosses threads. All SDK handles have been released.
        foreach (var old in runExports.Where(e => e.Value.IsCompleted).Select(e => e.Key).Take(Math.Max(0, runExports.Count - 7)).ToArray())
            runExports.Remove(old);
        var export = Task.Run(() => captureStore.ExportAsync(snapshot));
        runExports[snapshot.Status.RunId!] = export;
        // Keep an export failure observable on explicit stop while also observing background faults.
        _ = export.ContinueWith(task => System.Diagnostics.Trace.TraceError("Run export failed: " + task.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return export;
    }

    public void StopRecordingForShutdown()
    {
        RequireMainThread();
        if (recording is { } session) FinishRecording(session, "bridge_shutdown", false);
        // Background exports are pure disk I/O and never dispatch back to RobotStudio.
        Task.WhenAll(runExports.Values).GetAwaiter().GetResult();
    }
}
