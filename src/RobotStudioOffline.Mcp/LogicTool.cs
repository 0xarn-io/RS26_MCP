using System.Text.Json.Nodes;

namespace RobotStudioOffline.Mcp;

public static partial class ToolCatalog
{
    private static JsonObject LogicTool()
    {
        static JsonObject Endpoint() => Object(new JsonObject
        {
            ["component"] = Text("Exact Smart Component name or ID, mutually exclusive with robot."),
            ["robot"] = Robot(),
            ["member"] = Text("Exact signal or dynamic property name reported by the component, or mapped virtual I/O signal.")
        }, "member");
        return Tool("station_logic", "List, connect or disconnect station-level signal connections and property bindings. Wire component outputs to component inputs or mapped VC inputs, or a mapped VC output to a component input. Use kind=property to pass a sensor's detected object to an attacher. Edit wiring with simulation stopped. set_input and pulse drive a digital component input; value 0/1 is required for set_input.",
            Object(new JsonObject
            {
                ["action"] = Choice("list", "connect", "disconnect", "set_input", "pulse"),
                ["kind"] = Choice("signal", "property"), ["source"] = Endpoint(), ["target"] = Endpoint(),
                ["value"] = Integer(0, 1), ["offset"] = Integer(0, int.MaxValue), ["limit"] = Integer(1, 200)
            }), false);
    }
}
