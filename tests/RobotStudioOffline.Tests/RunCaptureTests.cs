using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddRunCaptureTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("run start preserves command and selectors after its JSON document closes", () =>
        {
            var start = (TcpRecordRequest)ParseRun("tcp_record", """{"action":"start","tool":"grip","workObject":"wobj0","command":{"job":751},"phaseSignal":"goTestPhase","liveFrameSignal":"goTestFrame","movingObjects":["robot","grip"],"obstacles":["fixture"]}""");
            Equal(25, start.RateHz);
            Equal(15000, start.MaxSamples);
            Equal(751, start.Command!.Value.GetProperty("job").GetInt32());
            Equal("goTestPhase", start.PhaseSignal!);
            Equal("goTestFrame", start.LiveFrameSignal!);
            Equal("grip", start.MovingObjects[1]);
            Equal("fixture", start.Obstacles.Single());
            Equal("tcp_record", RequestParser.ParseJson("tcp_record", """{"action":"status"}""").Operation);
            Equal("run_review", RequestParser.ParseJson("run_review", """{"runId":"123456789abcdef0123456789abcdef0"}""").Operation);
        }));
        tests.Add(("record and review inputs reject unsafe IDs, destinations, ambiguous rules and invalid bounds", () =>
        {
            Reject("tcp_record", """{"action":"start","tool":"t"}""");
            Reject("tcp_record", """{"action":"start","tool":"t","workObject":"w","rateHz":19}""");
            Reject("tcp_record", """{"action":"start","tool":"t","workObject":"w","rateHz":50.5}""");
            Reject("tcp_record", """{"action":"start","tool":"t","workObject":"w","maxSamples":36001}""");
            Reject("tcp_record", """{"action":"start","tool":"t","workObject":"w","obstacles":["p","p"]}""");
            Reject("tcp_record", """{"action":"stop","runId":"../other/run.json"}""");
            Reject("tcp_record", """{"action":"status","phase":"lift"}""");
            Reject("tcp_record", """{"action":"mark","phase":"x","phase":"y"}""");
            Reject("tcp_record", """{"action":"start","controller":"192.0.2.42"}""", "real_controller_permission_required");
            const string id = "123456789abcdef0123456789abcdef0";
            Reject("run_review", JsonSerializer.Serialize(new { runId = id, directory = @"C:\outside" }));
            foreach (var rule in new[]
            {
                """{"id":"a","type":"forbidden_box","min":[1,0,0],"max":[0,1,1]}""",
                """{"id":"a","type":"min_height","minXY":[0,0],"maxXY":[1,1],"minZ":1e400}""",
                """{"id":"a","type":"waypoint","position":[0,0,0],"tolerance":-1}""",
                """{"id":"a","type":"waypoint","position":[0,0,0],"tolerance":1,"frame":"tool"}"""
            }) Reject("run_review", "{\"runId\":\"" + id + "\",\"assertions\":[" + rule + "]}");
        }));
        tests.Add(("phase and frame selection produce the observed worst height and closest waypoint with pointers", () =>
        {
            var store = new RunCaptureStore();
            var id = store.Begin(new("start", Tool: "grip", WorkObject: "fixture"), "cell", "robot", "T_ROB1");
            store.AddSample(id, EvidenceSample(0, 10, "pickup"));
            store.AddSample(id, EvidenceSample(40, 2, "place") with { ManualPhase = "cycle" });
            store.AddSample(id, EvidenceSample(80, 4, "place"));
            store.Stop(id, "owner", 80);
            var review = store.Evaluate(new(id, [
                new("height", "min_height", "base", "place", [0,0], [0,0], 103),
                new("goal", "waypoint", "workObject", "cycle", Position: [0,0,1002], Tolerance: 0),
                new("outside", "min_height", "world", null, [50,50], [100,100], 1)
            ], false));
            var height = review.Assertions[0];
            Equal("fail", height.Outcome);
            Equal(2, height.SampleCount);
            Near(102, height.MeasuredMm!.Value);
            Near(40, height.Witness!.TimeMs);
            Equal(42, height.Witness.ProgramPointer!.Line!.Value);
            Equal("pass", review.Assertions[1].Outcome);
            Near(0, review.Assertions[1].MeasuredMm!.Value);
            Equal("insufficient", review.Assertions[2].Outcome);
            Equal("fail", review.Outcome);
        }));
        tests.Add(("dropped observations preserve witnessed hits and prevent absence-based passes or missed-waypoint failures", () =>
        {
            var store = new RunCaptureStore();
            var id = store.Begin(new("start", Tool: "grip", WorkObject: "fixture"), "cell", "robot", "T_ROB1");
            store.AddSample(id, EvidenceSample(0, 1));
            store.AddSample(id, EvidenceSample(200, 2));
            store.NoteDropped(id, 3);
            var stopped = store.Stop(id, "stopped", 200);
            True(!stopped.Status.CoverageComplete);
            Near(200, stopped.Status.MaxGapMs);
            Equal(3L, stopped.Status.DroppedSamples);
            var report = store.Evaluate(new(id, [
                new("inside", "forbidden_box", "world", null, [0,0,2], [5,5,10]),
                new("elsewhere", "forbidden_box", "world", null, [10,10,10], [20,20,20]),
                new("seen", "waypoint", "world", null, Position:[0,0,1], Tolerance:0),
                new("unseen", "waypoint", "world", null, Position:[3,3,3], Tolerance:0)
            ]));
            True(report.Assertions.Select(a => a.Outcome).SequenceEqual(new[] { "fail", "insufficient", "pass", "insufficient" }));
            Near(200, report.Assertions[0].Witness!.TimeMs);
        }));
        tests.Add(("clearance minima retain their measured points, sample phase and independent contact observation", () =>
        {
            var store = new RunCaptureStore();
            var id = store.Begin(new("start", Tool:"grip", WorkObject:"w") { MovingObjects = ["tool"], Obstacles = ["post", "unknown"] }, "cell", "r", "task");
            store.AddSample(id, EvidenceSample(0, 150) with { Clearances = [new("tool", "post", 30, Contact:false)] });
            store.AddSample(id, EvidenceSample(40, 125, "approach") with { Clearances = [
                new("tool","post",0,new(5,6,7),new(5,6,7), Contact:true, ContactEvidence:"native", MovingPart:"jaw"),
                new("tool","unknown",null,Error:"unavailable")] });
            store.Stop(id, "manual", 40);
            var results = store.Evaluate(new(id, [])).Clearances;
            var post = results.Single(p => p.Obstacle == "post");
            Near(0, post.MinimumDistanceMm!.Value);
            True(post.Complete && post.ContactObserved == true);
            Equal("approach", post.ClosestSample!.Phase!);
            Near(40, post.ContactSample!.TimeMs);
            Equal(new Vector3D(5,6,7), post.Closest!.MovingPointWorldMm!.Value);
            var unknown = results.Single(p => p.Obstacle == "unknown");
            True(!unknown.Complete && unknown.ContactObserved is null && unknown.MinimumDistanceMm is null);
            Equal(1, unknown.Errors);
        }));
        tests.Add(("missing default robot pairs stay visible and a contact error preserves the measured clearance", () =>
        {
            var store = new RunCaptureStore();
            var id = store.Begin(new("start", Tool:"t", WorkObject:"w") { Obstacles = ["fixture", "unobserved"] }, "cell", "r", "task");
            store.AddSample(id, EvidenceSample(0, 3) with { Clearances = [new("$robot", "fixture", 12, Error:"contact query failed")] });
            store.Stop(id, "stopped", 0);
            var pairs = store.Evaluate(new(id, [])).Clearances;
            var fixture = pairs.Single(p => p.Obstacle == "fixture");
            Near(12, fixture.MinimumDistanceMm!.Value);
            True(fixture.ContactObserved is null && !fixture.Complete);
            var missing = pairs.Single(p => p.Obstacle == "unobserved");
            Equal("$robot", missing.MovingObject);
            Equal(0, missing.ObservedSamples);
            True(missing.MinimumDistanceMm is null && missing.ContactObserved is null && !missing.Complete);
        }));
        tests.Add(("bounded store refuses concurrent runs, copies samples and counts capacity losses", () =>
        {
            var store = new RunCaptureStore();
            var start = new TcpRecordRequest("start", Tool:"g", WorkObject:"w", MaxSamples:2);
            var id = store.Begin(start, "s", "r", "t", "station-42", @"C:\cell.rs");
            Equal("recording_active", Throws<BridgeException>(() => store.Begin(start, "s", "r", "t")).Code);
            var sample = EvidenceSample(0, 4);
            True(store.AddSample(id, sample));
            sample.Joints[0] = 12;
            var exposed = store.Snapshot(id);
            exposed.Samples[0].Joints[0] = 23;
            Near(36, store.Snapshot(id).Samples[0].Joints[0]);
            True(store.AddSample(id, EvidenceSample(40, 6)));
            Near(40, store.Status(id).DurationMs);
            True(!store.AddSample(id, EvidenceSample(80, 10)));
            Equal(1L, store.Stop(id, "capacity", 80, false).Status.DroppedSamples);
            Equal("station-42", store.Status(id).StationId!);
            True(!store.Status(id).CoverageComplete);
            Equal("recording_stopped", Throws<BridgeException>(() => store.AddSample(id, sample)).Code);
        }));
        tests.Add(("JSON and CSV evidence exports reopen by safe run ID with command, event, PP, calibration and review", RoundTripRun));
    }

    private static RunRequest ParseRun(string op, string json)
    {
        using var document = JsonDocument.Parse(json);
        return RunRequests.Parse(op, document.RootElement);
    }
    private static RunSample EvidenceSample(double time, double z, string? phase = "cycle") =>
        new(time, 1000 + time, DateTimeOffset.UnixEpoch.AddMilliseconds(time), phase, "12", [36,-18,7,0,0,24],
            new(new(0,0,z), QuaternionD.Identity), new(new(0,0,z+100), QuaternionD.Identity),
            new(new(0,0,z+1000), QuaternionD.Identity), new("T_ROB1","Fixture","Inspect",42,8,"MoveL"),
            new("T_ROB1","Fixture","Inspect",41), [], RigidFrame.Identity, RigidFrame.Identity);

    private static void RoundTripRun()
    {
        var root = Directory.CreateTempSubdirectory("RobotStudioRun-");
        try
        {
            var store = new RunCaptureStore(root.FullName);
            var request = (TcpRecordRequest)ParseRun("tcp_record", """{"action":"start","tool":"grip","workObject":"w","command":{"job":"test,1"}}""");
            var id = store.Begin(request, "station", "TestRobot", "T_ROB1");
            store.AddSample(id, EvidenceSample(0, 12));
            store.AddSample(id, EvidenceSample(40, 10));
            store.AddEvent(id, new(40, DateTimeOffset.UnixEpoch, "rapid", "line 42, \"MoveL\"", "cycle", 1040));
            var stopped = store.Stop(id, "test", 40);
            var review = store.Evaluate(new(id, [new("goal", "waypoint", "world", null, Position:[0,0,10], Tolerance:0)]));
            var paths = store.ExportAsync(stopped, review).GetAwaiter().GetResult();
            True(File.Exists(paths.RunJson) && File.Exists(paths.ClearancesCsv));
            var csv = File.ReadAllText(paths.SamplesCsv);
            True(csv.Contains("worldFromBaseXmm") && csv.Contains("programRoutine") && csv.Contains("T_ROB1,Fixture,Inspect,42"));
            var events = File.ReadAllText(paths.EventsCsv);
            True(events.Contains("command") && events.Contains("\"line 42, \"\"MoveL\"\"\""));
            var reopened = new RunCaptureStore(root.FullName);
            var loaded = reopened.Snapshot(id);
            Equal(2, loaded.Samples.Length);
            Equal("test,1", loaded.Request.Command!.Value.GetProperty("job").GetString()!);
            Near(10, loaded.Samples[1].WorldTcp.Position.Z);
            Equal("pass", reopened.ReadReview(id)!.Assertions.Single().Outcome);
            Throws<BridgeException>(() => reopened.Snapshot(@"..\other"));
            Throws<BridgeException>(() => reopened.SaveView(id, "../other", []));
            var modified = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.RunJson))!.AsObject();
            modified["status"]!["droppedSamples"] = 3;
            File.WriteAllText(paths.RunJson, modified.ToJsonString());
            Equal("invalid_run_artifact", Throws<BridgeException>(() => reopened.Snapshot(id)).Code);
        }
        finally
        {
            // The unique fixture root contains only the files and directories this test created.
            foreach (var dir in root.EnumerateDirectories())
            {
                foreach (var file in dir.EnumerateFiles()) file.Delete();
                dir.Delete();
            }
            root.Delete();
        }
    }
}
