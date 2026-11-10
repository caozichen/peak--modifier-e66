param(
    [Parameter(Mandatory=$true)][string]$OldPackage,
    [Parameter(Mandatory=$true)][string]$GameDir,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$oldRoot = [IO.Path]::GetFullPath($OldPackage).TrimEnd('\')
$gameRoot = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
$pluginRelative = 'BepInEx\plugins\PeakWorkshop\PeakWorkshop.Plugin.dll'

function Assert-NoReparsePath([string]$Path) {
    $walk = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($walk)) {
        $item = Get-Item -LiteralPath $walk -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Symbolic links and junctions are not allowed: ' + $walk)
        }
        $parent = [IO.Path]::GetDirectoryName($walk.TrimEnd('\'))
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $walk) { break }
        $walk = $parent
    }
}
function Get-SafePath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or $Relative -match '(^|[\\/])\.\.?([\\/]|$)' -or $Relative -match '[\\/]{2}') {
        throw ('Invalid relative path in manifest: ' + $Relative)
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $resolved.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest escapes its root directory.' }
    Assert-NoReparsePath $resolved
    return $resolved
}
function Assert-ProcessesStopped {
    if (@(Get-Process -Name PEAK,PeakWorkshop -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'Close PEAK and every PEAK Workshop window before upgrading. No game is started by this script.'
    }
}
function Assert-Hash([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '^[0-9A-Fa-f]{64}$' -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ('Missing file or invalid SHA256: ' + $Path)
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) { throw ('SHA256 mismatch: ' + $Path) }
}
function Write-AtomicManifest([string]$Path, $Value, [string]$Stage) {
    $temp = Join-Path ([IO.Path]::GetDirectoryName($Path)) ('.installed-files-' + $Stage + '-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $previous = $temp + '.previous'
    Assert-NoReparsePath $Path
    try {
        $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temp -Encoding UTF8
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, $previous) }
        else { [IO.File]::Move($temp, $Path) }
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
        if ([IO.File]::Exists($previous)) { [IO.File]::Delete($previous) }
    }
}

