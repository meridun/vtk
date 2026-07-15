# vtk — V Token Killer

A command-output compaction wrapper for AI coding agents. Prefix any shell command with `vtk`
and it filters the output down to what the agent actually needs — failures, diffs, deltas —
cutting token usage by 60–90% on common development operations.

`vtk` is a workalike of [rtk](https://github.com/rtk-ai/rtk)
with two headline additions:

1. **Expanded tool coverage** — filters for more tools beyond rtk's catalog (see
   [docs/ToolCoverage.md](docs/ToolCoverage.md) for the target matrix).
2. **Fallback logging** — when a command has no dedicated filter and passes through unfiltered,
   vtk logs the invocation (command, output size, timestamp) so missed-savings opportunities are
   measurable and drive filter prioritization, instead of passing through silently.
3. **Output spool** — filtered commands print a short retrieval ID (`OK 2e3f`); the full raw
   output is briefly kept on disk and retrievable via `vtk show 2e3f` — no rerun needed, which
   matters for non-idempotent commands like `git commit`.

## Status

Early implementation, written in C# (.NET 9) under `dotnet/`. Shipped so far:

- **git filter family** — `status`, `log`, `diff`, `show`, `add`, `commit`, `push`, `pull`,
  `branch` (other subcommands pass through). Measured savings 47–89% on typical fixtures.
