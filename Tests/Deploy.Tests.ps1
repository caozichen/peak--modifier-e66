param([string]$ScratchRoot)
$ErrorActionPreference='Stop'
if (-not $ScratchRoot) { throw 'Provide an absolute scratch path inside the workspace.' }
$testRoot=[IO.Path]::GetFullPath($ScratchRoot)
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
function New-Case([string]$name) {
    $package=Join-Path $testRoot ($name+'\package')
    $game=Join-Path $testRoot ($name+'\game')
    New-Item -ItemType Directory -Force -Path $package,$game,(Join-Path $package 'GameFiles\BepInEx\plugins\PeakWorkshop'),(Join-Path $package 'GameFiles\BepInEx\core') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\scripts\legacy\Deploy.ps1') -Destination (Join-Path $package 'Deploy.ps1')
    Set-Content -LiteralPath (Join-Path $package 'GameFiles\winhttp.dll') -Value 'mock-loader'
    Set-Content -LiteralPath (Join-Path $package 'GameFiles\BepInEx\core\BepInEx.dll') -Value 'mock-core'
    Set-Content -LiteralPath (Join-Path $package 'GameFiles\BepInEx\plugins\PeakWorkshop\PeakWorkshop.Plugin.dll') -Value 'mock-plugin'
    Set-Content -LiteralPath (Join-Path $game 'PEAK.exe') -Value 'must-remain'
    return @{Package=$package; Game=$game; Script=(Join-Path $package 'Deploy.ps1')}
}
function Assert([bool]$value,[string]$reason) { if(-not $value) { throw $reason } }
$plain=New-Case 'plain'
& $plain.Script -GameDir $plain.Game
& $plain.Script -GameDir $plain.Game -Uninstall
Assert (-not(Test-Path -LiteralPath (Join-Path $plain.Game 'winhttp.dll'))) 'Loader not removed.'
Assert (Test-Path -LiteralPath (Join-Path $plain.Game 'PEAK.exe')) 'Unrelated game file removed.'
$refused=$false
try { & $plain.Script -GameDir $plain.Game -Uninstall } catch { $refused=$true }
Assert $refused 'Repeated uninstall was not refused.'
& $plain.Script -GameDir $plain.Game
& $plain.Script -GameDir $plain.Game -Uninstall

$shared=New-Case 'shared'
& $shared.Script -GameDir $shared.Game
New-Item -ItemType Directory -Force -Path (Join-Path $shared.Game 'BepInEx\plugins\OtherMod') | Out-Null
Set-Content -LiteralPath (Join-Path $shared.Game 'BepInEx\plugins\OtherMod\Other.dll') -Value 'foreign-mod'
& $shared.Script -GameDir $shared.Game -Uninstall
Assert (Test-Path -LiteralPath (Join-Path $shared.Game 'winhttp.dll')) 'Shared loader was removed.'
Assert (Test-Path -LiteralPath (Join-Path $shared.Game 'BepInEx\core\BepInEx.dll')) 'Shared core was removed.'
Assert (Test-Path -LiteralPath (Join-Path $shared.Game 'BepInEx\plugins\OtherMod\Other.dll')) 'Foreign mod was removed.'
Assert (-not(Test-Path -LiteralPath (Join-Path $shared.Game 'BepInEx\plugins\PeakWorkshop\PeakWorkshop.Plugin.dll'))) 'Own plugin not removed.'

$tampered=New-Case 'tampered'
& $tampered.Script -GameDir $tampered.Game
Set-Content -LiteralPath (Join-Path $tampered.Game 'winhttp.dll') -Value 'changed-by-other-tool'
$refused=$false
try { & $tampered.Script -GameDir $tampered.Game -Uninstall } catch { $refused=$true }
Assert $refused 'Changed file not protected.'
Assert (Test-Path -LiteralPath (Join-Path $tampered.Game 'BepInEx\plugins\PeakWorkshop\PeakWorkshop.Plugin.dll')) 'Partial removal occurred before all checks passed.'
Write-Output 'PASS install/remove/reinstall; repeated uninstall rejection; foreign mod and shared loader protection; changed-file protection; unrelated file preservation.'
