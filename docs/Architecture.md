# Architecture

Current and future-facing only. Rationale and rejected alternatives live in the linked GitHub
issues, not here.

## Decision registry

One line per decision, present tense, pointer to the debate. (Split into a child file when this
outgrows a screen.)

- Implementation language is **C# / .NET 9** — [#1](https://github.com/meridun/vtk/issues/1),
  [PR #57](https://github.com/meridun/vtk/pull/57)
- Raw output is spooled per command with a 4-hex-char command-hash ID, provenance header,
  atomic-rename writes, ~1h TTL sweep — [#2](https://github.com/meridun/vtk/issues/2)
- Registry entries may declare a per-filter exit-code allowlist of additional "expected" exit
  codes to filter (report-style tools; eslint: {0, 1}, exit 2+ stays raw). Exit-code parity
  unaffected — only the display path changes — [#7](https://github.com/meridun/vtk/issues/7)
- Intake is exempt from the one-item-per-pass loop: an intake worker may triage up to **5**
  items per pass, claiming/releasing each individually (per-issue lock semantics unchanged,
  one claim comment + one EMIT per item) — [#19](https://github.com/meridun/vtk/issues/19)
- The "no network calls" non-goal binds the **wrap path** and telemetry storage only; explicit
  user-invoked maintenance subcommands may shell out to `gh` etc. —
  [#34](https://github.com/meridun/vtk/issues/34)
- `vtk install` wires the wrappers into a shell rc/profile itself, rather than shipping a
  hand-copied dotfile snippet: self-locating via the running executable's path, marker-delimited managed
  block (idempotent + exact `--uninstall`), guarded on `$CLAUDECODE` — [#49](https://github.com/meridun/vtk/issues/49)
- Spool + the `OK <id>` recover-me signal are gated on **real** savings, not any positive byte
  delta: the compact result must clear an absolute-byte floor **and** a savings ratio (option C);
  below the bar vtk emits inline with no spool and no `OK`, so lossless reformats (e.g. `git
  branch`) never fire a false recover-me signal — [#52](https://github.com/meridun/vtk/issues/52)
- `vtk hooks` targets a **single canonical vtk binary — the C# port** (retiring the two-binary
  wiring); MVP scope first (`hooks init` + `verify`) with installers for **both Claude Code and
  GitHub Copilot**; trust/permissions port deferred to follow-ups —
  [#45](https://github.com/meridun/vtk/issues/45)
- `vtk gain` dollarization prices from a **local checked-in model→$ table** (updated by PR; no
  runtime fetch — no-network non-goal stands) using a **bytes/4 token heuristic**; log-based
  rollups (`--graph/--history/--daily`) split to
  [#58](https://github.com/meridun/vtk/issues/58), per-session view stays in #46 blocked on the
  shared session provider (#43/#44) — [#46](https://github.com/meridun/vtk/issues/46)

## Pipeline

```
vtk <cmd> [args...]
  │
  ├─ 1. Dispatch: match <cmd> (+ subcommand) against the filter registry
  │      match  → run command, capture output, spool raw, apply filter, emit compact result
  │      no match → run command with output passed through untouched, write gap entry
  │      wrapper (`npm run <script>`) → capture once, strip the wrapper banner, re-dispatch
  │      the body to the inner tool's filter; an uncovered inner tool gap-logs under the
  │      inner tool's family, not the wrapper's
  │
  ├─ 2. Preserve semantics: exit code and TTY detection mirror the wrapped command.
  │      Interactive/TTY-detected invocations bypass filtering entirely. On the filtered
  │      success path, stdout and stderr are folded into one compact result on stdout;
  │      per-stream separation is preserved on all raw, passthrough, and degraded paths.
  │      Nonzero child exits skip filtering — failures always emit raw — except codes a
  │      filter declares in its exit-code allowlist (report-style tools; see registry #7).
  │
  └─ 3. Sweep: opportunistically delete spool entries past TTL (no daemon)
```

## Output spool

When a filter elides content, the compact result carries a retrieval ID:

```
vtk git commit -m "msg"   →   OK 2e3f
vtk show 2e3f             →   full captured output (provenance header first)
vtk show 2e3f --grep pat  →   just the matching lines
```

- **ID** = first 4 hex chars of a checksum of the command line. Rerunning the same command
  overwrites its own spool file — the spool holds "latest output per distinct command," which
  also caps its size naturally.
- **Provenance header** — full command + timestamp at the top of each spool file, printed by
  `vtk show`, so a residual hash collision is visible rather than silently serving wrong output.
- **Writes** are temp-file + atomic rename: no locks, safe under parallel vtk invocations,
  readers never see a partial file.
- **Eviction** — TTL sweep (~1h) on each invocation, so secret-bearing output does not persist
  until a coincidental overwrite.
- **Redaction** — obvious credential patterns (`Authorization:` headers, `AWS_SECRET*`, PEM
  blocks) are masked before write. The spool dir is user-local with restrictive permissions,
  never inside a repo. On Windows, POSIX 0700 is a no-op on NTFS; protection rests on the
  default user-scoped ACLs of `%LocalAppData%`.
- The ID is emitted only when content was actually elided; full-passthrough commands print no ID.

## Components

- **Registry** — maps command/subcommand patterns to filters. Filters are pure functions:
  raw output in, compacted output out. Filters are hand-written C# classes, one per tool family
  (git, gh, npm, ... in `dotnet/Vtk.Core/Filter/`). A second declarative-TOML filter source
  (regex drop/keep/replace specs embedded at build, regex-fallback dispatch below exact-key
  matches) is planned — [#61](https://github.com/meridun/vtk/issues/61).
- **Runner** — spawns the wrapped command, streams/captures output, propagates exit code and
  signals. The only component with process-spawning responsibility.
- **Spool store** — the raw-output files above, plus per-invocation metadata (argv, byte
  counts, filtered/passthrough, unfiltered reason, timestamp). Backs `vtk show`, `vtk gain`,
  and `vtk gaps` — one store, three queries.

## Key invariants

1. Exit code of `vtk <cmd>` == exit code of `<cmd>`. Always.
2. A filter failure (panic in filter code) degrades to raw passthrough, never to lost output or
   a vtk-originated nonzero exit — and it stays visible: logged (`reason: filter-panic`) and
   surfaced (stderr warning + a degraded section in `vtk gaps`).
3. Gap/stats metadata never includes output content; only the short-lived spool files do.
4. Filters are testable in isolation: fixture in, expected out, no process spawning in filter
   unit tests.
