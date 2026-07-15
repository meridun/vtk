#Requires -Version 7.0
<#
.SYNOPSIS
    Idempotent lock release for the SDLC universal worker loop
    (prompts/sdlc/README.md, step 3): strips sdlc:wip on EMIT.

.DESCRIPTION
    Mechanical form of the README EMIT rule "every outcome removes sdlc:wip"
    (issue #76); the README prose remains the normative spec. Safe to re-run:
    a missing label is a successful no-op, not an error.

    Output contract / exit codes:
      RELEASED <n>                  0   label removed
      RELEASED <n> (already clear)  0   label was not present
      ERROR <reason>                2   gh/network failure
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][int]$IssueNumber,
    [string]$Repo = 'meridun/vtk'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$labelsRaw = (& gh issue view $IssueNumber -R $Repo --json labels --jq '[.labels[].name]' 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { "ERROR gh issue view failed: $labelsRaw"; exit 2 }
# Plain @() collection (no -NoEnumerate): the JSON array unrolls into pipeline
# items and @() recollects them - 0 labels => empty array, never $null nesting.
$labels = @(ConvertFrom-Json -InputObject $labelsRaw)

if ($labels -notcontains 'sdlc:wip') { "RELEASED $IssueNumber (already clear)"; exit 0 }

$out = (& gh issue edit $IssueNumber -R $Repo --remove-label 'sdlc:wip' 2>&1) -join ' '
if ($LASTEXITCODE -ne 0) { "ERROR remove-label sdlc:wip failed: $out"; exit 2 }
"RELEASED $IssueNumber"
exit 0
