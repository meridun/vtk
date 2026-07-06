//go:build smoke

package smoke

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// fakeEslintSrc is a standalone program compiled into a scratch bin dir and
// placed first on PATH as "eslint". It emits canned eslint stylish output and
// exits with the code named by VTK_FAKE_ESLINT_CODE (default 1):
//
//	0 -> clean run, no output
//	1 -> problems-found report on stdout (the case vtk compacts)
//	2 -> fatal/config error on stderr, no report (must stay raw)
//
// Using a fake keeps the smoke self-contained (no eslint/npm install) while
// still exercising the real vtk binary end-to-end: registry Lookup on
// "eslint", the {0,1} exit-code allowlist gating filter-vs-raw, the spool
// write + "OK <id>", and exit-code parity on every path.
const fakeEslintSrc = `package main

import (
	"fmt"
	"os"
	"strconv"
)

func main() {
	code := 1
	if v := os.Getenv("VTK_FAKE_ESLINT_CODE"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			code = n
		}
	}
	switch code {
	case 0:
		// clean run: eslint prints nothing
	case 2:
		fmt.Fprintln(os.Stderr, "Oops! Something went wrong! :(")
		fmt.Fprintln(os.Stderr, "ESLint: 9.0.0")
		fmt.Fprintln(os.Stderr, "Error: Cannot read config file: .eslintrc.json")
	default:
		fmt.Print(report)
	}
	os.Exit(code)
}

const report = "\nsrc/app.js\n" +
	"   1:1   error    'foo' is not defined                  no-undef\n" +
	"   2:10  error    Missing semicolon                     semi\n" +
	"   5:1   warning  'bar' is assigned a value but never used  no-unused-vars\n" +
	"  12:7   error    Missing semicolon                     semi\n" +
	"\nsrc/util.js\n" +
	"   3:5   error    Missing semicolon                     semi\n" +
	"   8:1   warning  Unexpected console statement          no-console\n" +
	"  14:3   error    'baz' is not defined                  no-undef\n" +
	"\n✖ 7 problems (5 errors, 2 warnings)\n"
`

// buildFakeEslint compiles the fake into dir/eslint(.exe) and returns dir so
// callers can prepend it to PATH.
func buildFakeEslint(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	src := filepath.Join(dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "main.go"), []byte(fakeEslintSrc), 0o644); err != nil {
		t.Fatal(err)
	}
	bin := filepath.Join(dir, "eslint")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command("go", "build", "-o", bin, "main.go")
	cmd.Dir = src
	cmd.Env = append(os.Environ(), "GO111MODULE=off")
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("build fake eslint: %v\n%s", err, out)
	}
	return dir
}

// runEslint runs the vtk binary with the fake eslint first on PATH and the
// child exit code forced via env. Returns combined stdout, stderr, and code.
func (h *harness) runEslint(t *testing.T, binDir string, code int, args ...string) (stdout, stderr string, exit int) {
	t.Helper()
	cmd := exec.Command(h.bin, args...)
	cmd.Dir = h.repo
	cmd.Env = append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
		"PATH="+binDir+string(os.PathListSeparator)+os.Getenv("PATH"),
		"VTK_FAKE_ESLINT_CODE="+itoa(code),
	)
	var out, errb strings.Builder
	cmd.Stdout = &out
	cmd.Stderr = &errb
	err := cmd.Run()
	exit = 0
	if err != nil {
		if ee, ok := err.(*exec.ExitError); ok {
			exit = ee.ExitCode()
		} else {
			exit = 127
		}
	}
	return out.String(), errb.String(), exit
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var b [20]byte
	i := len(b)
	for n > 0 {
		i--
		b[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		b[i] = '-'
	}
	return string(b[i:])
}

