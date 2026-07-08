package main

import (
	"strings"
	"testing"

	"github.com/meridun/vtk/internal/spool"
)

func TestParseExistingFilterFamilies(t *testing.T) {
	titles := []string{
		"Filter: cargo (auto-filed from vtk gaps)",
		"Filter: docker",
		"Filter:   kubectl (manual)", // extra spaces tolerated
		"Filter: (auto-filed from vtk gaps)",
		"Add a filter for cargo", // not a Filter issue
		"filter: lowercase",      // prefix is case-sensitive
		"",
	}
	got := parseExistingFilterFamilies(titles)
	want := map[string]bool{"cargo": true, "docker": true, "kubectl": true}
	if len(got) != len(want) {
		t.Fatalf("got %v, want %v", got, want)
	}
	for fam := range want {
		if !got[fam] {
			t.Errorf("missing family %q in %v", fam, got)
		}
	}
}

func TestPlanFilterIssues(t *testing.T) {
	candidates := []spool.GapSummary{
		{Family: "cargo", Calls: 3, RawBytes: 30000},
		{Family: "docker", Calls: 4, RawBytes: 60000},
		{Family: "kubectl", Calls: 2, RawBytes: 20000},
	}
	existing := map[string]bool{"docker": true}
	toFile, skipped := planFilterIssues(candidates, existing)
	if len(toFile) != 2 || toFile[0].Family != "cargo" || toFile[1].Family != "kubectl" {
		t.Errorf("toFile = %+v, want cargo+kubectl", toFile)
	}
	if len(skipped) != 1 || skipped[0] != "docker" {
		t.Errorf("skipped = %v, want [docker]", skipped)
	}
}

func TestFilterIssueTitleRoundTrip(t *testing.T) {
	// A filed title must parse back to its family, or dedupe silently breaks.
	for _, fam := range []string{"cargo", "docker", "kubectl"} {
		title := filterIssueTitle(fam)
		if !strings.HasPrefix(title, filterIssueTitlePrefix) {
			t.Errorf("title %q missing prefix", title)
		}
		got := parseExistingFilterFamilies([]string{title})
		if !got[fam] {
			t.Errorf("filterIssueTitle(%q) = %q did not round-trip; parsed %v", fam, title, got)
		}
	}
}

func TestFilterIssueBody(t *testing.T) {
	g := spool.GapSummary{Family: "cargo", Calls: 7, RawBytes: 123456}
	body := filterIssueBody(g, 51200, 3)
	for _, want := range []string{"cargo", "7", "123456", "min-bytes=51200", "min-calls=3", "Exit-code parity"} {
		if !strings.Contains(body, want) {
			t.Errorf("body missing %q:\n%s", want, body)
		}
	}
}

// TestGapsArgErrors pins the usage-error exits for malformed `vtk gaps` args.
// All cases fail during parse or the pre-file guard — no gh shell-out, so the
// test is hermetic.
func TestGapsArgErrors(t *testing.T) {
	tests := []struct {
		name string
		args []string
	}{
		{"unknown flag", []string{"gaps", "--bogus"}},
		{"min-bytes missing value", []string{"gaps", "--file-issues", "--min-bytes"}},
		{"min-bytes non-numeric", []string{"gaps", "--file-issues", "--min-bytes", "abc"}},
		{"min-calls non-numeric", []string{"gaps", "--file-issues", "--min-calls", "x"}},
		{"yes without file-issues", []string{"gaps", "--yes"}},
		{"min-bytes without file-issues", []string{"gaps", "--min-bytes", "1000"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := run(tt.args); got != 2 {
				t.Errorf("run(%v) = %d, want 2", tt.args, got)
			}
		})
	}
}
