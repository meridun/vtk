// Package spool is the raw-output store plus per-invocation metadata log —
// one store, three queries (`vtk show`, `vtk gain`, `vtk gaps`).
// See docs/Architecture.md #output-spool and decision #2.
package spool

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

// DefaultTTL is how long spool entries survive before the opportunistic sweep
// removes them.
const DefaultTTL = time.Hour

const spoolExt = ".txt"

var idRe = regexp.MustCompile(`^[0-9a-f]{4}$`)

// Store is the on-disk spool: <dir>/spool/<id>.txt raw-output files and
// <dir>/invocations.jsonl metadata (never output content — invariant 3).
type Store struct {
	dir string
}

// Open resolves the user-local store directory (never inside a repo) and
// ensures it exists with restrictive permissions.
func Open() (*Store, error) {
	cache, err := os.UserCacheDir()
	if err != nil {
		return nil, err
	}
	return NewAt(filepath.Join(cache, "vtk"))
}

// NewAt opens a store rooted at dir, creating it if needed. Used directly by
// tests; production callers use Open.
func NewAt(dir string) (*Store, error) {
	if err := os.MkdirAll(filepath.Join(dir, "spool"), 0o700); err != nil {
		return nil, err
	}
	return &Store{dir: dir}, nil
}

func (s *Store) spoolDir() string { return filepath.Join(s.dir, "spool") }

// ID returns the spool ID for a command line: first 4 hex chars of a
// SHA-256 checksum. Rerunning the same command overwrites its own entry.
func ID(argv []string) string {
	sum := sha256.Sum256([]byte(strings.Join(argv, " ")))
	return hex.EncodeToString(sum[:])[:4]
}

// Write spools raw output for argv: redaction pass, provenance header,
// temp-file + atomic-rename write. Returns the retrieval ID.
func (s *Store) Write(argv []string, raw string, now time.Time) (string, error) {
	id := ID(argv)
	content := "# vtk spool\n# cmd: " + Redact(strings.Join(argv, " ")) +
		"\n# time: " + now.UTC().Format(time.RFC3339) + "\n\n" + Redact(raw)
	tmp, err := os.CreateTemp(s.spoolDir(), id+".tmp-*")
	if err != nil {
		return "", err
	}
	tmpName := tmp.Name()
	if _, err := tmp.WriteString(content); err != nil {
		tmp.Close()
		os.Remove(tmpName)
		return "", err
	}
	if err := tmp.Close(); err != nil {
		os.Remove(tmpName)
		return "", err
	}
	if err := os.Rename(tmpName, filepath.Join(s.spoolDir(), id+spoolExt)); err != nil {
		os.Remove(tmpName)
		return "", err
	}
	return id, nil
}

// Read returns the spooled content (provenance header first) for an ID.
func (s *Store) Read(id string) (string, error) {
	if !idRe.MatchString(id) {
		return "", fmt.Errorf("invalid spool id %q", id)
	}
	b, err := os.ReadFile(filepath.Join(s.spoolDir(), id+spoolExt))
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// Sweep opportunistically deletes spool entries (and stale temp files) older
// than ttl. Errors are ignored: the sweep is best-effort by design.
func (s *Store) Sweep(ttl time.Duration, now time.Time) {
	entries, err := os.ReadDir(s.spoolDir())
	if err != nil {
		return
	}
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		if now.Sub(info.ModTime()) > ttl {
			os.Remove(filepath.Join(s.spoolDir(), e.Name()))
		}
	}
}

var (
	authRe = regexp.MustCompile(`(?i)(authorization:[ \t]*)[^\r\n]+`)
	awsRe  = regexp.MustCompile(`(?i)(AWS_SECRET[A-Z0-9_]*[ \t]*[=:][ \t]*)[^\s]+`)
	pemRe  = regexp.MustCompile(`(?s)-----BEGIN [A-Z0-9 ]+-----.*?-----END [A-Z0-9 ]+-----`)
)

// Redact masks obvious credential patterns: Authorization headers,
// AWS_SECRET* assignments, and PEM blocks.
func Redact(s string) string {
	s = authRe.ReplaceAllString(s, "${1}[REDACTED]")
	s = awsRe.ReplaceAllString(s, "${1}[REDACTED]")
	s = pemRe.ReplaceAllString(s, "[REDACTED PEM BLOCK]")
	return s
}

