package mocha

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

// Fixtures are mocha spec-reporter output captured from real runs (mocha 10;
// see .raw.txt provenance in the issue/PR); goldens are the expected compact
// form. Each case asserts exact output plus a minimum measured savings ratio.
func TestFilter(t *testing.T) {
	cases := []struct {
		name       string
		minSavings float64 // fraction of raw bytes that must be elided
	}{
		{"pass", 0.85},    // green run collapses to a single summary line
		{"pending", 0.60}, // pass + pending; specs folded, counts kept
		{"fail", 0.20},    // failure detail is kept verbatim; only the tree folds
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

// The summary counts must survive compaction: an agent relies on "N passing"
// (and "M failing" when present) to decide whether to dig in.
func TestSummaryPreserved(t *testing.T) {
	rawB, err := os.ReadFile(filepath.Join("testdata", "fail.raw.txt"))
	if err != nil {
		t.Fatal(err)
	}
	got := Filter(string(rawB))
	if !strings.Contains(got, "5 passing (6ms)") {
		t.Errorf("passing summary dropped:\n%s", got)
	}
	if !strings.Contains(got, "3 failing") {
		t.Errorf("failing summary dropped:\n%s", got)
	}
}

// Failure detail — the assertion message and the trimmed stack frame — is kept
// verbatim: that is the signal an agent needs to fix the test.
func TestFailureDetailKept(t *testing.T) {
	rawB, err := os.ReadFile(filepath.Join("testdata", "fail.raw.txt"))
	if err != nil {
		t.Fatal(err)
	}
	got := Filter(string(rawB))
	for _, want := range []string{
		"1) cart", // failure block header
		"AssertionError [ERR_ASSERTION]: 12 == 11",          // assertion message
		"Error: gateway timeout",                            // thrown-error message
		"at Context.<anonymous> (test\\mixed.test.js:5:45)", // stack frame
	} {
		if !strings.Contains(got, want) {
			t.Errorf("failure detail %q dropped:\n%s", want, got)
		}
	}
}

// Passing spec lines are folded away: a green run keeps no "✔ <title>" lines.
func TestPassingSpecsFolded(t *testing.T) {
	rawB, err := os.ReadFile(filepath.Join("testdata", "pass.raw.txt"))
	if err != nil {
		t.Fatal(err)
	}
	got := Filter(string(rawB))
	if strings.Contains(got, "✔") {
		t.Errorf("passing spec lines not folded:\n%s", got)
	}
	if got != "7 passing (5ms)" {
		t.Errorf("green run should collapse to the summary line, got:\n%q", got)
	}
}

// Unrecognized input (no mocha summary) passes through unchanged.
func TestUnrecognizedInputPassesThrough(t *testing.T) {
	weird := "some output vtk has never seen\nsecond line\n"
	if got := Filter(weird); got != weird {
		t.Errorf("altered unrecognized input:\n%q", got)
	}
}

// Empty output is left untouched.
func TestEmptyInput(t *testing.T) {
	if got := Filter(""); got != "" {
		t.Errorf("altered empty input: %q", got)
	}
}
