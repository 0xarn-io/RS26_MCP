using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddSceneTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("ray direction uses a normalized tool-frame vector and bounded range", () =>
        {
            var args = JsonSerializer.SerializeToElement(new { tool="tip", origin=new[]{0,0,100}, direction=new[]{0,4,3}, maxDistance=4000 });
            var ray = (RayQuery)SceneRequests.Parse("raycast",args);
            Equal(0.8, ray.Direction.Y);
            Equal(0.6, ray.Direction.Z);
            Reject("raycast", """{"tool":"tip","origin":[0,0,0],"direction":[0,0,0],"maxDistance":30}""");
            Reject("raycast", """{"tool":"tip","origin":[0,0,0],"direction":[0,0,1],"maxDistance":-3}""");
        }));
        tests.Add(("measured boxes can use a named robot base and enforce valid placement", () =>
        {
            var args = JsonSerializer.SerializeToElement(new { name="Test barrier", shape="box", size=new[]{10,20,30}, frame="base", robot="TestRobot" });
            var create = (GeometryCreateRequest)GeometryRequests.Parse("geometry_create",args);
            Equal("base",create.PlacementFrame);
            Equal("TestRobot",create.Robot!);
            Reject("geometry_create", """{"name":"A","shape":"box","size":[1,1,1],"frame":"world","robot":"TestRobot"}""");
        }));
        tests.Add(("speed, geometry groups, and view selectors reject invalid requests", () =>
        {
            RequestParser.ParseJson("simulation_speed", """{"factor":10}""");
            Reject("simulation_speed", """{"factor":11}""");
            Reject("collision_set", """{"name":"s","first":["x","x"],"second":["y"]}""");
            Reject("geometry", """{"limit":501}""");
            Reject("run_view", """{"runId":"../../old","view":"top"}""");
            Reject("run_view", """{"runId":"00000000000000000000000000000000","view":"camera"}""");
        }));
        tests.Add(("VC joint jumps are explicit and isolated to jump", () =>
        {
            const string args = """{"jointtarget":{"robotAxes":[36,-18,7,0,0,24]},"applyTo":"virtual"}""";
            Equal("virtual",RequestParser.ParseJson("jump",args).ApplyTo!);
            Reject("collisions",args);
            Reject("jump",args.Replace("virtual","real"));
        }));
    }
}
