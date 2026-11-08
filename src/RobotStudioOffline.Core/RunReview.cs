namespace RobotStudioOffline.Core;

public sealed record RunWitness(double TimeMs, double? SimulationTimeMs, DateTimeOffset Utc, string? Phase,
    string? ManualPhase, string? LiveFrame, double[] Joints, RigidFrame WorldTcp, RigidFrame BaseTcp, RigidFrame WorkObjectTcp,
    RunPointer? ProgramPointer, RunPointer? MotionPointer)
{
    internal static RunWitness? From(RunSample? s) => s is null ? null : new(s.TimeMs, s.SimulationTimeMs, s.Utc,
        s.Phase, s.ManualPhase, s.LiveFrame, (double[])s.Joints.Clone(), s.WorldTcp, s.BaseTcp, s.WorkObjectTcp,
        s.ProgramPointer, s.MotionPointer);
}
public sealed record RunAssertionResult(string Id, string Type, string Frame, string? Phase, string Outcome,
    int SampleCount, string Detail, double? MeasuredMm, double? LimitMm, RunWitness? Witness);
public sealed record RunClearanceSummary(string MovingObject, string Obstacle, int ObservedSamples, int ValidDistances,
    int Errors, bool Complete, double? MinimumDistanceMm, bool? ContactObserved, RunClearance? Closest,
    RunWitness? ClosestSample, RunWitness? ContactSample);
public sealed record RunReviewResult(string RunId, RunStatus Status, string Outcome, RunAssertion[] Rules,
    RunAssertionResult[] Assertions, RunClearanceSummary[] Clearances, string CoverageNote);

public sealed partial class RunCaptureStore
{
    public RunReviewResult Evaluate(RunReviewRequest request) => Evaluate(Snapshot(request.RunId), request);

    public static RunReviewResult Evaluate(RunSnapshot snapshot, RunReviewRequest request)
    {
        if (snapshot.Status.RunId != request.RunId) throw new BridgeException("run_mismatch", "Review and evidence refer to different runs.");
        var results = request.Assertions.Select(rule => CheckAssertion(snapshot, rule)).ToArray();
        var overall = results.Any(r => r.Outcome == "fail") ? "fail" : results.Any(r => r.Outcome == "insufficient")
            ? "insufficient" : results.Length > 0 ? "pass" : "not_requested";
        return new(request.RunId, snapshot.Status, overall, request.Assertions, results, SummarizeClearances(snapshot),
            "Results describe sampled TCP positions and sampled geometry pairs. Gaps over two requested periods, dropped evidence, " +
            "or an unfinished capture prevent absence-based conclusions. Motion between samples is unobserved.");
    }

    private static RunAssertionResult CheckAssertion(RunSnapshot run, RunAssertion rule)
    {
        var samples = run.Samples.Where(s => rule.Phase is null || s.Phase == rule.Phase || s.ManualPhase == rule.Phase).ToArray();
        var complete = run.Status.CoverageComplete && (rule.Phase is null || run.Samples.All(s => s.Phase is not null || s.ManualPhase is not null));
        var points = samples.Select(s => (Sample: s, P: rule.Frame switch
        {
            "world" => s.WorldTcp.Position, "base" => s.BaseTcp.Position, "workObject" => s.WorkObjectTcp.Position,
            _ => throw new BridgeException("invalid_request", "Unsupported assertion frame.")
        })).ToArray();

        RunAssertionResult Result(string state, string detail, int count, double? measured = null, double? limit = null, RunSample? s = null) =>
            new(rule.Id, rule.Type, rule.Frame, rule.Phase, state, count, detail, measured, limit, RunWitness.From(s));
        const string gap = "The sampled run has incomplete coverage.";
        if (points.Length == 0) return Result("insufficient", "No sample matches the requested phase or frame.", 0);
        if (rule.Type == "min_height")
        {
            var min = rule.Min!;
            var max = rule.Max!;
            var inside = points.Where(p => p.P.X >= min[0] && p.P.X <= max[0] && p.P.Y >= min[1] && p.P.Y <= max[1]).ToArray();
            if (inside.Length == 0) return Result("insufficient", "No sampled TCP entered the XY region.", 0);
            var lowest = inside.MinBy(p => p.P.Z);
            var violation = lowest.P.Z < rule.MinZ!.Value;
            return Result(violation ? "fail" : complete ? "pass" : "insufficient",
                violation ? "TCP was below the required height." : complete ? "All sampled TCPs in the region met the height." : gap,
                inside.Length, lowest.P.Z, rule.MinZ, lowest.Sample);
        }
        if (rule.Type == "forbidden_box")
        {
            var min = rule.Min!;
            var max = rule.Max!;
            var hit = points.FirstOrDefault(p => p.P.X >= min[0] && p.P.X <= max[0] && p.P.Y >= min[1] && p.P.Y <= max[1] &&
                p.P.Z >= min[2] && p.P.Z <= max[2]);
            if (hit.Sample is not null) return Result("fail", "TCP entered the closed forbidden box.", points.Length, 0, 0, hit.Sample);
            var nearest = points.Select(p => (p.Sample, D: Distance(p.P, new(Math.Clamp(p.P.X, min[0], max[0]),
                Math.Clamp(p.P.Y, min[1], max[1]), Math.Clamp(p.P.Z, min[2], max[2]))))).MinBy(p => p.D);
            return Result(complete ? "pass" : "insufficient", complete ? "All sampled TCPs were outside the forbidden box." : gap,
                points.Length, double.IsFinite(nearest.D) ? nearest.D : null, 0, nearest.Sample);
        }
        if (rule.Type != "waypoint") throw new BridgeException("invalid_request", "Unsupported assertion type.");
        var v = rule.Position!;
        var closest = points.Select(p => (p.Sample, D: Distance(p.P, new(v[0], v[1], v[2])))).MinBy(p => p.D);
        var reached = closest.D <= rule.Tolerance!.Value;
        return Result(reached ? "pass" : complete ? "fail" : "insufficient",
            reached ? "A sampled TCP was within waypoint tolerance." : complete ? "No sampled TCP reached waypoint tolerance." : gap,
            points.Length, double.IsFinite(closest.D) ? closest.D : null, rule.Tolerance, closest.Sample);
    }

