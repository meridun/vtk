package tomlfilter

import (
	"strconv"
	"testing"
)

// mustCompile compiles a Spec or fails the test.
func mustCompile(t *testing.T, s Spec) Compiled {
	t.Helper()
	c, err := s.Compile()
	if err != nil {
		t.Fatalf("Compile(%q): %v", s.Name, err)
	}
	return c
}

// base returns a minimal valid Spec that the per-op tests extend.
func base(name string) Spec {
	return Spec{Name: name, MatchCommand: "^x"}
}

func TestPipelineOps(t *testing.T) {
	cases := []struct {
		name string
		spec Spec
		in   string
		want string
	}{
		{
			name: "strip_lines drops matching lines",
			spec: func() Spec { s := base("strip"); s.StripLinesMatching = []string{`^progress`}; return s }(),
			in:   "progress 1\nkeep me\nprogress 2\n",
			want: "keep me\n",
		},
		{
			name: "keep_lines keeps only matching, applied before strip",
			spec: func() Spec {
				s := base("keep")
				s.KeepLinesMatching = []string{`error|warning`}
				return s
			}(),
			// keep-only drops every non-matching line, including the trailing
			// empty line left by the input's terminal newline.
			in:   "info: starting\nerror: boom\nnote\nwarning: heads up\n",
			want: "error: boom\nwarning: heads up",
		},
		{
			name: "keep then strip compose",
			spec: func() Spec {
				s := base("k+s")
				s.KeepLinesMatching = []string{`^L`}
				s.StripLinesMatching = []string{`skip`}
				return s
			}(),
			in:   "Lkeep\nLskip\nother\n",
			want: "Lkeep",
		},
		{
			name: "strip_ansi removes escape sequences",
			spec: func() Spec { s := base("ansi"); s.StripANSI = true; return s }(),
			in:   "\x1b[31merror\x1b[0m here",
			want: "error here",
		},
		{
			name: "replace substitutes across output",
			spec: func() Spec {
				s := base("rep")
				s.Replace = []Replacement{{Pattern: `\d+\.\d+s`, Replacement: "Ns"}}
				return s
			}(),
			in:   "done in 4.21s and 0.02s",
			want: "done in Ns and Ns",
		},
		{
			name: "match_output short-circuits to message",
			spec: func() Spec {
				s := base("mo")
				s.MatchOutput = []ShortCircuit{{Pattern: `All \d+ tests passed`, Message: "tests: all green"}}
				return s
			}(),
			in:   "line 1\nAll 42 tests passed\nline 3",
			want: "tests: all green",
		},
		{
			name: "truncate_lines_at caps line length",
			spec: func() Spec { s := base("trunc"); s.TruncateLinesAt = 5; return s }(),
			in:   "short\nabcdefghij",
			want: "short\nabcde...",
		},
		{
			name: "max_lines caps line count",
			spec: func() Spec { s := base("max"); s.MaxLines = 2; return s }(),
			in:   "a\nb\nc\nd",
			want: "a\nb\n... (2 more lines)",
		},
		{
			name: "on_empty message when filtered blank",
			spec: func() Spec {
				s := base("empty")
				s.StripLinesMatching = []string{`.*`}
				s.OnEmpty = "nothing to report"
				return s
			}(),
			in:   "a\nb\n",
			want: "nothing to report",
		},
		{
			name: "no-op filter returns input unchanged",
			spec: base("noop"),
			in:   "unchanged content",
			want: "unchanged content",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := mustCompile(t, tc.spec)
			got := c.Fn(tc.in)
			if got != tc.want {
				t.Errorf("Fn(%q) = %q, want %q", tc.in, got, tc.want)
			}
		})
	}
}

func TestValidate(t *testing.T) {
	cases := []struct {
		name    string
		spec    Spec
		wantErr bool
	}{
		{"valid minimal", base("ok"), false},
		{"missing name", Spec{MatchCommand: "^x"}, true},
		{"missing match_command", Spec{Name: "x"}, true},
		{"filter_stderr rejected", func() Spec { s := base("fs"); s.FilterStderr = true; return s }(), true},
		{"negative truncate", func() Spec { s := base("t"); s.TruncateLinesAt = -1; return s }(), true},
		{"negative max_lines", func() Spec { s := base("m"); s.MaxLines = -1; return s }(), true},
		{"bad strip regex", func() Spec { s := base("re"); s.StripLinesMatching = []string{"("}; return s }(), true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := tc.spec.Validate()
			if (err != nil) != tc.wantErr {
				t.Errorf("Validate() err = %v, wantErr = %v", err, tc.wantErr)
			}
		})
	}
}

func TestExitCodesDefault(t *testing.T) {
	c := mustCompile(t, base("d"))
	if len(c.ExitCodes) != 1 || c.ExitCodes[0] != 0 {
		t.Errorf("default ExitCodes = %v, want [0]", c.ExitCodes)
	}
	s := base("r")
	s.ExitCodes = []int{0, 1}
	c = mustCompile(t, s)
	if len(c.ExitCodes) != 2 {
		t.Errorf("ExitCodes = %v, want [0 1]", c.ExitCodes)
	}
}

func TestParseRejectsUnknownKey(t *testing.T) {
	_, err := Parse([]byte("name = \"x\"\nmatch_command = \"^x\"\nbogus = true\n"))
	if err == nil {
		t.Fatal("Parse should reject unknown key")
	}
}

func TestParseBadMatchCommand(t *testing.T) {
	s, err := Parse([]byte("name = \"x\"\nmatch_command = \"(\"\n"))
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}
	if _, err := s.Compile(); err == nil {
		t.Fatal("Compile should reject a bad match_command regex")
	}
}

// TestLoadEmbedded confirms the shipped defs all parse, validate, and compile.
func TestLoadEmbedded(t *testing.T) {
	compiled, err := Load()
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if len(compiled) == 0 {
		t.Fatal("no embedded filters loaded")
	}
	for _, c := range compiled {
		if c.Name == "" || c.Match == nil || c.Fn == nil {
			t.Errorf("incomplete compiled filter: %+v", c)
		}
	}
}

// TestEmbeddedFixtures runs every embedded filter's inline [[tests.*]] cases —
// the rtk "validate fixtures at build" analog. A filter file ships broken if
// any of its declared fixtures do not reproduce.
func TestEmbeddedFixtures(t *testing.T) {
	specs, err := Specs()
	if err != nil {
		t.Fatalf("Specs: %v", err)
	}
	total := 0
	for file, spec := range specs {
		c := mustCompile(t, spec)
		for group, fixtures := range spec.Tests {
			for i, fx := range fixtures {
				total++
				t.Run(file+"/"+group+"/"+strconv.Itoa(i), func(t *testing.T) {
					got := c.Fn(fx.Input)
					if got != fx.Expected {
						t.Errorf("%s %s[%d]: Fn(%q) = %q, want %q", file, group, i, fx.Input, got, fx.Expected)
					}
				})
			}
		}
	}
	if total == 0 {
		t.Fatal("no inline fixtures found in embedded filters")
	}
}
