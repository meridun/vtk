package eslint

import (
	"flag"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// -update regenerates the .want.txt golden files from the current filter
// output. Review the diff before committing a regeneration.
var update = flag.Bool("update", false, "rewrite golden .want.txt files")

// Fixtures are eslint stylish-formatter output (see .raw.txt provenance in the
// issue/PR); goldens are the expected compact form. Each case asserts exact
// output plus a minimum measured savings ratio.
func TestFilter(t *testing.T) {
	cases := []struct {
		name       string
		minSavings float64 // fraction of raw bytes that must be elided
	}{
		{"multi_rule", 0.30},
		{"big_report", 0.90}, // large real-world shape must clear the parity floor
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
			got := Filter(raw)

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

// The summary line and exit-relevant totals must survive compaction: an agent
// relies on "N problems (E errors, W warnings)" to decide whether to dig in.
func TestSummaryPreserved(t *testing.T) {
	rawB, err := os.ReadFile(filepath.Join("testdata", "multi_rule.raw.txt"))
	if err != nil {
		t.Fatal(err)
	}
	got := Filter(string(rawB))
	if !strings.Contains(got, "7 problems (5 errors, 2 warnings)") {
		t.Errorf("summary line dropped:\n%s", got)
	}
}

// Rules are rolled up: the compact form has one line per distinct rule id plus
// the summary — not one line per problem.
func TestPerRuleRollup(t *testing.T) {
	rawB, err := os.ReadFile(filepath.Join("testdata", "multi_rule.raw.txt"))
	if err != nil {
		t.Fatal(err)
	}
	got := Filter(string(rawB))
	lines := strings.Split(strings.TrimRight(got, "\n"), "\n")
	// 4 distinct rules (semi, no-undef, no-unused-vars, no-console) + summary.
	if len(lines) != 5 {
		t.Errorf("expected 4 rule lines + summary, got %d lines:\n%s", len(lines), got)
	}
	// Highest-count rule first: semi appears 3 times.
	if !strings.HasPrefix(lines[0], "3x error semi") {
		t.Errorf("expected semi rollup first, got %q", lines[0])
	}
}

// Unrecognized input (no problem lines, no summary) passes through unchanged.
func TestUnrecognizedInputPassesThrough(t *testing.T) {
	weird := "some output vtk has never seen\nsecond line\n"
	if got := Filter(weird); got != weird {
		t.Errorf("altered unrecognized input:\n%q", got)
	}
}

// Empty output (clean run: eslint prints nothing) is left untouched.
func TestEmptyInput(t *testing.T) {
	if got := Filter(""); got != "" {
		t.Errorf("altered empty input: %q", got)
	}
}
