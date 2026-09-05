#Requires -Version 7.0
# Plain-pwsh tests for scripts/lib/SdlcPublish.psm1 (no Pester dependency).
# Run: pwsh -NoProfile -File scripts/tests/SdlcPublish.Tests.ps1   (exit 0 = green)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\lib\SdlcPublish.psm1') -Force

$script:fail = 0; $script:pass = 0
function Canon($v) {
    # Key-order-insensitive canonical form (hashtables vs pscustomobjects).
    if ($null -eq $v -or $v -is [string] -or $v -is [ValueType]) { return $v }
    if ($v -is [System.Collections.IDictionary]) { $o = [ordered]@{}; foreach ($k in ($v.Keys | Sort-Object)) { $o[[string]$k] = Canon $v[$k] }; return $o }
    if ($v -is [System.Collections.IEnumerable]) { return , @(foreach ($x in $v) { Canon $x }) }
    $o = [ordered]@{}; foreach ($p in ($v.PSObject.Properties | Sort-Object Name)) { $o[$p.Name] = Canon $p.Value }; return $o
}
function Assert-Equal($Expected, $Actual, [string]$Name) {
    $e = ((Canon $Expected) | ConvertTo-Json -Depth 6 -Compress); $a = ((Canon $Actual) | ConvertTo-Json -Depth 6 -Compress)
    if ($e -eq $a) { $script:pass++ } else { $script:fail++; Write-Host "FAIL $Name`n  expected: $e`n  actual:   $a" }
}

$releases = 'C:\Users\me\tools\vtk-releases'

# ---- ConvertTo-DeployedSha --------------------------------------------------
Assert-Equal '8cc1a19' (ConvertTo-DeployedSha 'C:\Users\me\tools\vtk-releases\8cc1a19' $releases) 'deployed sha: junction under vtk-releases'
Assert-Equal '8cc1a19' (ConvertTo-DeployedSha '\??\C:\Users\me\tools\vtk-releases\8cc1a19' $releases) 'deployed sha: NT-prefixed junction target'
Assert-Equal '8cc1a19' (ConvertTo-DeployedSha 'c:\users\me\tools\VTK-RELEASES\8cc1a19\' "$releases\") 'deployed sha: case- and trailing-separator-insensitive'
Assert-Equal 'legacy' (ConvertTo-DeployedSha "$releases\legacy" $releases) 'deployed sha: migrated legacy dir is a (non-sha) name, not unknown'
Assert-Equal $null (ConvertTo-DeployedSha $null $releases) 'deployed sha: missing junction -> unknown'
Assert-Equal $null (ConvertTo-DeployedSha '' $releases) 'deployed sha: empty target -> unknown'
Assert-Equal $null (ConvertTo-DeployedSha 'C:\Users\me\tools\vtk' $releases) 'deployed sha: target outside vtk-releases -> unknown'
Assert-Equal $null (ConvertTo-DeployedSha "$releases\nested\8cc1a19" $releases) 'deployed sha: target nested deeper than one level -> unknown'
Assert-Equal $null (ConvertTo-DeployedSha $releases $releases) 'deployed sha: target IS the releases dir -> unknown'

# ---- Get-PublishPlan --------------------------------------------------------
$p = Get-PublishPlan -DeployedSha '805bafd' -DevSha 'c655605' -DevMoved $true
Assert-Equal @{ publish = $true; lagging = $true; reason = 'deployed 805bafd lags dev c655605 (dev moved this run)' } $p 'plan: moved + differs -> publish'

$p = Get-PublishPlan -DeployedSha '805bafd' -DevSha 'c655605' -DevMoved $false
Assert-Equal @{ publish = $true; lagging = $true; reason = 'deployed 805bafd lags dev c655605 (dev advanced outside this run)' } $p 'plan: NOT moved + differs -> publish (the #141 bug)'

$p = Get-PublishPlan -DeployedSha 'c655605' -DevSha 'c655605' -DevMoved $false
Assert-Equal @{ publish = $false; lagging = $false; reason = 'skipped (deployed matches dev)' } $p 'plan: equal -> skip'

$p = Get-PublishPlan -DeployedSha 'C655605' -DevSha 'c655605 ' -DevMoved $true
Assert-Equal @{ publish = $false; lagging = $false; reason = 'skipped (deployed matches dev)' } $p 'plan: equal (case/whitespace) even when dev moved -> skip'

$p = Get-PublishPlan -DeployedSha $null -DevSha 'c655605' -DevMoved $false
Assert-Equal @{ publish = $true; lagging = $true; reason = 'deployed sha unknown (junction missing or not under vtk-releases)' } $p 'plan: unknown deployed -> publish'

$p = Get-PublishPlan -DeployedSha 'legacy' -DevSha 'c655605'
Assert-Equal $true $p.publish 'plan: legacy deployment differs from dev -> publish'

$p = Get-PublishPlan -DeployedSha '805bafd' -DevSha '' -DevMoved $true
Assert-Equal @{ publish = $false; lagging = $false; reason = 'skipped (dev sha unknown)' } $p 'plan: dev sha unknown -> cannot publish, not lagging'

Write-Host "SdlcPublish: $script:pass passed, $script:fail failed"
exit $(if ($script:fail) { 1 } else { 0 })
