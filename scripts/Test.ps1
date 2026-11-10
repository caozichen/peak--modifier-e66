param([string]$GameDir = $env:PEAK_GAME_DIR, [string]$BepInExDir, [string]$NuGetConfig)
. (Join-Path $PSScriptRoot 'Common.ps1')
$properties = @(Get-ReferenceProperties $GameDir $BepInExDir)
$reportRoot = Join-Path $RepoRoot ('artifacts\tests\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $reportRoot | Out-Null
$suites = [ordered]@{
    transport = 'Tests\PeakWorkshop.Tests.csproj'
    controller = 'validation\controller\controller_tests.csproj'
    lifecycle = 'validation\lifecycle\lifecycle_tests.csproj'
    discovery = 'validation\discovery\discovery_tests.csproj'
    installation = 'validation\installation\portable_tests.csproj'
    world = 'validation\world\world_validation.csproj'
    ui = 'validation\ui\ui_tests.csproj'
}
Push-Location $RepoRoot
try {
    foreach ($name in $suites.Keys) {
        $project = Join-Path $RepoRoot $suites[$name]
        Restore-Project $project $NuGetConfig $properties
        $report = Join-Path $reportRoot ($name + '.json')
        $applicationArgs = @($report)
        if ($name -eq 'ui') { $applicationArgs += [IO.Path]::GetFullPath($GameDir) }
        Invoke-DotNet (@('run','--project',$project,'-c','Release','--no-restore') + $properties + @('--') + $applicationArgs)
    }
    # Legacy installer fixtures never point at the user's real game directory.
    & (Join-Path $RepoRoot 'Tests\Deploy.Tests.ps1') -ScratchRoot (Join-Path $reportRoot 'legacy-deploy')
    $summary = @($suites.Keys | ForEach-Object {
        $r = Get-Content -Raw -LiteralPath (Join-Path $reportRoot ($_ + '.json')) | ConvertFrom-Json
        if ($r.failed -ne 0 -or $r.total -ne $r.passed) { throw ('Incomplete suite: ' + $_) }
        [pscustomobject]@{suite=$_; total=$r.total; passed=$r.passed; failed=$r.failed}
    })
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding UTF8
    $summary | Format-Table
    Write-Output ('Reports: ' + $reportRoot)
} finally { Pop-Location }
