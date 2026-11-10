using System.Text.Json.Nodes;

namespace RobotStudioOffline.Mcp;

public static partial class ToolCatalog
{
    public static readonly string[] Names = RobotStudioOffline.Core.BridgeOperations.All;

    public static JsonArray Create() =>
    [
        Tool("station", "Read the open RobotStudio station name/file, robots, tools, work objects, graphical targets and paths. targetDeclarations also lists station robtarget declarations and graphical work-object bindings; declarations without bindings require explicit workObject for pose queries. This operation is station-only. Real-controller access requires explicit owner permission.", Object(), true),
        Tool("robot_pose", "Read current station robot joints (degrees) and TCP (millimetres, quaternion w,x,y,z) in the specified tool/work-object frames. Robot may be omitted only when unambiguous.", Object(new JsonObject
        {
            ["robot"] = Robot(), ["tool"] = ToolName(), ["workObject"] = WorkObject()
        }, "tool", "workObject"), true),
        Tool("reach", "Solve station robot inverse kinematics without moving it. Supply tool and exactly one named target or explicit robtarget. Named targets prefer existing graphical station targets and their stored work-object binding. If no graphical target matches, a unique station robtarget declaration requires explicit workObject and is used without creating persistent data. Explicit robtarget also requires workObject. Returns reachability, targetSource, joint solution and failure reason. This checks a virtual model, not physical robot safety or the swept path.", Pose(false), true),
        Tool("jump", "Put the station robot at exactly one named target, robtarget or jointtarget (virtual only). Cartesian poses require tool. Explicit robtarget and named station declarations without a graphical target require workObject; graphical matches retain their stored binding. Use applyTo virtual to update the mapped, stopped VC as well. Default station updates only station graphics. Real-controller access requires explicit owner permission.", Pose(true, jump: true), false),
        Tool("collisions", "Read configured collision sets, or check one named target, robtarget or jointtarget by temporarily posing the station robot and restoring it. Cartesian pose rules are the same as jump. Pose checks are refused during simulation. Checks the given pose only; empty/disabled/incomplete sets do not establish safety.", Pose(true), false),
        Tool("simulation", "Read or start, stop, reset the open station's virtual simulation. This tool does not access real controllers. Start can execute station simulation behavior and virtual-controller programs.", Object(new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("state", "start", "stop", "reset") }
        }, "action"), false),
        Tool("screenshot", "Capture the active graphics view of the open station as a PNG image. Requires an active view belonging to that station; never captures other application windows or the desktop.", Object(), true),
        Tool("path_details", "Read station path instructions and their ordered arguments/references. Select an exact path name or Module/name; omit path to list details of all paths in the selected robot task. Does not execute or synchronize a path.", Object(new JsonObject
        {
            ["robot"] = Robot(), ["path"] = Text("Exact station path name or Module/name; omit for all paths in the selected task.")
        }), true),
        Tool("validate_targets", "Batch-check up to 100 poses for one station robot, returning a result for each. Supply exactly one target, robtarget or jointtarget per item. Top-level tool/workObject are defaults; items may override them. Named station robtarget declarations without graphical targets require workObject at item or top level; existing graphical target bindings take precedence. checkCollisions additionally checks configured collision sets at each pose and restores the station robot; it does not check swept motion or physical safety.", Object(new JsonObject
        {
            ["robot"] = Robot(), ["tool"] = ToolName(), ["workObject"] = WorkObject(),
            ["checkCollisions"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
            ["targets"] = Items(BatchPose(true, true), 100)
        }, "targets"), false),
        Tool("preview_targets", "Show up to 100 Cartesian target markers in the station graphics without creating RAPID data, synchronizing or moving the robot. Use action clear alone to remove this bridge's preview. For show, supply target or robtarget per item. Explicit robtarget and named station declarations without graphical targets require workObject at item or top level; existing graphical target bindings take precedence. No tool is needed to draw target frames.", Object(new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("show", "clear") },
            ["robot"] = Robot(), ["workObject"] = WorkObject(),
            ["targets"] = Items(BatchPose(false, false), 100)
        }, "action"), false),
        Tool("edit_targets", "Apply up to 50 explicit graphical station target edits, with station undo. Existing selections must have a graphical target; declaration-only targets are not edited through the pose-resolution fallback. Each change contains name and complete robtarget; create must be true to create a new target. New targets require workObject at change or top level; existing targets may use their stored work object. Does not synchronize to any controller or move the robot.", Object(new JsonObject
        {
            ["robot"] = Robot(), ["workObject"] = WorkObject(),
            ["changes"] = Items(Object(new JsonObject
            {
                ["name"] = Text("Exact target name or Module/name; no file paths."), ["robtarget"] = RobTarget(),
                ["workObject"] = WorkObject(), ["create"] = new JsonObject { ["type"] = "boolean", ["default"] = false }
            }, "name", "robtarget"), 50)
        }, "changes"), false),
        Tool("data_compare", "Read and compare explicitly selected target/tool/work-object data between the station and its already connected virtual controller. Does not synchronize or modify either side. Module/name disambiguates declarations. No controller addresses or file paths are accepted.", Object(new JsonObject
        {
            ["robot"] = Robot(), ["data"] = DataSelections()
        }, "data"), true),
        Tool("data_sync", "Explicitly synchronize only the selected target/tool/work-object declarations between the station and its owned virtual controller. direction to_station overwrites station data; to_virtual_controller overwrites selected virtual RAPID data. Review data_compare first. No whole-task/path sync, real-controller access or file paths. Inspect each result: a batch can partially complete.", Object(new JsonObject
        {
            ["robot"] = Robot(),
            ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("to_station", "to_virtual_controller") },
            ["data"] = DataSelections()
        }, "direction", "data"), false),
        Tool("io_read", "Read I/O values and metadata from the selected robot's already connected, station-owned virtual controller. Supply 1..100 distinct signal names, or omit names to list signals with offset/limit paging; names cannot be combined with offset or limit. May read while the virtual controller is running. Real-controller access requires explicit owner permission.", Object(new JsonObject
        {
            ["robot"] = Robot(),
            ["names"] = new JsonObject
            {
                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 100, ["uniqueItems"] = true,
                ["items"] = SignalName(), ["description"] = "Exact virtual-controller signal names; mutually exclusive with offset and limit."
            },
            ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = int.MaxValue, ["default"] = 0 },
            ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500, ["default"] = 100 }
        }), true),
        Tool("io_write", "Write one I/O value in the selected robot's station-owned virtual controller. Digital values are 0 or 1; group values are unsigned 32-bit integers; analog values must be within the signal's SDK-reported range. simulateInput:true opts into physical-input simulation for this request and is valid only for input signals; false uses ordinary configured controller access. This writes once without a persistent force or latch; the virtual program or I/O source may change the value. May write while the virtual controller is running. Real-controller access requires explicit owner permission.", Object(new JsonObject
        {
            ["robot"] = Robot(), ["name"] = SignalName(),
            ["value"] = new JsonObject { ["type"] = "number", ["description"] = "Finite numeric value; signal type and configured range are validated by the virtual-controller adapter." },
            ["simulateInput"] = SimulateInput()
        }, "name", "value"), false),
        Tool("io_pulse", "Request a native digital-signal pulse in the selected robot's station-owned virtual controller. durationMs defaults to 100 and must be 1..5000. simulateInput:true opts into physical-input simulation for this request and is valid only for digital inputs; false uses ordinary configured controller access. A successful response acknowledges pulse submission, not pulse completion. No persistent force or latch is applied. May pulse while the virtual controller is running. Real-controller access requires explicit owner permission.", Object(new JsonObject
        {
            ["robot"] = Robot(), ["name"] = SignalName(),
            ["durationMs"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 100 },
            ["simulateInput"] = SimulateInput()
        }, "name"), false),
        Tool("geometry_create", "Create a named solid in the open station. For a box supply size [x,y,z]. For a cylinder supply radius and height. For a sphere supply radius. All dimensions are positive millimetres. Position places the box's lower corner, cylinder's base center or sphere's center in station world coordinates. The cylinder extends along its local +Z axis.", GeometryCreate(), false),
        Tool("geometry_import", "Import a self-contained local CAD model into the open station as a named component. Use a fully qualified local Windows path to SAT (.sat), STEP (.step or .stp) or STL (.stl). Availability depends on the installed RobotStudio translator and license. Optional position and quaternion place the component origin in station world coordinates. Check the returned bounds in mm, especially for unitless STL files.", GeometryImport(), false),
        .. RegressionTools(),
        .. EngineeringTools(),
        .. BodyTools(),
        .. MotionTools(),
        .. SmartTools(),
        LogicTool()
    ];

    private static JsonObject Tool(string name, string description, JsonObject schema, bool readOnly) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = !readOnly,
            ["idempotentHint"] = readOnly,
            ["openWorldHint"] = false
        }
    };

    private static JsonObject GeometryPlacement() => new()
    {
        ["name"] = Text("Name for the new station geometry."),
        ["frame"] = Choice("world", "base"), ["robot"] = Robot(),
        ["position"] = Numbers(3, "World placement [x,y,z] in millimetres. Defaults to [0,0,0]."),
        ["quaternion"] = Numbers(4, "Unit quaternion for world orientation in ABB [w,x,y,z] order. Defaults to [1,0,0,0].")
    };

    private static JsonObject GeometryCreate()
    {
        var properties = GeometryPlacement();
        properties["shape"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("box", "cylinder", "sphere") };
        properties["size"] = new JsonObject
        {
            ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = PositiveDimension(),
            ["description"] = "Box width, depth and height along local X, Y and Z in millimetres. Valid only for box."
        };
        properties["radius"] = PositiveDimension();
        properties["height"] = PositiveDimension();
        return Object(properties, "name", "shape");
    }

    private static JsonObject PositiveDimension() => new() { ["type"] = "number", ["exclusiveMinimum"] = 0 };

    private static JsonObject GeometryImport()
    {
        var properties = GeometryPlacement();
        properties["file"] = new JsonObject
        {
            ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 32767,
            ["description"] = "Fully qualified drive-letter path to a local SAT, STEP or STL file on the RobotStudio computer."
        };
        return Object(properties, "name", "file");
    }

    private static JsonObject Pose(bool allowJoints, bool jump = false)
    {
        var properties = new JsonObject
        {
            ["robot"] = Robot(), ["tool"] = ToolName(), ["workObject"] = WorkObject(),
            ["target"] = Text("Exact graphical target name, station robtarget name, or Module/name. Graphical matches keep their existing work-object binding. Otherwise a unique station declaration requires explicit workObject. Mutually exclusive with robtarget and jointtarget."),
            ["robtarget"] = RobTarget()
        };
        if (jump) properties["applyTo"] = Choice("station", "virtual");
        if (allowJoints)
            properties["jointtarget"] = Object(new JsonObject
            {
                ["robotAxes"] = Numbers(6, "Six robot joint angles in degrees."),
                ["externalAxes"] = Numbers(6, "RAPID external axes [eax_a..eax_f]. 9E9 means unspecified. Unsupported moving external axes are refused.")
            }, "robotAxes");
        // Keep the schema root a plain object for Claude API compatibility. The shared
        // request parser enforces exactly-one-pose and conditional frame requirements.
        return Object(properties);
    }

    private static JsonObject RobTarget() => Object(new JsonObject
    {
        ["position"] = Numbers(3, "TCP position in the work-object object frame, millimetres."),
        ["quaternion"] = Numbers(4, "Unit quaternion in ABB scalar-first order [w,x,y,z]."),
        ["configuration"] = Numbers(4, "ABB [cf1,cf4,cf6,cfx] configuration.", integer: true),
        ["externalAxes"] = Numbers(6, "RAPID external axes [eax_a..eax_f]. 9E9 means unspecified. Unsupported moving external axes are refused.")
    }, "position", "quaternion", "configuration");

    private static JsonObject BatchPose(bool allowJoints, bool allowTool)
    {
        var schema = Pose(allowJoints);
        var properties = schema["properties"]!.AsObject();
        properties.Remove("robot");
        if (!allowTool) properties.Remove("tool");
        properties["id"] = Text("Optional identifier returned with this item's result.");
        return schema;
    }

    private static JsonObject DataSelections() => Items(Object(new JsonObject
    {
        ["name"] = Text("Exact declaration name or Module/name. Prefer Module/name; URLs, file paths and array expressions are not supported."),
        ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("target", "tool", "workObject") }
    }, "name", "kind"), 100);

    private static JsonObject Items(JsonObject item, int maximum) => new()
    {
        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = maximum, ["items"] = item
    };

    private static JsonObject Robot() => Text("Exact mechanism name from station; optional only when robot selection is unambiguous.");
    private static JsonObject ToolName() => Text("Exact station tool name, for example the gripper returned by station.");
    private static JsonObject WorkObject() => Text("Exact station work-object name, for example wobj0.");
    private static JsonObject SignalName() => Text("Exact I/O signal name in the station-owned virtual controller; no control characters.");
    private static JsonObject SimulateInput() => new()
    {
        ["type"] = "boolean", ["default"] = false,
        ["description"] = "Opt into physical-input simulation for this request only; valid only for inputs. False uses ordinary configured controller access."
    };
    private static JsonObject Text(string description) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256, ["description"] = description };
    private static JsonObject Numbers(int count, string description, bool integer = false) => new()
    {
        ["type"] = "array", ["items"] = new JsonObject { ["type"] = integer ? "integer" : "number" },
        ["minItems"] = count, ["maxItems"] = count, ["description"] = description
    };
    private static JsonObject Object(JsonObject? properties = null, params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = properties ?? new JsonObject(),
        ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["additionalProperties"] = false
    };
}
