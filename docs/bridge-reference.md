# Bridge reference

## HTTP and MCP contract

Each of the 29 tools has a matching HTTP path. Send `POST http://127.0.0.1:18426/<name>` with `Authorization: Bearer <token>`, `Content-Type: application/json`, and a `Content-Length` body. Success is `{"ok":true,"data":{...}}` and failure is `{"ok":false,"error":{"code":"...","message":"..."}}`. MCP returns the same JSON in text content, setting `isError` for failures. Rendered views and screenshots use MCP image content. A valid reach query with `reachable:false` is a successful query.

| Endpoint/tool | Arguments | Result |
| --- | --- | --- |
| `station` | `{}` | Name/file, robot mechanism/model/task, tools, work objects, graphical targets, robtarget declarations and their bindings, paths/instruction names, station virtual controllers and units |
| `robot_pose` | `robot?`, `tool`, `workObject` | Current joints in degrees, requested-tool TCP in the requested work object, and world TCP |
| `reach` | `robot?`, `tool`, and `target` or `robtarget`; explicit poses and declarations without graphical targets require `workObject` | `reachable`, `targetSource`, joint solution when available, requested configuration, failure reason/detail and possible causes; alternative joints for configuration mismatch |
| `jump` | Same pose selectors, or `jointtarget`, optional `applyTo` | Instant graphical placement by default. `applyTo:"virtual"` also updates the mapped virtual mechanical unit and requires stopped RAPID and station simulation. |
| `collisions` | `{}` for current state, or same pose selectors as `jump` | Configured sets' active/state/near-miss distance/groups; optionally checks one pose and restores the original pose |
| `simulation` | `{"action":"state\|start\|stop\|reset"}` | Simulator state/configuration/time; control applies only to the active station's virtual simulation |
| `screenshot` | `{}` | PNG of the active station graphics view as base64 JSON over HTTP, image content over MCP |
| `path_details` | `robot?`, `path?` | Ordered instructions with every argument and resolved target/tool/work object, speed and zone expressions |
| `validate_targets` | `robot?`, `tool?`, `workObject?`, `checkCollisions?`, `targets` | Up to 100 independent pose checks with per-item reach/collision results and explicit partial/aborted reporting |
| `preview_targets` | `action: show` plus `robot?`, `workObject?`, `targets`; or just `action: clear` | Up to 100 temporary labeled TCP frames and connecting lines; clear removes only this bridge's preview |
| `edit_targets` | `robot?`, `workObject?`, `changes` | Up to 50 explicitly selected graphical target creations/edits in one undo step |
| `data_compare` | `robot?`, `data` | Compare up to 100 selected target/tool/work-object values between station and its mapped VC |
| `data_sync` | `robot?`, `data`, `direction: to_station` or `to_virtual_controller` | Explicit selected-data synchronization, with per-item results; requires stopped simulation and VC RAPID |
| `io_read` | `robot?`, optional `names`; otherwise `offset?`, `limit?` | Read selected signals or a page of the mapped VC's digital/analog/group I/O |
| `io_write` | `robot?`, `name`, `value`, `simulateInput?` | Set one VC signal, returning its prior value and observed readback |
| `io_pulse` | `robot?`, `name`, `durationMs?`, `simulateInput?` | Request a digital VC pulse, default 100 ms, bounded to 1–5000 ms |
| `geometry_create` | `name`, `shape`, dimensions, optional `position` and `quaternion` | Add a box, cylinder or sphere at the station root and return its world bounds in mm |
| `geometry_import` | `name`, `file`, optional `position` and `quaternion` | Import a local SAT, STEP or STL model into the open station and return its world bounds in mm |
| `geometry` | Optional `match`, `offset`, `limit` | Components and bodies, IDs, parent and available world bounds |
| `collision_set` | `name`, `first`, `second`, optional `nearMissMm` | Create an active set from two groups of components |
| `raycast` | `tool`, `origin`, `direction`, `maxDistance`, optional `robot`, `parts`, `exclude` | Nearest graphical part hit on a tool-frame ray, distance in mm and hit point in world |
| `simulation_speed` | Optional `factor` up to 10 and `stepMs` from 1 to 100 | Read or change the desired virtual/wall time ratio |
| `rapid_load` | `files`, optional `task`, `robot` | Check files, stop the selected running virtual task, replace modules in order, return partial results and installed RAPID verdict |
| `rapid_check` | Optional `files`, `task`, `robot` | Source syntax with files, or installed virtual-task check without files |
| `rapid_diff` | `files`, optional `task`, `robot`, `pers`, `includeReal` | Compare local/VC module text and current persistent values, with optional owner-approved real comparison |
| `rapid_sync_real` | `files`, `task`, optional `robot` | Replace modules on the stopped real controller after clone verification and local owner approval |
| `tcp_record` | `action` of `start`, `status`, `mark` or `stop` | Per-run evidence and exported files |
| `run_review` | `runId`, optional `assertions`, `export` | Sample witnesses, verdicts, closest points and contact by geometry pair |
| `run_view` | `runId`, `view` of `top` or `side`, optional `assertions` | Rendered station PNG with trace and zones, also saved alongside the run |

