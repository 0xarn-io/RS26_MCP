# RobotStudio 2026 SDK evidence and adapter behavior

Inspected on 2026-11-06 without initializing RobotStudio from the inspection process, connecting to a controller, loading an add-in or changing the owner's RobotStudio session. Public signatures were checked by reflection against the installed `Bin` assemblies (26.3.11844.1), then compiled against those assemblies.

## Target runtime and discovery

The installed `Bin/RobotStudio.runtimeconfig.json` targets .NET 10 and Windows Desktop. ABB's [SDK introduction](https://developercenter.robotstudio.com/api/robotstudio/index.html) identifies .NET 10 as the 2026 runtime and states that older .NET Framework add-ins require migration. The add-in therefore targets `net10.0-windows`; it does not reference `Bin-net48`.

ABB's [add-in creation instructions](https://developercenter.robotstudio.com/api/robotstudio/articles/How-To/Add-Ins/Creating-a-RobotStudio-Add-In.html) describe the `.rsaddin` XML declaration; its current C# example spells the public static entry point `AddinMain` (lowercase i), as used here. [Loading of Add-Ins](https://developercenter.robotstudio.com/api/robotstudio/articles/Concepts/AddinConcept/LoadingAdd-In.html) documents these discovery directories:

- `%ProgramFiles(X86)%\Common Files\ABB\RobotStudio\Addins`
- `%ProgramFiles(X86)%\ABB\RobotStudio 2026\Bin\Addins`

