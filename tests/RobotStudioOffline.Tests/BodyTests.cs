using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddBodyTests(List<(string Name, Action Run)> tests)
    {
        static BodyRequest Parse(string operation, string json)
        {
            using var doc = JsonDocument.Parse(json);
            return BodyRequests.Parse(operation, doc.RootElement);
        }
        tests.Add(("geometry moves preserve omitted pose components in a rotated robot base", () =>
        {
            var baseFrame = new RigidFrame(new(10, 20, 30), new(Math.Sqrt(0.5), 0, 0, Math.Sqrt(0.5)));
            var original = new RigidFrame(new(-5, 50, 1), new(0, 1, 0, 0));
            var move = Parse("body_edit", """{"action":"move","object":"part-A","frame":"base","robot":"arm","position":[50,10,0]}""");
            FrameNear(new(new(0, 70, 30), original.Orientation), move.MovePose(baseFrame, original));
            var rotate = Parse("body_edit", """{"action":"move","object":"part-A","frame":"base","quaternion":[1,0,0,0]}""");
            FrameNear(new(original.Position, baseFrame.Orientation), rotate.MovePose(baseFrame, original));
        }));
        tests.Add(("body and collision delete require an exact action-specific selector", () =>
        {
            Equal("part", Parse("body_edit", """{"action":"delete","object":"part"}""").Selector);
            Equal("fixture", Parse("collision_set_delete", """{"set":"fixture"}""").Selector);
            Equal("state", Parse("body_edit", """{"action":"state","object":"a"}""").Action);
            Reject("body_edit", """{"object":"part"}""");
            Reject("body_edit", """{"action":"delete","object":"part","position":[0,0,0]}""");
            Reject("collision_set_delete", """{"set":""}""");
            Reject("collision_set_delete", """{"set":"fixture","object":"extra"}""");
        }));
        tests.Add(("cargo transfer distinguishes a named holder from release into the world", () =>
        {
            Equal("gripper", Parse("body_edit", """{"action":"attach","object":"item","parent":"gripper"}""").Parent!);
            True(Parse("body_edit", """{"action":"release","object":"item"}""").Parent is null);
            Equal("tray", Parse("body_edit", """{"action":"release","object":"item","parent":"tray"}""").Parent!);
            Reject("body_edit", """{"action":"attach","object":"item"}""");
            Reject("body_edit", """{"action":"release","object":"item","robot":"arm"}""");
        }));
        tests.Add(("instant placement rejects malformed poses and controller access", () =>
        {
            foreach (var fields in new[] { "", ""","position":[0,0]""", ""","quaternion":[0,0,0,0]""",
                ""","position":[0,0,1],"frame":"tool" """, ""","position":[0,0,1],"robot":"arm" """ })
                Reject("body_edit", """{"action":"move","object":"part" """ + fields + "}");
            Reject("body_edit", """{"action":"move","object":"part","controller":"192.0.2.1"}""", "real_controller_permission_required");
        }));
    }
}
