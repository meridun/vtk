# SDLC worker prompts

Executable artifacts (not reference docs). Each file is **one stage worker** for vtk's agentic
SDLC pipeline (adapted from IsekaiOnline's). All share the universal worker loop below; lane
files define only their WORK and EMIT specifics.

Pipeline: `stage:intake` → `stage:queued` → `stage:build` → `stage:verify` → `stage:audit` →
`stage:ship` → *(PR merged, closed)*.

There is **no design lane**: vtk is a CLI tool with no UI to storyboard. Design questions are
handled at intake as decision debates — intake PARKs with the options framed in-issue; when the
human decides, intake records a one-liner in the [decision registry](../../docs/Architecture.md#decision-registry)
and routes onward. `stage:queued` is intentionally workerless — the human throttle.

## How to run

- **Scheduled:** an `sdlc-dispatch` scheduled task runs the dispatcher prompt
  ([`dispatch.md`](dispatch.md)) — per-issue wip gate (stale-lock reaping, verify-before-write),
  machine-locked git + worktree maintenance, one `vtk-sdlc-worker` subagent per non-empty lane
  (concurrently — per-issue claims and issue-scoped worktrees make lanes independent). There is
  no dispatcher singleton: overlapping dispatch runs — same machine or different machines —
  deconflict via per-issue claims, a per-machine maintenance lock, and idempotent GitHub
  writes. **Not yet enabled** — turn on once queue depth justifies the spend.
- **Manual:** paste this README plus a worker file's body into an agent session. Identical
  behavior — the prompt doesn't know what fired it. Mint your own run-id for the claim comment.
  Manual and scheduled runs coexist safely: claims deconflict per-issue.

## Universal worker loop (binding)

1. **CLAIM** — list open issues labeled `stage:<lane>` that are **NOT** labeled `sdlc:wip`,
   `sdlc:needs-human` (parked), or `sdlc:hold` (human keep-off). Pick the next: higher priority
   first (`priority:critical` › `priority:medium` › `priority:future`), then oldest by creation
   date (FIFO). If none → reply `<LANE>: idle` and stop. Then take the lock, in this order:
   1. Add `sdlc:wip` to the chosen issue.
   2. Post a claim comment: `sdlc:claim <run-id> <lane>` (run-id = the dispatcher-supplied id,
      or any unique id you mint for a manual run). The label is the visibility signal; the
      claim comment is the ownership record and tiebreaker.
   3. **Claim-verify:** re-fetch the issue's comments. If another `sdlc:claim` comment on this
      issue is newer than the last outcome EMIT and predates yours (or ties with a
      lexicographically lower run-id), you lost the race — leave the label and the winner's
      claim untouched, delete nothing, and go pick the next eligible item. Only the losing
      worker's own claim comment may be edited to note `superseded`.

   The lock is machine-owned and volatile; the dispatcher's reaper may strip it, and it
   re-checks the claim comment's run-id + timestamp immediately before doing so.
2. **WORK** — per the lane file, with these constraints:
   - **Never delegate — do all work inline, yourself.** No subagents (they run async; the worker
     yields and the item strands under `sdlc:wip`), no background tasks, no wait loops.
   - **Idempotent — reconcile, don't re-execute.** If the stage's artifact already exists, treat
     as done; do not redo. Schedulers fire on a clock, not on need — a re-run must be a safe
     no-op. The same applies to items a human rewound to an earlier stage or reopened after
     close: investigate what already exists before doing any work. Evidence hierarchy: merged
     code / branch state / PR status › recorded reports for the current branch HEAD › issue
     comments › labels — cite what you relied on when you no-op. Presume an existing valid
     artifact good unless the human's rewind comment gives a reason to distrust it or your own
     check finds something significant; then redo exactly the invalidated part. Keep the check
     cheap — dig deeper only when evidence conflicts, and PARK if it stays ambiguous rather than
     burn the pass. For partial work, post a short reconciliation note (what's already done +
     evidence, what remains) before continuing, then do only the gap. If the item is conclusively
     shipped already, PARK with the evidence (PR#, commit, observed behavior) for a human to
     close — don't march it through the remaining lanes.
   - **Worktree isolation.** Never work in the main checkout — it may hold human WIP or another
     worker. For any lane that touches a branch, use the issue-scoped worktree
     `../vtk-wt/<issue#>`: create it if missing (`git worktree add ../vtk-wt/<issue#> <branch>`,
     cutting the branch first if the lane owns branch creation), reuse it if present. Git's
     one-checkout-per-branch rule across worktrees is a second lock layer: if `worktree add`
     fails because the branch is checked out elsewhere, treat it as a lost claim race — release
     per CLAIM step 3 and move on. Do all git/build/test work inside the worktree; never stash,
     discard, or overwrite files in the main tree.
   - **Refresh from dev (staleness rule).** On entering the worktree: `git fetch origin`. If
     `git diff --name-only HEAD...origin/dev` (upstream side) intersects the paths this branch
     touches, `git merge origin/dev` (merge, never rebase — branches are pushed and handed
     between workers). No overlap → record "dev advanced, no path overlap" and do not merge, so
     existing verify/audit reports stay valid. **Conflict ownership:** build resolves merge
     conflicts; verify and audit never do — a conflicted merge there is a BOUNCE → `stage:build`
     naming the conflicting paths. Ship always merges (the PR must be mergeable) and may resolve
     docs-only conflicts itself; code conflicts BOUNCE to build.
3. **EMIT exactly one outcome** — ADVANCE, BOUNCE, or PARK (build also defines CONTINUE) — never
   silent. **Every outcome removes `sdlc:wip`** on the way out. Leave the worktree in place
   (dispatcher maintenance prunes worktrees for merged/dead branches).
4. **STOP** — reply the lane's one-line result. One item per pass; never pick up a second.
   **Intake-only exception (#19):** the intake worker may loop up to **5 items** per pass —
   triage is cheap and stateless. Each item is claimed and released individually (one claim
   comment, one EMIT per item), so per-issue lock semantics are unchanged and the lane still
   never has two workers.

## vtk specifics (bind in every lane)

- **Git flow is `{feature} → dev → main`.** Feature branches `<type>/<issue#>-<slug>` cut from
  `dev` (the default/integration branch); PRs target `dev`; the human merges. `main` is the
  stable/release branch — it moves only by human-initiated PR from `dev`. **No worker ever
  branches from, checks out, commits to, or targets `main`.**
- **.NET conventions:** `dotnet build dotnet/Vtk.sln` clean (0 errors, no new warnings) and
  `dotnet format whitespace dotnet/Vtk.sln --verify-no-changes` before any commit; table-driven
  fixture tests (xunit `[Theory]`) for filters (fixture in → expected compact out, no process
  spawning in filter unit tests).
- **Invariants from [Architecture.md](../../docs/Architecture.md#key-invariants)** are
  acceptance criteria on every change: exit-code parity, filter-failure degrades to passthrough,
  metadata never contains output content, filters fixture-testable.
- **No decisions in docs prose** — decisions are registry one-liners linking to the GitHub issue
  that holds the debate. Workers never write ADR-style history into docs.

## Files

| File | Stage | Notes |
|---|---|---|
| [`dispatch.md`](dispatch.md) | *(dispatcher — runs every lane)* | scheduled task, when enabled |
| [`intake.md`](intake.md) | `stage:intake` → `stage:queued` | triage + decision debates + merge sweep |
| [`build.md`](build.md) | `stage:build` → `stage:verify` | plan comment → implement + targeted tests |
| [`verify.md`](verify.md) | `stage:verify` → `stage:audit` | full suite + race + real-run smoke |
| [`audit.md`](audit.md) | `stage:audit` → `stage:ship` | security/invariant review of the diff |
| [`ship.md`](ship.md) | `stage:ship` → *(closed on merge)* | docs fan-out + PR |
