# Overview

## What vtk is

vtk (Voya Token Killer) is a shell-command wrapper that compacts noisy tool output before it
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

## Non-goals

- Not a shell, not a command runner with its own semantics. Exit codes, signals, and side
  effects of the wrapped command are preserved exactly.
- Not agent-specific. vtk is agent-agnostic; anything that reads stdout benefits.
- No network calls, no telemetry. Fallback logs are local files only.

## Comparison to rtk

| Aspect | rtk | vtk |
|---|---|---|
| Filter catalog | ~20 tool families | rtk parity + expansions (see ToolCoverage.md) |
| Unfiltered commands | Silent passthrough | Passthrough + fallback log |
| Gap analysis | `rtk discover` (post-hoc session scan) | `vtk gaps` (execution-time log) |
| Savings stats | `rtk gain` | `vtk gain` (same idea) |
| Language | Rust | Open — [ADR-0001](decisions/0001-implementation-language.md) |
