using System.Text.Json.Nodes;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Mcp;

public static partial class ToolCatalog
{
    private static IEnumerable<JsonNode> MotionTools()
    {
        yield return Tool("body_motion", "Move a station Part in a straight line using simulation time. Use action=start with part, deltaMm and durationMs before or during a run. Each delta is relative to the starting position along the parent's axes (station world for a top-level part). A kinematic part can interact with physics objects. Stop holds the attained pose for pickup. Status lists progress. Simulation stop restores parts still controlled by the bridge to their initial positions.", Object(new JsonObject
        {
            ["action"] = Choice("status", "start", "stop"),
            ["part"] = Text("Exact station Part name or ID from geometry. Required for start and stop."),
            ["deltaMm"] = new JsonObject
            {
                ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3,
                ["items"] = new JsonObject { ["type"] = "number", ["minimum"] = -MotionRequests.MaxTravelMmPerAxis,
                    ["maximum"] = MotionRequests.MaxTravelMmPerAxis },
                ["description"] = "Displacement [x,y,z] in mm in the Part parent's coordinate system."
            },
            ["durationMs"] = Integer(1, MotionRequests.MaxDurationMs)
        }), false);
    }
}
