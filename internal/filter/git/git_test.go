package git

import (
	"flag"
	"os"
	"path/filepath"
	"testing"
)

// -update regenerates the .want.txt golden files from the current filter
// output. Review the diff before committing a regeneration.
var update = flag.Bool("update", false, "rewrite golden .want.txt files")

// Fixtures are real captured git output (see .raw.txt provenance in the
// issue/PR); goldens are the expected compact form. Each case asserts exact
// output plus a minimum measured savings ratio.
func TestFilters(t *testing.T) {
	cases := []struct {
		name       string
		filter     func(string) string
		minSavings float64 // fraction of raw bytes that must be elided
	}{
		{"status_dirty", Status, 0.75},
		{"status_clean", Status, 0.20},
		{"log_default", Log, 0.75},
		{"diff_two_files", Diff, 0.75},
		{"show_commit", Show, 0.80},
		{"add_crlf_warning", Add, 0.99},
		{"commit_multi", Commit, 0.40},
		{"push_new_branch", Push, 0.0},
		{"push_progress", Push, 0.60},
		{"pull_ff", Pull, 0.40},
		{"branch_list", Branch, 0.0},
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
			got := tc.filter(raw)

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

// Filters must pass unrecognized input through unchanged rather than
// mangling it (when in doubt, pass through).
func TestUnrecognizedInputPassesThrough(t *testing.T) {
	weird := "some output vtk has never seen\nsecond line\n"
	for name, f := range map[string]func(string) string{
		"Status": Status,
		"Log":    Log,
		"Diff":   Diff,
		"Show":   Show,
		"Commit": Commit,
	} {
		if got := f(weird); got != weird {
			t.Errorf("%s altered unrecognized input:\n%q", name, got)
		}
	}
}
