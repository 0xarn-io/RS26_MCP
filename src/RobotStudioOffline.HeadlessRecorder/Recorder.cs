using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using RobotStudioOffline.Core;
using Io = ABB.Robotics.Controllers.IOSystemDomain;
using Motion = ABB.Robotics.Controllers.MotionDomain;
using Pc = ABB.Robotics.Controllers;
using Rapid = ABB.Robotics.Controllers.RapidDomain;

namespace RobotStudioOffline.HeadlessRecorder;

internal static class Recorder
{
    internal static int Capture(Options options, CloneOwnership owner)
    {
        var request = options.Request;
        owner.Check();
        using var controller = Pc.Controller.Connect(owner.ExpectedId, Pc.ConnectionType.Standalone, validateServerCertificate: true);
        void Check()
        {
            owner.Check();
            if (!controller.Connected || !controller.IsVirtual || controller.SystemId != owner.ExpectedId || controller.SystemName != owner.SystemName ||
                !IPAddress.IsLoopback(controller.IPAddress))
                throw new InvalidOperationException("The SDK connection does not match the proven local virtual clone.");
        }
        Check();
        controller.Logon(Pc.UserInfo.DefaultUser);
        Check();
        using var motion = controller.MotionSystem;
        var units = motion.MechanicalUnits.Cast<Motion.MechanicalUnit>().ToArray();
        try
        {
            var unit = units.SingleOrDefault(item => item.Name == request.Robot)
                ?? throw new ArgumentException("The clone has no mechanical unit " + request.Robot);
            using var task = unit.Task;
            if (task.Name != options.TaskName || !task.Motion || unit.Type != Motion.MechanicalUnitType.TcpRobot)
                throw new InvalidOperationException("The selected unit is not a TCP robot assigned to the selected motion task.");
            using var requestedTool = new Motion.Tool(unit, request.Tool!);
            _ = ToolFrame(requestedTool);
            _ = task.GetRobTarget(request.Tool!, request.WorkObject!);
            return Sample(options, owner, controller, unit, task, requestedTool, Check);
        }
        finally { foreach (var item in units) item.Dispose(); }
    }

