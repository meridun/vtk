// Package gh holds the GitHub CLI (`gh`) filter family: pure functions that
// compact the tab-separated human-format output of `gh` list subcommands.
// Raw output in, compact output out; returning the input unchanged means
// "nothing to elide" (docs/Architecture.md invariant 4).
//
// `gh` list commands emit tab-separated rows when their output is not a TTY
// (which is always the case under vtk's capture). `--json` forms are emitted
// as a JSON object/array and are passed through structurally intact.
package gh

import (
	"strings"
	"time"
)

// isNum reports whether s is a non-empty run of ASCII digits — the leading
// field of an issue/PR list row. `gh issue view` / `gh pr view` output leads
// with "title:" etc., so this guard keeps the filter from mangling view output
// that reaches the same two-token registry key (Lookup is cmd+subcommand).
func isNum(s string) bool {
	s = strings.TrimSpace(s)
	if s == "" {
		return false
	}
	for _, c := range s {
		if c < '0' || c > '9' {
			return false
		}
	}
	return true
}

// now is the clock used for relative-time formatting. The exported filters use
// time.Now(); tests inject a fixed value via the *At helpers so goldens stay
// deterministic.
func now() time.Time { return time.Now() }

// isJSON reports whether raw looks like a `gh --json` payload, which must pass
// through unchanged so the JSON stays structurally intact.
func isJSON(raw string) bool {
	t := strings.TrimSpace(raw)
	return strings.HasPrefix(t, "{") || strings.HasPrefix(t, "[")
}

// rows splits raw into non-empty lines, each further split on tabs. Returns nil
// if no tab-delimited row is found (unrecognized shape → caller passes through).
func rows(raw string) [][]string {
	var out [][]string
	for _, line := range strings.Split(raw, "\n") {
		if strings.TrimSpace(line) == "" {
			continue
		}
		if !strings.Contains(line, "\t") {
			continue
		}
		out = append(out, strings.Split(line, "\t"))
	}
	return out
}

// rel renders an ISO-8601 timestamp as a compact relative age (e.g. "3h",
// "2d", "5mo") against ref. Unparseable timestamps render empty.
func rel(ts string, ref time.Time) string {
	t, err := time.Parse(time.RFC3339, strings.TrimSpace(ts))
	if err != nil {
		return ""
	}
	d := ref.Sub(t)
	if d < 0 {
		d = 0
	}
	switch {
	case d < time.Minute:
		return "now"
	case d < time.Hour:
		return itoa(int(d.Minutes())) + "m"
	case d < 24*time.Hour:
		return itoa(int(d.Hours())) + "h"
	case d < 30*24*time.Hour:
		return itoa(int(d.Hours()/24)) + "d"
	case d < 365*24*time.Hour:
		return itoa(int(d.Hours()/(24*30))) + "mo"
	default:
		return itoa(int(d.Hours()/(24*365))) + "y"
	}
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var b []byte
	for n > 0 {
		b = append([]byte{byte('0' + n%10)}, b...)
		n /= 10
	}
	if neg {
		b = append([]byte{'-'}, b...)
	}
	return string(b)
}

// appendRel joins a base line with a "(age)" suffix when age is non-empty.
func appendRel(base, age string) string {
	if age == "" {
		return base
	}
	return base + " (" + age + ")"
}

// IssueList compacts `gh issue list` (TSV: num, STATE, title, labels, ts) to
// one "#<n> <state> <title> (<age>)" line per issue, dropping label noise.
func IssueList(raw string) string { return issueListAt(raw, now()) }

func issueListAt(raw string, ref time.Time) string {
	if isJSON(raw) {
		return raw
	}
	rs := rows(raw)
	if len(rs) == 0 {
		return raw
	}
	var out []string
	for _, f := range rs {
		if len(f) < 3 || !isNum(f[0]) {
			return raw // not a list row (e.g. `gh issue view`): pass through
		}
		age := ""
		if len(f) >= 5 {
			age = rel(f[4], ref)
		}
		line := "#" + f[0] + " " + strings.ToLower(f[1]) + " " + f[2]
		out = append(out, appendRel(line, age))
	}
	return strings.Join(out, "\n")
}

// PrList compacts `gh pr list` (TSV: num, title, headBranch, STATE, ts) to one
// "#<n> <state> <title> (<age>)" line per PR, dropping the head-branch column.
func PrList(raw string) string { return prListAt(raw, now()) }

func prListAt(raw string, ref time.Time) string {
	if isJSON(raw) {
		return raw
	}
	rs := rows(raw)
	if len(rs) == 0 {
		return raw
	}
	var out []string
	for _, f := range rs {
		if len(f) < 4 || !isNum(f[0]) {
			return raw // not a list row (e.g. `gh pr view`): pass through
		}
		age := ""
		if len(f) >= 5 {
			age = rel(f[4], ref)
		}
		line := "#" + f[0] + " " + strings.ToLower(f[3]) + " " + f[1]
		out = append(out, appendRel(line, age))
	}
	return strings.Join(out, "\n")
}

// runStatus is the set of `gh run list` first-column status values; anything
// else in field 0 means the input is not a run-list table (pass through).
var runStatus = map[string]bool{
	"completed": true, "in_progress": true, "queued": true,
	"requested": true, "waiting": true, "pending": true,
}

// RunList compacts `gh run list` (TSV: status, conclusion, title, workflow,
// branch, event, runID, elapsed, ts) to one "<conclusion> <title> · <workflow>
// (<age>)" line per run, dropping branch/event/runID/elapsed noise.
func RunList(raw string) string { return runListAt(raw, now()) }

func runListAt(raw string, ref time.Time) string {
	if isJSON(raw) {
		return raw
	}
	rs := rows(raw)
	if len(rs) == 0 {
		return raw
	}
	var out []string
	for _, f := range rs {
		if len(f) < 4 || !runStatus[strings.TrimSpace(f[0])] {
			return raw // not a run-list row: pass through unchanged
		}
		state := f[1]
		if state == "" {
			state = f[0] // in-progress runs have no conclusion yet
		}
		age := ""
		if len(f) >= 9 {
			age = rel(f[8], ref)
		}
		line := state + " " + f[2] + " · " + f[3]
		out = append(out, appendRel(line, age))
	}
	return strings.Join(out, "\n")
}
