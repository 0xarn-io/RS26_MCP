[CmdletBinding()]
param(
    [string]$Robot,
    [Parameter(Mandatory)][string]$Tool,
    [Parameter(Mandatory)][string]$WorkObject,
    [Parameter(Mandatory)][string]$Target,
    [ValidateCount(6, 6)][double[]]$ExpectedJoints,
    [ValidateRange(0.001, 10)][double]$JointToleranceDegrees = 1.0,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
$tokenFile = Join-Path $env:LOCALAPPDATA 'RobotStudioOfflineBridge\token'
if (-not (Test-Path -LiteralPath $tokenFile)) {
    throw 'Bridge token missing. The owner must install and load the add-in first; see README.md.'
}
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.BaseAddress = [Uri]'http://127.0.0.1:18426/'
$client.Timeout = [TimeSpan]::FromSeconds(120)
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', (Get-Content -Raw -LiteralPath $tokenFile).Trim())
$results = [ordered]@{ timestamp = [DateTimeOffset]::Now.ToString('o'); scope = 'station_only'; calls = [ordered]@{} }
function Invoke-Bridge([string]$Operation, [hashtable]$Arguments) {
    if ($Operation -notin @('station', 'robot_pose', 'reach')) { throw 'Smoke test permits only the three read operations.' }
    $content = [System.Net.Http.StringContent]::new(($Arguments | ConvertTo-Json -Depth 10 -Compress), [Text.Encoding]::UTF8, 'application/json')
    try {
        $response = $client.PostAsync($Operation, $content).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $value = $text | ConvertFrom-Json
            $results.calls[$Operation] = $value
            if (-not $response.IsSuccessStatusCode -or -not $value.ok) { throw "${Operation} failed: $($value.error.code)" }
            Write-Host "PASS $Operation"
            return $value.data
        } finally { $response.Dispose() }
    } finally { $content.Dispose() }
}
try {
    $station = Invoke-Bridge 'station' @{}
    $robots = @($station.robots | Where-Object { -not $Robot -or $_.name -ceq $Robot })
    if ($robots.Count -ne 1) { throw 'Specify -Robot using one exact mechanism name from station.robots.' }
    $selectedRobot = $robots[0]
    $frames = @{ robot = $selectedRobot.name; tool = $Tool; workObject = $WorkObject }
    $null = Invoke-Bridge 'robot_pose' $frames
    $frames.target = $Target
    $reach = Invoke-Bridge 'reach' $frames
    if (-not $reach.reachable) { throw 'The selected target was unreachable with the supplied frames.' }
    if (@($reach.joints).Count -ne 6) { throw 'Expected six robot joints in the reach response.' }
    if ($ExpectedJoints) {
        $errors = @(0..5 | ForEach-Object { [Math]::Abs([double]$reach.joints[$_] - $ExpectedJoints[$_]) })
        $nearExpected = @($errors | Where-Object { $_ -gt $JointToleranceDegrees }).Count -eq 0
        $results['jointCheck'] = @{ expectedDegrees = $ExpectedJoints; toleranceDegrees = $JointToleranceDegrees; absoluteErrors = $errors; passed = $nearExpected }
        if (-not $nearExpected) { throw 'The joint solution is outside the supplied tolerance.' }
    }
    Write-Host 'LIVE SMOKE PASSED'
} finally {
    $client.Dispose()
    if ($OutputPath) { $results | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding utf8 }
}
