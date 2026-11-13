param([string]$GamePath = "$env:APPDATA\Vintagestory")
$ErrorActionPreference = 'Stop'
$destination = Join-Path (Split-Path $PSScriptRoot) '.local\game'
$references = @(
    'VintagestoryAPI.dll',
    'VintagestoryAPI.xml',
    'VintagestoryLib.dll',
    'VintagestoryServer.dll',
    'Mods\VSEssentials.dll',
    'Mods\VSSurvivalMod.dll',
    'Lib\cairo-sharp.dll',
    'Lib\Newtonsoft.Json.dll',
    'Lib\protobuf-net.dll',
    'Lib\SkiaSharp.dll',
    'Lib\OpenTK.Core.dll',
    'Lib\OpenTK.Graphics.dll',
    'Lib\OpenTK.Mathematics.dll',
    'Lib\OpenTK.Windowing.Common.dll',
    'Lib\OpenTK.Windowing.Desktop.dll',
    'Lib\OpenTK.Windowing.GraphicsLibraryFramework.dll'
)
foreach ($reference in $references) {
    $source = Join-Path $GamePath $reference
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing Vintage Story build reference: $source" }
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($reference in $references) {
    Copy-Item -LiteralPath (Join-Path $GamePath $reference) -Destination $destination
}
Write-Output "Prepared local build references in $destination"
