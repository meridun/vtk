// Package filter maps command/subcommand patterns to pure filter functions.
// Filters take raw captured output and return a compacted form; they never
// spawn processes or touch the filesystem (docs/Architecture.md invariant 4).
package filter

import (
	"regexp"
	"strings"

	dbmatef "github.com/meridun/vtk/internal/filter/dbmate"
	eslintf "github.com/meridun/vtk/internal/filter/eslint"
	filesf "github.com/meridun/vtk/internal/filter/files"
	ghf "github.com/meridun/vtk/internal/filter/gh"
	gitf "github.com/meridun/vtk/internal/filter/git"
	mochaf "github.com/meridun/vtk/internal/filter/mocha"
	"github.com/meridun/vtk/internal/filter/tomlfilter"
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

// regexEntry is a filter matched by a regex against the whole command string,
// rather than by an exact "cmd"/"cmd subcommand" key. Declarative TOML filters
// register here; they are consulted only after the exact-key map misses, so the
// hand-written Go families keep precedence and their behavior is unchanged.
type regexEntry struct {
	re *regexp.Regexp
	Entry
}

// Registry maps "cmd" or "cmd subcommand" keys to entries, with a
// regex-matched fallback list for declarative filters.
type Registry struct {
	entries map[string]Entry
	regexes []regexEntry
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

// RegisterRegex binds a regex (matched against the full command string, argv
// joined by spaces) to a filter running for the given child exit codes. No
// codes means exit 0 only. Regex entries are the declarative-filter path and
// are checked only after the exact-key map misses.
func (r *Registry) RegisterRegex(re *regexp.Regexp, f Func, codes ...int) {
	if len(codes) == 0 {
		codes = []int{0}
	}
	set := make(map[int]bool, len(codes))
	for _, c := range codes {
		set[c] = true
	}
	r.regexes = append(r.regexes, regexEntry{re: re, Entry: Entry{Fn: f, ExitCodes: set}})
}

// Lookup matches argv against the registry: exact "argv[0] argv[1]" first, then
// bare "argv[0]", then (only if both miss) the regex fallback list against the
// full command string. Invocations whose second token is a flag (e.g.
// `git -C dir status`) miss the exact keys; a regex filter may still claim them
// if its pattern matches. A total miss falls through to passthrough — the gap
// log will show whether that pattern is worth handling.
func (r *Registry) Lookup(argv []string) (Entry, bool) {
	if len(argv) == 0 {
		return Entry{}, false
	}
	if len(argv) >= 2 {
		if e, ok := r.entries[argv[0]+" "+argv[1]]; ok {
			return e, true
		}
	}
	if e, ok := r.entries[argv[0]]; ok {
		return e, true
	}
	cmd := strings.Join(argv, " ")
	for _, re := range r.regexes {
		if re.re.MatchString(cmd) {
			return re.Entry, true
		}
	}
	return Entry{}, false
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
	// mocha reports test failures via exit 1; that failing run is exactly what
	// to compact (fold passing specs, keep failures + summary). Exit 2+ is a
	// mocha/config error and stays raw. Delegation from the `npm run` dispatch
	// layer reuses these keys via the runner's inner-tool lookup (#8, #12).
	r.RegisterCodes("mocha", mochaf.Filter, 0, 1)
	r.RegisterCodes("npx mocha", mochaf.Filter, 0, 1)
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
	// Declarative TOML filters (embedded at build) register on the regex path,
	// so they coexist with the Go families above without shadowing any exact
	// key. The embedded defs are parse/validate/fixture-checked by the
	// tomlfilter tests; a load error here would mean a corrupt binary, so skip
	// them and keep the Go families working rather than fail the whole registry.
	if defs, err := tomlfilter.Load(); err == nil {
		for _, d := range defs {
			r.RegisterRegex(d.Match, Func(d.Fn), d.ExitCodes...)
		}
	}
	return r
}
