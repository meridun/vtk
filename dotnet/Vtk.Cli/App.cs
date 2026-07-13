// vtk — V Token Killer. Transparent command wrapper that compacts tool
// output for AI coding agents. Port of cmd/vtk/main.go.
using System.Text.RegularExpressions;
using Vtk.Core.Filter;
using Vtk.Core.Runner;
using Vtk.Core.Spool;

namespace Vtk.Cli;


public static class Program
{
    // Documented vtk meta subcommands that are not implemented yet
    // (docs/ToolCoverage.md, Meta row). Guarding them keeps exec fallthrough
    // from turning "not implemented" into "executable file not found" (#11).
    private static readonly HashSet<string> ReservedMeta = new() { "proxy" };

    public static int Run(string[] args)
    {
        // Go's fmt.Println always emits "\n"; Console.WriteLine defaults to
        // Environment.NewLine ("\r\n" on Windows). Force "\n" so output is
        // byte-identical to the Go binary (spooled bytes, golden fixtures).
        Console.Out.NewLine = "\n";
        Console.Error.NewLine = "\n";

        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vtk <command> [args...] | vtk show <id> [--grep <pat>] | vtk gaps | vtk gain | vtk install [--shell bash|pwsh] [--dry-run] [--uninstall] [--print]");
            return 2;
        }

        switch (args[0])
        {
            case "show": return CmdShow(args[1..]);
            case "gaps": return CmdGaps();
            case "gain": return CmdGain();
            case "install": return Install.Run(args[1..]);
        }

        if (ReservedMeta.Contains(args[0]))
        {
            Console.Error.WriteLine($"vtk: \"{args[0]}\" is not implemented yet");
            return 2;
        }

        Store st;
        try
        {
            st = Store.Open();
        }
        catch (Exception ex)
        {
            // Degrade: no spool means no filtering (elided output would be
            // unrecoverable) and no gap log. Warn — a silent gap is a bug.
            Console.Error.WriteLine($"vtk: spool unavailable ({ex.Message}); raw passthrough");
            return Passthrough(null, args, IsTTY(), Store.ReasonSpoolFail);
        }
        st.Sweep(Store.DefaultTTL, DateTime.UtcNow);

        var found = Registry.Default().TryLookup(args, out var entry);

        // Interactive invocations bypass filtering entirely: interposing a
        // pipe would break the wrapped command's own TTY detection. A
        // bypassed covered command is not a coverage gap (tty-bypass); an
        // uncovered one still is — suppression is keyed on the lookup match,
        // not the command family.
        if (IsTTY())
        {
            var reason = found ? Store.ReasonTTYBypass : Store.ReasonNoFilter;
            return Passthrough(st, args, true, reason);
        }

        // `npm run <script>` is a dispatch layer, not a leaf command: npm
        // prepends a banner and the real work is done by an inner tool
        // (eslint, mocha, ...). Strip the banner and delegate the body to
        // the inner tool's filter, with gap attribution to that inner tool
        // — not to npm.
        if (IsNpmRun(args))
        {
            return RunNpm(st, args);
        }

