param([string]$GameDir = $env:PEAK_GAME_DIR, [string]$BepInExDir, [string]$NuGetConfig)
. (Join-Path $PSScriptRoot 'Common.ps1')
$properties = @(Get-ReferenceProperties $GameDir $BepInExDir)
$core = if ($BepInExDir) { [IO.Path]::GetFullPath($BepInExDir) } else { Join-Path $RepoRoot '.dependencies\BepInEx\BepInEx\core' }
$project = Join-Path $RepoRoot 'validation\game-assembly\game-assembly.csproj'
$report = Join-Path $RepoRoot 'artifacts\game-assembly.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $report) | Out-Null
Push-Location $RepoRoot
try {
    Restore-Project $project $NuGetConfig $properties
    Invoke-DotNet (@('run','--project',$project,'-c','Release','--no-restore') + $properties + @('--',(Join-Path ([IO.Path]::GetFullPath($GameDir)) 'PEAK_Data\Managed'),$core,(Join-Path $RepoRoot 'Plugin\bin\Release\netstandard2.1\PeakWorkshop.Plugin.dll'),$report))
    Write-Output ('Read-only game metadata and IL analysis: ' + $report)
} finally { Pop-Location }
