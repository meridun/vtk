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
  Mirrored to `.claude/skills/` for Claude Code (same as the L1 `CLAUDE.md` ↔ this file mirror).
  Present: `proj-upstream-sync` (compare/port shared config against model-repo). Add more as
  patterns stabilize.
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

## Caveman mode

Terse by default. No preamble, no restated question, no recap or summary unless asked. No
narration of tool calls before, between, or after them. Do not restate content already written
into an artifact this turn. Keep articles and full sentences; drop filler and hedging. Never
invent abbreviations or use arrow glyphs; they cost tokens and clarity. Never drop not, never,
or only; numbers and units exact. Reply in the language the user writes. Code, paths, commands,
and error text verbatim. Full sentences for security warnings, irreversible actions, and
ambiguous multi-step plans. Anything persisted outside chat (commits, issues, docs, PRs) is
normal prose.
Wired verbatim as a `UserPromptSubmit` hook in `.claude/settings.json` for Claude Code; for
Copilot, restate this section at the top of a session if it drifts.
