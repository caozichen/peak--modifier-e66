param(
    [string]$NewDll = (Join-Path $PSScriptRoot '..\..\Plugin\bin\Release\netstandard2.1\PeakWorkshop.Plugin.dll'),
    [Parameter(Mandatory=$true)][string]$OldDll
)
$ErrorActionPreference='Stop'
$ps51 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$runs = Join-Path $PSScriptRoot ('test-runs\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $runs | Out-Null
$plugin = 'BepInEx\plugins\PeakWorkshop\PeakWorkshop.Plugin.dll'
$utf8 = New-Object Text.UTF8Encoding($true)
function Assert($Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function SaveJson($Value,[string]$Path) { [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 10),$utf8) }
function Fixture([string]$Name) {
    $root=Join-Path $runs $Name
    $game=Join-Path $root 'game'; $old=Join-Path $root 'old'; $new=Join-Path $root 'new'
    foreach($path in @($game,$old,$new,(Join-Path $game (Split-Path $plugin)),(Join-Path $new ('data\game-files\'+(Split-Path $plugin))))) {
        New-Item -ItemType Directory -Force $path | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $game 'PEAK.exe'),'fixture only')
    [IO.File]::WriteAllText((Join-Path $game 'shared-loader.bin'),'shared loader sentinel')
    Copy-Item -LiteralPath $OldDll -Destination (Join-Path $game $plugin)
    Copy-Item -LiteralPath $NewDll -Destination (Join-Path $new ('data\game-files\'+$plugin))
    Copy-Item -LiteralPath (Join-Path $game 'shared-loader.bin') -Destination (Join-Path $new 'data\game-files\shared-loader.bin')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Upgrade.ps1') -Destination (Join-Path $new 'Upgrade.ps1')
    $files=@([pscustomobject]@{path=$plugin;sha256=(Hash (Join-Path $game $plugin))},[pscustomobject]@{path='shared-loader.bin';sha256=(Hash (Join-Path $game 'shared-loader.bin'))})
    SaveJson ([pscustomobject]@{gameRoot=$game;pluginVersion='0.2.5';installed=$true;files=$files}) (Join-Path $old 'installed-files.json')
    $f=[pscustomobject]@{Root=$root;Game=$game;Old=$old;New=$new;OldDllHash=(Hash (Join-Path $game $plugin));OldManifestHash=(Hash (Join-Path $old 'installed-files.json'));SharedHash=(Hash (Join-Path $game 'shared-loader.bin'))}
    ReleaseManifest $f
    return $f
}
function ReleaseManifest($Fixture) {
    $files=@(Get-ChildItem -LiteralPath $Fixture.New -Recurse -File | Where-Object {$_.Name -ne 'SHA256清单.json'} | ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($Fixture.New.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Hash $_.FullName)}
    })
    SaveJson $files (Join-Path $Fixture.New 'SHA256清单.json')
}
function RunUpgrade($Fixture,[string]$Mode='normal',[switch]$CheckOnly,[switch]$RealProcesses) {
    $hostScript=Join-Path $Fixture.Root ('invoke-'+[Guid]::NewGuid().ToString('N')+'.ps1')
    # Only Get-Process is substituted for fixture filesystem tests because the
    # user's real game and trainer may be open. There is no bypass in Upgrade.ps1.
    $shim=@'
param($Upgrade,$Old,$Game,$Mode,$Check)
$ErrorActionPreference='Stop'
function global:Get-Process {
    param([string[]]$Name,$ErrorAction)
    if($Mode -eq 'running') { [pscustomobject]@{ProcessName='PEAK';Id=1} }
}
if($Mode -eq 'rollback') {
    function global:Set-Content {
        param([Parameter(ValueFromPipeline=$true)]$Value,[string]$LiteralPath,[string]$Encoding)
        process {
            if($LiteralPath -like '*.installed-files-old-*') { throw 'Injected old-manifest commit failure' }
            Microsoft.PowerShell.Management\Set-Content -LiteralPath $LiteralPath -Value $Value -Encoding $Encoding
        }
    }
}
try {
    & $Upgrade -OldPackage $Old -GameDir $Game -CheckOnly:([bool]::Parse($Check))
    exit 0
} catch { Write-Output $_.Exception.ToString(); exit 1 }
'@
    if($RealProcesses) {
        $shim=[regex]::Replace($shim,'(?ms)^function global:Get-Process \{.*?^\}\r?\n','')
        if($shim.Contains('function global:Get-Process')) { throw 'Real-process test must not retain a process substitute.' }
    }
    [IO.File]::WriteAllText($hostScript,$shim,$utf8)
    $output = & $ps51 -NoProfile -ExecutionPolicy Bypass -File $hostScript (Join-Path $Fixture.New 'Upgrade.ps1') $Fixture.Old $Fixture.Game $Mode ([string][bool]$CheckOnly) 2>&1 | Out-String
    return [pscustomobject]@{Code=$LASTEXITCODE;Output=$output}
}
function Unchanged($Fixture) {
    Assert ((Hash (Join-Path $Fixture.Game $plugin)) -eq $Fixture.OldDllHash) 'Original DLL was not retained/restored.'
    Assert ((Hash (Join-Path $Fixture.Old 'installed-files.json')) -eq $Fixture.OldManifestHash) 'Old manifest was not retained/restored byte for byte.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $Fixture.New 'installed-files.json'))) 'A new active manifest was left behind.'
    Assert ((Hash (Join-Path $Fixture.Game 'shared-loader.bin')) -eq $Fixture.SharedHash) 'Shared loader was changed.'
}
$results=New-Object 'System.Collections.Generic.List[object]'
function Test([string]$Name,[scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{name=$Name;passed=$true}); Write-Output ('PASS '+$Name) }
    catch { $results.Add([pscustomobject]@{name=$Name;passed=$false;error=$_.Exception.ToString()}); Write-Output ('FAIL '+$Name+': '+$_.Exception.Message) }
}
Test 'CheckOnly validates files without mutation' {
    $f=Fixture 'check';$r=RunUpgrade $f -CheckOnly; Assert ($r.Code -eq 0) $r.Output; Unchanged $f
}
Test 'Upgrade replaces only own DLL, backs up 0.2.5 and transfers manifests' {
    $f=Fixture 'success';$r=RunUpgrade $f;Assert ($r.Code -eq 0) $r.Output
    Assert ((Hash (Join-Path $f.Game $plugin)) -eq (Hash $NewDll)) 'New DLL was not installed.'
    Assert ((Hash (Join-Path $f.Game 'shared-loader.bin')) -eq $f.SharedHash) 'Shared loader changed.'
    $old=Get-Content -Raw -LiteralPath (Join-Path $f.Old 'installed-files.json')|ConvertFrom-Json
    $new=Get-Content -Raw -LiteralPath (Join-Path $f.New 'installed-files.json')|ConvertFrom-Json
    Assert ($old.installed -eq $false -and $old.transferredTo -eq $f.New) 'Old installation was not marked transferred.'
    Assert ($new.installed -eq $true -and $new.pluginVersion -eq '0.2.6' -and $new.files.Count -eq 2) 'Invalid new uninstall manifest.'
    Assert ((Hash (Join-Path $new.backupDirectory 'PeakWorkshop.Plugin.0.2.5.dll')) -eq $f.OldDllHash) 'DLL backup differs.'
    Assert ((Hash (Join-Path $new.backupDirectory 'installed-files.0.2.5.json')) -eq $f.OldManifestHash) 'Manifest backup differs.'
    $again=RunUpgrade $f;Assert ($again.Code -ne 0) 'Repeated upgrade overwrote installation state.'
}
Test 'Tampered installed shared file is rejected before mutation' {
    $f=Fixture 'old-hash';[IO.File]::AppendAllText((Join-Path $f.Game 'shared-loader.bin'),'tampered');$r=RunUpgrade $f
    Assert ($r.Code -ne 0 -and $r.Output.Contains('SHA256 mismatch')) $r.Output
    Assert ((Hash (Join-Path $f.Game $plugin)) -eq $f.OldDllHash) 'DLL changed after failed validation.'
}
Test 'Tampered replacement is rejected by release SHA256' {
    $f=Fixture 'new-hash';[IO.File]::AppendAllText((Join-Path $f.New ('data\game-files\'+$plugin)),'tampered');$r=RunUpgrade $f
    Assert ($r.Code -ne 0 -and $r.Output.Contains('SHA256 mismatch')) $r.Output;Unchanged $f
}
Test 'Wrong replacement assembly version is rejected' {
    $f=Fixture 'version';Copy-Item -LiteralPath $OldDll -Destination (Join-Path $f.New ('data\game-files\'+$plugin)) -Force;ReleaseManifest $f;$r=RunUpgrade $f
    Assert ($r.Code -ne 0 -and $r.Output.Contains('0.2.6.0')) $r.Output;Unchanged $f
}
Test 'Manifest traversal is rejected' {
    $f=Fixture 'traversal';$m=Get-Content -Raw -LiteralPath (Join-Path $f.Old 'installed-files.json')|ConvertFrom-Json;$m.files[1].path='..\escape.bin';SaveJson $m (Join-Path $f.Old 'installed-files.json')
    $r=RunUpgrade $f;Assert ($r.Code -ne 0 -and $r.Output.Contains('Invalid relative path')) $r.Output
    Assert ((Hash (Join-Path $f.Game $plugin)) -eq $f.OldDllHash) 'DLL changed.'
}
Test 'Unexpected payload file is rejected even with valid release checksums' {
    $f=Fixture 'extra';[IO.File]::WriteAllText((Join-Path $f.New 'data\game-files\extra.bin'),'unexpected');ReleaseManifest $f;$r=RunUpgrade $f
    Assert ($r.Code -ne 0 -and $r.Output.Contains('different file counts')) $r.Output;Unchanged $f
}
Test 'Shared loader change is rejected even with valid release checksums' {
    $f=Fixture 'shared';[IO.File]::WriteAllText((Join-Path $f.New 'data\game-files\shared-loader.bin'),'changed loader');ReleaseManifest $f;$r=RunUpgrade $f
    Assert ($r.Code -ne 0 -and $r.Output.Contains('shared loader')) $r.Output;Unchanged $f
}
Test 'A running process blocks upgrade' {
    $f=Fixture 'running';$r=RunUpgrade $f -Mode running
    Assert ($r.Code -ne 0 -and $r.Output.Contains('Close PEAK')) $r.Output;Unchanged $f
}
Test 'Commit failure rolls back DLL and both installation manifests' {
    $f=Fixture 'rollback';$r=RunUpgrade $f -Mode rollback
    Assert ($r.Code -ne 0 -and $r.Output.Contains('Injected old-manifest commit failure') -and $r.Output.Contains('restored')) $r.Output;Unchanged $f
}
Test 'Directory junction in game path is rejected' {
    $f=Fixture 'junction';$outside=Join-Path $f.Root 'outside';New-Item -ItemType Directory $outside|Out-Null
    $link=Join-Path $f.Game 'linked';New-Item -ItemType Junction -Path $link -Target $outside|Out-Null
    $m=Get-Content -Raw -LiteralPath (Join-Path $f.Old 'installed-files.json')|ConvertFrom-Json;$m.files[1].path='linked\target.bin';SaveJson $m (Join-Path $f.Old 'installed-files.json')
    $r=RunUpgrade $f;Assert ($r.Code -ne 0 -and $r.Output.Contains('junction')) $r.Output
    Assert ((Hash (Join-Path $f.Game $plugin)) -eq $f.OldDllHash) 'DLL changed.'
}
Test 'Directory junction in payload is rejected before recursive traversal' {
    $f=Fixture 'payload-junction';$outside=Join-Path $f.Root 'outside';New-Item -ItemType Directory $outside|Out-Null
    New-Item -ItemType Junction -Path (Join-Path $f.New 'data\game-files\linked') -Target $outside|Out-Null
    $r=RunUpgrade $f;Assert ($r.Code -ne 0 -and $r.Output.Contains('junction')) $r.Output;Unchanged $f
}
$realRunning=@(Microsoft.PowerShell.Management\Get-Process -Name PEAK,PeakWorkshop -ErrorAction SilentlyContinue)
if($realRunning.Count -gt 0) {
    Test 'Unmodified process check rejects currently running real game/trainer' {
        $f=Fixture 'real-processes';$r=RunUpgrade $f -RealProcesses
        Assert ($r.Code -ne 0 -and $r.Output.Contains('Close PEAK')) $r.Output;Unchanged $f
    }
}
$failed=@($results|Where-Object{-not $_.passed}).Count
$report=[pscustomobject]@{total=$results.Count;passed=$results.Count-$failed;failed=$failed;createdUtc=[DateTime]::UtcNow.ToString('o');powershell=$ps51;fixtureRoot=$runs;processDetectionSubstitutedForFilesystemTests=$true;results=@($results.ToArray())}
SaveJson $report (Join-Path $PSScriptRoot 'upgrade-test-results.json')
Write-Output ('RESULT '+($results.Count-$failed)+'/'+$results.Count+' passed')
if($failed -gt 0){exit 1}
exit 0
