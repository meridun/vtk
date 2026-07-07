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

	for _, word := range []string{"proxy"} {
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

	// gain graduated from reserved stub to an implemented reporter (#14): it
	// now routes to cmdGain, exiting 0 (like show/gaps) instead of the reserved
	// guard's 2, and aggregates the invocation log into a cumulative-savings
	// roll-up. Earlier subtests share this harness's isolated home and have
	// already logged wrapped-command invocations, so gain reports the roll-up
	// (not the empty-log line). Populate one more to be self-contained.
	t.Run("gain reports cumulative savings", func(t *testing.T) {
		h.run(t, h.repo, "git", "status") // ensure at least one logged invocation
		out, _, code := h.run(t, h.repo, "gain")
		if code != 0 {
			t.Fatalf("gain exit %d, want 0", code)
		}
		if !strings.Contains(out, "cumulative savings:") {
			t.Errorf("gain missing cumulative roll-up: %q", out)
		}
	})
}