// Reason values classify why an invocation was not filtered. Only
// ReasonNoFilter entries are true coverage gaps; the rest are excluded from
// `vtk gaps` (and let `vtk gain` skip entries whose raw_bytes are
// uncountable-by-design, e.g. tty-bypass).
const (
	ReasonNoFilter    = "no-filter"    // no registry match for argv
	ReasonTTYBypass   = "tty-bypass"   // covered command, interactive bypass
	ReasonNonzeroExit = "nonzero-exit" // failures always emit raw
	ReasonFilterPanic = "filter-panic" // filter code panicked; degraded to raw
	ReasonSpoolFail   = "spool-fail"   // could not spool; elision would lose output
)

// Invocation is one metadata entry. It carries the (redacted) command line
// and byte counts — never output content (invariant 3).
type Invocation struct {
	Time     time.Time `json:"time"`
	Cmd      string    `json:"cmd"`
	RawBytes int64     `json:"raw_bytes"`
	OutBytes int64     `json:"out_bytes"`
	Filtered bool      `json:"filtered"`
	TTY      bool      `json:"tty,omitempty"`
	Reason   string    `json:"reason,omitempty"` // why unfiltered; empty when filtered
}

func (s *Store) metaPath() string { return filepath.Join(s.dir, "invocations.jsonl") }

// LogInvocation appends one metadata entry. The command line is redacted
// before write.
func (s *Store) LogInvocation(inv Invocation) error {
	inv.Cmd = Redact(inv.Cmd)
	b, err := json.Marshal(inv)
	if err != nil {
		return err
	}
	f, err := os.OpenFile(s.metaPath(), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = f.Write(append(b, '\n'))
	return err
}

// Invocations returns all logged entries, skipping malformed lines.
func (s *Store) Invocations() ([]Invocation, error) {
	b, err := os.ReadFile(s.metaPath())
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var out []Invocation
	for _, line := range strings.Split(string(b), "\n") {
		if strings.TrimSpace(line) == "" {
			continue
		}
		var inv Invocation
		if json.Unmarshal([]byte(line), &inv) != nil {
			continue
		}
		out = append(out, inv)
	}
	return out, nil
}

// GapSummary aggregates passthrough invocations for one command family.
type GapSummary struct {
	Family   string
	Calls    int
	RawBytes int64
}

// Gaps aggregates true coverage gaps (no registry match) by command family
// (first token), sorted by total raw bytes descending — the top of the list
// is the next filter to write. Covered-but-unfiltered invocations
// (tty-bypass, nonzero-exit, filter-panic, spool-fail) are excluded; entries
// written before the reason field existed count as gaps unless they were
// tty bypasses (whose raw_bytes:0 polluted the report — #6).
func (s *Store) Gaps() ([]GapSummary, error) {
	return s.aggregate(func(inv Invocation) bool {
		if inv.Filtered {
			return false
		}
		if inv.Reason == "" { // legacy entry, pre-reason
			return !inv.TTY
		}
		return inv.Reason == ReasonNoFilter
	})
}

// Degraded aggregates filter-panic invocations by command family: filters
// that exist but are crashing on real output (invariant 2 — a broken filter
// must stay visible).
func (s *Store) Degraded() ([]GapSummary, error) {
	return s.aggregate(func(inv Invocation) bool {
		return inv.Reason == ReasonFilterPanic
	})
}

// aggregate folds matching invocations into per-family summaries, sorted by
// total raw bytes descending, then family name.
func (s *Store) aggregate(match func(Invocation) bool) ([]GapSummary, error) {
	invs, err := s.Invocations()
	if err != nil {
		return nil, err
	}
	agg := make(map[string]*GapSummary)
	for _, inv := range invs {
		if !match(inv) {
			continue
		}
		family, _, _ := strings.Cut(inv.Cmd, " ")
		if family == "" {
			continue
		}
		g, ok := agg[family]
		if !ok {
			g = &GapSummary{Family: family}
			agg[family] = g
		}
		g.Calls++
		g.RawBytes += inv.RawBytes
	}
	out := make([]GapSummary, 0, len(agg))
	for _, g := range agg {
		out = append(out, *g)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].RawBytes != out[j].RawBytes {
			return out[i].RawBytes > out[j].RawBytes
		}
		return out[i].Family < out[j].Family
	})
	return out, nil
}
