package dbmate

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

// Fixtures are real captured dbmate 2.27.0 (sqlite) output; goldens are the
// expected compact form. Each case asserts exact output plus a minimum measured
// savings ratio.
func TestFilters(t *testing.T) {
	cases := []struct {
		name       string
		filter     func(string) string
		minSavings float64 // fraction of raw bytes that must be elided
	}{
		{"status_mostly_applied", Status, 0.55},
		{"status_all_pending", Status, 0.0},
		{"up_multi", Migrate, 0.40},
		{"rollback_one", Migrate, 0.40},
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

// TestPassthrough asserts unrecognized shapes degrade to raw (invariant:
// filter failure/no-match returns input unchanged).
func TestPassthrough(t *testing.T) {
	cases := []struct {
		name   string
		filter func(string) string
		raw    string
	}{
		{"status_garbage", Status, "some unrelated output\nwith no status entries\n"},
		{"migrate_noop", Migrate, "\n\n"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := tc.filter(tc.raw); got != tc.raw {
				t.Errorf("expected raw passthrough, got:\n%s", got)
			}
		})
	}
}

// TestMigrateKeepsUnknown asserts Migrate never drops non-progress content:
// unrecognized lines survive verbatim so a filter miss cannot lose output.
func TestMigrateKeepsUnknown(t *testing.T) {
	raw := "unexpected output\nwith no result lines\n"
	got := Migrate(raw)
	for _, want := range []string{"unexpected output", "with no result lines"} {
		if !strings.Contains(got, want) {
			t.Errorf("dropped %q:\n%s", want, got)
		}
	}
}

// TestErrorKept asserts dbmate error lines survive the Migrate filter even
// though they arrive alongside progress chatter.
func TestErrorKept(t *testing.T) {
	raw := "Creating: ./test.sqlite3\n" +
		"Applying: 20260101000001_good.sql\n" +
		"Applied: 20260101000001_good.sql in 2.0008ms\n" +
		"Applying: 20260101000002_bad.sql\n" +
		"Applied: 20260101000002_bad.sql in 0s\n" +
		"Error: near \")\": syntax error\n"
	got := Migrate(raw)
	if !strings.Contains(got, "Error: near") {
		t.Errorf("error line dropped:\n%s", got)
	}
	if strings.Contains(got, "Creating:") || strings.Contains(got, "Applying:") {
		t.Errorf("progress chatter kept:\n%s", got)
	}
}