Names are exact and case-sensitive. Omit `robot` only when one robot exists. Read names from `station`; use `Module/name` to disambiguate data. Inventory `targets` contains graphical station targets; `targetDeclarations` also lists station-task robtarget declarations and their graphical bindings. Each declaration includes `qualifiedName`, raw `pose` with `poseFrame:"workObject_local"`, configuration/external axes, and `graphicalTargets` with target names and qualified work-object names. `requiresExplicitWorkObject:true` identifies declarations without a graphical binding; their coordinates do not identify a world pose by themselves.

Named pose and preview requests resolve a station `RsTarget` name, robtarget name or module-qualified robtarget name in this order:

- Existing graphical targets take precedence. An optional `workObject` selects among their stored bindings. A different work object is an error; the bridge does not reinterpret their coordinates or fall back to the declaration to bypass that mismatch.
- If no graphical target matches the name, a unique station `RsRobTarget` declaration can be used with an **explicit `workObject`**. The bridge copies its numeric pose, configuration and external-axis values into detached request data, then binds that copy to the selected work object. It does not change the source declaration or add a target or declaration to the station. It never assumes `wobj0`. Ambiguous declarations require `Module/name`.

Declarations that exist only in controller RAPID are unavailable to pose resolution until the owner explicitly synchronizes them into the station. Cartesian reach/jump/collision checks require an explicit tool; preview needs no tool. `reach.targetSource` reports `graphical_target`, `station_declaration` or `request_pose`.

Cartesian request example (numbers are illustrative, not an instruction to move a physical robot):

```json
{
  "robot": "robot mechanism name from station",
  "tool": "gripper tool name from station",
  "workObject": "work object name from station",
  "robtarget": {
    "position": [500, 0, 800],
    "quaternion": [1, 0, 0, 0],
    "configuration": [1, 0, 0, 0],
    "externalAxes": [9000000000, 9000000000, 9000000000, 9000000000, 9000000000, 9000000000]
  }
}
```

- Position: **millimetres in the work object's object frame**. SDK metre conversion happens only at the boundary.
- Quaternion: unit **`[w,x,y,z]`**, ABB `q1,q2,q3,q4`, norm tolerance `1e-6`.
- Configuration: integer **`[cf1,cf4,cf6,cfx]`**. The exact IK solve respects it; a relaxed solve only diagnoses an alternative.
- Jointtarget: `{"jointtarget":{"robotAxes":[10,15,-20,0,0,5]}}`, in **degrees**, in six-field RAPID axis order. The SDK's `GetActiveJoints()` map converts to/from the actual mechanism's compact joint vector. Responses include `activeJointIndices`; inactive RAPID fields are zero, and nonzero requests for inactive fields are refused.
- Omitted external axes mean unspecified. Only omitted or six `9E9` sentinel values are supported. Active external axes, moving work objects, stationary tools and mechanisms outside one to six rotational axes with a valid SDK axis map are explicitly refused. They are not silently approximated.
- Inventory and pose operations use station data. `data_compare` and `data_sync` additionally read explicitly selected RAPID declarations from the mapped station VC.

Reach uses the SDK solver without moving the station robot. `configuration` means another configuration works; `joint_limit` means a returned joint solution fails the mechanism's limits. If the SDK returns no solution for both exact and relaxed configuration, the reason is **`out_of_reach_or_joint_limit`**, with both possible causes. The public API does not provide a reliable finer distinction; the bridge does not invent one or relax the model's limits.

