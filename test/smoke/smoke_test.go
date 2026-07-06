//go:build smoke

// Package smoke exercises vtk end-to-end through the real binary and real
// wrapped commands. Run with:
//
//	go test -tags smoke ./test/smoke/
//
// Requires git on PATH. Set VTK_SMOKE_BIN to an existing vtk binary to skip
// the build step (useful where the default build dir is not executable).
package smoke

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"
)

var okRe = regexp.MustCompile(`(?m)^OK ([0-9a-f]{4})$`)

// harness holds the built binary, an isolated spool home, and a scratch repo.
type harness struct {
	bin   string // vtk binary path
	home  string // isolated cache home (spool + gap log live under here)
	repo  string // scratch git repo
	other string // empty non-repo dir
}

// run executes the vtk binary with an isolated cache dir. Returns combined
// stdout, stderr, and exit code.
func (h *harness) run(t *testing.T, dir string, args ...string) (stdout, stderr string, code int) {
	t.Helper()
	cmd := exec.Command(h.bin, args...)
	cmd.Dir = dir
	// os.UserCacheDir reads LocalAppData on Windows, XDG_CACHE_HOME elsewhere.
	cmd.Env = append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
	)
	var out, errb strings.Builder
	cmd.Stdout = &out
	cmd.Stderr = &errb
	err := cmd.Run()
	code = 0
	if err != nil {
		if ee, ok := err.(*exec.ExitError); ok {
			code = ee.ExitCode()
		} else {
			code = 127
		}
	}
	return out.String(), errb.String(), code
}

func (h *harness) spoolDir() string { return filepath.Join(h.home, "vtk", "spool") }

func git(t *testing.T, dir string, args ...string) string {
	t.Helper()
	cmd := exec.Command("git", args...)
	cmd.Dir = dir
	out, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("git %v: %v\n%s", args, err, out)
	}
	return string(out)
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	if _, err := exec.LookPath("git"); err != nil {
		t.Skip("git not on PATH")
	}
	bin := os.Getenv("VTK_SMOKE_BIN")
	if bin == "" {
		bin = filepath.Join(t.TempDir(), "vtk")
		if runtime.GOOS == "windows" {
			bin += ".exe"
		}
		_, self, _, _ := runtime.Caller(0)
		root := filepath.Dir(filepath.Dir(filepath.Dir(self)))
		cmd := exec.Command("go", "build", "-o", bin, "./cmd/vtk")
		cmd.Dir = root
		if out, err := cmd.CombinedOutput(); err != nil {
			t.Fatalf("go build: %v\n%s", err, out)
		}
	}
	h := &harness{bin: bin, home: t.TempDir(), repo: t.TempDir(), other: t.TempDir()}
	git(t, h.repo, "init", "-q", "-b", "main")
	git(t, h.repo, "config", "user.email", "smoke@vtk")
	git(t, h.repo, "config", "user.name", "smoke")
	for i := 1; i <= 3; i++ {
		name := fmt.Sprintf("file%d.txt", i)
		if err := os.WriteFile(filepath.Join(h.repo, name), []byte(fmt.Sprintf("line %d content\n", i)), 0o644); err != nil {
			t.Fatal(err)
		}
		git(t, h.repo, "add", ".")
		git(t, h.repo, "commit", "-q", "-m", fmt.Sprintf("commit %d: add %s", i, name))
	}
	return h
}

func mustOKID(t *testing.T, out string) string {
	t.Helper()
	m := okRe.FindStringSubmatch(out)
	if m == nil {
		t.Fatalf("no OK <id> line in output:\n%s", out)
	}
	return m[1]
}

