// Package filter maps command/subcommand patterns to pure filter functions.
// Filters take raw captured output and return a compacted form; they never
// spawn processes or touch the filesystem (docs/Architecture.md invariant 4).
package filter

import (
	dbmatef "github.com/meridun/vtk/internal/filter/dbmate"
	eslintf "github.com/meridun/vtk/internal/filter/eslint"
	filesf "github.com/meridun/vtk/internal/filter/files"
	ghf "github.com/meridun/vtk/internal/filter/gh"
	gitf "github.com/meridun/vtk/internal/filter/git"
)

// Func is a pure filter: raw captured output in, compacted output out.
// Returning the input unchanged means "nothing to elide".
type Func func(raw string) string

// Entry is a registered filter plus the child exit codes on which it may run.
// Most tools only produce filterable output on success (code 0); report-style
// tools (eslint, tsc, ...) treat a nonzero "problems found" code as a report,
// not a failure, and declare those codes here. Exit-code parity is unaffected:
// the allowlist gates only whether the filter runs versus raw passthrough —
// vtk always returns the child's own code (docs/Architecture.md invariant 1,
// decision registry #7).
type Entry struct {
	Fn        Func
	ExitCodes map[int]bool
}

// Filters reports whether this entry is allowed to run for child exit code.
func (e Entry) Filters(code int) bool {
	return e.ExitCodes[code]
}

// Registry maps "cmd" or "cmd subcommand" keys to entries.
type Registry struct {
	entries map[string]Entry
}

// New returns an empty registry.
func New() *Registry {
	return &Registry{entries: make(map[string]Entry)}
}

// Register binds a key ("git status", "ls", ...) to a filter that runs only on
// a clean (exit 0) child.
func (r *Registry) Register(key string, f Func) {
	r.RegisterCodes(key, f, 0)
}

// RegisterCodes binds a key to a filter that runs for any of the given child
// exit codes. Passing no codes registers exit 0 only (same as Register).
func (r *Registry) RegisterCodes(key string, f Func, codes ...int) {
	if len(codes) == 0 {
		codes = []int{0}
	}
	set := make(map[int]bool, len(codes))
	for _, c := range codes {
		set[c] = true
	}
	r.entries[key] = Entry{Fn: f, ExitCodes: set}
}

// Lookup matches argv against the registry: "argv[0] argv[1]" first, then
// bare "argv[0]". Invocations whose second token is a flag (e.g.
// `git -C dir status`) intentionally miss and fall through to passthrough —
// the gap log will show whether that pattern is worth handling.
func (r *Registry) Lookup(argv []string) (Entry, bool) {
	if len(argv) == 0 {
		return Entry{}, false
	}
	if len(argv) >= 2 {
		if e, ok := r.entries[argv[0]+" "+argv[1]]; ok {
			return e, true
		}
	}
	e, ok := r.entries[argv[0]]
	return e, ok
}

// Default returns the registry with all shipped filter families wired in.
func Default() *Registry {
	r := New()
	r.Register("git status", gitf.Status)
	r.Register("git log", gitf.Log)
	r.Register("git diff", gitf.Diff)
	r.Register("git show", gitf.Show)
	r.Register("git add", gitf.Add)
	r.Register("git commit", gitf.Commit)
	r.Register("git push", gitf.Push)
	r.Register("git pull", gitf.Pull)
	r.Register("git branch", gitf.Branch)
	// eslint reports "problems found" via exit 1; that output is the whole
	// point to compact. Exit 2+ is a fatal/config error and stays raw.
	r.RegisterCodes("eslint", eslintf.Filter, 0, 1)
	r.RegisterCodes("npx eslint", eslintf.Filter, 0, 1)
	// gh list families: TSV human output on success (exit 0). `gh --json`
	// forms hit the same keys but pass through structurally intact (isJSON).
	r.Register("gh issue", ghf.IssueList)
	r.Register("gh pr", ghf.PrList)
	r.Register("gh run", ghf.RunList)
	// files/search family: clean-run-only (grep exit 1 = no matches = no
	// output worth compacting; find/ls nonzero exits keep raw error output).
	r.Register("ls", filesf.Ls)
	r.Register("grep", filesf.Grep)
	r.Register("find", filesf.Find)
	// dbmate migration family (direct, and via the npm-run inner-tool dispatch
	// wired in cmd/vtk): status collapses the applied-migrations list to a
	// count and keeps pending + summary; up/down/migrate/rollback drop progress
	// chatter and keep result + error lines. Clean-run-only — a failed
	// migration exits nonzero and passes through raw, preserving the error.
	r.Register("dbmate status", dbmatef.Status)
	r.Register("dbmate up", dbmatef.Migrate)
	r.Register("dbmate down", dbmatef.Migrate)
	r.Register("dbmate migrate", dbmatef.Migrate)
	r.Register("dbmate rollback", dbmatef.Migrate)
	return r
}
