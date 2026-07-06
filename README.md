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

## Status

Pre-implementation. The implementation language is an open decision — see
[docs/decisions/0001-implementation-language.md](docs/decisions/0001-implementation-language.md).

## Core behavior (design contract)

- **Always safe**: `vtk <cmd>` never changes the command's semantics or exit code. If no filter
  matches, output passes through unchanged (and the fallback is logged).
- **Chain-friendly**: works per-command inside `&&` chains.
- **Measurable**: `vtk gain` reports cumulative token savings; fallback logs report the gap.

## Documentation

- [docs/Overview.md](docs/Overview.md) — goals, scope, comparison to rtk
- [docs/Architecture.md](docs/Architecture.md) — filter pipeline design
- [docs/ToolCoverage.md](docs/ToolCoverage.md) — filter catalog: parity targets + expansions
- [docs/decisions/](docs/decisions/) — architecture decision records

## License

[MIT](LICENSE)
