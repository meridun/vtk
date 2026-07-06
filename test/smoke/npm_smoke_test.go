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

// fakeNpmSrc is a standalone program compiled into a scratch bin dir and placed
// first on PATH as "npm". It emulates `npm run <script>`: it prints the npm
// two-line banner ("> <pkg>@<ver> <script>" + "> <expanded command line>") then
// the inner tool's output, so vtk's npm-dispatch layer (#8) can be exercised
// end-to-end without a real npm install.
//
// The script name selects the inner behavior:
//
//	lint  -> banner "> eslint . --cache" + an eslint problems report (delegates
//	         to the eslint filter; exit code from VTK_FAKE_NPM_CODE, default 1)
//	nofil -> banner "> node scripts/x.mjs" + plain output (no inner filter;
//	         banner-stripped passthrough, gap attributed to node)
const fakeNpmSrc = `package main

import (
	"fmt"
	"os"
	"strconv"
)

func main() {
	// args: run <script> [extra...]
	script := ""
	if len(os.Args) >= 3 && os.Args[1] == "run" {
		script = os.Args[2]
	}
	code := 0
	if v := os.Getenv("VTK_FAKE_NPM_CODE"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			code = n
		}
	}
	switch script {
	case "lint":
		fmt.Println("> demo@1.0.0 lint")
		fmt.Println("> eslint . --cache")
		fmt.Println()
		fmt.Print(eslintReport)
	case "nofil":
		fmt.Println("> demo@1.0.0 nofil")
		fmt.Println("> node scripts/x.mjs --check")
		fmt.Println()
		fmt.Println("config is in sync (12 files checked)")
	default:
		fmt.Fprintln(os.Stderr, "unknown script")
		code = 1
	}
	os.Exit(code)
}

const eslintReport = "\nsrc/app.js\n" +
	"   1:1   error    'foo' is not defined                  no-undef\n" +
	"   2:10  error    Missing semicolon                     semi\n" +
	"  12:7   error    Missing semicolon                     semi\n" +
	"\n✖ 3 problems (3 errors, 0 warnings)\n"
`

// buildFakeNpm compiles the fake into dir/npm(.exe) and returns dir.
func buildFakeNpm(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	src := filepath.Join(dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "main.go"), []byte(fakeNpmSrc), 0o644); err != nil {
		t.Fatal(err)
	}
	bin := filepath.Join(dir, "npm")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command("go", "build", "-o", bin, "main.go")
	cmd.Dir = src
	cmd.Env = append(os.Environ(), "GO111MODULE=off")
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("build fake npm: %v\n%s", err, out)
	}
	return dir
}

// runNpm runs the vtk binary with the fake npm first on PATH.
func (h *harness) runNpm(t *testing.T, binDir string, code int, args ...string) (stdout, stderr string, exit int) {
	t.Helper()
	cmd := exec.Command(h.bin, args...)
	cmd.Dir = h.repo
	cmd.Env = append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
		"PATH="+binDir+string(os.PathListSeparator)+os.Getenv("PATH"),
		"VTK_FAKE_NPM_CODE="+itoa(code),
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

// TestSmokeNpmDispatch exercises #8 through the real vtk binary: strip the npm
// banner, delegate the body to the inner tool's filter, and attribute the gap
// to the inner tool when no filter exists — with exit-code parity throughout.
func TestSmokeNpmDispatch(t *testing.T) {
	if _, err := exec.LookPath("go"); err != nil {
		t.Skip("go toolchain not on PATH (needed to build the fake npm)")
	}
	h := newHarness(t)
	binDir := buildFakeNpm(t)

	t.Run("npm run lint delegates to the eslint filter and compacts", func(t *testing.T) {
		out, errS, code := h.runNpm(t, binDir, 1, "npm", "run", "lint")
		if code != 1 {
			t.Fatalf("exit %d, want 1 (parity); stderr: %s", code, errS)
		}
		id := mustOKID(t, out)
		// The eslint per-rule rollup, not the banner or raw stylish lines.
		if !strings.Contains(out, "semi") || !strings.Contains(out, "3 problems") {
			t.Errorf("expected eslint rollup, got:\n%s", out)
		}
		if strings.Contains(out, "demo@1.0.0") || strings.Contains(out, "> eslint") {
			t.Errorf("npm banner leaked into compact output:\n%s", out)
		}
		// The spool recovers the full raw, banner included (AC #1).
		shown, _, scode := h.run(t, h.repo, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "> eslint . --cache") || !strings.Contains(shown, "'foo' is not defined") {
			t.Errorf("spool did not recover full raw (banner + report):\n%s", shown)
		}
		// The invocation is attributed to eslint, not npm (AC #3 attribution).
		if log := h.invocationLog(t); !strings.Contains(log, `"cmd":"eslint`) {
			t.Errorf("expected inner-tool (eslint) attribution in log:\n%s", log)
		}
	})

	t.Run("npm run with no inner filter strips banner and gaps to inner tool", func(t *testing.T) {
		out, errS, code := h.runNpm(t, binDir, 0, "npm", "run", "nofil")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		// Banner stripped; body preserved.
		if strings.Contains(out, "demo@1.0.0") || strings.Contains(out, "> node") {
			t.Errorf("npm banner not stripped:\n%s", out)
		}
		if !strings.Contains(out, "config is in sync") {
			t.Errorf("inner body dropped:\n%s", out)
		}
		// Gap attributed to node (the inner tool), not npm (AC #3).
		gaps, _, gcode := h.run(t, h.repo, "gaps")
		if gcode != 0 {
			t.Fatalf("gaps exit %d, want 0", gcode)
		}
		if !strings.Contains(gaps, "node") {
			t.Errorf("gap not attributed to inner tool (node):\n%s", gaps)
		}
		if strings.Contains(gaps, "npm") {
			t.Errorf("gap wrongly attributed to npm:\n%s", gaps)
		}
	})
}
