using System.Text.Json.Nodes;

namespace RobotStudioOffline.Mcp;

public static partial class ToolCatalog
{
    private static IEnumerable<JsonNode> BodyTools()
    {
        yield return Tool("body_edit", "Inspect, move or remove an individual CAD Body or station Part. Move sets the supplied pose components in world or robot base coordinates (mm and wxyz). Attach a picked Part to a graphical gripper, another Part or a single-flange mechanism at its current world pose. Release into world or transfer to an optional receiving parent. Attach/release accept the sole Body of a Part. State reports the actual attachment and world pose. Move and transfer can run during simulation. Delete requires stopped simulation.", Object(new JsonObject
        {
            ["action"] = Choice("state", "move", "delete", "attach", "release"),
            ["object"] = Text("Exact component/body ID or unique name from geometry. Use the Part ID to move or remove a complete item."),
            ["parent"] = Text("Exact graphical parent ID or unique name. Required for attach, optional receiving parent for release."),
            ["frame"] = Choice("world", "base"), ["robot"] = Robot(),
            ["position"] = Numbers(3, "New XYZ in mm in the chosen frame."),
            ["quaternion"] = Numbers(4, "New ABB unit quaternion in wxyz order in the chosen frame.")
        }, "action", "object"), false);
        yield return Tool("collision_set_delete", "Remove one existing collision set by exact ID or unique name from the stopped station. The objects in its two groups remain in the station.", Object(new JsonObject
        {
            ["set"] = Text("Exact collision set ID or unique name.")
        }, "set"), false);
    }
}
