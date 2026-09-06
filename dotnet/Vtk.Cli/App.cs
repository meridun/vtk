// vtk — V Token Killer. Transparent command wrapper that compacts tool
// output for AI coding agents. Port of cmd/vtk/main.go.
using System.Globalization;
using System.Text.RegularExpressions;
using Vtk.Core.Analytics;
using Vtk.Core.Filter;
using Vtk.Core.Runner;
using Vtk.Core.Session;
using Vtk.Core.Spool;

namespace Vtk.Cli;


public static class Program
{
    // Documented vtk meta subcommands that are not implemented yet
    // (docs/ToolCoverage.md, Meta row). Guarding them keeps exec fallthrough
    // from turning "not implemented" into "executable file not found" (#11).
    private static readonly HashSet<string> ReservedMeta = new() { "proxy" };

    // Single source of the usage line: printed to stderr (exit 2) when vtk is
    // invoked bare, and to stdout (exit 0) for an explicit help request.
    private const string UsageText = "usage: vtk <command> [args...] | vtk show <id> [--grep <pat>] | vtk gaps [--since <Nd|Nh|date>] [--file-issues [--yes] [--min-bytes N] [--min-calls N]] | vtk gain [--daily] [--graph] [--history] [--session [--sessions <dir>]] | vtk learn [--min-confidence X] [--min-occurrences N] [--sessions <dir>] [--out <file>] [--dry-run] | vtk discover [--sessions <dir>] [--top N] [--since <Nd|Nh|date>] | vtk install [--shell bash|pwsh] [--dry-run] [--uninstall] [--print] | vtk hooks <init|verify|rewrite> | vtk version";

