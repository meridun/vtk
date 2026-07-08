// Package tomlfilter turns declarative TOML filter definitions into pure
// filter functions (raw captured output in, compact output out — the same
// contract as the hand-written Go filter families). Most tools only need to
// drop progress/noise lines and cap length; expressing that as ~20 lines of
// TOML with inline fixtures — instead of a bespoke Go package per tool — is the
// enabler for long-tail coverage (issue #40).
//
// A compiled filter is a plain func(raw string) string; this package never
// imports the filter registry (that would be an import cycle) and never spawns
// processes or touches the filesystem beyond the build-time embed
// (docs/Architecture.md invariant 4). Definitions live in defs/*.toml and are
// embedded at build; Load parses, validates, and compiles them.
package tomlfilter

import (
	"embed"
	"fmt"
	"io/fs"
	"path"
	"regexp"
	"sort"
	"strings"

	"github.com/BurntSushi/toml"
)

//go:embed defs/*.toml
var defsFS embed.FS

// Replacement is a regex substitution applied across the whole output.
type Replacement struct {
	Pattern     string `toml:"pattern"`
	Replacement string `toml:"replacement"`
}

// ShortCircuit collapses the entire output to Message when Pattern matches
// anywhere in it (rtk's match_output).
type ShortCircuit struct {
	Pattern string `toml:"pattern"`
	Message string `toml:"message"`
}

// Fixture is one inline test case: raw Input in, expected compact Expected out.
type Fixture struct {
	Input    string `toml:"input"`
	Expected string `toml:"expected"`
}

// Spec is the parsed TOML filter definition (rtk's schema, verified against the
// issue #40 field table). Zero-value fields are inert, so a minimal filter is
// just name + match_command + one line operation.
type Spec struct {
	Name         string `toml:"name"`
	MatchCommand string `toml:"match_command"`
	ExitCodes    []int  `toml:"exit_codes"`

	StripANSI    bool `toml:"strip_ansi"`
	FilterStderr bool `toml:"filter_stderr"`

	StripLinesMatching []string `toml:"strip_lines_matching"`
	KeepLinesMatching  []string `toml:"keep_lines_matching"`

	Replace     []Replacement  `toml:"replace"`
	MatchOutput []ShortCircuit `toml:"match_output"`

	TruncateLinesAt int    `toml:"truncate_lines_at"`
	MaxLines        int    `toml:"max_lines"`
	OnEmpty         string `toml:"on_empty"`

	Tests map[string][]Fixture `toml:"tests"`
}

// ansiRe matches CSI/SGR escape sequences (colors, cursor moves) so strip_ansi
// can drop terminal styling before line matching.
var ansiRe = regexp.MustCompile(`\x1b\[[0-9;?]*[ -/]*[@-~]`)

// Compiled is a validated, ready-to-run TOML filter: its match regex, the
// display filter, and the child exit codes it may run on.
type Compiled struct {
	Name      string
	Match     *regexp.Regexp
	Fn        func(raw string) string
	ExitCodes []int
}

// compiled holds the pre-built regexes for one Spec's pipeline.
type compiled struct {
	stripLines  []*regexp.Regexp
	keepLines   []*regexp.Regexp
	replace     []replaceRe
	matchOutput []matchOutRe
}

type replaceRe struct {
	re   *regexp.Regexp
	with string
}

type matchOutRe struct {
	re  *regexp.Regexp
	msg string
}

// Parse decodes a single TOML filter definition. It is strict: unknown keys are
// rejected so a typo in a filter file fails loudly at build/test time rather
// than silently disabling an operation.
func Parse(data []byte) (Spec, error) {
	var s Spec
	md, err := toml.Decode(string(data), &s)
	if err != nil {
		return Spec{}, err
	}
	if undecoded := md.Undecoded(); len(undecoded) > 0 {
		keys := make([]string, len(undecoded))
		for i, k := range undecoded {
			keys[i] = k.String()
		}
		return Spec{}, fmt.Errorf("unknown key(s): %s", strings.Join(keys, ", "))
	}
	return s, nil
}

