package files

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

// Fixtures are real captured command output (ls/grep on the IsekaiOnline
// repo, find on this repo — see the issue thread for provenance); goldens are
// the expected compact form. Each case asserts exact output plus a minimum
// measured savings ratio.
func TestFilters(t *testing.T) {
	cases := []struct {
		name       string
		filter     func(string) string
		minSavings float64 // fraction of raw bytes that must be elided
	}{
		{"ls_serverjs", Ls, 0.60}, // 172-entry dir: cap + pack
		{"grep_rn", Grep, 0.25},   // 19 matches over 3 files: per-file cap
		{"grep_l", Grep, 0.60},    // 165-file -l list: cap + pack
		{"find_walk", Find, 0.10}, // 49 paths: cap barely engages
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

// Long-format ls output is not a plain name list: it must pass through
// unchanged rather than being packed into nonsense.
func TestLsLongFormatPassesThrough(t *testing.T) {
	rawB, err := os.ReadFile(filepath.Join("testdata", "ls_long.raw.txt"))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	raw := string(rawB)
	if got := Ls(raw); got != raw {
		t.Errorf("Ls altered long-format output:\n%q", got)
	}
}

// Empty output (e.g. ls of an empty dir, find with no hits) passes through.
func TestEmptyInputPassesThrough(t *testing.T) {
	for name, f := range map[string]func(string) string{
		"Ls": Ls, "Grep": Grep, "Find": Find,
	} {
		for _, raw := range []string{"", "\n"} {
			if got := f(raw); got != raw {
				t.Errorf("%s(%q) = %q, want unchanged", name, raw, got)
			}
		}
	}
}

// Grep keeps non-match lines (e.g. "Binary file X matches") verbatim when
// match lines are present.
func TestGrepKeepsUnrecognizedLines(t *testing.T) {
	raw := "a.go:1:x\nBinary file b.bin matches\n"
	got := Grep(raw)
	if !strings.Contains(got, "Binary file b.bin matches") {
		t.Errorf("Grep dropped a non-match line:\n%q", got)
	}
}

// Below the caps nothing is elided: every input line survives in the output
// (packed, but present), so no information is lost without a spool tail.
func TestUncappedListKeepsAllEntries(t *testing.T) {
	raw := "one.txt\ntwo.txt\nthree.txt\n"
	got := Ls(raw)
	for _, name := range []string{"one.txt", "two.txt", "three.txt"} {
		if !strings.Contains(got, name) {
			t.Errorf("Ls dropped %q under the cap:\n%q", name, got)
		}
	}
}
