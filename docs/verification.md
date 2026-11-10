# Verification

The release was exercised on 64-bit Windows with RobotStudio 2026.3 and ABB assemblies `26.3.11844.1`. The build uses the vendor files from the local installation.

## Reproduce the offline checks

From the repository root, run `.\build.ps1`. The build runs the core and MCP suites, the headless recorder self-test, the supported Windows PowerShell binding check and validation of `OfflineStationBridge.rsaddin` against ABB’s installed schema. See `artifacts\build.log` for the results of your build.

The tests cover request parsing, JSON shapes, four-axis RAPID joint mapping, quaternion and frame math, TCP evidence and assertions, loopback authentication and shutdown, subprocess MCP initialization, tool discovery, and request forwarding. The binding check verifies that the host can load the expected SDK. These checks can run with RobotStudio closed.

The engineering tests also exercise parameter selectors and scalar conversion, simulation edit requests, and SafeMove draft creation and rejection against the installed RobotWare 8 geometry schema when present.

## Read-only smoke tests in RobotStudio

Open a test station, load the add-in and check that the panel shows **Listening**. Call `station` to find a graphical target, its work object, and a tool in the same task. Supply those exact names in PowerShell from the repository root.

```powershell
$selection = @{
    Tool = Read-Host 'Tool name'
    WorkObject = Read-Host 'Work object name'
    Target = Read-Host 'Graphical target name'
}
.\smoke.ps1 @selection
.\smoke-development.ps1 @selection
```

With the engineering build loaded, add `-CheckEngineering` to the development smoke test to read VC domains, EIO types and station physics. Supply `-SafeMoveGeometryFile 'C:\Exports\cell-geometry.xml'` to inspect a local Visual SafeMove export. These options issue read requests only.

Add `-CheckCell` to inspect a part’s world pose, motion status, station wiring and the installed LineSensor template. To verify actual travel, grip, release and contact, follow the [conveyor example](conveyor.md) in a disposable station and compare `body_edit` state before and after each step.

### Live pickup and conveyor check

The 38-tool source build was exercised in a disposable RobotStudio station through the authenticated loopback endpoint. A native LineSensor supplied the detected Part to an Attacher. The graphical gripper lifted it 60 mm, moved it 130 mm horizontally and lowered it 60 mm. A native Detacher released it at the placement pose. The carrier then moved 80 mm with the workpiece attached. After release, moving the carrier another 40 mm left the workpiece in place.

In a separate contact check, the bridge restored the dropped workpiece to Dynamic physics over a fixed belt with surface speed 0.25 m/s. Its world position advanced 93.04 mm along the belt in a 0.384 s observation interval with no attachment. Cleanup restored the original geometry and collision-set IDs, removed the test wiring and Smart Components, and left the simulator stopped. Both checks used a graphical gripper with no controller or RAPID program.

For stations with multiple mechanisms, add the robot name to `$selection`. The first script checks station inventory, current pose and inverse kinematics. You can supply six known RAPID joint values with `-ExpectedJoints` and set the comparison tolerance using `-JointToleranceDegrees`. The development checks cover path details, station/VC comparisons, virtual I/O and simulation state. Add `-CheckGeometry` to exercise ray queries against station geometry. Both scripts print a summary. Use `-OutputPath` to save a full report locally.
