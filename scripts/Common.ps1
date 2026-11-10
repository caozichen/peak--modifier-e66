$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DotNet = if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    Join-Path $env:DOTNET_ROOT 'dotnet.exe'
} else { (Get-Command dotnet -ErrorAction Stop).Source }

function Invoke-DotNet([string[]]$Arguments) {
    & $DotNet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed with exit code ' + $LASTEXITCODE) }
}
function Restore-Project([string]$Project, [string]$Config, [string[]]$Properties) {
    $restoreArgs = @('restore', $Project) + $Properties
    if ($Config) { $restoreArgs += @('--configfile', [IO.Path]::GetFullPath($Config)) }
    Invoke-DotNet $restoreArgs
}
function Get-ReferenceProperties([string]$GameDir, [string]$BepInExDir) {
    if (-not $GameDir) { throw 'Supply -GameDir or set PEAK_GAME_DIR to the PEAK installation directory.' }
    $game = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
    foreach ($relative in @('PEAK.exe','PEAK_Data\Managed\Assembly-CSharp.dll','PEAK_Data\Managed\Newtonsoft.Json.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $game $relative) -PathType Leaf)) { throw ('Missing game file: ' + $relative) }
    }
    $core = if ($BepInExDir) { [IO.Path]::GetFullPath($BepInExDir) } else { Join-Path $RepoRoot '.dependencies\BepInEx\BepInEx\core' }
    foreach ($name in @('BepInEx.dll','0Harmony.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $core $name) -PathType Leaf)) { throw 'Prepare BepInEx first with scripts/Prepare-Dependencies.ps1, or supply -BepInExDir.' }
    }
    return @("-p:GameDir=$game", "-p:BepInExDir=$core")
}
