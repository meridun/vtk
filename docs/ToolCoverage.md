# Tool Coverage

Filter catalog: rtk-parity targets first, then vtk expansions. Status values:
`planned` → `in-progress` → `shipped`. Savings % are rtk's published figures where they exist;
vtk figures get measured from fixtures once filters ship.

## rtk parity targets

| Family | Commands | rtk savings | Status |
|---|---|---|---|
| Tests | `playwright test`, generic `test <cmd>` wrapper | 90–94% | planned |
| Lint/format | `eslint`, `npx eslint` | 70–84% | shipped — measured 41–99% on fixtures (small multi-rule 41%, 500-problem report 99%); direct invocations only, exit {0,1} filtered / 2+ raw |
| Lint/format (rest) | `prettier --check`, `npm run lint` wrapped path | 70–84% | planned — wrapped path is #8 |
| Git (core) | status, log, diff, show, add, commit, push, pull, branch (+ passthrough for all other subcommands) | 59–80% | shipped — measured 47–89% on typical fixtures (status 84%, log 82%, diff 84%, show 85%, push-with-progress 89%; low end 2–11% on already-terse output, 100% on noise-only) |
| Git (rest) | fetch, stash, worktree | 59–80% | planned |
| GitHub | `gh pr view/checks`, `gh run list`, `gh issue list`, `gh api` | 26–87% | planned |
| npm/npx | `npm run`, `npx` | 70–90% | planned |
| Files/search | `ls`, `read`, `grep`, `find` | 60–75% | planned |
| Analysis | `err`, `log`, `json`, `env`, `summary`, `diff` | 70–90% | planned |
| Docker/network | `docker ps/images/logs`, `curl` | 65–85% | planned |
| Meta | `show`, `discover`-equivalent (`gaps`) | — | shipped |
| Meta | `gain`, `proxy` | — | planned |

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
