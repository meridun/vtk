package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestInstallBlockIdempotent(t *testing.T) {
	block := renderBash("/c/tools/vtk/vtk.exe")

	// Fresh file: block appended, single trailing newline.
	got := installBlock("", block)
	if got != block+"\n" {
		t.Fatalf("fresh install = %q, want block+newline", got)
	}

	// Re-running with the same block is a fixed point.
	if again := installBlock(got, block); again != got {
		t.Fatalf("install not idempotent:\n first: %q\nsecond: %q", got, again)
	}

	// Existing unrelated content is preserved above the block, separated by one
	// blank line.
	withPrior := installBlock("export FOO=1\n", block)
	if !strings.HasPrefix(withPrior, "export FOO=1\n\n"+installMarkerBegin) {
		t.Fatalf("prior content not preserved with separator: %q", withPrior)
	}
	if strings.Count(withPrior, installMarkerBegin) != 1 {
		t.Fatalf("expected exactly one managed block, got %q", withPrior)
	}
	// And still idempotent with prior content.
	if again := installBlock(withPrior, block); again != withPrior {
		t.Fatalf("install with prior content not idempotent")
	}
}

func TestInstallBlockUpdatesInPlace(t *testing.T) {
	oldBlock := renderBash("/c/old/vtk.exe")
	newBlock := renderBash("/c/new/vtk.exe")
	installed := installBlock("keep me\n", oldBlock)

	updated := installBlock(installed, newBlock)
	if strings.Count(updated, installMarkerBegin) != 1 {
		t.Fatalf("update left more than one block: %q", updated)
	}
	if strings.Contains(updated, "/c/old/vtk.exe") {
		t.Fatalf("old path survived update: %q", updated)
	}
	if !strings.Contains(updated, "/c/new/vtk.exe") {
		t.Fatalf("new path missing after update: %q", updated)
	}
	if !strings.HasPrefix(updated, "keep me\n") {
		t.Fatalf("surrounding content lost on update: %q", updated)
	}
}

func TestUninstallBlock(t *testing.T) {
	block := renderPwsh(`C:\tools\vtk\vtk.exe`)

	// Block-only file uninstalls to empty.
	res, found := uninstallBlock(installBlock("", block))
	if !found || res != "" {
		t.Fatalf("uninstall of block-only file = (%q, %v), want (\"\", true)", res, found)
	}

	// Surrounding content survives; no doubled blanks left behind.
	installed := installBlock("line A\nline B\n", block)
	res, found = uninstallBlock(installed)
	if !found {
		t.Fatal("expected to find block")
	}
	if res != "line A\nline B\n" {
		t.Fatalf("uninstall left residue: %q", res)
	}

	// No block present → not found, content untouched.
	if res, found := uninstallBlock("nothing here\n"); found || res != "nothing here\n" {
		t.Fatalf("uninstall with no block = (%q, %v), want unchanged/false", res, found)
	}
}

func TestWinToMsys(t *testing.T) {
	cases := map[string]string{
		`C:\Users\me\tools\vtk\vtk.exe`: "/c/Users/me/tools/vtk/vtk.exe",
		`D:\vtk.exe`:                    "/d/vtk.exe",
		"/already/posix":               "/already/posix",
	}
	for in, want := range cases {
		if got := winToMsys(in); got != want {
			t.Errorf("winToMsys(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestRenderPwshQuoting(t *testing.T) {
	block := renderPwsh(`C:\Program Files\vtk\vtk.exe`)
	// Backslashes and spaces must survive verbatim inside the single-quoted PS
	// literal (no escaping), and the guard must be present.
	if !strings.Contains(block, `'C:\Program Files\vtk\vtk.exe'`) {
		t.Fatalf("pwsh path not single-quoted verbatim: %q", block)
	}
	if !strings.Contains(block, "$env:CLAUDECODE") {
		t.Fatalf("pwsh block missing CLAUDECODE guard: %q", block)
	}
}

func TestApplyTargetWritesAndUninstalls(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "nested", ".bashrc") // parent dir does not exist yet
	tgt := shellTarget{name: "bash", path: path, block: renderBash("/c/tools/vtk/vtk.exe")}

	// Dry-run must not create the file.
	if action, err := applyTarget(tgt, false, true); err != nil {
		t.Fatalf("dry-run err: %v", err)
	} else if !strings.Contains(action, "dry-run") {
		t.Fatalf("dry-run action = %q", action)
	}
	if _, err := os.Stat(path); !os.IsNotExist(err) {
		t.Fatal("dry-run created the file")
	}

	// Real install creates parent dir + file.
	if action, err := applyTarget(tgt, false, false); err != nil || action != "installed" {
		t.Fatalf("install = (%q, %v)", action, err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read after install: %v", err)
	}
	if !strings.Contains(string(data), installMarkerBegin) {
		t.Fatal("installed file missing marker")
	}

	// Second install is unchanged (no write needed).
	if action, err := applyTarget(tgt, false, false); err != nil || action != "unchanged" {
		t.Fatalf("re-install = (%q, %v), want unchanged", action, err)
	}

	// Uninstall removes the block.
	if action, err := applyTarget(tgt, true, false); err != nil || action != "removed" {
		t.Fatalf("uninstall = (%q, %v)", action, err)
	}
	data, _ = os.ReadFile(path)
	if strings.Contains(string(data), installMarkerBegin) {
		t.Fatal("block survived uninstall")
	}
}
