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

// buildGHStub compiles a fake `gh` into an isolated dir and returns that dir.
// The stub is deterministic and offline, so the `vtk gaps --file-issues`
// filing path (#34) can be exercised end-to-end through the real binary
// without touching the network or filing real issues:
//   - `gh issue list ... --json title` echoes GH_STUB_TITLES (a JSON array of
//     titles) so dedupe against open Filter issues is controllable.
//   - `gh issue create --title T ...` records T to GH_STUB_CREATED and prints a
//     fake URL, so the --yes write path is observable (and its absence in
//     dry-run is assertable).
//   - GH_STUB_FAIL makes `issue list` exit nonzero, exercising the refuse-to-
//     file-when-dedupe-unavailable guard.
func buildGHStub(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	src := filepath.Join(dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "main.go"), []byte(ghStubSrc), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "go.mod"), []byte("module ghstub\n\ngo 1.21\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	bin := filepath.Join(dir, "gh")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command("go", "build", "-o", bin, ".")
	cmd.Dir = src
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("build gh stub: %v\n%s", err, out)
	}
	return dir
}

const ghStubSrc = `package main

import (
	"encoding/json"
	"fmt"
	"os"
)

func main() {
	var list, create bool
	for _, a := range os.Args[1:] {
		switch a {
		case "list":
			list = true
		case "create":
			create = true
		}
	}
	switch {
	case list:
		if os.Getenv("GH_STUB_FAIL") != "" {
			fmt.Fprintln(os.Stderr, "gh stub: forced failure")
			os.Exit(1)
		}
		var titles []string
		if v := os.Getenv("GH_STUB_TITLES"); v != "" {
			_ = json.Unmarshal([]byte(v), &titles)
		}
		type row struct {
			Title string ` + "`json:\"title\"`" + `
		}
		rows := make([]row, 0, len(titles))
		for _, t := range titles {
			rows = append(rows, row{Title: t})
		}
		b, _ := json.Marshal(rows)
		os.Stdout.Write(b)
	case create:
		if f := os.Getenv("GH_STUB_CREATED"); f != "" {
			var title string
			args := os.Args[1:]
			for i, a := range args {
				if a == "--title" && i+1 < len(args) {
					title = args[i+1]
				}
			}
			fh, _ := os.OpenFile(f, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
			fmt.Fprintln(fh, title)
			fh.Close()
		}
		fmt.Println("https://example.test/issues/999")
	}
}
`

