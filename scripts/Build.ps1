param(
    [string]$GameDir = $env:PEAK_GAME_DIR,
    [string]$BepInExDir,
    [string]$NuGetConfig,
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release'
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$properties = @(Get-ReferenceProperties $GameDir $BepInExDir)
Push-Location $RepoRoot
try {
    $pluginProject = Join-Path $RepoRoot 'Plugin\PeakWorkshop.Plugin.csproj'
    Restore-Project $pluginProject $NuGetConfig $properties
    Invoke-DotNet (@('build',$pluginProject,'-c',$Configuration,'--no-restore') + $properties)
    $plugin = Join-Path $RepoRoot "Plugin\bin\$Configuration\netstandard2.1\PeakWorkshop.Plugin.dll"
    $hash = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash
    # The installer pins the bytes of this build; it does not gate game versions.
    $policyPath = Join-Path $RepoRoot 'Desktop\InstallationPolicy.cs'
    $policy = [IO.File]::ReadAllText($policyPath)
    $pattern = '(\["BepInEx/plugins/PeakWorkshop/PeakWorkshop.Plugin.dll"\]\s*=\s*")[A-F0-9]{64}("[ ,]*)'
    if ([regex]::Matches($policy,$pattern).Count -ne 1) { throw 'Expected exactly one plugin integrity hash in InstallationPolicy.cs.' }
    $policy = [regex]::Replace($policy,$pattern,('${1}' + $hash + '${2}'))
    $version = ([xml](Get-Content -Raw -LiteralPath $pluginProject)).Project.PropertyGroup.Version
    $policy = [regex]::Replace($policy,'(PluginVersion \{ get; init; \} = ")[^"]+(";)',('${1}' + $version + '${2}'))
    [IO.File]::WriteAllText($policyPath,$policy,(New-Object Text.UTF8Encoding($false)))
    $desktopProject = Join-Path $RepoRoot 'Desktop\PeakWorkshop.Desktop.csproj'
    Restore-Project $desktopProject $NuGetConfig $properties
    Invoke-DotNet (@('build',$desktopProject,'-c',$Configuration,'--no-restore') + $properties)
    Write-Output ('Build complete. Plugin SHA256: ' + $hash)
} finally { Pop-Location }
