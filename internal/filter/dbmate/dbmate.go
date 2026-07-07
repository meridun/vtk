// Package dbmate holds the dbmate filter family: pure functions that compact
// the human-format output of dbmate migration commands. Raw output in, compact
// output out; returning the input unchanged means "nothing to elide".
//
// The dominant noise source (measured on IsekaiOnline: `npm run db:status` →
// 3,674 bytes) is the full applied-migrations list in `dbmate status`, which is
// almost entirely "[X] <name>" lines. The signal is the pending list and the
// applied/pending counts. The up/rollback/migrate commands emit a transient
// "Applying:"/"Rolling back:" progress line per migration alongside the durable
// result line; only the result (and any error) carries information.
//
// Everything here is pure: no process spawning, no filesystem access
// (docs/Architecture.md invariant 4).
package dbmate

import (
	"strconv"
	"strings"
)

// Status compacts `dbmate status`: applied migrations ("[X] ...") collapse to a
// single "[X] Applied: N" count line, pending migrations ("[ ] ...") are kept
// verbatim (they are the actionable signal), and the trailing summary is
// preserved. Output whose shape is not recognized (no status entries) passes
// through unchanged.
func Status(raw string) string {
	var out []string
	applied := 0
	sawEntry := false
	for _, line := range strings.Split(raw, "\n") {
		t := strings.TrimSpace(line)
		switch {
		case strings.HasPrefix(t, "[X]"):
			applied++
			sawEntry = true
			// collapse: emit the count line once, in place of the first
			// applied entry, so ordering (applied block then pending) holds.
			if applied == 1 {
				out = append(out, "") // placeholder, filled in below
			}
		case strings.HasPrefix(t, "[ ]"):
			sawEntry = true
			out = append(out, t)
		default:
			// blank lines and the "Applied: N"/"Pending: N" summary lines.
			out = append(out, t)
		}
	}
	if !sawEntry {
		return raw // unrecognized shape: pass through unchanged
	}
	// Fill the collapsed-applied placeholder now that the total is known. If
	// there were no applied migrations there is no placeholder to fill.
	if applied > 0 {
		for i, l := range out {
			if l == "" {
				out[i] = "[X] Applied: " + strconv.Itoa(applied)
				break
			}
		}
	}
	return strings.Join(trimTrailingBlanks(out), "\n")
}

// Migrate compacts `dbmate up`/`down`/`migrate`/`rollback`: it drops the
// transient progress lines ("Applying:", "Rolling back:", "Creating:") and
// keeps the durable result lines ("Applied:", "Rolled back:") plus any error
// output. Output with no recognized result or error line passes through
// unchanged (e.g. an already-up-to-date no-op emits nothing).
func Migrate(raw string) string {
	var out []string
	kept := false
	for _, line := range strings.Split(raw, "\n") {
		t := strings.TrimSpace(line)
		if t == "" {
			continue
		}
		switch {
		case strings.HasPrefix(t, "Applying:"),
			strings.HasPrefix(t, "Rolling back:"),
			strings.HasPrefix(t, "Creating:"),
			strings.HasPrefix(t, "Dropping:"):
			// transient progress chatter: drop.
			continue
		default:
			// result lines ("Applied:", "Rolled back:"), errors, and anything
			// else we do not recognize as pure progress: keep verbatim.
			out = append(out, t)
			kept = true
		}
	}
	if !kept {
		return raw
	}
	return strings.Join(out, "\n")
}

// trimTrailingBlanks removes trailing empty strings so a collapsed status does
// not end with dangling blank lines.
func trimTrailingBlanks(lines []string) []string {
	for len(lines) > 0 && lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	return lines
}
