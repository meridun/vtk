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
  ([`dispatch.md`](dispatch.md)) — wip-lock gate, git maintenance, one `vtk-sdlc-worker`
  subagent per non-empty lane, serially, in pipeline order. **Not yet enabled** — turn on once
  queue depth justifies the spend.
- **Manual:** paste this README plus a worker file's body into an agent session. Identical
  behavior — the prompt doesn't know what fired it.

## Universal worker loop (binding)

1. **CLAIM** — list open issues labeled `stage:<lane>` that are **NOT** labeled `sdlc:wip`,
   `sdlc:needs-human` (parked), or `sdlc:hold` (human keep-off). Pick the next: higher priority
   first (`priority:critical` › `priority:medium` › `priority:future`), then oldest by creation
   date (FIFO). If none → reply `<LANE>: idle` and stop. Add `sdlc:wip` to the chosen issue
   **before** doing anything else — it is the lock (machine-owned, volatile; the dispatcher's
   reaper may strip it).
2. **WORK** — per the lane file, with these constraints:
   - **Never delegate — do all work inline, yourself.** No subagents (they run async; the worker
     yields and the item strands under `sdlc:wip`), no background tasks, no wait loops.
   - **Idempotent.** If the stage's artifact already exists, treat as done; do not redo.
   - **Tree hygiene.** Before any branch switch, record the entry branch
     (`git rev-parse --abbrev-ref HEAD`) and restore exactly it before EMIT. Never stash,
     discard, or overwrite uncommitted files you didn't create (human WIP); if they genuinely
     block the work, PARK.
3. **EMIT exactly one outcome** — ADVANCE, BOUNCE, or PARK (build also defines CONTINUE) — never
   silent. **Every outcome removes `sdlc:wip`** on the way out.
4. **STOP** — reply the lane's one-line result. One item per pass; never pick up a second.

## vtk specifics (bind in every lane)

- **Integration branch is `main`.** Feature branches `<type>/<issue#>-<slug>` cut from `main`;
  PRs target `main`; the human merges.
- **Go conventions:** `go vet ./...` before any commit; `gofmt` formatting; table-driven
  fixture tests for filters (fixture in → expected compact out, no process spawning in filter
  unit tests).
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
| [`build.md`](build.md) | `stage:build` → `stage:verify` | implement + targeted tests |
| [`verify.md`](verify.md) | `stage:verify` → `stage:audit` | full suite + race + real-run smoke |
| [`audit.md`](audit.md) | `stage:audit` → `stage:ship` | security/invariant review of the diff |
| [`ship.md`](ship.md) | `stage:ship` → *(closed on merge)* | docs fan-out + PR |
