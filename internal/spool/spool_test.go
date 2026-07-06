package spool

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func newTestStore(t *testing.T) *Store {
	t.Helper()
	s, err := NewAt(t.TempDir())
	if err != nil {
		t.Fatalf("NewAt: %v", err)
	}
	return s
}

func TestID(t *testing.T) {
	argv := []string{"git", "status"}
	id := ID(argv)
	if len(id) != 4 {
		t.Fatalf("id length = %d, want 4", len(id))
	}
	if !idRe.MatchString(id) {
		t.Fatalf("id %q not 4 lowercase hex chars", id)
	}
	if ID(argv) != id {
		t.Error("ID not deterministic")
	}
	if ID([]string{"git", "log"}) == id {
		t.Error("distinct commands produced the same ID (fixed inputs should differ)")
	}
}

func TestWriteReadRoundtrip(t *testing.T) {
	s := newTestStore(t)
	now := time.Date(2026, 7, 6, 12, 0, 0, 0, time.UTC)
	argv := []string{"git", "log"}
	id, err := s.Write(argv, "line one\nline two\n", now)
	if err != nil {
		t.Fatalf("Write: %v", err)
	}
	got, err := s.Read(id)
	if err != nil {
		t.Fatalf("Read: %v", err)
	}
	for _, want := range []string{
		"# cmd: git log",
		"# time: 2026-07-06T12:00:00Z",
		"line one\nline two\n",
	} {
		if !strings.Contains(got, want) {
			t.Errorf("spool file missing %q:\n%s", want, got)
		}
	}
}

func TestWriteOverwritesSameCommand(t *testing.T) {
	s := newTestStore(t)
	argv := []string{"git", "status"}
	if _, err := s.Write(argv, "old output", time.Now()); err != nil {
		t.Fatal(err)
	}
	id, err := s.Write(argv, "new output", time.Now())
	if err != nil {
		t.Fatal(err)
	}
	got, err := s.Read(id)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(got, "old output") || !strings.Contains(got, "new output") {
		t.Errorf("rerun did not overwrite: %s", got)
	}
	entries, _ := os.ReadDir(s.spoolDir())
	if len(entries) != 1 {
		t.Errorf("spool holds %d files, want 1 (latest output per distinct command)", len(entries))
	}
}

func TestReadRejectsBadID(t *testing.T) {
	s := newTestStore(t)
	for _, id := range []string{"", "..", "zzzz", "ABCD", "../x", "12345"} {
		if _, err := s.Read(id); err == nil {
			t.Errorf("Read(%q) succeeded, want error", id)
		}
	}
}

func TestSweep(t *testing.T) {
	s := newTestStore(t)
	now := time.Now()
	oldID, err := s.Write([]string{"git", "log"}, "old", now)
	if err != nil {
		t.Fatal(err)
	}
	stale := now.Add(-2 * time.Hour)
	if err := os.Chtimes(filepath.Join(s.spoolDir(), oldID+spoolExt), stale, stale); err != nil {
		t.Fatal(err)
	}
	freshID, err := s.Write([]string{"git", "status"}, "fresh", now)
	if err != nil {
		t.Fatal(err)
	}
	s.Sweep(DefaultTTL, now)
	if _, err := s.Read(oldID); err == nil {
		t.Error("expired entry survived the sweep")
	}
	if _, err := s.Read(freshID); err != nil {
		t.Errorf("fresh entry removed by sweep: %v", err)
	}
}

func TestRedact(t *testing.T) {
	cases := []struct {
		name string
		in   string
		want string
	}{
		{
			"authorization header",
			"Authorization: Bearer sekrit-token-value\nnext",
			"Authorization: [REDACTED]\nnext",
		},
		{
			"aws secret",
			"AWS_SECRET_ACCESS_KEY=abc123/xyz\ndone",
			"AWS_SECRET_ACCESS_KEY=[REDACTED]\ndone",
		},
		{
			"pem block",
			"before\n-----BEGIN RSA PRIVATE KEY-----\nMIIkeymaterial\n-----END RSA PRIVATE KEY-----\nafter",
			"before\n[REDACTED PEM BLOCK]\nafter",
		},
		{
			"clean text untouched",
			"nothing secret here\n",
			"nothing secret here\n",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := Redact(tc.in); got != tc.want {
				t.Errorf("Redact(%q) = %q, want %q", tc.in, got, tc.want)
			}
		})
	}
}

func TestWriteRedactsContent(t *testing.T) {
	s := newTestStore(t)
	id, err := s.Write([]string{"curl", "-v", "example.com"},
		"Authorization: Bearer supersecret\nbody\n", time.Now())
	if err != nil {
		t.Fatal(err)
	}
	got, err := s.Read(id)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(got, "supersecret") {
		t.Error("secret survived the redaction pass")
	}
}

func TestInvocationsAndGaps(t *testing.T) {
	s := newTestStore(t)
	now := time.Now().UTC()
	entries := []Invocation{
		{Time: now, Cmd: "cargo build", RawBytes: 5000, OutBytes: 5000, Filtered: false},
		{Time: now, Cmd: "cargo test", RawBytes: 7000, OutBytes: 7000, Filtered: false},
		{Time: now, Cmd: "ls -la", RawBytes: 300, OutBytes: 300, Filtered: false},
		{Time: now, Cmd: "git status", RawBytes: 400, OutBytes: 60, Filtered: true},
	}
	for _, e := range entries {
		if err := s.LogInvocation(e); err != nil {
			t.Fatalf("LogInvocation: %v", err)
		}
	}
	invs, err := s.Invocations()
	if err != nil {
		t.Fatal(err)
	}
	if len(invs) != 4 {
		t.Fatalf("got %d invocations, want 4", len(invs))
	}
	gaps, err := s.Gaps()
	if err != nil {
		t.Fatal(err)
	}
	want := []GapSummary{
		{Family: "cargo", Calls: 2, RawBytes: 12000},
		{Family: "ls", Calls: 1, RawBytes: 300},
	}
	if len(gaps) != len(want) {
		t.Fatalf("got %d gap families, want %d: %+v", len(gaps), len(want), gaps)
	}
	for i := range want {
		if gaps[i] != want[i] {
			t.Errorf("gaps[%d] = %+v, want %+v", i, gaps[i], want[i])
		}
	}
}

// Invariant 3: metadata never contains output content. The Invocation schema
// only carries the command line and counts; this guards the log file itself.
func TestMetadataHoldsNoOutput(t *testing.T) {
	s := newTestStore(t)
	output := "unique-output-marker-9f8e7d\n"
	if _, err := s.Write([]string{"git", "log"}, output, time.Now()); err != nil {
		t.Fatal(err)
	}
	if err := s.LogInvocation(Invocation{
		Time: time.Now(), Cmd: "git log",
		RawBytes: int64(len(output)), OutBytes: 20, Filtered: true,
	}); err != nil {
		t.Fatal(err)
	}
	meta, err := os.ReadFile(s.metaPath())
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(meta), "unique-output-marker") {
		t.Error("invocation metadata contains output content")
	}
}

func TestLogInvocationRedactsCmd(t *testing.T) {
	s := newTestStore(t)
	if err := s.LogInvocation(Invocation{
		Time: time.Now(), Cmd: `curl -H "Authorization: Bearer tok123"`,
	}); err != nil {
		t.Fatal(err)
	}
	meta, err := os.ReadFile(s.metaPath())
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(meta), "tok123") {
		t.Error("credential in argv survived into metadata log")
	}
}
