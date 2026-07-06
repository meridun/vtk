// vtk — Voya Token Killer. Transparent command wrapper that compacts tool
// output for AI coding agents. See docs/Architecture.md.
package main

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"regexp"
	"strings"
	"time"

	"github.com/meridun/vtk/internal/filter"
	"github.com/meridun/vtk/internal/spool"
)

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(args []string) int {
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "usage: vtk <command> [args...] | vtk show <id> [--grep <pat>] | vtk gaps")
		return 2
	}
	switch args[0] {
	case "show":
		return cmdShow(args[1:])
	case "gaps":
		return cmdGaps()
	}

	st, err := spool.Open()
	if err != nil {
		// Degrade: no spool means no filtering (elided output would be
		// unrecoverable) and no gap log. Warn — a silent gap is a bug.
		fmt.Fprintf(os.Stderr, "vtk: spool unavailable (%v); raw passthrough\n", err)
		return passthrough(nil, args, isTTY(os.Stdout))
	}
	st.Sweep(spool.DefaultTTL, time.Now())

	// Interactive invocations bypass filtering entirely: interposing a pipe
	// would break the wrapped command's own TTY detection.
	if isTTY(os.Stdout) {
		return passthrough(st, args, true)
	}
	f, ok := filter.Default().Lookup(args)
	if !ok {
		return passthrough(st, args, false)
	}
	return runFiltered(st, f, args)
}

// passthrough runs the command with output untouched. Unless the output is a
// TTY it counts bytes through a tee so the gap entry is measurable.
func passthrough(st *spool.Store, args []string, tty bool) int {
	cmd := exec.Command(args[0], args[1:]...)
	cmd.Stdin = os.Stdin
	var out, errw countingWriter
	if tty {
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
	} else {
		out.w = os.Stdout
		errw.w = os.Stderr
		cmd.Stdout = &out
		cmd.Stderr = &errw
	}
	code := exitCode(cmd.Run())
	if st != nil {
		n := out.n + errw.n
		logInvocation(st, args, n, n, false, tty)
	}
	return code
}

// runFiltered captures output, applies the filter, spools the raw bytes when
// content was elided, and emits "OK <id>". Any failure on this path degrades
// to raw passthrough — never to lost output (invariant 2). Exit-code parity
// holds on every branch (invariant 1).
func runFiltered(st *spool.Store, f filter.Func, args []string) int {
	cmd := exec.Command(args[0], args[1:]...)
	cmd.Stdin = os.Stdin
	var outBuf, errBuf bytes.Buffer
	cmd.Stdout = &outBuf
	cmd.Stderr = &errBuf
	code := exitCode(cmd.Run())
	raw := outBuf.String() + errBuf.String()

	emitRaw := func() int {
		os.Stdout.Write(outBuf.Bytes())
		os.Stderr.Write(errBuf.Bytes())
		logInvocation(st, args, int64(len(raw)), int64(len(raw)), false, false)
		return code
	}

	if code != 0 {
		// When in doubt, pass through unchanged: failures keep full output.
		return emitRaw()
	}
	compact, ok := applyFilter(f, raw)
	if !ok {
		return emitRaw() // filter panicked: degrade + gap entry
	}
	if len(compact) >= len(raw) {
		// Nothing elided: raw output, no ID.
		os.Stdout.Write(outBuf.Bytes())
		os.Stderr.Write(errBuf.Bytes())
		logInvocation(st, args, int64(len(raw)), int64(len(raw)), true, false)
		return code
	}
	id, err := st.Write(args, raw, time.Now())
	if err != nil {
		return emitRaw() // can't offer recovery: don't elide
	}
	if compact != "" {
		fmt.Print(compact)
		if !strings.HasSuffix(compact, "\n") {
			fmt.Println()
		}
	}
	fmt.Printf("OK %s\n", id)
	logInvocation(st, args, int64(len(raw)), int64(len(compact)), true, false)
	return code
}

// applyFilter runs the filter, converting a panic into ok=false.
func applyFilter(f filter.Func, raw string) (compact string, ok bool) {
	defer func() {
		if r := recover(); r != nil {
			compact, ok = "", false
		}
	}()
	return f(raw), true
}

func logInvocation(st *spool.Store, args []string, rawBytes, outBytes int64, filtered, tty bool) {
	if st == nil {
		return
	}
	err := st.LogInvocation(spool.Invocation{
		Time:     time.Now().UTC(),
		Cmd:      strings.Join(args, " "),
		RawBytes: rawBytes,
		OutBytes: outBytes,
		Filtered: filtered,
		TTY:      tty,
	})
	if err != nil {
		fmt.Fprintf(os.Stderr, "vtk: gap log failed: %v\n", err)
	}
}

// exitCode maps cmd.Run's error to the wrapped command's exit code:
// 0 on success, the child's code on exit, 127 when the command could not run.
func exitCode(err error) int {
	if err == nil {
		return 0
	}
	if exitErr, ok := err.(*exec.ExitError); ok {
		return exitErr.ExitCode()
	}
	fmt.Fprintf(os.Stderr, "vtk: %v\n", err)
	return 127
}

func isTTY(f *os.File) bool {
	fi, err := f.Stat()
	if err != nil {
		return false
	}
	return fi.Mode()&os.ModeCharDevice != 0
}

type countingWriter struct {
	w interface{ Write([]byte) (int, error) }
	n int64
}

func (c *countingWriter) Write(p []byte) (int, error) {
	n, err := c.w.Write(p)
	c.n += int64(n)
	return n, err
}

func cmdShow(args []string) int {
	id, pat := "", ""
	for i := 0; i < len(args); i++ {
		switch {
		case args[i] == "--grep":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "vtk show: --grep requires a pattern")
				return 2
			}
			i++
			pat = args[i]
		case id == "":
			id = args[i]
		default:
			fmt.Fprintf(os.Stderr, "vtk show: unexpected argument %q\n", args[i])
			return 2
		}
	}
	if id == "" {
		fmt.Fprintln(os.Stderr, "usage: vtk show <id> [--grep <pat>]")
		return 2
	}
	st, err := spool.Open()
	if err != nil {
		fmt.Fprintf(os.Stderr, "vtk: %v\n", err)
		return 1
	}
	content, err := st.Read(id)
	if err != nil {
		fmt.Fprintf(os.Stderr, "vtk: no spool entry %s (expired or never spooled)\n", id)
		return 1
	}
	if pat == "" {
		fmt.Print(content)
		if !strings.HasSuffix(content, "\n") {
			fmt.Println()
		}
		return 0
	}
	re, err := regexp.Compile(pat)
	if err != nil {
		fmt.Fprintf(os.Stderr, "vtk show: bad pattern: %v\n", err)
		return 2
	}
	for _, line := range strings.Split(content, "\n") {
		if re.MatchString(line) {
			fmt.Println(line)
		}
	}
	return 0
}

func cmdGaps() int {
	st, err := spool.Open()
	if err != nil {
		fmt.Fprintf(os.Stderr, "vtk: %v\n", err)
		return 1
	}
	gaps, err := st.Gaps()
	if err != nil {
		fmt.Fprintf(os.Stderr, "vtk: %v\n", err)
		return 1
	}
	if len(gaps) == 0 {
		fmt.Println("no gap entries")
		return 0
	}
	fmt.Printf("%-24s %7s %12s\n", "FAMILY", "CALLS", "RAW BYTES")
	for _, g := range gaps {
		fmt.Printf("%-24s %7d %12d\n", g.Family, g.Calls, g.RawBytes)
	}
	return 0
}
