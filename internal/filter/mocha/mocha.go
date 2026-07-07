// Package mocha holds the mocha filter: a pure function that compacts the
// default "spec" reporter output of `mocha` / `npx mocha`. Raw output in,
// compact output out; returning the input unchanged means "nothing to elide".
//
// A passing run prints one indented "✔ <title>" line per spec under a nested
// suite tree — pure per-spec noise once the run is green. A failing run adds a
// "failing" summary line and a detail block per failure (name, assertion,
// trimmed stack) — that detail is the whole point to keep. The filter folds the
// spec tree, keeps the summary lines, and keeps the failure-detail blocks
// verbatim.
//
// mocha reports test failures via exit 1 — that output is exactly what we want
// to compact (fold the passing specs, keep the failures). The registry declares
// mocha's exit-code allowlist as {0, 1} (exit 2+ is a mocha/config error and
// stays raw); exit-code parity is unaffected (docs/Architecture.md decision
// registry #7).
package mocha

import (
	"regexp"
	"strings"
)

var (
	// A summary line: "  N passing (12ms)", "  M failing", "  K pending".
	// mocha indents these two spaces; count first, keyword second.
	summaryRe = regexp.MustCompile(`^\s*\d+\s+(passing|failing|pending)\b`)
	// The first line of a failure-detail block: "  N) <suite title>". Same
	// shape appears in the tree ("    N) <title>"), but the detail region only
	// begins after the summary, so we switch on the summary boundary rather
	// than on this pattern alone.
	failDetailRe = regexp.MustCompile(`^\s*\d+\)\s`)
)

// Filter compacts mocha spec-reporter output to just the summary lines plus the
// per-failure detail blocks:
//
//	N passing (12ms)
//	M failing
//
//	1) suite title
//	     spec title:
//	    AssertionError ...
//	    at ...
//
// The passing/pending/suite spec tree above the summary is folded away (a green
// run collapses to a single "N passing" line). Everything from the summary
// onward — the counts and every failure block with its assertion and stack — is
// preserved verbatim. Input that carries no mocha summary passes through
// unchanged (unrecognized shape: fallback is a feature).
func Filter(raw string) string {
	lines := strings.Split(raw, "\n")

	// Locate the summary region: the contiguous run of summary lines. mocha
	// prints them together (passing, then failing, then pending as applicable)
	// after the spec tree and before the failure-detail blocks.
	firstSummary, lastSummary := -1, -1
	for i, line := range lines {
		if summaryRe.MatchString(line) {
			if firstSummary == -1 {
				firstSummary = i
			}
			lastSummary = i
		}
	}
	if firstSummary == -1 {
		// No mocha summary: not spec-reporter output we recognize. Pass through
		// unchanged rather than risk mangling an unrelated tool's output.
		return raw
	}

	var out []string
	// Keep the summary lines, trimmed of mocha's leading indent so the rollup
	// reads as a compact header.
	for i := firstSummary; i <= lastSummary; i++ {
		if summaryRe.MatchString(lines[i]) {
			out = append(out, strings.TrimSpace(lines[i]))
		}
	}

	// Keep the failure-detail region verbatim: from the first "N)" block after
	// the summary to the last non-blank line. Preserve interior blank lines
	// (they separate blocks and are part of the assertion diff), but trim the
	// trailing blank padding mocha emits.
	detailStart := -1
	for i := lastSummary + 1; i < len(lines); i++ {
		if failDetailRe.MatchString(lines[i]) {
			detailStart = i
			break
		}
	}
	if detailStart != -1 {
		detailEnd := len(lines) - 1
		for detailEnd >= detailStart && strings.TrimSpace(lines[detailEnd]) == "" {
			detailEnd--
		}
		if detailEnd >= detailStart {
			out = append(out, "") // blank line between summary and first block
			out = append(out, lines[detailStart:detailEnd+1]...)
		}
	}

	return strings.Join(out, "\n")
}