Assert-NoReparsePath $packageRoot
Assert-NoReparsePath $oldRoot
Assert-NoReparsePath $gameRoot
if ($packageRoot -eq $oldRoot -or $packageRoot -eq $gameRoot -or $oldRoot -eq $gameRoot) { throw 'Package and game directories must be distinct.' }
if (-not (Test-Path -LiteralPath (Get-SafePath $gameRoot 'PEAK.exe') -PathType Leaf)) { throw 'PEAK.exe was not found in GameDir.' }
Assert-ProcessesStopped
$oldManifestPath = Get-SafePath $oldRoot 'installed-files.json'
$newManifestPath = Get-SafePath $packageRoot 'installed-files.json'
if (Test-Path -LiteralPath $newManifestPath) { throw 'The new package already has an installation manifest. Refusing to overwrite installation state.' }
$manifest = Get-Content -Raw -LiteralPath $oldManifestPath -Encoding UTF8 | ConvertFrom-Json
if ($manifest.installed -ne $true -or $manifest.pluginVersion -ne '0.2.5') { throw 'An active 0.2.5 installation manifest is required.' }
if ([IO.Path]::GetFullPath($manifest.gameRoot).TrimEnd('\') -ne $gameRoot) { throw 'Old manifest belongs to a different game directory.' }

# Validate the release manifest, including the desktop and upgrade scripts. The
# manifest is an integrity check for this local package, not a remote signature.
$releaseManifestPath = Get-SafePath $packageRoot 'SHA256清单.json'
$releaseFiles = Get-Content -Raw -LiteralPath $releaseManifestPath -Encoding UTF8 | ConvertFrom-Json
$releaseFiles = @($releaseFiles)
if ($releaseFiles.Count -eq 0) { throw 'Release SHA256 manifest is empty.' }
$releaseMap = @{}
foreach ($file in $releaseFiles) {
    $relative = ([string]$file.path).Replace('/', '\')
    $path = Get-SafePath $packageRoot $relative
    if ($releaseMap.ContainsKey($relative)) { throw ('Duplicate release path: ' + $relative) }
    $releaseMap[$relative] = $file
    Assert-Hash $path ([string]$file.sha256)
    if ((Get-Item -LiteralPath $path).Length -ne [long]$file.bytes) { throw ('Release file length mismatch: ' + $relative) }
}

$payloadRoot = Get-SafePath $packageRoot 'data\game-files'
Assert-NoReparsePath $payloadRoot
# Reject links before recursive enumeration can traverse a linked directory.
$pending = New-Object 'System.Collections.Generic.Queue[string]'
$pending.Enqueue($payloadRoot)
$payloadFiles = @()
while ($pending.Count -gt 0) {
    $directory = $pending.Dequeue()
    foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Payload contains a symbolic link or junction: ' + $item.FullName) }
        if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        else { $payloadFiles += $item }
    }
}
$oldMap = @{}
$files = @()
foreach ($file in @($manifest.files)) {
    $relative = ([string]$file.path).Replace('/', '\')
    if ($oldMap.ContainsKey($relative)) { throw ('Duplicate installed path: ' + $relative) }
    $oldMap[$relative] = $file
    $target = Get-SafePath $gameRoot $relative
    Assert-Hash $target ([string]$file.sha256)
    $payload = Get-SafePath $payloadRoot $relative
    $releaseRelative = 'data\game-files\' + $relative
    if (-not $releaseMap.ContainsKey($releaseRelative)) { throw ('Installed file is missing from the new release manifest: ' + $relative) }
    $payloadHash = [string]$releaseMap[$releaseRelative].sha256
    Assert-Hash $payload $payloadHash
    if ($relative -ne $pluginRelative -and $payloadHash -ne $file.sha256) { throw ('Upgrade would change a shared loader file: ' + $relative) }
    $files += [pscustomobject]@{ path = $relative; sha256 = $payloadHash.ToUpperInvariant() }
}
if (-not $oldMap.ContainsKey($pluginRelative)) { throw 'The old manifest does not contain the PEAK Workshop plugin.' }
if ($payloadFiles.Count -ne $oldMap.Count) { throw 'The new data\game-files payload and old installation manifest have different file counts.' }
foreach ($file in $payloadFiles) {
    $relative = $file.FullName.Substring($payloadRoot.Length + 1)
    if (-not $oldMap.ContainsKey($relative)) { throw ('Unexpected new payload file: ' + $relative) }
}
$targetDll = Get-SafePath $gameRoot $pluginRelative
$newDll = Get-SafePath $payloadRoot $pluginRelative
$oldIdentity = [Reflection.AssemblyName]::GetAssemblyName($targetDll)
$newIdentity = [Reflection.AssemblyName]::GetAssemblyName($newDll)
if ($oldIdentity.Name -ne 'PeakWorkshop.Plugin' -or $oldIdentity.Version -ne [Version]'0.2.5.0') { throw 'Installed plugin identity/version is not PeakWorkshop.Plugin 0.2.5.0.' }
if ($newIdentity.Name -ne 'PeakWorkshop.Plugin' -or $newIdentity.Version -ne [Version]'0.2.6.0') { throw 'Replacement plugin identity/version is not PeakWorkshop.Plugin 0.2.6.0.' }
$oldHash = [string]$oldMap[$pluginRelative].sha256
$newHash = [string]$releaseMap[('data\game-files\' + $pluginRelative)].sha256
$oldManifestHash = (Get-FileHash -LiteralPath $oldManifestPath -Algorithm SHA256).Hash
Assert-ProcessesStopped
if ($CheckOnly) {
    [pscustomobject]@{ check='passed'; from='0.2.5'; to='0.2.6'; gameRoot=$gameRoot; verifiedInstalledFiles=$files.Count; gameFilesToReplace=1; changedFiles=0 } | ConvertTo-Json
    return
}

$backupRoot = Get-SafePath $packageRoot ('回滚备份\0.2.5-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$backupDll = Join-Path $backupRoot 'PeakWorkshop.Plugin.0.2.5.dll'
$backupManifest = Join-Path $backupRoot 'installed-files.0.2.5.json'
Copy-Item -LiteralPath $targetDll -Destination $backupDll
Copy-Item -LiteralPath $oldManifestPath -Destination $backupManifest
Assert-Hash $backupDll $oldHash
Assert-Hash $backupManifest $oldManifestHash
$newManifest = [pscustomobject]@{
    gameRoot=$gameRoot; pluginVersion='0.2.6'; installed=$true
    createdUtc=[DateTime]::UtcNow.ToString('o'); upgradedFrom='0.2.5'
    oldPackage=$oldRoot; backupDirectory=$backupRoot; files=$files
}
$oldTransferred = Get-Content -Raw -LiteralPath $backupManifest -Encoding UTF8 | ConvertFrom-Json
$oldTransferred | Add-Member -NotePropertyName installed -NotePropertyValue $false -Force
$oldTransferred | Add-Member -NotePropertyName transferredTo -NotePropertyValue $packageRoot -Force
$oldTransferred | Add-Member -NotePropertyName transferredUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force

$mutationStarted = $false
try {
    Assert-ProcessesStopped
    Assert-Hash $targetDll $oldHash
    Assert-Hash $oldManifestPath $oldManifestHash
    # Re-check replacement bytes after preparing the backup.
    Assert-Hash $newDll $newHash
    $mutationStarted = $true
    Copy-Item -LiteralPath $newDll -Destination $targetDll -Force
    Assert-Hash $targetDll $newHash
    Assert-ProcessesStopped
    Write-AtomicManifest $newManifestPath $newManifest 'new'
    Write-AtomicManifest $oldManifestPath $oldTransferred 'old'
} catch {
    $originalError = $_
    $rollbackErrors = @()
    if ($mutationStarted) {
        try { [IO.File]::Copy($backupDll, $targetDll, $true); Assert-Hash $targetDll $oldHash }
        catch { $rollbackErrors += ('DLL: ' + $_.Exception.Message) }
        try { [IO.File]::Copy($backupManifest, $oldManifestPath, $true); Assert-Hash $oldManifestPath $oldManifestHash }
        catch { $rollbackErrors += ('old manifest: ' + $_.Exception.Message) }
        try { if ([IO.File]::Exists($newManifestPath)) { [IO.File]::Delete($newManifestPath) } }
        catch { $rollbackErrors += ('new manifest: ' + $_.Exception.Message) }
    }
    if ($rollbackErrors.Count -gt 0) {
        throw ('Upgrade failed: ' + $originalError.Exception.Message + '; automatic rollback also failed: ' + ($rollbackErrors -join '; ') + '. Backups: ' + $backupRoot)
    }
    throw ('Upgrade failed; original DLL and installation state restored. ' + $originalError.Exception.Message + ' Backups: ' + $backupRoot)
}
Write-Output 'Upgrade complete: only PeakWorkshop.Plugin.dll was replaced with 0.2.6. Shared loader files were verified and retained.'
Write-Output ('Backup: ' + $backupRoot)
Write-Output 'Use this 0.2.6 package for launch/uninstall. The old package installation manifest is marked transferred.'
