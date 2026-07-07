//go:build smoke && !windows

package smoke

import (
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/creack/pty"
)

// TestSmokePTYBypass exercises the interactive TTY-bypass branch end-to-end
// through the real binary. Workers run headless, so this branch
// (isTTY(os.Stdout) true + a registry match -> passthrough with
// ReasonTTYBypass) had only ever been unit-tested; a real terminal is the only
// way to make term.IsTerminal report true on the child's stdout. We allocate a
// pseudo-terminal, attach the child's stdout to the slave, and assert the three
// AC properties (#20):
//
//	(a) output is unfiltered/uncaptured — the raw `git status` is emitted and
//	    no "OK <id>" elision line appears (the interactive path never spools),
//	(b) the invocation is logged with reason "tty-bypass" (post-#17 schema),
//	    tty:true, filtered:false,
//	(c) exit-code parity — the wrapped command's exit code is preserved.
//
// PTY-only, so the file is Windows-excluded via build tag (no PTY there).
func TestSmokePTYBypass(t *testing.T) {
	h := newHarness(t)

	// Dirty the tree so a real `git status` has content to (not) filter.
	if err := os.WriteFile(filepath.Join(h.repo, "file1.txt"), []byte("line 1 content\ndirty\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(h.repo, "newfile.txt"), []byte("untracked\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	out, code := h.runPTY(t, h.repo, "git", "status")

	// (c) exit-code parity: a clean `git status` on a dirty repo exits 0.
	if code != 0 {
		t.Fatalf("exit %d, want 0 (parity)", code)
	}

	// (a) unfiltered/uncaptured: the compact "OK <id>" line only appears on the
	// filtered path; the interactive bypass must emit the raw status verbatim.
	if okRe.MatchString(out) {
		t.Errorf("tty-bypass emitted an OK <id> elision line (output was captured/filtered):\n%s", out)
	}
	for _, want := range []string{"file1.txt", "newfile.txt"} {
		if !strings.Contains(out, want) {
			t.Errorf("raw status missing %q under tty bypass:\n%s", want, out)
		}
	}

	// (b) metadata: the invocation is logged as a tty bypass, not a gap.
	log := h.invocationLog(t)
	if !strings.Contains(log, `"reason":"tty-bypass"`) {
		t.Errorf("expected reason \"tty-bypass\" in invocation log, got:\n%s", log)
	}
	if !strings.Contains(log, `"tty":true`) {
		t.Errorf("expected tty:true in invocation log, got:\n%s", log)
	}
	if strings.Contains(log, `"filtered":true`) {
		t.Errorf("tty bypass must not be logged as filtered:\n%s", log)
	}

	// A tty bypass is covered-but-unfiltered, not a coverage gap: `vtk gaps`
	// must not list the git family from this run.
	gaps, _, gcode := h.run(t, h.repo, "gaps")
	if gcode != 0 {
		t.Fatalf("gaps exit %d, want 0", gcode)
	}
	if strings.Contains(gaps, "git") {
		t.Errorf("tty-bypassed covered command polluted gaps:\n%s", gaps)
	}
}

// runPTY runs the vtk binary with its stdout attached to a pseudo-terminal
// slave, so term.IsTerminal(stdout) reports true inside the child and the
// interactive bypass path is taken. It mirrors harness.run/runNull but for the
// "real terminal" sink. Returns the combined PTY output and the exit code.
func (h *harness) runPTY(t *testing.T, dir string, args ...string) (output string, code int) {
	t.Helper()

	cmd := exec.Command(h.bin, args...)
	cmd.Dir = dir
	cmd.Env = append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
		// Keep git's own output deterministic and non-paged under a PTY.
		"GIT_PAGER=cat",
		"PAGER=cat",
		"TERM=dumb",
	)

	// pty.Start attaches stdin, stdout, AND stderr of the child to the pty
	// slave, giving the child a genuine controlling terminal on stdout.
	ptmx, err := pty.Start(cmd)
	if err != nil {
		t.Fatalf("pty.Start: %v", err)
	}
	defer func() { _ = ptmx.Close() }()

	// Drain the master concurrently; on Linux, reads return EIO once the slave
	// is fully closed after the child exits — treat that as a clean EOF.
	type readResult struct {
		data []byte
		err  error
	}
	done := make(chan readResult, 1)
	go func() {
		data, err := io.ReadAll(ptmx)
		done <- readResult{data, err}
	}()

	runErr := cmd.Wait()
	code = 0
	if runErr != nil {
		if ee, ok := runErr.(*exec.ExitError); ok {
			code = ee.ExitCode()
		} else {
			code = 127
		}
	}
	// Closing the master unblocks the reader if the child left it open.
	_ = ptmx.Close()

	select {
	case r := <-done:
		output = string(r.data)
	case <-time.After(10 * time.Second):
		t.Fatal("timed out draining pty master")
	}
	return output, code
}
