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
  - build: `go build -o vtk.exe ./cmd/vtk` · targeted: `go test ./internal/<pkg>/...`
  - full suite: `go test ./...` (+ `go test -race ./...`, needs `CGO_ENABLED=1`/MinGW gcc)
  - smoke: `test/smoke/` · lint: `go vet ./...`
  - decision record: GitHub issues registry (no ADRs).
- **Deterministic core:** none yet (label rituals in-prompt).
- **Known deviations:** none declared.
