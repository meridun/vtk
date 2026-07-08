package main

// vtk gaps --file-issues — turn recurring passthrough families surfaced by the
// gap log into stage:intake filter issues, so coverage becomes a tracked queue
// rather than a number nobody reads (#34). Filing is explicit and user-invoked:
// it shells to `gh` (outside the no-network non-goal, which binds only the wrap
// path and telemetry storage — human ruling on #34), is dry-run by default, and
// dedupes against already-open Filter issues so re-running never refiles.

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"strings"

	"github.com/meridun/vtk/internal/spool"
)

// Filing thresholds. Defaults are the intended everyday floor; the hard floors
// clamp over-eager overrides so filing can never spam trivial gaps (#34).
const (
	defaultMinBytes int64 = 51200 // 50 KiB cumulative raw output per family
	defaultMinCalls       = 3
	floorMinBytes   int64 = 4096
	floorMinCalls         = 2
)

// filterIssueTitlePrefix is both the title stem for filed issues and the token
// dedupe keys on when scanning existing open issues.
const filterIssueTitlePrefix = "Filter: "

// fileIssuesOpts holds the parsed `vtk gaps --file-issues` options.
type fileIssuesOpts struct {
	yes      bool
	minBytes int64
	minCalls int
}

// cmdGapsFileIssues selects over-threshold gap families, drops any that already
// have an open Filter issue, and either prints the plan (dry-run) or files each
// via `gh issue create`. Returns nonzero only on a hard error (bad metadata,
// unreachable gh, or a failed create) — an empty candidate set is success.
func cmdGapsFileIssues(st *spool.Store, o fileIssuesOpts) int {
	candidates, err := st.FileIssueGaps(o.minBytes, o.minCalls)
	if err != nil {
		fmt.Fprintf(os.Stderr, "vtk: %v\n", err)
		return 1
	}
	if len(candidates) == 0 {
		fmt.Printf("no gap families over threshold (min-bytes=%d, min-calls=%d)\n", o.minBytes, o.minCalls)
		return 0
	}

	existing, err := ghExistingFilterFamilies()
	if err != nil {
		// Can't dedupe → refuse to file rather than risk duplicate issues.
		fmt.Fprintf(os.Stderr, "vtk gaps: cannot list existing issues (%v); refusing to file\n", err)
		return 1
	}

	toFile, skipped := planFilterIssues(candidates, existing)
	for _, fam := range skipped {
		fmt.Printf("skip %s — open %s%s issue already exists\n", fam, filterIssueTitlePrefix, fam)
	}
	if len(toFile) == 0 {
		fmt.Println("nothing to file (all candidates already have open issues)")
		return 0
	}

	if !o.yes {
		fmt.Printf("would file %d intake issue(s) (dry-run; pass --yes to create):\n", len(toFile))
		for _, g := range toFile {
			fmt.Printf("  %s  [%d calls, %d raw bytes]\n", filterIssueTitle(g.Family), g.Calls, g.RawBytes)
		}
		return 0
	}

	rc := 0
	for _, g := range toFile {
		url, err := ghCreateFilterIssue(filterIssueTitle(g.Family), filterIssueBody(g, o.minBytes, o.minCalls))
		if err != nil {
			fmt.Fprintf(os.Stderr, "vtk gaps: failed to file %s: %v\n", g.Family, err)
			rc = 1
			continue
		}
		fmt.Printf("filed %s — %s\n", g.Family, url)
	}
	return rc
}

// planFilterIssues splits candidate families into those to file and those
// skipped because an open Filter issue already exists (dedupe → idempotent).
// Pure: no I/O, so the selection is unit-testable.
func planFilterIssues(candidates []spool.GapSummary, existing map[string]bool) (toFile []spool.GapSummary, skipped []string) {
	for _, g := range candidates {
		if existing[g.Family] {
			skipped = append(skipped, g.Family)
			continue
		}
		toFile = append(toFile, g)
	}
	return toFile, skipped
}

