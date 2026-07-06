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

// fakeGhSrc is a standalone program compiled into a scratch bin dir and placed
// first on PATH as "gh". It emits canned, non-TTY `gh` output keyed on argv so
// the smoke stays self-contained (no live GitHub, no auth) while still driving
// the real vtk binary end-to-end: registry Lookup on "gh <sub>", the exit-0
// filter gating, the compaction, JSON/view passthrough, and exit-code parity.
//
// Shapes mirror real `gh` piped (non-TTY) output:
//
//	gh issue list  -> TSV: num \t STATE \t title \t labels \t ISO-ts
//	gh pr list     -> TSV: num \t title \t headBranch \t STATE \t ISO-ts
//	gh run list    -> TSV: status \t conclusion \t title \t workflow \t branch \t event \t runID \t elapsed \t ISO-ts
//	gh issue view  -> "title:\t..." field block (leads non-numeric: must pass through)
//	gh <sub> list --json ... -> a JSON array (must pass through structurally intact)
//
// Exit code is forced via VTK_FAKE_GH_CODE (default 0).
const fakeGhSrc = "package main\n" + `
import (
	"fmt"
	"os"
	"strconv"
	"strings"
)

func main() {
	code := 0
	if v := os.Getenv("VTK_FAKE_GH_CODE"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			code = n
		}
	}
	args := os.Args[1:]
	hasJSON := false
	for _, a := range args {
		if a == "--json" {
			hasJSON = true
		}
	}
	sub, verb := "", ""
	if len(args) >= 1 {
		sub = args[0]
	}
	if len(args) >= 2 {
		verb = args[1]
	}
	if hasJSON {
		fmt.Print(jsonArray)
		os.Exit(code)
	}
	switch {
	case sub == "issue" && verb == "list":
		fmt.Print(issueList)
	case sub == "issue" && verb == "view":
		fmt.Print(issueView)
	case sub == "pr" && verb == "list":
		fmt.Print(prList)
	case sub == "run" && verb == "list":
		fmt.Print(runList)
	default:
		fmt.Fprintln(os.Stderr, "unknown fake gh args: "+strings.Join(args, " "))
	}
	os.Exit(code)
}

const issueList = "12\tOPEN\tFix the flux capacitor\tbug,urgent\t2026-07-01T10:00:00Z\n" +
	"11\tOPEN\tAdd a new coverage filter\tenhancement\t2026-06-20T08:30:00Z\n" +
	"9\tCLOSED\tDocument the spool format\tdocs\t2026-05-15T12:00:00Z\n"

const prList = "42\tWire up the gh filter\tfeat/9-gh-filter\tOPEN\t2026-07-05T09:00:00Z\n" +
	"41\tBump go version\tchore/go\tMERGED\t2026-07-02T14:00:00Z\n"

const runList = "completed\tsuccess\tCI\tbuild.yml\tmain\tpush\t7788990011\t45s\t2026-07-06T11:00:00Z\n" +
	"in_progress\t\tCI\tbuild.yml\tfeat/9\tpush\t7788990012\t\t2026-07-06T11:30:00Z\n"

const issueView = "title:\tFix the flux capacitor\n" +
	"state:\tOPEN\n" +
	"author:\tmeridun\n" +
	"labels:\tbug, urgent\n" +
	"--\n" +
	"The capacitor fluxes intermittently under load.\n"

const jsonArray = "[{\"number\":12,\"state\":\"OPEN\",\"title\":\"Fix the flux capacitor\"}," +
	"{\"number\":11,\"state\":\"OPEN\",\"title\":\"Add a new coverage filter\"}]\n"
`

// buildFakeGh compiles the fake into dir/gh(.exe) and returns dir so callers
// can prepend it to PATH.
func buildFakeGh(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	src := filepath.Join(dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(src, "main.go"), []byte(fakeGhSrc), 0o644); err != nil {
		t.Fatal(err)
	}
	bin := filepath.Join(dir, "gh")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command("go", "build", "-o", bin, "main.go")
	cmd.Dir = src
	cmd.Env = append(os.Environ(), "GO111MODULE=off")
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("build fake gh: %v\n%s", err, out)
	}
	return dir
}

