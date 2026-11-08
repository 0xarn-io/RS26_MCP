# Verification

The release was exercised on 64-bit Windows with RobotStudio 2026.3 and ABB assemblies `26.3.11844.1`. The build uses the vendor files from the local installation.

## Reproduce the offline checks

From the repository root, run `.\build.ps1`. The build runs the core and MCP suites, the headless recorder self-test, the supported Windows PowerShell binding check and validation of `OfflineStationBridge.rsaddin` against ABB’s installed schema. See `artifacts\build.log` for the results of your build.

The tests cover request parsing, JSON shapes, four-axis RAPID joint mapping, quaternion and frame math, TCP evidence and assertions, loopback authentication and shutdown, subprocess MCP initialization, tool discovery, and request forwarding. The binding check verifies that the host can load the expected SDK. These checks can run with RobotStudio closed.

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

For stations with multiple mechanisms, add the robot name to `$selection`. The first script checks station inventory, current pose and inverse kinematics. You can supply six known RAPID joint values with `-ExpectedJoints` and set the comparison tolerance using `-JointToleranceDegrees`. The development checks cover path details, station/VC comparisons, virtual I/O and simulation state. Add `-CheckGeometry` to exercise ray queries against station geometry. Both scripts print a summary. Use `-OutputPath` to save a full report locally.