// runNull is like run but sends the child's stdout to the OS null device
// (NUL on Windows, /dev/null elsewhere) — a char device, not a pipe. This is
// the #6 regression shape: char-device sinks must not be mistaken for a TTY.
func (h *harness) runNull(t *testing.T, dir string, args ...string) (stderr string, code int) {
	t.Helper()
	null, err := os.OpenFile(os.DevNull, os.O_WRONLY, 0)
	if err != nil {
		t.Fatalf("open %s: %v", os.DevNull, err)
	}
	defer null.Close()
	cmd := exec.Command(h.bin, args...)
	cmd.Dir = dir
	cmd.Env = append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
	)
	var errb strings.Builder
	cmd.Stdout = null
	cmd.Stderr = &errb
	runErr := cmd.Run()
	code = 0
	if runErr != nil {
		if ee, ok := runErr.(*exec.ExitError); ok {
			code = ee.ExitCode()
		} else {
			code = 127
		}
	}
	return errb.String(), code
}

func (h *harness) invocationLog(t *testing.T) string {
	t.Helper()
	meta, err := os.ReadFile(filepath.Join(h.home, "vtk", "invocations.jsonl"))
	if err != nil {
		t.Fatalf("read invocation log: %v", err)
	}
	return string(meta)
}

// TestSmokeNullRedirect exercises #6 through the real binary: stdout
// redirected to the null char device must take the filtered path, not the
// interactive TTY bypass, and only genuine registry misses may appear in
// `vtk gaps`.
func TestSmokeNullRedirect(t *testing.T) {
	h := newHarness(t)
	if err := os.WriteFile(filepath.Join(h.repo, "file1.txt"), []byte("line 1 content\ndirty\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	t.Run("covered command redirected to null device is filtered", func(t *testing.T) {
		if _, code := h.runNull(t, h.repo, "git", "status"); code != 0 {
			t.Fatalf("exit %d, want 0", code)
		}
		log := h.invocationLog(t)
		if !strings.Contains(log, `"cmd":"git status"`) || !strings.Contains(log, `"filtered":true`) {
			t.Errorf("expected filtered git status entry, got:\n%s", log)
		}
		if strings.Contains(log, `"tty":true`) {
			t.Errorf("null-device redirect misdetected as tty:\n%s", log)
		}
		gaps, _, code := h.run(t, h.repo, "gaps")
		if code != 0 {
			t.Fatalf("gaps exit %d, want 0", code)
		}
		if strings.Contains(gaps, "git") {
			t.Errorf("filtered invocation polluted gaps:\n%s", gaps)
		}
	})

	t.Run("uncovered command redirected to null device is a no-filter gap", func(t *testing.T) {
		if _, code := h.runNull(t, h.repo, "git", "rev-parse", "HEAD"); code != 0 {
			t.Fatalf("exit %d, want 0", code)
		}
		if log := h.invocationLog(t); !strings.Contains(log, `"reason":"no-filter"`) {
			t.Errorf("expected no-filter reason entry, got:\n%s", log)
		}
		gaps, _, code := h.run(t, h.repo, "gaps")
		if code != 0 {
			t.Fatalf("gaps exit %d, want 0", code)
		}
		if !strings.Contains(gaps, "git") {
			t.Errorf("genuine gap missing from gaps:\n%s", gaps)
		}
	})

	t.Run("nonzero exit keeps parity and is excluded from gaps", func(t *testing.T) {
		rawCmd := exec.Command("git", "diff", "vtk-no-such-ref")
		rawCmd.Dir = h.repo
		rawCode := 0
		if err := rawCmd.Run(); err != nil {
			if ee, ok := err.(*exec.ExitError); ok {
				rawCode = ee.ExitCode()
			}
		}
		if rawCode == 0 {
			t.Fatal("expected raw git diff on a bad ref to fail")
		}
		stderr, code := h.runNull(t, h.repo, "git", "diff", "vtk-no-such-ref")
		if code != rawCode {
			t.Errorf("exit %d, want %d (parity)", code, rawCode)
		}
		if !strings.Contains(stderr, "vtk-no-such-ref") {
			t.Errorf("failure stderr not passed through raw:\n%s", stderr)
		}
		if log := h.invocationLog(t); !strings.Contains(log, `"reason":"nonzero-exit"`) {
			t.Errorf("expected nonzero-exit reason entry, got:\n%s", log)
		}
		gaps, _, gcode := h.run(t, h.repo, "gaps")
		if gcode != 0 {
			t.Fatalf("gaps exit %d, want 0", gcode)
		}
		// The only git entries eligible for gaps remain the rev-parse miss.
		if got := strings.Count(gaps, "git"); got != 1 {
			t.Errorf("gaps git family lines = %d, want 1 (no-filter only):\n%s", got, gaps)
		}
	})
}

