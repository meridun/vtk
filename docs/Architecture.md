# Architecture

Pre-implementation sketch. Sections marked *(open)* firm up as ADRs are decided.

## Pipeline

```
vtk <cmd> [args...]
  │
  ├─ 1. Dispatch: match <cmd> (+ subcommand) against the filter registry
  │      match  → run command, capture stdout/stderr, apply filter, emit compacted output
  │      no match → run command with output passed through untouched, write fallback log entry
  │
  ├─ 2. Preserve semantics: exit code, stderr routing, and TTY detection mirror the wrapped
  │      command. Interactive/TTY-detected invocations bypass filtering entirely.
  │
  └─ 3. Record: append savings entry (filtered) or gap entry (passthrough) to the local stats log
```

## Components

- **Registry** — maps command/subcommand patterns to filters. Filters are pure functions:
  raw output in, compacted output out. One filter per tool family (git, gh, npm, ...).
- **Runner** — spawns the wrapped command, streams/captures output, propagates exit code and
  signals. The only component with process-spawning responsibility.
- **Stats store** — append-only local log of savings and gaps; backs `vtk gain` and `vtk gaps`.
  Format *(open — likely JSONL in a platform data dir)*.
- **Fallback logger** — writes gap entries: full argv, raw output byte/line count, timestamp,
  cwd. Never writes command *output content* (may contain secrets), only metadata.

## Key invariants

1. Exit code of `vtk <cmd>` == exit code of `<cmd>`. Always.
2. A filter failure (panic/exception in filter code) degrades to raw passthrough + gap log,
   never to lost output or a vtk-originated nonzero exit.
3. Fallback logging is metadata-only — no output content persisted.
4. Filters are testable in isolation: fixture in, expected out, no process spawning in filter
   unit tests.

## Open decisions

- [ADR-0001: Implementation language](decisions/0001-implementation-language.md)
- Stats/gap log format and location — draft as ADR-0002 when picked up
- Filter configuration/override mechanism (user-disabled filters, custom filters) — future ADR
