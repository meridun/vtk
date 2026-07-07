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

// fakeMochaSrc is a standalone program compiled into a scratch bin dir and
// placed first on PATH. It emulates a mocha spec-reporter run without a real
// mocha install: it prints one of the committed fixtures (a real captured
// mocha 10 spec-reporter run) and exits with a controllable code.
//
//	VTK_FAKE_MOCHA_CODE (default 1) selects the exit code AND the fixture:
//	  0 -> passRaw  (green run; exit 0)
//	  1 -> failRaw  (3 failures; exit 1 = mocha's "tests failed", filterable)
//	  2 -> failRaw  (exit 2 = mocha/config error; outside the {0,1} allowlist,
//	                 must stay raw — the passthrough invariant)
//
// When invoked with a leading "npm run <script>" argv it also prints the npm
// two-line banner naming `mocha` as the inner tool, so the npm-dispatch layer
// (#8) can delegate to the mocha filter (#12) with attribution to mocha.
const fakeMochaSrc = `package main

import (
	"fmt"
	"os"
	"strconv"
)

func main() {
	code := 1
	if v := os.Getenv("VTK_FAKE_MOCHA_CODE"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			code = n
		}
	}
	// npm-run dispatch shape: emulate ` + "`npm run test`" + ` wrapping mocha.
	if len(os.Args) >= 3 && os.Args[1] == "run" {
		fmt.Println("> demo@1.0.0 " + os.Args[2])
		fmt.Println("> mocha --reporter spec")
		fmt.Println()
	}
	if code == 0 {
		fmt.Print(passRaw)
	} else {
		fmt.Print(failRaw)
	}
	os.Exit(code)
}

` + fakeMochaFixtures + `
`

// fakeMochaFixtures embeds the committed testdata verbatim (real mocha 10
// spec-reporter captures) so the smoke exercises the same bytes the unit
// fixtures assert on. Keep these in sync with
// internal/filter/mocha/testdata/{pass,fail}.raw.txt.
const fakeMochaFixtures = "const failRaw = `\n\n" +
	"  cart\n" +
	"    ✔ starts empty\n" +
	"    ✔ adds an item\n" +
	"    1) computes total\n" +
	"    2) applies discount\n" +
	"    ✔ is serializable\n\n" +
	"  checkout\n" +
	"    ✔ validates address\n" +
	"    3) charges card\n" +
	"    ✔ sends receipt\n\n\n" +
	"  5 passing (6ms)\n" +
	"  3 failing\n\n" +
	"  1) cart\n" +
	"       computes total:\n\n" +
	"      AssertionError [ERR_ASSERTION]: 12 == 11\n" +
	"      + expected - actual\n\n" +
	"      -12\n" +
	"      +11\n" +
	"      \n" +
	"      at Context.<anonymous> (test\\mixed.test.js:5:45)\n" +
	"      at process.processImmediate (node:internal/timers:485:21)\n\n" +
	"  2) cart\n" +
	"       applies discount:\n\n" +
	"      AssertionError [ERR_ASSERTION]: 90 == 80\n" +
	"      + expected - actual\n\n" +
	"      -90\n" +
	"      +80\n" +
	"      \n" +
	"      at Context.<anonymous> (test\\mixed.test.js:6:47)\n" +
	"      at process.processImmediate (node:internal/timers:485:21)\n\n" +
	"  3) checkout\n" +
	"       charges card:\n" +
	"     Error: gateway timeout\n" +
	"      at Context.<anonymous> (test\\mixed.test.js:11:42)\n" +
	"      at process.processImmediate (node:internal/timers:485:21)\n\n\n\n`\n\n" +
	"const passRaw = `\n\n" +
	"  math\n" +
	"    addition\n" +
	"      ✔ adds small numbers\n" +
	"      ✔ adds zero\n" +
	"      ✔ is commutative\n" +
	"    subtraction\n" +
	"      ✔ subtracts\n" +
	"      ✔ handles negatives\n\n" +
	"  strings\n" +
	"    ✔ concatenates\n" +
	"    ✔ uppercases\n\n\n" +
	"  7 passing (5ms)\n\n`\n"

// buildFakeMocha compiles the fake into dir/mocha(.exe), also copying it to
// npx(.exe) and npm(.exe) so `npx mocha` and `npm run test` both resolve to it,
// and returns dir.
func buildFakeMocha(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	src := filepath.Join(dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "main.go"), []byte(fakeMochaSrc), 0o644); err != nil {
		t.Fatal(err)
	}
	suffix := ""
	if runtime.GOOS == "windows" {
		suffix = ".exe"
	}
	bin := filepath.Join(dir, "mocha"+suffix)
	cmd := exec.Command("go", "build", "-o", bin, "main.go")
	cmd.Dir = src
	cmd.Env = append(os.Environ(), "GO111MODULE=off")
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("build fake mocha: %v\n%s", err, out)
	}
	raw, err := os.ReadFile(bin)
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"npx", "npm"} {
		if err := os.WriteFile(filepath.Join(dir, name+suffix), raw, 0o755); err != nil {
			t.Fatal(err)
		}
	}
	return dir
}

