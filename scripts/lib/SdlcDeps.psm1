#Requires -Version 7.0
<#
.SYNOPSIS
    Pure dependency math for the SDLC dispatcher (issue #142; port of
    agentic-sdlc #27). No I/O, no gh, no git — sdlc-maint.ps1 fetches the
    native issue-dependency edges and hands them here; the dispatcher writes.

.DESCRIPTION
    Blocking is a fact about GitHub's NATIVE issue dependencies (blockedBy /
    blocking edges), never about the `blocked` / `ready` labels or prose in a
    body. Every function takes issues shaped like the GraphQL snapshot:

        @{ number = 12; title = '...'; labels = @('stage:build', ...)
           blockedBy = @(@{ number = 7; state = 'OPEN' }, ...)
           blocking  = @(@{ number = 15; state = 'OPEN' }, ...) }

    (`labels` may also be objects with a `.name`; edges may be GraphQL
    `{ nodes = @(...) }` connections — both are normalized.)

    Exported:
      Get-OpenBlockers        edge list -> numbers of the OPEN blockers
      Get-BlockedIssues       open snapshot -> [{number, blockers}] with any OPEN blocker
      Find-DependencyCycles   open snapshot -> cycles among OPEN issues, each once
      Get-DepsPlan            open snapshot -> derived-label edits + lint findings
      Get-CloseSweep          closed + open snapshots -> intake's close-sweep work-list
#>

Set-StrictMode -Version Latest

function ConvertTo-EdgeList {
    <# Normalize an edge collection (GraphQL connection, array of objects, or
       $null) to an array of @{ number; state } with numeric numbers. #>
    param($Edges)
    if ($null -eq $Edges) { return @() }
    $items = $Edges
    if ($Edges -is [System.Collections.IDictionary] -and $Edges.Contains('nodes')) { $items = $Edges['nodes'] }
    elseif ($Edges -isnot [System.Collections.IEnumerable] -and $null -ne $Edges.PSObject.Properties['nodes']) { $items = $Edges.nodes }
    $out = @()
    foreach ($e in @($items)) {
        if ($null -eq $e) { continue }
        $num = if ($e -is [System.Collections.IDictionary]) { $e['number'] } else { $e.number }
        $state = if ($e -is [System.Collections.IDictionary]) { $e['state'] } else { $e.state }
        if ($null -eq $num) { continue }
        $out += [pscustomobject]@{ number = [int]$num; state = ("$state").ToUpperInvariant() }
    }
    return , $out
}

function Get-LabelNames {
    param($Labels)
    $out = @()
    foreach ($l in @($Labels)) {
        if ($null -eq $l) { continue }
        if ($l -is [string]) { $out += $l }
        elseif ($l -is [System.Collections.IDictionary]) { $out += [string]$l['name'] }
        else { $out += [string]$l.name }
    }
    return , $out
}

function Get-Field {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) { return $(if ($Object.Contains($Name)) { $Object[$Name] } else { $null }) }
    $p = $Object.PSObject.Properties[$Name]
    return $(if ($null -ne $p) { $p.Value } else { $null })
}

function Get-OpenBlockers {
    <# Numbers of the OPEN blockers in a blockedBy edge list, ascending. #>
    param($BlockedBy)
    $nums = @((ConvertTo-EdgeList $BlockedBy) | Where-Object { $_.state -eq 'OPEN' } | ForEach-Object { $_.number } | Sort-Object -Unique)
    return , $nums
}

function ConvertTo-IssueNorm {
    param($Issues)
    $out = @()
    foreach ($i in @($Issues)) {
        if ($null -eq $i) { continue }
        $out += [pscustomobject]@{
            number   = [int](Get-Field $i 'number')
            title    = [string](Get-Field $i 'title')
            labels   = Get-LabelNames (Get-Field $i 'labels')
            edges    = ConvertTo-EdgeList (Get-Field $i 'blockedBy')
            blockers = Get-OpenBlockers (Get-Field $i 'blockedBy')
            blocking = ConvertTo-EdgeList (Get-Field $i 'blocking')
            closedAt = Get-Field $i 'closedAt'
        }
    }
    return , @($out | Sort-Object number)
}

function Get-BlockedIssues {
    <# Open issues with at least one OPEN native blocker: the dispatcher's
       fourth ineligibility bucket. [{ number; blockers = int[] }] #>
    param($OpenIssues)
    $out = @((ConvertTo-IssueNorm $OpenIssues) | Where-Object { @($_.blockers).Count -gt 0 } |
        ForEach-Object { [pscustomobject]@{ number = $_.number; blockers = $_.blockers } })
    return , $out
}

function Find-DependencyCycles {
    <# Cycles among OPEN issues over their OPEN blockedBy edges. Each cycle is
       reported once as its members starting from the lowest number
       (@(3,5,9) = 3 <- 5 <- 9 <- 3). Closed blockers are ignored — a cycle
       through a closed issue is already broken. #>
    param($OpenIssues)
    $graph = @{}
    foreach ($i in (ConvertTo-IssueNorm $OpenIssues)) { $graph[$i.number] = @($i.blockers) }
    $cycles = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new()

    function Visit([int]$start, [int]$node, [System.Collections.Generic.List[int]]$stack, [System.Collections.Generic.HashSet[int]]$onStack) {
        foreach ($next in @($graph[$node])) {
            if (-not $graph.ContainsKey($next)) { continue }   # blocker not in the open snapshot
            if ($next -eq $start) {
                $min = ($stack | Measure-Object -Minimum).Minimum
                $idx = $stack.IndexOf([int]$min)
                $rotated = @($stack.GetRange($idx, $stack.Count - $idx)) + @($stack.GetRange(0, $idx))
                $key = ($rotated -join '>')
                if ($seen.Add($key)) { $cycles.Add([int[]]$rotated) }
                continue
            }
            if ($onStack.Contains($next) -or $next -lt $start) { continue }   # found from the lowest member only
            $null = $onStack.Add($next); $stack.Add($next)
            Visit $start $next $stack $onStack
            $stack.RemoveAt($stack.Count - 1); $null = $onStack.Remove($next)
        }
    }

    foreach ($start in ($graph.Keys | Sort-Object)) {
        $stack = [System.Collections.Generic.List[int]]::new(); $stack.Add([int]$start)
        $onStack = [System.Collections.Generic.HashSet[int]]::new(); $null = $onStack.Add([int]$start)
        Visit ([int]$start) ([int]$start) $stack $onStack
    }
    return , @($cycles.ToArray())
}

