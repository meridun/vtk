# SDLC conformance profile: vtk

Bindings per the agentic-sdlc spec (`agentic-sdlc/docs/Composability.md`).

- **Spine:** `intake → queued → build → verify → audit → ship`, collapsed tail (human PR merge is
  the `ready` gate; `shipping → complete` = merge-and-close).
- **VP1 tracker:** GitHub issues, standard label taxonomy.
- **VP2 topology:** single repo.
- **VP3 modules:** design lane **off** — CLI tool, no UI to storyboard; design questions are
  intake decision debates (PARK, human decides, `<DECISION_RECORD>` in-issue). PSI lane **off**.
- **VP4 dispatcher:** Claude Code scheduled task → [dispatch.md](dispatch.md) → one
  `vtk-sdlc-worker` subagent per non-empty lane; no dispatcher singleton — concurrent runs
  deconflict via per-issue claim comments, issue-scoped worktrees (`C:\Claude\vtk-wt`), a
  per-machine maintenance lock (`.git/sdlc-maint.lock`), and idempotent GitHub writes.
- **VP5 quality bars:**
  - build: `dotnet build dotnet/Vtk.sln` (0 errors, no new warnings) · targeted:
    `dotnet test dotnet/Vtk.Tests --filter "FullyQualifiedName~Vtk.Tests.<Area>"`
  - full suite: `dotnet test dotnet/Vtk.sln` (no race-detector equivalent in .NET — the spool's
    concurrency claims are exercised by the smoke suite's concurrent-invocation tests)
  - smoke: `dotnet/Vtk.Tests/Smoke/` xunit collection against the published binary
    (`dotnet publish dotnet/Vtk.Cli -c Release -o dotnet/Vtk.Cli/bin/smoke`; `VTK_SMOKE_BIN`
    overrides) · format: `dotnet format whitespace dotnet/Vtk.sln --verify-no-changes`
  - decision record: GitHub issues registry (no ADRs).
  - dependency audit (`<DEP_AUDIT_CMD>`): `dotnet list dotnet/Vtk.sln package --vulnerable
    --include-transitive` — intake's per-pass sweep (one batch issue) + audit's manifest-diff
    check. Migration knobs (`<MIGRATIONS_DIR>` &c.) **unbound** — no database.
- **Deterministic core:** `scripts/sdlc-maint.ps1` — dispatcher Steps -1/0/0a (machine lock,
  issue + PR snapshot with claim ages, git/worktree/publish maintenance) plus the
  **native-dependency eligibility gate**, derived `blocked`/`ready` label plan + lint, and the
  close-sweep work-list (pure math in `scripts/lib/SdlcDeps.psm1`, tested by
  `scripts/tests/SdlcDeps.Tests.ps1`). Claim/release rituals in `scripts/sdlc-claim.ps1` /
  `scripts/sdlc-release.ps1`. The script computes; the dispatcher writes.
- **Known deviations:** the close sweep has no ack marker (the spec's `sweep --ack`) — it is
  bounded by the 24h window and idempotent via the "blocker closed" comment, the spec's
  CLI-less form.
