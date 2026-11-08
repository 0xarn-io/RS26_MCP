using System.Net;
using RobotStudioOffline.Core;
using Pc = ABB.Robotics.Controllers;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static BridgeException RapidAccessRequired(Pc.Controller controller, Pc.WriteAccessStatus? status = null)
    {
        var holder = status?.Held == true && !string.IsNullOrWhiteSpace(status.HeldByName)
            ? $" Current holder: {status.HeldByName}." : "";
        var external = status?.ExternalControlEnabled == false ? " External control is disabled." : "";
        var target = controller.IsVirtual ? "the selected virtual controller" : "the approved real controller";
        return Error("rapid_write_access_required", $"RobotWare requires RobotStudio Write Access on {target} for the installed RAPID check or load.{holder}{external} Request Write Access in RobotStudio and retry. For a local source syntax check, call rapid_check with files.");
    }

    private static bool IsRapidAccessDenial(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
            if (e is Pc.WriteAccessHeldException || e.HResult == unchecked((int)0xc004ab1f) ||
                e.Message.Contains("C004AB1F", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void RequireRw8Access(Pc.Controller controller)
    {
        if (controller.RobotWareVersion.Major < 8) return;
        var status = controller.ControlStation.WriteAccessStatus;
        if (!status.HeldByMe) throw RapidAccessRequired(controller, status);
    }

    private static IDisposable? BeginRapidLoadAccess(Pc.Controller controller, bool virtualLoad, List<string> warnings)
    {
        if (controller.RobotWareVersion.Major < 8)
            return controller.Rapid.IsMaster ? null : Pc.Mastership.Request(controller);

        var station = controller.ControlStation;
        var status = station.WriteAccessStatus;
        switch (RapidWriteAccessPolicy.Decide(status.HeldByMe, status.Held, status.ExternalControlEnabled,
                    virtualLoad && controller.IsVirtual && IPAddress.IsLoopback(controller.IPAddress)))
        {
            case RapidAccessDecision.Reuse: return null;
            case RapidAccessDecision.Refuse: throw RapidAccessRequired(controller, status);
        }

        var lease = new TemporaryVcWriteAccess(controller, station, warnings);
        try
        {
            // One ordinary synchronous request. Never appeal to another holder, enable external
            // control, change mode, or poll/retry the grant on the RobotStudio main thread.
            lease.Request();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private sealed class TemporaryVcWriteAccess(Pc.Controller controller, Pc.ControlStation station, List<string> warnings) : IDisposable
    {
        private readonly Guid systemId = controller.SystemId;
        private bool subscribed, acquired;
        private string? grantHolder;
        private int observedHeld, observedLoss;

        internal void Request()
        {
            station.WriteAccessStatusChanged += OnAccessChanged;
            subscribed = true;
            // A second preflight covers changes while hooking notifications. An owner who now
            // holds access keeps it; this scope never releases a pre-existing RobotStudio grant.
            var before = station.WriteAccessStatus;
            if (before.HeldByMe) return;
            if (before.Held || !before.ExternalControlEnabled) throw RapidAccessRequired(controller, before);
            try { station.RequestWriteAccess(); }
            catch (Exception ex) { throw Error("rapid_write_access_required", RapidAccessRequired(controller).Message + " SDK: " + ex.Message); }
            acquired = true;
            var granted = station.WriteAccessStatus;
            if (!granted.HeldByMe) throw RapidAccessRequired(controller, granted);
            grantHolder = granted.HeldById;
            Interlocked.Exchange(ref observedHeld, 1);
        }

        private void OnAccessChanged(object? sender, Pc.WriteAccessStatusChangedEventArgs args)
        {
            if (args.Status.HeldByMe) Interlocked.Exchange(ref observedHeld, 1);
            else if (Volatile.Read(ref observedHeld) != 0) Interlocked.Exchange(ref observedLoss, 1);
        }

        public void Dispose()
        {
            try
            {
                // A grant lost and later reacquired by the shared RobotStudio session belongs
                // to that newer operation. Leave it alone even when HeldByMe has become true.
                if (!acquired || Volatile.Read(ref observedLoss) != 0) return;
                if (!controller.Connected || !controller.IsVirtual || controller.SystemId != systemId ||
                    !IPAddress.IsLoopback(controller.IPAddress))
                    throw new InvalidOperationException("The original VC connection is unavailable.");
                var current = station.WriteAccessStatus;
                if (current.HeldByMe && (grantHolder is null || current.HeldById == grantHolder) &&
                    Volatile.Read(ref observedLoss) == 0) station.ReleaseWriteAccess();
            }
            catch (Exception ex) { warnings.Add("Inspect RobotStudio Write Access for the virtual controller. Release verification failed: " + ex.Message); }
            finally
            {
                if (subscribed)
                {
                    try { station.WriteAccessStatusChanged -= OnAccessChanged; }
                    catch (Exception ex) { warnings.Add("Write Access event cleanup: " + ex.Message); }
                }
            }
        }
    }
}
