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
- **Git flow** — `{feature} → dev → main`. All work happens on feature branches
  (`<type>/<issue#>-<slug>`) cut from `dev` (the default/integration branch) and lands via PR to
  `dev`. `main` is the stable/release branch: it only moves by PR from `dev`; never branch from,
  checkout, commit to, or merge to `main` unless explicitly requested. Both hops are PRs — no
  direct pushes to `dev` or `main` except trivial docs-only commits to `dev`.

## Documentation Tiers

- **L1** (this file) — loaded every request. Principles + routing only.
- **L2** (`.github/skills/*/SKILL.md`) — task-scoped patterns, auto-loaded on match.
  (None yet; add via a `skills` directory as patterns stabilize.)
- **L3** (`docs/`) — read explicitly when needed.

L3 entry points: [Overview.md](../docs/Overview.md),
[Architecture.md](../docs/Architecture.md), [ToolCoverage.md](../docs/ToolCoverage.md).

Agentic SDLC pipeline: [prompts/sdlc/README.md](../prompts/sdlc/README.md) — stage-labeled
issues worked lane-by-lane (intake → queued → build → verify → audit → ship). `stage:queued`
admission and PR merges are the human gates.

Docs are current/future-facing only. Decisions are one-liners in the Architecture.md decision
registry, each linking to the GitHub issue holding the debate. Historical rationale lives in
issues, never in docs. Check the registry before assuming an implementation choice is open.

## Tone

Professional and concise.
