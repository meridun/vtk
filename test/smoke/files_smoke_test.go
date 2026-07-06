//go:build smoke

package smoke

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// requireOnPath skips the test when a wrapped tool is absent. The skip message
// names the tool so an environment gap is visible in verify reports rather
// than silent.
func requireOnPath(t *testing.T, name string) {
	t.Helper()
	if _, err := exec.LookPath(name); err != nil {
		t.Skipf("%s not on PATH; files-family smoke unverified in this environment", name)
	}
}

// requireGNUFind skips unless `find` on PATH is GNU findutils. On Windows a
// bare PATH may resolve to System32 find.exe (a different tool entirely);
// vtk would spawn that same binary, so there is nothing meaningful to smoke.
func requireGNUFind(t *testing.T) {
	t.Helper()
	out, err := exec.Command("find", "--version").CombinedOutput()
	if err != nil || !strings.Contains(string(out), "GNU findutils") {
		t.Skipf("find on PATH is not GNU findutils; find smoke unverified in this environment")
	}
}

// rawTool runs a command directly (no vtk) with stdout piped, returning
// stdout and exit code — the parity/savings baseline.
func rawTool(t *testing.T, dir, name string, args ...string) (string, int) {
	t.Helper()
	cmd := exec.Command(name, args...)
	cmd.Dir = dir
	var out strings.Builder
	cmd.Stdout = &out
	err := cmd.Run()
	code := 0
	if err != nil {
		if ee, ok := err.(*exec.ExitError); ok {
			code = ee.ExitCode()
		} else {
			t.Fatalf("raw %s %v: %v", name, args, err)
		}
	}
	return out.String(), code
}

// packRowsMaxWidth asserts every packed row (everything before the OK line,
// excluding the "(+N more)" tail) fits the 96-char pack width.
func packRowsMaxWidth(t *testing.T, out string) {
	t.Helper()
	for _, line := range strings.Split(strings.TrimRight(out, "\n"), "\n") {
		if okRe.MatchString(line) || strings.HasPrefix(line, "(+") {
			continue
		}
		if len(line) > 96 {
			t.Errorf("packed row exceeds 96 chars (%d): %q", len(line), line)
		}
	}
}