The default `jump` changes the graphical mechanism. With `applyTo:"virtual"`, the add-in verifies the mapped local VC, requires stopped execution, then invokes ABB’s controller-updating joint setter. Both variants position the virtual robot without executing a RAPID move. A simulation configuration that restores a saved initial state on start can replace this pose. Collision probes restore the original graphical joints. Their coverage is limited to the active sets and the queried pose.

Simulation `reset` means stop simulation and restore the active configuration's named saved initial state. It refuses reset without a restorable initial state. It never restarts RobotStudio or a controller. Simulation executes the owner's station content and SmartComponents; this bridge does not audit third-party station scripts/plugins or replace their network controls.

Screenshot uses the SDK's inexpensive `GraphicControl.ScreenShot` API on the main thread. It requires an active graphics view belonging to the open station and returns a bounded-size PNG. It captures the station view, without desktop capture, clipboard access or an image file.

## Regression run

1. Call `rapid_load` with the local files and task, for example `{"task":"T_ROB1","files":["C:\\Robot\\Data.modx","C:\\Robot\\Main.modx"]}`. Check the returned verdict and per-module results. Successfully replaced modules remain installed after a later module fails. The task stays stopped. `rapid_check` can also inspect the currently installed task. A definite syntax error skips the entire batch before the stop. `sourceChecks[].errors[]` identifies the submitted file’s one-based line and UTF-16 column. `installedTaskCheck.errors[]` refers to the program actually installed. A RobotWare rejection without a reported position returns a null location.
2. Find the robot, gripper, work object and fixture IDs with `station` and `geometry`. For an illustrative rectangular fixture, `geometry_create` accepts `{"name":"Fixture block","shape":"box","frame":"base","position":[900,-500,1400],"size":[100,1000,80]}`. These illustrative millimetres locate the lower corner in the mapped robot base. Use measured values for the cell. A held item needs its own station geometry and attachment.
3. Set the starting joints with `jump`, for example `{"applyTo":"virtual","jointtarget":{"robotAxes":[10,15,-20,0,0,5]}}`. Set `simulation_speed` with `{"factor":5}` or `{"factor":10,"stepMs":10}` for a regression. ABB applies the ratio to station simulation. Achieved throughput depends on the virtual controller, simulation time step and host workload.
4. Call `tcp_record` with the configuration below. Save the returned `runId`, then launch the station simulation and run the case. `tcp_record` arms on a stopped simulator or records from the current instant on a running one. It samples the selected tool and work object on simulator ticks. For stand-in runs outside the station simulator, see the headless workflow.
5. Stop the station simulation at the end of the case or call `tcp_record` with `{"action":"stop","runId":"<id>"}`. A normal simulator stop exports automatically. Explicit stop also returns the completed bundle paths. `tcp_record status` reports sample count, observed Hz, maximum gap and dropped samples.
6. Call `run_review` to evaluate another rule set, or read the exported verdict. `run_view` with `{"runId":"<id>","view":"top"}` and then `"side"` returns PNGs with the recorded path and assertion zones on the current station geometry. Open the same saved station for both views. Blue marks TCP samples, red marks forbidden boxes, green marks waypoints and yellow marks height regions.

```json
{
  "action": "start",
  "tool": "Demo/tProbe",
  "workObject": "wobj0",
  "rateHz": 50,
  "phaseSignal": "ExamplePhase",
  "liveFrameSignal": "ExampleFrame",
  "command": {"case":"fixture-path","speedFactor":5},
  "movingObjects": ["$robot", "Gripper CAD", "Held item"],
  "obstacles": ["Fixture block"],
  "assertions": [
    {"id":"keep-clear","type":"forbidden_box","frame":"base","min":[900,-500,1400],"max":[1000,500,1480]},
    {"id":"approach","type":"waypoint","frame":"workObject","position":[700,0,1500],"tolerance":5},
    {"id":"clearance","type":"min_height","frame":"base","minXY":[500,-300],"maxXY":[900,300],"minZ":1200}
  ]
}
```

