[CmdletBinding()]
param(
    [string]$RobotStudioDir = 'C:\Program Files (x86)\ABB\RobotStudio 2026',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.build\packages'
    $env:RS_OFFLINE_TEST_SDK = $RobotStudioDir
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:DOTNET_NOLOGO = '1'
    $env:MSBUILDDISABLENODEREUSE = '1'
    New-Item -ItemType Directory -Path 'artifacts' -Force | Out-Null
    $log = Join-Path $PSScriptRoot 'artifacts\build.log'
    "Build started $([DateTimeOffset]::Now.ToString('o'))" | Set-Content -LiteralPath $log
    function Invoke-Dotnet([string[]]$Arguments) {
        & dotnet @Arguments 2>&1 | Tee-Object -FilePath $log -Append
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
    }
    Invoke-Dotnet @('build', 'RobotStudioOffline.slnx', '-c', $Configuration, '--disable-build-servers', '-m:1', "-p:RobotStudioDir=$RobotStudioDir")
    Invoke-Dotnet @("tests\RobotStudioOffline.Tests\bin\$Configuration\net10.0\RobotStudioOffline.Tests.dll")
    Invoke-Dotnet @("tests\RobotStudioOffline.Mcp.Tests\bin\$Configuration\net10.0\RobotStudioOffline.Mcp.Tests.dll")

    Invoke-Dotnet @("src\RobotStudioOffline.HeadlessRecorder\bin\$Configuration\net10.0-windows\RobotStudioOffline.HeadlessRecorder.dll", '--self-test')

    $standardInstall = Join-Path ${env:ProgramFiles(x86)} 'ABB\RobotStudio 2026'
    if ([IO.Path]::GetFullPath($RobotStudioDir) -eq [IO.Path]::GetFullPath($standardInstall)) {
        $winPS = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        & $winPS -NoProfile -NonInteractive -File .\headless-vc.ps1 check 2>&1 | Tee-Object -FilePath $log -Append
        if ($LASTEXITCODE -ne 0) { throw "Headless VC binding check failed with exit code $LASTEXITCODE" }
    }

    $manifest = Join-Path $PSScriptRoot 'src\RobotStudioOffline.Addin\OfflineStationBridge.rsaddin'
    $schema = Join-Path $RobotStudioDir 'Schemas\RobotStudioAddin.xsd'
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.ValidationType = [System.Xml.ValidationType]::Schema
    $settings.Schemas.Add('urn:abb-robotics-robotstudio-addin', $schema) | Out-Null
    $settings.add_ValidationEventHandler({ param($sender, $eventArgs) throw $eventArgs.Exception })
    $reader = [System.Xml.XmlReader]::Create($manifest, $settings)
    try { while ($reader.Read()) { } } finally { $reader.Dispose() }
    'PASS: .rsaddin validated against installed ABB schema.' | Tee-Object -FilePath $log -Append

    Invoke-Dotnet @('publish', 'src\RobotStudioOffline.Addin\RobotStudioOffline.Addin.csproj', '-c', $Configuration, '--no-build', '-o', 'artifacts\addin', "-p:RobotStudioDir=$RobotStudioDir")
    Invoke-Dotnet @('publish', 'src\RobotStudioOffline.Mcp\RobotStudioOffline.Mcp.csproj', '-c', $Configuration, '--no-build', '-o', 'artifacts\mcp')
    Invoke-Dotnet @('publish', 'src\RobotStudioOffline.HeadlessRecorder\RobotStudioOffline.HeadlessRecorder.csproj', '-c', $Configuration, '--no-build', '-o', 'artifacts\headless-recorder', "-p:RobotStudioDir=$RobotStudioDir")
    $vendorCopies = Get-ChildItem -LiteralPath 'artifacts\addin','artifacts\headless-recorder' -Filter 'ABB.*.dll'
    if ($vendorCopies) { throw 'Vendor assemblies must not be redistributed with the add-in.' }
    'BUILD AND OFFLINE TESTS PASSED. Artifacts: artifacts\addin, artifacts\mcp, artifacts\headless-recorder.' | Tee-Object -FilePath $log -Append
} finally {
    Pop-Location
}
