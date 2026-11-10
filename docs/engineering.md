# Station and virtual-controller engineering

These four tools are available through MCP and the matching authenticated HTTP endpoints. A call with no action reads the selected resource. Lists accept `offset` and `limit` with defaults of 0 and 50. The maximum page size is 200. Use the names and IDs from a read when preparing an edit.

## Configuration and I/O

`configuration` starts at the selected robot’s mapped virtual controller. Call it with `{}` to list domains, supply a domain to list its types, then add a type to list instances. Add an instance to see its attributes, current values, SDK types, units, bounds and `canSet`. Use `robot` for stations with multiple mechanisms. The page is under `data.result`. An array attribute returns up to 64 values and indicates truncation. [ABB PC SDK configuration domain](https://developercenter.robotstudio.com/api/pcsdk/articles/Manual/Using-the-PC-SDK/Configuration-domain.html)

`io_project` uses the same workflow with the installed EIO domain already selected. For example, `{"type":"EIO_SIGNAL","limit":10}` returns signal instance names. Use `io_read` for a signal's live value. To change a stored attribute, read its instance, choose a field with `canSet:true` and send its current typed value as `expected`. For a signal named `diExample` whose Label is currently empty, the edit would be:

```json
{"action":"set","type":"EIO_SIGNAL","instance":"diExample","attribute":"Label","expected":"","value":"Part present"}
```

The bridge permits signal label and category edits and numeric scalar edits in selected MOC types (`ACC_DATA`, `ARM_LOAD`, `JOG_PARAMETERS`, `MOTION_PROCESS_MODE`). For simulated I/O with empty device and mapping, it also supports the digital default (0 or 1) and group signal width (1–32 bits). RobotWare applies its access and value checks. The bridge compares the installed value under VC write access, writes one field and reads it back through a newly opened instance. Check `verified`, `observed` and any warnings. Some parameters take effect after a VC restart. Restarting is an owner action.

ABB I/O Engineering stores additional project information in `.ioeprj`. The bridge exposes installed EIO data. To bring that configuration into a native project, use **I/O Project → Read/Write Configuration**, select the intended virtual controller, choose **Read**, then **Save As**. Edit or merge the native project in ABB’s editor. [ABB I/O Engineering manual, §§3.1.3–3.1.5](https://search.abb.com/library/Download.aspx?DocumentID=3HAC082346-001&LanguageCode=en&DocumentPartId=&Action=Launch)

## Physics and simulation setup

Call `simulation_setup` with `{}` for floor settings, gravity and the active simulation configuration. Read `section:"parts"` for material, motion control, collision geometry and conveyor surface velocity. Other paged sections are `configurations`, `savedStates`, `participants` and `eligibleParticipants`. The participant sections accept a configuration ID or unique name and default to the active configuration. [ABB physics properties](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.PhysicsSimulationProperties.html) · [ABB simulation configurations](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.SimulationConfiguration.html)

With simulation stopped, each change is one RobotStudio undo step. Only supply the properties to change.

| Action | Editable fields |
| --- | --- |
| `set_world` | `floorEnabled`, `floorHeightMm` and signed `gravityMps2` |
| `set_configuration` | `dataRecordingEnabled`, `virtualTimeMode` (`VirtualFreeRun` or `VirtualTimeSlice`), `initialState` (a saved-state name) |
| `set_participant` | `participant` (name or ID), `included` (boolean), optional `configuration` |
| `set_part` | `part` (name or ID), `motionControl` (`None`, `Static`, `Kinematic`, `Dynamic`), `useOriginalGeometry`, `densityKgPerM3`, `surfaceRoughness`, `restitution`, `damping`, `surfaceVelocityEnabled`, `surfaceSpeedMps`, `surfaceDirection` (unit XYZ vector for straight travel, replaces existing surface control points) |

You can also set `detectableBySensors` on a part so the pickup eye sees the cargo and ignores the belt. After reading the conveyor part, reuse its ID as `part`. A request to enable its surface at 0.3 metres per second looks like this:

```json
{"action":"set_part","part":"Part ID from the parts list","surfaceVelocityEnabled":true,"surfaceSpeedMps":0.3}
```

## SafeMove geometry drafts

Export a **geometry configuration XML** from Visual SafeMove. The bridge reads ABB’s `SimplifiedSafetyConfiguration` format, up to 2 MiB. The local RobotWare 8 RobotControl package supplies `utility\SafeMove2\SimplifiedSafetyControllerSchema.xsd`. `safemove` reads the export’s drive modules, robot, external axes, tool geometry and zones. Results identify the file as their source. Zone coordinates are **metres in controller world**. [ABB functional safety and SafeMove manual, §10.5](https://library.e.abb.com/public/853f4d70848649a7b4b441258aea8def/3HAC098577%20AM%20Functional%20safety%20and%20SafeMove%20RW%208-en.pdf)

```json
{"file":"C:\\Exports\\cell-geometry.xml","limit":10}
```

To draft a change, use an existing drive module and zone ID from the file. Set a height, the 4–24 XY vertices, or both. The edit checks the resulting heights and any new polygon, validates the document against ABB’s XSD and saves it under a **new sibling filename**. An existing output produces `safemove_output_exists`.

```json
{"action":"edit_zone_copy","file":"C:\\Exports\\cell-geometry.xml","outputName":"cell-geometry-review.xml","driveModuleId":1,"zoneId":1,"topM":1.8}
```

Open the draft in Visual SafeMove for review. Commissioning and safety validation remain in the ABB workflow. The bridge reports `appliedToController:false` on edits and `activeConfigurationVerified:false` and `safetyValidationVerified:false` on reads and edits.
