//go:build smoke

package smoke

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// TestSmokeReservedMeta exercises #11 through the real binary: documented-but-
// unimplemented meta subcommands (docs/ToolCoverage.md, Meta row) must fail
// clearly with vtk's own usage-error exit code instead of falling through to
// exec's misleading "executable file not found" (127), and must leave no
// spool or gap-log side effects. Genuinely unknown words keep the 127 path.
func TestSmokeReservedMeta(t *testing.T) {
	h := newHarness(t)

	for _, word := range []string{"gain", "proxy"} {
		t.Run(word+" fails clearly with exit 2", func(t *testing.T) {
			out, stderr, code := h.run(t, h.repo, word)
			if code != 2 {
				t.Errorf("exit %d, want 2", code)
			}
			if !strings.Contains(stderr, "not implemented yet") {
				t.Errorf("stderr missing clear message: %q", stderr)
			}
			if strings.Contains(stderr, "executable file not found") {
				t.Errorf("misleading exec error leaked: %q", stderr)
			}
			if out != "" {
				t.Errorf("unexpected stdout: %q", out)
			}
		})
	}

	t.Run("gain with args still reserved", func(t *testing.T) {
		if _, _, code := h.run(t, h.repo, "gain", "--since", "7d"); code != 2 {
			t.Errorf("exit %d, want 2", code)
		}
	})

	t.Run("reserved words leave no spool or gap-log trace", func(t *testing.T) {
		if _, err := os.Stat(filepath.Join(h.home, "vtk", "invocations.jsonl")); !os.IsNotExist(err) {
			t.Errorf("invocation log exists after reserved-word-only runs: %v", err)
		}
		entries, err := os.ReadDir(h.spoolDir())
		if err == nil && len(entries) != 0 {
			t.Errorf("spool has %d entries, want 0", len(entries))
		}
	})

	t.Run("unknown non-reserved word keeps exec 127 path", func(t *testing.T) {
		if _, _, code := h.run(t, h.repo, "vtk-not-reserved-xyz"); code != 127 {
			t.Errorf("exit %d, want 127", code)
		}
	})

	t.Run("shipped subcommands unaffected", func(t *testing.T) {
		if _, stderr, code := h.run(t, h.repo, "git", "status"); code != 0 {
			t.Fatalf("git status exit %d, want 0; stderr: %s", code, stderr)
		}
		if _, _, code := h.run(t, h.repo, "gaps"); code != 0 {
			t.Errorf("gaps exit %d, want 0", code)
		}
	})
}
