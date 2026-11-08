using System.Drawing;
using System.Windows.Forms;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Environment;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

/// <summary>RobotStudio UI integration only. It does not control a station or controller.</summary>
public static class BridgePanelHost
{
    private const string WindowId = "RobotStudioOffline.BridgePanel";
    private const string ButtonId = "RobotStudioOffline.ShowBridgePanel";
    private const string GroupId = "RobotStudioOffline.BridgeGroup";
    private static ToolWindow? window;
    private static BridgePanel? panel;
    private static CommandBarButton? button;
    private static RibbonGroup? group;
    private static RibbonTab? ribbonTab;
    private static bool ownsTab;

    public static void Initialize(Func<BridgeUiState> getState, Action startBridge, Action stopBridge, BridgeActivity activity)
    {
        RequireUiThread();
        if (panel is not null && !panel.IsDisposed) { Show(); return; }
        try
        {
        panel = new BridgePanel(getState, startBridge, stopBridge, activity);
        window = new ToolWindow(WindowId, panel, "Offline Station Bridge")
        {
            CloseButtonBehavior = CloseButtonBehavior.Hide,
            PreferredSize = new Size(480, 720), DefaultTabVisibility = true
        };
        UIEnvironment.Windows.AddDockedOrTabbed(window, DockStyle.Right);

        // IDs are verified in the installed Ribbon.xml and are independent of UI language.
        ribbonTab = UIEnvironment.RibbonTabs.FirstOrDefault(tab => tab.Id == "AddIns")
            ?? UIEnvironment.RibbonTabs.FirstOrDefault(tab => tab.Id == "Home");
        if (ribbonTab is null)
        {
            ribbonTab = new RibbonTab("RobotStudioOffline.Tab", "Offline Bridge");
            UIEnvironment.RibbonTabs.Add(ribbonTab);
            ownsTab = true;
        }
        group = new RibbonGroup(GroupId, "Offline Bridge");
        button = new CommandBarButton(ButtonId, "Open bridge panel")
        {
            DefaultEnabled = true,
            HelpText = "Show bridge status, local tools, request activity and Claude Code setup.",
            IsUndoable = false
        };
        button.ExecuteCommand += OnShowPanel;
        group.Controls.Add(button);
        group.SetControlLayout(button, RibbonControlLayout.Large);
        ribbonTab.Groups.Add(group);
        UIEnvironment.VisualStyle.ThemeChanged += OnThemeChanged;
        ApplyTheme();
        Show();
        }
        catch (Exception initializationError)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { initializationError.Data["PanelCleanupError"] = cleanupError.Message; }
            throw;
        }
    }

    public static void Show()
    {
        RequireUiThread();
        if (window is null || panel is null || panel.IsDisposed) return;
        window.Visible = true;
        window.ActiveTab = true;
        Window.ActiveWindow = window;
        panel.RefreshState();
    }

    public static void Dispose()
    {
        RequireUiThread();
        Exception? firstError = null;
        void Release(Action action)
        {
            try { action(); }
            catch (Exception exception) { firstError ??= exception; }
        }
        Release(() => UIEnvironment.VisualStyle.ThemeChanged -= OnThemeChanged);
        var oldButton = button;
        var oldGroup = group;
        var oldTab = ribbonTab;
        if (oldButton is not null)
        {
            Release(() => oldButton.ExecuteCommand -= OnShowPanel);
            Release(() => oldGroup?.Controls.Remove(oldButton));
            Release(() => UIEnvironment.CommandBarControls.Remove(ButtonId));
        }
        if (oldGroup is not null) Release(() => oldTab?.Groups.Remove(oldGroup));
        if (ownsTab && oldTab is not null) Release(() => UIEnvironment.RibbonTabs.Remove(oldTab));
        Release(() => window?.Dispose());
        Release(() => panel?.Dispose());
        window = null;
        panel = null;
        button = null;
        group = null;
        ribbonTab = null;
        ownsTab = false;
        if (firstError is not null) throw new InvalidOperationException("Some bridge panel resources could not be released.", firstError);
    }

    private static void OnShowPanel(object sender, ExecuteCommandEventArgs args) => Show();
    private static void OnThemeChanged(object? sender, EventArgs args) => ApplyTheme();
    private static void ApplyTheme()
    {
        if (panel is null || panel.IsDisposed) return;
        var style = UIEnvironment.VisualStyle;
        panel.ApplyTheme(style.GetThemeColor(ThemeColor.BackColorToolControl),
            style.GetThemeColor(ThemeColor.WindowTextColor), style.GetThemeColor(ThemeColor.WindowBackColor));
    }

    private static void RequireUiThread()
    {
        if (Thread.CurrentThread != RobotStudioAPI.MainThread)
            throw new InvalidOperationException("Bridge panel integration must execute on RobotStudio's main thread.");
    }
}
