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
		{"gain reserved", []string{"gain"}, 2},
		{"proxy reserved", []string{"proxy"}, 2},
		{"gain with args still reserved", []string{"gain", "--since", "7d"}, 2},
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

// TestReservedMetaSet pins the reserved set to the words documented as
// planned in docs/ToolCoverage.md; update both together.
func TestReservedMetaSet(t *testing.T) {
	want := []string{"gain", "proxy"}
	if len(reservedMeta) != len(want) {
		t.Errorf("reservedMeta has %d entries, want %d (%v)", len(reservedMeta), len(want), want)
	}
	for _, w := range want {
		if !reservedMeta[w] {
			t.Errorf("reservedMeta missing %q", w)
		}
	}
	for _, shipped := range []string{"show", "gaps"} {
		if reservedMeta[shipped] {
			t.Errorf("shipped subcommand %q must not be reserved", shipped)
		}
	}
}
