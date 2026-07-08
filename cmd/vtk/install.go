package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

// vtk install — wire the token-killer wrappers into a shell rc/profile so
// git/gh/npm route through vtk inside Claude Code sessions without the agent
// having to prefix every command. The wrappers are guarded on $CLAUDECODE, so
// the block is inert in normal interactive shells, and point at THIS binary's
// own path (os.Executable), so install is self-locating — no assumption about
// where vtk lives. The managed region is delimited by markers so re-running is
// idempotent (same binary path → byte-identical file) and --uninstall is exact.

const (
	installMarkerBegin = "# >>> vtk wrappers >>>"
	installMarkerEnd   = "# <<< vtk wrappers <<<"
	installManagedNote = "  (managed by `vtk install` — do not edit between the markers)"
)

// shellTarget is one resolved rc/profile file and the block to splice into it.
type shellTarget struct {
	name  string // "bash" | "pwsh"
	path  string // rc/profile file (may not exist yet)
	block string // marker-delimited wrapper block, no trailing newline
}

func cmdInstall(args []string) int {
	var (
		only      string
		dryRun    bool
		uninstall bool
		printOnly bool
	)
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--shell":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "vtk install: --shell requires bash or pwsh")
				return 2
			}
			i++
			only = args[i]
		case "--dry-run":
			dryRun = true
		case "--uninstall":
			uninstall = true
		case "--print":
			printOnly = true
		default:
			fmt.Fprintf(os.Stderr, "vtk install: unexpected argument %q\n", args[i])
			return 2
		}
	}
	if only != "" && only != "bash" && only != "pwsh" {
		fmt.Fprintf(os.Stderr, "vtk install: --shell must be bash or pwsh, got %q\n", only)
		return 2
	}

	exe, err := os.Executable()
	if err != nil {
		fmt.Fprintf(os.Stderr, "vtk install: cannot locate own binary: %v\n", err)
		return 1
	}
	if abs, err := filepath.Abs(exe); err == nil {
		exe = abs
	}

	targets, warns := resolveTargets(only, exe)
	for _, w := range warns {
		fmt.Fprintln(os.Stderr, "vtk install: "+w)
	}
	if len(targets) == 0 {
		fmt.Fprintln(os.Stderr, "vtk install: no shell targets resolved")
		return 1
	}

	// --print is side-effect-free: emit the block(s) for the user to place by
	// hand (or pipe), touching no files.
	if printOnly {
		for _, t := range targets {
			fmt.Printf("# --- %s (%s) ---\n%s\n", t.name, t.path, t.block)
		}
		return 0
	}

	rc := 0
	for _, t := range targets {
		action, err := applyTarget(t, uninstall, dryRun)
		if err != nil {
			fmt.Fprintf(os.Stderr, "vtk install: %s: %v\n", t.name, err)
			rc = 1
			continue
		}
		fmt.Printf("%s: %s (%s)\n", t.name, action, t.path)
	}
	return rc
}

// resolveTargets locates each requested shell's rc/profile and renders its
// wrapper block. Unresolvable shells become warnings (skipped), never hard
// failures — installing bash on a box without pwsh should still succeed.
func resolveTargets(only, exe string) (targets []shellTarget, warns []string) {
	want := func(s string) bool { return only == "" || only == s }

	if want("bash") {
		home, err := os.UserHomeDir()
		if err != nil {
			warns = append(warns, "bash: cannot resolve home dir: "+err.Error())
		} else {
			targets = append(targets, shellTarget{
				name:  "bash",
				path:  filepath.Join(home, ".bashrc"),
				block: renderBash(bashExe(exe)),
			})
		}
	}
	if want("pwsh") {
		if p, err := pwshProfilePath(); err != nil {
			warns = append(warns, "pwsh: "+err.Error()+" (skipped)")
		} else {
			targets = append(targets, shellTarget{
				name:  "pwsh",
				path:  p,
				block: renderPwsh(exe),
			})
		}
	}
	return targets, warns
}

// applyTarget reads the current file, splices or removes the managed block, and
// writes it back. Returns a human action word. Never writes when the result is
// byte-identical (reports "unchanged") so re-runs and --dry-run are honest.
func applyTarget(t shellTarget, uninstall, dryRun bool) (string, error) {
	old := ""
	if data, err := os.ReadFile(t.path); err == nil {
		old = string(data)
	} else if !os.IsNotExist(err) {
		return "", err
	}

	var next, action string
	if uninstall {
		res, found := uninstallBlock(old)
		if !found {
			return "no block present", nil
		}
		next, action = res, "removed"
	} else {
		next = installBlock(old, t.block)
		switch {
		case next == old:
			return "unchanged", nil
		case strings.Contains(old, installMarkerBegin):
			action = "updated"
		default:
			action = "installed"
		}
	}

	if dryRun {
		return action + " (dry-run)", nil
	}
	if err := os.MkdirAll(filepath.Dir(t.path), 0o755); err != nil {
		return "", err
	}
	if err := os.WriteFile(t.path, []byte(next), 0o644); err != nil {
		return "", err
	}
	return action, nil
}

