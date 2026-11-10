param([string]$BepInExArchive)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dependencyRoot = Join-Path $root '.dependencies'
$target = Join-Path $dependencyRoot 'BepInEx'
$expected = '6C57A714A892EFB4E9E1FEF63D608A3391F30EC984031C465404E4EE381A0755'
New-Item -ItemType Directory -Force -Path $dependencyRoot | Out-Null
if (-not $BepInExArchive) {
    $BepInExArchive = Join-Path $dependencyRoot 'BepInEx_win_x64_5.4.23.6.zip'
    if (-not (Test-Path -LiteralPath $BepInExArchive)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip' -OutFile $BepInExArchive
    }
}
$archive = [IO.Path]::GetFullPath($BepInExArchive)
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'BepInEx archive checksum mismatch; no files extracted.' }
if (Test-Path -LiteralPath $target) { throw 'The dependency folder already exists. Use it, or choose a clean checkout before preparing dependencies again.' }
Expand-Archive -LiteralPath $archive -DestinationPath $target
Write-Output ('Prepared BepInEx 5.4.23.6 x64 Mono: ' + $target)
