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

// fakeDbmateSrc is compiled into a scratch bin dir and placed first on PATH as
// "dbmate". It emulates dbmate 2.27.0 (sqlite) human-format output for the
// commands the filter family handles, so vtk's dbmate filter (#13) can be
// exercised end-to-end without a real dbmate/database.
//
// The subcommand selects the output shape (captured from real dbmate 2.27.0):
//
//	status   -> 18 applied "[X]" lines + 2 pending "[ ]" lines + summary
//	up       -> two Applying:/Applied: pairs (progress + result)
//	rollback -> one Rolling back:/Rolled back: pair
//	fail     -> an Error: line on stderr with a nonzero exit (parity path)
//
// Exit code is 0 for the report shapes and VTK_FAKE_DBMATE_CODE (default 1) for
// the fail shape.
const fakeDbmateSrc = `package main

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
	switch sub {
	case "status":
		fmt.Print(statusOut)
	case "up":
		fmt.Print(upOut)
	case "rollback":
		fmt.Print(rollbackOut)
	case "fail":
		fmt.Fprint(os.Stderr, "Applying: 20260101000019_add_read_flag.sql\n")
		fmt.Fprint(os.Stderr, "Error: table users already exists\n")
		code := 1
		if v := os.Getenv("VTK_FAKE_DBMATE_CODE"); v != "" {
			if n, err := strconv.Atoi(v); err == nil {
				code = n
			}
		}
		os.Exit(code)
	default:
		fmt.Fprintln(os.Stderr, "unknown command")
		os.Exit(1)
	}
}

const statusOut = "[X] 20260101000001_create_users.sql\n" +
	"[X] 20260101000002_create_posts.sql\n" +
	"[X] 20260101000003_add_index.sql\n" +
	"[X] 20260101000004_create_sessions.sql\n" +
	"[X] 20260101000005_add_email_col.sql\n" +
	"[X] 20260101000006_create_orders.sql\n" +
	"[X] 20260101000007_add_orders_idx.sql\n" +
	"[X] 20260101000008_create_products.sql\n" +
	"[X] 20260101000009_create_carts.sql\n" +
	"[X] 20260101000010_add_cart_fk.sql\n" +
	"[X] 20260101000011_create_reviews.sql\n" +
	"[X] 20260101000012_add_review_rating.sql\n" +
	"[X] 20260101000013_create_tags.sql\n" +
	"[X] 20260101000014_create_post_tags.sql\n" +
	"[X] 20260101000015_add_slug.sql\n" +
	"[X] 20260101000016_create_media.sql\n" +
	"[X] 20260101000017_add_media_type.sql\n" +
	"[X] 20260101000018_create_notifications.sql\n" +
	"[ ] 20260101000019_add_read_flag.sql\n" +
	"[ ] 20260101000020_create_audit_log.sql\n" +
	"\nApplied: 18\nPending: 2\n"

const upOut = "Applying: 20260101000019_add_read_flag.sql\n" +
	"Applied: 20260101000019_add_read_flag.sql in 956.9µs\n" +
	"Applying: 20260101000020_create_audit_log.sql\n" +
	"Applied: 20260101000020_create_audit_log.sql in 2.0052ms\n"

const rollbackOut = "Rolling back: 20260101000020_create_audit_log.sql\n" +
	"Rolled back: 20260101000020_create_audit_log.sql in 2.0049ms\n"
`

// buildFakeDbmate compiles the fake into dir/dbmate(.exe) and returns dir.
func buildFakeDbmate(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	src := filepath.Join(dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "main.go"), []byte(fakeDbmateSrc), 0o644); err != nil {
		t.Fatal(err)
	}
	bin := filepath.Join(dir, "dbmate")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command("go", "build", "-o", bin, "main.go")
	cmd.Dir = src
	cmd.Env = append(os.Environ(), "GO111MODULE=off")
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("build fake dbmate: %v\n%s", err, out)
	}
	return dir
}

// runOnPath runs the vtk binary with binDir prepended to PATH and an optional
// VTK_FAKE_DBMATE_CODE override.
func (h *harness) runOnPath(t *testing.T, binDir string, code int, args ...string) (stdout, stderr string, exit int) {
	t.Helper()
	cmd := exec.Command(h.bin, args...)
	cmd.Dir = h.repo
	env := append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
		"PATH="+binDir+string(os.PathListSeparator)+os.Getenv("PATH"),
	)
	if code != 0 {
		env = append(env, "VTK_FAKE_DBMATE_CODE="+itoa(code))
	}
	cmd.Env = env
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

