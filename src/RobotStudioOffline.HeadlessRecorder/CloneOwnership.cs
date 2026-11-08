using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.HeadlessRecorder;

internal sealed class CloneOwnership(Process host, Process runtime, string generation, string systemName, Guid expectedId) : IDisposable
{
    internal Guid ExpectedId => expectedId;
    internal string SystemName => systemName;
    internal string Generation => generation;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    internal static CloneOwnership Open()
    {
        var name = Environment.GetEnvironmentVariable("RS_BRIDGE_CLONE_NAME");
        if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9 _-]{0,63}\z") || name.EndsWith(' ') ||
            !Guid.TryParse(Environment.GetEnvironmentVariable("RS_BRIDGE_CLONE_ID"), out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Set RS_BRIDGE_CLONE_NAME and RS_BRIDGE_CLONE_ID to the dedicated local clone's system name and expected GUID.");
        var cloneDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RobotStudio", "Virtual Controllers", name);
        var guidFile = Path.Combine(cloneDir, "internal", "system.guid");
        var statusFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobotStudioOfflineBridge", "headless", name, "status.json");
        LocalInputFile.RequireAvailable(LocalInputFile.CanonicalPath(guidFile), "clone");
        if (!Guid.TryParse(File.ReadAllText(guidFile).Trim(), out var diskId) || diskId != id)
            throw new InvalidOperationException("The dedicated local clone identity changed.");
        LocalInputFile.RequireAvailable(LocalInputFile.CanonicalPath(statusFile), "host");
        if (new FileInfo(statusFile).Length > 16384) throw new InvalidDataException("Invalid host state file.");
        using var disk = JsonDocument.Parse(File.ReadAllText(statusFile));
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No Windows user SID.");
        using var pipe = new NamedPipeClientStream(".", "RobotStudioOfflineBridge.Headless." + sid + "." + name,
            PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        pipe.Connect(2000);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException("Cannot identify the local VC host pipe server.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[16384];
        int used = 0;
        int newline;
        do
        {
            if (used == buffer.Length) throw new InvalidDataException("Invalid host status response.");
            var read = pipe.ReadAsync(buffer.AsMemory(used), deadline.Token).AsTask().GetAwaiter().GetResult();
            if (read == 0) throw new EndOfStreamException("VC host closed its status pipe.");
            newline = Array.IndexOf(buffer, (byte)'\n', used, read);
            used += read;
        } while (newline < 0);
        using var live = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.AsSpan(0, newline)).TrimStart('\uFEFF'));
        var proof = live.RootElement;
        var file = disk.RootElement;
        foreach (var key in new[] { "state", "generation", "systemId", "systemPath", "hostPid", "hostStartUtc",
            "runtimePid", "runtimeStartUtc", "virtual", "address" })
            if (!JsonElement.DeepEquals(file.GetProperty(key), proof.GetProperty(key)))
                throw new InvalidOperationException("The current headless host does not match its state file.");
        if (proof.GetProperty("state").GetString() != "running" || proof.GetProperty("systemId").GetGuid() != id ||
            !Guid.TryParseExact(proof.GetProperty("generation").GetString(), "N", out _) ||
            !proof.GetProperty("virtual").GetBoolean() || !IPAddress.TryParse(proof.GetProperty("address").GetString(), out var address) ||
            !IPAddress.IsLoopback(address) || (uint)proof.GetProperty("hostPid").GetInt32() != pid ||
            !string.Equals(Path.TrimEndingDirectorySeparator(cloneDir), Path.TrimEndingDirectorySeparator(proof.GetProperty("systemPath").GetString()!),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The running host did not prove ownership of the dedicated loopback clone.");
        var host = Process.GetProcessById(checked((int)pid));
        Process? runtime = null;
        try
        {
            runtime = Process.GetProcessById(proof.GetProperty("runtimePid").GetInt32());
            if (host.Id == runtime.Id || !host.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                host.StartTime.ToUniversalTime() != proof.GetProperty("hostStartUtc").GetDateTimeOffset().UtcDateTime ||
                runtime.StartTime.ToUniversalTime() != proof.GetProperty("runtimeStartUtc").GetDateTimeOffset().UtcDateTime)
                throw new InvalidOperationException("The owning host or VC process changed.");
            var ownership = new CloneOwnership(host, runtime, proof.GetProperty("generation").GetString()!, name, id);
            ownership.Check();
            return ownership;
        }
        catch { host.Dispose(); runtime?.Dispose(); throw; }
    }

    internal void Check()
    {
        if (host.HasExited || runtime.HasExited) throw new InvalidOperationException("The owning headless VC session ended.");
    }
    public void Dispose() { host.Dispose(); runtime.Dispose(); }
}
