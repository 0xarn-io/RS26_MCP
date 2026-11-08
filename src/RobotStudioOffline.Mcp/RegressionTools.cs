using System.Text.Json.Nodes;

namespace RobotStudioOffline.Mcp;

public static partial class ToolCatalog
{
    private static JsonObject Choice(params string[] values) => new() { ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
    private static JsonObject Integer(int min, int max) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max };
    private static JsonObject Flag() => new() { ["type"] = "boolean" };
    private static JsonObject RunId() => new() { ["type"] = "string", ["pattern"] = "^[0-9A-Fa-f]{32}$",
        ["description"] = "Run identifier returned by tcp_record start." };
    private static JsonObject Assertions() => Items(Object(new JsonObject
    {
        ["id"] = Text("Unique assertion label"), ["type"] = Choice("forbidden_box", "waypoint", "min_height"),
        ["frame"] = Choice("base", "world", "workObject"), ["phase"] = Text("Optional phase filter"),
        ["min"] = Numbers(3, "Forbidden box lower corner in mm"), ["max"] = Numbers(3, "Forbidden box upper corner"),
        ["position"] = Numbers(3, "Waypoint XYZ in mm"), ["tolerance"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 },
        ["minXY"] = Numbers(2, "Height region lower XY"), ["maxXY"] = Numbers(2, "Height region upper XY"),
        ["minZ"] = new JsonObject { ["type"] = "number" }
    }, "id", "type"), 64);

    private static IEnumerable<JsonNode> RegressionTools()
    {
        JsonObject Selectors(int max) => Items(Text("Exact geometry name or unique ID"), max);
        yield return Tool("geometry", "List station components and CAD bodies, IDs, parent and available world bounds in mm. match filters by name substring or ID.", Object(new JsonObject
        { ["match"] = Text("Name or ID"), ["offset"] = Integer(0,int.MaxValue), ["limit"] = Integer(1,500) }), true);
        yield return Tool("raycast", "Measure the nearest graphical intersection from a point in the named tool frame. Set parts or exclude to select CAD parts, e.g. skip the sensor housing. Distances are in mm.", Object(new JsonObject
        {
            ["robot"] = Robot(), ["tool"] = ToolName(), ["origin"] = Numbers(3,"Tool-relative origin, mm"),
            ["direction"] = Numbers(3,"Nonzero tool-relative direction"), ["maxDistance"] = PositiveDimension(),
            ["parts"] = Selectors(64), ["exclude"] = Selectors(64)
        }, "tool","origin","direction","maxDistance"), true);
        yield return Tool("collision_set", "Create an active named RobotStudio collision set from two groups of station graphic components in one undo step. Stop simulation first.", Object(new JsonObject
        {
            ["name"] = Text("New set name"), ["first"] = Selectors(64), ["second"] = Selectors(64),
            ["nearMissMm"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 }
        }, "name","first","second"), false);
        yield return Tool("simulation_speed", "Read or set RobotStudio's desired simulation-time/wall-time ratio, such as 5 or 10. Actual throughput depends on the cell and host.", Object(new JsonObject
        { ["factor"] = new JsonObject { ["type"] = "number",["exclusiveMinimum"] = 0,["maximum"] = 10 },
            ["stepMs"] = new JsonObject { ["type"] = "number",["minimum"] = 1,["maximum"] = 100,["description"] = "Requested simulator time step in ms, e.g. 10 for a 50 Hz trace." } }), false);
        yield return Tool("tcp_record", "Start before or during station simulation. Record actual simulator ticks, tool TCP in three frames, joints, selected VC signals and optional geometry distances. Start needs tool and workObject. mark needs runId and phase. stop exports the run. status accepts an optional runId.", Object(new JsonObject
        {
            ["action"] = Choice("start","stop","status","mark"), ["runId"] = RunId(), ["robot"] = Robot(), ["tool"] = ToolName(),
            ["assertions"] = Assertions(),
            ["workObject"] = WorkObject(), ["rateHz"] = Integer(20,50), ["maxSamples"] = Integer(1,36000), ["phase"] = Text("Manual phase label"),
            ["phaseSignal"] = SignalName(), ["liveFrameSignal"] = SignalName(), ["movingObjects"] = Selectors(8), ["obstacles"] = Selectors(8),
            ["command"] = new JsonObject { ["description"] = "Original run command or JSON object saved in the bundle.",
                ["type"] = new JsonArray("string","object") }
        },"action"), false);
        yield return Tool("run_review", "Evaluate sampled TCP against forbidden boxes, waypoint tolerances and regional height floors. Returns evidence, sampled pair minima and coverage. Export defaults to true and writes the verdict in the run folder.", Object(new JsonObject
        { ["runId"] = RunId(), ["assertions"] = Assertions(), ["export"] = Flag() }, "runId"), false);
        yield return Tool("run_view", "Return a top or side PNG of the current station geometry with the recorded TCP and specified assertion overlays. With no assertions, uses the last exported review. Also saves PNG in the run folder.", Object(new JsonObject
        { ["runId"] = RunId(), ["view"] = Choice("top","side"), ["assertions"] = Assertions() }, "runId", "view"), false);
        foreach (var name in new[] { "rapid_load", "rapid_check", "rapid_diff", "rapid_sync_real" })
        {
            var fields = new JsonObject { ["robot"] = Robot(), ["task"] = Text("RAPID task name") };
            fields["files"] = Items(new JsonObject { ["type"] = "string", ["description"] = "Fully qualified local RAPID module file path" },20);
            if (name == "rapid_diff") { fields["includeReal"] = Flag(); fields["pers"] = Items(Text("Module/name persistent value"),100); }
            var description = name switch
            {
                "rapid_check" => "With files, check local module syntax and source locations. With no files, check the installed virtual task's program.",
                "rapid_load" => "Stop the selected virtual RAPID task if running, replace modules from local file snapshots and return a load/check verdict with SDK locations where available.",
                "rapid_diff" => "Compare selected files against modules loaded in the station VC, with optional PERS values. includeReal opens a local RobotStudio owner permission dialog before checking the selected real controller.",
                _ => "Load the exact clone-verified files on the identified, stopped real controller. Requires one-use affirmative owner permission inside RobotStudio for the displayed operation."
            };
            string[] required = name switch { "rapid_check" => [], "rapid_sync_real" => ["files","task"], _ => ["files"] };
            var tool = Tool(name, description, Object(fields,required), name == "rapid_check");
            if (name is "rapid_diff" or "rapid_sync_real") tool["annotations"]!["openWorldHint"] = true;
            yield return tool;
        }
    }
}