    private static double Distance(Vector3D a, Vector3D b) => double.Hypot(double.Hypot(a.X - b.X, a.Y - b.Y), a.Z - b.Z);

    private static RunClearanceSummary[] SummarizeClearances(RunSnapshot run)
    {
        var groups = new Dictionary<(string Moving, string Obstacle), Pair>();
        // The SDK defaults to the selected robot when an obstacle is requested on its own.
        var movers = run.Request.MovingObjects.Length == 0 && run.Request.Obstacles.Length > 0
            ? ["$robot"] : run.Request.MovingObjects;
        foreach (var mover in movers)
        foreach (var obstacle in run.Request.Obstacles) groups.TryAdd((mover, obstacle), new());
        for (var index = 0; index < run.Samples.Length; index++)
        {
            var sample = run.Samples[index];
            foreach (var item in sample.Clearances)
            {
                var key = (item.MovingObject, item.Obstacle);
                if (!groups.TryGetValue(key, out var pair)) groups.Add(key, pair = new());
                if (pair.LastSeenIndex != index) { pair.Observed++; pair.LastSeenIndex = index; }
                if (item.Error is not null || item.DistanceMm is null) pair.Errors++;
                // A later contact query may have failed after the SDK returned a valid distance.
                if (item.DistanceMm.HasValue)
                {
                    if (pair.LastValidIndex != index) { pair.Valid++; pair.LastValidIndex = index; }
                    if (pair.Minimum is null || item.DistanceMm < pair.Minimum.DistanceMm)
                        (pair.Minimum, pair.MinimumSample) = (item, sample);
                }
                if (item.Contact.HasValue && pair.LastContactIndex != index) { pair.ContactReads++; pair.LastContactIndex = index; }
                if (item.Contact == true) pair.ContactSample ??= sample;
            }
        }
        return groups.Select(entry =>
        {
            var p = entry.Value;
            var complete = run.Status.CoverageComplete && p.Valid == run.Samples.Length && p.Errors == 0;
            return new RunClearanceSummary(entry.Key.Moving, entry.Key.Obstacle, p.Observed, p.Valid, p.Errors, complete,
                p.Minimum?.DistanceMm, p.ContactSample is not null ? true : complete && p.ContactReads == run.Samples.Length ? false : null,
                p.Minimum, RunWitness.From(p.MinimumSample), RunWitness.From(p.ContactSample));
        }).ToArray();
    }
    private sealed class Pair
    {
        public int Observed, Valid, Errors, ContactReads;
        public int LastSeenIndex = -1, LastValidIndex = -1, LastContactIndex = -1;
        public RunClearance? Minimum;
        public RunSample? MinimumSample, ContactSample;
    }
}
