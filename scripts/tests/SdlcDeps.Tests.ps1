#Requires -Version 7.0
# Plain-pwsh tests for scripts/lib/SdlcDeps.psm1 (no Pester dependency).
# Run: pwsh -NoProfile -File scripts/tests/SdlcDeps.Tests.ps1   (exit 0 = green)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\lib\SdlcDeps.psm1') -Force

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
function Edge([int]$n, [string]$s = 'OPEN') { @{ number = $n; state = $s } }

# ---- Get-OpenBlockers -----------------------------------------------------
Assert-Equal @(3, 9) (Get-OpenBlockers @((Edge 9), (Edge 3), (Edge 4 'CLOSED'), (Edge 3))) 'open blockers: open only, sorted, unique'
Assert-Equal @() (Get-OpenBlockers $null) 'open blockers: null edges'
Assert-Equal @(5) (Get-OpenBlockers @{ nodes = @((Edge 5), (Edge 6 'closed')) }) 'open blockers: GraphQL connection shape, case-insensitive state'

# ---- Get-BlockedIssues ----------------------------------------------------
$open = @(
    @{ number = 10; title = 'a'; labels = @('stage:build'); blockedBy = @((Edge 11)) },
    @{ number = 11; title = 'b'; labels = @(@{ name = 'stage:build' }); blockedBy = @() },
    @{ number = 12; title = 'c'; labels = @('stage:queued', 'blocked'); blockedBy = @((Edge 11 'CLOSED')) }
)
Assert-Equal @(@{ number = 10; blockers = @(11) }) (Get-BlockedIssues $open) 'blocked bucket: only issues with an OPEN blocker'

# ---- Find-DependencyCycles ------------------------------------------------
$cyc = @(
    @{ number = 3; blockedBy = @((Edge 9)) },
    @{ number = 5; blockedBy = @((Edge 3)) },
    @{ number = 9; blockedBy = @((Edge 5)) },
    @{ number = 20; blockedBy = @((Edge 21)) },
    @{ number = 21; blockedBy = @((Edge 20 'CLOSED')) },   # closed edge breaks this "cycle"
    @{ number = 30; blockedBy = @((Edge 31)) }             # 31 not in the open snapshot
)
Assert-Equal @(, @(3, 9, 5)) (Find-DependencyCycles $cyc) 'cycles: 3 <- 9 <- 5 <- 3 reported once from lowest member; closed and off-snapshot edges ignored'
Assert-Equal @() (Find-DependencyCycles $open) 'cycles: none'
Assert-Equal @(, @(1, 2)) (Find-DependencyCycles @(@{ number = 1; blockedBy = @((Edge 2)) }, @{ number = 2; blockedBy = @((Edge 1)) })) 'cycles: two-node'

# ---- Get-DepsPlan ---------------------------------------------------------
$plan = Get-DepsPlan @(
    @{ number = 1; labels = @('ready'); blockedBy = @((Edge 2), (Edge 3)) },      # open blockers, wrongly `ready`
    @{ number = 2; labels = @('blocked'); blockedBy = @((Edge 7 'CLOSED')) },      # all closed, wrongly `blocked`
    @{ number = 3; labels = @('blocked'); blockedBy = @() },                       # label-only -> lint
    @{ number = 4; labels = @('blocked'); blockedBy = @((Edge 2)) },               # already correct -> no edit
    @{ number = 5; labels = @(); blockedBy = @() }                                 # no edges, no labels -> nothing
)
Assert-Equal @(1, 4) $plan.blocked 'deps: blocked set'
Assert-Equal @(2) $plan.ready 'deps: ready set'
Assert-Equal @(
    @{ number = 1; add = @('blocked'); remove = @('ready'); reason = 'open blockers #2, #3' },
    @{ number = 2; add = @('ready'); remove = @('blocked'); reason = 'every blocker closed (#7)' }
) $plan.edits 'deps: only the unambiguous edits'
Assert-Equal @('label-only-blocked') @($plan.findings | ForEach-Object kind) 'deps: label-only lint, no repair'
Assert-Equal 3 $plan.findings[0].number 'deps: lint names the issue'

$planCyc = Get-DepsPlan $cyc
Assert-Equal @('cycle') @($planCyc.findings | ForEach-Object kind) 'deps: cycle lint'
Assert-Equal 3 $planCyc.findings[0].number 'deps: cycle lint anchored on lowest member'

# ---- Get-CloseSweep -------------------------------------------------------
$now = [DateTimeOffset]::Parse('2026-09-05T12:00:00Z')
$closed = @(
    @{ number = 7; title = 'landed'; closedAt = '2026-09-05T10:00:00Z'; blocking = @((Edge 2), (Edge 8), (Edge 99)) },   # 8 still blocked by 9; 99 not open
    @{ number = 6; title = 'old'; closedAt = '2026-09-03T10:00:00Z'; blocking = @((Edge 2)) },                           # outside the window
    @{ number = 5; title = 'lonely'; closedAt = '2026-09-05T11:00:00Z'; blocking = @() }                                   # blocked nothing
)
$openS = @(
    @{ number = 2; title = 'dep'; labels = @('blocked'); blockedBy = @((Edge 7 'CLOSED')) },
    @{ number = 8; title = 'dep2'; labels = @(); blockedBy = @((Edge 7 'CLOSED'), (Edge 9)) },
    @{ number = 9; title = 'other'; labels = @(); blockedBy = @() }
)
$sweep = Get-CloseSweep $closed $openS ($now.AddHours(-24))
Assert-Equal $false $sweep.empty 'sweep: not empty'
Assert-Equal @(7) $sweep.closedIssues 'sweep: window + blocking-nothing filters'
Assert-Equal @(
    @{ number = 2; title = 'dep'; blocked = $true; remainingBlockers = @(); flip = $true },
    @{ number = 8; title = 'dep2'; blocked = $false; remainingBlockers = @(9); flip = $false }
) $sweep.items[0].dependents 'sweep: flip only when every remaining blocker is closed; off-snapshot dependents dropped'
Assert-Equal $true (Get-CloseSweep @() $openS $null).empty 'sweep: empty'
Assert-Equal @(6, 7) (Get-CloseSweep $closed $openS $null).closedIssues 'sweep: no window -> every closed issue with open dependents'

Write-Host "SdlcDeps: $script:pass passed, $script:fail failed"
exit $(if ($script:fail) { 1 } else { 0 })
