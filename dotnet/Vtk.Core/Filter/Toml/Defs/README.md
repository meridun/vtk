# Declarative TOML filters

One `*.toml` file per filter. Each is embedded at build (`EmbeddedResource` in
`Vtk.Core.csproj`), compiled into a pure `FilterFunc`, and registered into the
filter dispatch alongside the hand-written C# filter families. Use TOML for
tools whose output can be compacted by dropping/keeping lines and capping
length; keep C# for reformatters that regex-stripping can't do (spec-tree
folding, per-rule rollups).

## Schema

| field | type | purpose |
|---|---|---|
| `name` | string (required) | filter id, used in registration + errors |
| `match_command` | regex string (required) | matched against the full command string (`argv` joined by spaces) |
| `exit_codes` | int array | child exit codes on which the filter runs (default `[0]`; report-style tools add e.g. `1`) |
| `strip_ansi` | bool | strip ANSI/SGR escape sequences before matching |
| `filter_stderr` | bool | **not supported yet** — the runner merges stdout+stderr before a filter runs, so this is rejected at load until stream separation lands (issue #40) |
| `strip_lines_matching` | regex array | drop any line matching one of these |
| `keep_lines_matching` | regex array | keep only lines matching one of these (applied before `strip_lines_matching`) |
| `[[replace]]` | `{pattern, replacement}` | regex substitution across the whole output |
| `[[match_output]]` | `{pattern, message}` | if `pattern` matches anywhere, the whole output collapses to `message` (short-circuit) |
| `truncate_lines_at` | int | cap each line at N chars, appending `...` |
| `max_lines` | int | cap total lines, appending `... (N more lines)` |
| `on_empty` | string | message emitted when filtering leaves the output blank |
| `[[tests.<name>]]` | `{input, expected}` | inline fixtures, run under `dotnet test` |

## Pipeline order

`strip_ansi` → `match_output` → `keep_lines_matching` → `strip_lines_matching` →
`replace` → `truncate_lines_at` → `max_lines` → `on_empty`.

If the pipeline elides nothing, the raw input is returned unchanged and vtk
passes it through untouched (no `OK <id>`), exactly like a hand-written filter
that finds nothing to compact.

## Invariants

- **Exit-code parity:** `exit_codes` only gates whether the filter runs vs. raw
  passthrough; vtk always returns the child's own exit code.
- **Failures emit raw:** default `exit_codes = [0]` keeps error output verbatim.
- **Fixtures required:** every filter ships `[[tests.<name>]]` cases (captured
  real output preferred; scrub anything sensitive before committing). The test
  harness runs them all and fails the build on any mismatch or bad regex.
