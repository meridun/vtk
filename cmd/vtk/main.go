// vtk — Voya Token Killer. Transparent command wrapper that compacts tool
// output for AI coding agents. See docs/Architecture.md.
package main

import (
	"fmt"
	"os"
	"os/exec"
)

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "usage: vtk <command> [args...]")
		os.Exit(2)
	}

	// Skeleton: raw passthrough only. Filter registry, spool, and gap
	// logging land next; the exit-code invariant holds from day one.
	cmd := exec.Command(os.Args[1], os.Args[2:]...)
	cmd.Stdin = os.Stdin
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			os.Exit(exitErr.ExitCode())
		}
		fmt.Fprintf(os.Stderr, "vtk: %v\n", err)
		os.Exit(127)
	}
}
