[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT' -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Package the Windows x64 release from 64-bit PowerShell on an x64 Windows PC.'
}

$props = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable major.minor.patch Version in Directory.Build.props.' }

# This also verifies the ABB schema, the SDK bindings and the offline test suites.
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release

$files = [ordered]@{
    'INSTALL.md' = 'release/INSTALL.md'
    'addin/OfflineStationBridge/OfflineStationBridge.rsaddin' = 'artifacts/addin/OfflineStationBridge.rsaddin'
    'addin/OfflineStationBridge/RobotStudioOffline.Addin.dll' = 'artifacts/addin/RobotStudioOffline.Addin.dll'
    'addin/OfflineStationBridge/RobotStudioOffline.Addin.deps.json' = 'artifacts/addin/RobotStudioOffline.Addin.deps.json'
    'addin/OfflineStationBridge/RobotStudioOffline.Core.dll' = 'artifacts/addin/RobotStudioOffline.Core.dll'
    'headless/headless-vc.ps1' = 'headless-vc.ps1'
    'headless/tools/headless/OwnedClone.cs' = 'tools/headless/OwnedClone.cs'
}
foreach ($app in @('Mcp', 'HeadlessRecorder')) {
    $source = if ($app -eq 'Mcp') { 'artifacts/mcp' } else { 'artifacts/headless-recorder' }
    $destination = if ($app -eq 'Mcp') { 'mcp' } else { 'headless/recorder' }
    foreach ($leaf in @('RobotStudioOffline.Core.dll', "RobotStudioOffline.$app.dll", "RobotStudioOffline.$app.exe",
                        "RobotStudioOffline.$app.deps.json", "RobotStudioOffline.$app.runtimeconfig.json")) {
        $files["$destination/$leaf"] = "$source/$leaf"
    }
}

$output = New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts/release') -Force
$zipName = "RobotStudioOfflineBridge-$version-win-x64.zip"
$zipPath = Join-Path $output.FullName $zipName
$temporaryZip = Join-Path $output.FullName ([guid]::NewGuid().ToString('N') + '.tmp')
$temporaryChecksum = "$temporaryZip.sha256"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $archive = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files.GetEnumerator()) {
            $entry = $archive.CreateEntry($file.Key, [IO.Compression.CompressionLevel]::Optimal)
            # Repackaging identical inputs should produce the same checksum.
            $entry.LastWriteTime = [datetimeoffset]::new(2000, 1, 1, 0, 0, 0, [timespan]::Zero)
            $sourceStream = [IO.File]::OpenRead((Join-Path $PSScriptRoot $file.Value))
            try {
                $entryStream = $entry.Open()
                try { $sourceStream.CopyTo($entryStream) } finally { $entryStream.Dispose() }
            } finally { $sourceStream.Dispose() }
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $temporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($temporaryChecksum, "$hash  $zipName`n", [Text.Encoding]::ASCII)
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
    Move-Item -LiteralPath $temporaryChecksum -Destination "$zipPath.sha256" -Force
    Write-Output "ZIP $zipPath"
    Write-Output "SHA256 $hash"
} finally {
    foreach ($temporary in @($temporaryZip, $temporaryChecksum)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