// Validate reports whether a Spec is well-formed and buildable. It rejects
// definitions that cannot be honored faithfully rather than degrading them
// silently — a silent semantic change would violate the wrapper's transparency
// contract.
func (s Spec) Validate() error {
	if strings.TrimSpace(s.Name) == "" {
		return fmt.Errorf("name is required")
	}
	if strings.TrimSpace(s.MatchCommand) == "" {
		return fmt.Errorf("%s: match_command is required", s.Name)
	}
	if s.FilterStderr {
		// By the time a filter Func runs, the runner has already merged the
		// child's stdout and stderr into one string (cmd/vtk/main.go), so the
		// stream a Func sees is undivided and stderr cannot be dropped here.
		// Honoring filter_stderr needs runner-side stream separation; until
		// that lands, reject it rather than pretend to apply it.
		return fmt.Errorf("%s: filter_stderr is not supported yet (needs runner stream separation; see issue #40)", s.Name)
	}
	if s.TruncateLinesAt < 0 {
		return fmt.Errorf("%s: truncate_lines_at must be >= 0", s.Name)
	}
	if s.MaxLines < 0 {
		return fmt.Errorf("%s: max_lines must be >= 0", s.Name)
	}
	if _, err := s.compile(); err != nil {
		return err
	}
	return nil
}

// compile pre-builds every regex in the pipeline, surfacing a bad pattern as a
// build/test-time error instead of a runtime panic.
func (s Spec) compile() (compiled, error) {
	var c compiled
	build := func(pats []string, field string) ([]*regexp.Regexp, error) {
		out := make([]*regexp.Regexp, 0, len(pats))
		for _, p := range pats {
			re, err := regexp.Compile(p)
			if err != nil {
				return nil, fmt.Errorf("%s: %s %q: %w", s.Name, field, p, err)
			}
			out = append(out, re)
		}
		return out, nil
	}
	var err error
	if c.stripLines, err = build(s.StripLinesMatching, "strip_lines_matching"); err != nil {
		return compiled{}, err
	}
	if c.keepLines, err = build(s.KeepLinesMatching, "keep_lines_matching"); err != nil {
		return compiled{}, err
	}
	for _, r := range s.Replace {
		re, err := regexp.Compile(r.Pattern)
		if err != nil {
			return compiled{}, fmt.Errorf("%s: replace %q: %w", s.Name, r.Pattern, err)
		}
		c.replace = append(c.replace, replaceRe{re: re, with: r.Replacement})
	}
	for _, m := range s.MatchOutput {
		re, err := regexp.Compile(m.Pattern)
		if err != nil {
			return compiled{}, fmt.Errorf("%s: match_output %q: %w", s.Name, m.Pattern, err)
		}
		c.matchOutput = append(c.matchOutput, matchOutRe{re: re, msg: m.Message})
	}
	return c, nil
}

// exitCodes returns the child exit codes this filter may run on, defaulting to
// {0} (clean run only), matching Register's default in the filter registry.
func (s Spec) exitCodes() []int {
	if len(s.ExitCodes) == 0 {
		return []int{0}
	}
	return s.ExitCodes
}

// Compile validates the Spec and returns a Compiled filter. The returned Fn is
// a pure function; matching noise is elided per the pipeline and, if nothing is
// elided, the input is returned unchanged so the caller passes it through raw.
func (s Spec) Compile() (Compiled, error) {
	if err := s.Validate(); err != nil {
		return Compiled{}, err
	}
	match, err := regexp.Compile(s.MatchCommand)
	if err != nil {
		return Compiled{}, fmt.Errorf("%s: match_command %q: %w", s.Name, s.MatchCommand, err)
	}
	c, err := s.compile()
	if err != nil {
		return Compiled{}, err
	}
	return Compiled{
		Name:      s.Name,
		Match:     match,
		Fn:        s.makeFn(c),
		ExitCodes: s.exitCodes(),
	}, nil
}

