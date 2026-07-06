package gh

import (
	"flag"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// -update regenerates the .want.txt golden files from the current filter
// output. Review the diff before committing a regeneration.
var update = flag.Bool("update", false, "rewrite golden .want.txt files")

// ref is a fixed clock for deterministic relative-time goldens; it sits just
// after the newest fixture timestamp (2026-07-06) so ages stay stable.
var ref = time.Date(2026, 7, 6, 21, 0, 0, 0, time.UTC)

// Fixtures are real captured `gh` output (see .raw.txt provenance in the
// issue/PR); goldens are the expected compact form. Each case asserts exact
// output plus a minimum measured savings ratio.
func TestFilters(t *testing.T) {
	cases := []struct {
		name       string
		filter     func(string, time.Time) string
		minSavings float64
	}{
		{"issue_list", issueListAt, 0.30},
		{"pr_list", prListAt, 0.40},
		{"run_list", runListAt, 0.45},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			rawPath := filepath.Join("testdata", tc.name+".raw.txt")
			wantPath := filepath.Join("testdata", tc.name+".want.txt")
			rawB, err := os.ReadFile(rawPath)
			if err != nil {
				t.Fatalf("read fixture: %v", err)
			}
			raw := string(rawB)
			got := tc.filter(raw, ref)

			if *update {
				if err := os.WriteFile(wantPath, []byte(got), 0o644); err != nil {
					t.Fatalf("update golden: %v", err)
				}
			}
			wantB, err := os.ReadFile(wantPath)
			if err != nil {
				t.Fatalf("read golden (run with -update to create): %v", err)
			}
			if got != string(wantB) {
				t.Errorf("output mismatch\n--- got ---\n%s\n--- want ---\n%s", got, wantB)
			}

			if len(raw) == 0 {
				t.Fatal("empty fixture")
			}
			savings := 1 - float64(len(got))/float64(len(raw))
			t.Logf("savings: %.0f%% (%d -> %d bytes)", savings*100, len(raw), len(got))
			if savings < tc.minSavings {
				t.Errorf("savings %.2f below minimum %.2f", savings, tc.minSavings)
			}
		})
	}
}

// JSON payloads (`gh --json ...`) must pass through structurally intact — the
// filter never mangles JSON output (Architecture invariant: --json stays whole).
func TestJSONPassesThrough(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join("testdata", "json_passthrough.raw.txt"))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	for name, f := range map[string]func(string) string{
		"IssueList": IssueList,
		"PrList":    PrList,
		"RunList":   RunList,
	} {
		if got := f(string(raw)); got != string(raw) {
			t.Errorf("%s altered JSON input:\n%q", name, got)
		}
	}
}

// Unrecognized shapes (no tab rows, or view-style output on the same registry
// key) pass through unchanged rather than being mangled.
func TestUnrecognizedInputPassesThrough(t *testing.T) {
	cases := map[string]string{
		"no_tabs":      "some output vtk has never seen\nsecond line\n",
		"issue_view":   "title:\tFix the bug\nstate:\tOPEN\nnumber:\t9\n",
		"empty":        "",
		"non_num_lead": "notanumber\tOPEN\tTitle\tlabel\t2026-07-06T18:00:00Z\n",
	}
	filters := map[string]func(string) string{
		"IssueList": IssueList,
		"PrList":    PrList,
		"RunList":   RunList,
	}
	for cname, in := range cases {
		for fname, f := range filters {
			if got := f(in); got != in {
				t.Errorf("%s altered unrecognized input %q:\n%q", fname, cname, got)
			}
		}
	}
}

// rel renders compact relative ages; unparseable timestamps render empty.
func TestRel(t *testing.T) {
	base := time.Date(2026, 7, 6, 21, 0, 0, 0, time.UTC)
	cases := []struct {
		ts   string
		want string
	}{
		{"2026-07-06T20:59:30Z", "now"},
		{"2026-07-06T20:30:00Z", "30m"},
		{"2026-07-06T18:00:00Z", "3h"},
		{"2026-07-04T21:00:00Z", "2d"},
		{"2026-05-06T21:00:00Z", "2mo"},
		{"2024-07-06T21:00:00Z", "2y"},
		{"not-a-timestamp", ""},
		{"2027-01-01T00:00:00Z", "now"}, // future clamps to now
	}
	for _, tc := range cases {
		if got := rel(tc.ts, base); got != tc.want {
			t.Errorf("rel(%q) = %q, want %q", tc.ts, got, tc.want)
		}
	}
}
