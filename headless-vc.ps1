[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Position=0)][ValidateSet('check','start','status','stop','worker')][string]$Action = 'status',
    [ValidatePattern('^[a-f0-9]{32}$')][string]$Generation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-State {
    if (-not [IO.File]::Exists($statePath)) { return $null }
    $stream = [IO.FileStream]::new($statePath,'Open','Read',[IO.FileShare]'ReadWrite,Delete')
    try {
        $reader = [IO.StreamReader]::new($stream)
        try { $state = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($state.systemId -ne $cloneId -or $state.systemPath -ne $systemPath) {
            throw 'The saved lifecycle state belongs to a different clone configuration.'
        }
        return $state
    } finally { $stream.Dispose() }
}
function Test-Host($state) {
    if ($null -eq $state -or $state.state -notin 'starting','running','stopping') { return $false }
    try {
        $process = [Diagnostics.Process]::GetProcessById([int]$state.hostPid)
        try { return $process.StartTime.ToUniversalTime().Ticks -eq ([DateTimeOffset]$state.hostStartUtc).UtcTicks }
        finally { $process.Dispose() }
    } catch { return $false }
}
function State-Report {
    $saved = Read-State
    if ($null -eq $saved) {
        return [pscustomobject]@{state='stopped'; owned=$false; systemPath=$systemPath; systemId=$cloneId}
    }
    if ($saved.state -in 'starting','running','stopping' -and -not (Test-Host $saved)) {
        $saved.state = 'host_unavailable'
    }
    return $saved
}
function Publish-State($state) {
    $temporary = Join-Path $stateDir ("state-" + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($state | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        if (-not [IO.File]::Exists($statePath)) { [IO.File]::Move($temporary, $statePath); return }
        for ($attempt=0; $attempt -lt 10; $attempt++) {
            try { [IO.File]::Replace($temporary, $statePath, [NullString]::Value); return }
            catch [IO.IOException] {
                if ($attempt -eq 9) { throw }
                Start-Sleep -Milliseconds 25
            }
        }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
function Require-LocalPath([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    if ($full -notmatch '^[a-zA-Z]:\\' -or ([IO.DriveInfo]::new($full.Substring(0,3))).DriveType -ne 'Fixed') {
        throw 'The clone and lifecycle state must be on fixed local drives.'
    }
    for ($current=$full; $current; $current=[IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing reparse point in selected local path: $current"
        }
    }
}
function Confirm-Clone {
    $identity = Join-Path $systemPath 'internal\system.guid'
    Require-LocalPath $identity
    Require-LocalPath $statePath
    if (-not [IO.File]::Exists($identity) -or ([Guid]([IO.File]::ReadAllText($identity).Trim())).ToString('D') -ne $cloneId) {
        throw "The prepared $cloneName clone with ID $cloneId is required at $systemPath."
    }
}
function Open-StatusPipe {
    $security = [IO.Pipes.PipeSecurity]::new()
    $networkSid = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::NetworkSid,$null)
    $security.AddAccessRule([IO.Pipes.PipeAccessRule]::new($networkSid,[IO.Pipes.PipeAccessRights]::FullControl,'Deny'))
    $security.AddAccessRule([IO.Pipes.PipeAccessRule]::new($ownerSid,[IO.Pipes.PipeAccessRights]::FullControl,'Allow'))
    return [IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::Out,1,
        [IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous,4096,4096,$security)
}
function Load-CloneHost {
    $bin = Join-Path ${env:ProgramFiles(x86)} 'ABB\RobotStudio 2026\Bin-net48'
    $api = Join-Path $bin 'RobotStudio.Services.RobApi.dll'
    $desktop = Join-Path $bin 'RobotStudio.Services.RobApi.Desktop.dll'
    if ([Reflection.AssemblyName]::GetAssemblyName($desktop).Version.ToString() -ne '26.3.11844.1') {
        throw 'This isolated instance binding was checked against RobotStudio build 26.3.11844.1.'
    }
    # LoadFrom resolves managed siblings in the same trusted installation directory.
    [void][Reflection.Assembly]::LoadFrom($api)
    [void][Reflection.Assembly]::LoadFrom($desktop)
    Add-Type -Path (Join-Path $PSScriptRoot 'tools\headless\OwnedClone.cs') -ReferencedAssemblies $api,$desktop
}
function Run-Worker {
    if (-not $Generation -or $PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) {
        throw 'The worker requires 64-bit Windows PowerShell 5.1 and a launch generation.'
    }
    $mutex = [Threading.Mutex]::new($false,$mutexName)
    $held = $false
    $stop = $null
    $pipe = $null
    $runtime = $null
    $shutdownSubmitted = $false
    try {
        try { $held=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held=$true }
        if (-not $held) { throw 'A dedicated clone worker already holds the lifecycle lock.' }
        Confirm-Clone
        [void][IO.Directory]::CreateDirectory($stateDir)
        $stop = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,"$mutexName.stop.$Generation")
        $hostStart = [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().ToString('o')
        $state = [ordered]@{generation=$Generation; systemId=$cloneId; systemPath=$systemPath; state='starting';
            hostPid=$PID; hostStartUtc=$hostStart; runtimePid=$null; runtimeStartUtc=$null; address=$null;
            virtual=$true; pipeName=$pipeName; error=$null}
        Publish-State $state
        try {
            Load-CloneHost
            $runtime = [RobotStudioOffline.Headless.OwnedClone]::Start($systemPath, $cloneName, [Guid]$cloneId)
            $state.runtimePid = $runtime.RuntimePid
            $state.runtimeStartUtc = $runtime.RuntimeStartUtc.ToString('o')
            $state.address = $runtime.Address
            $pipe = Open-StatusPipe
            $pending = $pipe.BeginWaitForConnection($null,$null)
            $state.state='running'
            Publish-State $state
            while (-not $stop.WaitOne(0) -and -not $runtime.Exited) {
                if ($pending.AsyncWaitHandle.WaitOne(200)) {
                    try {
                        $pipe.EndWaitForConnection($pending)
                        $line = [Text.Encoding]::UTF8.GetBytes(($state | ConvertTo-Json -Compress) + [Environment]::NewLine)
                        $pipe.Write($line,0,$line.Length)
                    } catch [IO.IOException] { } # A status reader may disconnect without reading.
                    finally { if ($pipe.IsConnected) { $pipe.Disconnect() } }
                    $pending.AsyncWaitHandle.Dispose()
                    $pending = $pipe.BeginWaitForConnection($null,$null)
                }
            }
            $state.state='stopping'
            Publish-State $state
            $shutdownSubmitted = $true
            $runtime.Stop()
            $state.state='stopped'
        } catch {
            $state.state='failed'
            $state.error=$_.Exception.Message
            if (-not $shutdownSubmitted -and $null -ne $runtime -and -not $runtime.Exited) {
                try { $runtime.Stop() } catch { $state.error += ' Instance shutdown failed: ' + $_.Exception.Message }
            }
        } finally { Publish-State $state }
    } finally {
        if ($null -ne $pipe) { $pipe.Dispose() }
        if ($null -ne $runtime) { $runtime.Dispose() }
        if ($null -ne $stop) { $stop.Dispose() }
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

if ($Action -eq 'check') {
    if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion -lt [Version]'5.1' -or -not [Environment]::Is64BitProcess) {
        throw 'Run check using 64-bit Windows PowerShell 5.1.'
    }
    Load-CloneHost
    [pscustomobject]@{check='passed'; sdk='26.3.11844.1'; workerType=[RobotStudioOffline.Headless.OwnedClone].FullName;
        powershell=$PSVersionTable.PSVersion.ToString(); bitness=64; controllerStarted=$false} | ConvertTo-Json -Compress
    return
}
$cloneName = $env:RS_BRIDGE_CLONE_NAME
$id = [Guid]::Empty
if ($cloneName -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9 _-]{0,63}\z' -or $cloneName.EndsWith(' ') -or
    -not [Guid]::TryParse($env:RS_BRIDGE_CLONE_ID, [ref]$id) -or $id -eq [Guid]::Empty) {
    throw 'Set RS_BRIDGE_CLONE_NAME to the local clone system/folder name and RS_BRIDGE_CLONE_ID to its expected GUID.'
}
$cloneId = $id.ToString('D')
$systemPath = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "RobotStudio\Virtual Controllers\$cloneName"
$stateDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "RobotStudioOfflineBridge\headless\$cloneName"
$statePath = Join-Path $stateDir 'status.json'
$ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$pipeName = "RobotStudioOfflineBridge.Headless.$($ownerSid.Value).$cloneName"
$mutexName = "Local\$pipeName"
if ($Action -eq 'status') { State-Report | ConvertTo-Json -Compress; return }
if ($Action -eq 'worker') {
    if ($PSCmdlet.ShouldProcess($systemPath,'Run exclusive virtual clone worker')) { Run-Worker }
    return
}
if ($Action -eq 'start') {
    Confirm-Clone
    $existing = Read-State
    if (Test-Host $existing) { $existing | ConvertTo-Json -Compress; return }
    if (-not $PSCmdlet.ShouldProcess($systemPath,'Warm-start isolated clone')) { return }
    $Generation = [Guid]::NewGuid().ToString('N')
    $engine = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $workerArgs = '-NoProfile -NonInteractive -MTA -File "' + $PSCommandPath + '" -Action worker -Generation ' + $Generation
    $worker = Start-Process -FilePath $engine -ArgumentList $workerArgs -WindowStyle Hidden -PassThru
    try {
        $deadline=[DateTime]::UtcNow.AddMinutes(3)
        do {
            $saved=Read-State
            if ($null -ne $saved -and $saved.generation -eq $Generation -and $saved.state -ne 'starting') {
                $saved | ConvertTo-Json -Compress
                if ($saved.state -ne 'running') { throw "Clone startup ended in state '$($saved.state)'." }
                return
            }
            if ($worker.HasExited) { throw 'Worker exited before publishing this generation. Check status for another active host.' }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'The worker is still starting. Check status before retrying.'
    } finally { $worker.Dispose() }
}

$saved = Read-State
if (-not (Test-Host $saved)) { State-Report | ConvertTo-Json -Compress; return }
if (-not $PSCmdlet.ShouldProcess("$cloneName generation $($saved.generation)",'Stop the worker-owned virtual clone')) { return }
if ([string]$saved.generation -notmatch '^[a-f0-9]{32}$') { throw 'Invalid worker generation in local status.' }
$stopEvent=[Threading.EventWaitHandle]::OpenExisting("$mutexName.stop.$($saved.generation)")
try { [void]$stopEvent.Set() } finally { $stopEvent.Dispose() }
$deadline=[DateTime]::UtcNow.AddMinutes(2)
do {
    $result=State-Report
    if ($result.generation -ne $saved.generation) { throw 'The recorded generation changed during stop.' }
    if ($result.state -eq 'stopped') { $result | ConvertTo-Json -Compress; return }
    if ($result.state -in 'failed','host_unavailable') { $result | ConvertTo-Json -Compress; throw "Stop ended in state '$($result.state)'." }
    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $deadline)
throw 'Owner worker has not confirmed shutdown. Check status.'