// makeFn builds the display pipeline closure. Order: strip_ansi ->
// match_output short-circuit -> keep_lines -> strip_lines -> replace ->
// truncate_lines_at -> max_lines -> on_empty.
func (s Spec) makeFn(c compiled) func(string) string {
	return func(raw string) string {
		out := raw
		if s.StripANSI {
			out = ansiRe.ReplaceAllString(out, "")
		}

		// Short-circuit: if any match_output pattern hits, the whole output
		// collapses to its message.
		for _, m := range c.matchOutput {
			if m.re.MatchString(out) {
				return m.msg
			}
		}

		if len(c.keepLines) > 0 || len(c.stripLines) > 0 {
			lines := strings.Split(out, "\n")
			kept := make([]string, 0, len(lines))
			for _, ln := range lines {
				if len(c.keepLines) > 0 && !anyMatch(c.keepLines, ln) {
					continue
				}
				if anyMatch(c.stripLines, ln) {
					continue
				}
				kept = append(kept, ln)
			}
			out = strings.Join(kept, "\n")
		}

		for _, r := range c.replace {
			out = r.re.ReplaceAllString(out, r.with)
		}

		if s.TruncateLinesAt > 0 {
			lines := strings.Split(out, "\n")
			for i, ln := range lines {
				if len(ln) > s.TruncateLinesAt {
					lines[i] = ln[:s.TruncateLinesAt] + "..."
				}
			}
			out = strings.Join(lines, "\n")
		}

		if s.MaxLines > 0 {
			lines := strings.Split(out, "\n")
			if len(lines) > s.MaxLines {
				more := len(lines) - s.MaxLines
				lines = append(lines[:s.MaxLines], fmt.Sprintf("... (%d more lines)", more))
				out = strings.Join(lines, "\n")
			}
		}

		if s.OnEmpty != "" && strings.TrimSpace(out) == "" {
			return s.OnEmpty
		}
		return out
	}
}

func anyMatch(res []*regexp.Regexp, s string) bool {
	for _, re := range res {
		if re.MatchString(s) {
			return true
		}
	}
	return false
}

// Load parses, validates, and compiles every embedded filter definition,
// returning them sorted by name for deterministic registration order. Any bad
// definition is a hard error — a broken filter file must fail the build, not
// ship disabled.
func Load() ([]Compiled, error) {
	return load(defsFS)
}

// load is Load's testable core, parameterized on the filesystem.
func load(fsys fs.FS) ([]Compiled, error) {
	entries, err := fs.Glob(fsys, "defs/*.toml")
	if err != nil {
		return nil, err
	}
	sort.Strings(entries)
	out := make([]Compiled, 0, len(entries))
	for _, name := range entries {
		data, err := fs.ReadFile(fsys, name)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", path.Base(name), err)
		}
		spec, err := Parse(data)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", path.Base(name), err)
		}
		comp, err := spec.Compile()
		if err != nil {
			return nil, fmt.Errorf("%s: %w", path.Base(name), err)
		}
		out = append(out, comp)
	}
	return out, nil
}

// Specs parses and validates every embedded definition, returning the raw Specs
// (with their inline fixtures) so a test harness can exercise each filter's
// declared cases. Compilation errors surface here too.
func Specs() (map[string]Spec, error) {
	entries, err := fs.Glob(defsFS, "defs/*.toml")
	if err != nil {
		return nil, err
	}
	out := make(map[string]Spec, len(entries))
	for _, name := range entries {
		data, err := fs.ReadFile(defsFS, name)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", path.Base(name), err)
		}
		spec, err := Parse(data)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", path.Base(name), err)
		}
		if err := spec.Validate(); err != nil {
			return nil, fmt.Errorf("%s: %w", path.Base(name), err)
		}
		out[path.Base(name)] = spec
	}
	return out, nil
}
