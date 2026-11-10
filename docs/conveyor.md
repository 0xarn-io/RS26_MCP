# Build a conveyor pick in a virtual station

Use `geometry` to find existing parts and CAD bodies. `geometry_create` returns the **Part ID** of the new box, cylinder or sphere. Use that ID for motion and attachments. Each example below is one tool call. The names stand for objects in your station.

## Move a unit and transfer a workpiece

In an empty station, give the simulator an active participant before testing travel. `body_motion` advances on simulator ticks. A configuration containing only a `LineSensor` can stop at time zero. For a standalone test, create a `LinearMover` on a separate marker part, set its speed and direction, confirm `includedInActiveConfiguration:true`, and set its `Execute` input to 1 **before** starting the simulation. Check that `simulation` reports an increasing `timeSeconds`. If a move is still armed in a stopped station, call `body_motion` with `action:"stop"` for that part before deleting it. [ABB simulation lifecycle](https://developercenter.robotstudio.com/api/robotstudio/articles/Concepts/Simulation.html)

| Tool | Arguments | What to observe |
| --- | --- | --- |
| `body_edit` | `{"action":"state","object":"Workpiece"}` | World pose, physics mode and current attachment parent |
| `body_motion` | `{"action":"start","part":"Carrier","deltaMm":[400,0,0],"durationMs":2000}` | Arms the carrier before simulation or starts travel in a running simulation |
| `simulation` | `{"action":"start"}` | Advances the active station simulation |
| `body_motion` | `{"action":"status","part":"Carrier"}` | Fraction of the two simulated seconds elapsed and whether the move completed |
| `body_edit` | `{"action":"attach","object":"Workpiece","parent":"Gripper"}` | With the gripper at pickup, the item keeps its world pose and follows the gripper |
| `body_edit` | `{"action":"release","object":"Workpiece","parent":"Carrier"}` | With the gripper at unloading, the carrier becomes the new parent at the current world pose |
| `body_edit` | `{"action":"release","object":"Workpiece"}` | Frees the item in world and restores any physics mode the bridge changed for the carry |

`body_motion` moves independent parts whose physics mode is `None` or `Kinematic`. The displacement is in the part parent’s axes, in millimetres. Use `{"action":"stop","part":"Carrier"}` to hold its current position. Simulation stop restores a bridge-driven part that still has the expected pose and parent. The gripper selector is a **graphic component** or a single-flange station mechanism. A RAPID tooldata declaration by itself cannot carry geometry. [ABB attachment API](https://developercenter.robotstudio.com/api/robotstudio/articles/How-To/Geometry/Attachments.html)

## Carry loose items by surface contact

Place a box on a conveyor part, with its lower face resting on the top surface. With simulation stopped, call `simulation_setup` once for each part.

```json
{"action":"set_part","part":"Belt","motionControl":"Static","surfaceVelocityEnabled":true,"surfaceDirection":[1,0,0],"surfaceSpeedMps":0.3,"detectableBySensors":false}
```

```json
{"action":"set_part","part":"Workpiece","motionControl":"Dynamic","detectableBySensors":true}
```

Start the simulation and read `body_edit` with `action:"state"` to observe the workpiece’s world position. Contact and the parts’ physics material determine the resulting movement. Here the belt advances its surface while its frame stays fixed. Setting a straight `surfaceDirection` replaces an existing control-point path on that part. [ABB surface velocity](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.PhysicsSurfaceVelocity.html) · [ABB operating manual, “The Infeeder”](https://library.e.abb.com/public/5ac9707abd684c4a95329d272846d9d5/3HAC032104-001_en_AZ_Operating%20manual%20-%20RobotStudio.pdf)

## Wire a sensor and gripper

With simulation stopped, call `smart_component` with `{"action":"catalog"}` to see which ABB templates are installed. Add `"template":"LineSensor"` to read the sensor’s native properties and signals. Component placement uses **world millimetres**. Geometric values in the template’s dynamic properties use **SDK metres**. In this example a horizontal 0.2 m beam spans the pickup point. Adjust its placement to detect the item as it arrives.

| Tool | Arguments |
| --- | --- |
| `smart_component` | `{"action":"create","template":"LineSensor","name":"PickupEye","position":[0,0,120],"properties":[{"name":"Start","value":[0,-0.1,0]},{"name":"End","value":[0,0.1,0]}]}` |
| `smart_component` | `{"action":"create","template":"Attacher","name":"GripItem","properties":[{"name":"Parent","value":{"object":"Gripper"}},{"name":"Mount","value":false}]}` |
| `smart_component` | `{"action":"create","template":"Detacher","name":"DropItem","properties":[{"name":"Child","value":{"object":"Workpiece"}},{"name":"KeepPosition","value":true}]}` |
| `station_logic` | `{"action":"connect","kind":"property","source":{"component":"PickupEye","member":"SensedPart"},"target":{"component":"GripItem","member":"Child"}}` |
| `station_logic` | `{"action":"connect","source":{"component":"PickupEye","member":"SensorOut"},"target":{"robot":"Arm","member":"diAtPickup"}}` |
| `station_logic` | `{"action":"connect","source":{"robot":"Arm","member":"doGrip"},"target":{"component":"GripItem","member":"Execute"}}` |
| `station_logic` | `{"action":"connect","source":{"robot":"Arm","member":"doRelease"},"target":{"component":"DropItem","member":"Execute"}}` |

Substitute existing virtual signal names from `io_read` and the mechanism name from `station`. During the run, enable the beam with `station_logic {"action":"set_input","target":{"component":"PickupEye","member":"Active"},"value":1}`. Inspect the sensor to see `SensedPart` before triggering grip. For a station-only trigger test, leave the two VC output wires disconnected and use `station_logic {"action":"pulse","target":{"component":"GripItem","member":"Execute"}}`. At unloading, pulse `DropItem.Execute`. Read `body_edit` after each trigger to verify the actual attachment. This detacher selects the known workpiece so it can release it after the item leaves the beam. [ABB signal connections](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.IOConnection.html) · [ABB property bindings](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Stations.PropertyBinding.html)

For a persistent, signal-controlled travelling unit, create a `LinearMover` with `Object` set to the carrier, `Direction` set to `[1,0,0]` and `Speed` set in m/s. Drive its `Execute` input high to travel and low to stop. Use one motion source per part. Check the component’s simulation participation in the creation result or through `simulation_setup`.

## Move and remove trial objects

For an absolute edit use `body_edit {"action":"move","object":"Workpiece","position":[400,0,100]}`. Omit `quaternion` to preserve orientation. For a CAD body in a multi-body part, use the body ID to edit that body or the part ID to move the assembly of bodies together.

To clean up, stop simulation, release attachments and disconnect any wiring with the original source and target plus `"action":"disconnect"`. Remove a trial collision set using `collision_set_delete {"set":"Trial clearance"}`. Remove an independent trial part with `body_edit {"action":"delete","object":"Part ID"}`. Use the **Part ID** to remove the entire primitive created by `geometry_create`. Geometry edits and collision-set removal participate in RobotStudio Undo.

If a component still references the part, clear the reference with `smart_component` (for example, `{"action":"configure","component":"DropItem","properties":[{"name":"Child","value":null}]}`) or remove the trial component with `{"action":"delete","component":"DropItem"}` after disconnecting its wires.