// writeGapLog overwrites the harness's invocation metadata log with lines.
func (h *harness) writeGapLog(t *testing.T, lines ...string) {
	t.Helper()
	dir := filepath.Join(h.home, "vtk")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	body := strings.Join(lines, "\n") + "\n"
	if err := os.WriteFile(filepath.Join(dir, "invocations.jsonl"), []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
}

// TestSmokeGapsFileIssues exercises `vtk gaps --file-issues` (#34) end-to-end
// through the real binary against an offline gh stub: threshold selection,
// machine-readable exclusion, dedupe, dry-run-vs-write gating, floor clamping,
// and the arg-validation exit codes. The synthetic gap log is metadata only
// (redacted command family + byte counts), matching the wrap path's writes.
func TestSmokeGapsFileIssues(t *testing.T) {
	h := newHarness(t)
	stubDir := buildGHStub(t)
	// Shadow any real gh with the offline stub; the harness's run() reads
	// os.Environ(), so t.Setenv propagates to the child.
	t.Setenv("PATH", stubDir+string(os.PathListSeparator)+os.Getenv("PATH"))

	// docker: 3 gap calls, 60000 raw (over default 50 KiB / 3-call threshold).
	// helm:   machine-readable (--output json) — excluded from filing.
	// cargo:  3 gap calls but only 15000 raw — below the byte threshold.
	overThreshold := []string{
		`{"time":"2026-07-08T10:00:00Z","cmd":"docker build .","raw_bytes":20000,"filtered":false,"reason":"no-filter"}`,
		`{"time":"2026-07-08T10:01:00Z","cmd":"docker build .","raw_bytes":20000,"filtered":false,"reason":"no-filter"}`,
		`{"time":"2026-07-08T10:02:00Z","cmd":"docker build .","raw_bytes":20000,"filtered":false,"reason":"no-filter"}`,
		`{"time":"2026-07-08T10:03:00Z","cmd":"helm template x --output json","raw_bytes":100000,"filtered":false,"reason":"no-filter"}`,
		`{"time":"2026-07-08T10:04:00Z","cmd":"helm template x --output json","raw_bytes":100000,"filtered":false,"reason":"no-filter"}`,
		`{"time":"2026-07-08T10:05:00Z","cmd":"helm template x --output json","raw_bytes":100000,"filtered":false,"reason":"no-filter"}`,
		`{"time":"2026-07-08T10:06:00Z","cmd":"cargo build","raw_bytes":5000,"filtered":false,"reason":"no-filter"}`,
		`{"time":"2026-07-08T10:07:00Z","cmd":"cargo build","raw_bytes":5000,"filtered":false,"reason":"no-filter"}`,
		`{"time":"2026-07-08T10:08:00Z","cmd":"cargo build","raw_bytes":5000,"filtered":false,"reason":"no-filter"}`,
	}

	t.Run("dry-run plans over-threshold gaps, excludes json and sub-threshold", func(t *testing.T) {
		h.writeGapLog(t, overThreshold...)
		t.Setenv("GH_STUB_TITLES", "")
		created := filepath.Join(t.TempDir(), "created.txt")
		t.Setenv("GH_STUB_CREATED", created)

		out, stderr, code := h.run(t, h.repo, "gaps", "--file-issues")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, stderr)
		}
		if !strings.Contains(out, "Filter: docker") || !strings.Contains(out, "60000") {
			t.Errorf("docker not planned as expected:\n%s", out)
		}
		if strings.Contains(out, "helm") {
			t.Errorf("machine-readable family leaked into plan:\n%s", out)
		}
		if strings.Contains(out, "cargo") {
			t.Errorf("sub-threshold family leaked into plan:\n%s", out)
		}
		if !strings.Contains(out, "dry-run") {
			t.Errorf("missing dry-run banner:\n%s", out)
		}
		if _, err := os.Stat(created); !os.IsNotExist(err) {
			t.Errorf("dry-run created an issue (marker exists): %v", err)
		}
	})

	t.Run("dedupe skips a family with an open Filter issue", func(t *testing.T) {
		h.writeGapLog(t, overThreshold...)
		t.Setenv("GH_STUB_TITLES", `["Filter: docker (auto-filed from vtk gaps)"]`)

		out, stderr, code := h.run(t, h.repo, "gaps", "--file-issues")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, stderr)
		}
		if !strings.Contains(out, "skip docker") {
			t.Errorf("expected docker dedupe skip:\n%s", out)
		}
		if !strings.Contains(out, "nothing to file") {
			t.Errorf("expected nothing-to-file after dedupe:\n%s", out)
		}
	})

	t.Run("--yes files via gh create; only over-threshold family filed", func(t *testing.T) {
		h.writeGapLog(t, overThreshold...)
		t.Setenv("GH_STUB_TITLES", "")
		created := filepath.Join(t.TempDir(), "created.txt")
		t.Setenv("GH_STUB_CREATED", created)

		out, stderr, code := h.run(t, h.repo, "gaps", "--file-issues", "--yes")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, stderr)
		}
		if !strings.Contains(out, "filed docker") || !strings.Contains(out, "https://example.test/issues/999") {
			t.Errorf("expected docker filed with stub URL:\n%s", out)
		}
		body, err := os.ReadFile(created)
		if err != nil {
			t.Fatalf("read create marker: %v", err)
		}
		if !strings.Contains(string(body), "Filter: docker") {
			t.Errorf("create marker missing docker title:\n%s", body)
		}
		if strings.Contains(string(body), "helm") || strings.Contains(string(body), "cargo") {
			t.Errorf("excluded family was filed:\n%s", body)
		}
	})

	t.Run("refuses to file when dedupe fetch fails", func(t *testing.T) {
		h.writeGapLog(t, overThreshold...)
		t.Setenv("GH_STUB_FAIL", "1")
		defer os.Unsetenv("GH_STUB_FAIL")

		out, stderr, code := h.run(t, h.repo, "gaps", "--file-issues")
		if code != 1 {
			t.Fatalf("exit %d, want 1; out: %s stderr: %s", code, out, stderr)
		}
		if !strings.Contains(stderr, "refusing to file") {
			t.Errorf("expected refuse-to-file message:\n%s", stderr)
		}
	})

	t.Run("below-floor thresholds clamp up and short-circuit before gh", func(t *testing.T) {
		// A single sub-4KiB gap: after clamping to the 4 KiB / 2-call floor it
		// is still under threshold, so no candidates and no gh call.
		h.writeGapLog(t,
			`{"time":"2026-07-08T11:00:00Z","cmd":"cargo build","raw_bytes":300,"filtered":false,"reason":"no-filter"}`,
			`{"time":"2026-07-08T11:01:00Z","cmd":"cargo build","raw_bytes":300,"filtered":false,"reason":"no-filter"}`,
		)
		t.Setenv("GH_STUB_FAIL", "1") // gh would fail if reached; it must not be
		defer os.Unsetenv("GH_STUB_FAIL")

		out, stderr, code := h.run(t, h.repo, "gaps", "--file-issues", "--min-bytes", "1", "--min-calls", "1")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, stderr)
		}
		if !strings.Contains(stderr, "below floor; using 4096") || !strings.Contains(stderr, "below floor; using 2") {
			t.Errorf("expected floor-clamp warnings:\n%s", stderr)
		}
		if !strings.Contains(out, "no gap families over threshold (min-bytes=4096, min-calls=2)") {
			t.Errorf("expected clamped no-candidates line:\n%s", out)
		}
	})

	t.Run("arg validation exits 2", func(t *testing.T) {
		cases := [][]string{
			{"gaps", "--yes"},                               // --yes without --file-issues
			{"gaps", "--min-bytes", "100000"},               // --min-bytes without --file-issues
			{"gaps", "--bogus"},                             // unknown arg
			{"gaps", "--file-issues", "--min-bytes"},        // missing value
			{"gaps", "--file-issues", "--min-bytes", "abc"}, // non-numeric
		}
		for _, args := range cases {
			if _, _, code := h.run(t, h.repo, args...); code != 2 {
				t.Errorf("%v: exit %d, want 2", args, code)
			}
		}
	})
}
