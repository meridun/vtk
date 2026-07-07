// Package npm holds the shared dispatch layer for `npm run <script>`. npm
// prepends a two-line banner to every script run — a "> <pkg>@<ver> <script>"
// header and a "> <expanded command line>" line — pure per-call noise. This
// package strips that banner and parses the inner tool from the expanded
// command line so the runner can delegate the remaining output to that tool's
// own filter (eslint, mocha, dbmate, ...).
//
// Everything here is pure: raw output in, banner-stripped body + inner argv
// out. No process spawning, no filesystem access (docs/Architecture.md
// invariant 4). The runner (cmd/vtk) owns the delegation and the gap
// attribution to the inner tool's family.
package npm

import "strings"

// bannerPrefix marks both npm banner lines. npm writes them as "> ..." with a
// single leading "> ".
const bannerPrefix = "> "

// StripBanner detects the npm run banner in raw and returns the output body
// with the banner removed, plus the inner command parsed from the expanded
// command line. ok is false when raw does not carry a recognizable npm banner,
// in which case body and inner are unspecified and the caller must treat the
// invocation as an ordinary (unhandled) command — never as a banner strip.
//
// Recognized shape (leading blank lines tolerated — npm/stderr interleaving can
// emit one):
//
//	> <pkg>@<ver> <script>
//	> <expanded command line>
//	<optional blank line>
//	<inner tool output...>
//
// The inner command is the fields of the second banner line, so
// "> eslint . --cache" yields inner = ["eslint", ".", "--cache"].
func StripBanner(raw string) (body string, inner []string, ok bool) {
	lines := strings.Split(raw, "\n")

	// Skip any leading blank lines before the banner.
	i := 0
	for i < len(lines) && strings.TrimSpace(lines[i]) == "" {
		i++
	}

	// Need two consecutive banner lines: the script header and the expanded
	// command line. Anything else is not an npm run banner.
	if i+1 >= len(lines) ||
		!strings.HasPrefix(lines[i], bannerPrefix) ||
		!strings.HasPrefix(lines[i+1], bannerPrefix) {
		return "", nil, false
	}

	expanded := strings.TrimSpace(strings.TrimPrefix(lines[i+1], bannerPrefix))
	inner = strings.Fields(expanded)
	if len(inner) == 0 {
		// A banner with an empty command line is malformed; do not claim it.
		return "", nil, false
	}

	// Body is everything after the two banner lines. Drop a single blank
	// separator line that npm emits between the banner and the tool output;
	// preserve any further blank lines (they may be meaningful to the inner
	// filter).
	rest := lines[i+2:]
	if len(rest) > 0 && strings.TrimSpace(rest[0]) == "" {
		rest = rest[1:]
	}
	return strings.Join(rest, "\n"), inner, true
}