// rawEslint runs the fake eslint directly (no vtk) to establish the parity
// baseline for a given exit code.
func rawEslint(t *testing.T, binDir string, code int) (combined string, exit int) {
	t.Helper()
	bin := filepath.Join(binDir, "eslint")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command(bin)
	cmd.Env = append(os.Environ(), "VTK_FAKE_ESLINT_CODE="+itoa(code))
	out, err := cmd.CombinedOutput()
	exit = 0
	if err != nil {
		if ee, ok := err.(*exec.ExitError); ok {
			exit = ee.ExitCode()
		}
	}
	return string(out), exit
}

// TestSmokeEslint exercises issue #7 through the real vtk binary: the eslint
// exit-code allowlist ({0,1} filters, 2+ stays raw) with exit-code parity on
// every path.
func TestSmokeEslint(t *testing.T) {
	if _, err := exec.LookPath("go"); err != nil {
		t.Skip("go toolchain not on PATH (needed to build the fake eslint)")
	}
	h := newHarness(t)
	binDir := buildFakeEslint(t)

	t.Run("exit 1 problems report is compacted, parity preserved, raw recoverable", func(t *testing.T) {
		rawOut, rawCode := rawEslint(t, binDir, 1)
		if rawCode != 1 {
			t.Fatalf("raw eslint exit %d, want 1", rawCode)
		}
		out, errS, code := h.runEslint(t, binDir, 1, "eslint", "src/")
		if code != rawCode {
			t.Fatalf("exit %d, want %d (parity); stderr: %s", code, rawCode, errS)
		}
		id := mustOKID(t, out)
		if len(out) >= len(rawOut) {
			t.Errorf("compact output (%d bytes) not smaller than raw (%d bytes)", len(out), len(rawOut))
		}
		// Per-rule rollup, not the raw stylish lines.
		for _, want := range []string{"3x error semi", "2x error no-undef", "✖ 7 problems"} {
			if !strings.Contains(out, want) {
				t.Errorf("compact eslint output missing %q:\n%s", want, out)
			}
		}
		// The raw report survives in the spool and is recoverable in full.
		shown, _, scode := h.run(t, h.repo, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "# cmd: eslint src/") {
			t.Errorf("missing provenance header:\n%s", shown)
		}
		if !strings.Contains(shown, "'foo' is not defined") {
			t.Errorf("spooled raw missing original problem detail:\n%s", shown)
		}
		// Invariant 3: the gap/invocation log carries no output content.
		meta, err := os.ReadFile(filepath.Join(h.home, "vtk", "invocations.jsonl"))
		if err != nil {
			t.Fatalf("read invocation log: %v", err)
		}
		if strings.Contains(string(meta), "is not defined") {
			t.Error("invocation log leaked command output content")
		}
	})

	t.Run("exit 0 clean run passes through with no OK", func(t *testing.T) {
		out, errS, code := h.runEslint(t, binDir, 0, "eslint", "src/")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		if strings.TrimSpace(out) != "" {
			t.Errorf("clean eslint run should print nothing, got:\n%s", out)
		}
		if okRe.MatchString(out) {
			t.Errorf("clean run emitted an OK id:\n%s", out)
		}
	})

	t.Run("exit 2 fatal error stays raw with exit-code parity", func(t *testing.T) {
		rawOut, rawCode := rawEslint(t, binDir, 2)
		if rawCode != 2 {
			t.Fatalf("raw eslint exit %d, want 2", rawCode)
		}
		out, errS, code := h.runEslint(t, binDir, 2, "eslint", "src/")
		if code != rawCode {
			t.Errorf("exit %d, want %d (parity)", code, rawCode)
		}
		if okRe.MatchString(out + errS) {
			t.Errorf("fatal run emitted an OK id (should stay raw):\n%s%s", out, errS)
		}
		// Fatal output must survive unaltered (invariant 2: filter-failure /
		// out-of-allowlist degrades to raw, never lost output).
		if out+errS != rawOut {
			t.Errorf("fatal output altered:\nraw: %q\nvtk: %q", rawOut, out+errS)
		}
		if log := h.invocationLog(t); !strings.Contains(log, `"reason":"nonzero-exit"`) {
			t.Errorf("expected nonzero-exit reason entry for exit 2, got:\n%s", log)
		}
	})
}
