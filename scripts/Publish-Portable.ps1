param(
    [string]$GameDir = $env:PEAK_GAME_DIR,
    [string]$BepInExDir,
    [string]$NuGetConfig,
    [string]$OutputDir,
    [switch]$SkipBuild,
    [switch]$Zip
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$properties = @(Get-ReferenceProperties $GameDir $BepInExDir)
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'artifacts\这次我一定要爬到顶' }
$output = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $output) { throw 'Output directory already exists. Choose a new empty output location; runtime settings and backups will never be overwritten.' }
if ($Zip -and (Test-Path -LiteralPath ($output + '.zip'))) { throw 'The ZIP already exists. Choose a new output name.' }
$loader = if ($BepInExDir) { Split-Path -Parent (Split-Path -Parent ([IO.Path]::GetFullPath($BepInExDir))) } else { Join-Path $RepoRoot '.dependencies\BepInEx' }
Push-Location $RepoRoot
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -GameDir $GameDir -BepInExDir $BepInExDir -NuGetConfig $NuGetConfig }
    $policy = [IO.File]::ReadAllText((Join-Path $RepoRoot 'Desktop\InstallationPolicy.cs'))
    $matches = [regex]::Matches($policy,'\["([^"]+)"\]\s*=\s*"([A-F0-9]{64})"')
    if ($matches.Count -ne 23) { throw 'Unexpected installation policy payload count.' }
    $pluginVersion = ([xml](Get-Content -Raw -LiteralPath (Join-Path $RepoRoot 'Plugin\PeakWorkshop.Plugin.csproj'))).Project.PropertyGroup.Version
    $baseline = [regex]::Match($policy,'SupportedGameHash = "([A-F0-9]{64})"').Groups[1].Value
    $files = @()
    # Verify all inputs before creating a release directory.
    foreach ($match in $matches) {
        $relative = $match.Groups[1].Value
        $expected = $match.Groups[2].Value
        $source = if ($relative -eq 'BepInEx/plugins/PeakWorkshop/PeakWorkshop.Plugin.dll') {
            Join-Path $RepoRoot 'Plugin\bin\Release\netstandard2.1\PeakWorkshop.Plugin.dll'
        } else { Join-Path $loader $relative }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expected) { throw ('Payload checksum mismatch: ' + $relative) }
        $files += [pscustomobject]@{ path=$relative; sha256=$expected; source=$source }
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    $publish = Join-Path $output '.publish'
    $project = Join-Path $RepoRoot 'Desktop\PeakWorkshop.Desktop.csproj'
    Restore-Project $project $NuGetConfig $properties
    Invoke-DotNet (@('publish',$project,'-c','Release','--no-restore','-o',$publish) + $properties)
    # Keep runtime/license notices emitted by the SDK alongside the single EXE.
    foreach ($item in Get-ChildItem -LiteralPath $publish -Force) { Move-Item -LiteralPath $item.FullName -Destination $output }
    Remove-Item -LiteralPath $publish
    foreach ($file in $files) {
        $target = Join-Path $output ('data/game-files/' + $file.path)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.source -Destination $target
    }
    $utf8 = New-Object Text.UTF8Encoding($false)
    $manifest = [pscustomobject]@{schema=1;pluginVersion=[string]$pluginVersion;supportedAssemblySha256=$baseline;files=@($files | Select-Object path,sha256)}
    [IO.File]::WriteAllText((Join-Path $output 'data/payload.json'),($manifest | ConvertTo-Json -Depth 6),$utf8)
    [IO.File]::WriteAllText((Join-Path $output 'settings.json'),'{"gamePath":"","values":{}}',$utf8)
    foreach ($name in @('logs','backups','data/licenses')) { New-Item -ItemType Directory -Force -Path (Join-Path $output $name) | Out-Null }
    foreach ($license in Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'licenses') -File -Filter '*.txt') {
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $output 'data/licenses')
    }
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $output 'data/licenses')
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'docs\user-guide.md') -Destination (Join-Path $output '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'docs\features.md') -Destination (Join-Path $output 'data\功能说明.md')
    [IO.File]::WriteAllText((Join-Path $output 'Launch.cmd'),"@echo off`r`nstart `"`" `"%~dp0PeakWorkshop.exe`"`r`n",[Text.Encoding]::ASCII)
    $rootPrefix = $output.TrimEnd('\') + '\'
    $checksums = @(Get-ChildItem -LiteralPath $output -File -Recurse -Force | ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($rootPrefix.Length).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    [IO.File]::WriteAllText((Join-Path $output 'files.sha256.json'),($checksums | ConvertTo-Json -Depth 4),$utf8)
    if ($Zip) { Compress-Archive -LiteralPath $output -DestinationPath ($output + '.zip') }
    Write-Output ('Portable folder prepared: ' + $output)
    Write-Output 'No installed game files were changed. Open PeakWorkshop.exe to manage installation.'
} finally { Pop-Location }
