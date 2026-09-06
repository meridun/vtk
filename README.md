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
  Global options ahead of the subcommand (`-C <dir>`, `-c <k=v>`, `--no-pager`/`-P`,
  `-p`/`--paginate`, `--git-dir[=<path>]`, `--work-tree[=<path>]`) still engage the filter for
  `status`, `branch`, `add`, `commit`, `push`, `pull`; `diff`, `show`, `log` behind a global
  option pass through unfiltered (#152). Hunk-shaped `git diff` / `git show` output below 64 KiB
  (the shared fold floor) passes through byte-identical — agents run these to read the hunks, so
  folding them only cost a `vtk show` round trip; at or above the floor the per-file stats shape
  plus an `OK <id>` recovery line still applies (#135). `--stat` / `--numstat` and `show -s`
  shapes are unchanged.
- **eslint filter** — `eslint`, `npx eslint` (direct invocations): problems rolled up by rule id,
  top example per rule, `✖ N problems` summary preserved. Measured 41–99% on fixtures. Report-style
  exits are filtered via a per-filter exit-code allowlist — eslint filters exit `{0, 1}` ("problems
  found" is a report, not a failure); exit `2`+ (fatal/config) stays raw. The child's exit code is
  always returned unchanged. The `npm run lint` wrapped form is covered via the npm run
  dispatch layer below.
- **npm run dispatch** — `npm run <script>` and the lifecycle aliases `npm test`/`t`/`tst`/
  `start`/`stop`/`restart` (#120): strips the two-line npm banner
  (`> pkg@ver script` + expanded command line), detects the inner tool from the expanded line,
  and delegates the remaining output to that tool's filter (`npm run lint` → eslint). When the
  inner tool has no filter, a size-floored fold applies (#93): successful (exit 0) output of
  64 KiB or more collapses to a short summary tail (last few lines) plus an `OK <id>` recovery
  line — including the no-banner shape modern npm emits under pipe capture, where the inner
  tool can't be detected at all. The tail also pins up to 3 runner summary lines that fit
  (mocha `N passing`/`failing`/`pending`, jest `Tests: … passed`/`failed`, eslint
  `N problems`) from above the last lines, nearest first, inside the same 512-byte cap
  (#134) — so a `1200 passing` line followed by leftover console noise survives the fold
  instead of needing a `vtk show` round trip. Below the floor, output passes through byte-identical (so
  terse, load-bearing scripts like `npm run sdlc` are never touched), and failures are never
  folded — nonzero exits keep their full output inline. Unfolded gaps are attributed to the
  inner tool's family when the banner reveals it — `vtk gaps` points at the real tool, not
  npm. When the inner filter exists and ran but had nothing to elide (a mocha crash before
  any spec, say), the invocation is logged as filtered under that filter's name — the same
  attribution the direct path uses (#153) — so `vtk gaps` never proposes a filter that already
  shipped. The full raw output, banner included, stays recoverable via `vtk show`. Measured 44–58%
  on banner-strip/delegation fixtures and ≈99.8% on a 122 KB folded run; savings compound with
  the inner filter's on large reports. Aliases behave byte-identically to their `npm run`
  spellings; off-TTY `npm start` buffers output until exit (same as `npm run start`), and on a
  real terminal all npm calls still pass through interactively.
- **powershell -File fold** — `powershell`/`pwsh -ExecutionPolicy ... -File <script>` (#131)
  reuses the same size-floored fold: successful (exit 0) script output of 64 KiB or more (a
  shared floor constant with the npm fold) collapses to a short summary tail (same shape as the
  npm fold, including the pinned runner summary lines, #134) plus an `OK <id>`
  recovery line, with the full raw output recoverable via `vtk show`. Below the floor, output
  passes through byte-identical; failures are never folded — a red test run keeps its full
  output inline with the script's exit code returned unchanged. Both spellings (± `.exe`, any
  case) are covered; `-Command`/`-EncodedCommand` invocations bypass the fold and pass through
  gap-logged. Measured ≈99.8% on a 146 KB real-run fold.
- **launcher-prefix unwrap** (#97) — filter matching sees through transparent launcher prefixes:
  `cross-env VAR=x <cmd>`, `npx <cmd>` (with `--yes`/`-y`/`--no-install`), and bare leading
  `VAR=x` tokens, stacked in any combination, unwrap to the inner command before filter lookup —
  so `cross-env INTEGRATION=1 mocha ...` (invoked directly or expanded inside an `npm run`
  script) engages the mocha filter instead of passing through unfiltered. Gap/gain telemetry is
  attributed to the inner tool too, so `cross-env` never shows up as its own family. The executed
  command line is never altered, and non-transparent shapes (`npx -p pkg <cmd>`) still pass
  through unchanged, gap-logged.
- **mocha filter** — `mocha`, `npx mocha` (spec reporter): folds passing/pending/suite spec-tree
  lines away and keeps the summary (`N passing`/`M failing`/`K pending`) plus every failure-detail
  block (name + assertion + stack) verbatim — the signal an agent needs. Measured 23–94% on
  fixtures (savings scale with the pass:fail ratio: a green run collapses to a single summary line,
  a failure-heavy run keeps most of its bytes). Filtered on any exit `0`–`255`: mocha exits with
  its failure count (`min(failures, 255)`), so a multi-failure run is a report, not a crash;
  fatal/config errors (no `N passing`/`failing`/`pending` summary line) pass through raw on
  content, with the child's exit code always returned unchanged. The `npm run test` wrapped form
  is covered via the npm run dispatch layer; full raw is recoverable via `vtk show`.
- **gh filter family** — `gh issue list`, `gh pr list`, `gh run list`: table output compacts to
  one line per row (`#<n> <state> <title> (<age>)`; runs show `<conclusion> <title> · <workflow>`),
  labels/branch/runID noise dropped. Measured savings 33–48% on fixtures.
  `gh run view --log` / `--log-failed` fold CI job logs (#96): passing job sections collapse to
  `OK <job> (n lines)` with `##[warning]` lines kept, failing sections (`##[error]` / nonzero
  step exit) stay inline with job/step/timestamp prefixes, and a failure-scoped log with no
  explicit marker keeps its last 20 lines so the evidence survives. Measured 56–91% on fixtures;
  full raw always recoverable via `vtk show`. `gh run` filters exit `{0,1}` — a failed run's log
  is a report, not a crash, and non-log error output passes through raw via shape guards; other
  gh commands filter exit `0` only. `view` summary shapes and any `--json` output pass through
  structurally intact.
- **files/search filter family** — `ls`, `grep`, `find`: list output column-packed and capped at
  40 entries with a `(+N more)` tail; `grep` match lines capped at 5 per file with per-file
  `(+N more)` tails. The full listing is always recoverable via `vtk show <id>`. Measured savings
  17–75% on fixtures (scales with list size). `ls -l` long format passes through raw; filtered
  exit `0` only, so grep's no-match exit `1` stays raw with exit-code parity intact.
- **dbmate filter** — `dbmate up/status/rollback` (#13): collapses the applied-migration spam,
  keeps pending migrations and counts.
- **cargo filter + declarative TOML engine** — `cargo build/check/test/run/clippy` (#61): strips
  per-crate progress chatter (`Compiling`/`Checking`/`Downloading` ...), keeps warnings, errors,
  and the `Finished` summary. Measured 12–94% on fixtures (savings scale with progress chatter);
  compile errors (exit 101) pass through raw. First filter shipped as data, not code: a ~20-line
  TOML spec (`dotnet/Vtk.Core/Filter/Toml/Defs/cargo.toml`) with inline fixtures the test suite
  replays, embedded at build and matched by regex only when no hand-written filter claims the
  command — the cheap path for new regex-shaped filter families (authoring guide in
  `Defs/README.md`).
- **winget filter** — `winget install/upgrade/uninstall/download` (#105, TOML def): strips the
  license-agreement boilerplate, `Downloading`/hash-verification/`Starting package ...` chatter,
  and progress-bar/spinner frames; keeps `Found <pkg> Version <v>`, result/alias/PATH lines, and
  upgrade tables (a listing passes through unchanged). Measured 64% on a real install capture;
  failed installs (non-zero exit) pass through raw with exit-code parity intact.
- **choco filter** — `choco install/upgrade/uninstall/outdated` (#106, TOML def): strips the
  `Chocolatey v...` banner, `Progress:` redraw frames, `Downloading`/hash-verification chatter,
  and license-acceptance boilerplate; keeps package results, versions, warnings, errors, and
  summaries. Read-only verbs (`search`, `list`) never engage the filter. Measured 10–19% on
  real-capture fixtures; failed installs (non-zero exit) pass through raw with exit-code parity
  intact.
- **long-tail TOML strip defs** — `gcc`/`g++`, `make`, `terraform plan`, `shellcheck`,
  `yamllint`, `hadolint` (#41): six pure line-strip defs ported from rtk. gcc drops
  include-chain preambles and spacer lines, keeping every diagnostic (a clean compile prints
  `gcc: ok`); make drops recursive-make `Entering/Leaving directory` chatter, including the
  Windows `make.EXE` argv[0] shape; terraform plan drops state-refresh and lock chatter,
  keeping the action list and `Plan:` summary; the three linters drop blank spacer lines with
  a length cap. Measured 13% (gcc real capture), 12–67% (make golden capture / recursive
  real run), ≈60% (terraform plan fixture); linter savings are small by design. Compile,
  recipe, and plan failures (non-zero exit) pass through raw; shellcheck/hadolint exit `1`
  and yamllint exits `1`–`2` are findings reports and stay filtered — exit-code parity intact
  throughout.
- **dotnet filter** — `dotnet build`/`dotnet test` (#121, TOML def): strips restore chatter,
  per-project compile lines, and the VSTest banner; keeps compiler/analyzer warnings verbatim,
  the `Build succeeded.` counts + elapsed summary, and the `Passed!`/`Failed!` test summary
  line. Measured 5–85% on fixtures (green test 85%, clean build 69%, warning-heavy build 5%);
  red builds and failing tests (non-zero exit) pass through raw with exit-code parity intact.
- **Output spool + `vtk show <id>`** — filtered output is spooled (~1h TTL, credential
  redaction), keyed per (directory, command) so sibling worktrees never clobber each other;
  `vtk show <id>` retrieves it, `--grep <pat>` returns matching lines only, and a one-line
  stderr warning flags a read from a directory other than the one that wrote it.
- **Gap logging + `vtk gaps`** — every unfiltered passthrough is logged (metadata only) with a
  reason (`no-filter`, `tty-bypass`, `nonzero-exit`, `filter-panic`, `spool-fail`,
  `spawn-fail`), and filtered
  invocations record the engaged filter's registry name (e.g. `gh run`) as theirs, so the
  invocation log names which filter handled each call; `vtk gaps`
  reports only true coverage gaps (`no-filter`), aggregated by command family and sorted by raw
  bytes, plus a DEGRADED section when a filter panicked and degraded to raw passthrough.
  Families are keyed `argv[0] argv[1]` for commands with a registered pair key (`git`, `gh`,
  `npx`, `dbmate`; known git global options such as `-C <dir>` / `--no-pager` are normalized
  first) and `argv[0]` otherwise, so a multiplexer's row names the missing subcommand
  (`git rev-parse`, `gh api`) rather than one umbrella `git` (#139); `vtk gain` keeps the
  `argv[0]` grain.
  A child that never started (unresolvable command, self-flag typo) still exits 127 and is
  still logged, but under `spawn-fail` — it never ranks a gap family.
  `vtk gaps --file-issues` (#61) turns recurring gap families into `stage:intake` filter issues
  via `gh`: dry-run by default (`--yes` to create), `--min-bytes`/`--min-calls` thresholds
  (defaults 50 KiB / 3 calls, floors 4096 B / 2), and dedupe against open `Filter:` issues so
  re-running never refiles. Issue bodies carry family + call/byte counts only — never output
  content; for pair-keyed commands the title carries the subcommand (`Filter: git rev-parse`),
  and a family that already resolves in the registry prints as a `dispatch gap` line instead of
  a proposed filter (#139). `--since <N>d|<N>h|<ISO-8601 date>` (#140) windows the report (and the
  `--file-issues` thresholds) to invocations logged at or after the bound (UTC); opt-in —
  without it the output is unchanged. A `window: since <utc> (<spec>)` header line is printed
  only when the flag is given; invalid or out-of-range specs exit 2 with usage.

- **Cumulative savings + `vtk gain`** — aggregates the invocation log into total raw vs emitted
  bytes, bytes saved and savings %, overall and per command family (ranked by bytes saved).
  tty-bypass and other 0-byte rows are excluded so the percentage is honest. The summary includes
  an *approximate* dollarized line (bytes/4 token heuristic, checked-in per-model input prices
  updated by PR — no network calls), and optional rollups: `--daily` (per-UTC-day table),
  `--graph` (bar chart of daily saved bytes over the last 30 logged days), `--history` (the last
  10 invocations with per-command savings), `--session` (savings per Claude Code session:
  attributes logged invocations to session transcript time windows and renders the 10 most
  recent sessions with calls, saved bytes/%, ~tokens, ~USD; `--sessions <dir>` overrides the
  transcript directory). The session view reads only top-level transcript *timestamps* — never
  transcript content.
- **Shell integration + `vtk install`** — splices a self-locating, `$CLAUDECODE`-guarded wrapper
  block into `~/.bashrc` and the pwsh profile so `git`/`gh`/`npm`/`winget`/`choco`/`reg` route
  through vtk without being prefixed. Marker-delimited and idempotent, with
  `--print`/`--dry-run`/`--uninstall`/`--shell`.
- **Session mining + `vtk learn`** — mines Claude Code session transcripts (JSONL) for
  commands that failed and were then corrected (same base command, error output first, clean
  run within the lookahead window), classifies the error (`UnknownFlag`, `CommandNotFound`,
  `WrongSyntax`, `WrongPath`, `MissingArg`, `PermissionDenied`, `Other`), dedupes pairs into
  confidence-scored rules, and writes `.claude/rules/cli-corrections.md` so the agent stops
  repeating the mistake. Read-only analysis over local transcripts — no process spawning, no
  network; commands pass the spool credential-redaction pass before landing in the rules file.
  `--min-confidence`/`--min-occurrences` thresholds, `--sessions`/`--out` overrides, `--dry-run`
  prints instead of writing. First consumer of the shared session provider that `discover`
  (#44) and per-session gain (`vtk gain --session`, #46) reuse.
- **Missed-optimization report + `vtk discover`** — rule-based analysis pass over the same
  session transcripts (#44), layered on `gaps`: where `gaps` counts raw bytes at execution
  time, `discover` reasons post-hoc about specific command shapes. Each mined command segment
  (compound commands split, launcher prefixes unwrapped, `vtk`-wrapped and failed invocations
  skipped) is classified **unwrapped** — a shipped vtk filter covers the shape, run it via vtk
  to bank the savings — or **candidate** — it matches a seeded rule for a known-compressible
  shape (`dotnet build/test`, `go build/test`, `npm install/ci`, `pip install`, `docker build`,
  `terraform plan/apply`, `make`, `winget`/`choco install`) with no filter yet. Coverage is
  probed against the live filter registry before rules, so a shape stops reporting as a
  candidate the moment its filter ships. Segments that actually routed through vtk via
  transcript-invisible shell-function wrappers are subtracted by cross-referencing the
  invocation log (match = redacted argv + ±5-minute window, consumed one-to-one), with the
  excluded count reported in the summary; when the log is unavailable the report is simply
  produced without the cross-reference. Ranked by observed output volume; `--sessions <dir>`
  and `--top <N>` flags; `--since <N>d|<N>h|<ISO-8601 date>` (#140) skips session files not
  modified since the bound and drops events stamped before it (undated events dropped and
  counted), so history predating shipped fixes stops dominating the report — the window is
  appended to the summary line, and bare `discover` is unchanged. Read-only like `learn`: no
  process spawning, no network, no disk writes — the report goes to stdout only.
- **Agent hooks + `vtk hooks`** — installs a pre-tool-call rewrite hook that routes plain
  `git`/`gh`/`npm`/`winget`/`choco`/`reg` — plus, for bash tool calls, `grep`/`ls`/`find` —
  shell tool calls through vtk at the agent's
  tool-call layer, with an
  integrity-verifiable install: `init` writes the managed hook config (Claude Code
  `~/.claude/settings.json` by default; GitHub Copilot CLI `~/.copilot/hooks/vtk.json` with
  `--copilot`), `verify` fails loudly on a missing, duplicated, or desynced install, and
  `rewrite` is the hook payload itself. Anything it can't safely wrap passes through untouched.
- **Version stamp + `vtk version`** — prints the running build's commit as `vtk <sha>`; both
  `vtk version` and `vtk --version` are reserved ahead of exec-passthrough. Deployed builds
  print the short SHA stamped at publish; unstamped source builds print the full SourceLink
  SHA; a versioned-deploy binary without either falls back to its release-dir name.

Further filter families are not yet implemented. The meta word `proxy` is reserved: invoking it
prints `vtk: "proxy" is not implemented yet` and exits `2` instead of falling through to exec — so
it fails clearly rather than with a misleading "executable not found", and a real executable
named `proxy` cannot be run through vtk. `help`, `--help`, and `-h` are likewise reserved: they
print the usage line to stdout and exit `0` instead of spawning as external commands (bare `vtk`
still prints usage to stderr and exits `2`). Only these documented meta words are intercepted; any
other unknown word still execs as usual.
Design decisions are recorded one line each in
[docs/Architecture.md](docs/Architecture.md#decision-registry) with links to the debate issues.

## Usage

```
vtk git status          # compact status; prints "OK <id>" when filtering clears the savings bar
vtk show <id>           # full captured output (provenance header first)
vtk show <id> --grep x  # only matching lines
vtk gaps                # uncovered-command families ranked by raw bytes (+ degraded filters)
vtk gaps --since 14d    # same report over a time window (<N>d, <N>h, or ISO-8601 date; UTC)
vtk gaps --file-issues  # file recurring gap families as intake issues (dry-run; --yes to create)
vtk gain                # cumulative savings: raw vs emitted bytes, overall and per family,
                        # plus an approximate dollarized total (bytes/4 heuristic)
vtk gain --daily        # per-UTC-day savings table (~tokens, ~USD per day)
vtk gain --graph        # bar chart of daily saved bytes, last 30 logged days
vtk gain --history      # last 10 invocations with per-command savings (flags combine)
vtk gain --session      # savings per Claude Code session (timestamps only; --sessions <dir>)
vtk learn               # mine session JSONL for fail->succeed corrections ->
                        # .claude/rules/cli-corrections.md
vtk learn --dry-run     # print the rules file without writing it
vtk discover            # ranked missed-optimization report from session history
                        # (unwrapped = filter exists, use vtk; candidate = filter evidence)
vtk discover --top 5    # limit the table to the top 5 opportunities
vtk discover --since 14d # only sessions/events in the window (same forms as gaps --since)
vtk install             # wire git/gh/npm/winget/choco/reg -> vtk into your shell
                        # rc/profile (bash + pwsh)
vtk install --print     # print the wrapper block(s) without writing anything
vtk install --uninstall # remove the managed block
vtk hooks init          # install the Claude Code PreToolUse rewrite hook (~/.claude/settings.json)
vtk hooks verify        # integrity-check the installed hook; exit 1 on missing/desync
vtk hooks init --uninstall  # remove exactly the managed hook entry
vtk hooks init --copilot    # install the GitHub Copilot CLI preToolUse hook (~/.copilot/hooks/vtk.json)
vtk hooks verify --copilot  # integrity-check the Copilot hook file; exit 1 on missing/desync
vtk version             # running build's commit: "vtk <sha>" (vtk --version also works)
vtk help                # usage line, exit 0 (vtk --help / vtk -h also work)
```

### Shell integration (`vtk install`)

`vtk <cmd>` filters when you prefix it, but to route *every* call in the intercepted families
(`git`, `gh`, `npm`, `winget`, `choco`, `reg`) through vtk without
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

### Agent hooks (`vtk hooks`)

Where `vtk install` wraps commands at the shell layer, `vtk hooks` does it at the agent's
tool-call layer with an integrity-verifiable install. It supports two hosts: Claude Code
(default) and GitHub Copilot CLI (`--copilot` on each subcommand).

`vtk hooks init` splices a single managed
`PreToolUse` entry (matcher `Bash`) into `~/.claude/settings.json`, pointing at this binary
(self-locating, like `vtk install`). Re-runs are idempotent (byte-identical fixed point), a
moved binary updates the entry in place, `--uninstall` removes exactly the managed entry, and
everything else in the file is preserved — an unparseable settings file is never overwritten.
`vtk hooks verify` exits nonzero when the hook is missing, duplicated, mis-matched, or points at
a binary that is absent or is not the one running the check — the desync class of bug a
hand-wired setup can't detect.

The installed hook runs `vtk hooks rewrite` on each Bash tool call: a plain, top-level
command in an intercepted family (`git`/`gh`/`npm`/`winget`/`choco`/`reg`, plus
`grep`/`ls`/`find` — bash-only families the shell wrappers deliberately skip, since a shell
function wraps mid-pipeline calls too) is rewritten (via
`hookSpecificOutput.updatedInput`) to run through
vtk. Everything else — pipes, redirects, chained or substituted commands, non-Bash tools,
already-wrapped commands, malformed input, any internal error — produces no output and exit `0`,
so the agent's command runs exactly as typed and compacted output never lands inside a pipeline.
Wrapped `grep` output spools like any other wrapped family — full results (redacted) land in the
capture spool, retrievable via `vtk show <id>`.
The hook never emits a `permissionDecision`: it can update a tool call's input but cannot
approve or block it — the normal permission flow still applies.

`--print` emits the hook command without touching any file; `--dry-run` reports the action
without writing; `--settings <path>` targets a non-default settings file.

#### GitHub Copilot CLI (`--copilot`)

`vtk hooks init --copilot` writes a wholly-vtk-owned hook file `vtk.json` into the Copilot CLI
user hooks directory (`~/.copilot/hooks`, or `$COPILOT_HOME/hooks` when `COPILOT_HOME` is set;
`--hooks-dir <path>` overrides). The file holds one version-1 `preToolUse` command hook
(matcher `bash|powershell`) with shell-appropriate commands for both tools; the file itself is
the marker, so re-runs are idempotent (`installed`/`updated`/`unchanged`) and `--uninstall`
deletes exactly that file. `vtk hooks verify --copilot` checks the same desync classes as the
Claude path. The rewrite payload speaks Copilot's native shape — `{toolName, toolArgs}` in,
`{"modifiedArgs": …}` out — with the same conservative eligibility as the Claude hook, plus a
stricter ban list for `powershell` tool calls (`(`, `)`, `{`, `}`, `@` disqualify, since those
shift PowerShell into expression mode). The `bash` flavor intercepts the full family set
including `grep`/`ls`/`find`; the `powershell` flavor keeps the core set only, because
PowerShell resolves `ls` to its `Get-ChildItem` alias and Windows resolves `find` to
`find.exe` (string search) — rewriting those would change what runs.

One behavioral difference matters: Copilot CLI `preToolUse` hooks are **fail-closed** — if the
hook process exits nonzero, Copilot *denies* the agent's tool call. vtk's rewrite always exits
`0` (passthrough on anything unexpected), but if the installed `vtk.json` points at a vtk
binary that has since moved or been deleted, Copilot will deny agent shell commands until you
run `vtk hooks init --copilot` again (heals in place) or `vtk hooks init --copilot
--uninstall`. `vtk hooks verify --copilot` detects exactly this desync.

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

## Shared config

Repo-level agent configuration is cherry-picked from the
[meridun/model-repo](https://github.com/meridun/model-repo) upstream hub. Procedure:
`.github/skills/proj-upstream-sync/SKILL.md`. Declined rows are recorded so the decision can be
revisited; they are not drift.

> **Upstream pin:** meridun/model-repo **d97e6b6** (2026-09-05). To re-sync, run the
> proj-upstream-sync downstream interview against the component inventory in model-repo's
> README, then bump this pin. Local adaptations to preserve: `.claude/` copies are hand-mirrored
> (no sync script); caveman drift check (meta-drift rule 4) not adopted; SDLC is pinned to
> agentic-sdlc directly, not through model-repo.

| Component | Status | Reason / notes |
|---|---|---|
| Doc-tier system (L1/L2/L3) | partial: L1 shape and `CLAUDE.md` include only | `proj-doc-tiers` / `proj-agent-skill` skills and the Compound Tasks table not needed yet |
| Config sync + meta-drift guard | declined | .NET repo, no Node toolchain; one agent and one skill are hand-mirrored into `.claude/` |
| Caveman mode hook | adopted: L1 `## Caveman mode` + `UserPromptSubmit` hook in `.claude/settings.json` | replaces the former `caveman` skill; drift rule 4 not adopted (needs the Node drift script) |
| graphify nudge hook | declined | graphify not used in vtk |
| Role-based model routing | declined | `vtk-sdlc-worker` is the only agent; work runs through SDLC lanes, main-session delegation is rare |
| Agentic SDLC pipeline | adopted from [meridun/agentic-sdlc](https://github.com/meridun/agentic-sdlc) directly | see `prompts/sdlc/PROFILE.md`; design lane off, PowerShell deterministic core; not synced via model-repo |
| Upstream sync procedure | adopted: `.github/skills/proj-upstream-sync/` (mirrored to `.claude/skills/`) | prefix kept as `proj-`; its links to `proj-agent-skill` / `proj-doc-tiers` dangle here (declined above) |

## Documentation

- [docs/Overview.md](docs/Overview.md) — goals, scope, comparison to rtk
- [docs/Architecture.md](docs/Architecture.md) — filter pipeline design
- [docs/ToolCoverage.md](docs/ToolCoverage.md) — filter catalog: parity targets + expansions

## License

[MIT](LICENSE)
