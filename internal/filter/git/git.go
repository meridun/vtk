// Package git holds the git filter family: pure functions that compact the
// human-format output of high-frequency git subcommands. Raw output in,
// compact output out; returning the input unchanged means "nothing to elide".
package git

import (
	"fmt"
	"regexp"
	"strings"
)

var (
	commitRe   = regexp.MustCompile(`^commit ([0-9a-f]{7,40})(?: \((.*)\))?`)
	diffFileRe = regexp.MustCompile(`^diff --git a/(.+) b/(.+)$`)
	aheadRe    = regexp.MustCompile(`Your branch is (ahead|behind) [^\s]+ by (\d+) commit`)
	statLineRe = regexp.MustCompile(`^\S.*\|\s+(\d+\s*[+-]*|Bin\b.*)$`)
)

// Status compacts `git status` (human format) to a porcelain-style summary:
// a "## branch" header plus one short-coded line per changed file.
func Status(raw string) string {
	branch := ""
	track := ""
	var entries []string
	section := ""
	for _, line := range strings.Split(raw, "\n") {
		t := strings.TrimSpace(line)
		switch {
		case strings.HasPrefix(t, "On branch "):
			branch = strings.TrimPrefix(t, "On branch ")
		case strings.HasPrefix(t, "HEAD detached at "):
			branch = "detached@" + strings.TrimPrefix(t, "HEAD detached at ")
		case aheadRe.MatchString(t):
			m := aheadRe.FindStringSubmatch(t)
			track = " [" + m[1] + " " + m[2] + "]"
		case strings.Contains(t, "have diverged"):
			track = " [diverged]"
		case strings.HasPrefix(t, "Changes to be committed"):
			section = "staged"
		case strings.HasPrefix(t, "Changes not staged"):
			section = "unstaged"
		case strings.HasPrefix(t, "Untracked files"):
			section = "untracked"
		case strings.HasPrefix(t, "Unmerged paths"):
			section = "unmerged"
		case strings.HasPrefix(line, "\t") && t != "" && section != "":
			entries = append(entries, statusEntry(t, section))
		}
	}
	if branch == "" && len(entries) == 0 {
		return raw // unrecognized shape: pass through unchanged
	}
	head := "## " + branch + track
	if strings.Contains(raw, "nothing to commit, working tree clean") {
		return head + " clean"
	}
	return strings.Join(append([]string{head}, entries...), "\n")
}

func statusEntry(entry, section string) string {
	kind, file := "", entry
	if i := strings.Index(entry, ":"); i >= 0 {
		kind = entry[:i]
		file = strings.TrimSpace(entry[i+1:])
	}
	var c string
	switch kind {
	case "new file":
		c = "A"
	case "modified":
		c = "M"
	case "deleted":
		c = "D"
	case "renamed":
		c = "R"
	case "copied":
		c = "C"
	case "typechange":
		c = "T"
	default:
		c = "?"
	}
	switch section {
	case "staged":
		return c + "  " + file
	case "unstaged":
		return " " + c + " " + file
	case "unmerged":
		return "UU " + file
	default: // untracked
		return "?? " + file
	}
}

// Log compacts default `git log` output to one line per commit:
// short hash, decorations if present, subject.
func Log(raw string) string {
	var out []string
	cur := ""
	haveSubject := false
	for _, line := range strings.Split(raw, "\n") {
		if m := commitRe.FindStringSubmatch(line); m != nil {
			if cur != "" {
				out = append(out, cur)
			}
			h := m[1]
			if len(h) > 7 {
				h = h[:7]
			}
			cur = h
			if m[2] != "" {
				cur += " (" + m[2] + ")"
			}
			haveSubject = false
			continue
		}
		if cur != "" && !haveSubject && strings.HasPrefix(line, "    ") && strings.TrimSpace(line) != "" {
			cur += " " + strings.TrimSpace(line)
			haveSubject = true
		}
	}
	if cur != "" {
		out = append(out, cur)
	}
	if len(out) == 0 {
		return raw
	}
	return strings.Join(out, "\n")
}

type fileStat struct {
	name   string
	add    int
	del    int
	binary bool
}

func diffStats(raw string) []fileStat {
	var stats []fileStat
	var cur *fileStat
	for _, line := range strings.Split(raw, "\n") {
		if m := diffFileRe.FindStringSubmatch(line); m != nil {
			stats = append(stats, fileStat{name: m[2]})
			cur = &stats[len(stats)-1]
			continue
		}
		if cur == nil {
			continue
		}
		switch {
		case strings.HasPrefix(line, "Binary files"):
			cur.binary = true
		case strings.HasPrefix(line, "+++"), strings.HasPrefix(line, "---"):
		case strings.HasPrefix(line, "+"):
			cur.add++
		case strings.HasPrefix(line, "-"):
			cur.del++
		}
	}
	return stats
}