// installBlock returns file content with exactly one managed block, appended
// after the existing (non-managed) content. Idempotent: it first strips any
// prior managed block, so re-running with the same binary path is a fixed point.
func installBlock(existing, block string) string {
	base, _ := removeBlock(existing)
	base = strings.TrimRight(base, "\n")
	if base == "" {
		return block + "\n"
	}
	return base + "\n\n" + block + "\n"
}

// uninstallBlock removes the managed block and returns the cleaned content and
// whether a block was present.
func uninstallBlock(existing string) (string, bool) {
	res, found := removeBlock(existing)
	if !found {
		return existing, false
	}
	res = strings.TrimRight(res, "\n")
	if res == "" {
		return "", true
	}
	return res + "\n", true
}

// removeBlock excises the marker-delimited region (inclusive) and trims the
// newlines that bordered it so no doubled blank lines are left behind.
func removeBlock(existing string) (string, bool) {
	b := strings.Index(existing, installMarkerBegin)
	if b < 0 {
		return existing, false
	}
	e := strings.Index(existing, installMarkerEnd)
	if e < b {
		return existing, false
	}
	end := e + len(installMarkerEnd)
	before := strings.TrimRight(existing[:b], "\n")
	after := strings.TrimLeft(existing[end:], "\n")
	switch {
	case before == "" && after == "":
		return "", true
	case before == "":
		return after, true
	case after == "":
		return before + "\n", true
	default:
		return before + "\n\n" + after, true
	}
}

// renderBash renders the POSIX-shell wrapper block. exe must already be in a
// form the shell's test/exec understands (see bashExe).
func renderBash(exe string) string {
	return strings.Join([]string{
		installMarkerBegin + installManagedNote,
		`if [ -n "$CLAUDECODE" ] && [ -x "` + exe + `" ]; then`,
		`  vtk() { "` + exe + `" "$@"; }`,
		`  git() { vtk git "$@"; }`,
		`  gh()  { vtk gh "$@"; }`,
		`  npm() { vtk npm "$@"; }`,
		`fi`,
		installMarkerEnd,
	}, "\n")
}

// renderPwsh renders the PowerShell wrapper block. The exe path is embedded in a
// single-quoted PS string (literal; embedded quotes doubled), so Windows
// backslashes need no escaping.
func renderPwsh(exe string) string {
	q := "'" + strings.ReplaceAll(exe, "'", "''") + "'"
	return strings.Join([]string{
		installMarkerBegin + installManagedNote,
		"if ($env:CLAUDECODE -and (Test-Path " + q + ")) {",
		"    function vtk { & " + q + " @args }",
		"    function git { & " + q + " git @args }",
		"    function gh  { & " + q + " gh  @args }",
		"    function npm { & " + q + " npm @args }",
		"}",
		installMarkerEnd,
	}, "\n")
}

// bashExe converts the binary path to the form a bash test/exec expects. On
// Windows that means the MSYS/Git-Bash form (/c/Users/... not C:\Users\...);
// elsewhere the native path is already correct.
func bashExe(exe string) string {
	if runtime.GOOS == "windows" {
		return winToMsys(exe)
	}
	return exe
}

// winToMsys rewrites a Windows path to the MSYS form Git Bash understands:
// C:\Users\x\vtk.exe -> /c/Users/x/vtk.exe.
func winToMsys(p string) string {
	if len(p) >= 2 && p[1] == ':' {
		drive := strings.ToLower(p[:1])
		return "/" + drive + strings.ReplaceAll(p[2:], `\`, "/")
	}
	return strings.ReplaceAll(p, `\`, "/")
}

// pwshProfilePath resolves the current user's all-hosts PowerShell profile path
// by asking pwsh itself (robust to OneDrive Documents redirection). Falls back
// to Windows PowerShell 5.1 (powershell.exe) if pwsh 7+ is not installed.
func pwshProfilePath() (string, error) {
	for _, bin := range []string{"pwsh", "powershell"} {
		out, err := exec.Command(bin, "-NoProfile", "-Command", "$PROFILE.CurrentUserAllHosts").Output()
		if err != nil {
			continue
		}
		if p := strings.TrimSpace(string(out)); p != "" {
			return p, nil
		}
	}
	return "", fmt.Errorf("PowerShell not found on PATH")
}