// rawDbmate runs the fake dbmate directly (no vtk) to capture the raw baseline
// for parity comparisons.
func rawDbmate(t *testing.T, binDir, dir string, args ...string) (combined string, code int) {
	t.Helper()
	bin := filepath.Join(binDir, "dbmate")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command(bin, args...)
	cmd.Dir = dir
	out, err := cmd.CombinedOutput()
	code = 0
	if err != nil {
		if ee, ok := err.(*exec.ExitError); ok {
			code = ee.ExitCode()
		} else {
			code = 127
		}
	}
	return string(out), code
}

// TestSmokeDbmate exercises the #13 dbmate filter family through the real vtk
// binary, both direct (`vtk dbmate <sub>`) and npm-run-wrapped
// (`vtk npm run db:status`, the primary measured path via #8's dispatch).
func TestSmokeDbmate(t *testing.T) {
	if _, err := exec.LookPath("go"); err != nil {
		t.Skip("go toolchain not on PATH (needed to build the fake dbmate)")
	}
	h := newHarness(t)
	binDir := buildFakeDbmate(t)

	t.Run("AC: dbmate status collapses applied, keeps pending + summary", func(t *testing.T) {
		raw, rawCode := rawDbmate(t, binDir, h.repo, "status")
		if rawCode != 0 {
			t.Fatalf("raw dbmate status exit %d, want 0", rawCode)
		}
		out, errS, code := h.runOnPath(t, binDir, 0, "dbmate", "status")
		if code != 0 {
			t.Fatalf("exit %d, want 0 (parity); stderr: %s", code, errS)
		}
		id := mustOKID(t, out)
		if len(out) >= len(raw) {
			t.Errorf("compact status (%d) not smaller than raw (%d)", len(out), len(raw))
		}
		// Applied list collapsed to the count line; no individual [X] entries.
		if !strings.Contains(out, "[X] Applied: 18") {
			t.Errorf("missing collapsed applied count:\n%s", out)
		}
		if strings.Contains(out, "create_users") {
			t.Errorf("applied entries not collapsed:\n%s", out)
		}
		// Pending entries kept verbatim; summary preserved.
		for _, want := range []string{"add_read_flag.sql", "create_audit_log.sql", "Applied: 18", "Pending: 2"} {
			if !strings.Contains(out, want) {
				t.Errorf("compact status missing %q:\n%s", want, out)
			}
		}
		// Spool recovers the full raw applied list.
		shown, _, scode := h.run(t, h.repo, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "create_users") || !strings.Contains(shown, "# cmd: dbmate status") {
			t.Errorf("spool did not recover full raw with provenance:\n%s", shown)
		}
	})

	t.Run("AC: dbmate up drops progress, keeps result lines", func(t *testing.T) {
		out, errS, code := h.runOnPath(t, binDir, 0, "dbmate", "up")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		mustOKID(t, out)
		if strings.Contains(out, "Applying:") {
			t.Errorf("transient progress not dropped:\n%s", out)
		}
		for _, want := range []string{"Applied: 20260101000019_add_read_flag.sql", "Applied: 20260101000020_create_audit_log.sql"} {
			if !strings.Contains(out, want) {
				t.Errorf("result line dropped %q:\n%s", want, out)
			}
		}
	})

	t.Run("AC: dbmate rollback drops progress, keeps result", func(t *testing.T) {
		out, _, code := h.runOnPath(t, binDir, 0, "dbmate", "rollback")
		if code != 0 {
			t.Fatalf("exit %d, want 0", code)
		}
		mustOKID(t, out)
		if strings.Contains(out, "Rolling back:") {
			t.Errorf("progress not dropped:\n%s", out)
		}
		if !strings.Contains(out, "Rolled back: 20260101000020_create_audit_log.sql") {
			t.Errorf("result dropped:\n%s", out)
		}
	})

	t.Run("invariant: failed migration exits nonzero and passes through raw", func(t *testing.T) {
		raw, rawCode := rawDbmate(t, binDir, h.repo, "fail")
		if rawCode != 1 {
			t.Fatalf("raw fail exit %d, want 1", rawCode)
		}
		out, errS, code := h.runOnPath(t, binDir, 1, "dbmate", "fail")
		if code != rawCode {
			t.Errorf("exit %d, want %d (parity)", code, rawCode)
		}
		// Clean-run-only registration: nonzero exit is never filtered, no OK id.
		if okRe.MatchString(out + errS) {
			t.Errorf("failed migration was filtered (OK emitted):\n%s%s", out, errS)
		}
		// Error preserved verbatim (raw is on stderr).
		if !strings.Contains(errS, "Error: table users already exists") {
			t.Errorf("error not passed through raw:\n%s", errS)
		}
		if out+errS != raw {
			t.Errorf("failure output altered:\nraw: %q\nvtk: %q", raw, out+errS)
		}
	})

	t.Run("AC: exit-code parity on command-not-found", func(t *testing.T) {
		if _, _, code := h.runOnPath(t, binDir, 0, "dbmate", "bogus-subcmd"); code != 1 {
			// fake dbmate exits 1 on unknown subcommand; unrecognized shape
			// falls through raw, parity preserved.
			t.Errorf("exit %d, want 1 (parity on unknown subcommand)", code)
		}
	})
}