- **eslint filter** — `eslint`, `npx eslint` (direct invocations): problems rolled up by rule id,
  top example per rule, `✖ N problems` summary preserved. Measured 41–99% on fixtures. Report-style
  exits are filtered via a per-filter exit-code allowlist — eslint filters exit `{0, 1}` ("problems
  found" is a report, not a failure); exit `2`+ (fatal/config) stays raw. The child's exit code is
  always returned unchanged. The `npm run lint` wrapped form is covered via the npm run
  dispatch layer below.
- **npm run dispatch** — `npm run <script>`: strips the two-line npm banner
  (`> pkg@ver script` + expanded command line), detects the inner tool from the expanded line,
  and delegates the remaining output to that tool's filter (`npm run lint` → eslint). When the
  inner tool has no filter, the banner-stripped body passes through and the gap is attributed
  to the inner tool's family — `vtk gaps` points at the real tool, not npm. The full raw
  output, banner included, stays recoverable via `vtk show`. Measured 44–58% on fixtures;
  savings compound with the inner filter's on large reports.
- **mocha filter** — `mocha`, `npx mocha` (spec reporter): folds passing/pending/suite spec-tree
  lines away and keeps the summary (`N passing`/`M failing`/`K pending`) plus every failure-detail
  block (name + assertion + stack) verbatim — the signal an agent needs. Measured 23–94% on
  fixtures (savings scale with the pass:fail ratio: a green run collapses to a single summary line,
  a failure-heavy run keeps most of its bytes). Filtered exit `{0, 1}` (`1` = test failures, a
  report not a crash); exit `2`+ (config error) stays raw, with the child's exit code always
  returned unchanged. The `npm run test` wrapped form is covered via the npm run dispatch layer;
  full raw is recoverable via `vtk show`.
- **gh filter family** — `gh issue list`, `gh pr list`, `gh run list`: table output compacts to
  one line per row (`#<n> <state> <title> (<age>)`; runs show `<conclusion> <title> · <workflow>`),
  labels/branch/runID noise dropped. Measured savings 33–48% on fixtures. Filtered exit `0` only;
  `view` shapes and any `--json` output pass through structurally intact.
- **files/search filter family** — `ls`, `grep`, `find`: list output column-packed and capped at
  40 entries with a `(+N more)` tail; `grep` match lines capped at 5 per file with per-file
  `(+N more)` tails. The full listing is always recoverable via `vtk show <id>`. Measured savings
  17–75% on fixtures (scales with list size). `ls -l` long format passes through raw; filtered
  exit `0` only, so grep's no-match exit `1` stays raw with exit-code parity intact.
- **dbmate filter** — `dbmate up/status/rollback` (#13): collapses the applied-migration spam,
  keeps pending migrations and counts.
- **Output spool + `vtk show <id>`** — filtered output is spooled (~1h TTL, credential
  redaction); `vtk show <id>` retrieves it, `--grep <pat>` returns matching lines only.
- **Gap logging + `vtk gaps`** — every unfiltered passthrough is logged (metadata only) with a
  reason (`no-filter`, `tty-bypass`, `nonzero-exit`, `filter-panic`, `spool-fail`); `vtk gaps`
  reports only true coverage gaps (`no-filter`), aggregated by command family and sorted by raw
  bytes, plus a DEGRADED section when a filter panicked and degraded to raw passthrough.

- **Cumulative savings + `vtk gain`** — aggregates the invocation log into total raw vs emitted
  bytes, bytes saved and savings %, overall and per command family (ranked by bytes saved).
  tty-bypass and other 0-byte rows are excluded so the percentage is honest. The summary includes
  an *approximate* dollarized line (bytes/4 token heuristic, checked-in per-model input prices
  updated by PR — no network calls), and optional rollups: `--daily` (per-UTC-day table),
  `--graph` (bar chart of daily saved bytes over the last 30 logged days), `--history` (the last
  10 invocations with per-command savings).
- **Shell integration + `vtk install`** — splices a self-locating, `$CLAUDECODE`-guarded wrapper
  block into `~/.bashrc` and the pwsh profile so `git`/`gh`/`npm` route through vtk without being
  prefixed. Marker-delimited and idempotent, with `--print`/`--dry-run`/`--uninstall`/`--shell`.

Planned next ([#61](https://github.com/meridun/vtk/issues/61)): a **declarative TOML filter
engine** (~20-line regex specs as the cheap path for new filter families, starting with cargo)
and **`vtk gaps --file-issues`** (turn recurring gap families into `stage:intake` filter issues
via `gh`).

Further filter families are not yet implemented. The meta word `proxy` is reserved: invoking it
prints `vtk: "proxy" is not implemented yet` and exits `2` instead of falling through to exec — so
it fails clearly rather than with a misleading "executable not found", and a real executable
named `proxy` cannot be run through vtk. Only this documented meta word is intercepted; any other
unknown word still execs as usual.
Design decisions are recorded one line each in
[docs/Architecture.md](docs/Architecture.md#decision-registry) with links to the debate issues.

## Usage

```
vtk git status          # compact status; prints "OK <id>" when filtering clears the savings bar
vtk show <id>           # full captured output (provenance header first)
vtk show <id> --grep x  # only matching lines
vtk gaps                # uncovered-command families ranked by raw bytes (+ degraded filters)
vtk gain                # cumulative savings: raw vs emitted bytes, overall and per family,
                        # plus an approximate dollarized total (bytes/4 heuristic)
vtk gain --daily        # per-UTC-day savings table (~tokens, ~USD per day)
vtk gain --graph        # bar chart of daily saved bytes, last 30 logged days
vtk gain --history      # last 10 invocations with per-command savings (flags combine)
vtk install             # wire git/gh/npm -> vtk into your shell rc/profile (bash + pwsh)
vtk install --print     # print the wrapper block(s) without writing anything
vtk install --uninstall # remove the managed block
```

### Shell integration (`vtk install`)

`vtk <cmd>` filters when you prefix it, but to filter *every* `git`/`gh`/`npm` call without
prefixing, `vtk install` splices wrapper functions into your shell startup file — `~/.bashrc`
for bash, `$PROFILE.CurrentUserAllHosts` for PowerShell (resolved via pwsh, so OneDrive
Documents redirection is handled). The block:

- **points at this binary** (self-locating via the running executable's path), so it keeps
  working wherever vtk lives;
- is **guarded on `$CLAUDECODE`**, so it is inert in normal interactive shells and only wraps
  commands inside Claude Code sessions;
- is **marker-delimited and idempotent** — re-running with the same binary path is a no-op, a
  moved binary updates in place, and `--uninstall` removes exactly the managed block.

`--shell bash|pwsh` targets one shell; `--dry-run` reports actions without writing.

## Install

From source, with the .NET 9 SDK:

```
dotnet publish dotnet/Vtk.Cli -c Release -o <install-dir>
```

The published `vtk` executable is self-contained within `<install-dir>`; put it on `PATH` (or
let `vtk install` reference it in place). Note for Windows: the spool directory relies on the default
user-scoped ACLs of `%LocalAppData%` (POSIX 0700 permissions are a no-op on NTFS).

## Core behavior (design contract)

- **Always safe**: `vtk <cmd>` never changes the command's semantics or exit code. If no filter
  matches, output passes through unchanged (and the fallback is logged).
- **Chain-friendly**: works per-command inside `&&` chains.
- **Measurable**: `vtk gain` reports cumulative token savings; fallback logs
  (`vtk gaps`) report the gap.
- **Compact means folded**: on the filtered success path, stdout and stderr are folded into a
  single compact result on stdout. Per-stream separation is preserved on all raw, passthrough,
  and degraded paths.
- **Codepage-proof**: when vtk's stdout/stderr are redirected (the agent-capture path), output
  is written as BOM-less UTF-8 regardless of the console codepage, so Unicode in wrapped-tool
  output (`✔`, `—`, `ü`) survives byte-faithfully even under Windows legacy codepages. Raw
  passthrough copies the child's bytes stream-to-stream, untouched.

## Documentation

- [docs/Overview.md](docs/Overview.md) — goals, scope, comparison to rtk
- [docs/Architecture.md](docs/Architecture.md) — filter pipeline design
- [docs/ToolCoverage.md](docs/ToolCoverage.md) — filter catalog: parity targets + expansions

## License

[MIT](LICENSE)
