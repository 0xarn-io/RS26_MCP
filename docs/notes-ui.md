# Native bridge panel

The bridge panel is a Windows Forms `UserControl` hosted in RobotStudio's public `ToolWindow` API. `BridgePanel` contains no ABB SDK calls: status, station name, lifecycle callbacks and metadata activity are supplied by the add-in entry point. This separation permits rendering the control in an isolated Windows Forms harness without starting or touching RobotStudio.

Verified installed assembly: `C:\Program Files (x86)\ABB\RobotStudio 2026\Bin\ABB.Robotics.RobotStudio.Environment.dll`, version `26.3.11844.1`. The project requires this SDK reference with `Private=false`; existing `UseWindowsForms=true` supplies the native control framework.

- [ToolWindow](https://developercenter.robotstudio.com/api/robotstudio/api/ABB.Robotics.RobotStudio.Environment.ToolWindow.html) provides the `(object id, Control control, string caption)` constructor, `PreferredSize`, and `CloseButtonBehavior`. The bridge explicitly uses `Hide`, so clicking its X hides the panel without stopping the listener or destroying history. A stable add-in ID and idempotent host initialization keep one window.
- [Creating a ToolWindow](https://developercenter.robotstudio.com/api/robotstudio/articles/How-To/Buttons/CreateToolWindows.html) documents `UIEnvironment.Windows.AddDockedOrTabbed(window, DockStyle.Right)`. The bridge opens the panel when the add-in initializes and can show it again from its ribbon button.
- [Creating a Button](https://developercenter.robotstudio.com/api/robotstudio/articles/How-To/Buttons/CreateButton.html) documents `CommandBarButton`, `ExecuteCommand`, `RibbonGroup` and ribbon placement. The installed localized `Bin\es\Ribbon.xml` confirms language-independent `AddIns` and `Home` tab IDs. The bridge chooses Add-Ins, then Home, with a dedicated fallback tab if neither exists. It never invokes application Exit, station actions or controller commands.
- The installed public `VisualStyle` metadata exposes `GetThemeColor`, `ThemeColor.BackColorToolControl`, `WindowTextColor`, `WindowBackColor` and `ThemeChanged`. The host applies these to the control and unsubscribes on disposal. The view uses DPI scaling, a 420×500 minimum size, and a one-second Windows Forms timer on the UI thread. The timer pauses while the panel is hidden.

The panel displays listener/stopping/error state, loopback endpoint, current station, lifetime counts, and bounded recent request metadata. It makes no claim that Claude is connected: HTTP requests are independent. Pending includes queued and executing requests. Arguments, response bodies, auth tokens and exception messages from station requests are not retained or displayed by activity history. Clearing history preserves pending entries and lifetime counts.

Stop drains in-flight requests, ends bridge TCP recording and restores parts still controlled by `body_motion`. Station simulation and ABB Smart Components continue running. Start is disabled while stopping or while requests remain pending. The Tools tab lists the available MCP operations.

MCP setup chooses `RobotStudioOffline.Mcp.dll` through a file picker and copies a PowerShell registration command only after a button click. Its path is single-quoted and embedded single quotes are doubled; it is never executed by the panel. No repository path is hardcoded and no token is copied.

The graceful-stop integration test checks that an executing mock write finishes, its HTTP response is retained, queued work is cancelled and the listener refuses new connections. Activity tests cover concurrency, bounded history, pending rows, counters and privacy.

The SDK-free Windows Forms harness exercised all three tabs at 520×760 and narrow 420×540 layouts, the dark theme, and registration-command quoting for apostrophes, dollar signs and backticks. Live theme switching remains unverified.
