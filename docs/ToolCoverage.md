# Tool Coverage

Filter catalog: rtk-parity targets first, then vtk expansions. Status values:
`planned` → `in-progress` → `shipped`. Savings % are rtk's published figures where they exist;
vtk figures get measured from fixtures once filters ship.

## rtk parity targets

| Family | Commands | rtk savings | Status |
|---|---|---|---|
| Tests | `playwright test`, generic `test <cmd>` wrapper | 90–94% | planned |
| Lint/format | `lint` (eslint), `prettier --check` | 70–84% | planned |
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

Every passthrough writes a metadata-only gap entry. `vtk gaps` aggregates by command family and
sorts by total raw bytes emitted — the top of that list is the next filter to write. This table
should be re-prioritized from real gap data once vtk is in daily use.
