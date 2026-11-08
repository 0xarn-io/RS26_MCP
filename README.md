# RobotStudio 2026 Offline Station Bridge

Use Claude Code to inspect an open RobotStudio station, check a target with its actual tool and work object, or record and review a virtual run. A C# add-in runs inside RobotStudio. A local stdio MCP server translates tool calls to authenticated JSON requests on `127.0.0.1:18426`.

## Install on Windows

You need 64-bit Windows, RobotStudio 2026 with a license for general add-ins, Claude Code, and the x64 .NET 10 runtime on your path (`dotnet --list-runtimes`). The tested host is RobotStudio 2026.3, ABB build `26.3.11844.1`. [ABB add-in requirements](https://developercenter.robotstudio.com/api/robotstudio/articles/Introduction/Installation.html)

1. Download the **win-x64 ZIP** and its SHA-256 checksum from the [latest release](https://github.com/0xarn-io/RS26_MCP/releases/latest). Compare the checksum with `Get-FileHash -Algorithm SHA256 <zip-path>` in PowerShell. Extract the ZIP to a permanent folder of your choice. Keep `mcp` there so Claude can launch it in future sessions.
2. Copy the complete `OfflineStationBridge` folder from the ZIP's `addin` directory into your RobotStudio installation's `Bin\Addins` directory. With the default installation the manifest must end up at `C:\Program Files (x86)\ABB\RobotStudio 2026\Bin\Addins\OfflineStationBridge\OfflineStationBridge.rsaddin`. Windows may request administrator permission for the copy.
3. Open RobotStudio and the intended station. On **Add-Ins**, right-click **Offline Station Bridge** and choose **Load Add-In**. The bridge panel should show **Listening**. RobotStudio scans the add-in directory at startup. If you copied the files while it was running and the entry is missing, save your work and restart RobotStudio yourself. [ABB add-in loading](https://developercenter.robotstudio.com/api/robotstudio/articles/Concepts/AddinConcept/LoadingAdd-In.html)

To update an already loaded add-in, save and close RobotStudio yourself before replacing its files. Keep the folder name and manifest name together.

## Connect Claude Code

Open PowerShell in the **extracted folder that contains `mcp`**. Run the following once under the same Windows account as RobotStudio. User scope makes the bridge available from any Claude Code project. [Claude Code MCP setup](https://code.claude.com/docs/en/mcp#option-3-add-a-local-stdio-server)

```powershell
$wrapper = (Resolve-Path '.\mcp\RobotStudioOffline.Mcp.dll').Path
claude mcp add --transport stdio --scope user robotstudio-offline -- dotnet "$wrapper"
claude mcp get robotstudio-offline
```

Start a new Claude Code session in your robot project, enter `/mcp`, and check that **robotstudio-offline** is connected. Try this read-only request:

> Use the station tool to tell me which station is open and list its robot, tools and work objects.

The add-in creates a local bearer token for the current Windows user. The wrapper reads it automatically. You can also open **Add-Ins → Offline Bridge → Open bridge panel → MCP setup** and select the wrapper DLL to copy a project-local registration command. The bridge and wrapper communicate locally. Claude Code's model connection is separate.

## What you can do

| Task | Tools |
| --- | --- |
| Inspect a station and plan motion | `station`, `robot_pose`, `reach`, `path_details`, `validate_targets` |
| Set up a scene | `geometry`, `geometry_create`, `geometry_import`, `raycast`, `collision_set` |
| Exercise and measure a virtual run | `jump`, `simulation`, `simulation_speed`, `collisions`, `tcp_record`, `run_review`, `run_view`, `screenshot` |
| Inspect or edit station and controller data | `preview_targets`, `edit_targets`, `data_compare`, `data_sync`, `rapid_check`, `rapid_load`, `rapid_diff`, `rapid_sync_real` |
| Read or simulate virtual I/O | `io_read`, `io_write`, `io_pulse` |

Calls operate on local files, the station or its mapped virtual controller by default. To enable real-controller comparison or replacement, set `RS_BRIDGE_REAL_ADDRESS` to the intended controller’s IPv4 address in RobotStudio’s environment before launching RobotStudio. The add-in requires one matching existing connection. Each `rapid_diff` with `includeReal:true` or `rapid_sync_real` request requires explicit owner approval in RobotStudio. The dialog identifies the controller, task and module selection, and defaults to Cancel.

For JSON arguments, units, supported robot configurations, reach failure reasons, and a complete recording example, see the [bridge reference](docs/bridge-reference.md). A recorded clearance covers the selected geometry at the acquired samples. To assess a carried item, include its attached CAD geometry.

## Build from source

On the RobotStudio PC, install the **x64 .NET 10 SDK** and run the following in PowerShell from the repository root. The build references the local ABB assemblies and uses package-free offline test runners.

```powershell
.\build.ps1
```

For a custom installation path, use `.\build.ps1 -RobotStudioDir 'D:\ABB\RobotStudio 2026'`. The script compiles the solution, tests the bridge and MCP transport, checks the headless recorder, and validates the add-in manifest against ABB's installed schema. It saves the output to `artifacts\build.log`.

For a source build, copy **all files** in `artifacts\addin` to the `OfflineStationBridge` installation folder above. Use `artifacts\mcp\RobotStudioOffline.Mcp.dll` as the wrapper path in the Claude registration command. To create the distributable ZIP and checksum, run `.\package-release.ps1`.

## Reference

- [Bridge tools, HTTP contract and run recording](docs/bridge-reference.md)
- [Release verification and read-only smoke tests](docs/verification.md)
- [RobotStudio SDK, runtime, discovery and kinematics](docs/sdk-notes.md)
- [Target editing](docs/notes-editing.md), [virtual I/O](docs/notes-io.md), [station and VC synchronization](docs/notes-sync.md), and [bridge panel](docs/notes-ui.md)
- [Optional headless recorder and dedicated clone requirements](tools/headless/README.md)
