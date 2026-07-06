package filter

import "testing"

func TestLookup(t *testing.T) {
	r := Default()
	cases := []struct {
		name string
		argv []string
		want bool
	}{
		{"git status matches", []string{"git", "status"}, true},
		{"git commit with args matches", []string{"git", "commit", "-m", "msg"}, true},
		{"unknown git subcommand misses", []string{"git", "frobnicate"}, false},
		{"bare git misses", []string{"git"}, false},
		{"unknown command misses", []string{"kubectl", "get", "pods"}, false},
		{"flag before subcommand misses (gap-logged)", []string{"git", "-C", "dir", "status"}, false},
		{"empty argv misses", nil, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f, ok := r.Lookup(tc.argv)
			if ok != tc.want {
				t.Errorf("Lookup(%v) ok = %v, want %v", tc.argv, ok, tc.want)
			}
			if ok && f == nil {
				t.Error("matched but filter is nil")
			}
		})
	}
}

func TestBareCommandKeyMatches(t *testing.T) {
	r := New()
	called := false
	r.Register("ls", func(raw string) string { called = true; return raw })
	f, ok := r.Lookup([]string{"ls", "-la"})
	if !ok {
		t.Fatal("bare-command key did not match")
	}
	f("x")
	if !called {
		t.Error("wrong filter returned")
	}
}
