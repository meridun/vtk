package filter

import (
	"regexp"
	"testing"
)

func TestLookup(t *testing.T) {
	r := Default()
	cases := []struct {
		name string
		argv []string
		want bool
	}{
		{"git status matches", []string{"git", "status"}, true},
		{"git commit with args matches", []string{"git", "commit", "-m", "msg"}, true},
		{"eslint matches", []string{"eslint", "."}, true},
		{"npx eslint matches", []string{"npx", "eslint", "src"}, true},
		{"unknown git subcommand misses", []string{"git", "frobnicate"}, false},
		{"bare git misses", []string{"git"}, false},
		{"unknown command misses", []string{"kubectl", "get", "pods"}, false},
		{"flag before subcommand misses (gap-logged)", []string{"git", "-C", "dir", "status"}, false},
		{"empty argv misses", nil, false},
		{"bare ls matches", []string{"ls"}, true},
		{"ls with args matches", []string{"ls", "serverjs"}, true},
		{"grep with flags matches", []string{"grep", "-rn", "pat", "dir"}, true},
		{"find with args matches", []string{"find", ".", "-type", "f"}, true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			e, ok := r.Lookup(tc.argv)
			if ok != tc.want {
				t.Errorf("Lookup(%v) ok = %v, want %v", tc.argv, ok, tc.want)
			}
			if ok && e.Fn == nil {
				t.Error("matched but filter is nil")
			}
		})
	}
}

func TestBareCommandKeyMatches(t *testing.T) {
	r := New()
	called := false
	r.Register("ls", func(raw string) string { called = true; return raw })
	e, ok := r.Lookup([]string{"ls", "-la"})
	if !ok {
		t.Fatal("bare-command key did not match")
	}
	e.Fn("x")
	if !called {
		t.Error("wrong filter returned")
	}
}

// Register defaults to filtering exit 0 only; every git family entry is
// clean-run-only. Report-style tools declare extra codes via RegisterCodes.
func TestExitCodeAllowlist(t *testing.T) {
	r := Default()

	git, ok := r.Lookup([]string{"git", "status"})
	if !ok {
		t.Fatal("git status not registered")
	}
	if !git.Filters(0) {
		t.Error("git status should filter exit 0")
	}
	if git.Filters(1) {
		t.Error("git status must not filter exit 1 (failures emit raw)")
	}

	es, ok := r.Lookup([]string{"eslint", "."})
	if !ok {
		t.Fatal("eslint not registered")
	}
	if !es.Filters(0) || !es.Filters(1) {
		t.Error("eslint should filter exits 0 and 1 (problems found is a report)")
	}
	if es.Filters(2) {
		t.Error("eslint must not filter exit 2 (fatal/config error stays raw)")
	}
}

// RegisterCodes with no codes is equivalent to Register (exit 0 only).
func TestRegisterCodesDefaultsToZero(t *testing.T) {
	r := New()
	r.RegisterCodes("foo", func(raw string) string { return raw })
	e, ok := r.Lookup([]string{"foo"})
	if !ok {
		t.Fatal("foo not registered")
	}
	if !e.Filters(0) || e.Filters(1) {
		t.Errorf("default allowlist should be {0}, got %v", e.ExitCodes)
	}
}

// RegisterRegex filters match against the full command string and are consulted
// only after the exact-key map misses.
func TestRegisterRegexLookup(t *testing.T) {
	r := New()
	marker := "REGEX"
	r.RegisterRegex(regexp.MustCompile(`^cargo (build|test)\b`), func(string) string { return marker }, 0, 1)

	if _, ok := r.Lookup([]string{"cargo", "clippy"}); ok {
		t.Error("cargo clippy should not match `build|test` regex")
	}
	e, ok := r.Lookup([]string{"cargo", "build", "--release"})
	if !ok {
		t.Fatal("cargo build should match the regex filter")
	}
	if e.Fn("x") != marker {
		t.Error("wrong filter returned for regex match")
	}
	if !e.Filters(0) || !e.Filters(1) {
		t.Error("regex filter should honor its declared exit codes")
	}
}

// Exact keys win over regex fallbacks: a Go family key is never shadowed.
func TestExactKeyBeatsRegex(t *testing.T) {
	r := New()
	r.Register("git status", func(string) string { return "KEY" })
	r.RegisterRegex(regexp.MustCompile(`^git`), func(string) string { return "REGEX" })

	e, ok := r.Lookup([]string{"git", "status"})
	if !ok {
		t.Fatal("git status not found")
	}
	if e.Fn("x") != "KEY" {
		t.Error("regex shadowed an exact key; exact keys must win")
	}
}

// The default registry wires the embedded TOML filters onto the regex path:
// cargo (a TOML demonstrator) matches, and its exit-code allowlist is {0}.
func TestDefaultLoadsTOMLFilters(t *testing.T) {
	r := Default()
	e, ok := r.Lookup([]string{"cargo", "build"})
	if !ok {
		t.Fatal("embedded cargo TOML filter not registered")
	}
	if e.Fn == nil {
		t.Error("cargo filter has nil Fn")
	}
	if !e.Filters(0) || e.Filters(101) {
		t.Error("cargo should filter exit 0 only (compile errors exit 101 stay raw)")
	}
}