    private static int Sample(Options options, CloneOwnership owner, Pc.Controller controller, Motion.MechanicalUnit unit,
        Rapid.Task task, Motion.Tool requestedTool, Action verify)
    {
        var store = new RunCaptureStore();
        var req = options.Request;
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += interrupt;
        var clock = Stopwatch.StartNew();
        var id = store.Begin(req, owner.SystemName + " (headless VC)", unit.Name, task.Name, "headless:" + owner.ExpectedId);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        void Event(string kind, string message) => store.AddEvent(id, new(clock.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow, kind, message));
        void Warn(string key, string message)
        {
            if (warnings.Add(key)) Event("telemetry_unavailable", key + ": " + message);
        }
        Io.Signal? OpenSignal(string? name)
        {
            if (name is null) return null;
            try { return controller.IOSystem.GetSignal(name) ?? throw new InvalidOperationException("Signal not found."); }
            catch (Exception ex) { Warn(name, ex.Message); return null; }
        }
        string? ReadSignal(Io.Signal? signal, string? name)
        {
            if (signal is null || name is null) return null;
            try
            {
                var state = signal.State;
                if (state.Quality != Io.SignalQuality.Good) { Warn(name, "Quality " + state.Quality); return null; }
                return signal is Io.GroupSignal group ? group.GroupValue.ToString(CultureInfo.InvariantCulture)
                    : state.Value.ToString("R", CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { Warn(name, ex.Message); return null; }
        }
        RunPointer? Pointer(bool program)
        {
            try
            {
                var position = program ? task.ProgramPointer : task.MotionPointer;
                if (position is null || string.IsNullOrEmpty(position.Module)) return null;
                var begin = position.Range.Begin;
                return new(task.Name, position.Module, position.Routine, begin.Row, begin.Column);
            }
            catch (Exception ex) { Warn(program ? "program_pointer" : "motion_pointer", ex.Message); return null; }
        }
        var reason = "duration";
        var success = true;
        string? lastPhase = null;
        RunPointer? lastProgram = null, lastMotion = null;
        NativeEventBuffer? nativeEvents = null;
        try
        {
            using var phase = OpenSignal(req.PhaseSignal);
            using var liveFrame = OpenSignal(req.LiveFrameSignal);
            try { nativeEvents = new(controller.EventLog, clock); }
            catch (Exception ex) { Warn("vc_event_log", ex.Message); }
            Event("source", "Headless VC " + owner.ExpectedId + " host generation " + owner.Generation +
                $". Active axes {unit.NumberOfAxes}; joints are all six native RAPID robax slots. Times are wall-clock. World means controller world in wobj0. " +
                "Frame/joint/signal/pointer reads are sequential within a sample. Event pointers are nearest delivery observations. No station geometry or simulator clock.");
            Console.WriteLine(JsonSerializer.Serialize(new { runId = id, state = "recording", rateHz = req.RateHz }, JsonDefaults.Options));
            var period = 1000.0 / req.RateHz;
            var limitMs = options.Seconds * 1000.0;
            int count = 0;
            long nextSlot = 0;
            // Begin the acquisition clock after optional signal lookup. Elapsed sample and event
            // timestamps share the original stopwatch, including its short setup interval.
            var start = clock.Elapsed.TotalMilliseconds;
            string? previousPhase = null, previousFrame = null;
            while (count < req.MaxSamples && !cancel.IsCancellationRequested)
            {
                var now = clock.Elapsed.TotalMilliseconds - start;
                if (now >= limitMs) break;
                var remaining = nextSlot * period - now;
                if (remaining > 0)
                {
                    if (remaining > 2) cancel.Token.WaitHandle.WaitOne(Math.Max(1, (int)(remaining - 1)));
                    else Thread.SpinWait(100);
                    continue;
                }
                var skipped = Program.MissedSlots(now, nextSlot, period);
                if (skipped > 0) store.NoteDropped(id, skipped);
                nextSlot += skipped + 1;
                verify();
                // MechanicalUnit caches this SDK property. Its owner is the unit.
                var joggingTool = unit.Tool;
                var active = joggingTool.Name;
                var same = active.Equals(req.Tool, StringComparison.OrdinalIgnoreCase);
                var offset = same ? RigidFrame.Identity : FrameMath.RelativeTo(ToolFrame(joggingTool), ToolFrame(requestedTool));
                var basis = FrameMath.Compose(Frame(unit.GetPosition(Motion.CoordinateSystemType.Base)), offset);
                var world = Frame(task.GetRobTarget(req.Tool!, "wobj0"));
                var work = req.WorkObject!.Equals("wobj0", StringComparison.OrdinalIgnoreCase) ? world
                    : Frame(task.GetRobTarget(req.Tool!, req.WorkObject!));
                var axes = JointSlots(task.GetJointTarget().RobAx);
                var current = unit.Tool;
                if (!active.Equals(current.Name, StringComparison.OrdinalIgnoreCase))
                {
                    store.NoteDropped(id);
                    Warn("tool_changed", "Jogging tool changed during acquisition; affected sample omitted.");
                    continue;
                }
                var phaseValue = ReadSignal(phase, req.PhaseSignal);
                var frameValue = ReadSignal(liveFrame, req.LiveFrameSignal);
                var pp = Pointer(true);
                var mp = Pointer(false);
                lastPhase = phaseValue; lastProgram = pp; lastMotion = mp;
                var observed = clock.Elapsed.TotalMilliseconds;
                var utc = DateTimeOffset.UtcNow;
                var sample = new RunSample(observed, null, utc, phaseValue, frameValue,
                    axes, world, basis, work, pp, mp, []);
                if (!store.AddSample(id, sample)) { reason = "sample_limit"; break; }
                count++;
                nativeEvents?.Drain(store, id, lastPhase, lastProgram, lastMotion);
                if (previousPhase != phaseValue || previousFrame != frameValue)
                {
                    store.AddEvent(id, new(observed, utc, "phase_frame", $"Phase {phaseValue}, live frame {frameValue}.", phaseValue,
                        ProgramPointer: pp, MotionPointer: mp));
                    previousPhase = phaseValue;
                    previousFrame = frameValue;
                }
            }
            if (cancel.IsCancellationRequested) reason = "explicit_stop";
            else if (count >= req.MaxSamples) reason = "sample_limit";
            else
            {
                int expectedSlots = checked(options.Seconds * req.RateHz);
                if (nextSlot < expectedSlots) store.NoteDropped(id, expectedSlots - (int)nextSlot);
            }
        }
        catch (Exception ex)
        {
            reason = "acquisition_failed";
            success = false;
            Event("acquisition_failed", ex.Message);
            Console.Error.WriteLine(ex.Message);
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
            if (nativeEvents is not null)
            {
                try { nativeEvents.Stop(); }
                catch (Exception ex) { Warn("event_log_detach", ex.Message); }
                nativeEvents.Drain(store, id, lastPhase, lastProgram, lastMotion);
            }
        }
        var snapshot = store.Stop(id, reason, clock.Elapsed.TotalMilliseconds, success);
        var artifacts = store.ExportAsync(snapshot).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new { status = snapshot.Status, artifacts }, JsonDefaults.Options));
        return success ? 0 : 1;
    }

    private static RigidFrame ToolFrame(Motion.Tool tool)
    {
        if (tool.Name.Equals("tool0", StringComparison.OrdinalIgnoreCase)) return RigidFrame.Identity;
        if (tool.Data is not Rapid.ToolData data || !data.Robhold)
            throw new InvalidOperationException("Select an accessible robot-held tool. Unable to resolve " + tool.Name);
        return Frame(data.Tframe.Trans, data.Tframe.Rot);
    }
    private static RigidFrame Frame(Rapid.RobTarget value) => Frame(value.Trans, value.Rot);
    private static RigidFrame Frame(Rapid.Pos p, Rapid.Orient q) =>
        new(new(p.X, p.Y, p.Z), FrameMath.Normalize(new(q.Q1, q.Q2, q.Q3, q.Q4)));

    private static double[] JointSlots(Rapid.RobJoint native) =>
        [native.Rax_1, native.Rax_2, native.Rax_3, native.Rax_4, native.Rax_5, native.Rax_6];

    internal static void CheckFourAxisLayout()
    {
        // Four physical palletizer axes occupy RAPID slots 1,2,3,6. Preserve even
        // the native inactive values in 4 and 5; never compact/re-index the SDK data.
        var axes = new Rapid.RobJoint { Rax_1 = 12, Rax_2 = 23, Rax_3 = 34, Rax_4 = 0, Rax_5 = 0, Rax_6 = 56 };
        if (!JointSlots(axes).SequenceEqual(new double[] { 12, 23, 34, 0, 0, 56 }))
            throw new InvalidOperationException("The four-axis RAPID joint layout changed.");
    }
}
