# RobotStudio 2026 Offline Station Bridge

This Windows x64 package connects Claude Code to an open RobotStudio station. It was built against RobotStudio 2026.3. The MCP server requires the x64 .NET 10 runtime. The optional headless recorder uses the .NET 10 Windows Desktop Runtime.

## Load the add-in

1. Extract the ZIP into a permanent folder on the RobotStudio PC. Keep the `mcp` folder there for Claude Code.
2. Save your work. If replacing a loaded bridge, close RobotStudio before copying the DLLs.
3. Copy the entire `addin\OfflineStationBridge` folder into `C:\Program Files (x86)\ABB\RobotStudio 2026\Bin\Addins`. The installed folder must contain `OfflineStationBridge.rsaddin` directly inside it. Windows may request administrator permission for the copy.
4. Open RobotStudio and the desired station. If RobotStudio was open during installation and the add-in does not appear, save your work and restart it. On **Add-Ins**, right-click **Offline Station Bridge** and select **Load Add-In**.
5. Check that the bridge panel shows **Listening** on `http://127.0.0.1:18426`.

## Register with Claude Code

Run these commands in PowerShell from the extracted folder under the same Windows account as RobotStudio.

```powershell
$bridgeDll = (Resolve-Path '.\mcp\RobotStudioOffline.Mcp.dll').Path
claude mcp add --transport stdio --scope user robotstudio-offline -- dotnet "$bridgeDll"
claude mcp get robotstudio-offline
```

Open Claude Code in your project directory. Enter `/mcp` to check the connection, then ask Claude to use `station` to report the station name and robots. The wrapper reads the local authentication token automatically.

## Optional headless tools

The launcher requires ABB build 26.3.11844.1 and 64-bit Windows PowerShell 5.1. For a headless run, set `RS_BRIDGE_CLONE_NAME` and `RS_BRIDGE_CLONE_ID` to your prepared local clone’s system name and GUID. These offline checks can run before configuring a clone.

```powershell
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File .\headless\headless-vc.ps1 check
dotnet .\headless\recorder\RobotStudioOffline.HeadlessRecorder.dll --self-test
```

Station geometry, views and simulation-time recording use the add-in inside RobotStudio. See the [project documentation](https://github.com/0xarn-io/RS26_MCP) for tool arguments, recording formats, clone prerequisites and controller permission prompts.
