#Requires -Version 7.0
<#
.SYNOPSIS
    Pure publish-gate math for the SDLC dispatcher's Step 0a.3 (issue #141).
    No I/O, no git — sdlc-maint.ps1 reads the junction target and the dev
    sha and hands them here; the script performs the publish.

.DESCRIPTION
    The dogfooding binary is published when the DEPLOYED sha drifts from
    dev, never merely when this run's fast-forward happened to move dev:
    a human `git pull` between dispatches (or any advance the script did
    not perform) must still trigger a publish on the next cycle.

    Exported:
      ConvertTo-DeployedSha   junction target + releases dir -> release dir name, or $null
      Get-PublishPlan         deployedSha, devSha, devMoved -> @{ publish; lagging; reason }
#>

Set-StrictMode -Version Latest

function ConvertTo-DeployedSha {
    <# The deployed sha is the name of the release dir the ~/tools/vtk junction
       points at. $null ("unknown") when the junction is missing, has no
       target, or points outside vtk-releases (a plain dir, a foreign path). #>
    param([string]$LinkTarget, [Parameter(Mandatory)][string]$ReleasesDir)
    if ([string]::IsNullOrWhiteSpace($LinkTarget)) { return $null }
    $target = ($LinkTarget -replace '^\\\?\?\\', '').TrimEnd('\', '/')
    $releases = $ReleasesDir.TrimEnd('\', '/')
    $parent = [IO.Path]::GetDirectoryName($target)
    if ([string]::IsNullOrEmpty($parent)) { return $null }
    if (-not [string]::Equals($parent, $releases, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $name = [IO.Path]::GetFileName($target)
    return $(if ([string]::IsNullOrWhiteSpace($name)) { $null } else { $name })
}

function Get-PublishPlan {
    <# publish  = deployed sha unknown, or differs from dev (drift), regardless
                  of whether THIS run moved dev.
       lagging  = same predicate, reported for the digest (true whenever a
                  publish is owed — before the publish runs, or after it failed).
       reason   = the publish.result text for the skip case, or why publishing. #>
    param([string]$DeployedSha, [string]$DevSha, [bool]$DevMoved = $false)
    $devKnown = -not [string]::IsNullOrWhiteSpace($DevSha)
    $deployedKnown = -not [string]::IsNullOrWhiteSpace($DeployedSha)
    if (-not $devKnown) {
        return [ordered]@{ publish = $false; lagging = $false; reason = 'skipped (dev sha unknown)' }
    }
    if (-not $deployedKnown) {
        return [ordered]@{ publish = $true; lagging = $true; reason = 'deployed sha unknown (junction missing or not under vtk-releases)' }
    }
    if ([string]::Equals($DeployedSha.Trim(), $DevSha.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
        return [ordered]@{ publish = $false; lagging = $false; reason = 'skipped (deployed matches dev)' }
    }
    $how = if ($DevMoved) { 'dev moved this run' } else { 'dev advanced outside this run' }
    return [ordered]@{ publish = $true; lagging = $true; reason = "deployed $DeployedSha lags dev $DevSha ($how)" }
}

Export-ModuleMember -Function ConvertTo-DeployedSha, Get-PublishPlan
