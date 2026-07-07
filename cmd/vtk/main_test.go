package main

import "testing"

// TestRunReservedMeta verifies that documented-but-unimplemented meta
// subcommands are guarded before exec fallthrough (#11): each returns vtk's
// usage-error exit code instead of exec's misleading 127. The guard sits
// before spool.Open, so these calls have no side effects and spawn nothing.
func TestRunReservedMeta(t *testing.T) {
	tests := []struct {
		name string
		args []string
		want int
	}{
		{"proxy reserved", []string{"proxy"}, 2},
		{"proxy with args still reserved", []string{"proxy", "--since", "7d"}, 2},
		{"no args is usage error", []string{}, 2},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := run(tt.args); got != tt.want {
				t.Errorf("run(%v) = %d, want %d", tt.args, got, tt.want)
			}
		})
	}
}

// TestRunGainImplemented confirms gain has graduated from reserved stub to a
// real meta subcommand (#14): it routes to cmdGain rather than the reserved
// guard, so it exits 0 (like show/gaps) — never the usage-error 2 the reserved
// path returns. cmdGain is a read-only reporter and returns 0 whether the log
// is empty ("no invocations logged") or populated. End-to-end coverage of the
// aggregation lives in the isolated smoke harness (test/smoke).
func TestRunGainImplemented(t *testing.T) {
	if got := run([]string{"gain"}); got != 0 {
		t.Errorf("run([gain]) = %d, want 0 (implemented, not reserved)", got)
	}
}

// TestReservedMetaSet pins the reserved set to the words documented as
// planned in docs/ToolCoverage.md; update both together. gain graduated to an
// implemented subcommand in #14, leaving proxy as the sole reserved word.
func TestReservedMetaSet(t *testing.T) {
	want := []string{"proxy"}
	if len(reservedMeta) != len(want) {
		t.Errorf("reservedMeta has %d entries, want %d (%v)", len(reservedMeta), len(want), want)
	}
	for _, w := range want {
		if !reservedMeta[w] {
			t.Errorf("reservedMeta missing %q", w)
		}
	}
	for _, shipped := range []string{"show", "gaps", "gain"} {
		if reservedMeta[shipped] {
			t.Errorf("shipped subcommand %q must not be reserved", shipped)
		}
	}
}
