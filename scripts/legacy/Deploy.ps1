# Historical GameFiles-layout installer; use the desktop manager for current portable builds.
param(
    [Parameter(Mandatory=$true)][string]$GameDir,
    [switch]$Uninstall,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$payloadRoot = Join-Path $packageRoot 'GameFiles'
$manifestPath = Join-Path $packageRoot 'installed-files.json'
$gameRoot = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'PEAK.exe'))) { throw 'PEAK.exe was not found in GameDir.' }
if (Get-Process -Name PEAK -ErrorAction SilentlyContinue) { throw 'Close PEAK before installing or removing plugin files.' }
function Get-TargetPath([string]$relative) {
    $target = [IO.Path]::GetFullPath((Join-Path $gameRoot $relative))
    if (-not $target.StartsWith($gameRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid path in manifest.' }
    $walk = $target
    while ($walk.Length -ge $gameRoot.Length) {
        if ((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'A symbolic link or junction was found in a target path. No automatic file changes allowed.'
        }
        if ($walk -eq $gameRoot) { break }
        $walk = Split-Path -Parent $walk
    }
    return $target
}
if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'No installation manifest is available. No files removed.' }
    $installed = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($installed.gameRoot -ne $gameRoot) { throw 'Manifest belongs to a different game directory.' }
    if ($installed.installed -ne $true) { throw 'This manifest is already uninstalled. No files removed.' }
    $allowedPaths = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($payloadRoot.Length).TrimStart('\') })
    foreach ($file in $installed.files) {
        if ($file.path -notin $allowedPaths) { throw 'Manifest contains a file outside the package payload. No files removed.' }
    }
    $known = @($installed.files | ForEach-Object { $_.path })
    $foreignMods = @()
    foreach ($subdir in @('BepInEx\plugins','BepInEx\patchers','BepInEx\core')) {
        $directory = Get-TargetPath $subdir
        if (Test-Path -LiteralPath $directory) {
            $foreignMods += @(Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.dll' | Where-Object {
                $_.FullName.Substring($gameRoot.Length).TrimStart('\') -notin $known
            })
        }
    }
    $removeFiles = @($installed.files)
    if ($foreignMods.Count -gt 0) {
        $removeFiles = @($installed.files | Where-Object { $_.path -like 'BepInEx\plugins\PeakWorkshop\*' })
        Write-Output 'Other mods were detected. Only PEAK Workshop plugin files will be removed; shared loader/core will remain.'
    }
    foreach ($file in $removeFiles) {
        $target = Get-TargetPath $file.path
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) {
                throw ('File was modified after installation; no automatic removal: ' + $file.path)
            }
        }
    }
    if ($CheckOnly) { Write-Output 'Removal check passed. No files changed.'; exit 0 }
    foreach ($file in $removeFiles) {
        $target = Get-TargetPath $file.path
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    $installed | Add-Member -NotePropertyName installed -NotePropertyValue $false -Force
    $installed | Add-Member -NotePropertyName sharedLoaderRetained -NotePropertyValue ($foreignMods.Count -gt 0) -Force
    $installed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Output 'Installed files removed. Generated logs/configs and empty folders were retained.'
    exit 0
}
$payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Force)
if ($payloadFiles.Count -eq 0) { throw 'GameFiles payload is empty.' }
$previous = $null
if (Test-Path -LiteralPath $manifestPath) {
    $previous = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($previous.gameRoot -ne $gameRoot -or $previous.installed -ne $false) {
        throw 'An active installation manifest already exists. Use the existing installation or uninstall it first.'
    }
}
if ((Test-Path -LiteralPath (Join-Path $gameRoot 'BepInEx')) -and $null -eq $previous) {
    throw 'An existing BepInEx installation was found. It will not be overwritten automatically.'
}
$files = @()
foreach ($file in $payloadFiles) {
    $relative = $file.FullName.Substring($payloadRoot.Length).TrimStart('\')
    $target = Get-TargetPath $relative
    if (Test-Path -LiteralPath $target) { throw ('Existing file will not be overwritten: ' + $relative) }
    $files += [pscustomobject]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
if ($CheckOnly) {
    [pscustomobject]@{ gameRoot=$gameRoot; newFiles=$files.Count; existingFilesOverwritten=0; check='passed' } | ConvertTo-Json
    exit 0
}
$copied = @()
try {
    foreach ($file in $files) {
        $target = Get-TargetPath $file.path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $payloadRoot $file.path) -Destination $target
        $copied += $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Post-copy checksum mismatch.' }
    }
    [pscustomobject]@{ gameRoot=$gameRoot; pluginVersion='0.2.4'; installed=$true; createdUtc=[DateTime]::UtcNow.ToString('o'); files=$files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Output ('Installed and verified ' + $files.Count + ' new files. You can now start PEAK.')
} catch {
    foreach ($target in $copied) { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target } }
    throw
}
