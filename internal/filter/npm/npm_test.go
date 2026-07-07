package npm

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

// Fixtures are real `npm run <script>` output (absolute paths scrubbed to
// <repo>; see the raw provenance in the issue/PR). StripBanner must remove the
// two-line banner and parse the inner command from the expanded command line.
func TestStripBanner(t *testing.T) {
	cases := []struct {
		name      string
		wantInner []string
		wantOK    bool
		// A fragment that must appear in the stripped body, and the banner
		// fragment that must NOT.
		wantBody    string
		wantNoBody  string
		minStripped int // body must be at least this many bytes shorter than raw
	}{
		{
			name:        "no_inner_match",
			wantInner:   []string{"node", "scripts/sync-claude-config.mjs", "--check"},
			wantOK:      true,
			wantBody:    "in sync",
			wantNoBody:  "isekai-rpg@0.0.1",
			minStripped: 40,
		},
		{
			name:        "lint_eslint",
			wantInner:   []string{"eslint", ".", "--cache"},
			wantOK:      true,
			wantBody:    "7 problems",
			wantNoBody:  "> eslint . --cache",
			minStripped: 40,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			rawB, err := os.ReadFile(filepath.Join("testdata", tc.name+".raw.txt"))
			if err != nil {
				t.Fatalf("read fixture: %v", err)
			}
			raw := string(rawB)
			body, inner, ok := StripBanner(raw)
			if ok != tc.wantOK {
				t.Fatalf("ok = %v, want %v", ok, tc.wantOK)
			}
			if !reflect.DeepEqual(inner, tc.wantInner) {
				t.Errorf("inner = %v, want %v", inner, tc.wantInner)
			}
			if tc.wantBody != "" && !strings.Contains(body, tc.wantBody) {
				t.Errorf("body missing %q:\n%s", tc.wantBody, body)
			}
			if tc.wantNoBody != "" && strings.Contains(body, tc.wantNoBody) {
				t.Errorf("body still contains banner fragment %q:\n%s", tc.wantNoBody, body)
			}
			if stripped := len(raw) - len(body); stripped < tc.minStripped {
				t.Errorf("stripped only %d bytes, want >= %d", stripped, tc.minStripped)
			}
		})
	}
}

// The body starts at the inner tool's first real output line — the separator
// blank line npm emits after the banner is dropped, not leaked.
func TestBodyDropsSeparatorBlank(t *testing.T) {
	raw := "> pkg@1.0.0 build\n> tsc -p .\n\nsrc/x.ts: error TS1005\n"
	body, inner, ok := StripBanner(raw)
	if !ok {
		t.Fatal("banner not recognized")
	}
	if want := []string{"tsc", "-p", "."}; !reflect.DeepEqual(inner, want) {
		t.Errorf("inner = %v, want %v", inner, want)
	}
	if want := "src/x.ts: error TS1005\n"; body != want {
		t.Errorf("body = %q, want %q", body, want)
	}
}

// A leading blank line (npm/stderr interleaving) before the banner is tolerated.
func TestLeadingBlankTolerated(t *testing.T) {
	raw := "\n> pkg@1.0.0 lint\n> eslint .\n\nclean\n"
	body, inner, ok := StripBanner(raw)
	if !ok {
		t.Fatal("banner behind a leading blank not recognized")
	}
	if want := []string{"eslint", "."}; !reflect.DeepEqual(inner, want) {
		t.Errorf("inner = %v, want %v", inner, want)
	}
	if body != "clean\n" {
		t.Errorf("body = %q, want %q", body, "clean\n")
	}
}

// Output with no npm banner (a bare tool, or output that never had a banner)
// is not claimed: ok is false so the runner treats it as an ordinary command.
func TestNonBannerNotClaimed(t *testing.T) {
	for _, raw := range []string{
		"",
		"just some output\nno banner here\n",
		"> only one banner line\nbody\n", // single > line, not a run banner
		"> pkg@1.0.0 script\n  indented not-a-banner", // second line lacks "> "
	} {
		if _, _, ok := StripBanner(raw); ok {
			t.Errorf("claimed non-banner input as npm banner:\n%q", raw)
		}
	}
}

// A banner whose expanded command line is empty is malformed and not claimed.
func TestEmptyCommandLineNotClaimed(t *testing.T) {
	if _, _, ok := StripBanner("> pkg@1.0.0 script\n> \nbody\n"); ok {
		t.Error("claimed a banner with an empty command line")
	}
}
