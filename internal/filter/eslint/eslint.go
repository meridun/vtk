// Package eslint holds the eslint filter: a pure function that compacts the
// stylish-formatter output of `eslint` / `npx eslint`. Raw output in, compact
// output out; returning the input unchanged means "nothing to elide".
//
// eslint reports "problems found" via exit 1 — that large report is the whole
// point to compact. The registry declares eslint's exit-code allowlist as
// {0, 1} (exit 2+ is a fatal/config error and stays raw); exit-code parity is
// unaffected (docs/Architecture.md decision registry #7).
package eslint

import (
	"fmt"
	"regexp"
	"sort"
	"strings"
)

var (
	// A problem line: "  <line>:<col>  <severity>  <message>  <rule-id>".
	// The rule id is the last whitespace-run-separated token; the message is
	// everything between the severity and the rule id. Two-space column gaps
	// in the stylish formatter are collapsed by splitting on runs of spaces.
	problemRe = regexp.MustCompile(`^\s+(\d+):(\d+)\s+(error|warning)\s+(.*?)(?:\s{2,}([\w./-]+))?\s*$`)
	// The trailing summary line, e.g. "✖ 3 problems (2 errors, 1 warning)".
	summaryRe = regexp.MustCompile(`^[\s\x{2716}✖xX*]*\d+ problems?\b`)
)

type ruleAgg struct {
	rule     string
	severity string // "error" if any occurrence is an error, else "warning"
	count    int
	example  string // first "file:line:col message" seen for this rule
}

// Filter compacts eslint stylish output into a per-rule rollup:
//
//	<count>x <severity> <rule>  — <file:line:col first message>
//
// sorted by count descending (ties broken by rule id), followed by the
// original summary line. Unrecognized input (no problem lines and no summary)
// passes through unchanged.
func Filter(raw string) string {
	lines := strings.Split(raw, "\n")

	var curFile string
	byRule := map[string]*ruleAgg{}
	var order []string // rule ids in first-seen order, for stable ties
	summary := ""
	sawProblem := false

	for _, line := range lines {
		if summaryRe.MatchString(strings.TrimSpace(line)) {
			summary = strings.TrimSpace(line)
			continue
		}
		if m := problemRe.FindStringSubmatch(line); m != nil {
			lineNo, col, sev, msg, rule := m[1], m[2], m[3], strings.TrimSpace(m[4]), m[5]
			if rule == "" {
				// No rule id (e.g. a parse error): bucket under a sentinel so
				// it is still counted rather than dropped.
				rule = "(no rule)"
			}
			sawProblem = true
			agg, ok := byRule[rule]
			if !ok {
				agg = &ruleAgg{rule: rule, severity: sev}
				byRule[rule] = agg
				order = append(order, rule)
				loc := curFile
				if loc == "" {
					loc = "?"
				}
				agg.example = fmt.Sprintf("%s:%s:%s %s", loc, lineNo, col, msg)
			}
			agg.count++
			if sev == "error" {
				agg.severity = "error"
			}
			continue
		}
		// A non-empty, non-indented, non-summary line is a file header.
		if t := strings.TrimSpace(line); t != "" && !strings.HasPrefix(line, " ") {
			curFile = t
		}
	}

	if !sawProblem && summary == "" {
		return raw // unrecognized shape: pass through unchanged
	}

	firstSeen := make(map[string]int, len(order))
	for i, r := range order {
		firstSeen[r] = i
	}
	aggs := make([]*ruleAgg, 0, len(byRule))
	for _, a := range byRule {
		aggs = append(aggs, a)
	}
	sort.Slice(aggs, func(i, j int) bool {
		if aggs[i].count != aggs[j].count {
			return aggs[i].count > aggs[j].count
		}
		return firstSeen[aggs[i].rule] < firstSeen[aggs[j].rule]
	})

	var out []string
	for _, a := range aggs {
		out = append(out, fmt.Sprintf("%dx %s %s  — %s", a.count, a.severity, a.rule, a.example))
	}
	if summary != "" {
		out = append(out, summary)
	}
	return strings.Join(out, "\n")
}
