// Package files holds the files/search filter family: pure functions that
// compact the piped output of `ls`, `grep`, and `find`. Raw output in,
// compact output out; returning the input unchanged means "nothing to elide".
//
// Byte savings come from capping long listings — the full output stays
// recoverable via the spool `OK <id>` line the runner emits whenever content
// was elided (docs/Architecture.md #output-spool).
package files

import (
	"fmt"
	"regexp"
	"strings"
)

const (
	// packWidth is the maximum row width when column-packing a name list.
	packWidth = 96
	// maxEntries caps how many list entries are shown before the "(+N more)"
	// tail; the rest live in the spool.
	maxEntries = 40
	// maxPerFile caps grep match lines shown per file.
	maxPerFile = 5
)

var (
	// A long-format mode string ("drwxr-xr-x", "-rw-r--r--", ...): `ls -l`
	// output is not a plain name list and passes through unchanged.
	modeRe = regexp.MustCompile(`^[bcdlps-][rwxsStT-]{9}`)
	// A grep match line with line numbers: "path:lineno:content".
	grepMatchRe = regexp.MustCompile(`^(.+?):(\d+):`)
)

// nonEmptyLines splits raw into lines, trimming a trailing CR and dropping
// blank lines.
func nonEmptyLines(raw string) []string {
	var out []string
	for _, line := range strings.Split(raw, "\n") {
		line = strings.TrimRight(line, "\r")
		if strings.TrimSpace(line) == "" {
			continue
		}
		out = append(out, line)
	}
	return out
}

// packList column-packs entries into rows of at most packWidth characters
// (two-space separated), capped at maxEntries with a "(+N more)" tail.
func packList(entries []string) string {
	shown := entries
	if len(entries) > maxEntries {
		shown = entries[:maxEntries]
	}
	var rows []string
	cur := ""
	for _, e := range shown {
		switch {
		case cur == "":
			cur = e
		case len(cur)+2+len(e) <= packWidth:
			cur += "  " + e
		default:
			rows = append(rows, cur)
			cur = e
		}
	}
	if cur != "" {
		rows = append(rows, cur)
	}
	if n := len(entries) - len(shown); n > 0 {
		rows = append(rows, fmt.Sprintf("(+%d more)", n))
	}
	return strings.Join(rows, "\n")
}

// Ls compacts piped `ls` output (one entry per line) by column-packing the
// names and capping the list. Long-format output (`ls -l`: a "total N" header
// or mode-string lines) is not a plain name list and passes through unchanged.
func Ls(raw string) string {
	lines := nonEmptyLines(raw)
	if len(lines) == 0 {
		return raw
	}
	for _, l := range lines {
		if modeRe.MatchString(l) || strings.HasPrefix(l, "total ") {
			return raw // long format: pass through unchanged
		}
	}
	return packList(lines)
}

// Grep compacts `grep -rn`-style output by capping match lines per file at
// maxPerFile with a per-file "(+N more)" tail. Output with no
// "path:lineno:content" lines at all (e.g. `grep -l` file lists) is treated
// as a plain name list and column-packed. Lines that match neither shape
// (e.g. "Binary file X matches") are kept verbatim.
func Grep(raw string) string {
	lines := nonEmptyLines(raw)
	if len(lines) == 0 {
		return raw
	}

	// First pass: total match lines per file.
	total := map[string]int{}
	sawMatch := false
	for _, l := range lines {
		if m := grepMatchRe.FindStringSubmatch(l); m != nil {
			total[m[1]]++
			sawMatch = true
		}
	}
	if !sawMatch {
		return packList(lines) // plain file list (grep -l)
	}

	// Second pass: emit up to maxPerFile lines per file, one tail per file.
	seen := map[string]int{}
	var out []string
	for _, l := range lines {
		m := grepMatchRe.FindStringSubmatch(l)
		if m == nil {
			out = append(out, l) // unrecognized shape: keep verbatim
			continue
		}
		file := m[1]
		seen[file]++
		switch {
		case seen[file] <= maxPerFile:
			out = append(out, l)
		case seen[file] == maxPerFile+1:
			out = append(out, fmt.Sprintf("%s: (+%d more)", file, total[file]-maxPerFile))
		}
	}
	return strings.Join(out, "\n")
}

// Find compacts `find` output (one path per line) by column-packing and
// capping the list.
func Find(raw string) string {
	lines := nonEmptyLines(raw)
	if len(lines) == 0 {
		return raw
	}
	return packList(lines)
}
