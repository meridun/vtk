# ADR-0001: Implementation language

- **Status**: OPEN — deliberately deferred; to be debated before implementation starts.
- **Date opened**: 2026-07-06

## Context

vtk is a per-command wrapper: it is invoked once per shell command an agent runs, so cold-start
latency is on the critical path of every single tool call. It spawns child processes, streams
their output, applies text filters, and appends to a local log. It must ship as an easy-to-install
binary/package on Windows, macOS, and Linux.

## Requirements the choice must satisfy

1. **Cold start** — added latency per invocation should be low single-digit milliseconds; tens of
   ms per command compounds badly over an agent session.
2. **Process control** — faithful exit-code/signal propagation and stream handling, including on
   Windows.
3. **Distribution** — single binary or trivially-installed package; no runtime version juggling
   for users.
4. **Filter authoring velocity** — filters are the bulk of ongoing work; the language should make
   fixture-driven text-transform code cheap to write and test.

## Candidates

| Option | For | Against |
|---|---|---|
| Rust | rtk parity by construction; fastest cold start; single static binary | Slowest iteration on filter authoring; steeper contribution bar |
| Go | Fast cold start; single static binary; simple; good Windows process support | Text-wrangling more verbose than scripting languages |
| Node.js | Fastest filter iteration; author's home stack | ~50–100ms cold start per invocation; runtime dependency (mitigable via SEA/bun compile) |
| Zig/C | Minimal overhead | Ecosystem/maintenance cost too high for a text-filter tool |

## Decision

*Not yet made.* Record the debate outcome here, flip Status to ACCEPTED, and note rejected
options with reasons.