    public static int Run(string[] args)
    {
        // Redirected (pipe/file) output re-encodes as UTF-8, matching the
        // UTF-8 decode in ProcessRunner.RunCaptured: without this, writes go
        // out in the ambient console codepage, whose best-fit mapping quietly
        // rewrites non-ASCII bytes a wrapped tool emitted ("—" -> "-") —
        // altered output, which vtk must never produce. A real console is
        // left untouched (the TTY path never writes captured content anyway).
        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });

        // Go's fmt.Println always emits "\n"; Console.WriteLine defaults to
        // Environment.NewLine ("\r\n" on Windows). Force "\n" so output is
        // byte-identical to the Go binary (spooled bytes, golden fixtures).
        Console.Out.NewLine = "\n";
        Console.Error.NewLine = "\n";

        if (args.Length == 0)
        {
            Console.Error.WriteLine(UsageText);
            return 2;
        }

        switch (args[0])
        {
            // Explicit help request resolves in-process (#118): without this
            // guard `--help`/`-h` spawn-fail as external commands and `help`
            // falls through to Windows cmd's HELP. Same reserved-spelling
            // mechanism as `version`/`--version` (#11, #98).
            case "help":
            case "--help":
            case "-h":
                Console.Out.WriteLine(UsageText);
                return 0;
            case "show": return CmdShow(args[1..]);
            case "gaps": return CmdGaps(args[1..]);
            case "gain": return CmdGain(args[1..]);
            case "learn": return Learn.Run(args[1..]);
            case "discover": return Discover.Run(args[1..]);
            case "install": return Install.Run(args[1..]);
            case "hooks": return Hooks.Run(args[1..]);
            case "version":
            case "--version": return VersionCmd.Run(args[1..]);
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

        // Transparent launcher prefixes (cross-env, npx, bare VAR=x) are
        // unwrapped for filter matching and telemetry attribution only (#97):
        // the executed command line stays `args` on every path, so command
        // semantics and exit-code parity are untouched.
        var match = Prefix.Unwrap(args);
        var found = Registry.Default().TryLookup(match, out var entry);

        // Interactive invocations bypass filtering entirely: interposing a
        // pipe would break the wrapped command's own TTY detection. A
        // bypassed covered command is not a coverage gap (tty-bypass); an
        // uncovered one still is — suppression is keyed on the lookup match,
        // not the command family.
        if (IsTTY())
        {
            var reason = found ? Store.ReasonTTYBypass : Store.ReasonNoFilter;
            return Passthrough(st, args, true, reason, match);
        }

        // `npm run <script>` — and its lifecycle aliases (`npm test`, ...)
        // (#120) — is a dispatch layer, not a leaf command: npm prepends a
        // banner and the real work is done by an inner tool (eslint, mocha,
        // ...). Strip the banner and delegate the body to the inner tool's
        // filter, with gap attribution to that inner tool — not to npm.
        if (IsNpmScript(match))
        {
            return RunNpm(st, args, match);
        }

        // `powershell -File <script>` / `pwsh -File <script>` (#131): the
        // family's traffic is script runs whose bulk is test-runner + server
        // log output, with no argv-derivable inner tool. Route success
        // output through the shared size-floored fold (#93 mechanism);
        // failures and small outputs stay inline.
        if (PowershellFile.Matches(match))
        {
            return RunPowershellFile(st, args, match);
        }

        if (!found)
        {
            return Passthrough(st, args, false, Store.ReasonNoFilter, match);
        }
        return RunFiltered(st, entry, args, match);
    }

    // npm subcommands that run a package script without the `run` verb
    // (npm's lifecycle aliases: `npm test` = `npm run test`, likewise its
    // shorthands and the start/stop/restart trio). They produce the same
    // banner/fold output shapes as `npm run` and must route through the same
    // dispatch layer (#120).
    private static readonly HashSet<string> NpmLifecycleAliases =
        new() { "test", "t", "tst", "start", "stop", "restart" };

    /// <summary>
    /// Reports whether args is an npm script invocation the dispatch layer
    /// handles: `npm run &lt;script&gt; ...` or a lifecycle alias (`npm test`,
    /// `npm t`, `npm tst`, `npm start`, `npm stop`, `npm restart`) (#120).
    /// Bare `npm run` (no script) and all other npm subcommands
    /// (install/ci/ls/...) fall through to normal gap-logged passthrough.
    /// </summary>
    internal static bool IsNpmScript(string[] args)
    {
        if (args.Length < 2 || args[0] != "npm") return false;
        if (args[1] == "run") return args.Length >= 3;
        return NpmLifecycleAliases.Contains(args[1]);
    }

    /// <summary>
    /// Captures the `npm run` output once, strips the npm banner, and routes
    /// the body to the inner tool's filter. Preserves every RunFiltered
    /// invariant: exit-code parity (returns the child's code on every
    /// branch), a panicking inner filter degrades to raw, and the spooled
    /// bytes are the full raw output (banner included) so `vtk show`
    /// recovers everything that was elided. Gap and stats attribution use
    /// the inner argv — prefix-unwrapped (#97), since npm scripts routinely
    /// expand to `cross-env VAR=x <tool> ...` — so `vtk gaps` points at the
    /// real tool rather than npm or its launcher. `match` is the unwrapped
    /// outer argv, used only when no npm banner is recognized.
    /// </summary>
    private static int RunNpm(Store st, string[] args, string[] match)
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

        // npm itself never started (runner already printed the error and
        // synthesized 127): not a coverage gap (#118).
        if (result.SpawnFailed)
        {
            return EmitRawAttr(match, Store.ReasonSpawnFail);
        }

        var strip = Npm.StripBanner(raw);
        if (!strip.Ok)
        {
            // No recognizable npm banner. Modern npm (>= 11) emits the banner
            // only on a TTY, so under capture the inner tool is
            // undiscoverable (#93) — the size-floored fold (registry option
            // C) is all that applies. Success only; any failure stays inline
            // raw, gap-logged under the npm family as before.
            if (result.ExitCode == 0)
            {
                if (raw.Length >= Fold.FloorBytes)
                {
                    if (TryFold(st, args, match, raw, raw, Npm.FoldName))
                        return result.ExitCode;
                    return EmitRawAttr(match, Store.ReasonSpoolFail);
                }
                // Below the floor: byte-identical per-stream passthrough,
                // logged as an intentional near-passthrough (filtered=true,
                // fold identity) so terse load-bearing scripts (`npm run
                // sdlc`) stop inflating the gap table without ever being
                // silently unlogged (#93 tier 2).
                Console.Out.Write(result.Stdout);
                Console.Error.Write(result.Stderr);
                LogInvocation(st, match, raw.Length, raw.Length, filtered: true, tty: false, Npm.FoldName);
                return result.ExitCode;
            }
            return EmitRawAttr(match, Store.ReasonNoFilter);
        }

        strip = strip with { Inner = Prefix.Unwrap(strip.Inner) };
        var found = Registry.Default().TryLookup(strip.Inner, out var entry);
        if (!found)
        {
            // Banner stripped but the inner tool has no filter: emit the
            // banner-stripped body (already a saving) and log the gap under
            // the inner tool's family so `vtk gaps` prioritizes the real tool.
            return EmitNpmBody(st, strip.Inner, raw, strip.Body, result.ExitCode, filterName: null);
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
        // prefer the banner-stripped body when that alone is a saving. The
        // filter ran and elided nothing, so the row is attributed to it
        // (filtered=true, its name) — not a coverage gap (#153).
        if (compact.Length >= strip.Body.Length)
        {
            return EmitNpmBody(st, strip.Inner, raw, strip.Body, result.ExitCode, entry.Name);
        }
        if (!ClearsSavingsBar(raw.Length, compact.Length))
        {
            // Inner filter shrank the body, but against the full raw the agent
            // would otherwise see the delta is below the savings bar (#52):
            // emit compact inline, no spool, no `OK`.
            Console.Out.Write(compact);
            if (compact != "" && !compact.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, strip.Inner, raw.Length, compact.Length, filtered: true, tty: false, entry.Name);
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
        LogInvocation(st, strip.Inner, raw.Length, compact.Length, filtered: true, tty: false, entry.Name);
        return result.ExitCode;
    }

    /// <summary>
    /// Prints the banner-stripped body and records the outcome. When the body
    /// is smaller than the raw (the banner was elided) it spools the full
    /// raw under an ID for recovery; otherwise it is a plain passthrough.
    /// Attribution is always to the inner tool's family. Any spool failure
    /// degrades to full raw so no output is ever lost.
    /// <paramref name="filterName"/> is the inner filter that ran and elided
    /// nothing (logged filtered=true under its name, matching the direct
    /// path's "nothing elided" row), or null when the inner tool has no
    /// filter (logged filtered=false, no-filter: a real coverage gap) (#153).
    /// </summary>
    private static int EmitNpmBody(Store st, string[] inner, string raw, string body, int code, string? filterName)
    {
        var filtered = filterName != null;
        var reason = filterName ?? Store.ReasonNoFilter;

        // Unrecognized inner tool with a successful, floor-clearing body:
        // the size-floored fold (#93, option C) beats banner-only stripping.
        // A fold failure falls through to the pre-fold behavior below (which
        // itself degrades to raw on spool failure — never lost output).
        if (code == 0 && body.Length >= Fold.FloorBytes && TryFold(st, inner, inner, raw, body, Npm.FoldName))
            return code;

        if (body.Length >= raw.Length)
        {
            // No saving from stripping the banner: emit the raw, logged
            // under the inner family so the tool still surfaces in `vtk gaps`
            // when it has no filter.
            Console.Out.Write(raw);
            if (raw != "" && !raw.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, inner, raw.Length, raw.Length, filtered, tty: false, reason);
            return code;
        }

        if (!ClearsSavingsBar(raw.Length, body.Length))
        {
            // Banner stripping saved bytes but below the savings bar (#52):
            // emit the body inline, no spool, no `OK`. Logged with the bytes
            // the body saved; a coverage gap only when no inner filter exists.
            Console.Out.Write(body);
            if (body != "" && !body.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, inner, raw.Length, body.Length, filtered, tty: false, reason);
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
        // Banner stripped; recorded with the bytes the body saved. A coverage
        // gap for the inner tool only when it has no filter.
        LogInvocation(st, inner, raw.Length, body.Length, filtered, tty: false, reason);
        return code;
    }

    /// <summary>
    /// Captures a `powershell -File`/`pwsh -File` run and routes it through
    /// the shared size-floored fold (#131; mechanism #93): success at or
    /// above <see cref="Fold.FloorBytes"/> folds behind `OK &lt;id&gt;` with the
    /// full raw spooled and a short summary tail inline; success below the
    /// floor is a byte-identical per-stream passthrough logged as an
    /// intentional near-passthrough under the fold identity; any failure
    /// stays raw inline and remains a genuine powershell-family gap.
    /// Exit-code parity holds on every branch, and a fold failure degrades
    /// to raw — never to lost output.
    /// </summary>
    private static int RunPowershellFile(Store st, string[] args, string[] match)
    {
        var result = ProcessRunner.RunCaptured(args);
        var raw = result.Combined;

        int EmitRaw(string reason)
        {
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            LogInvocation(st, match, raw.Length, raw.Length, filtered: false, tty: false, reason);
            return result.ExitCode;
        }

        // The host never started (runner already printed the error and
        // synthesized 127): not a coverage gap (#118).
        if (result.SpawnFailed)
        {
            return EmitRaw(Store.ReasonSpawnFail);
        }
        if (result.ExitCode != 0)
        {
            // Failures never fold: the script's error/test-failure output is
            // the load-bearing case. Full raw, still a coverage gap.
            return EmitRaw(Store.ReasonNoFilter);
        }
        if (raw.Length >= Fold.FloorBytes)
        {
            if (TryFold(st, args, match, raw, raw, PowershellFile.FoldName))
                return result.ExitCode;
            return EmitRaw(Store.ReasonSpoolFail);
        }
        // Below the floor: byte-identical per-stream passthrough, logged as
        // an intentional near-passthrough (filtered=true, fold identity) so
        // terse script runs leave the gap table without ever being silently
        // unlogged.
        Console.Out.Write(result.Stdout);
        Console.Error.Write(result.Stderr);
        LogInvocation(st, match, raw.Length, raw.Length, filtered: true, tty: false, PowershellFile.FoldName);
        return result.ExitCode;
    }

    /// <summary>
    /// The above-floor half of the size-floored fold (#93 mechanism, shared
    /// by `npm run` and `powershell -File`): spools the full raw (banner
    /// included when present) and emits the body's short summary tail plus
    /// `OK &lt;id&gt;`, logging filtered=true under <paramref name="foldName"/>,
    /// the fold's static identity. Callers gate on child exit 0 and
    /// <see cref="Fold.FloorBytes"/>. The 64KB floor strictly dominates the
    /// #52 savings bar, so a fold never fires a false recover-me signal.
    /// Returns false — emitting nothing — when the tail computation throws
    /// or the spool write fails, so the caller can degrade to raw
    /// (invariant 2: recovery must exist before anything is elided).
    /// </summary>
    private static bool TryFold(Store st, string[] spoolArgv, string[] logArgv, string raw, string body, string foldName)
    {
        if (!TryApplyFilter(Fold.Tail, body, out var tail)) return false;
        string id;
        try
        {
            id = st.Write(spoolArgv, raw, DateTime.UtcNow);
        }
        catch
        {
            return false;
        }
        if (tail != "")
        {
            Console.Out.Write(tail);
            if (!tail.EndsWith('\n')) Console.Out.WriteLine();
        }
        Console.Out.WriteLine($"OK {id}");
        LogInvocation(st, logArgv, raw.Length, tail.Length, filtered: true, tty: false, foldName);
        return true;
    }

    /// <summary>
    /// Runs the command with output untouched. Unless the output is a TTY,
    /// bytes are counted so the gap entry is measurable. <paramref name="logArgs"/>
    /// (when given) is the prefix-unwrapped argv used for gap attribution
    /// (#97); execution always uses <paramref name="args"/>.
    /// </summary>
    private static int Passthrough(Store? st, string[] args, bool tty, string reason, string[]? logArgs = null)
    {
        var code = ProcessRunner.RunPassthroughCounted(args, tty, out var n, out var spawnFailed);
        if (st is not null)
        {
            // A child that never started is not a coverage gap: log it under
            // spawn-fail so junk argv (self-flag typos, nonexistent commands)
            // never ranks a family in `vtk gaps` (#118).
            LogInvocation(st, logArgs ?? args, n, n, filtered: false, tty, spawnFailed ? Store.ReasonSpawnFail : reason);
        }
        return code;
    }

    /// <summary>
    /// Captures output, applies the filter, spools the raw bytes when content
    /// was elided, and emits "OK &lt;id&gt;". Any failure on this path degrades
    /// to raw passthrough — never to lost output (invariant 2). Exit-code
    /// parity holds on every branch (invariant 1). <paramref name="match"/> is
    /// the prefix-unwrapped argv (#97) used for stats/gap attribution;
    /// execution and spool provenance keep the original <paramref name="args"/>.
    /// </summary>
    private static int RunFiltered(Store st, Entry entry, string[] args, string[] match)
    {
        var result = ProcessRunner.RunCaptured(args);
        var raw = result.Combined;

        int EmitRaw(string reason)
        {
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            LogInvocation(st, match, raw.Length, raw.Length, filtered: false, tty: false, reason);
            return result.ExitCode;
        }

        // The covered tool never started (runner already printed the error
        // and synthesized 127): not a coverage gap (#118).
        if (result.SpawnFailed)
        {
            return EmitRaw(Store.ReasonSpawnFail);
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
            Console.Error.WriteLine($"vtk: filter for \"{match[0]}\" panicked; raw passthrough (see vtk gaps)");
            return EmitRaw(Store.ReasonFilterPanic);
        }

        if (compact.Length >= raw.Length)
        {
            // Nothing elided: raw output, no ID.
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            LogInvocation(st, match, raw.Length, raw.Length, filtered: true, tty: false, entry.Name);
            return result.ExitCode;
        }

        if (!ClearsSavingsBar(raw.Length, compact.Length))
        {
            // Compact is smaller but the delta is below the savings bar (#52):
            // emit it inline with no spool and no `OK` — nothing worth a
            // recover-me round-trip was elided.
            Console.Out.Write(compact);
            if (compact != "" && !compact.EndsWith('\n')) Console.Out.WriteLine();
            LogInvocation(st, match, raw.Length, compact.Length, filtered: true, tty: false, entry.Name);
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
        LogInvocation(st, match, raw.Length, compact.Length, filtered: true, tty: false, entry.Name);
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

    /// <summary>
    /// Prints the coverage-gap report, or with --file-issues turns recurring
    /// gap families into stage:intake filter issues (#34). Filing is an
    /// explicit, user-invoked action that shells to `gh` — outside the
    /// no-network non-goal, which binds only the wrap path and telemetry
    /// storage (registry / #34). It is dry-run by default; --yes actually
    /// creates issues. `--since &lt;Nd|Nh|date&gt;` (#140) windows both the
    /// report and the filing measurement; without it all history is read,
    /// output byte-identical to before the flag existed.
    /// </summary>
    private static int CmdGaps(string[] args)
    {
        var fileIssues = false;
        var o = new FileIssuesOpts();
        string? sinceSpec = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file-issues":
                    fileIssues = true;
                    break;
                case "--since":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk gaps: --since requires a value (<N>d, <N>h, or an ISO-8601 date)");
                        return 2;
                    }
                    i++;
                    if (!SinceSpec.TryParse(args[i], DateTime.UtcNow, out var since))
                    {
                        Console.Error.WriteLine($"vtk gaps: invalid --since \"{args[i]}\" (want <N>d, <N>h, or an ISO-8601 date)");
                        return 2;
                    }
                    sinceSpec = args[i];
                    o.Since = since;
                    break;
                case "--yes":
                    o.Yes = true;
                    break;
                case "--min-bytes":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk gaps: --min-bytes requires a value");
                        return 2;
                    }
                    i++;
                    if (!long.TryParse(args[i], out var mb) || mb < 0)
                    {
                        Console.Error.WriteLine($"vtk gaps: invalid --min-bytes \"{args[i]}\"");
                        return 2;
                    }
                    o.MinBytes = mb;
                    break;
                case "--min-calls":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk gaps: --min-calls requires a value");
                        return 2;
                    }
                    i++;
                    if (!int.TryParse(args[i], out var mc) || mc < 0)
                    {
                        Console.Error.WriteLine($"vtk gaps: invalid --min-calls \"{args[i]}\"");
                        return 2;
                    }
                    o.MinCalls = mc;
                    break;
                default:
                    Console.Error.WriteLine($"vtk gaps: unexpected argument \"{args[i]}\"");
                    return 2;
            }
        }
        if (!fileIssues &&
            (o.Yes || o.MinBytes != GapsIssues.DefaultMinBytes || o.MinCalls != GapsIssues.DefaultMinCalls))
        {
            Console.Error.WriteLine("vtk gaps: --yes/--min-bytes/--min-calls require --file-issues");
            return 2;
        }

        Store st;
        try { st = Store.Open(); }
        catch (Exception ex) { Console.Error.WriteLine($"vtk: {ex.Message}"); return 1; }

        // Window header only when a window was asked for — the unwindowed
        // report stays byte-identical (#140).
        if (o.Since is { } win)
            Console.Out.WriteLine($"window: since {SinceSpec.Format(win)} ({sinceSpec})");

        if (!fileIssues) return PrintGapsReport(st, o.Since);

        // Sane floors: below-floor thresholds clamp up (anti-spam guard) so
        // an over-eager invocation can't file trivial gaps.
        if (o.MinBytes < GapsIssues.FloorMinBytes)
        {
            Console.Error.WriteLine($"vtk gaps: --min-bytes {o.MinBytes} below floor; using {GapsIssues.FloorMinBytes}");
            o.MinBytes = GapsIssues.FloorMinBytes;
        }
        if (o.MinCalls < GapsIssues.FloorMinCalls)
        {
            Console.Error.WriteLine($"vtk gaps: --min-calls {o.MinCalls} below floor; using {GapsIssues.FloorMinCalls}");
            o.MinCalls = GapsIssues.FloorMinCalls;
        }
        return GapsIssues.Run(st, o);
    }

    /// <summary>Emits the human-facing coverage-gap and degraded-filter tables (the default `vtk gaps` output).</summary>
    private static int PrintGapsReport(Store st, DateTime? since)
    {
        var gaps = st.Gaps(since);
        var degraded = st.Degraded(since);

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

    /// <summary>
    /// Reports cumulative token savings from the invocation log, dollarized
    /// via the checked-in price table + bytes/4 heuristic (#58). Optional
    /// rollups: --daily (per-UTC-day table), --graph (bar chart of daily
    /// saved bytes), --history (recent invocations), --session (savings per
    /// recent agent session, #46 — attribution by transcript time window).
    /// Read-only over metadata — no exec, no network, no output content
    /// (invariant 3; the session view reads only transcript timestamps).
    /// </summary>
    private static int CmdGain(string[] args)
    {
        bool daily = false, graph = false, history = false, session = false;
        string? sessionsDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--daily": daily = true; break;
                case "--graph": graph = true; break;
                case "--history": history = true; break;
                case "--session": session = true; break;
                case "--sessions":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk gain: --sessions requires a directory");
                        return 2;
                    }
                    i++;
                    sessionsDir = args[i];
                    break;
                default:
                    Console.Error.WriteLine($"vtk gain: unexpected argument \"{args[i]}\"");
                    Console.Error.WriteLine("usage: vtk gain [--daily] [--graph] [--history] [--session [--sessions <dir>]]");
                    return 2;
            }
        }

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
        Console.Out.WriteLine($"~ {Tokens(report.Saved)} tokens = {Usd(Economics.SavedUsd(report.Saved))} saved ({Economics.DefaultModel} input @ {Usd(Economics.PriceFor(Economics.DefaultModel))}/MTok, bytes/4 heuristic)");
        Console.Out.WriteLine();
        Console.Out.WriteLine($"{"FAMILY",-24} {"CALLS",7} {"RAW BYTES",12} {"SAVED",12} {"SAVED%",8}");
        foreach (var g in report.Families)
            Console.Out.WriteLine($"{g.Family,-24} {g.Calls,7} {g.RawBytes,12} {g.Saved,12} {Percent(g.Saved, g.RawBytes),8}");

        var days = daily || graph ? st.Daily() : null;
        if (daily && days is not null)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"{"DATE",-10} {"CALLS",7} {"RAW BYTES",12} {"SAVED",12} {"SAVED%",8} {"~TOKENS",9} {"~USD",10}");
            foreach (var d in days)
                Console.Out.WriteLine($"{d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),-10} {d.Calls,7} {d.RawBytes,12} {d.Saved,12} {Percent(d.Saved, d.RawBytes),8} {Tokens(d.Saved),9} {Usd(Economics.SavedUsd(d.Saved)),10}");
        }
        if (graph && days is not null)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("daily saved bytes (last 30 days logged)");
            var window = days.Count <= 30 ? days : days[^30..];
            var max = window.Max(d => Math.Max(d.Saved, 0));
            foreach (var d in window)
            {
                var len = max > 0 ? (int)Math.Round(Math.Max(d.Saved, 0) / (double)max * 40) : 0;
                Console.Out.WriteLine($"{d.Date.ToString("MM-dd", CultureInfo.InvariantCulture)} |{new string('#', len),-40} {d.Saved} ({Usd(Economics.SavedUsd(d.Saved))})");
            }
        }
        if (history)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("recent invocations");
            foreach (var inv in st.Recent(10))
            {
                var saved = inv.RawBytes - inv.OutBytes;
                var cmd = inv.Cmd.Length <= 40 ? inv.Cmd : inv.Cmd[..37] + "...";
                Console.Out.WriteLine($"{inv.Time.ToUniversalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)}  {cmd,-40}  saved {saved,10} ({Percent(saved, inv.RawBytes),6}, {Usd(Economics.SavedUsd(saved))})");
            }
        }
        if (session)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("savings per session (invocations within each transcript's time window)");
            var dir = sessionsDir ?? SessionProvider.SessionDirFor(Directory.GetCurrentDirectory());
            var windows = SessionWindows(dir);
            var sessions = st.BySession(windows);
            if (sessions.Count == 0)
            {
                Console.Out.WriteLine(windows.Count == 0
                    ? $"no session transcripts in {dir}"
                    : "no logged invocations fall inside a session window");
            }
            else
            {
                Console.Out.WriteLine($"{"SESSION",-10} {"START (UTC)",-16} {"CALLS",7} {"SAVED",12} {"SAVED%",8} {"~TOKENS",9} {"~USD",10}");
                var window = sessions.Count <= 10 ? sessions : sessions[^10..];
                foreach (var s in window)
                {
                    var id = s.Id.Length <= 8 ? s.Id : s.Id[..8];
                    Console.Out.WriteLine($"{id,-10} {s.Start.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),-16} {s.Calls,7} {s.Saved,12} {Percent(s.Saved, s.RawBytes),8} {Tokens(s.Saved),9} {Usd(Economics.SavedUsd(s.Saved)),10}");
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Reads each transcript's time window from a session directory: id =
    /// file name stem, bounds = first/last top-level timestamp. Unreadable
    /// or timestamp-less transcripts are skipped — best-effort scan, same as
    /// learn's. Only timestamps are read; transcript content never surfaces.
    /// </summary>
    private static List<(string Id, DateTime Start, DateTime End)> SessionWindows(string dir)
    {
        var windows = new List<(string, DateTime, DateTime)>();
        foreach (var file in SessionProvider.SessionFiles(dir))
        {
            (DateTime Start, DateTime End)? w;
            try { w = SessionReader.ReadWindow(File.ReadLines(file)); }
            catch (IOException) { continue; } // transcript in use / vanished
            if (w is null) continue;
            windows.Add((Path.GetFileNameWithoutExtension(file), w.Value.Start, w.Value.End));
        }
        return windows;
    }

    /// <summary>Formats a token count with invariant thousands separators.</summary>
    private static string Tokens(long savedBytes) =>
        Economics.TokensFromBytes(savedBytes).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Formats USD with invariant culture: two decimals minimum, four when the value is tiny.</summary>
    private static string Usd(double v) => "$" + v.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>Formats saved/raw as a percentage, guarding raw&lt;=0 to avoid a divide-by-zero.</summary>
    private static string Percent(long saved, long raw)
    {
        if (raw <= 0) return "n/a";
        return $"{(double)saved / raw * 100:F1}%";
    }
}
