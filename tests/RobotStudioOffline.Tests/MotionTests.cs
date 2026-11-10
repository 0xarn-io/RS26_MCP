using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddMotionTests(List<(string Name, Action Run)> tests)
    {
        BodyMotionRequest Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return MotionRequests.Parse("body_motion", document.RootElement);
        }
        tests.Add(("body travel accepts signed parent-frame displacement and clamps at the endpoint", () =>
        {
            var travel = Parse("""{"action":"start","part":"Unit","deltaMm":[100,-750,0],"durationMs":2500}""");
            Equal(new Vector3D(100, -750, 0), travel.DeltaMm);
            Near(0, travel.TravelFraction(0));
            Near(0.5, travel.TravelFraction(1250));
            Near(1, travel.TravelFraction(2500));
            Near(1, travel.TravelFraction(2505));
            Throws<ArgumentOutOfRangeException>(() => travel.TravelFraction(-1));
        }));
        tests.Add(("body motion strictly separates status, stop and start arguments", () =>
        {
            Equal("status", Parse("{}").Action);
            Equal("Unit", Parse("""{"action":"status","part":"Unit"}""").Part!);
            Equal("stop", Parse("""{"action":"stop","part":"Unit"}""").Action);
            foreach (var args in new[] { """{"action":"stop"}""", """{"part":"x","durationMs":5}""",
                """{"action":"start","part":"x","deltaMm":[0,0,0],"durationMs":1}""",
                """{"action":"start","part":"x","deltaMm":[0,1,2]}""",
                """{"action":"start","part":"x","deltaMm":[0,1,2],"durationMs":0}""",
                """{"action":"start","part":"x","deltaMm":[0,1,2],"durationMs":3600001}""",
                """{"action":"start","part":"x","deltaMm":[1000001,0,0],"durationMs":20}""",
                """{"action":"start","part":"x","deltaMm":[1,0],"durationMs":20}""",
                """{"action":"stop","part":"x","deltaMm":[1,0,0]}""",
                """{"action":"status","controller":"x"}""" })
                Throws<BridgeException>(() => Parse(args));
        }));
    }
}
