using System.IO;
using System.Security.Cryptography;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public static class AddInEntry
{
    private static readonly BridgeActivity activity = new();
    private static StationApi? api;
    private static LoopbackServer? server;
    private static MainThreadDispatcher? dispatcher;
    private static bool initialized;
    private static bool stopping;
    private static bool shuttingDown;
    private static string? lastError;
    private const string Endpoint = "http://127.0.0.1:18426";

    // Spelling follows ABB's current Creating-a-RobotStudio-Add-In sample.
    public static void AddinMain()
    {
        if (initialized)
        {
            TryShowPanel();
            return;
        }
        try
        {
            api = new StationApi();
            var context = SynchronizationContext.Current ?? RobotStudioAPI.SyncContext
                ?? throw new InvalidOperationException("RobotStudio main-thread synchronization context is unavailable.");
            // Keep one dispatcher across listener restarts so SDK operations never overlap.
            dispatcher = new MainThreadDispatcher(action => RobotStudioAPI.RunOnMainThread(() =>
            {
                var previous = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    action();
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            }, true), api.ExecuteAsync);
            RobotStudioAPI.ShuttingDown += (_, _) => Shutdown();
            initialized = true;
            StartBridge();
            TryShowPanel();
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            Log("Offline Station Bridge did not initialize: " + exception.Message);
        }
    }

    private static BridgeUiState GetUiState() => new(
        server is not null && !stopping && !shuttingDown, Endpoint,
        Station.ActiveStation?.Name, lastError, stopping);

    private static void TryShowPanel()
    {
        try { BridgePanelHost.Initialize(GetUiState, StartBridge, StopBridge, activity); }
        catch (Exception exception)
        {
            // A window/layout failure must not stop an otherwise functioning listener.
            Log("Offline Station Bridge panel could not open: " + exception.Message);
        }
    }

    private static void StartBridge()
    {
        if (server is not null || stopping || shuttingDown) return;
        if (activity.Snapshot().Pending != 0)
        {
            lastError = "Wait for pending requests to finish before starting the bridge.";
            return;
        }
        try
        {
            var dispatch = dispatcher ?? throw new InvalidOperationException("Bridge dispatcher is unavailable.");
            var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "RobotStudioOfflineBridge");
            Directory.CreateDirectory(directory);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var candidate = new LoopbackServer(token, (operation, arguments, cancellation) =>
                activity.TrackAsync(operation, () => dispatch.ExecuteAsync(operation, arguments, cancellation)));
            try
            {
                candidate.Start(); // Bind before publishing token: another RS instance cannot replace it.
                var temporaryToken = Path.Combine(directory, "token." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllText(temporaryToken, token);
                    File.Move(temporaryToken, Path.Combine(directory, "token"), overwrite: true);
                }
                finally { if (File.Exists(temporaryToken)) File.Delete(temporaryToken); }
            }
            catch { candidate.Dispose(); throw; }
            server = candidate;
            lastError = null;
            Log("Offline Station Bridge listening on " + Endpoint + ". Real-controller access requires explicit owner permission.");
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            Log("Offline Station Bridge did not start: " + exception.Message);
        }
    }

    // Action callback runs on RobotStudio's UI thread; all exceptions are handled here.
    private static async void StopBridge()
    {
        if (server is null || stopping || shuttingDown) return;
        var current = server;
        stopping = true;
        lastError = null;
        try
        {
            await current.StopAsync();
            api?.StopBodyMotionsForShutdown();
            api?.StopRecordingForShutdown();
            if (!shuttingDown) Log("Offline Station Bridge stopped. RobotStudio and station simulation were not stopped.");
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            if (!shuttingDown) Log("Offline Station Bridge stop reported an error: " + exception.Message);
        }
        finally
        {
            try { current.Dispose(); }
            catch (Exception exception) { lastError ??= exception.Message; }
            if (ReferenceEquals(server, current)) server = null;
            stopping = false;
        }
    }

    private static void Shutdown()
    {
        shuttingDown = true;
        try { api?.StopBodyMotionsForShutdown(); } catch (Exception error) { Log(error.Message); }
        try { api?.StopRecordingForShutdown(); } catch (Exception error) { Log(error.Message); }
        try { server?.Dispose(); } // Never wait on an SDK continuation during host shutdown.
        catch (Exception) { /* Host shutdown must continue even if transport cleanup fails. */ }
        try { BridgePanelHost.Dispose(); }
        catch (Exception) { /* The host may already be tearing down its UI services. */ }
    }

    private static void Log(string message) => Logger.AddMessage(new LogMessage(message));
}
