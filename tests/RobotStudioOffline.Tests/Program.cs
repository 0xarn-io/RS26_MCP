using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private const string Pose = """{"position":[450,12,700],"quaternion":[1,0,0,0],"configuration":[1,0,-1,0]}""";
    static partial void AddRapidModuleTests(List<(string Name, Action Run)> tests);
    private static int Main()
    {
        var tests = new List<(string Name, Action Run)>
        {
            ("station accepts only an object with no arguments", () =>
            {
                Equal("station", RequestParser.ParseJson("station", "{}").Operation);
                Reject("station", "[]");
                Reject("station", "{\"unused\":true}");
            }),
            ("request parser rejects unknown operations, malformed JSON and case variants", () =>
            {
                Reject("connect", "{}", "unknown_operation");
                Reject("station", "{", "invalid_json");
                Reject("robot_pose", "{\"Tool\":\"probe\",\"workObject\":\"wobj0\"}");
            }),
            ("real controller requests require explicit owner permission recursively", () =>
            {
                foreach (var json in new[] { "{\"controller\":\"192.0.2.42\"}", "{\"url\":\"http://localhost\"}",
                    "{\"x\":{\"controllerId\":\"x\"}}", "{\"robot\":\"192.0.2.42\"}", "{\"x\":[{\"IP\":\"198.51.100.24\"}]}" })
                {
                    var error = Reject("station", json, "real_controller_permission_required");
                    Equal(403, error.StatusCode);
                    True(error.Message.Contains("permission", StringComparison.OrdinalIgnoreCase));
                }
            }),
            ("robot pose requires explicit tool and work object", () =>
            {
                Reject("robot_pose", "{\"tool\":\"probe\"}");
                Reject("robot_pose", "{\"workObject\":\"wobj0\"}");
                var request = RequestParser.ParseJson("robot_pose", "{\"robot\":\"TestRobot\",\"tool\":\"probe\",\"workObject\":\"wobj0\"}");
                Equal("TestRobot", request.Robot!);
                Equal("probe", request.Tool!);
            }),
            ("named reach target preserves selector and derives only work object", () =>
            {
                var request = RequestParser.ParseJson("reach", "{\"target\":\"pReference\",\"tool\":\"probe\"}");
                Equal("pReference", request.Target!);
                True(request.WorkObject is null);
                Reject("reach", "{\"target\":\"pReference\"}");
            }),
            ("robtarget preserves RAPID units and scalar-first quaternion", () =>
            {
                var request = RequestParser.ParseJson("reach", Reach(Pose));
                Near(450, request.RobTarget!.Position[0]);
                Near(1, request.RobTarget.Quaternion[0]);
                Equal(-1, request.RobTarget.Configuration[2]);
                Reject("reach", "{\"robtarget\":" + Pose + ",\"tool\":\"probe\"}");
            }),
            ("duplicate, ambiguous and absent pose arguments are rejected", () =>
            {
                Reject("reach", "{\"target\":\"pReference\",\"target\":\"pOther\",\"tool\":\"probe\"}");
                Reject("reach", "{\"target\":\"pReference\",\"tool\":\"probe\",\"workObject\":\"wobj0\",\"robtarget\":" + Pose + "}");
                Reject("reach", "{}");
                Reject("jump", "{}");
                Reject("reach", Reach(Pose.Replace("\"position\":", "\"position\":[0,0,0],\"position\":")));
            }),
            ("nonfinite, wrong-length, fractional configuration and unknown pose fields are rejected", () =>
            {
                foreach (var target in new[] { Pose.Replace("450", "1e400"), Pose.Replace("450,12,700", "450,12"),
                    Pose.Replace("1,0,-1,0", "1,0,-1.2,0"), Pose.Replace("1,0,-1,0", "1,0,2147483648,0"),
                    Pose.Replace("\"configuration\"", "\"config\""), Pose.Replace("[450,12,700]", "[\"450\",12,700]") })
                    Reject("reach", Reach(target));
            }),
            ("quaternion accepts sign-equivalent unit values and rejects zero or scaled values", () =>
            {
                RequestParser.ParseJson("reach", Reach(Pose.Replace("[1,0,0,0]", "[-1,0,0,0]")));
                foreach (var quaternion in new[] { "[0,0,0,0]", "[2,0,0,0]", "[1,0,0]", "[1e400,0,0,0]" })
                    Reject("reach", Reach(Pose.Replace("[1,0,0,0]", quaternion)));
            }),
            ("jointtarget preserves degrees and six external axes including undefined sentinel", () =>
            {
                var request = RequestParser.ParseJson("jump", "{\"jointtarget\":{\"robotAxes\":[36,-18,7,0,0,24],\"externalAxes\":[9e9,9e9,9e9,9e9,9e9,9e9]}}");
                Near(36, request.JointTarget!.RobotAxes[0]);
                Near(9e9, request.JointTarget.ExternalAxes![0]);
                Reject("jump", "{\"jointtarget\":{\"robotAxes\":[36,0]}}");
                Reject("reach", "{\"jointtarget\":{\"robotAxes\":[36,-18,7,0,0,24]}}");
            }),
            ("collision and simulation requests validate their operation-specific arguments", () =>
            {
                RequestParser.ParseJson("collisions", "{}");
                RequestParser.ParseJson("collisions", "{\"target\":\"pReference\",\"tool\":\"probe\"}");
                Reject("collisions", "{\"tool\":\"probe\"}");
                foreach (var action in new[] { "state", "start", "stop", "reset" })
                    Equal(action, RequestParser.ParseJson("simulation", "{\"action\":\"" + action + "\"}").Action!);
                Reject("simulation", "{}");
                Reject("simulation", "{\"action\":\"restart\"}");
            }),
            ("names reject null, whitespace, control characters and excessive lengths", () =>
            {
                foreach (var name in new[] { "null", "\" \"", "\"a\\nb\"", JsonSerializer.Serialize(new string('x', 257)) })
                    Reject("robot_pose", "{\"robot\":" + name + ",\"tool\":\"probe\",\"workObject\":\"wobj0\"}");
            }),
            ("success and error JSON envelopes have stable camelCase shapes", () =>
            {
                var success = JsonSerializer.Serialize(BridgeResponse.Success(new { reachable = true, joints = new[] { 36.0, 5.0 } }), JsonDefaults.Options);
                using var parsed = JsonDocument.Parse(success);
                True(parsed.RootElement.GetProperty("ok").GetBoolean());
                True(parsed.RootElement.GetProperty("data").GetProperty("reachable").GetBoolean());
                True(!parsed.RootElement.TryGetProperty("error", out _));
                var failure = JsonSerializer.Serialize(BridgeResponse.Failure(new("invalid_request", "bad pose")), JsonDefaults.Options);
                Equal("{\"ok\":false,\"error\":{\"code\":\"invalid_request\",\"message\":\"bad pose\"}}", failure);
            }),
            ("request DTO serialization round trips exact robtarget and jointtarget JSON keys", () =>
            {
                var reach = RequestParser.ParseJson("reach", Reach(Pose));
                var json = JsonSerializer.Serialize(reach, JsonDefaults.Options);
                using var parsed = JsonDocument.Parse(json);
                True(parsed.RootElement.TryGetProperty("robtarget", out _));
                True(!parsed.RootElement.TryGetProperty("operation", out _));
                Near(450, RequestParser.ParseJson("reach", json).RobTarget!.Position[0]);
                var jump = RequestParser.ParseJson("jump", "{\"jointtarget\":{\"robotAxes\":[36,-18,7,0,0,24]}}");
                Near(36, RequestParser.ParseJson("jump", JsonSerializer.Serialize(jump, JsonDefaults.Options)).JointTarget!.RobotAxes[0]);
            }),
            ("scalar-first quaternion rotates X toward Y for positive Z rotation", () =>
            {
                var rotated = FrameMath.Rotate(new(Math.Sqrt(0.5), 0, 0, Math.Sqrt(0.5)), new(100, 0, 0));
                VectorNear(new(0, 100, 0), rotated);
            }),
            ("frame composition rotates child translation and keeps millimetres", () =>
            {
                var parent = new RigidFrame(new(1000, 2000, 3000), new(Math.Sqrt(0.5), 0, 0, Math.Sqrt(0.5)));
                var result = FrameMath.Compose(parent, new(new(100, 0, 50), QuaternionD.Identity));
                VectorNear(new(1000, 2100, 3050), result.Position);
                VectorNear(new(0, 1, 0), FrameMath.Rotate(result.Orientation, new(1, 0, 0)));
            }),
            ("inverse and relative frame round trips across noncommuting rotations", () =>
            {
                var a = new RigidFrame(new(123, -456, 789), new(0.5, 0.5, 0.5, 0.5));
                var b = new RigidFrame(new(-91, 82, 73), new(Math.Sqrt(0.5), 0, Math.Sqrt(0.5), 0));
                FrameNear(RigidFrame.Identity, FrameMath.Compose(a, FrameMath.Inverse(a)));
                FrameNear(b, FrameMath.RelativeTo(a, FrameMath.Compose(a, b)));
                var ab = FrameMath.Compose(a, b);
                var ba = FrameMath.Compose(b, a);
                True(Math.Abs(ab.Position.X - ba.Position.X) > 1);
            }),
            ("tool offset is removed in rotated TCP coordinates", () =>
            {
                var worldTcp = new RigidFrame(new(700, 0, 500), new(Math.Sqrt(0.5), 0, Math.Sqrt(0.5), 0));
                var flangeTcp = new RigidFrame(new(0, 0, 200), QuaternionD.Identity);
                var worldFlange = FrameMath.FlangeFromTcp(worldTcp, flangeTcp);
                VectorNear(new(500, 0, 500), worldFlange.Position);
                FrameNear(worldTcp, FrameMath.Compose(worldFlange, flangeTcp));
            }),
            ("quaternion sign equivalence, large normalization and angle conversions", () =>
            {
                var v = new Vector3D(3, 5, -7);
                VectorNear(FrameMath.Rotate(new(0.5, 0.5, -0.5, 0.5), v), FrameMath.Rotate(new(-0.5, -0.5, 0.5, -0.5), v));
                Near(1, FrameMath.Normalize(new(1e300, 0, 0, 0)).W);
                Near(Math.PI / 2, FrameMath.DegreesToRadians(90));
                Near(90, FrameMath.RadiansToDegrees(Math.PI / 2));
                Throws<ArgumentException>(() => FrameMath.Normalize(new(0, 0, 0, 0)));
            }),
            ("parsed robtarget feeds frame math without units or quaternion reordering", () =>
            {
                var target = RequestParser.ParseJson("reach", Reach(Pose)).RobTarget!;
                var frame = FrameMath.FromRobTarget(target);
                VectorNear(new(450, 12, 700), frame.Position);
                VectorNear(new(1, 2, 3), FrameMath.Rotate(frame.Orientation, new(1, 2, 3)));
            })
        };
        AddJointMappingTests(tests);
        AddDevelopmentTests(tests);
        AddIoRequestTests(tests);
        AddGeometryRequestTests(tests);
        AddRunCaptureTests(tests);
        AddRapidModuleTests(tests);
        AddSceneTests(tests);
        AddBridgeActivityTests(tests);
        AddNamedTargetSelectorTests(tests);
        AddTransportTests(tests);
        var failed = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed.");
        return failed == 0 ? 0 : 1;
    }

    static partial void AddTransportTests(List<(string Name, Action Run)> tests);

    private static string Reach(string pose) => "{\"tool\":\"probe\",\"workObject\":\"wobj0\",\"robtarget\":" + pose + "}";
    private static BridgeException Reject(string operation, string json, string code = "invalid_request")
    {
        var exception = Throws<BridgeException>(() => RequestParser.ParseJson(operation, json));
        Equal(code, exception.Code);
        return exception;
    }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Near(double expected, double actual, double tolerance = 1e-9) { if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void VectorNear(Vector3D expected, Vector3D actual) { Near(expected.X, actual.X); Near(expected.Y, actual.Y); Near(expected.Z, actual.Z); }
    private static void FrameNear(RigidFrame expected, RigidFrame actual)
    {
        VectorNear(expected.Position, actual.Position);
        VectorNear(FrameMath.Rotate(expected.Orientation, new(1, 0, 0)), FrameMath.Rotate(actual.Orientation, new(1, 0, 0)));
        VectorNear(FrameMath.Rotate(expected.Orientation, new(0, 1, 0)), FrameMath.Rotate(actual.Orientation, new(0, 1, 0)));
    }
}
