using System.Text.Json.Nodes;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Mcp;

public static partial class ToolCatalog
{
    private static IEnumerable<JsonNode> SmartTools()
    {
        var fields = PageFields();
        fields["action"] = Choice("catalog", "list", "inspect", "create", "configure", "delete");
        fields["template"] = Choice(SmartRequests.Templates.ToArray());
        fields["name"] = Text("New station component name, required for create.");
        fields["component"] = Text("Existing ABB smart component name or ID, required for inspect, configure or delete.");
        fields["position"] = Numbers(3, "World position of the component origin in mm.");
        fields["quaternion"] = Numbers(4, "World orientation as an ABB unit quaternion [w,x,y,z].");
        fields["properties"] = Items(Object(new JsonObject
        {
            ["name"] = Text("Exact dynamic property from catalog or inspect."),
            ["value"] = new JsonObject
            {
                ["description"] = "SDK native property value. Supply an object reference by exact station name or ID. Use null to clear an optional reference.",
                ["anyOf"] = new JsonArray(Text("String or exact choice"), new JsonObject { ["type"] = "number" }, Flag(),
                    Numbers(3, "SDK vector, e.g. sensor position in metres or rotation in radians."),
                    Object(new JsonObject { ["object"] = Text("Existing station object's exact name or ID.") }, "object"),
                    new JsonObject { ["type"] = "null" })
            }
        }, "name", "value"), 32);
        yield return Tool("smart_component",
            "Catalog the installed ABB mover, rotator, conveyor control, sensor, attacher and detacher templates. Add template to catalog for the exact dynamic properties and signal names. List station instances or inspect by component. With simulation stopped, create with template and name, configure, or delete an unused top-level instance. Simulation-capable creations join the active station configuration when one exists. Supply ordered properties in native SDK units or set the component origin using world position (mm) and quaternion.",
            Object(fields), false);
    }
}