// TestSmokeFilesFamily exercises issue #10 through the real vtk binary and
// real ls/grep/find: listings are column-packed and capped with the full
// output recoverable via `OK <id>`, `ls -l` passes through untouched, grep
// match lines are capped per file, and grep's no-match exit 1 keeps parity.
func TestSmokeFilesFamily(t *testing.T) {
	requireOnPath(t, "ls")
	requireOnPath(t, "grep")
	h := newHarness(t)

	// listdir: 160 plain files — mirrors the gap-log target shape (a large
	// flat directory) so the cap actually bites.
	listdir := t.TempDir()
	for i := 1; i <= 160; i++ {
		name := fmt.Sprintf("entry%03d.txt", i)
		if err := os.WriteFile(filepath.Join(listdir, name), []byte("x\n"), 0o644); err != nil {
			t.Fatal(err)
		}
	}

	// grepdir: one file with 8 matches (cap is 5), one with 2 (under cap).
	grepdir := t.TempDir()
	var a strings.Builder
	for i := 1; i <= 8; i++ {
		fmt.Fprintf(&a, "needle mark %d\n", i)
	}
	a.WriteString("filler line one\nfiller line two\n")
	if err := os.WriteFile(filepath.Join(grepdir, "notes_a.txt"), []byte(a.String()), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(grepdir, "notes_b.txt"), []byte("needle b 1\nneedle b 2\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	t.Run("ls packs, caps at 40, and spools the full listing", func(t *testing.T) {
		raw, rawCode := rawTool(t, listdir, "ls")
		if rawCode != 0 {
			t.Fatalf("raw ls exit %d, want 0", rawCode)
		}
		out, errS, code := h.run(t, listdir, "ls")
		if code != rawCode {
			t.Fatalf("exit %d, want %d (parity); stderr: %s", code, rawCode, errS)
		}
		id := mustOKID(t, out)
		if !strings.Contains(out, "(+120 more)") {
			t.Errorf("missing cap tail (+120 more):\n%s", out)
		}
		if !strings.Contains(out, "entry001.txt") || strings.Contains(out, "entry041.txt") {
			t.Errorf("cap boundary wrong (want entry001 shown, entry041 elided):\n%s", out)
		}
		packRowsMaxWidth(t, out)
		if savings := 1 - float64(len(out))/float64(len(raw)); savings < 0.60 {
			t.Errorf("savings %.0f%% below the 60%% band floor (%d -> %d bytes)", savings*100, len(raw), len(out))
		}
		shown, _, scode := h.run(t, listdir, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "# cmd: ls") {
			t.Errorf("missing provenance header:\n%s", shown)
		}
		if !strings.Contains(shown, "entry041.txt") || !strings.Contains(shown, "entry160.txt") {
			t.Errorf("spooled raw missing elided entries:\n%s", shown)
		}
	})

	t.Run("ls -l long format passes through unchanged with no OK", func(t *testing.T) {
		raw, rawCode := rawTool(t, grepdir, "ls", "-l")
		if rawCode != 0 {
			t.Fatalf("raw ls -l exit %d, want 0", rawCode)
		}
		out, errS, code := h.run(t, grepdir, "ls", "-l")
		if code != rawCode {
			t.Fatalf("exit %d, want %d (parity); stderr: %s", code, rawCode, errS)
		}
		if okRe.MatchString(out) {
			t.Errorf("long format was elided (emitted OK), should pass through:\n%s", out)
		}
		if out != raw {
			t.Errorf("long format altered:\nraw: %q\nvtk: %q", raw, out)
		}
	})

	t.Run("grep -rn caps matches per file with a per-file tail", func(t *testing.T) {
		raw, rawCode := rawTool(t, grepdir, "grep", "-rn", "needle", ".")
		if rawCode != 0 {
			t.Fatalf("raw grep exit %d, want 0", rawCode)
		}
		out, errS, code := h.run(t, grepdir, "grep", "-rn", "needle", ".")
		if code != rawCode {
			t.Fatalf("exit %d, want %d (parity); stderr: %s", code, rawCode, errS)
		}
		id := mustOKID(t, out)
		if got := len(regexp.MustCompile(`(?m)^\./notes_a\.txt:\d+:`).FindAllString(out, -1)); got != 5 {
			t.Errorf("notes_a match lines shown = %d, want 5:\n%s", got, out)
		}
		if !strings.Contains(out, "./notes_a.txt: (+3 more)") {
			t.Errorf("missing per-file tail for notes_a:\n%s", out)
		}
		if got := len(regexp.MustCompile(`(?m)^\./notes_b\.txt:\d+:`).FindAllString(out, -1)); got != 2 {
			t.Errorf("notes_b match lines shown = %d, want 2 (under cap, no elision):\n%s", got, out)
		}
		if strings.Contains(out, "notes_b.txt: (+") {
			t.Errorf("under-cap file got a tail:\n%s", out)
		}
		if strings.Contains(out, "needle mark 8") {
			t.Errorf("elided match leaked into compact output:\n%s", out)
		}
		if len(out) >= len(raw) {
			t.Errorf("compact output (%d bytes) not smaller than raw (%d bytes)", len(out), len(raw))
		}
		shown, _, scode := h.run(t, grepdir, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "needle mark 8") {
			t.Errorf("spooled raw missing elided match:\n%s", shown)
		}
	})

	t.Run("grep -rl file list is packed and capped", func(t *testing.T) {
		many := t.TempDir()
		for i := 1; i <= 50; i++ {
			name := fmt.Sprintf("m%02d.txt", i)
			if err := os.WriteFile(filepath.Join(many, name), []byte("needle\n"), 0o644); err != nil {
				t.Fatal(err)
			}
		}
		out, errS, code := h.run(t, many, "grep", "-rl", "needle", ".")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		id := mustOKID(t, out)
		if !strings.Contains(out, "(+10 more)") {
			t.Errorf("missing cap tail (+10 more):\n%s", out)
		}
		if !strings.Contains(out, "./m01.txt") || strings.Contains(out, "m41.txt") {
			t.Errorf("cap boundary wrong (want m01 shown, m41 elided):\n%s", out)
		}
		packRowsMaxWidth(t, out)
		shown, _, scode := h.run(t, many, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "./m50.txt") {
			t.Errorf("spooled raw missing elided file:\n%s", shown)
		}
	})

	t.Run("grep no-match exit 1 keeps parity and stays raw", func(t *testing.T) {
		raw, rawCode := rawTool(t, grepdir, "grep", "-rn", "zzz-absent", ".")
		if rawCode != 1 {
			t.Fatalf("raw grep no-match exit %d, want 1", rawCode)
		}
		out, errS, code := h.run(t, grepdir, "grep", "-rn", "zzz-absent", ".")
		if code != rawCode {
			t.Errorf("exit %d, want %d (parity)", code, rawCode)
		}
		if okRe.MatchString(out + errS) {
			t.Errorf("no-match run emitted an OK id (exit 1 not in allowlist):\n%s%s", out, errS)
		}
		if out != raw {
			t.Errorf("no-match output altered:\nraw: %q\nvtk: %q", raw, out)
		}
	})

	t.Run("find packs, caps, and spools the full walk", func(t *testing.T) {
		requireGNUFind(t)
		raw, rawCode := rawTool(t, listdir, "find", ".")
		if rawCode != 0 {
			t.Fatalf("raw find exit %d, want 0", rawCode)
		}
		out, errS, code := h.run(t, listdir, "find", ".")
		if code != rawCode {
			t.Fatalf("exit %d, want %d (parity); stderr: %s", code, rawCode, errS)
		}
		id := mustOKID(t, out)
		if !strings.Contains(out, "(+121 more)") {
			t.Errorf("missing cap tail (+121 more):\n%s", out)
		}
		packRowsMaxWidth(t, out)
		if len(out) >= len(raw) {
			t.Errorf("compact output (%d bytes) not smaller than raw (%d bytes)", len(out), len(raw))
		}
		shown, _, scode := h.run(t, listdir, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "entry160.txt") {
			t.Errorf("spooled raw missing elided path:\n%s", shown)
		}
	})
}
