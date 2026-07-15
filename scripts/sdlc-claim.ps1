#Requires -Version 7.0
<#
.SYNOPSIS
    Atomic CLAIM for the SDLC universal worker loop (prompts/sdlc/README.md,
    step 1): add sdlc:wip, post the claim comment, run the claim-verify race
    check. Prints exactly one line on stdout.

.DESCRIPTION
    Mechanical form of README CLAIM sub-steps i-iii (issue #76); the README
    prose remains the normative spec.

    - "Newer than the last outcome EMIT" is encoded as: newer than the
      issue's most recent sdlc:wip *unlabeled* timeline event. Every outcome
      EMIT removes sdlc:wip, so that event is the machine-visible boundary;
      a reaper strip also (correctly) invalidates earlier claims.
    - Winner = earliest live claim by createdAt, ties broken by
      lexicographically lower run-id. Re-running with the winning run-id
      prints WON again (idempotent re-claim).
    - On LOST: the label and the winner's claim are left untouched, nothing
      is deleted; only this run's own claim comment is edited to append
      "(superseded)".
    - If verification itself fails (gh unavailable mid-sequence), the label
      and claim comment are left in place - a re-run with the same run-id
      completes the verify idempotently.

    Output contract / exit codes:
      WON                   0   claim held; proceed to WORK
      LOST <winner-run-id>  1   lost the race; pick the next eligible item
      INELIGIBLE <reason>   3   issue closed, sdlc:needs-human, or sdlc:hold
      ERROR <reason>        2   gh/network failure; nothing verified

.PARAMETER IssueNumber
    The issue to claim.

.PARAMETER RunId
    This worker's run identifier (dispatcher-supplied, or minted for a
    manual run). Recorded in the claim comment: "sdlc:claim <run-id> <lane>".

.PARAMETER Lane
    The lane claiming the issue (intake|build|verify|audit|ship).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][int]$IssueNumber,
    [Parameter(Mandatory, Position = 1)][string]$RunId,
    [Parameter(Mandatory, Position = 2)]
    [ValidateSet('intake', 'build', 'verify', 'audit', 'ship')][string]$Lane,
    [string]$Repo = 'meridun/vtk'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

function ConvertTo-UtcOffset {
    # ConvertFrom-Json may hand back [DateTime] (already parsed) or a raw ISO
    # string; normalize both to a UTC DateTimeOffset without double-shifting.
    param([Parameter(Mandatory)]$Value)
    if ($Value -is [DateTime]) { return [DateTimeOffset]::new($Value.ToUniversalTime(), [TimeSpan]::Zero) }
    return [DateTimeOffset]::Parse("$Value", [cultureinfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
}

function Get-ClaimRunId {
    # Claim comment format: "sdlc:claim <run-id> <lane>".
    param([Parameter(Mandatory)][string]$Body)
    return ((($Body -split '\s+') | Select-Object -Skip 1 -First 1) ?? 'unknown')
}

# --------------------------------------------------------------------------
# Eligibility gate (README CLAIM: never claim parked/held/closed items).
# --------------------------------------------------------------------------
$issueRaw = (& gh issue view $IssueNumber -R $Repo --json state,labels 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { "ERROR gh issue view failed: $issueRaw"; exit 2 }
$issue = ConvertFrom-Json -InputObject $issueRaw
if ($issue.state -ne 'OPEN') { "INELIGIBLE issue is $($issue.state)"; exit 3 }
$labels = @($issue.labels | ForEach-Object name)
foreach ($block in @('sdlc:needs-human', 'sdlc:hold')) {
    if ($labels -contains $block) { "INELIGIBLE labeled $block"; exit 3 }
}
$hadWip = $labels -contains 'sdlc:wip'

# --------------------------------------------------------------------------
# i. Add sdlc:wip (idempotent if already present).
# --------------------------------------------------------------------------
$out = (& gh issue edit $IssueNumber -R $Repo --add-label 'sdlc:wip' 2>&1) -join ' '
if ($LASTEXITCODE -ne 0) { "ERROR add-label sdlc:wip failed: $out"; exit 2 }

# --------------------------------------------------------------------------
# ii. Post the claim comment (ownership record + tiebreaker).
# --------------------------------------------------------------------------
$claimBody = "sdlc:claim $RunId $Lane"
$out = (& gh issue comment $IssueNumber -R $Repo --body $claimBody 2>&1) -join ' '
if ($LASTEXITCODE -ne 0) {
    if (-not $hadWip) {
        # Roll back only a label this run added - never someone else's lock.
        & gh issue edit $IssueNumber -R $Repo --remove-label 'sdlc:wip' 2>&1 | Out-Null
    }
    "ERROR claim comment failed: $out"; exit 2
}
$myCommentId = if ($out -match 'issuecomment-(\d+)') { $Matches[1] } else { $null }

# --------------------------------------------------------------------------
# iii. Claim-verify: boundary = newest sdlc:wip unlabel event; live claims
# are claim comments after it; earliest live claim wins (run-id tiebreak).
# --------------------------------------------------------------------------
$boundary = [DateTimeOffset]::MinValue
$tl = & gh api "repos/$Repo/issues/$IssueNumber/timeline?per_page=100" --paginate `
    --jq '.[] | select(.event=="unlabeled" and .label.name=="sdlc:wip") | .created_at' 2>$null
if ($LASTEXITCODE -ne 0) { "ERROR claim-verify timeline fetch failed; label and claim left in place - re-run to verify"; exit 2 }
$lastUnlabel = @($tl | Where-Object { $_ -and "$_" -ne 'null' }) | Select-Object -Last 1
if ($lastUnlabel) { $boundary = ConvertTo-UtcOffset $lastUnlabel }

$claimsRaw = (& gh issue view $IssueNumber -R $Repo --json comments `
        --jq '[.comments[] | select(.body | startswith("sdlc:claim")) | {url, createdAt, body}]' 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { "ERROR claim-verify comment fetch failed; label and claim left in place - re-run to verify"; exit 2 }
# Plain @() collection (no -NoEnumerate): the JSON array unrolls into pipeline
# items and @() recollects them - 0 claims => empty array, never $null nesting.
$claims = @(ConvertFrom-Json -InputObject $claimsRaw)

$live = @($claims | Where-Object { (ConvertTo-UtcOffset $_.createdAt) -gt $boundary })
if ($live.Count -eq 0) { "ERROR claim-verify saw no live claims (own comment not visible yet) - re-run to verify"; exit 2 }

$winner = $live |
    Sort-Object @{ Expression = { ConvertTo-UtcOffset $_.createdAt } }, @{ Expression = { Get-ClaimRunId $_.body } } |
    Select-Object -First 1
$winnerRunId = Get-ClaimRunId $winner.body

if ($winnerRunId -eq $RunId) { 'WON'; exit 0 }

# Lost the race: leave label + winner's claim untouched; mark only own comment.
if ($myCommentId) {
    & gh api -X PATCH "repos/$Repo/issues/comments/$myCommentId" -f body="$claimBody (superseded)" 2>&1 | Out-Null
}
"LOST $winnerRunId"
exit 1