func TestSmoke(t *testing.T) {
	h := newHarness(t)

	// Dirty the tree: one modification, one untracked file.
	if err := os.WriteFile(filepath.Join(h.repo, "file1.txt"), []byte("line 1 content\ndirty\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(h.repo, "newfile.txt"), []byte("untracked\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	var statusID string
	t.Run("filtered status elides and emits OK", func(t *testing.T) {
		raw := git(t, h.repo, "status")
		out, errS, code := h.run(t, h.repo, "git", "status")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		statusID = mustOKID(t, out)
		if len(out) >= len(raw) {
			t.Errorf("compact output (%d bytes) not smaller than raw (%d bytes)", len(out), len(raw))
		}
		for _, want := range []string{"file1.txt", "newfile.txt"} {
			if !strings.Contains(out, want) {
				t.Errorf("compact status missing %q:\n%s", want, out)
			}
		}
	})

	t.Run("show recovers raw with provenance header", func(t *testing.T) {
		out, _, code := h.run(t, h.repo, "show", statusID)
		if code != 0 {
			t.Fatalf("show exit %d, want 0", code)
		}
		if !strings.Contains(out, "# cmd: git status") {
			t.Errorf("missing provenance header:\n%s", out)
		}
		if !strings.Contains(out, "modified:") {
			t.Errorf("spooled content missing raw status detail:\n%s", out)
		}
	})

	t.Run("show --grep filters lines", func(t *testing.T) {
		out, _, code := h.run(t, h.repo, "show", statusID, "--grep", "file1")
		if code != 0 {
			t.Fatalf("exit %d, want 0", code)
		}
		for _, line := range strings.Split(strings.TrimRight(out, "\n"), "\n") {
			if !strings.Contains(line, "file1") {
				t.Errorf("non-matching line in grep output: %q", line)
			}
		}
	})

	t.Run("show missing id exits 1", func(t *testing.T) {
		if _, _, code := h.run(t, h.repo, "show", "dead"); code != 1 {
			t.Errorf("exit %d, want 1", code)
		}
	})

	t.Run("exit-code parity on failure with identical output", func(t *testing.T) {
		rawCmd := exec.Command("git", "status")
		rawCmd.Dir = h.other
		rawOut, rawErr := rawCmd.CombinedOutput()
		rawCode := 0
		if ee, ok := rawErr.(*exec.ExitError); ok {
			rawCode = ee.ExitCode()
		}
		if rawCode == 0 {
			t.Fatal("expected raw git status to fail outside a repo")
		}
		out, errS, code := h.run(t, h.other, "git", "status")
		if code != rawCode {
			t.Errorf("exit %d, want %d (parity)", code, rawCode)
		}
		if out+errS != string(rawOut) {
			t.Errorf("failure output altered:\nraw: %q\nvtk: %q", rawOut, out+errS)
		}
	})

	t.Run("exit-code 127 when command cannot run", func(t *testing.T) {
		if _, _, code := h.run(t, h.repo, "vtk-no-such-cmd-xyz"); code != 127 {
			t.Errorf("exit %d, want 127", code)
		}
	})

	t.Run("nothing elided emits no OK", func(t *testing.T) {
		git(t, h.repo, "checkout", "-q", "--", "file1.txt")
		if err := os.Remove(filepath.Join(h.repo, "newfile.txt")); err != nil {
			t.Fatal(err)
		}
		out, _, code := h.run(t, h.repo, "git", "diff") // clean tree: empty diff
		if code != 0 {
			t.Fatalf("exit %d, want 0", code)
		}
		if okRe.MatchString(out) || strings.TrimSpace(out) != "" {
			t.Errorf("empty diff should print nothing and no OK, got:\n%s", out)
		}
	})

	t.Run("passthrough is gap-logged without output content", func(t *testing.T) {
		out, _, code := h.run(t, h.repo, "git", "rev-parse", "HEAD")
		if code != 0 {
			t.Fatalf("exit %d, want 0", code)
		}
		head := strings.TrimSpace(out)
		gaps, _, code := h.run(t, h.repo, "gaps")
		if code != 0 {
			t.Fatalf("gaps exit %d, want 0", code)
		}
		if !strings.Contains(gaps, "git") {
			t.Errorf("gaps missing git family:\n%s", gaps)
		}
		meta, err := os.ReadFile(filepath.Join(h.home, "vtk", "invocations.jsonl"))
		if err != nil {
			t.Fatalf("read gap log: %v", err)
		}
		if strings.Contains(string(meta), head) {
			t.Error("gap log contains command output content (HEAD hash)")
		}
	})

	t.Run("command line secrets redacted in gap log", func(t *testing.T) {
		// The command need not succeed; the invocation is logged regardless.
		h.run(t, h.repo, "git", "vtk-smoke-noop", "AWS_SECRET_ACCESS_KEY=supersecret123")
		meta, err := os.ReadFile(filepath.Join(h.home, "vtk", "invocations.jsonl"))
		if err != nil {
			t.Fatalf("read gap log: %v", err)
		}
		if strings.Contains(string(meta), "supersecret123") {
			t.Error("secret leaked into gap log")
		}
		if !strings.Contains(string(meta), "AWS_SECRET_ACCESS_KEY=[REDACTED]") {
			t.Error("expected redaction marker in gap log")
		}
	})

	t.Run("spooled content is redacted", func(t *testing.T) {
		if err := os.WriteFile(filepath.Join(h.repo, "file2.txt"),
			[]byte("line 2 content\nAuthorization: Bearer sk-live-abc123\n"), 0o644); err != nil {
			t.Fatal(err)
		}
		out, _, code := h.run(t, h.repo, "git", "diff")
		if code != 0 {
			t.Fatalf("exit %d, want 0", code)
		}
		id := mustOKID(t, out)
		shown, _, code := h.run(t, h.repo, "show", id)
		if code != 0 {
			t.Fatalf("show exit %d, want 0", code)
		}
		if strings.Contains(shown, "sk-live-abc123") {
			t.Error("secret survived redaction in spool")
		}
		if !strings.Contains(shown, "Authorization: [REDACTED]") {
			t.Errorf("expected redaction marker in spooled diff:\n%s", shown)
		}
		git(t, h.repo, "checkout", "-q", "--", "file2.txt")
	})

	t.Run("TTL sweep removes expired entries", func(t *testing.T) {
		path := filepath.Join(h.spoolDir(), statusID+".txt")
		old := time.Now().Add(-2 * time.Hour)
		if err := os.Chtimes(path, old, old); err != nil {
			t.Fatalf("backdate %s: %v", path, err)
		}
		if _, _, code := h.run(t, h.repo, "git", "rev-parse", "HEAD"); code != 0 {
			t.Fatalf("sweep-trigger invocation failed: %d", code)
		}
		if _, err := os.Stat(path); !os.IsNotExist(err) {
			t.Errorf("expired entry still present: %v", err)
		}
		if _, _, code := h.run(t, h.repo, "show", statusID); code != 1 {
			t.Errorf("show of swept id: exit %d, want 1", code)
		}
	})

	t.Run("concurrent identical invocations both succeed", func(t *testing.T) {
		var wg sync.WaitGroup
		outs := make([]string, 2)
		codes := make([]int, 2)
		for i := 0; i < 2; i++ {
			wg.Add(1)
			go func(i int) {
				defer wg.Done()
				outs[i], _, codes[i] = h.run(t, h.repo, "git", "log")
			}(i)
		}
		wg.Wait()
		for i := 0; i < 2; i++ {
			if codes[i] != 0 {
				t.Errorf("run %d exit %d, want 0", i, codes[i])
			}
			mustOKID(t, outs[i])
		}
		if outs[0] != outs[1] {
			t.Errorf("concurrent outputs differ:\n%q\n%q", outs[0], outs[1])
		}
	})
}