The sample uses synthetic names and coordinates. Supply your own names from `station`, `geometry` and `io_read`. Omit `phaseSignal` and `liveFrameSignal` when the run has no matching signals. Each chosen moving object is checked against each obstacle at acquired samples. If only obstacles are supplied, the moving object defaults to the selected robot. Add tool CAD and the carried item explicitly. Component pairs use the SDK collision check. A body-level contact result uses zero graphical distance as its indicator. The ray sensor uses ABB’s nearest graphical intersection for each selected part. For instance, use `raycast` with `{"tool":"Demo/tProbe","origin":[0,0,20],"direction":[0,0,1],"maxDistance":2000,"parts":["Fixture block"]}` for a tool-mounted beam aimed at a modeled fixture.

Runs are saved under `%LOCALAPPDATA%\RobotStudioOfflineBridge\runs\<runId>\`. Link the folder or `review.json` in the review log. `run.json` includes the command, acquisition settings, actual samples and events. `samples.csv` includes time, phase, live frame, six RAPID axes and the TCP in world, robot base and named work object. `events.csv` includes phase and pointer changes plus delivered VC event-log messages, source timestamps and the pointer observed when the bridge received the event. `clearances.csv` holds pairwise measurements. `review.json` contains assertion witnesses and sampled minima. Rendering adds `top.png` or `side.png`.

Every assertion applies to acquired samples. A witnessed violation can fail a run with gaps. Missing coverage, an unobserved region or a truncated recording can yield `insufficient`. Compare the observed cadence with the requested 20–50 Hz when reviewing a fine point. Moving fixtures and station geometry are rendered at review time. The archive retains pair distances and closest-point evidence from acquisition time. [ABB simulator timing](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.Simulator.html) · [ABB ray intersections](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.Part.html)

A completed bundle can also be reviewed with RobotStudio closed. From the extracted release folder, pipe a single-line `run_review` JSON object to the wrapper.

```powershell
'{"runId":"<32-hex-run-id>","assertions":[{"id":"approach","type":"waypoint","frame":"base","position":[700,0,1500],"tolerance":5}]}' | dotnet .\mcp\RobotStudioOffline.Mcp.dll --review-run
```

## Dedicated headless clone

The optional lifecycle helper starts a prepared local virtual controller. Set `RS_BRIDGE_CLONE_NAME` and `RS_BRIDGE_CLONE_ID` to its local system name and GUID in the shell that runs the launcher and recorder. Use the ZIP’s `headless` directory in 64-bit Windows PowerShell 5.1. Launch your case after the recorder prints its run ID. Ctrl+C finalizes the capture.

```powershell
.\headless-vc.ps1 status
.\headless-vc.ps1 start
.\recorder\RobotStudioOffline.HeadlessRecorder.exe --tool tool0 --work-object wobj0 --rate-hz 50 --seconds 120 --command "stand-in case 1"
.\headless-vc.ps1 stop
```

The headless recording uses wall time, controller world/base, the selected tool and work object, six RAPID joint fields, optional phase and live-frame signals, and observed program pointers. Readbacks within a sample are sequential. Review the exported bundle with `--review-run` as above. CAD distances, ray casting, station views and `simulation_speed` use RobotStudio’s station simulator. Controller-only runs can exercise RAPID and virtual I/O with RobotStudio closed. The lifecycle helper pins the prepared clone and installed SDK build and controls only an instance it started. [Headless host setup and ownership](../tools/headless/README.md)

To check module source with RobotStudio closed, run this from the extracted release root using fully qualified source filenames. The wrapper calls the installed RobotStudio editor parser and returns an exit code of 0 for clean syntax, 2 for syntax errors and 3 for unresolved checks. Task-level symbol resolution and execution checks run on the VC through `rapid_load` or `rapid_check`.

```powershell
dotnet .\mcp\RobotStudioOffline.Mcp.dll --check-files 'C:\Robot\Data.modx' 'C:\Robot\Main.modx'
```

## Clone and real module comparison

Use `rapid_diff` with the same absolute module filenames used for `rapid_load`. Include `pers:["Data/variableName"]` for selected persistent values. The controller can serialize a module differently from the local file, and an initialized PERS can change while the task runs. The result identifies module text differences and reports live PERS separately.

For an owner-approved hardware operation, set `RS_BRIDGE_REAL_ADDRESS` in RobotStudio’s environment to the intended physical controller’s IPv4 address before launching RobotStudio, then connect the intended real controller in RobotStudio yourself. `rapid_diff` with `includeReal:true` asks before exporting any real module or reading PERS. Export uses a temporary controller HOME subdirectory. `rapid_sync_real` verifies the source against the installed clone modules, shows the owner the task, filenames and hashes, then loads only the approved snapshots onto an already stopped real task. The bridge rechecks controller type, configured address and approved system ID, uses existing RobotStudio access, and reports partial writes and cleanup warnings. The real task remains stopped.

## Development workflow

Start with `path_details {}`. It returns raw instruction arguments, including speed/zone expressions and disabled optional arguments, with resolved station data where available. Expressions are not evaluated, and the SDK's cached reachability is labeled as cached.

Batch reach checks use one tool/work-object default, with per-item overrides:

```json
{"tool":"Demo/tProbe","workObject":"wobj0","checkCollisions":false,"targets":[{"id":"home","target":"pInspect"},{"id":"approach","target":"pStandby"}]}
```

Each item accepts `target`, `robtarget` or `jointtarget`; raw poses use the same shapes as `reach`/`jump`. A named station declaration without a graphical target needs `workObject` at item or top level, using the resolution rules above. `checkCollisions:true` temporarily poses the station robot and restores it after each check. Batch results contain per-item errors and skipped IDs if the batch aborts. Obstacle geometry and collision-set settings are sampled at each probe; do not edit the scene during validation. No swept path is checked.

Preview named or proposed targets before editing:

```json
{"action":"show","targets":[{"id":"home","target":"pInspect"}]}
```

For a proposed pose replace `target` with `robtarget` and specify `workObject`. A named station declaration without a graphical target also requires `workObject` at item or top level. Previewing requires no tool, does not move the robot, and does not prove reachability. `{"action":"clear"}` clears only this bridge's graphics. The connecting lines are visual guides, not simulated trajectories.

`edit_targets` accepts `changes:[{"name":"Demo/pNew","create":true,"workObject":"wobj0","robtarget":{...}}]`. Each `robtarget` is the complete position/quaternion/configuration object shown above. Omit `create` or set it to false to edit an existing **graphical** target. A declaration without a graphical target cannot be edited through the detached pose-resolution fallback; `create:true` does not overwrite an existing declaration. The whole selection is validated before writes and applied in one undo step. Existing path references are retained; reassigning a referenced target's work object is refused. Preview and edit require stopped simulation. Edits do not save or synchronize the station, and do not automatically check reach or collisions.

Compare selected station and mapped virtual-controller data:

```json
{"data":[{"name":"Demo/pInspect","kind":"target"},{"name":"Demo/tProbe","kind":"tool"}]}
```

Kinds are `target`, `tool`, and `workObject`. Results include both values, difference paths, equality tolerances and missing/error status. Use `Module/name` for declarations absent from the station; built-in declarations without a module and arrays are not synchronized. Comparison includes tool loads and work-object frames but excludes declaration storage/locality and graphical target/work-object associations. An equal robtarget value does not establish a graphical binding, a work-object choice or reachability. The station VC must already be connected in RobotStudio.

To synchronize, pass the same selection to `data_sync` with an explicit `direction` of `to_station` or `to_virtual_controller`. It prevalidates all selected IDs, requires stopped simulation and stopped VC RAPID, then synchronizes only those IDs. The batch stops on the first failure and reports partial results; completed VC writes are not undone by station Undo. Compare again before retrying an interrupted request. Neither comparison nor synchronization discovers controllers or opens a connection to a supplied address.

See the [verification guide](verification.md) for read-only smoke checks.

## Create or import station geometry

Use `geometry` to find existing station components, `geometry_create` to add a primitive from dimensions, and `geometry_import` to bring in an existing CAD file.

Both tools insert one named component at the station root when simulation is stopped. Each successful call appears as one RobotStudio Undo step. Use a distinct component name. The response gives the component ID, its world pose and its axis-aligned `worldBounds` in millimetres. You can save the station in RobotStudio after reviewing the result.

`geometry_create` takes positive dimensions in mm. A box uses `size:[x,y,z]` measured from its lower local corner. A cylinder uses `radius` and `height` measured from its base centre along local +Z. A sphere uses `radius` around its centre. Both geometry tools accept optional `position:[x,y,z]` in millimetres in `frame:"world"` (default) or the selected `frame:"base"` and a unit `quaternion:[w,x,y,z]`. The defaults place the component origin at world zero with identity rotation. [ABB solid constructors](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.Body.html)

For a live check in a scratch station, call `geometry_create` with this body. The expected bounds are approximately `min:[200,30,40]` and `max:[260,100,120]`. Use RobotStudio Undo to remove the block.

```json
{"name":"Example block","shape":"box","size":[60,70,80],"position":[200,30,40]}
```

`geometry_import` accepts a self-contained ACIS SAT, STEP (`.step` or `.stp`), or STL file on a local Windows drive. Supply the path to the source model. The bridge checks each path component and rejects mapped network drives, links, offline placeholders, directories and empty files. RobotStudio handles source file units and translation according to its installed importer and license. For unitless STL files, check the returned bounds against the intended dimensions. Use self-contained exports for offline work because the native translator controls resolution of dependencies inside a CAD file.

For an import check, this sample uses the SAT file shipped with RobotStudio. Inspect the imported component and its bounds, then Undo. The MCP wrapper allows up to ten minutes for a native import. Wait for it to finish before performing other modelling operations in RobotStudio. A client timeout can occur while RobotStudio is still importing. Check the station tree for the requested name before retrying. [ABB import API](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.GraphicComponent.html)

```json
{"name":"Imported training box","file":"C:\\Program Files (x86)\\ABB\\RobotStudio 2026\\ABB Library\\Training Objects\\Box.sat","position":[200,30,40]}
```

## Virtual-controller I/O

`io_read {}` lists a page of the selected robot's mapped virtual-controller signals. Use `{"offset":100,"limit":100}` for another page (limit 1–500), or `{"names":["diPartPresent","doGrip"]}` to read 1–100 explicit names. Named selection cannot be combined with paging. Results include signal type, value, quality and simulated status. Group values retain unsigned 32-bit precision.

Example requests (replace these illustrative names with names returned by `io_read`):

```json
{"name":"diPartPresent","value":1,"simulateInput":true}
```

Send that body to `io_write` to simulate a physical input on the virtual controller. Digital values must be 0 or 1, group values unsigned 32-bit integers within the signal range, and analog values finite and within the configured range. Omit `simulateInput` for an ordinary write subject to the signal's access rules; `simulateInput:true` is accepted only for inputs. This uses the SDK's `InputAsPhysical` option for that operation, then restores the wrapper option; it does not change I/O configuration or set the controller's simulated-state flag.

```json
{"name":"diPartPresent","durationMs":100,"simulateInput":true}
```

Send that body to `io_pulse` for a digital pulse. Its response acknowledges the native request with `pulseRequested:true, completed:false`; it does not wait for or verify pulse completion. These tools can be used while the virtual program is running so an agent can drive handshakes and observe responses. They do not stop or start RAPID. Writes and pulses may trigger virtual program behavior, including virtual motion. Values can be overwritten by RAPID, simulated devices or other clients; **these operations are not persistent force/release**. Inspect the returned outcome and use `io_read` to check later state. Never assume a failed or timed-out write did nothing.

The connection is selected from the open station and checked as its existing local virtual controller. Real-controller access requires explicit owner permission. I/O on station SmartComponents is a separate API and is not included in these three tools.

## Main thread and local boundary

All station reads and writes are posted through `RobotStudioAPI.RunOnMainThread`, preserving RobotStudio's synchronization context through awaited SDK calls. A semaphore covers each complete asynchronous operation. Requests canceled while queued cannot execute later. Started SDK calls are awaited to completion rather than abandoned in the background. A client-side timeout/disconnect cannot undo a write already started; inspect state before retrying.

The transport binds an IPv4 socket to the literal `127.0.0.1` only, checks the peer and exact Host header, rejects browser Origin headers, and requires a random bearer token. It accepts one bounded HTTP/1.1 JSON POST per connection, with 8 KiB headers, 64 KiB request bodies, eight concurrent connections and bounded receive/queue time. No HTTP.sys URL ACL or firewall opening is needed. The wrapper disables proxies, redirects and cookies; its destination host cannot be configured. Controller/address arguments require owner permission before any access; unknown JSON properties are rejected before SDK execution. Data comparison uses the public PC SDK through RobotStudio's existing connection to the selected station VC, with identity/type/virtual/loopback checks. There is no network discovery, RWS or address-forwarding endpoint.
