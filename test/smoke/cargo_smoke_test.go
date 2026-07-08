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

// fakeCargoSrc is a standalone program compiled into a scratch bin dir and
// placed first on PATH as "cargo". It emulates `cargo build`: per-crate
// progress chatter plus a Finished summary on success, or a compile error on
// stderr with exit 101 on failure. This exercises the declarative TOML filter
// engine (#40) end-to-end through the real vtk binary — the embedded
// defs/cargo.toml registers on the regex path, so `cargo build` is claimed by a
// data-only filter with no Go package.
//
// VTK_FAKE_CARGO_CODE selects the exit code (default 0). A nonzero code takes
// the compile-error branch (raw error, no Finished line).
const fakeCargoSrc = `package main

import (
	"fmt"
	"os"
	"strconv"
)

func main() {
	sub := ""
	if len(os.Args) >= 2 {
		sub = os.Args[1]
	}
	code := 0
	if v := os.Getenv("VTK_FAKE_CARGO_CODE"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			code = n
		}
	}
	if sub != "build" {
		fmt.Fprintln(os.Stderr, "unknown subcommand")
		os.Exit(1)
	}
	// Progress chatter cargo prints while working (the filter strips these).
	fmt.Println("   Compiling libc v0.2.169")
	fmt.Println("   Compiling proc-macro2 v1.0.92")
	fmt.Println("   Compiling unicode-ident v1.0.14")
	fmt.Println("    Checking myapp v0.1.0 (/home/u/myapp)")
	if code != 0 {
		fmt.Fprintln(os.Stderr, "error[E0425]: cannot find value ` + "`count`" + ` in this scope")
		fmt.Fprintln(os.Stderr, " --> src/main.rs:4:9")
		fmt.Fprintln(os.Stderr, "error: could not compile ` + "`myapp`" + ` due to 1 previous error")
		os.Exit(code)
	}
	fmt.Println("    Finished ` + "`dev`" + ` profile [unoptimized + debuginfo] target(s) in 4.21s")
	os.Exit(0)
}
`

// buildFakeCargo compiles the fake into dir/cargo(.exe) and returns dir.
func buildFakeCargo(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	src := filepath.Join(dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "main.go"), []byte(fakeCargoSrc), 0o644); err != nil {
		t.Fatal(err)
	}
	bin := filepath.Join(dir, "cargo")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command("go", "build", "-o", bin, "main.go")
	cmd.Dir = src
	cmd.Env = append(os.Environ(), "GO111MODULE=off")
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("build fake cargo: %v\n%s", err, out)
	}
	return dir
}

// runCargo runs the vtk binary with the fake cargo first on PATH.
func (h *harness) runCargo(t *testing.T, binDir string, code int, args ...string) (stdout, stderr string, exit int) {
	t.Helper()
	cmd := exec.Command(h.bin, args...)
	cmd.Dir = h.repo
	cmd.Env = append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
		"PATH="+binDir+string(os.PathListSeparator)+os.Getenv("PATH"),
		"VTK_FAKE_CARGO_CODE="+itoa(code),
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

// TestSmokeCargoTOMLFilter exercises #40 through the real vtk binary: a
// declarative TOML filter (defs/cargo.toml), matched on the regex path, strips
// cargo's per-crate progress lines on a clean build and keeps the Finished
// summary; a compile error (exit 101) passes through raw with exit-code parity.
func TestSmokeCargoTOMLFilter(t *testing.T) {
	if _, err := exec.LookPath("go"); err != nil {
		t.Skip("go toolchain not on PATH (needed to build the fake cargo)")
	}
	h := newHarness(t)
	binDir := buildFakeCargo(t)

	t.Run("clean build strips progress via the TOML regex filter and compacts", func(t *testing.T) {
		out, errS, code := h.runCargo(t, binDir, 0, "cargo", "build")
		if code != 0 {
			t.Fatalf("exit %d, want 0 (parity); stderr: %s", code, errS)
		}
		// OK <id> is only emitted when the filter elided content: proof the
		// TOML regex filter claimed and compacted the command.
		id := mustOKID(t, out)
		if strings.Contains(out, "Compiling") || strings.Contains(out, "Checking") {
			t.Errorf("progress lines not stripped:\n%s", out)
		}
		if !strings.Contains(out, "Finished") {
			t.Errorf("Finished summary dropped:\n%s", out)
		}
		// The spool recovers the full raw, stripped progress included (AC:
		// metadata/recovery — elided bytes are never lost).
		shown, _, scode := h.run(t, h.repo, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "Compiling libc v0.2.169") {
			t.Errorf("spool did not recover stripped progress:\n%s", shown)
		}
	})

	t.Run("compile error exits 101 raw with parity and no elision", func(t *testing.T) {
		out, errS, code := h.runCargo(t, binDir, 101, "cargo", "build")
		if code != 101 {
			t.Fatalf("exit %d, want 101 (parity)", code)
		}
		if okRe.MatchString(out) {
			t.Errorf("failing run must not emit an OK id:\n%s", out)
		}
		combined := out + errS
		if !strings.Contains(combined, "error[E0425]") {
			t.Errorf("compile error not passed through raw:\n%s", combined)
		}
		// A nonzero exit is outside the filter's exit_codes ({0}); the whole
		// raw output — progress lines included — passes through untouched.
		if !strings.Contains(combined, "Compiling libc v0.2.169") {
			t.Errorf("raw progress missing on failure path (should not be filtered):\n%s", combined)
		}
	})
}
