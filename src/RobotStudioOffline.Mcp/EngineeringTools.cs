using System.Text.Json.Nodes;

namespace RobotStudioOffline.Mcp;

public static partial class ToolCatalog
{
    private static IEnumerable<JsonNode> EngineeringTools()
    {
        yield return Tool("configuration",
            "Browse installed parameters on the station robot's mapped VC. Start with {}, then select domain, type and instance in order. To edit one supported attribute, use action=set with its exact selectors, value and expected current value from a read. Readback reports the result.",
            ParameterSchema(false), false);
        yield return Tool("io_project",
            "Browse installed I/O configuration on the mapped VC. Start with {}, then select type and instance. Set a supported EIO signal attribute with action=set, attribute, expected and value. This accesses the installed EIO domain. The native I/O Engineering project editor is separate.",
            ParameterSchema(true), false);

        var setup = PageFields();
        setup["action"] = Choice("read", "set_world", "set_configuration", "set_part", "set_participant");
        setup["section"] = Choice("overview", "configurations", "participants", "eligibleParticipants", "savedStates", "parts");
        setup["configuration"] = Text("Configuration name or ID from configurations. Defaults to the active configuration.");
        setup["part"] = Text("Part name or ID from the parts section, required for set_part.");
        setup["participant"] = Text("Exact station object name or ID, required for set_participant.");
        setup["included"] = Flag();
        setup["floorEnabled"] = Flag();
        setup["floorHeightMm"] = new JsonObject { ["type"] = "number", ["description"] = "Station floor height in millimetres." };
        setup["gravityMps2"] = new JsonObject { ["type"] = "number", ["description"] = "Signed SDK gravity in metres per second squared." };
        setup["dataRecordingEnabled"] = Flag();
        setup["virtualTimeMode"] = Choice("VirtualFreeRun", "VirtualTimeSlice");
        setup["initialState"] = Text("Exact station saved-state name.");
        setup["motionControl"] = Choice("None", "Static", "Kinematic", "Dynamic");
        setup["useOriginalGeometry"] = Flag();
        setup["densityKgPerM3"] = PositiveDimension();
        setup["surfaceRoughness"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 };
        setup["restitution"] = Range(0, 1);
        setup["damping"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 };
        setup["surfaceVelocityEnabled"] = Flag();
        setup["detectableBySensors"] = Flag();
        setup["surfaceDirection"] = Numbers(3, "Unit XYZ vector for straight surface travel. Replaces the part's existing surface control-point path.");
        setup["surfaceSpeedMps"] = new JsonObject { ["type"] = "number", ["description"] = "Speed along the part's configured surface direction, in metres per second." };
        yield return Tool("simulation_setup",
            "Read station physics and simulation setup. Default read section is overview. Page configurations, participants, savedStates or parts. With simulation stopped, set_world edits floor and gravity, set_configuration edits recording, time mode and initial state, set_participant includes or removes one object, and set_part edits motion control, collision geometry, material and conveyor surface velocity.",
            Object(setup), false);

        var safe = PageFields();
        safe["file"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32767, ["description"] = "Absolute local Windows path to an ABB SimplifiedSafetyConfiguration geometry XML export." };
        safe["action"] = Choice("inspect", "edit_zone_copy");
        safe["outputName"] = Text("New sibling .xml filename, required for edit_zone_copy.");
        safe["driveModuleId"] = Integer(1, 4);
        safe["zoneId"] = Integer(1, 16);
        safe["bottomM"] = Range(-30, 30);
        safe["topM"] = Range(-30, 30);
        safe["pointsM"] = new JsonObject
        {
            ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 24,
            ["items"] = new JsonObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = Range(-1000, 1000) }
        };
        yield return Tool("safemove",
            "Inspect an exported ABB SafeMove geometry XML. To revise one existing zone, use edit_zone_copy with outputName, driveModuleId, zoneId and bottomM, topM or pointsM (XY vertices). Units are metres. Creates a separate file for Visual SafeMove review. Active, commissioned and validated controller safety state are outside this tool.",
            Object(safe, "file"), false);
    }

    private static JsonObject ParameterSchema(bool eio)
    {
        var fields = PageFields();
        fields["robot"] = Robot();
        fields["action"] = Choice("read", "set");
        if (!eio) fields["domain"] = Text("Exact installed domain identifier.");
        fields["type"] = Text("Exact type identifier returned for the selected domain.");
        fields["instance"] = Text("Exact instance identifier returned for the selected type.");
        fields["attribute"] = Text("Exact writable attribute reported by the instance read.");
        foreach (var field in new[] { "value", "expected" })
        {
            fields[field] = new JsonObject
            {
                ["type"] = new JsonArray("boolean", "number", "string"), ["maxLength"] = 4096,
                ["description"] = field == "expected" ? "Current typed value from the last read." : "New value of the attribute's reported type."
            };
        }
        return Object(fields);
    }

    private static JsonObject PageFields() => new()
    {
        ["offset"] = Integer(0, int.MaxValue), ["limit"] = Integer(1, 200)
    };

    private static JsonObject Range(double min, double max) => new()
    {
        ["type"] = "number", ["minimum"] = min, ["maximum"] = max
    };
}
