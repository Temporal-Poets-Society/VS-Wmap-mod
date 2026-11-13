param(
    [Parameter(Mandatory=$true)][string]$EarthRoot,
    [string]$Output = '.local/playtest-after',
    [string]$BaselineAssembly,
    [ValidateSet(409600,510976)][int]$RingWidth = 510976
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
$previousAppData = $env:APPDATA
$previousDotnetHome = $env:DOTNET_CLI_HOME
$previousBaseline = $env:WMAP_BASELINE
try {
    # Keep SDK state inside this working copy; shared compiler servers can have another sandbox's file permissions.
    $env:APPDATA = Join-Path $root '.local/appdata'
    $env:DOTNET_CLI_HOME = Join-Path $root '.local/dotnet'
    New-Item -ItemType Directory -Force $env:APPDATA,$env:DOTNET_CLI_HOME | Out-Null
    $config = Join-Path $root '.local/NuGet.Config'
    Set-Content -LiteralPath $config '<configuration><packageSources><clear /></packageSources></configuration>'
    & dotnet build -c Release -p:UseSharedCompilation=false "-p:RestoreConfigFile=$config"
    if ($LASTEXITCODE) { throw 'Wmap build failed' }
    & dotnet run --project tests/WmapTests -c Release -p:UseSharedCompilation=false "-p:RestoreConfigFile=$config"
    if ($LASTEXITCODE) { throw 'Core checks failed' }
    & dotnet build tests/PlaytestTests -c Release -p:UseSharedCompilation=false "-p:RestoreConfigFile=$config" "-p:EarthRoot=$EarthRoot"
    if ($LASTEXITCODE) { throw 'Playtest checks build failed' }
    $target = Join-Path $root 'tests/PlaytestTests/bin/Release/net8.0/Wmap.dll'
    if ($BaselineAssembly) {
        Copy-Item -LiteralPath $BaselineAssembly -Destination $target
        $env:WMAP_BASELINE = '1'
    } else { $env:WMAP_BASELINE = '' }
    & dotnet tests/PlaytestTests/bin/Release/net8.0/PlaytestTests.dll (Join-Path $EarthRoot 'earthdata') $Output $RingWidth
    if ($LASTEXITCODE) { throw 'Playtest checks failed' }
} finally {
    if ($BaselineAssembly -and (Test-Path 'bin/Release/mod/Wmap.dll')) { Copy-Item 'bin/Release/mod/Wmap.dll' 'tests/PlaytestTests/bin/Release/net8.0/Wmap.dll' }
    $env:APPDATA = $previousAppData
    $env:DOTNET_CLI_HOME = $previousDotnetHome
    $env:WMAP_BASELINE = $previousBaseline
    Pop-Location
}
