# Overview

## What vtk is

vtk (V Token Killer) is a shell-command wrapper that compacts noisy tool output before it
reaches an AI coding agent's context window. Usage model is identical to rtk: prefix any
command (`vtk git status`, `vtk npm test`), get filtered output when a dedicated filter exists,
raw output otherwise.

## Goals

1. **rtk workalike** — command-line compatible for the rtk filter catalog (git, gh, npm/npx,
   test runners, ls/read/grep/find, docker, curl, err/log/json/env/summary, gain/discover/proxy).
2. **Expanded coverage** — filters for tools rtk lacks or covers thinly. Candidate list and
   status in [ToolCoverage.md](ToolCoverage.md).
3. **Fallback logging** — every unfiltered passthrough is logged (command, raw output size,
   timestamp) to a local log, queryable via `vtk gaps`. This turns "which filters should we
   write next" from guesswork into data. rtk's `discover` analyzes Claude Code sessions
   after the fact; vtk instruments at the point of execution.
4. **Output spool** — filtered commands emit a short retrieval ID (`OK 2e3f`); the full raw
   output is spooled to disk briefly so an agent can inspect it via `vtk show 2e3f` without
   rerunning the command. See [Architecture.md](Architecture.md#output-spool).

## Branch model

`{feature} → dev → main`, both hops by PR. `dev` is the default/integration branch; `main` is
stable/release and moves only by PR from `dev`. Full discipline in
[.github/copilot-instructions.md](../.github/copilot-instructions.md).

## Non-goals

- Not a shell, not a command runner with its own semantics. Exit codes, signals, and side
  effects of the wrapped command are preserved exactly.
- Filtering is not agent-specific — the wrap path is agent-agnostic; anything that reads stdout
  benefits. Analysis tooling (`learn`, `discover`) may be agent-aware, reading local session
  data (Claude Code JSONL first) — see decision
  [#43](https://github.com/meridun/vtk/issues/43).
- No network calls of vtk's own on the wrap path, no telemetry leaving the machine. The wrapped
  command's network behavior is untouched; gap/gain data is local files only. Explicit,
  user-invoked maintenance subcommands (e.g. `gaps --file-issues`) may shell out to tools like
  `gh` — that is the user's action, not background traffic.

## Comparison to rtk

| Aspect | rtk | vtk |
|---|---|---|
| Filter catalog | ~20 tool families | rtk parity + expansions (see ToolCoverage.md) |
| Unfiltered commands | Silent passthrough | Passthrough + fallback log |
| Gap analysis | `rtk discover` (post-hoc session scan) | `vtk gaps` (execution-time log), layered with a post-hoc `discover` pass ([#44](https://github.com/meridun/vtk/issues/44)) |
| Savings stats | `rtk gain` | `vtk gain` (same idea) |
| Raw output recovery | rerun the command | `vtk show <id>` from the spool |
| Language | Rust | C# / .NET 9 ([#1](https://github.com/meridun/vtk/issues/1)) |