func formatStats(stats []fileStat) []string {
	var out []string
	ta, td := 0, 0
	for _, s := range stats {
		if s.binary {
			out = append(out, s.name+" | bin")
			continue
		}
		out = append(out, fmt.Sprintf("%s | +%d -%d", s.name, s.add, s.del))
		ta += s.add
		td += s.del
	}
	if len(stats) > 1 {
		out = append(out, fmt.Sprintf("%d files, +%d -%d", len(stats), ta, td))
	}
	return out
}

// Diff compacts `git diff` to a per-file +/- summary with a total line.
func Diff(raw string) string {
	stats := diffStats(raw)
	if len(stats) == 0 {
		return raw // no unified-diff headers (e.g. --stat output): pass through
	}
	return strings.Join(formatStats(stats), "\n")
}

// Show compacts `git show` to "hash subject" plus the diff summary.
func Show(raw string) string {
	head := ""
	for _, line := range strings.Split(raw, "\n") {
		if m := commitRe.FindStringSubmatch(line); m != nil && head == "" {
			h := m[1]
			if len(h) > 7 {
				h = h[:7]
			}
			head = h
			continue
		}
		if head != "" && strings.HasPrefix(line, "    ") && strings.TrimSpace(line) != "" {
			head += " " + strings.TrimSpace(line)
			break
		}
	}
	stats := diffStats(raw)
	if head == "" && len(stats) == 0 {
		return raw
	}
	var out []string
	if head != "" {
		out = append(out, head)
	}
	out = append(out, formatStats(stats)...)
	return strings.Join(out, "\n")
}

// Add drops line-ending conversion warnings from `git add`; anything else
// (errors, unusual warnings) is kept verbatim.
func Add(raw string) string {
	var out []string
	for _, line := range strings.Split(raw, "\n") {
		t := strings.TrimSpace(line)
		if t == "" {
			continue
		}
		if strings.Contains(t, "LF will be replaced by CRLF") ||
			strings.Contains(t, "CRLF will be replaced by LF") ||
			strings.HasPrefix(t, "warning: in the working copy of") {
			continue
		}
		out = append(out, t)
	}
	return strings.Join(out, "\n")
}

// Commit keeps the "[branch hash] subject" line and the change summary,
// dropping per-file create/delete/rewrite mode chatter.
func Commit(raw string) string {
	var out []string
	for _, line := range strings.Split(raw, "\n") {
		t := strings.TrimSpace(line)
		switch {
		case strings.HasPrefix(t, "["):
			out = append(out, t)
		case strings.Contains(t, "file changed") || strings.Contains(t, "files changed"):
			out = append(out, t)
		}
	}
	if len(out) == 0 {
		return raw
	}
	return strings.Join(out, "\n")
}

var transferNoise = []string{
	"Enumerating objects", "Counting objects", "Compressing objects",
	"Writing objects", "Receiving objects", "Resolving deltas",
	"Delta compression", "Unpacking objects", "Total ",
	"remote: Enumerating", "remote: Counting", "remote: Compressing",
	"remote: Resolving", "remote: Total",
}

func isTransferNoise(t string) bool {
	for _, p := range transferNoise {
		if strings.HasPrefix(t, p) {
			return true
		}
	}
	return false
}

// Push drops object-transfer progress chatter, keeping the destination,
// ref-update lines, rejections, and errors.
func Push(raw string) string {
	var out []string
	for _, line := range strings.Split(raw, "\n") {
		t := strings.TrimSpace(line)
		if t == "" || isTransferNoise(t) {
			continue
		}
		out = append(out, t)
	}
	return strings.Join(out, "\n")
}

// Pull drops transfer progress, the "From ..." line, and per-file diffstat
// lines, keeping the update range, strategy, and change summary.
func Pull(raw string) string {
	var out []string
	for _, line := range strings.Split(raw, "\n") {
		t := strings.TrimSpace(line)
		if t == "" || isTransferNoise(t) {
			continue
		}
		if strings.HasPrefix(t, "From ") {
			continue
		}
		if strings.HasPrefix(t, "create mode ") || strings.HasPrefix(t, "delete mode ") {
			continue
		}
		if statLineRe.MatchString(t) && !strings.Contains(t, "changed") {
			continue
		}
		out = append(out, t)
	}
	return strings.Join(out, "\n")
}

// Branch joins the branch list onto a single line; the current branch keeps
// its "*" marker.
func Branch(raw string) string {
	var items []string
	for _, line := range strings.Split(raw, "\n") {
		t := strings.TrimSpace(line)
		if t == "" {
			continue
		}
		items = append(items, t)
	}
	return strings.Join(items, ", ")
}
