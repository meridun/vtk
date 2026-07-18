#Requires -Version 7.0
<#
.SYNOPSIS
    Deterministic encoding of the SDLC dispatcher's Steps -1, 0, and 0a
    (prompts/sdlc/dispatch.md). Emits a JSON digest on stdout.

.DESCRIPTION
    One pass, no arguments required. Responsibility split (issue #75):

    - The script EXECUTES machine-local maintenance, serialized by the machine
      lock: root-gate origin check, run-id minting, .git/sdlc-maint.lock
      acquire / stale-reap / release, git fetch --prune, dev fast-forward,
      versioned publish + junction flip (with one-time migration, release GC,
      and the exit-code-parity health check `vtk cmd /c "exit 3"` -> 3),
      worktree sweep, and merged-branch prune.
    - The script COMPUTES but never writes GitHub state: issue snapshot, lane
      depths, per-wip claim ages (claim comment, timeline fallback) with
      staleCandidate flags at the 2h threshold, and the open-PR snapshot with
      conflicting flags. The dispatcher performs all GitHub writes
      (verify-before-write reaps, conflict comments/label swaps).

    A third mode, -AppendTokens (issue #79), appends per-lane-pass token cost
    rows to the machine-local telemetry CSV ($ToolsDir\vtk-sdlc\tokens.csv)
    and exits: no lock, no maintenance, no GitHub reads. The dispatcher calls
    it at digest time, once per cycle, with one row per lane worker spawned.
    The CSV is machine-local and never committed or uploaded.

    Exit codes: 0 = digest emitted / rows appended (individual operations may
    have been skipped; see .notes), 2 = root gate failed (nothing was touched),
    4 = -AppendTokens payload invalid (CSV untouched).

.PARAMETER RunId
    Run identifier for lock ownership. Minted (dispatch-<yyyymmdd-hhmm>-<4 hex>)
    when omitted.

.PARAMETER DataOnly
    Skip the machine lock and all local maintenance; emit only the GitHub data
    sections (issues, prs). Read-only against both git and GitHub.

.PARAMETER AppendTokens
    Token-append mode. A JSON array (single object also accepted) of rows,
    each {lane, issue, outcome, tokens, toolUses, durationMs} — lane and
    outcome required, the rest nullable (issue null for an IDLE pass; unknown
    metrics stay null, never guessed). The script stamps the UTC timestamp
    and RunId and appends one CSV row per element to
    $ToolsDir\vtk-sdlc\tokens.csv, creating directory and header when missing.
#>
[CmdletBinding()]
param(
    [string]$RunId,
    [switch]$DataOnly,
    [string]$AppendTokens,
    [string]$RepoRoot = 'C:\Claude\vtk',
    [string]$Repo = 'meridun/vtk',
    [string]$ToolsDir = (Join-Path $env:USERPROFILE 'tools')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$script:Notes = [System.Collections.Generic.List[string]]::new()
function Note([string]$Message) { $script:Notes.Add($Message) }

function Invoke-Git {
    # Runs git against the repo root; returns @{ Ok; ExitCode; Output (string[]) }.
    param([Parameter(Mandatory)][string[]]$ArgumentList, [string]$WorkDir = $RepoRoot)
    $out = & git -C $WorkDir @ArgumentList 2>&1 | ForEach-Object { "$_" }
    return @{ Ok = ($LASTEXITCODE -eq 0); ExitCode = $LASTEXITCODE; Output = @($out) }
}

function Invoke-GitRetryOnce {
    # dispatch.md Step 0a: ref-lock contention is transient — retry once, then
    # skip the operation and record it.
    param([Parameter(Mandatory)][string[]]$ArgumentList, [string]$WorkDir = $RepoRoot)
    $r = Invoke-Git -ArgumentList $ArgumentList -WorkDir $WorkDir
    if (-not $r.Ok -and (($r.Output -join "`n") -match 'cannot lock ref|\.lock')) {
        Start-Sleep -Seconds 2
        $r = Invoke-Git -ArgumentList $ArgumentList -WorkDir $WorkDir
    }
    return $r
}

function ConvertTo-UtcOffset {
    # ConvertFrom-Json may hand back [DateTime] (already parsed) or a raw ISO
    # string; normalize both to a UTC DateTimeOffset without double-shifting.
    param([Parameter(Mandatory)]$Value)
    if ($Value -is [DateTime]) { return [DateTimeOffset]::new($Value.ToUniversalTime(), [TimeSpan]::Zero) }
    return [DateTimeOffset]::Parse("$Value", [cultureinfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
}

function Invoke-Gh {
    # Runs gh (always -R $Repo where applicable is the caller's job); returns
    # parsed JSON or $null on failure.
    param([Parameter(Mandatory)][string[]]$ArgumentList)
    $raw = (& gh @ArgumentList 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) { return $null }
    # -NoEnumerate + comma wrap: an empty JSON array is a valid (empty) result,
    # not a failure — keep it from unrolling to $null on return.
    try { return , (ConvertFrom-Json -InputObject $raw -NoEnumerate) } catch { return $null }
}

# --------------------------------------------------------------------------
# Root gate (dispatch.md Step -2, check 2): repo identity. Belt-and-suspenders
# for gh mis-targeting; the agent-registry check stays with the dispatcher.
# --------------------------------------------------------------------------
$originCheck = Invoke-Git -ArgumentList @('remote', 'get-url', 'origin')
$origin = if ($originCheck.Ok) { ($originCheck.Output -join '').Trim() } else { '' }
$rootGate = [ordered]@{ ok = ($originCheck.Ok -and $origin -match 'vtk'); origin = $origin }
if (-not $rootGate.ok) {
    [ordered]@{
        runId    = $RunId
        rootGate = $rootGate
        notes    = @("aborted - origin '$origin' does not identify the vtk repo; nothing was touched")
    } | ConvertTo-Json -Depth 8
    exit 2
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = 'dispatch-{0:yyyyMMdd-HHmm}-{1}' -f [DateTimeOffset]::UtcNow, (-join ((1..4) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) }))
}
$now = [DateTimeOffset]::UtcNow

# --------------------------------------------------------------------------
# Token-append mode (issue #79): append one CSV row per lane pass to
# $ToolsDir\vtk-sdlc\tokens.csv and exit. Machine-local telemetry — never
# committed, never uploaded (#34 posture). No lock, no maintenance, no GitHub
# reads; the root gate above still applies. Header columns are the issue #79
# spec verbatim.
# --------------------------------------------------------------------------
if ($PSBoundParameters.ContainsKey('AppendTokens')) {
    $tokensCsv = Join-Path $ToolsDir 'vtk-sdlc\tokens.csv'

    function ConvertTo-CsvField($Value) {
        if ($null -eq $Value) { return '' }
        $s = "$Value"
        if ($s -match '[",\r\n]') { return '"' + ($s -replace '"', '""') + '"' }
        return $s
    }
    function Get-RowValue($Row, [string]$Name) {
        $p = $Row.PSObject.Properties[$Name]
        if ($null -ne $p) { return $p.Value } else { return $null }
    }

    try {
        $rows = ConvertFrom-Json -InputObject $AppendTokens -NoEnumerate -ErrorAction Stop
        if ($rows -isnot [array]) { $rows = @($rows) }
        if ($rows.Count -eq 0) { throw 'empty row array' }
        foreach ($row in $rows) {
            foreach ($field in @('lane', 'outcome')) {
                if ([string]::IsNullOrWhiteSpace("$(Get-RowValue $row $field)")) {
                    throw "row missing required field '$field'"
                }
            }
        }
    } catch {
        [ordered]@{
            runId    = $RunId
            tokensCsv = $tokensCsv
            appended = 0
            error    = "invalid -AppendTokens payload: $($_.Exception.Message)"
        } | ConvertTo-Json -Depth 4
        exit 4
    }

    $null = New-Item -ItemType Directory -Path (Split-Path $tokensCsv) -Force
    if (-not (Test-Path $tokensCsv)) {
        Set-Content -Path $tokensCsv -Value 'timestamp,run-id,lane,issue#,outcome,tokens,tool_uses,duration_ms' -Encoding utf8
    }
    $stamp = '{0:o}' -f $now
    $lines = @(foreach ($row in $rows) {
            @(
                $stamp
                (ConvertTo-CsvField $RunId)
                (ConvertTo-CsvField (Get-RowValue $row 'lane'))
                (ConvertTo-CsvField (Get-RowValue $row 'issue'))
                (ConvertTo-CsvField (Get-RowValue $row 'outcome'))
                (ConvertTo-CsvField (Get-RowValue $row 'tokens'))
                (ConvertTo-CsvField (Get-RowValue $row 'toolUses'))
                (ConvertTo-CsvField (Get-RowValue $row 'durationMs'))
            ) -join ','
        })
    Add-Content -Path $tokensCsv -Value $lines -Encoding utf8
    [ordered]@{ runId = $RunId; tokensCsv = $tokensCsv; appended = $rows.Count } | ConvertTo-Json -Depth 4
    exit 0
}

# --------------------------------------------------------------------------
# Machine maintenance lock (dispatch.md Step -1). Directory-as-lock inside
# .git; acquire is atomic create, stale (>30 min) is reaped by atomic rename.
# Never abort the pass over this lock — only maintenance is conditional on it.
# --------------------------------------------------------------------------
$lockDir = Join-Path $RepoRoot '.git\sdlc-maint.lock'
$machineLock = [ordered]@{ result = 'skipped (data-only)'; acquired = $false; holder = $null; holderAgeMinutes = $null }

function Test-LockAcquire {
    try {
        $null = New-Item -ItemType Directory -Path $lockDir -ErrorAction Stop
        Set-Content -Path (Join-Path $lockDir 'owner.txt') -Value ("{0} {1:o}" -f $RunId, [DateTimeOffset]::UtcNow) -Encoding utf8
        return $true
    } catch { return $false }
}

if (-not $DataOnly) {
    if (Test-LockAcquire) {
        $machineLock.result = 'acquired'
        $machineLock.acquired = $true
    } else {
        $ownerFile = Join-Path $lockDir 'owner.txt'
        $holder = 'unknown'; $held = $null
        if (Test-Path $ownerFile) {
            $ownerLine = (Get-Content $ownerFile -ErrorAction SilentlyContinue | Select-Object -First 1) ?? ''
            $parts = $ownerLine -split '\s+', 2
            if ($parts.Count -ge 1 -and $parts[0]) { $holder = $parts[0] }
            if ($parts.Count -ge 2) { try { $held = [DateTimeOffset]::Parse($parts[1], [cultureinfo]::InvariantCulture) } catch { $held = $null } }
        }
        if ($null -eq $held) {
            try { $held = [DateTimeOffset](Get-Item $lockDir).CreationTimeUtc } catch { $held = $now }
        }
        $ageMin = [math]::Round(($now - $held).TotalMinutes, 1)
        $machineLock.holder = $holder
        $machineLock.holderAgeMinutes = $ageMin
        if ($ageMin -lt 30) {
            $machineLock.result = "skipped (lock held by $holder, ${ageMin}m)"
        } else {
            # Holder presumed dead: reap by rename (atomic — one contender wins).
            $staleName = "sdlc-maint.lock.stale-$($RunId -replace '[^\w\-.]', '_')"
            try {
                Rename-Item -Path $lockDir -NewName $staleName -ErrorAction Stop
                Remove-Item -Path (Join-Path (Split-Path $lockDir) $staleName) -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-LockAcquire) {
                    $machineLock.result = "acquired (reaped stale lock owned by $holder, ${ageMin}m)"
                    $machineLock.acquired = $true
                } else {
                    $machineLock.result = 'skipped (lost acquire race after reap)'
                }
            } catch {
                $machineLock.result = 'skipped (stale-lock rename lost to another run)'
            }
        }
    }
}

# --------------------------------------------------------------------------
# Step 0 — issue snapshot + stale-claim age computation (DATA ONLY).
# The dispatcher owns every reap write, verify-before-write.
# --------------------------------------------------------------------------
$issueSnapshot = Invoke-Gh -ArgumentList @('issue', 'list', '-R', $Repo, '--state', 'open',
    '--json', 'number,title,labels,createdAt,updatedAt', '--limit', '200')
if ($null -eq $issueSnapshot) { $issueSnapshot = @(); Note 'issue snapshot failed - gh unavailable or rate-limited; lane/wip data empty' }
$issueSnapshot = @($issueSnapshot)

$laneDepths = [ordered]@{}
foreach ($lane in @('intake', 'queued', 'build', 'verify', 'audit', 'ship')) {
    # @(... | ForEach-Object name): StrictMode-safe when labels = @() (unlabeled
    # issue) — bare `$_.labels.name` member enumeration throws and would silently
    # drop the issue from the snapshot.
    $laneDepths[$lane] = @($issueSnapshot | Where-Object { @($_.labels | ForEach-Object name) -contains "stage:$lane" }).Count
}
$needsHuman = @($issueSnapshot | Where-Object { @($_.labels | ForEach-Object name) -contains 'sdlc:needs-human' } | ForEach-Object { $_.number })
$holds = @($issueSnapshot | Where-Object { @($_.labels | ForEach-Object name) -contains 'sdlc:hold' } | ForEach-Object { $_.number })

$wip = @()
foreach ($issue in @($issueSnapshot | Where-Object { @($_.labels | ForEach-Object name) -contains 'sdlc:wip' })) {
    $n = $issue.number
    $entry = [ordered]@{
        number         = $n
        claimRunId     = $null
        ageHours       = $null
        ageSource      = 'unknown'
        staleCandidate = $false
    }
    # Lock age/owner come from the newest sdlc:claim comment — never updatedAt.
    $claims = Invoke-Gh -ArgumentList @('issue', 'view', "$n", '-R', $Repo, '--json', 'comments',
        '--jq', '[.comments[] | select(.body | startswith("sdlc:claim"))] | last')
    if ($null -ne $claims -and $claims.PSObject.Properties['createdAt']) {
        $entry.ageSource = 'claim-comment'
        $entry.claimRunId = ((("$($claims.body)" -split '\s+') | Select-Object -Skip 1 -First 1) ?? 'unknown')
        $entry.ageHours = [math]::Round(($now - (ConvertTo-UtcOffset $claims.createdAt)).TotalHours, 2)
    } else {
        # Bare label: age is the labeled-event timestamp from the timeline.
        # (--paginate runs the jq per page, one line per matching event; the
        # last non-empty line is the newest labeling.)
        $tl = & gh api "repos/$Repo/issues/$n/timeline?per_page=100" --paginate `
            --jq '.[] | select(.event=="labeled" and .label.name=="sdlc:wip") | .created_at' 2>$null
        $labeledAt = if ($LASTEXITCODE -eq 0) { @($tl | Where-Object { $_ -and "$_" -ne 'null' }) | Select-Object -Last 1 } else { $null }
        if ($labeledAt) {
            $entry.ageSource = 'timeline-labeled-event'
            $entry.ageHours = [math]::Round(($now - (ConvertTo-UtcOffset $labeledAt)).TotalHours, 2)
        } else {
            Note "issue #${n}: sdlc:wip with no claim comment and no findable labeled event - age unprovable, never reap"
        }
    }
    if ($null -ne $entry.ageHours -and $entry.ageHours -ge 2) { $entry.staleCandidate = $true }
    $wip += [pscustomobject]$entry
}

# --------------------------------------------------------------------------
# Open-PR snapshot + CONFLICTING detection (DATA ONLY; dispatcher decides
# comments and label swaps).
# --------------------------------------------------------------------------
$prList = Invoke-Gh -ArgumentList @('pr', 'list', '-R', $Repo, '--state', 'open',
    '--json', 'number,title,headRefName,mergeable,reviewDecision,isDraft')
if ($null -eq $prList) { $prList = @(); Note 'PR snapshot failed - gh unavailable or rate-limited' }
$prs = @($prList | ForEach-Object {
        [pscustomobject][ordered]@{
            number         = $_.number
            title          = $_.title
            headRefName    = $_.headRefName
            mergeable      = $_.mergeable
            reviewDecision = $_.reviewDecision
            isDraft        = $_.isDraft
            conflicting    = ($_.mergeable -eq 'CONFLICTING')
        }
    })

# --------------------------------------------------------------------------
# Step 0a — git + worktree maintenance. Only while holding the machine lock.
# Never touches any working tree's uncommitted state; never rebases or forces.
# --------------------------------------------------------------------------
$git = [ordered]@{ performed = $false; fetched = $false; devBefore = $null; devAfter = $null; devMoved = $false; devUpdateResult = 'skipped' }
$publish = [ordered]@{ performed = $false; sha = $null; releaseDir = $null; flipped = $false; healthCheck = $null; migration = $null; gcRemoved = @(); gcLeft = @(); result = 'skipped (dev did not move)' }
$worktrees = [ordered]@{ removed = @(); left = @(); pruned = $false }
$branches = [ordered]@{ pruned = @(); left = @() }

if ($machineLock.acquired) {
    try {
        $git.performed = $true

        # 0a.1 — fetch + prune.
        $fetch = Invoke-GitRetryOnce -ArgumentList @('fetch', 'origin', '--prune')
        $git.fetched = $fetch.Ok
        if (-not $fetch.Ok) { Note "git fetch --prune failed: $($fetch.Output -join ' ')" }

        # 0a.2 — fast-forward local dev without touching any working tree.
        $git.devBefore = (Invoke-Git -ArgumentList @('rev-parse', 'dev')).Output -join ''
        $wtPorcelain = (Invoke-Git -ArgumentList @('worktree', 'list', '--porcelain')).Output
        $devWorktree = $null
        for ($i = 0; $i -lt $wtPorcelain.Count; $i++) {
            if ($wtPorcelain[$i] -eq 'branch refs/heads/dev') {
                for ($j = $i; $j -ge 0; $j--) {
                    if ($wtPorcelain[$j] -like 'worktree *') { $devWorktree = $wtPorcelain[$j].Substring(9); break }
                }
            }
        }
        if ($git.fetched) {
            if ($devWorktree) {
                $ff = Invoke-GitRetryOnce -ArgumentList @('pull', '--ff-only', 'origin', 'dev') -WorkDir $devWorktree
            } else {
                $ff = Invoke-GitRetryOnce -ArgumentList @('fetch', 'origin', 'dev:dev')
            }
            $git.devUpdateResult = if ($ff.Ok) { 'updated' } else { "skipped ($($ff.Output -join ' '))" }
            if (-not $ff.Ok) { Note 'dev update skipped - non-fast-forward or tree collision; never rebase or force' }
        } else {
            $git.devUpdateResult = 'skipped (fetch failed)'
        }
        $git.devAfter = (Invoke-Git -ArgumentList @('rev-parse', 'dev')).Output -join ''
        $git.devMoved = ($git.devBefore -ne $git.devAfter)

        # 0a.3 — dogfooding binary: versioned publish + junction flip.
        if ($git.devMoved) {
            $shortSha = ((Invoke-Git -ArgumentList @('rev-parse', '--short', 'dev')).Output -join '').Trim()
            $publish.sha = $shortSha
            $releasesDir = Join-Path $ToolsDir 'vtk-releases'
            $releaseDir = Join-Path $releasesDir $shortSha
            $publish.releaseDir = $releaseDir
            $null = New-Item -ItemType Directory -Path $releasesDir -Force

            $built = $false
            if ((Test-Path (Join-Path $releaseDir '.publish-ok'))) {
                $built = $true
                $publish.result = 'reused existing release (already published for this sha)'
            } else {
                # Choose the build tree: main checkout on dev + clean under
                # dotnet/ -> build in place; otherwise build from a detached
                # worktree pinned at the dev sha (never bake uncommitted code).
                $mainBranch = ((Invoke-Git -ArgumentList @('rev-parse', '--abbrev-ref', 'HEAD')).Output -join '').Trim()
                $buildTree = $null; $scratch = $null
                if ($mainBranch -eq 'dev') {
                    $dirty = (Invoke-Git -ArgumentList @('status', '--porcelain', '--', 'dotnet')).Output -join ''
                    if ([string]::IsNullOrWhiteSpace($dirty)) {
                        $buildTree = $RepoRoot
                    } else {
                        $publish.result = 'skipped (main tree on dev but dirty under dotnet/ - kept current deployment)'
                    }
                } else {
                    $scratch = Join-Path (Split-Path $RepoRoot -Parent) 'vtk-wt\.maint-build'
                    if (Test-Path $scratch) {
                        $null = Invoke-Git -ArgumentList @('worktree', 'remove', '--force', $scratch)
                        if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
                        $null = Invoke-Git -ArgumentList @('worktree', 'prune')
                    }
                    $add = Invoke-Git -ArgumentList @('worktree', 'add', '--detach', $scratch, 'dev')
                    if ($add.Ok) { $buildTree = $scratch } else { $publish.result = "skipped (detached build worktree failed: $($add.Output -join ' '))" }
                }
                if ($buildTree) {
                    # SourceRevisionId bakes "1.0.0+<shortsha>" into the assembly
                    # informational version — the primary source for `vtk version` (#98).
                    & dotnet publish (Join-Path $buildTree 'dotnet\Vtk.Cli') -c Release -o $releaseDir "-p:SourceRevisionId=$shortSha" 2>&1 | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Set-Content -Path (Join-Path $releaseDir '.publish-ok') -Value ("{0} {1:o}" -f $RunId, [DateTimeOffset]::UtcNow)
                        $built = $true
                        $publish.performed = $true
                        $publish.result = 'published'
                    } else {
                        $publish.result = 'build failed - kept current deployment (passthrough fallback intact)'
                    }
                }
                if ($scratch -and (Test-Path $scratch)) {
                    $null = Invoke-Git -ArgumentList @('worktree', 'remove', '--force', $scratch)
                    $null = Invoke-Git -ArgumentList @('worktree', 'prune')
                }
            }

            if ($built) {
                $vtkLink = Join-Path $ToolsDir 'vtk'
                $flipOk = $true
                if (Test-Path $vtkLink) {
                    $item = Get-Item $vtkLink -Force
                    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                        # One-time migration: plain dir -> vtk-releases/legacy.
                        try {
                            Move-Item -Path $vtkLink -Destination (Join-Path $releasesDir 'legacy') -ErrorAction Stop
                            $publish.migration = 'migrated plain ~/tools/vtk to vtk-releases/legacy'
                        } catch {
                            $publish.migration = 'migration failed (files locked) - flip skipped; a later cycle retries'
                            $flipOk = $false
                        }
                    } else {
                        # Junction: rmdir removes only the link, never the target.
                        & cmd /c rmdir $vtkLink 2>&1 | Out-Null
                        if (Test-Path $vtkLink) { $flipOk = $false; Note 'junction rmdir failed - flip skipped' }
                    }
                }
                if ($flipOk) {
                    try {
                        $null = New-Item -ItemType Junction -Path $vtkLink -Target $releaseDir -ErrorAction Stop
                        $publish.flipped = $true
                    } catch {
                        Note "junction create failed: $($_.Exception.Message)"
                    }
                }

                # Post-deploy health check: exit-code parity IS the invariant —
                # `vtk cmd /c "exit 3"` must exit 3.
                if ($publish.flipped) {
                    $vtkExe = Join-Path $vtkLink 'vtk.exe'
                    if (Test-Path $vtkExe) {
                        & $vtkExe cmd /c 'exit 3' *> $null
                        $publish.healthCheck = [ordered]@{ exitCode = $LASTEXITCODE; ok = ($LASTEXITCODE -eq 3) }
                        if ($LASTEXITCODE -ne 3) { Note "HEALTH CHECK FAILED: vtk cmd /c 'exit 3' exited $LASTEXITCODE (expected 3) - exit-code parity broken in deployed binary" }
                    } else {
                        $publish.healthCheck = [ordered]@{ exitCode = $null; ok = $false }
                        Note 'health check skipped - vtk.exe not found in flipped release'
                    }

                    # GC: keep the junction target and the two newest releases.
                    $linkItem = Get-Item $vtkLink -Force
                    $target = $linkItem.LinkTarget ?? $releaseDir
                    if ($target -is [array]) { $target = $target[0] }
                    $keep = @($target) + @(Get-ChildItem $releasesDir -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 2 | ForEach-Object { $_.FullName })
                    foreach ($dir in Get-ChildItem $releasesDir -Directory) {
                        if ($keep -contains $dir.FullName) { continue }
                        try {
                            Remove-Item $dir.FullName -Recurse -Force -ErrorAction Stop
                            $publish.gcRemoved += $dir.Name
                        } catch {
                            $publish.gcLeft += $dir.Name  # still executing — leave it
                        }
                    }
                }
            }
        }

        # 0a.4 — worktree sweep: remove clean issue worktrees whose branch is
        # merged to dev, or upstream-gone with the issue closed.
        $wtPorcelain = (Invoke-Git -ArgumentList @('worktree', 'list', '--porcelain')).Output
        $wtEntries = @(); $cur = $null
        foreach ($line in $wtPorcelain) {
            if ($line -like 'worktree *') { if ($cur) { $wtEntries += $cur }; $cur = @{ Path = $line.Substring(9); Branch = $null } }
            elseif ($line -like 'branch refs/heads/*') { $cur.Branch = $line.Substring(18) }
        }
        if ($cur) { $wtEntries += $cur }
        $checkedOutBranches = @($wtEntries | Where-Object { $_.Branch } | ForEach-Object { $_.Branch })

        foreach ($wt in $wtEntries) {
            if ($wt.Path -notmatch '[\\/]vtk-wt[\\/](\d+)$' -or -not $wt.Branch) { continue }
            $issueNum = $Matches[1]
            $merged = (Invoke-Git -ArgumentList @('merge-base', '--is-ancestor', $wt.Branch, 'dev')).Ok
            $removable = $merged
            if (-not $removable) {
                $track = ((Invoke-Git -ArgumentList @('for-each-ref', '--format', '%(upstream:track)', "refs/heads/$($wt.Branch)")).Output -join '').Trim()
                if ($track -eq '[gone]') {
                    $state = Invoke-Gh -ArgumentList @('issue', 'view', $issueNum, '-R', $Repo, '--json', 'state', '--jq', '.state')
                    $removable = ("$state" -eq 'CLOSED')
                }
            }
            if (-not $removable) { continue }
            $status = (Invoke-Git -ArgumentList @('status', '--porcelain') -WorkDir $wt.Path).Output -join ''
            if ([string]::IsNullOrWhiteSpace($status)) {
                $rm = Invoke-Git -ArgumentList @('worktree', 'remove', $wt.Path)
                if ($rm.Ok) { $worktrees.removed += $wt.Path } else { $worktrees.left += "$($wt.Path) (remove failed)" }
            } else {
                $worktrees.left += "$($wt.Path) (dirty)"
            }
        }
        $null = Invoke-Git -ArgumentList @('worktree', 'prune')
        $worktrees.pruned = $true

        # 0a.5 — prune local branches merged to dev; squash-merged only under
        # the three-way check (upstream gone + PR MERGED + tip == headRefOid).
        $checkedOutBranches = @(((Invoke-Git -ArgumentList @('worktree', 'list', '--porcelain')).Output |
                Where-Object { $_ -like 'branch refs/heads/*' } | ForEach-Object { $_.Substring(18) }))
        $mergedBranches = @((Invoke-Git -ArgumentList @('branch', '--merged', 'dev', '--format', '%(refname:short)')).Output |
                ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notin @('dev', 'main') -and $_ -notin $checkedOutBranches })
        foreach ($branch in $mergedBranches) {
            if ((Invoke-Git -ArgumentList @('merge-base', '--is-ancestor', $branch, 'dev')).Ok) {
                $del = Invoke-GitRetryOnce -ArgumentList @('branch', '-D', $branch)
                if ($del.Ok) { $branches.pruned += $branch } else { $branches.left += "$branch (delete refused - in use)" }
            }
        }
        $goneBranches = @((Invoke-Git -ArgumentList @('for-each-ref', '--format', '%(refname:short) %(upstream:track)', 'refs/heads/')).Output |
                Where-Object { $_ -match '^(\S+) \[gone\]$' } | ForEach-Object { ($_ -split ' ')[0] } |
                Where-Object { $_ -notin @('dev', 'main') -and $_ -notin $checkedOutBranches -and $_ -notin $branches.pruned })
        foreach ($branch in $goneBranches) {
            $pr = Invoke-Gh -ArgumentList @('pr', 'view', $branch, '-R', $Repo, '--json', 'state,headRefOid')
            $tip = ((Invoke-Git -ArgumentList @('rev-parse', $branch)).Output -join '').Trim()
            if ($null -ne $pr -and $pr.state -eq 'MERGED' -and $pr.headRefOid -eq $tip) {
                $del = Invoke-GitRetryOnce -ArgumentList @('branch', '-D', $branch)
                if ($del.Ok) { $branches.pruned += "$branch (squash-merged)" } else { $branches.left += "$branch (delete refused - in use)" }
            } else {
                $branches.left += "$branch (upstream gone but squash-merge checks ambiguous)"
            }
        }
    } finally {
        # Release at the end of Step 0a — success or not; lane dispatch never
        # needs the lock.
        Remove-Item -Path $lockDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --------------------------------------------------------------------------
# Digest.
# --------------------------------------------------------------------------
[ordered]@{
    runId       = $RunId
    generatedAt = '{0:o}' -f $now
    rootGate    = $rootGate
    machineLock = $machineLock
    issues      = [ordered]@{
        snapshot   = @($issueSnapshot | ForEach-Object {
                [ordered]@{ number = $_.number; title = $_.title; labels = @($_.labels | ForEach-Object name); createdAt = $_.createdAt; updatedAt = $_.updatedAt }
            })
        laneDepths = $laneDepths
        wip        = $wip
        needsHuman = $needsHuman
        hold       = $holds
    }
    git         = $git
    publish     = $publish
    worktrees   = $worktrees
    branches    = $branches
    prs         = $prs
    notes       = @($script:Notes)
} | ConvertTo-Json -Depth 8
exit 0
