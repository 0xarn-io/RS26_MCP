using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using RobotStudio.Services.RobApi;
using RobotStudio.Services.RobApi.Desktop;
using RobotStudio.Services.RobApi.Interop;

namespace RobotStudioOffline.Headless
{
    // ABB's installed 26.3 desktop interop defines this IUnknown vtable. Bind the
    // instance returned by GetVCInterface once; a GUID lookup at stop time could
    // refer to a later, independently started incarnation of the same clone.
    [ComImport, Guid("897AB146-832C-4B85-AD42-007B543A3708"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IStartedVirtualController
    {
        [PreserveSig] int UnusedLaunch(IntPtr systemPath, int mode, IntPtr systemId);
        void Kill();
        void IsRunning(out int running);
    }

    public sealed class OwnedClone : IDisposable
    {
        private readonly IStartedVirtualController instance;
        private readonly Process process;
        public string Address { get; private set; }
        public int RuntimePid { get { return process.Id; } }
        public DateTime RuntimeStartUtc { get { return process.StartTime.ToUniversalTime(); } }
        public bool Exited { get { return process.HasExited; } }

        private OwnedClone(IStartedVirtualController instance, Process process, string address)
        { this.instance = instance; this.process = process; Address = address; }

        public static OwnedClone Start(string directory, string expectedName, Guid expectedId)
        {
            var id = Guid.Parse(File.ReadAllText(Path.Combine(directory, "internal", "system.guid")).Trim());
            if (expectedId == Guid.Empty || id != expectedId || !String.Equals(Path.GetFileName(directory), expectedName, StringComparison.Ordinal))
                throw new InvalidOperationException("The prepared local clone identity does not match the configured clone.");
            RobApiDesktopInit.Initialize(true, false, "RobotStudioOffline.Headless");
            var before = DateTime.UtcNow;
            var started = RobVcFactory.StartVC(directory, VCStartType.WarmStart, VcStartOptions.ThrowIfStarted,
                VcLogOptions.LogToFile, false).GetAwaiter().GetResult();
            if (started.AlreadyRunning || Guid.Parse(started.SystemId) != id)
                throw new InvalidOperationException("ABB did not confirm an exclusive start of the selected clone.");

            // Resolve only the returned exact GUID in ABB's local registry. No enumeration,
            // scan, network endpoint, login or public controller connection is used here.
            Guid classId = typeof(RobNetScanHostClass).GUID;
            Guid registryId = typeof(IRobNetScan).GUID;
            object registry = null;
            Process process = null;
            try
            {
                // ABB registers the local RobNetScanHost executable in the 32-bit COM view.
                // Request the local-server class and interface explicitly from the 64-bit worker.
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 0x4 | 0x40000, ref registryId, out registry));
                var lookup = (IRobNetScan)registry;
                var identity = (IRobCtrlIdentity4)registry;
                uint key;
                if (lookup.GetCtrlFromSystemID(id.ToString("B").ToUpperInvariant(), out key) != 0)
                    throw new InvalidOperationException("The newly started clone has no local registration.");
                string systemId, name, address;
                uint pid;
                RobCtrlType kind;
                identity.GetCtrlType(key, out kind);
                identity.GetSystemID(key, out systemId);
                identity.GetSystemName(key, out name);
                identity.GetCtrlAddress(key, out address);
                IPAddress ip;
                if (kind != RobCtrlType.VC || Guid.Parse(systemId) != id ||
                    !String.Equals(name, expectedName, StringComparison.Ordinal) ||
                    !IPAddress.TryParse(address, out ip) || !IPAddress.IsLoopback(ip))
                    throw new InvalidOperationException("The registration must match the configured local loopback virtual controller.");
                identity.GetVCProcessID(key, out pid);
                process = Process.GetProcessById(checked((int)pid));
                // Keep this native process handle alive so PID reuse cannot validate ownership.
                var handle = process.Handle;
                if (process.HasExited || process.StartTime.ToUniversalTime() < before)
                    throw new InvalidOperationException("The VC process predates this launch or has exited.");
                VCInterface binding;
                identity.GetVCInterface(key, out binding);
                var pinned = (IStartedVirtualController)binding.pVC;
                uint afterPid;
                identity.GetVCProcessID(key, out afterPid);
                if (afterPid != pid || process.HasExited) throw new InvalidOperationException("The VC changed during ownership verification.");
                var result = new OwnedClone(pinned, process, ip.ToString());
                process = null;
                return result;
            }
            catch (Exception ex)
            {
                // Never stop by GUID as recovery: ownership was not proved. Report this
                // separately so the owner can inspect a clone that may have completed boot.
                throw new InvalidOperationException("ABB accepted clone startup, but this host could not pin its instance. Inspect the clone in RobotStudio. " + ex.Message, ex);
            }
            finally
            {
                if (process != null) process.Dispose();
                if (registry != null && Marshal.IsComObject(registry)) Marshal.ReleaseComObject(registry);
            }
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        public void Stop()
        {
            if (process.HasExited) return;
            instance.Kill(); // The same per-instance shutdown request used by ABB's StopVC.
            if (!process.WaitForExit(60000)) throw new TimeoutException("The clone did not exit within one minute of its shutdown request.");
        }

        public void Dispose()
        {
            if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
            process.Dispose();
        }
    }
}