// runMocha runs the vtk binary with the fake mocha/npx/npm first on PATH and a
// controllable child exit code.
func (h *harness) runMocha(t *testing.T, binDir string, code int, args ...string) (stdout, stderr string, exit int) {
	t.Helper()
	cmd := exec.Command(h.bin, args...)
	cmd.Dir = h.repo
	cmd.Env = append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
		"PATH="+binDir+string(os.PathListSeparator)+os.Getenv("PATH"),
		"VTK_FAKE_MOCHA_CODE="+itoa(code),
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

// TestSmokeMocha exercises the #12 mocha filter end-to-end through the real vtk
// binary and a real (fake-tool) subprocess: fold passing specs, keep failures +
// summary, recover full raw via `vtk show`, and hold exit-code parity across the
// success / failure / config-error / not-found paths.
func TestSmokeMocha(t *testing.T) {
	if _, err := exec.LookPath("go"); err != nil {
		t.Skip("go toolchain not on PATH (needed to build the fake mocha)")
	}
	h := newHarness(t)
	binDir := buildFakeMocha(t)

	// AC #1: a direct `npx mocha` failing run (exit 1) folds passing specs but
	// keeps every failure block + the summary, with exit-code parity, and the
	// spool recovers the full raw behind OK <id>.
	t.Run("npx mocha failing run folds passing specs, keeps failures + parity", func(t *testing.T) {
		out, errS, code := h.runMocha(t, binDir, 1, "npx", "mocha")
		if code != 1 {
			t.Fatalf("exit %d, want 1 (parity); stderr: %s", code, errS)
		}
		id := mustOKID(t, out)
		// Passing specs folded away.
		if strings.Contains(out, "starts empty") || strings.Contains(out, "✔") {
			t.Errorf("passing specs not folded:\n%s", out)
		}
		// Summary + every failure block kept.
		for _, want := range []string{"5 passing", "3 failing", "computes total", "applies discount", "charges card", "gateway timeout"} {
			if !strings.Contains(out, want) {
				t.Errorf("compact output missing %q:\n%s", want, out)
			}
		}
		// The spool recovers the full raw, folded specs included (AC #1 recovery).
		shown, _, scode := h.run(t, h.repo, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "starts empty") || !strings.Contains(shown, "sends receipt") {
			t.Errorf("spool did not recover folded passing specs:\n%s", shown)
		}
		if !strings.Contains(shown, "# cmd: npx mocha") {
			t.Errorf("missing provenance header:\n%s", shown)
		}
	})

	// AC #3: a green run collapses to the single summary line.
	t.Run("npx mocha green run collapses to one summary line", func(t *testing.T) {
		out, errS, code := h.runMocha(t, binDir, 0, "npx", "mocha")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		if !strings.Contains(out, "7 passing") {
			t.Errorf("summary missing from green run:\n%s", out)
		}
		if strings.Contains(out, "✔") || strings.Contains(out, "adds small numbers") {
			t.Errorf("green run did not fold passing specs:\n%s", out)
		}
		// One content line (the summary) plus the OK line.
		body := strings.TrimSpace(out)
		lines := strings.Split(body, "\n")
		if len(lines) != 2 {
			t.Errorf("green run should be summary + OK (2 lines), got %d:\n%s", len(lines), out)
		}
	})

	// AC #2: an `npm run test`-wrapped mocha run delegates through the mocha
	// filter, with gap/stats attribution to mocha (not npm) and full-raw
	// recovery of the banner + folded specs.
	t.Run("npm run test delegates to the mocha filter with mocha attribution", func(t *testing.T) {
		out, errS, code := h.runMocha(t, binDir, 1, "npm", "run", "test")
		if code != 1 {
			t.Fatalf("exit %d, want 1 (parity); stderr: %s", code, errS)
		}
		id := mustOKID(t, out)
		if !strings.Contains(out, "5 passing") || !strings.Contains(out, "charges card") {
			t.Errorf("expected mocha rollup, got:\n%s", out)
		}
		// npm banner stripped, passing specs folded.
		if strings.Contains(out, "demo@1.0.0") || strings.Contains(out, "> mocha") || strings.Contains(out, "✔") {
			t.Errorf("npm banner or passing specs leaked into compact output:\n%s", out)
		}
		// Attribution is to mocha, not npm (AC #3 attribution / gap direction).
		if log := h.invocationLog(t); !strings.Contains(log, `"cmd":"mocha`) {
			t.Errorf("expected inner-tool (mocha) attribution in log:\n%s", log)
		}
		// The spool recovers the full raw, banner + folded specs included.
		shown, _, scode := h.run(t, h.repo, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "> mocha --reporter spec") || !strings.Contains(shown, "starts empty") {
			t.Errorf("spool did not recover full raw (banner + folded specs):\n%s", shown)
		}
	})

	// Passthrough invariant: exit 2 is outside the {0,1} allowlist (a mocha /
	// config error), so the output must stay raw — no fold, no OK — with parity.
	t.Run("exit 2 config error stays raw (allowlist passthrough)", func(t *testing.T) {
		out, errS, code := h.runMocha(t, binDir, 2, "npx", "mocha")
		if code != 2 {
			t.Fatalf("exit %d, want 2 (parity); stderr: %s", code, errS)
		}
		if okRe.MatchString(out) {
			t.Errorf("exit 2 should not emit OK (raw passthrough):\n%s", out)
		}
		if !strings.Contains(out, "✔") || !strings.Contains(out, "starts empty") {
			t.Errorf("exit 2 should pass raw output through unfiltered:\n%s", out)
		}
	})
}
