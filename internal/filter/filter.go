// Package filter maps command/subcommand patterns to pure filter functions.
// Filters take raw captured output and return a compacted form; they never
// spawn processes or touch the filesystem (docs/Architecture.md invariant 4).
package filter

import gitf "github.com/meridun/vtk/internal/filter/git"

// Func is a pure filter: raw captured output in, compacted output out.
// Returning the input unchanged means "nothing to elide".
type Func func(raw string) string

// Registry maps "cmd" or "cmd subcommand" keys to filters.
type Registry struct {
	entries map[string]Func
}

// New returns an empty registry.
func New() *Registry {
	return &Registry{entries: make(map[string]Func)}
}

// Register binds a key ("git status", "ls", ...) to a filter.
func (r *Registry) Register(key string, f Func) {
	r.entries[key] = f
}

// Lookup matches argv against the registry: "argv[0] argv[1]" first, then
// bare "argv[0]". Invocations whose second token is a flag (e.g.
// `git -C dir status`) intentionally miss and fall through to passthrough —
// the gap log will show whether that pattern is worth handling.
func (r *Registry) Lookup(argv []string) (Func, bool) {
	if len(argv) == 0 {
		return nil, false
	}
	if len(argv) >= 2 {
		if f, ok := r.entries[argv[0]+" "+argv[1]]; ok {
			return f, true
		}
	}
	f, ok := r.entries[argv[0]]
	return f, ok
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
	return r
}
