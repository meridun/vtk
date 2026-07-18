# Tool Coverage

Filter catalog: rtk-parity targets first, then vtk expansions. Status values:
`planned` → `in-progress` → `shipped`. Savings % are rtk's published figures where they exist;
vtk figures get measured from fixtures once filters ship.

Upstream baseline: [rtk-ai/rtk](https://github.com/rtk-ai/rtk) now ships ~65 filters plus a TOML
rule engine and `learn`/`discover`/`hooks`/`analytics` subsystems. Several parity rows below that
vtk still lists `planned` are now shipped and validated upstream (noted per-row); the Status
column tracks vtk's own implementation state, so those rows stay `planned` until vtk ships them.
The TOML long-tail and the subsystems are fresh parity gaps tracked in #41 and #43–#46.

**Adding a filter.** Filters are hand-written C# classes under `dotnet/Vtk.Core/Filter/`,
registered in `Registry.cs`, with fixture-based tests (raw in → expected compact out + savings
assertion) under `dotnet/Vtk.Tests/Filter/`. Captured fixtures under
`dotnet/Vtk.Tests/Filter/testdata/` are exempt from git eol conversion (`.gitattributes -text`)
so golden files stay byte-exact on every platform. A second, cheaper path ships for families that
are just *drop/keep/replace by regex* ([#61](https://github.com/meridun/vtk/issues/61)): a ~20-line
declarative TOML spec under `dotnet/Vtk.Core/Filter/Toml/Defs/` (authoring guide in
`Defs/README.md`), embedded at build. Defs carry their own inline `[[tests.*]]` fixtures, replayed
by the test suite, and dispatch by regex only after both exact-key registry probes miss — a
hand-written filter always wins its command.

## rtk parity targets

| Family | Commands | rtk savings | Status |
|---|---|---|---|
| Tests | `mocha`, `npx mocha`, `playwright test`, generic `test <cmd>` wrapper | 90–94% | in-progress — `mocha`/`npx mocha` shipped (#12): folds passing/pending/suite spec-tree lines, keeps summary + failure-detail blocks (assertion + stack) verbatim; measured 23–94% on fixtures (green run 94%, pending 81%, 3-failure run 23% — savings scale with pass:fail ratio). Exit `{0,1}` filtered (1 = test failures, a report per decision #7), `2`+ (config error) stays raw; `npm run test`-wrapped mocha covered via the npm-run dispatch layer. `playwright test` now shipped/validated upstream (rtk-ai/rtk), vtk: planned; generic `test` wrapper planned |
| Lint/format | `eslint`, `npx eslint` | 70–84% | shipped — measured 41–99% on fixtures (small multi-rule 41%, 500-problem report 99%); direct invocations only, exit {0,1} filtered / 2+ raw |
| Lint/format (rest) | `prettier --check`, `npm run lint` wrapped path | 70–84% | in-progress — `npm run` dispatch layer shipped (#8): banner strip + inner-tool delegation; `prettier --check` now shipped/validated upstream (rtk-ai/rtk), vtk inner filter: planned |
| Git (core) | status, log, diff, show, add, commit, push, pull, branch (+ passthrough for all other subcommands) | 59–80% | shipped — measured 47–89% on typical fixtures (status 84%, log 82%, diff 84%, show 85%, push-with-progress 89%; low end 2–11% on already-terse output, 100% on noise-only) |
| Git (rest) | fetch, stash, worktree | 59–80% | planned |
| GitHub | `gh pr view/checks`, `gh run list`, `gh issue list`, `gh api` | 26–87% | shipped (list shapes) — measured 33–48% on fixtures (`gh issue list` 33%, `gh pr list` 46%, `gh run list` 48%); compacts list-table output to `#<n> <state> <title> (<age>)` (runs: `<conclusion> <title> · <workflow>`), exit 0 only. `view`/`--json`/`api` shapes pass through structurally intact |
| npm/npx | `npm run`, `npx` | 70–90% | shipped (`npm run` dispatch, #8) — measured 44–58% on fixtures (eslint-delegated `npm run lint` 44%, banner-only strip on short script output 58%; savings compound with the inner filter's on large reports). Strips the npm banner, delegates the body to the inner tool's registered filter, gap-attributes uncovered inner tools to their own family (not npm); full raw (banner included) recoverable via `OK <id>`. Generic `npx` dispatch still planned |
| Files/search | `ls`, `grep`, `find` | 60–75% | shipped — measured 17–75% on fixtures (`ls` 140-entry dir 75%, `grep -l` 75%, `grep -rn` 34%, `find` walk 17% — savings scale with list size; real gap-log target `vtk ls serverjs` 74.6%). Column-packed rows capped at 40 entries with `(+N more)` tail, grep capped at 5 matches/file, full listing behind `OK <id>`; `ls -l` long format passes through raw; exit 0 only (grep's no-match exit 1 stays raw) |
| Files/search (rest) | `read`, `tree` | 60–75% | planned — both now shipped/validated upstream (rtk-ai/rtk) |
| Analysis | `err`, `log`, `json`, `env`, `summary`, `diff` | 70–90% | planned — `err`/`log`/`json`/`env` now shipped/validated upstream (rtk-ai/rtk); `summary`/`diff` planned |
| Docker/network | `docker ps/images/logs`, `curl` | 65–85% | planned — `docker ps/images/logs` + `curl` now shipped/validated upstream (rtk-ai/rtk) |
| Meta | `show`, `discover`-equivalent (`gaps`), `gain` | — | shipped — `gain` (#14) rolls up the invocation log into cumulative raw→emitted savings, overall and per family (ranked by bytes saved), excluding tty-bypass/legacy 0-byte rows so counts and % stay honest; read-only reporter, exit 0/1 like `gaps`. Dollarized rollups (#58): the summary gains an *approximate* $ line (bytes/4 token heuristic, checked-in per-model input price table updated by PR — no network), plus combinable `--daily` (per-UTC-day table with ~tokens/~USD), `--graph` (bar chart of daily saved bytes, last 30 logged days), `--history` (last 10 invocations); unknown flag → usage + exit 2 like `vtk show`. `gaps --file-issues` shipped ([#61](https://github.com/meridun/vtk/issues/61), porting #34): turns recurring gap families into `stage:intake` `Filter: <family>` issues via `gh` — dry-run by default (`--yes` to create), thresholds `--min-bytes`/`--min-calls` (defaults 50 KiB / 3 calls, clamped to floors 4096 B / 2), dedupes against open `Filter:` issues so re-runs never refile; issue bodies are metadata-only (family + call/byte counts). Explicit user-invoked `gh` shell-out per the #34 network ruling |
| Meta | `install` | — | shipped (#49) — wires the git/gh/npm→vtk wrappers into a shell rc/profile (bash `~/.bashrc`, pwsh `$PROFILE.CurrentUserAllHosts`). Self-locating via the running executable's path, marker-delimited managed block (idempotent, byte-identical re-runs, exact `--uninstall`), `$CLAUDECODE`-guarded so it is inert outside Claude Code; `--print`/`--dry-run`/`--shell bash\|pwsh` |
| Meta | `hooks` | — | shipped (#45, Claude-Code MVP) — self-installs the Claude Code `PreToolUse` rewrite hook against the running binary: `init` splices one marker-identified entry into `~/.claude/settings.json` (idempotent fixed point, exact `--uninstall`, `--print`/`--dry-run`/`--settings`, never overwrites unparseable JSON), `verify` integrity-checks the install (exit 1 on missing/duplicate/wrong-matcher/missing-binary/desync), `rewrite` is the hook payload — routes a plain top-level `git`/`gh`/`npm` Bash command through vtk via `hookSpecificOutput.updatedInput`, never emits `permissionDecision`, and passes through (empty output, exit 0) on pipes/redirects/chaining/substitution, non-Bash tools, already-wrapped commands, malformed input, and every internal error. GitHub Copilot CLI shipped (#83, `--copilot` on each subcommand): `init` writes a wholly-vtk-owned `vtk.json` into `~/.copilot/hooks` (`COPILOT_HOME` honored, `--hooks-dir` override) holding one version-1 `preToolUse` entry (matcher `bash\|powershell`), the file itself being the marker (idempotent installed/updated/unchanged, exact `--uninstall` deletes only that file); `verify --copilot` mirrors the Claude problems list; `rewrite --copilot` speaks the native `{toolName, toolArgs}` → `modifiedArgs` shape with the shared eligibility core plus a stricter powershell ban list (`(){}@`). Exit 0 on every path is load-bearing there — Copilot preToolUse is fail-closed, so a nonzero hook would deny the agent's tool call. Trust/permissions port deferred |
| Meta | `proxy` | — | planned — name reserved: invoking exits 2 with "not implemented yet" instead of exec fallthrough (#11) |
| rtk TOML long-tail | `gcc`, `make`, `terraform`, `docker`, `systemctl`, linters, installers (rtk's TOML-driven engine) | — | planned — the TOML engine itself shipped via #61 (cargo def first); the long-tail defs are tracked in #41 |
| rtk subsystems | `learn` (#43), `discover` (#44), `hooks` (#45), `analytics`/gain-economics (#46) | — | in-progress — upstream subsystems beyond filtering; vtk equivalents tracked in #43–#46. `learn` shipped via #43: mines Claude Code session JSONL for fail→succeed correction pairs (full rtk ErrorType set), dedupes into confidence-scored rules (`1 − 0.5^occurrences`), and writes `.claude/rules/cli-corrections.md` through the spool redaction pass; `--min-confidence`/`--min-occurrences`/`--sessions`/`--out`/`--dry-run`, exit 0/1/2 per the show/gaps convention. Ships the shared session provider (`Vtk.Core/Session/`) that #44/#46 reuse — the #46 per-session blocker is now cleared. The `hooks` Claude-Code MVP shipped via #45 and the GitHub Copilot CLI installer via #83 (see the Meta `hooks` row; trust/permissions deferred). The log-based half of gain-economics (dollarized `--daily`/`--graph`/`--history` rollups) shipped via #58 |

## vtk expansions (candidates — promote/demote based on gap-log data)

| Family | Commands | Rationale | Status |
|---|---|---|---|
| Language toolchains | `cargo`, `go`, `tsc`, `dotnet`, `mvn`/`gradle`, `pip`/`uv`, `pytest`, `jest`/`vitest` | rtk ships some of these; verify + fill gaps | in-progress — `cargo build/check/test/run/clippy` shipped ([#61](https://github.com/meridun/vtk/issues/61)) as the first declarative TOML def (`Defs/cargo.toml`): strips per-crate progress lines, keeps warnings/errors and the `Finished` summary. Measured 12–94% on fixtures (progress-heavy inline fixture 66%, 33-line real-run smoke 94%, warning-heavy 12% — savings scale with the progress:diagnostic ratio); exit `{0}` filtered, compile errors (exit 101) stay raw. Rest planned |
| Package managers | `pnpm`, `yarn`, `winget`, `choco`, `brew`, `apt` | install/upgrade output is huge and low-signal | planned |
| Kubernetes/cloud | `kubectl`, `helm`, `terraform plan/apply`, `az`, `aws`, `gcloud` | plan/apply and describe output are token bombs | planned |
| CI/CD | `gh run view --log`, `act` | log dumps dominate CI debugging sessions | planned |
| Databases | `psql`, `mysql`, `sqlite3`, `dbmate` | wide result sets, migration chatter | in-progress — `dbmate` shipped (#13): collapses applied-migration lines, keeps pending migrations + counts; rest planned |
| Windows-native | PowerShell cmdlet output shaping, `wsl`, `reg query` | rtk is unix-centric; first-class Windows support | planned |
| HTTP/API | `http`(ie), `grpcurl` | beyond rtk's `curl` | planned |
| Media/misc | `ffmpeg` (progress spam), `pandoc` | verbose stderr progress noise | planned |

## Fallback logging (`vtk gaps`)

Every passthrough writes a metadata-only entry tagged with a reason; `vtk gaps` counts only true
coverage gaps (`no-filter` — no registry match), aggregated by command family and sorted by total
raw bytes emitted — the top of that list is the next filter to write. Covered-but-unfiltered
invocations (tty-bypass, nonzero-exit, filter-panic, spool-fail) are excluded; panicking filters
surface separately in the DEGRADED section. This table
should be re-prioritized from real gap data once vtk is in daily use.