function Get-DepsPlan {
    <# Derived readiness labels + lint, from edge state alone:
         any OPEN blocker   -> `blocked` (drop `ready`)
         edges, all CLOSED  -> `ready`   (drop `blocked`)
         no edges           -> neither; an existing `blocked` label is
                               AMBIGUOUS (cross-repo / prose-only) -> lint only
       Returns @{ edits = [{number, add, remove, reason}]; findings = [{kind, number, detail}];
                  blocked = int[]; ready = int[] }. Issues on sdlc:hold are still
       relabeled — readiness is orthogonal to a human keep-off. #>
    param($OpenIssues)
    $norm = ConvertTo-IssueNorm $OpenIssues
    $edits = @(); $findings = @(); $blocked = @(); $ready = @()
    foreach ($issue in $norm) {
        $hasBlocked = $issue.labels -contains 'blocked'
        $hasReady = $issue.labels -contains 'ready'
        if (@($issue.blockers).Count -gt 0) {
            $blocked += $issue.number
            $add = @(if (-not $hasBlocked) { 'blocked' }); $remove = @(if ($hasReady) { 'ready' })
            if (@($add).Count -or @($remove).Count) {
                $plural = if (@($issue.blockers).Count -eq 1) { '' } else { 's' }
                $edits += [pscustomobject]@{ number = $issue.number; add = $add; remove = $remove; reason = "open blocker$plural #$($issue.blockers -join ', #')" }
            }
        }
        elseif (@($issue.edges).Count -gt 0) {
            $ready += $issue.number
            $add = @(if (-not $hasReady) { 'ready' }); $remove = @(if ($hasBlocked) { 'blocked' })
            if (@($add).Count -or @($remove).Count) {
                $edits += [pscustomobject]@{ number = $issue.number; add = $add; remove = $remove; reason = "every blocker closed (#$(($issue.edges | ForEach-Object number) -join ', #'))" }
            }
        }
        elseif ($hasBlocked) {
            $findings += [pscustomobject]@{ kind = 'label-only-blocked'; number = $issue.number; detail = 'blocked label with no native dependency edge - stale label, or a cross-repo block that must be sdlc:hold + prose; a human decides' }
        }
    }
    foreach ($cycle in (Find-DependencyCycles $OpenIssues)) {
        $findings += [pscustomobject]@{ kind = 'cycle'; number = $cycle[0]; detail = "dependency cycle #$($cycle -join ' <- #') <- #$($cycle[0]) - nothing in it can become eligible until a human cuts an edge" }
    }
    return [ordered]@{ edits = $edits; findings = $findings; blocked = $blocked; ready = $ready }
}

function Get-CloseSweep {
    <# Intake's close-sweep work-list (intake.md step 0): recently CLOSED issues
       -> the OPEN issues they were blocking (native `blocking` edges) -> per
       dependent, `flip` when every remaining blocker is closed. Bookkeeping,
       not gating — the dispatcher's eligibility gate already refuses items
       with an open blocker. A closed issue blocking nothing open, or closed
       at/before -SinceUtc, is skipped, so the sweep is idempotent and bounded. #>
    param($ClosedIssues, $OpenIssues, [Nullable[DateTimeOffset]]$SinceUtc = $null)
    $open = @{}
    foreach ($i in (ConvertTo-IssueNorm $OpenIssues)) { $open[$i.number] = $i }
    $items = @()
    foreach ($closed in (ConvertTo-IssueNorm $ClosedIssues)) {
        if ($null -ne $SinceUtc -and $closed.closedAt -and ([DateTimeOffset]::Parse("$($closed.closedAt)", [cultureinfo]::InvariantCulture) -le $SinceUtc)) { continue }
        $deps = @()
        foreach ($e in ($closed.blocking | Where-Object { $_.state -eq 'OPEN' -and $open.ContainsKey($_.number) } | Sort-Object number)) {
            $d = $open[$e.number]
            $deps += [pscustomobject]@{
                number             = $d.number
                title              = $d.title
                blocked            = ($d.labels -contains 'blocked')
                remainingBlockers  = $d.blockers
                flip               = (@($d.blockers).Count -eq 0)
            }
        }
        if (@($deps).Count -eq 0) { continue }
        $items += [pscustomobject]@{ number = $closed.number; closedAt = $closed.closedAt; title = $closed.title; dependents = $deps }
    }
    return [ordered]@{ items = $items; closedIssues = @($items | ForEach-Object number); empty = (@($items).Count -eq 0) }
}

Export-ModuleMember -Function Get-OpenBlockers, Get-BlockedIssues, Find-DependencyCycles, Get-DepsPlan, Get-CloseSweep
