# Copilot Instructions

## Core Principles

- **Ask when unclear, flag uncertainty** — no silent assumptions about intent or design.
- **Minimal change** — simplest thing that works; don't touch unrelated files.
- **Never change command semantics** — vtk is a transparent wrapper. Filters compact output;
  they must never alter exit codes, stdin/stdout piping behavior, or the wrapped command's
  side effects. When in doubt, pass through unchanged.
- **Fallback is a feature** — unfiltered passthrough must always log (command, byte counts,
  timestamp) so coverage gaps are measurable. A silent passthrough is a bug.
- **Test every filter** — each filter needs fixture-based tests: raw captured output in,
  expected compacted output out, with a measured savings assertion.
- **No git operations** unless explicitly requested.

## Documentation Tiers

- **L1** (this file) — loaded every request. Principles + routing only.
- **L2** (`.github/skills/*/SKILL.md`) — task-scoped patterns, auto-loaded on match.
  (None yet; add via a `skills` directory as patterns stabilize.)
- **L3** (`docs/`) — read explicitly when needed.

L3 entry points: [Overview.md](../docs/Overview.md),
[Architecture.md](../docs/Architecture.md), [ToolCoverage.md](../docs/ToolCoverage.md),
[decisions/](../docs/decisions/).

Open decisions live in `docs/decisions/` as ADRs. Check there before assuming an
implementation choice (language, config format, log format) has been made.

## Tone

Professional and concise.
