# Tool Coverage

Filter catalog: rtk-parity targets first, then vtk expansions. Status values:
`planned` → `in-progress` → `shipped`. Savings % are rtk's published figures where they exist;
vtk figures get measured from fixtures once filters ship.

## rtk parity targets

| Family | Commands | rtk savings | Status |
|---|---|---|---|
| Tests | `playwright test`, generic `test <cmd>` wrapper | 90–94% | planned |
| Lint/format | `eslint`, `npx eslint` | 70–84% | shipped — measured 41–99% on fixtures (small multi-rule 41%, 500-problem report 99%); direct invocations only, exit {0,1} filtered / 2+ raw |
| Lint/format (rest) | `prettier --check`, `npm run lint` wrapped path | 70–84% | in-progress — `npm run` dispatch layer shipped (#8): banner strip + inner-tool delegation; `prettier` inner filter still planned |
| Git (core) | status, log, diff, show, add, commit, push, pull, branch (+ passthrough for all other subcommands) | 59–80% | shipped — measured 47–89% on typical fixtures (status 84%, log 82%, diff 84%, show 85%, push-with-progress 89%; low end 2–11% on already-terse output, 100% on noise-only) |
| Git (rest) | fetch, stash, worktree | 59–80% | planned |
| GitHub | `gh pr view/checks`, `gh run list`, `gh issue list`, `gh api` | 26–87% | shipped (list shapes) — measured 33–48% on fixtures (`gh issue list` 33%, `gh pr list` 46%, `gh run list` 48%); compacts list-table output to `#<n> <state> <title> (<age>)` (runs: `<conclusion> <title> · <workflow>`), exit 0 only. `view`/`--json`/`api` shapes pass through structurally intact |
| npm/npx | `npm run`, `npx` | 70–90% | shipped (`npm run` dispatch, #8) — measured 44–58% on fixtures (eslint-delegated `npm run lint` 44%, banner-only strip on short script output 58%; savings compound with the inner filter's on large reports). Strips the npm banner, delegates the body to the inner tool's registered filter, gap-attributes uncovered inner tools to their own family (not npm); full raw (banner included) recoverable via `OK <id>`. Generic `npx` dispatch still planned |
| Files/search | `ls`, `grep`, `find` | 60–75% | shipped — measured 17–75% on fixtures (`ls` 140-entry dir 75%, `grep -l` 75%, `grep -rn` 34%, `find` walk 17% — savings scale with list size; real gap-log target `vtk ls serverjs` 74.6%). Column-packed rows capped at 40 entries with `(+N more)` tail, grep capped at 5 matches/file, full listing behind `OK <id>`; `ls -l` long format passes through raw; exit 0 only (grep's no-match exit 1 stays raw) |
| Files/search (rest) | `read` | 60–75% | planned |
| Analysis | `err`, `log`, `json`, `env`, `summary`, `diff` | 70–90% | planned |
| Docker/network | `docker ps/images/logs`, `curl` | 65–85% | planned |
| Meta | `show`, `discover`-equivalent (`gaps`), `gain` | — | shipped — `gain` (#14) rolls up the invocation log into cumulative raw→emitted savings, overall and per family (ranked by bytes saved), excluding tty-bypass/legacy 0-byte rows so counts and % stay honest; read-only reporter, exit 0/1 like `gaps` |
| Meta | `proxy` | — | planned — name reserved: invoking exits 2 with "not implemented yet" instead of exec fallthrough (#11) |

## vtk expansions (candidates — promote/demote based on gap-log data)

| Family | Commands | Rationale | Status |
|---|---|---|---|
| Language toolchains | `cargo`, `go`, `tsc`, `dotnet`, `mvn`/`gradle`, `pip`/`uv`, `pytest`, `jest`/`vitest` | rtk ships some of these; verify + fill gaps | planned |
| Package managers | `pnpm`, `yarn`, `winget`, `choco`, `brew`, `apt` | install/upgrade output is huge and low-signal | planned |
| Kubernetes/cloud | `kubectl`, `helm`, `terraform plan/apply`, `az`, `aws`, `gcloud` | plan/apply and describe output are token bombs | planned |
| CI/CD | `gh run view --log`, `act` | log dumps dominate CI debugging sessions | planned |
| Databases | `psql`, `mysql`, `sqlite3`, `dbmate` | wide result sets, migration chatter | planned |
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
