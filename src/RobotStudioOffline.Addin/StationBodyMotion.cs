using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private MotionRun? motionRun;

    private sealed class MotionRun(Station station, SimulationConfiguration configuration, SynchronizationContext main)
    {
        internal readonly Station Station = station;
        internal readonly SimulationConfiguration Configuration = configuration;
        internal readonly SynchronizationContext Main = main;
        internal readonly Dictionary<string, Travel> Parts = new(StringComparer.Ordinal);
        internal EventHandler? Tick, StateChanged, Closed, ProjectChanged;
        internal int QueuedTick;
        internal bool Updating;
        internal double? LastClock;
    }

    private sealed class Travel(Part part, BodyMotionRequest request)
    {
        internal readonly Part Part = part;
        internal readonly string Id = part.UniqueId;
        internal readonly string Name = part.Name;
        internal readonly ProjectObject Parent = part.Parent;
        internal readonly BodyMotionRequest Request = request;
        internal Matrix4 Home = part.Transform.Matrix;
        internal Matrix4 SegmentStart = part.Transform.Matrix;
        internal Matrix4 Expected = part.Transform.Matrix;
        internal double? OriginMs;
        internal double Fraction;
        internal bool OwnsPose;
        internal string State = "armed";
        internal string? Reason;
        internal bool Active => State is "armed" or "moving";

        internal object Summary() => new
        {
            part = Name, id = Id, state = State, reason = Reason, fraction = Fraction,
            deltaMm = Request.DeltaMm.ToArray(), Request.DurationMs,
            elapsedMotionMs = Request.DurationMs * Fraction
        };
    }

    private object BodyMotion(Station station, BodyMotionRequest request)
    {
        RequireMainThread();
        var run = motionRun;
        if (run != null && !ReferenceEquals(station, run.Station))
        {
            EndBodyMotionRun(run, restore: false);
            run = null;
        }
        var part = request.Part == null ? null : (Part)ResolveSceneObject(station, request.Part, typeof(Part));
        Travel? prior = null;
        if (part != null && run != null) run.Parts.TryGetValue(part.UniqueId, out prior);
        if (request.Action == "status")
        {
            var selected = part == null ? run?.Parts.Values.ToArray() ?? [] : prior == null ? [] : new[] { prior };
            return new { station = station.Name, simulator = Simulator.State.ToString(), motions = selected.Select(t => t.Summary()).ToArray() };
        }
        if (request.Action == "stop")
        {
            if (prior == null) throw Error("motion_missing", "This part has no bridge motion in the current run.");
            if (prior.Active) prior.State = "stopped";
            return prior.Summary();
        }

        var configuration = Simulator.ActiveConfiguration;
        if (configuration == null || !ReferenceEquals(configuration.ContainingProject, station))
            throw Error("simulation_configuration", "Select a simulation configuration in this station.");
        if (Simulator.State is SimulationState.Shutdown or SimulationState.Init)
            throw Error("simulation_transition", "Wait for the simulator to finish its current transition.");
        if (run != null && !ReferenceEquals(configuration, run.Configuration))
        {
            EndBodyMotionRun(run, restore: true);
            run = null;
            prior = null;
        }
        if (prior?.Active == true) throw Error("motion_active", "Stop this part's current motion before starting another.");
        if (prior == null && run?.Parts.Count >= 64) throw Error("motion_capacity", "A run can track at most 64 parts.");
        if (!EligibleMotionPart(station, part!))
            throw Error("motion_part", "Select an unattached station Part with None or Kinematic motion control, outside a mechanism or smart component.");
        var travel = new Travel(part!, request);
        if (prior?.OwnsPose == true && StillOwns(station, prior))
        {
            travel.Home = prior.Home;
            travel.OwnsPose = true;
        }
        run ??= WatchBodyMotions(station, configuration);
        run.Parts[travel.Id] = travel;
        if (Simulator.State is SimulationState.Running or SimulationState.Paused)
            AdvanceBodyMotions(run);
        return travel.Summary();
    }

    private static bool EligibleMotionPart(Station station, Part part)
    {
        if (!ReferenceEquals(part.ContainingProject, station) || station.ReadOnly ||
            part.PhysicsMotionControl is not (PhysicsMotionControl.None or PhysicsMotionControl.Kinematic)) return false;
        _ = EditablePartOwner(station, part);
        // A part inside an attached group moves with the group's owner as well.
        for (ProjectObject? item = part; item is GraphicComponent; item = item.Parent)
            if (CurrentBodyAttachment(station, item) != null) return false;
        return true;
    }

    private static bool StillOwns(Station station, Travel travel) =>
        ReferenceEquals(travel.Part.Parent, travel.Parent) && EligibleMotionPart(station, travel.Part) &&
        travel.Part.Transform.Matrix.AlmostEquals(travel.Expected, 1e-9);

    private MotionRun WatchBodyMotions(Station station, SimulationConfiguration configuration)
    {
        var main = SynchronizationContext.Current ?? RobotStudioAPI.SyncContext
            ?? throw Error("motion_context", "The RobotStudio UI synchronization context is unavailable.");
        var run = new MotionRun(station, configuration, main);
        void Dispatch(bool tick, Action action)
        {
            void Execute()
            {
                if (tick) Interlocked.Exchange(ref run.QueuedTick, 0);
                if (!ReferenceEquals(motionRun, run) || run.Updating) return;
                run.Updating = true;
                try { action(); }
                catch (Exception exception)
                {
                    Logger.AddMessage(new LogMessage("Offline bridge body motion stopped: " + exception.Message));
                    EndBodyMotionRun(run, restore: true);
                }
                finally { run.Updating = false; }
            }
            if (Thread.CurrentThread == RobotStudioAPI.MainThread) Execute();
            else if (!tick || Interlocked.Exchange(ref run.QueuedTick, 1) == 0) run.Main.Post(_ => Execute(), null);
        }
        run.Tick = (_, _) => Dispatch(true, () => AdvanceBodyMotions(run));
        run.StateChanged = (_, _) => Dispatch(false, () => AdvanceBodyMotions(run));
        run.Closed = (_, _) => Dispatch(false, () => EndBodyMotionRun(run, restore: false));
        run.ProjectChanged = (_, _) => Dispatch(false, () =>
        {
            if (!ReferenceEquals(Station.ActiveStation, station)) EndBodyMotionRun(run, restore: false);
        });
        motionRun = run;
        try
        {
            Simulator.Tick += run.Tick;
            Simulator.StateChanged += run.StateChanged;
            station.Closed += run.Closed;
            Project.ActiveProjectChanged += run.ProjectChanged;
            return run;
        }
        catch { EndBodyMotionRun(run, restore: false); throw; }
    }

    private void AdvanceBodyMotions(MotionRun run)
    {
        if (!ReferenceEquals(run.Station, Station.ActiveStation) || !ReferenceEquals(run.Configuration, Simulator.ActiveConfiguration))
        {
            EndBodyMotionRun(run, restore: ReferenceEquals(run.Station, Station.ActiveStation));
            return;
        }
        var state = Simulator.State;
        if (state is not (SimulationState.Running or SimulationState.Paused))
        {
            if (state == SimulationState.Shutdown || (run.LastClock.HasValue && state is SimulationState.Stopped or SimulationState.Ready))
                EndBodyMotionRun(run, restore: true);
            return;
        }
        var now = Simulator.CurrentTime;
        if (!double.IsFinite(now) || now < run.LastClock) { EndBodyMotionRun(run, restore: true); return; }
        run.LastClock = now;
        foreach (var t in run.Parts.Values)
        {
            if (!t.Active && !t.OwnsPose) continue;
            try
            {
                if (t.OriginMs == null && t.Active)
                {
                    // StartAsync can load a saved state before the first tick. Capture the post-load pose.
                    if (!ReferenceEquals(t.Part.Parent, t.Parent) || !EligibleMotionPart(run.Station, t.Part))
                        throw Error("motion_ownership", "The part was removed, attached or its physics ownership changed.");
                    t.SegmentStart = t.Expected = t.Part.Transform.Matrix;
                    if (!t.OwnsPose) t.Home = t.SegmentStart;
                    t.OriginMs = now;
                    t.State = "moving";
                }
                if (!StillOwns(run.Station, t)) throw Error("motion_ownership", "Another edit, attachment or physics operation took ownership of the part.");
                if (!t.Active) continue;
                var fraction = t.Request.TravelFraction(now - t.OriginMs!.Value);
                if (fraction == t.Fraction) continue;
                var d = t.Request.DeltaMm;
                var position = t.SegmentStart.Translation + new Vector3(d.X, d.Y, d.Z) * (fraction / 1000.0);
                t.Part.Transform.Translation = position;
                t.Expected = t.SegmentStart.WithTranslation(position);
                if (!t.Part.Transform.Matrix.AlmostEquals(t.Expected, 1e-9)) throw Error("motion_write", "RobotStudio did not apply the requested translation.");
                t.Fraction = fraction;
                t.OwnsPose = true;
                if (fraction == 1) t.State = "completed";
            }
            catch (Exception exception)
            {
                t.State = "interrupted";
                t.Reason = exception.Message;
                t.OwnsPose = false;
            }
        }
    }

    private bool HasActiveBodyMotion(Station station, ProjectObject selected)
    {
        var part = selected as Part ?? (selected as Body)?.Parent as Part;
        return part != null && motionRun is { } run && ReferenceEquals(run.Station, station) &&
            run.Parts.TryGetValue(part.UniqueId, out var travel) && travel.Active;
    }

    private void ForgetBodyMotion(Station station, ProjectObject selected)
    {
        var part = selected as Part ?? (selected as Body)?.Parent as Part;
        if (part != null && motionRun is { } run && ReferenceEquals(run.Station, station) &&
            run.Parts.TryGetValue(part.UniqueId, out var t) && !t.Active) run.Parts.Remove(t.Id);
    }

    internal void StopBodyMotionsForShutdown()
    {
        if (motionRun is not { } run) return;
        if (Thread.CurrentThread == RobotStudioAPI.MainThread) EndBodyMotionRun(run, restore: true);
        else run.Main.Post(_ => EndBodyMotionRun(run, restore: true), null);
    }

    private void EndBodyMotionRun(MotionRun run, bool restore)
    {
        if (!ReferenceEquals(motionRun, run)) return;
        motionRun = null;
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception e) { Logger.AddMessage(new LogMessage("Offline bridge body motion cleanup: " + e.Message)); }
        }
        Cleanup(() => Simulator.Tick -= run.Tick);
        Cleanup(() => Simulator.StateChanged -= run.StateChanged);
        Cleanup(() => run.Station.Closed -= run.Closed);
        Cleanup(() => Project.ActiveProjectChanged -= run.ProjectChanged);
        if (restore && ReferenceEquals(run.Station, Station.ActiveStation))
            foreach (var t in run.Parts.Values.Where(t => t.OwnsPose))
                Cleanup(() =>
                {
                    if (StillOwns(run.Station, t)) t.Part.Transform.Translation = t.Home.Translation;
                });
    }
}
