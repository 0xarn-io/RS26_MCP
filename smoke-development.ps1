[CmdletBinding()]
param(
    [string]$Robot,
    [Parameter(Mandatory)][string]$Tool,
    [Parameter(Mandatory)][string]$WorkObject,
    [Parameter(Mandatory)][string]$Target,
    [switch]$CheckGeometry,
    [string]$OutputPath,
    [switch]$CheckEngineering,
    [string]$SafeMoveGeometryFile,
    [switch]$CheckCell
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$handler.UseCookies = $false
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.BaseAddress = [Uri]'http://127.0.0.1:18426/'
$client.Timeout = [TimeSpan]::FromSeconds(120)
$tokenFile = Join-Path $env:LOCALAPPDATA 'RobotStudioOfflineBridge\token'
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', (Get-Content -Raw -LiteralPath $tokenFile).Trim())
$results = [ordered]@{ timestamp = [DateTimeOffset]::Now.ToString('o'); readOnly = $true; calls = [ordered]@{} }
$failures = [System.Collections.Generic.List[string]]::new()
function Invoke-Read([string]$Label, [string]$Operation, [hashtable]$Arguments) {
    if ($Operation -notin @('station','robot_pose','reach','path_details','validate_targets','data_compare','io_read','collisions','simulation','geometry','raycast','configuration','io_project','simulation_setup','safemove','body_motion','smart_component','station_logic','body_edit')) { throw 'Only read operations are permitted.' }
    if ($Operation -in @('body_motion','smart_component','station_logic') -and $Arguments.ContainsKey('action')) { throw 'Cell smoke uses only the default read actions.' }
    if ($Operation -eq 'body_edit' -and $Arguments.action -ne 'state') { throw 'Only body state may be read.' }
    if ($Operation -in @('configuration','io_project','simulation_setup','safemove') -and $Arguments.ContainsKey('action')) { throw 'Engineering smoke uses default read actions only.' }
    if ($Operation -eq 'validate_targets' -and $Arguments.checkCollisions -ne $false) { throw 'Collision pose probes are not permitted in this script.' }
    if ($Operation -eq 'collisions' -and $Arguments.Count -ne 0) { throw 'Only existing collision state may be read.' }
    if ($Operation -eq 'simulation' -and ($Arguments.action -ne 'state' -or $Arguments.Count -ne 1)) { throw 'Only simulation state may be read.' }
    $content = [System.Net.Http.StringContent]::new(($Arguments | ConvertTo-Json -Depth 15 -Compress), [Text.Encoding]::UTF8, 'application/json')
    try {
        $response = $client.PostAsync($Operation, $content).GetAwaiter().GetResult()
        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            $results.calls[$Label] = [ordered]@{ operation = $Operation; arguments = $Arguments; httpStatus = [int]$response.StatusCode; response = $body }
            if (-not $response.IsSuccessStatusCode -or -not $body.ok) {
                $failures.Add($Label)
                Write-Host "FAIL ${Label}: $($body.error.code)"
                return $null
            }
            Write-Host "PASS ${Label}: HTTP $([int]$response.StatusCode)"
            return $body.data
        } finally { $response.Dispose() }
    } catch {
        $failures.Add($Label)
        $results.calls[$Label] = [ordered]@{ operation = $Operation; arguments = $Arguments; transportError = $_.Exception.Message }
        Write-Host "FAIL ${Label}: $($_.Exception.Message)"
        return $null
    } finally { $content.Dispose() }
}
try {
    $station = Invoke-Read 'station' 'station' @{}
    if ($null -eq $station) { throw 'Station inventory failed; dependent checks were not attempted.' }
    $robots = @($station.robots | Where-Object { -not $Robot -or $_.name -ceq $Robot })
    if ($robots.Count -ne 1) { throw 'Specify -Robot using one exact mechanism name from station.robots.' }
    $robot = $robots[0].name
    $frames = @{ robot = $robot; tool = $Tool; workObject = $WorkObject }
    $before = Invoke-Read 'robot_pose_before' 'robot_pose' $frames
    $reach = Invoke-Read 'reach' 'reach' @{ robot = $robot; tool = $Tool; workObject = $WorkObject; target = $Target }
    if ($null -ne $reach -and -not $reach.reachable) { $failures.Add('reach_not_reachable') }
    $null = Invoke-Read 'path_details' 'path_details' @{ robot = $robot }
    $batch = Invoke-Read 'validate_targets' 'validate_targets' @{ robot = $robot; tool = $Tool; workObject = $WorkObject; checkCollisions = $false; targets = @(@{ id = 'target'; target = $Target }) }
    if ($null -ne $batch -and ($batch.aborted -or $batch.completed -ne 1 -or -not $batch.results[0].ok -or -not $batch.results[0].data.reach.reachable)) { $failures.Add('batch_result') }
    $stored = @($station.targets | Where-Object {
        $_.task -ceq $robots[0].task -and ($_.name -ceq $Target -or $_.robtarget -ceq $Target -or "$($_.module)/$($_.robtarget)" -ceq $Target)
    })
    if ($stored.Count -eq 1 -and $stored[0].module) {
        $comparison = Invoke-Read 'data_compare' 'data_compare' @{ robot = $robot; data = @(@{ name = "$($stored[0].module)/$($stored[0].robtarget)"; kind = 'target' }, @{ name = $Tool; kind = 'tool' }) }
        if ($null -ne $comparison) {
            foreach ($item in $comparison.results) {
                Write-Host "Compare $($item.kind): $($item.status)"
                if ($item.status -eq 'error') { $failures.Add('data_compare_item') }
            }
        }
    }
    $io = Invoke-Read 'io_read_inventory' 'io_read' @{ robot = $robot; limit = 100 }
    if ($null -ne $io) {
        $good = @($io.signals | Where-Object { $_.ok })
        if ($good.Count -ne @($io.signals).Count) { $failures.Add('io_inventory_item') }
        $names = @($good | Select-Object -First 5 | ForEach-Object { $_.name })
        if ($names.Count -gt 0) {
            $named = Invoke-Read 'io_read_named' 'io_read' @{ robot = $robot; names = $names }
            if ($null -ne $named -and @($named.signals | Where-Object { -not $_.ok }).Count -gt 0) { $failures.Add('io_named_item') }
        }
    }
    $null = Invoke-Read 'collisions_state' 'collisions' @{}
    $null = Invoke-Read 'simulation_state' 'simulation' @{ action = 'state' }
    if ($CheckCell) {
        $null = Invoke-Read 'motion_status' 'body_motion' @{}
        $null = Invoke-Read 'wiring' 'station_logic' @{limit = 1}
        $sensor = Invoke-Read 'sensor_template' 'smart_component' @{template = 'LineSensor'}
        if ($sensor -and 'SensedPart' -cnotin @($sensor.properties.name)) { $failures.Add('sensor_missing_part_output') }
        $parts = Invoke-Read 'cell_parts' 'simulation_setup' @{section = 'parts'; limit = 1}
        if ($parts -and $parts.items.Count -gt 0) {
            $null = Invoke-Read 'part_state' 'body_edit' @{action = 'state'; object = $parts.items[0].id}
        }
    }
    if ($CheckEngineering) {
        $cfg = Invoke-Read 'configuration_domains' 'configuration' @{ robot = $robot; limit = 1 }
        if ($cfg -and ($cfg.scope -ne 'station_owned_virtual_controller' -or $cfg.result.level -ne 'domains')) { $failures.Add('configuration_scope') }
        $eio = Invoke-Read 'installed_io' 'io_project' @{ robot = $robot; limit = 1 }
        if ($eio -and ($eio.source -ne 'installed_eio_configuration' -or $eio.result.level -ne 'types')) { $failures.Add('io_project_scope') }
        $setup = Invoke-Read 'physics_overview' 'simulation_setup' @{}
        if ($setup -and $null -eq $setup.physics.gravityMps2) { $failures.Add('missing_physics') }
        $null = Invoke-Read 'physics_parts' 'simulation_setup' @{ section = 'parts'; limit = 1 }
    }
    if ($SafeMoveGeometryFile) {
        $geometryFile = Invoke-Read 'safemove_geometry' 'safemove' @{ file = $SafeMoveGeometryFile; limit = 1 }
        if ($geometryFile -and (-not $geometryFile.schemaChecked -or $geometryFile.activeConfigurationVerified)) { $failures.Add('safemove_provenance') }
    }
    if ($CheckGeometry) {
        $inventory = Invoke-Read 'geometry_inventory' 'geometry' @{ limit = 1 }
        if ($null -ne $inventory -and $inventory.total -lt 1) { $failures.Add('robot_missing_from_geometry') }
        $rayArgs = @{ robot = $robot; tool = $Tool; origin = @(0,0,0); direction = @(0,0,1); maxDistance = 5000 }
        $ray = Invoke-Read 'tool_ray' 'raycast' $rayArgs
        if ($null -ne $ray -and $ray.hit) {
            $hit = Invoke-Read 'ray_part_inventory' 'geometry' @{ match = $ray.partId; limit = 1 }
            if ($null -ne $hit -and ($hit.total -ne 1 -or $hit.items[0].id -ne $ray.partId)) {
                $failures.Add('ray_part_missing_from_inventory')
            }
            $filtered = Invoke-Read 'ray_part_by_id' 'raycast' ($rayArgs + @{ parts = @($ray.partId) })
            if ($null -ne $filtered -and (-not $filtered.hit -or $filtered.partId -ne $ray.partId -or
                [Math]::Abs($filtered.distanceMm - $ray.distanceMm) -gt 0.1)) { $failures.Add('filtered_ray_mismatch') }
        }
    }
    $after = Invoke-Read 'robot_pose_after' 'robot_pose' $frames
    if ($null -ne $before -and $null -ne $after) {
        $unchanged = ($before.joints | ConvertTo-Json -Compress) -ceq ($after.joints | ConvertTo-Json -Compress)
        $results['jointsUnchanged'] = $unchanged
        Write-Host "Observed joints unchanged: $unchanged"
        if (-not $unchanged) { $failures.Add('joints_changed_during_reads') }
    }
} finally {
    $client.Dispose()
    $results['failures'] = @($failures.ToArray())
    if ($OutputPath) { $results | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath $OutputPath -Encoding utf8 }
}
if ($failures.Count -gt 0) { throw "Read-only smoke had $($failures.Count) failures: $($failures -join ', ')." }
Write-Host 'READ-ONLY DEVELOPMENT SMOKE PASSED. No writes, pulses, pose changes or simulation control were sent.'