// TestSmokeDbmateViaNpmRun exercises the primary measured path: `npm run
// db:status` whose expanded inner command is `dbmate status`, routed through
// #8's npm-run inner-tool dispatch into the dbmate filter, attributed to
// dbmate (not npm).
func TestSmokeDbmateViaNpmRun(t *testing.T) {
	if _, err := exec.LookPath("go"); err != nil {
		t.Skip("go toolchain not on PATH")
	}
	h := newHarness(t)
	binDir := buildFakeDbmateNpm(t)

	out, errS, code := h.runOnPath(t, binDir, 0, "npm", "run", "db:status")
	if code != 0 {
		t.Fatalf("exit %d, want 0 (parity); stderr: %s", code, errS)
	}
	id := mustOKID(t, out)
	// npm banner stripped; dbmate applied list collapsed.
	if strings.Contains(out, "demo@1.0.0") || strings.Contains(out, "> dbmate status") {
		t.Errorf("npm banner leaked into compact output:\n%s", out)
	}
	if !strings.Contains(out, "[X] Applied: 18") {
		t.Errorf("dbmate filter did not fire on the wrapped inner argv:\n%s", out)
	}
	if strings.Contains(out, "create_users") {
		t.Errorf("applied entries not collapsed on wrapped path:\n%s", out)
	}
	// Attribution to dbmate, not npm (AC #3 of the #8 dispatch contract).
	if log := h.invocationLog(t); !strings.Contains(log, `"cmd":"dbmate status"`) {
		t.Errorf("expected inner-tool (dbmate) attribution:\n%s", log)
	}
	// Spool recovers banner + full raw applied list.
	shown, _, scode := h.run(t, h.repo, "show", id)
	if scode != 0 {
		t.Fatalf("show exit %d, want 0", scode)
	}
	if !strings.Contains(shown, "> dbmate status") || !strings.Contains(shown, "create_users") {
		t.Errorf("spool did not recover banner + full raw:\n%s", shown)
	}
}

// fakeDbmateNpm builds a fake "npm" that on `run db:status` prints the npm
// two-line banner then the dbmate status report, exercising #8's banner strip
// + inner-tool dispatch into the dbmate filter.
func buildFakeDbmateNpm(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	src := filepath.Join(dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "main.go"), []byte(fakeDbmateNpmSrc), 0o644); err != nil {
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
		t.Fatalf("build fake npm (dbmate): %v\n%s", err, out)
	}
	return dir
}

const fakeDbmateNpmSrc = `package main

import (
	"fmt"
	"os"
)

func main() {
	script := ""
	if len(os.Args) >= 3 && os.Args[1] == "run" {
		script = os.Args[2]
	}
	if script != "db:status" {
		fmt.Fprintln(os.Stderr, "unknown script")
		os.Exit(1)
	}
	fmt.Println("> demo@1.0.0 db:status")
	fmt.Println("> dbmate status")
	fmt.Println()
	fmt.Print(statusOut)
}

const statusOut = "[X] 20260101000001_create_users.sql\n" +
	"[X] 20260101000002_create_posts.sql\n" +
	"[X] 20260101000003_add_index.sql\n" +
	"[X] 20260101000004_create_sessions.sql\n" +
	"[X] 20260101000005_add_email_col.sql\n" +
	"[X] 20260101000006_create_orders.sql\n" +
	"[X] 20260101000007_add_orders_idx.sql\n" +
	"[X] 20260101000008_create_products.sql\n" +
	"[X] 20260101000009_create_carts.sql\n" +
	"[X] 20260101000010_add_cart_fk.sql\n" +
	"[X] 20260101000011_create_reviews.sql\n" +
	"[X] 20260101000012_add_review_rating.sql\n" +
	"[X] 20260101000013_create_tags.sql\n" +
	"[X] 20260101000014_create_post_tags.sql\n" +
	"[X] 20260101000015_add_slug.sql\n" +
	"[X] 20260101000016_create_media.sql\n" +
	"[X] 20260101000017_add_media_type.sql\n" +
	"[X] 20260101000018_create_notifications.sql\n" +
	"[ ] 20260101000019_add_read_flag.sql\n" +
	"[ ] 20260101000020_create_audit_log.sql\n" +
	"\nApplied: 18\nPending: 2\n"
`