        if (!found)
        {
            return Passthrough(st, args, false, Store.ReasonNoFilter);
        }
        return RunFiltered(st, entry, args);
    }

    /// <summary>
    /// Reports whether args is an `npm run &lt;script&gt; ...` invocation, the
    /// only npm form that carries the dispatch banner this layer handles.
    /// Bare `npm run` (no script) and other npm subcommands fall through to
    /// normal gap-logged passthrough.
    /// </summary>
    private static bool IsNpmRun(string[] args) =>
        args.Length >= 3 && args[0] == "npm" && args[1] == "run";

    /// <summary>
    /// Captures the `npm run` output once, strips the npm banner, and routes
    /// the body to the inner tool's filter. Preserves every RunFiltered
    /// invariant: exit-code parity (returns the child's code on every
    /// branch), a panicking inner filter degrades to raw, and the spooled
    /// bytes are the full raw output (banner included) so `vtk show`
    /// recovers everything that was elided. Gap and stats attribution use
    /// the inner argv, so `vtk gaps` points at the real tool rather than npm.
    /// </summary>
    private static int RunNpm(Store st, string[] args)
    {
        var result = ProcessRunner.RunCaptured(args);
        var raw = result.Combined;

        int EmitRawAttr(string[] logArgs, string reason)
        {
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            LogInvocation(st, logArgs, raw.Length, raw.Length, filtered: false, tty: false, reason);
            return result.ExitCode;
        }

        var strip = Npm.StripBanner(raw);
        if (!strip.Ok)
        {
            // Not a recognizable npm banner: treat as an ordinary uncovered
            // command, gap-logged under the npm family.
            return EmitRawAttr(args, Store.ReasonNoFilter);
        }

        var found = Registry.Default().TryLookup(strip.Inner, out var entry);
        if (!found)
        {
            // Banner stripped but the inner tool has no filter: emit the
            // banner-stripped body (already a saving) and log the gap under
            // the inner tool's family so `vtk gaps` prioritizes the real tool.
            return EmitNpmBody(st, strip.Inner, raw, strip.Body, result.ExitCode);
        }
        if (!entry.Filters(result.ExitCode))
        {
            // Inner filter exists but this exit code is outside its
            // allowlist (a genuine failure): keep full raw output,
            // attributed to the inner tool.
            return EmitRawAttr(strip.Inner, Store.ReasonNonzeroExit);
        }

        if (!TryApplyFilter(entry.Fn, strip.Body, out var compact))
        {
            Console.Error.WriteLine($"vtk: filter for \"{strip.Inner[0]}\" panicked; raw passthrough (see vtk gaps)");
            return EmitRawAttr(strip.Inner, Store.ReasonFilterPanic);
        }
        // Compare the compacted output against the raw the agent would
        // otherwise see: if the inner filter did not shrink the body, still
        // prefer the banner-stripped body when that alone is a saving.
        if (compact.Length >= strip.Body.Length)
        {
            return EmitNpmBody(st, strip.Inner, raw, strip.Body, result.ExitCode);
        }
        if (!ClearsSavingsBar(raw.Length, compact.Length))
        {
            // Inner filter shrank the body, but against the full raw the agent
            // would otherwise see the delta is below the savings bar (#52):
            // emit compact inline, no spool, no `OK`.
            Console.Out.Write(compact);
            if (compact != "" && !compact.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, strip.Inner, raw.Length, compact.Length, filtered: true, tty: false, "");
            return result.ExitCode;
        }
        // Spool the full raw (banner + body) so `vtk show` recovers everything.
        string id;
        try
        {
            id = st.Write(strip.Inner, raw, DateTime.UtcNow);
        }
        catch
        {
            return EmitRawAttr(strip.Inner, Store.ReasonSpoolFail);
        }
        if (compact != "")
        {
            Console.Out.Write(compact);
            if (!compact.EndsWith('\n')) Console.Out.WriteLine();
        }
        Console.Out.WriteLine($"OK {id}");
        LogInvocation(st, strip.Inner, raw.Length, compact.Length, filtered: true, tty: false, "");
        return result.ExitCode;
    }

    /// <summary>
    /// Prints the banner-stripped body and records the outcome. When the body
    /// is smaller than the raw (the banner was elided) it spools the full
    /// raw under an ID for recovery; otherwise it is a plain passthrough.
    /// Attribution is always to the inner tool's family. Any spool failure
    /// degrades to full raw so no output is ever lost.
    /// </summary>
    private static int EmitNpmBody(Store st, string[] inner, string raw, string body, int code)
    {
        if (body.Length >= raw.Length)
        {
            // No saving from stripping the banner: emit the raw and gap-log
            // the inner family so the tool still surfaces in `vtk gaps`.
            Console.Out.Write(raw);
            if (raw != "" && !raw.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, inner, raw.Length, raw.Length, filtered: false, tty: false, Store.ReasonNoFilter);
            return code;
        }

        if (!ClearsSavingsBar(raw.Length, body.Length))
        {
            // Banner stripping saved bytes but below the savings bar (#52):
            // emit the body inline, no spool, no `OK`. Still a coverage gap
            // for the inner tool, logged with the bytes the body saved.
            Console.Out.Write(body);
            if (body != "" && !body.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, inner, raw.Length, body.Length, filtered: false, tty: false, Store.ReasonNoFilter);
            return code;
        }

        string id;
        try
        {
            id = st.Write(inner, raw, DateTime.UtcNow);
        }
        catch
        {
            // Can't offer recovery for the elided banner: emit full raw instead.
            Console.Out.Write(raw);
            if (raw != "" && !raw.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, inner, raw.Length, raw.Length, filtered: false, tty: false, Store.ReasonSpoolFail);
            return code;
        }
        if (body != "")
        {
            Console.Out.Write(body);
            if (!body.EndsWith('\n')) Console.Out.WriteLine();
        }
        Console.Out.WriteLine($"OK {id}");
        // Banner stripped but no inner filter ran: this is still a coverage
        // gap for the inner tool, recorded with the bytes the body saved.
        LogInvocation(st, inner, raw.Length, body.Length, filtered: false, tty: false, Store.ReasonNoFilter);
        return code;
    }

    /// <summary>Runs the command with output untouched. Unless the output is a TTY, bytes are counted so the gap entry is measurable.</summary>
    private static int Passthrough(Store? st, string[] args, bool tty, string reason)
    {
        var code = ProcessRunner.RunPassthroughCounted(args, tty, out var n);
        if (st is not null)
        {
            LogInvocation(st, args, n, n, filtered: false, tty, reason);
        }
        return code;
    }

    /// <summary>
    /// Captures output, applies the filter, spools the raw bytes when content
    /// was elided, and emits "OK &lt;id&gt;". Any failure on this path degrades
    /// to raw passthrough — never to lost output (invariant 2). Exit-code
    /// parity holds on every branch (invariant 1).
    /// </summary>
    private static int RunFiltered(Store st, Entry entry, string[] args)
    {
        var result = ProcessRunner.RunCaptured(args);
        var raw = result.Combined;

        int EmitRaw(string reason)
        {
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            LogInvocation(st, args, raw.Length, raw.Length, filtered: false, tty: false, reason);
            return result.ExitCode;
        }

        if (!entry.Filters(result.ExitCode))
        {
            // Child exit is outside this filter's allowlist (a genuine
            // failure for most tools; a fatal/config error for report-style
            // ones). When in doubt, pass through unchanged.
            return EmitRaw(Store.ReasonNonzeroExit);
        }

        if (!TryApplyFilter(entry.Fn, raw, out var compact))
        {
            Console.Error.WriteLine($"vtk: filter for \"{args[0]}\" panicked; raw passthrough (see vtk gaps)");
            return EmitRaw(Store.ReasonFilterPanic);
        }

        if (compact.Length >= raw.Length)
        {
            // Nothing elided: raw output, no ID.
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            LogInvocation(st, args, raw.Length, raw.Length, filtered: true, tty: false, "");
            return result.ExitCode;
        }

        if (!ClearsSavingsBar(raw.Length, compact.Length))
        {
            // Compact is smaller but the delta is below the savings bar (#52):
            // emit it inline with no spool and no `OK` — nothing worth a
            // recover-me round-trip was elided.
            Console.Out.Write(compact);
            if (compact != "" && !compact.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, args, raw.Length, compact.Length, filtered: true, tty: false, "");
            return result.ExitCode;
        }

        string id;
        try
        {
            id = st.Write(args, raw, DateTime.UtcNow);
        }
        catch
        {
            return EmitRaw(Store.ReasonSpoolFail); // can't offer recovery: don't elide
        }
        if (compact != "")
        {
            Console.Out.Write(compact);
            if (!compact.EndsWith('\n')) Console.Out.WriteLine();
        }
        Console.Out.WriteLine($"OK {id}");
        LogInvocation(st, args, raw.Length, compact.Length, filtered: true, tty: false, "");
        return result.ExitCode;
    }

    /// <summary>Runs the filter, converting an exception into ok=false (a filter must never lose output — invariant 2).</summary>
    private static bool TryApplyFilter(FilterFunc f, string raw, out string compact)
    {
        try
        {
            compact = f(raw);
            return true;
        }
        catch
        {
            compact = "";
            return false;
        }
    }

    /// <summary>Absolute-byte floor a compact result must save before spool + `OK` fire (#52).</summary>
    internal const int MinSavingsBytes = 256;

    /// <summary>Savings ratio (0..1) a compact result must clear before spool + `OK` fire (#52).</summary>
    internal const double MinSavingsRatio = 0.20;

    /// <summary>
    /// True when compacting to <paramref name="shownLen"/> from <paramref name="rawLen"/> saves
    /// enough to justify spooling the raw and emitting the `OK &lt;id&gt;` recover-me signal:
    /// the byte delta must clear an absolute floor <b>and</b> a savings ratio (option C, #52).
    /// Below the bar the caller emits the compact output inline with no spool and no `OK`, so a
    /// lossless reformat (e.g. `git branch`) never fires a false recover-me signal.
    /// </summary>
    internal static bool ClearsSavingsBar(int rawLen, int shownLen)
    {
        var savings = rawLen - shownLen;
        if (savings < MinSavingsBytes) return false;
        if (rawLen <= 0) return false;
        return savings / (double)rawLen >= MinSavingsRatio;
    }

    private static void LogInvocation(Store st, string[] args, long rawBytes, long outBytes, bool filtered, bool tty, string reason)
    {
        try
        {
            st.LogInvocation(new Invocation
            {
                Time = DateTime.UtcNow,
                Cmd = string.Join(" ", args),
                RawBytes = rawBytes,
                OutBytes = outBytes,
                Filtered = filtered,
                TTY = tty,
                Reason = reason,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk: gap log failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reports whether stdout is an interactive terminal. A real isatty
    /// check is required: char-device sinks like NUL are not terminals, and
    /// treating them as such would route filtered commands through the
    /// interactive bypass.
    /// </summary>
    private static bool IsTTY() => !Console.IsOutputRedirected;

    private static int CmdShow(string[] args)
    {
        string id = "", pat = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--grep")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("vtk show: --grep requires a pattern");
                    return 2;
                }
                i++;
                pat = args[i];
            }
            else if (id == "")
            {
                id = args[i];
            }
            else
            {
                Console.Error.WriteLine($"vtk show: unexpected argument \"{args[i]}\"");
                return 2;
            }
        }
        if (id == "")
        {
            Console.Error.WriteLine("usage: vtk show <id> [--grep <pat>]");
            return 2;
        }

        Store st;
        try { st = Store.Open(); }
        catch (Exception ex) { Console.Error.WriteLine($"vtk: {ex.Message}"); return 1; }

        string content;
        try { content = st.Read(id); }
        catch { Console.Error.WriteLine($"vtk: no spool entry {id} (expired or never spooled)"); return 1; }

        if (pat == "")
        {
            Console.Out.Write(content);
            if (!content.EndsWith('\n')) Console.Out.WriteLine();
            return 0;
        }

        Regex re;
        try { re = new Regex(pat); }
        catch (Exception ex) { Console.Error.WriteLine($"vtk show: bad pattern: {ex.Message}"); return 2; }

        foreach (var line in content.Split('\n'))
        {
            if (re.IsMatch(line)) Console.Out.WriteLine(line);
        }
        return 0;
    }

    private static int CmdGaps()
    {
        Store st;
        try { st = Store.Open(); }
        catch (Exception ex) { Console.Error.WriteLine($"vtk: {ex.Message}"); return 1; }

        var gaps = st.Gaps();
        var degraded = st.Degraded();

        if (gaps.Count == 0 && degraded.Count == 0)
        {
            Console.Out.WriteLine("no gap entries");
            return 0;
        }
        if (gaps.Count > 0)
        {
            Console.Out.WriteLine($"{"FAMILY",-24} {"CALLS",7} {"RAW BYTES",12}");
            foreach (var g in gaps)
                Console.Out.WriteLine($"{g.Family,-24} {g.Calls,7} {g.RawBytes,12}");
        }
        if (degraded.Count > 0)
        {
            if (gaps.Count > 0) Console.Out.WriteLine();
            Console.Out.WriteLine("DEGRADED (filter panicked; output passed through raw)");
            Console.Out.WriteLine($"{"FAMILY",-24} {"CALLS",7} {"RAW BYTES",12}");
            foreach (var g in degraded)
                Console.Out.WriteLine($"{g.Family,-24} {g.Calls,7} {g.RawBytes,12}");
        }
        return 0;
    }

    /// <summary>Reports cumulative token savings from the invocation log. Read-only over metadata — no exec, no output content (invariant 3).</summary>
    private static int CmdGain()
    {
        Store st;
        try { st = Store.Open(); }
        catch (Exception ex) { Console.Error.WriteLine($"vtk: {ex.Message}"); return 1; }

        var report = st.Gain();
        if (report.Calls == 0)
        {
            Console.Out.WriteLine("no invocations logged");
            return 0;
        }
        Console.Out.WriteLine($"cumulative savings: {report.RawBytes} raw -> {report.OutBytes} emitted, saved {report.Saved} bytes ({Percent(report.Saved, report.RawBytes)}) over {report.Calls} calls");
        Console.Out.WriteLine();
        Console.Out.WriteLine($"{"FAMILY",-24} {"CALLS",7} {"RAW BYTES",12} {"SAVED",12} {"SAVED%",8}");
        foreach (var g in report.Families)
            Console.Out.WriteLine($"{g.Family,-24} {g.Calls,7} {g.RawBytes,12} {g.Saved,12} {Percent(g.Saved, g.RawBytes),8}");
        return 0;
    }

    /// <summary>Formats saved/raw as a percentage, guarding raw&lt;=0 to avoid a divide-by-zero.</summary>
    private static string Percent(long saved, long raw)
    {
        if (raw <= 0) return "n/a";
        return $"{(double)saved / raw * 100:F1}%";
    }
}