// runGh runs the vtk binary with the fake gh first on PATH and the child exit
// code forced via env. Returns combined stdout, stderr, and code.
func (h *harness) runGh(t *testing.T, binDir string, code int, args ...string) (stdout, stderr string, exit int) {
	t.Helper()
	cmd := exec.Command(h.bin, args...)
	cmd.Dir = h.repo
	cmd.Env = append(os.Environ(),
		"LOCALAPPDATA="+h.home,
		"XDG_CACHE_HOME="+h.home,
		"HOME="+h.home,
		"PATH="+binDir+string(os.PathListSeparator)+os.Getenv("PATH"),
		"VTK_FAKE_GH_CODE="+itoa(code),
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

// rawGh runs the fake gh directly (no vtk) to establish parity/passthrough
// baselines for a given argv and exit code.
func rawGh(t *testing.T, binDir string, code int, args ...string) (combined string, exit int) {
	t.Helper()
	bin := filepath.Join(binDir, "gh")
	if runtime.GOOS == "windows" {
		bin += ".exe"
	}
	cmd := exec.Command(bin, args...)
	cmd.Env = append(os.Environ(), "VTK_FAKE_GH_CODE="+itoa(code))
	out, err := cmd.CombinedOutput()
	exit = 0
	if err != nil {
		if ee, ok := err.(*exec.ExitError); ok {
			exit = ee.ExitCode()
		}
	}
	return string(out), exit
}

// TestSmokeGh exercises issue #9 through the real vtk binary: the gh list
// families compact on success, view/JSON shapes pass through structurally
// intact, and exit-code parity holds on success and failure.
func TestSmokeGh(t *testing.T) {
	if _, err := exec.LookPath("go"); err != nil {
		t.Skip("go toolchain not on PATH (needed to build the fake gh)")
	}
	h := newHarness(t)
	binDir := buildFakeGh(t)

	t.Run("issue list is compacted to #<n> <state> <title>, labels dropped, parity", func(t *testing.T) {
		rawOut, rawCode := rawGh(t, binDir, 0, "issue", "list")
		if rawCode != 0 {
			t.Fatalf("raw gh issue list exit %d, want 0", rawCode)
		}
		out, errS, code := h.runGh(t, binDir, 0, "gh", "issue", "list")
		if code != rawCode {
			t.Fatalf("exit %d, want %d (parity); stderr: %s", code, rawCode, errS)
		}
		id := mustOKID(t, out)
		if len(out) >= len(rawOut) {
			t.Errorf("compact output (%d bytes) not smaller than raw (%d bytes)", len(out), len(rawOut))
		}
		for _, want := range []string{"#12 open Fix the flux capacitor", "#9 closed Document the spool format"} {
			if !strings.Contains(out, want) {
				t.Errorf("compact issue list missing %q:\n%s", want, out)
			}
		}
		// Labels dropped (AC: drop label noise unless requested).
		if strings.Contains(out, "urgent") || strings.Contains(out, "enhancement") {
			t.Errorf("label noise leaked into compact issue list:\n%s", out)
		}
		// Raw survives in the spool, recoverable in full with provenance.
		shown, _, scode := h.run(t, h.repo, "show", id)
		if scode != 0 {
			t.Fatalf("show exit %d, want 0", scode)
		}
		if !strings.Contains(shown, "# cmd: gh issue list") {
			t.Errorf("missing provenance header:\n%s", shown)
		}
		if !strings.Contains(shown, "bug,urgent") {
			t.Errorf("spooled raw missing original label column:\n%s", shown)
		}
		// Invariant 3: the invocation/gap log carries no output content.
		meta, err := os.ReadFile(filepath.Join(h.home, "vtk", "invocations.jsonl"))
		if err != nil {
			t.Fatalf("read invocation log: %v", err)
		}
		if strings.Contains(string(meta), "flux capacitor") {
			t.Error("invocation log leaked command output content")
		}
	})

	t.Run("pr list is compacted to #<n> <state> <title>, head branch dropped", func(t *testing.T) {
		out, errS, code := h.runGh(t, binDir, 0, "gh", "pr", "list")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		mustOKID(t, out)
		for _, want := range []string{"#42 open Wire up the gh filter", "#41 merged Bump go version"} {
			if !strings.Contains(out, want) {
				t.Errorf("compact pr list missing %q:\n%s", want, out)
			}
		}
		if strings.Contains(out, "feat/9-gh-filter") || strings.Contains(out, "chore/go") {
			t.Errorf("head-branch column leaked into compact pr list:\n%s", out)
		}
	})

	t.Run("run list is compacted with conclusion+workflow, low-signal cols dropped", func(t *testing.T) {
		out, errS, code := h.runGh(t, binDir, 0, "gh", "run", "list")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		mustOKID(t, out)
		if !strings.Contains(out, "success CI · build.yml") {
			t.Errorf("completed run line missing/altered:\n%s", out)
		}
		// In-progress run has no conclusion: falls back to the status field.
		if !strings.Contains(out, "in_progress CI · build.yml") {
			t.Errorf("in-progress run line missing status fallback:\n%s", out)
		}
		// runID / event / elapsed dropped as low-signal.
		if strings.Contains(out, "7788990011") || strings.Contains(out, "45s") || strings.Contains(out, "push") {
			t.Errorf("low-signal run columns leaked:\n%s", out)
		}
	})

	t.Run("issue view (non-list shape) passes through unchanged", func(t *testing.T) {
		rawOut, _ := rawGh(t, binDir, 0, "issue", "view", "12")
		out, errS, code := h.runGh(t, binDir, 0, "gh", "issue", "view", "12")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		if okRe.MatchString(out) {
			t.Errorf("view output was spooled/filtered (emitted OK), should pass through:\n%s", out)
		}
		if out != rawOut {
			t.Errorf("view output altered:\nraw: %q\nvtk: %q", rawOut, out)
		}
	})

	t.Run("--json form passes through structurally intact", func(t *testing.T) {
		rawOut, _ := rawGh(t, binDir, 0, "issue", "list", "--json", "number,state,title")
		out, errS, code := h.runGh(t, binDir, 0, "gh", "issue", "list", "--json", "number,state,title")
		if code != 0 {
			t.Fatalf("exit %d, want 0; stderr: %s", code, errS)
		}
		if okRe.MatchString(out) {
			t.Errorf("--json output was spooled/filtered (emitted OK), should pass through:\n%s", out)
		}
		if out != rawOut {
			t.Errorf("--json output altered (not structurally intact):\nraw: %q\nvtk: %q", rawOut, out)
		}
		if !strings.Contains(out, `"number":12`) {
			t.Errorf("--json payload missing expected structure:\n%s", out)
		}
	})

	t.Run("nonzero exit stays raw with exit-code parity", func(t *testing.T) {
		rawOut, rawCode := rawGh(t, binDir, 1, "issue", "list")
		if rawCode != 1 {
			t.Fatalf("raw gh exit %d, want 1", rawCode)
		}
		out, errS, code := h.runGh(t, binDir, 1, "gh", "issue", "list")
		if code != rawCode {
			t.Errorf("exit %d, want %d (parity)", code, rawCode)
		}
		if okRe.MatchString(out + errS) {
			t.Errorf("failed run emitted an OK id (should stay raw, exit not in allowlist):\n%s%s", out, errS)
		}
		// Failure output survives unaltered (invariant 2: out-of-allowlist
		// degrades to raw, never lost/rewritten output).
		if out+errS != rawOut {
			t.Errorf("failed output altered:\nraw: %q\nvtk: %q", rawOut, out+errS)
		}
	})
}