ABB ships `.rsaddin` examples under `Bin/Addins` and in packaged ABB add-ins. The [installation guide](../README.md#install-on-windows) lists the owner's discovery and loading steps.

Discovery correction verified on 2026-11-06: read-only inspection of the installed `RobotStudio.dll`, `AddinManager.ScanAddins`, showed that root manifests are globbed as `*.rsaddin`, but each immediate subfolder is checked specifically for `<subfolder-name>.rsaddin` (with a same-name DLL fallback). It does not recursively glob arbitrary manifest names. Thus the installed path must be `Addins\OfflineStationBridge\OfflineStationBridge.rsaddin`. The initial packaging named the file `RobotStudioOffline.rsaddin`, which was invisible inside `OfflineStationBridge`; the source and build output now use the matching name. The manifest's referenced assembly remains `RobotStudioOffline.Addin.dll`.

## Station kinematics

[Mechanism](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.Mechanism.html) exposes async target inverse kinematics, with an explicit configuration flag. The bridge calls it first with the target configuration, then without that constraint if necessary. It validates each returned vector with `InsideLimits`. There is no robot movement during `reach`. The same API documents synchronous `SetJointValues` as changing station joints without updating the virtual controller, which the graphical `jump` and temporary collision probes use. An explicit `jump` with `applyTo:"virtual"` uses `SetJointValuesAsync` with a controller update after verifying the mapped virtual unit and stopped execution.

The public inverse-kinematics return value is a joint array or null, not a detailed failure enum. Therefore:

- `joint_limit` is used when a returned solution fails the mechanism's limit test.
- `configuration` is used when the requested configuration fails but an alternative succeeds.
- `out_of_reach_or_joint_limit` preserves the uncertainty when both calls return no solution. The bridge does not alter limits or claim an unsupported precise diagnosis.

[Flange.GlobalMatrix](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.Flange.html) is the current wrist transform in station world coordinates. [RsToolData.Frame](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.RsToolData.html) holds the tool offset in wrist coordinates. `robot_pose` composes these with the tested pure `FrameMath` functions and expresses the result relative to `RsWorkObject.ObjectFrame.GlobalMatrix`. It does not require selecting the requested tool as RobotStudio's active tool. API metres/radians become wire-format millimetres/degrees; quaternion order is w,x,y,z.

Pose operations accept one to six active rotational axes, a robot-held tool, a stationary programmed work object and unspecified external axes. TCP reporting requires one wrist flange. Unsupported cases fail explicitly. A named target is resolved within the selected robot's station task; ambiguous names require `Module/name` and a work object. [RsRobTarget](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.RsRobTarget.html) distinguishes RAPID external-axis units from SI; the wire inventory uses RAPID units. Request targets are detached SDK objects and are never added to the station's data declarations.

`Mechanism.GetActiveJoints()` supplies the one-based positions corresponding to the compact SDK joint vector. The bridge uses this map to translate between compact radians and the six RAPID joint fields in degrees. The response exposes `activeJointIndices` and fills inactive RAPID fields with zero. Input validation rejects nonzero values in inactive fields. A change to the active-axis map invalidates an outstanding IK calculation. [ABB RobotAxisValues](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.RobotAxisValues.html)

## Collision and simulation scope

[CollisionDetector](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.CollisionDetector.html) checks station collision sets using triangulated graphics (or bounding boxes if the owner enabled `FastCheck`). Results are approximate. A pose probe requires stopped simulation, sets station joints temporarily, samples the configured sets, restores original joints in `finally`, rolls back its undo step, and recomputes the original collision state. It does not test the movement between two poses.

The installed `Simulator` exposes `StartAsync`, `StopAsync` and `ResumeAsync`; there is no public `Reset` method. `simulation reset` stops simulation and restores the named saved initial state already selected by the owner in the active simulation configuration. A missing/non-restorable initial state is an explicit error. This does not restart RobotStudio or a controller. [ABB's simulation concept](https://developercenter.robotstudio.com/api/robotstudio/articles/Concepts/Simulation.html) describes simulation as progressing station objects and virtual controllers. The adapter also verifies that the active configuration and simulated objects belong to the open station and every participating `VirtualController` is in that station's `VirtualControllers` collection.

Real-controller access requires explicit owner permission. The parser reports `real_controller_permission_required` without attempting access when a real-controller destination is supplied. There is no discovery, RWS or arbitrary-address forwarding. The selected-data feature uses the public PC SDK through RobotStudio's existing mapped station-VC connection; it checks station ownership, identity, `StationVC` type, `IsStationController`, then `IsVirtual` and loopback before reads. Existing station simulation content is the owner's content: the bridge is not a sandbox for third-party SmartComponents or RAPID programs configured to perform network I/O.

The optional `screenshot` operation uses the public [GraphicControl.ScreenShot(width, height)](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.Forms.GraphicControl.html) API. It requires an active graphics view whose root is the open station, preserves its aspect ratio within 1280 by 720 pixels, and returns PNG bytes as base64. It captures only that graphics view, not the desktop or other windows.

## Virtual-controller I/O

The I/O adapter uses the same verified, existing RobotStudio station-VC connection as data comparison. [Signal](https://developercenter.robotstudio.com/api/pcsdk/api/ABB.Robotics.Controllers.IOSystemDomain.Signal.html) exposes value access and `InputAsPhysical`, which ABB documents for simulating physical inputs on a VC. The flag belongs to the signal wrapper and is restored after each operation. [SignalState.Simulated](https://developercenter.robotstudio.com/api/pcsdk/api/ABB.Robotics.Controllers.IOSystemDomain.SignalState.html) is read-only; the bridge does not claim to toggle it or provide persistent force/release.

Digital pulses use [DigitalSignal.Pulse(int)](https://developercenter.robotstudio.com/api/pcsdk/api/ABB.Robotics.Controllers.IOSystemDomain.DigitalSignal.html), whose duration is milliseconds. Installed implementation inspection confirmed the duration and input-simulation flag are submitted in the native pulse request. The response acknowledges submission, not pulse completion. No main-thread sleep or bridge timer is used to hold/reset the signal. [GroupSignal](https://developercenter.robotstudio.com/api/pcsdk/api/ABB.Robotics.Controllers.IOSystemDomain.GroupSignal.html) uses exact unsigned `GroupValue`, `GroupMinValue` and `GroupMaxValue` rather than the legacy floating-point properties. Detailed API evidence and runtime limitations are in [I/O notes](notes-io.md).

Virtual I/O remains available during program execution. Reads and writes use the virtual controller’s signal configuration.

## Verification boundary

The dockable bridge panel uses public `ToolWindow`, ribbon and visual-style APIs from `ABB.Robotics.RobotStudio.Environment.dll` with `Private=false`. Its view can be rendered without ABB assemblies; RobotStudio-specific integration remains in `BridgePanelHost`. Listener stop/start retains one SDK dispatcher, drains already-started requests asynchronously, cancels queued work and atomically publishes a fresh token only after a successful bind. [Panel SDK evidence](notes-ui.md)

Development additions use public [RsInstruction argument APIs](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.RsInstruction.html), [temporary graphics](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.Forms.TemporaryGraphicCollection.html), and [UndoContext](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.UndoContext.html). Batch validation reuses the existing solver and collision APIs, checks model snapshots around awaits, and restores temporary poses before proceeding. Cancellation aborts the remainder. Collision scene geometry is sampled per probe rather than frozen across the batch.

Comparison reads typed `RobTarget`, `ToolData`, and `WobjData` using [Rapid.GetRapidData](https://developercenter.robotstudio.com/api/pcsdk/api/ABB.Robotics.Controllers.RapidDomain.Rapid.html); it does not synchronize as a way to inspect data. Explicit synchronization calls [RsTask.SyncDataAsync](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.RsTask.html) with validated `Module/name` IDs and a required direction. Paths/URLs cannot enter `dataId`. No standalone PC SDK discovery/logon or user-supplied controller connection is used. Detailed implementation evidence is in [editing notes](notes-editing.md) and [synchronization notes](notes-sync.md).

Compilation verifies the installed public API signatures. Offline tests cover the shared contracts and mathematics, and the [verification record](verification.md) describes how to run the offline and read-only station checks.
