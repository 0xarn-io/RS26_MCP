# Headless clone lifecycle

Use a dedicated local virtual controller that you prepared in RobotStudio. From 64-bit Windows PowerShell 5.1, set the clone's local system name and GUID for this shell before running `status`, `start` or `stop`. The recorder reads the same settings.

```powershell
$env:RS_BRIDGE_CLONE_NAME = Read-Host 'Local virtual controller system name'
$env:RS_BRIDGE_CLONE_ID = Read-Host 'Local virtual controller system GUID'
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File .\headless-vc.ps1 check
.\headless-vc.ps1 status
.\headless-vc.ps1 start -WhatIf
.\headless-vc.ps1 start
.\headless-vc.ps1 stop
```

The launcher requires ABB's installed RobotStudio build 26.3.11844.1. Use the clone’s folder name under `Documents\RobotStudio\Virtual Controllers`; its `internal\system.guid` must match the configured ID. Starting the clone performs a warm boot and can execute its configured startup tasks.

`check` loads the two installed .NET Framework SDK assemblies and compiles the instance binding. It runs independently of the clone, with no COM activation or controller connection.

`start` launches a hidden worker and waits up to three minutes for the startup result. ABB's bundled `RobVcFactory.StartVC` receives `ThrowIfStarted`. After startup the worker verifies the reported GUID, virtual-controller type, exact system name, loopback address, runtime PID and creation time. It retains the runtime's process handle and the ABB COM interface for that instance. A separate caller can repeat `start` to get the dedicated worker's status. The host leaves a clone already owned by another application alone.

`stop` signals a per-generation Windows event. Only the worker holding that generation can submit the shutdown to its pinned VC interface. A stale worker PID or unavailable process produces `host_unavailable`. If ABB accepted a boot and the worker could not verify its identity, the failure is recorded for owner inspection. There is no recovery command targeting a controller by name or GUID.

The status pipe is local to the current Windows user. A recorder verifies the pipe server, generation, process creation times, system GUID and loopback address before using the dedicated clone. Lifecycle status is stored under `%LOCALAPPDATA%\RobotStudioOfflineBridge\headless`.

The clone provides controller execution and controller telemetry. Station CAD, active camera views, station geometry and RobotStudio simulation tick capture require an open station and the add-in. Live warm boot, runtime identity binding and shutdown remain owner-run checks on this installation.
