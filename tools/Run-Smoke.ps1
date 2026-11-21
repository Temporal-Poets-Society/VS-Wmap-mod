param([ValidateSet('earth','cylinder','plain')][string]$Profile = 'earth', [string]$GamePath = "$env:APPDATA\Vintagestory", [ValidateRange(512,8192)][int]$OverviewWidth = 2048, [ValidateSet('default','earth')][string]$PolarMode = 'default',
    [string]$SuiteRoot, [string]$EarthZip, [string]$CylinderZip, [string]$SeaZip, [string]$AstraZip, [string]$HydrologyZip,
    [ValidateRange(128,2048)][int]$MapHeight = 384, [int]$MapWidth = 256000, [int]$MapLength = 102400,
    [string]$VerticalBudget, [switch]$Wait, [ValidateRange(30,3600)][int]$TimeoutSeconds = 900, [int]$Port = 42439,
    # Runs go to .local\smoke-* in this checkout (the GPU checks find the project from there). An Earth run links the
    # player's GLO-90 heights into its run folder as a junction (ModData\earth\glo90): remove that junction on its own
    # ([IO.Directory]::Delete on that path, or cmd /c rmdir) before deleting the run folder, or Remove-Item -Recurse may
    # follow it into the player's heights.
    # Earth's ore deposits: maps (Earth's own generator, whose readings the ore heat checks expect) or vanilla (the play style's default)
    [ValidateSet('maps','vanilla')][string]$EarthOreDeposits = 'maps',
    # Earth's rocks (earthRocks): empty keeps the Earth play style's own (vanilla from Earth 2.0.1, groundcolumns in 2.0.0).
    [ValidateSet('','vanilla','groundcolumns','regional')][string]$EarthRocks = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
# the suite root: the first folder above this checkout with suite.json (a worktree sits deeper than the main checkout)
if (-not $SuiteRoot) {
    $SuiteRoot = $projectRoot
    while ($SuiteRoot -and -not (Test-Path -LiteralPath (Join-Path $SuiteRoot 'suite.json'))) { $SuiteRoot = Split-Path $SuiteRoot }
    if (-not $SuiteRoot) { $SuiteRoot = Join-Path $projectRoot '..' }
}
$playerMods = Join-Path $env:APPDATA 'VintagestoryData\Mods'
$runPath = Join-Path $projectRoot ('.local\smoke-' + $Profile + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force (Join-Path $runPath 'Mods'),(Join-Path $runPath 'ModConfig'),(Join-Path $runPath 'Saves') | Out-Null
$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'modinfo.json') -Raw | ConvertFrom-Json).version
Copy-Item -LiteralPath (Join-Path $projectRoot ('release\wmap_' + $version + '.zip')) -Destination (Join-Path $runPath 'Mods')
Copy-Item -LiteralPath (Join-Path $projectRoot 'tests\SmokeMod\bin\Release\mod') -Destination (Join-Path $runPath 'Mods\wmapsmoke') -Recurse
# the newest <id>-<version>.zip in the given folders, by version (a pre-release sorts below its release): each sibling
# mod's release\ folder and the player's VintagestoryData\Mods
function Get-NewestZip([string]$id, [string[]]$dirs) {
    $items = @(foreach ($d in $dirs) { if ($d -and (Test-Path -LiteralPath $d)) { Get-ChildItem -LiteralPath $d -Filter ($id + '-*.zip') -File } })
    $pattern = '^' + [regex]::Escape($id) + '-(\d+)\.(\d+)\.(\d+)(?:-(.+))?\.zip$'
    $best = $null; $bestKey = $null
    foreach ($f in $items) {
        if ($f.Name -notmatch $pattern) { continue }
        $key = '{0:D6}.{1:D6}.{2:D6}' -f [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
        if ($Matches[4]) { $key += '|0' + (($Matches[4] -split '[.-]' | ForEach-Object { if ($_ -match '^\d+$') { '0' + ([int]$_).ToString('D6') } else { '1' + $_ } }) -join '|') }
        else { $key += '|1' }
        if ($null -eq $bestKey -or [string]::CompareOrdinal($key, $bestKey) -gt 0) { $best = $f.FullName; $bestKey = $key }
    }
    $best
}
function Add-Mod([string]$what, [string]$zip) {
    if (-not $zip -or -not (Test-Path -LiteralPath $zip)) { throw "No $what package found; pass it explicitly." }
    Write-Output "${what}: $zip"
    Copy-Item -LiteralPath $zip -Destination (Join-Path $runPath 'Mods')
}
if ($Profile -ne 'plain') {
    if (-not $CylinderZip) { $CylinderZip = Get-NewestZip cylinderworld @((Join-Path $SuiteRoot 'Cworld\release'), $playerMods) }
    Add-Mod 'Cylinder World' $CylinderZip
}
if ($Profile -eq 'earth') {
    # Earth 2.0 packs earth-<version>.zip and refuses a world without Hydrology
    if (-not $EarthZip) { $EarthZip = Get-NewestZip earth @((Join-Path $SuiteRoot 'Earth\release'), $playerMods) }
    Add-Mod 'Earth' $EarthZip
    if (-not $HydrologyZip) { $HydrologyZip = Get-NewestZip hydrology @((Join-Path $SuiteRoot 'Hydrology\release'), $playerMods) }
    Add-Mod 'Hydrology' $HydrologyZip
}
# Sea's winds for the wind layer, when a Sea package is built next door or installed
if ($Profile -eq 'earth') {
    if (-not $SeaZip) { $SeaZip = Get-NewestZip sea @((Join-Path $SuiteRoot 'Sea\release'), $playerMods) }
    if ($SeaZip) { Add-Mod 'Sea' $SeaZip }
}
# AstraTerra's longitude-aware sun, so the day and night checks see a sun that follows longitude (Earth only gives
# AstraTerra the longitude of each X). The suite's fork (forks\AstraTerra, modid astraearth) builds into dist\, not
# release\; its builds since 0.15.0-dev.1-earthtest have SetLongitudeProvider. Not referenced by Wmap.
if ($Profile -eq 'earth') {
    $astra = if ($AstraZip) { $AstraZip } else { Get-NewestZip astraearth @((Join-Path $SuiteRoot 'forks\AstraTerra\dist'), $playerMods) }
    if (-not $astra -or -not (Test-Path -LiteralPath $astra)) { throw "AstraTerra build not found (forks\AstraTerra\dist\astraearth-*.zip); pass -AstraZip" }
    Write-Output "AstraTerra: $astra"
    Copy-Item -LiteralPath $astra -Destination (Join-Path $runPath 'Mods')
    @{LongitudeAwareSun=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'ModConfig\astraterra.json')
}
$settings = Get-Content -LiteralPath (Join-Path $SuiteRoot 'Earth\testserver\serverconfig.json') -Raw | ConvertFrom-Json
$settings.Ip = '127.0.0.1'; $settings.Port = $Port; $settings.AdvertiseServer = $false; $settings.Upnp = $false
$settings.ServerIdentifier = [guid]::NewGuid().ToString(); $settings.LastLaunchMods = $null
$settings.MapSizeX = $MapWidth; $settings.MapSizeZ = $MapLength; $settings.MapSizeY = $MapHeight
$settings.WorldConfig.MapSizeY = $MapHeight
$settings.WorldConfig.WorldConfiguration.worldWidth = [string]$MapWidth
$settings.WorldConfig.WorldConfiguration.worldLength = [string]$MapLength
if ($Profile -eq 'earth') {
    # Earth 2.0 makes worlds only of its own play styles: the earth play style with its Earth tab settings (worldconfig.json
    # in the Earth zip). Its size option is a scale; "vanilla" keeps this world's width and length, which the checks'
    # distances assume Earth fills.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($EarthZip)
    try { $reader = [IO.StreamReader]::new($zip.GetEntry('worldconfig.json').Open()); $styles = ($reader.ReadToEnd() | ConvertFrom-Json).playStyles; $reader.Dispose() } finally { $zip.Dispose() }
    $style = $styles | Where-Object { $_.code -eq 'earth' } | Select-Object -First 1
    if (-not $style) { throw "Earth has no 'earth' play style: $EarthZip" }
    $settings.WorldConfig.PlayStyle = 'earth'; $settings.WorldConfig.PlayStyleLangCode = 'preset-earth'
    foreach ($p in $style.worldConfig.PSObject.Properties) {
        # the template's own choices (its size, edge, game mode and spawn) stay
        if (-not $settings.WorldConfig.WorldConfiguration.PSObject.Properties[$p.Name]) { $settings.WorldConfig.WorldConfiguration | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value }
    }
    $settings.WorldConfig.WorldConfiguration | Add-Member -NotePropertyName earthWorldSize -NotePropertyValue 'vanilla' -Force
    $settings.WorldConfig.WorldConfiguration | Add-Member -NotePropertyName earthOreDeposits -NotePropertyValue $EarthOreDeposits -Force
    if ($EarthRocks) { $settings.WorldConfig.WorldConfiguration | Add-Member -NotePropertyName earthRocks -NotePropertyValue $EarthRocks -Force }
    $settings.WorldConfig.WorldConfiguration | Add-Member -NotePropertyName earthWorldHeight -NotePropertyValue $MapHeight -Force
    # no downloads: Earth's data comes from the player's store (data, ground and cache copied, glo90 linked as a junction)
    $data = Join-Path $env:APPDATA 'VintagestoryData\ModData\earth'
    if (Test-Path -LiteralPath $data) {
        New-Item -ItemType Directory -Force (Join-Path $runPath 'ModData\earth') | Out-Null
        foreach ($sub in 'data','ground','cache') { if (Test-Path -LiteralPath (Join-Path $data $sub)) { Copy-Item -LiteralPath (Join-Path $data $sub) -Destination (Join-Path $runPath 'ModData\earth') -Recurse } }
        if (Test-Path -LiteralPath (Join-Path $data 'glo90')) { New-Item -ItemType Junction -Path (Join-Path $runPath 'ModData\earth\glo90') -Target (Join-Path $data 'glo90') | Out-Null }
    }
    $off = 'http://127.0.0.1:9/'
    [ordered]@{ ConfigVersion=5; SelfTest=@{ Enabled=$false }; DataRepo=$off; ElevationRepo=$off; ElevationSource=$off; GroundRepo=$off } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runPath 'ModConfig\earth.json')
}
if ($VerticalBudget) { $settings.WorldConfig.WorldConfiguration | Add-Member -NotePropertyName earthVerticalBudget -NotePropertyValue $VerticalBudget -Force }
$settings.ModPaths = @('Mods',(Join-Path $runPath 'Mods'))
$settings.WorldConfig.SaveFileLocation = Join-Path $runPath 'Saves\wmap-smoke.vcdbs'
$settings.WorldConfig.WorldName = 'Wmap isolated integration test'
$settings.WorldConfig.Seed = '918273'
$settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $runPath 'serverconfig.json')
Set-Content -LiteralPath (Join-Path $runPath 'ModConfig\wmap.json') -Value '{"AllowAllPlayers":true,"WorldChartSize":512}'
@{PolarIceMode=$PolarMode} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'ModConfig\cylinderworld.json')
@{OverviewWidth=$OverviewWidth} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'ModConfig\wmap-smoke.json')
Write-Output "Isolated test directory: $runPath"
$process = Start-Process -FilePath (Join-Path $GamePath 'VintagestoryServer.exe') -ArgumentList @('--dataPath',('"' + $runPath + '"')) -WorkingDirectory $GamePath -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runPath 'console.log') -RedirectStandardError (Join-Path $runPath 'stderr.log')
$process.Id | Set-Content -LiteralPath (Join-Path $runPath 'process-id.txt')
Write-Output "Server PID: $($process.Id)"
Write-Output $runPath
if ($Wait) {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force
        throw "Smoke server exceeded $TimeoutSeconds seconds; stopped this run's PID $($process.Id)."
    }
    $report = Join-Path $runPath 'WmapSmoke\report.txt'
    if (-not (Test-Path -LiteralPath $report)) { throw "Smoke server exited without a report: $runPath" }
    $result = Get-Content -LiteralPath $report -Raw
    Write-Output $result
    if (-not $result.StartsWith('PASS')) { throw "Smoke failed: $report" }
}