// filterIssueTitle is the issue title for a gap family. The prefix is fixed so
// parseExistingFilterFamilies can recover the family for dedupe.
func filterIssueTitle(family string) string {
	return filterIssueTitlePrefix + family + " (auto-filed from vtk gaps)"
}

// filterIssueBody renders the intake issue body from metadata only: family name
// plus call/byte counts (invariant 3 — never output content).
func filterIssueBody(g spool.GapSummary, minBytes int64, minCalls int) string {
	var b strings.Builder
	fmt.Fprintf(&b, "Auto-filed by `vtk gaps --file-issues`: the `%s` command family is passing through unfiltered at meaningful volume.\n\n", g.Family)
	b.WriteString("## Measured gap\n\n")
	fmt.Fprintf(&b, "- Family: `%s`\n", g.Family)
	fmt.Fprintf(&b, "- Passthrough calls: %d\n", g.Calls)
	fmt.Fprintf(&b, "- Cumulative raw bytes: %d\n", g.RawBytes)
	fmt.Fprintf(&b, "- Threshold at filing: min-bytes=%d, min-calls=%d\n\n", minBytes, minCalls)
	b.WriteString("Machine-readable (`--json` &c.) invocations are excluded from this measurement — they are structurally uncompressable, not a filter defect.\n\n")
	b.WriteString("## Acceptance criteria\n\n")
	fmt.Fprintf(&b, "- A `%s` filter: pure function, raw output in → compact out, fixture-tested with a measured-savings assertion.\n", g.Family)
	b.WriteString("- Exit-code parity preserved; filter failure degrades to raw passthrough; no output content in gap/stats metadata.\n")
	return b.String()
}

// parseExistingFilterFamilies extracts the gap family from each open-issue title
// of the form `Filter: <family> ...`, returning a set for dedupe. Pure.
func parseExistingFilterFamilies(titles []string) map[string]bool {
	out := make(map[string]bool)
	for _, t := range titles {
		rest, ok := strings.CutPrefix(t, filterIssueTitlePrefix)
		if !ok {
			continue
		}
		rest = strings.TrimSpace(rest)
		// Family is the first whitespace- or paren-delimited token.
		fam := rest
		if i := strings.IndexAny(rest, " ("); i >= 0 {
			fam = rest[:i]
		}
		if fam != "" {
			out[fam] = true
		}
	}
	return out
}

// ghExistingFilterFamilies lists open issues and returns the set of families
// that already have a Filter issue. The `gh` shell-out is kept thin (mirrors
// install.go's exec glue); the parsing is delegated to the pure helper.
func ghExistingFilterFamilies() (map[string]bool, error) {
	out, err := exec.Command("gh", "issue", "list", "--state", "open", "--limit", "500", "--json", "title").Output()
	if err != nil {
		return nil, ghError(err)
	}
	var issues []struct {
		Title string `json:"title"`
	}
	if err := json.Unmarshal(out, &issues); err != nil {
		return nil, fmt.Errorf("parsing gh output: %w", err)
	}
	titles := make([]string, len(issues))
	for i, is := range issues {
		titles[i] = is.Title
	}
	return parseExistingFilterFamilies(titles), nil
}

// ghCreateFilterIssue files one stage:intake filter issue and returns the issue
// URL gh prints on stdout.
func ghCreateFilterIssue(title, body string) (string, error) {
	out, err := exec.Command("gh", "issue", "create",
		"--title", title,
		"--body", body,
		"--label", "stage:intake",
		"--label", "enhancement",
	).Output()
	if err != nil {
		return "", ghError(err)
	}
	return strings.TrimSpace(string(out)), nil
}

// ghError enriches an exec error with gh's stderr when available, so failures
// (not logged in, no repo, missing label) are legible.
func ghError(err error) error {
	if ee, ok := err.(*exec.ExitError); ok && len(ee.Stderr) > 0 {
		return fmt.Errorf("%s", strings.TrimSpace(string(ee.Stderr)))
	}
	return err
}
