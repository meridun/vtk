# vtk — Voya Token Killer

A command-output compaction wrapper for AI coding agents. Prefix any shell command with `vtk`
and it filters the output down to what the agent actually needs — failures, diffs, deltas —
cutting token usage by 60–90% on common development operations.

`vtk` is a workalike of [rtk (rust-token-killer)](https://github.com/wildmaples/rust-token-killer)
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

Early implementation, written in Go. Shipped so far:

- **git filter family** — `status`, `log`, `diff`, `show`, `add`, `commit`, `push`, `pull`,
  `branch` (other subcommands pass through). Measured savings 47–89% on typical fixtures.
- **Output spool + `vtk show <id>`** — filtered output is spooled (~1h TTL, credential
  redaction); `vtk show <id>` retrieves it, `--grep <pat>` returns matching lines only.
- **Gap logging + `vtk gaps`** — every unfiltered passthrough is logged (metadata only) with a
  reason (`no-filter`, `tty-bypass`, `nonzero-exit`, `filter-panic`, `spool-fail`); `vtk gaps`
  reports only true coverage gaps (`no-filter`), aggregated by command family and sorted by raw
  bytes, plus a DEGRADED section when a filter panicked and degraded to raw passthrough.

`vtk gain` (cumulative savings stats) and further filter families are not yet implemented.
Design decisions are recorded one line each in
[docs/Architecture.md](docs/Architecture.md#decision-registry) with links to the debate issues.

## Usage

```
vtk git status          # compact status; prints "OK <id>" when content was elided
vtk show <id>           # full captured output (provenance header first)
vtk show <id> --grep x  # only matching lines
vtk gaps                # uncovered-command families ranked by raw bytes (+ degraded filters)
```

## Install

With a Go toolchain (no clone needed):

```
go install github.com/meridun/vtk/cmd/vtk@latest
```

Without Go: grab a prebuilt binary (windows/amd64, linux/amd64, darwin/arm64) from the
[releases page](https://github.com/meridun/vtk/releases).

From source: `go build ./cmd/vtk`. Note for Windows: the spool directory relies on the default
user-scoped ACLs of `%LocalAppData%` (POSIX 0700 permissions are a no-op on NTFS).

## Core behavior (design contract)

- **Always safe**: `vtk <cmd>` never changes the command's semantics or exit code. If no filter
  matches, output passes through unchanged (and the fallback is logged).
- **Chain-friendly**: works per-command inside `&&` chains.
- **Measurable**: `vtk gain` (planned) reports cumulative token savings; fallback logs
  (`vtk gaps`) report the gap.
- **Compact means folded**: on the filtered success path, stdout and stderr are folded into a
  single compact result on stdout. Per-stream separation is preserved on all raw, passthrough,
  and degraded paths.

## Documentation

- [docs/Overview.md](docs/Overview.md) — goals, scope, comparison to rtk
- [docs/Architecture.md](docs/Architecture.md) — filter pipeline design
- [docs/ToolCoverage.md](docs/ToolCoverage.md) — filter catalog: parity targets + expansions

## License

[MIT](LICENSE)
