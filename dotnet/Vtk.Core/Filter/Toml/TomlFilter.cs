// Declarative TOML filter definitions compiled into pure filter functions
// (raw captured output in, compact output out — the same contract as the
// hand-written C# filter families). Most tools only need to drop
// progress/noise lines and cap length; expressing that as ~20 lines of TOML
// with inline fixtures — instead of a bespoke filter class per tool — is the
// enabler for long-tail coverage (issue #40, ported from Go per #61).
//
// A compiled filter is a plain FilterFunc; this file never references the
// filter registry (the registry consumes Load()) and never spawns processes
// or touches the filesystem beyond the build-time embed
// (docs/Architecture.md invariant 4). Definitions live in Defs/*.toml and
// are embedded at build; Load parses, validates, and compiles them.
using System.Text.RegularExpressions;
using Tomlyn.Model;

namespace Vtk.Core.Filter.Toml;

/// <summary>A regex substitution applied across the whole output.</summary>
public sealed record ReplaceRule(string Pattern, string Replacement);

/// <summary>Collapses the entire output to Message when Pattern matches anywhere in it (rtk's match_output).</summary>
public sealed record ShortCircuit(string Pattern, string Message);

/// <summary>One inline test case: raw Input in, expected compact Expected out.</summary>
public sealed record Fixture(string Input, string Expected);

/// <summary>
/// The parsed TOML filter definition (rtk's schema, verified against the
/// issue #40 field table). Zero-value fields are inert, so a minimal filter
/// is just name + match_command + one line operation.
/// </summary>
public sealed class Spec
{
    public string Name { get; set; } = "";
    public string MatchCommand { get; set; } = "";
    public List<int> ExitCodesList { get; set; } = new();

    public bool StripAnsi { get; set; }
    public bool FilterStderr { get; set; }

    public List<string> StripLinesMatching { get; set; } = new();
    public List<string> KeepLinesMatching { get; set; } = new();

    public List<ReplaceRule> Replace { get; set; } = new();
    public List<ShortCircuit> MatchOutput { get; set; } = new();

    public int TruncateLinesAt { get; set; }
    public int MaxLines { get; set; }
    public string OnEmpty { get; set; } = "";

    public Dictionary<string, List<Fixture>> Tests { get; set; } = new();

    /// <summary>
    /// Reports whether the Spec is well-formed and buildable, throwing
    /// otherwise. It rejects definitions that cannot be honored faithfully
    /// rather than degrading them silently — a silent semantic change would
    /// violate the wrapper's transparency contract.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new FormatException("name is required");
        if (string.IsNullOrWhiteSpace(MatchCommand))
            throw new FormatException($"{Name}: match_command is required");
        if (FilterStderr)
        {
            // By the time a FilterFunc runs, the runner has already merged the
            // child's stdout and stderr into one string (Vtk.Cli/App.cs), so
            // the stream a FilterFunc sees is undivided and stderr cannot be
            // dropped here. Honoring filter_stderr needs runner-side stream
            // separation; until that lands, reject it rather than pretend to
            // apply it (issue #40).
            throw new FormatException($"{Name}: filter_stderr is not supported yet (needs runner stream separation; see issue #40)");
        }
        if (TruncateLinesAt < 0)
            throw new FormatException($"{Name}: truncate_lines_at must be >= 0");
        if (MaxLines < 0)
            throw new FormatException($"{Name}: max_lines must be >= 0");
        CompilePipeline(); // surfaces a bad regex as a build/test-time error
    }

    /// <summary>
    /// Validates the Spec and returns a Compiled filter. The returned Fn is a
    /// pure function; matching noise is elided per the pipeline and, if
    /// nothing is elided, the input is returned unchanged so the caller
    /// passes it through raw.
    /// </summary>
    public Compiled Compile()
    {
        Validate();
        Regex match;
        try
        {
            match = new Regex(MatchCommand);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"{Name}: match_command \"{MatchCommand}\": {ex.Message}");
        }
        var c = CompilePipeline();
        return new Compiled
        {
            Name = Name,
            Match = match,
            Fn = MakeFn(c),
            ExitCodes = ExitCodes(),
        };
    }

    /// <summary>The child exit codes this filter may run on, defaulting to {0} (clean run only), matching Register's default in the filter registry.</summary>
    internal int[] ExitCodes() => ExitCodesList.Count == 0 ? new[] { 0 } : ExitCodesList.ToArray();

    /// <summary>Pre-built regexes for one Spec's pipeline.</summary>
    private sealed class Pipeline
    {
        public List<Regex> StripLines = new();
        public List<Regex> KeepLines = new();
        public List<(Regex Re, string With)> Replace = new();
        public List<(Regex Re, string Msg)> MatchOutput = new();
    }

    /// <summary>Pre-builds every regex in the pipeline, surfacing a bad pattern as a build/test-time error instead of a runtime exception.</summary>
    private Pipeline CompilePipeline()
    {
        var c = new Pipeline();
        List<Regex> Build(List<string> pats, string field)
        {
            var outList = new List<Regex>(pats.Count);
            foreach (var p in pats)
            {
                try { outList.Add(new Regex(p)); }
                catch (ArgumentException ex) { throw new FormatException($"{Name}: {field} \"{p}\": {ex.Message}"); }
            }
            return outList;
        }
        c.StripLines = Build(StripLinesMatching, "strip_lines_matching");
        c.KeepLines = Build(KeepLinesMatching, "keep_lines_matching");
        foreach (var r in Replace)
        {
            try { c.Replace.Add((new Regex(r.Pattern), r.Replacement)); }
            catch (ArgumentException ex) { throw new FormatException($"{Name}: replace \"{r.Pattern}\": {ex.Message}"); }
        }
        foreach (var m in MatchOutput)
        {
            try { c.MatchOutput.Add((new Regex(m.Pattern), m.Message)); }
            catch (ArgumentException ex) { throw new FormatException($"{Name}: match_output \"{m.Pattern}\": {ex.Message}"); }
        }
        return c;
    }

    // Matches CSI/SGR escape sequences (colors, cursor moves) so strip_ansi
    // can drop terminal styling before line matching.
    private static readonly Regex AnsiRe = new("\x1b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    /// <summary>
    /// Builds the display pipeline closure. Order: strip_ansi →
    /// match_output short-circuit → keep_lines → strip_lines → replace →
    /// truncate_lines_at → max_lines → on_empty.
    /// </summary>
    private FilterFunc MakeFn(Pipeline c)
    {
        return raw =>
        {
            var output = raw;
            if (StripAnsi)
            {
                output = AnsiRe.Replace(output, "");
            }

            // Short-circuit: if any match_output pattern hits, the whole
            // output collapses to its message.
            foreach (var (re, msg) in c.MatchOutput)
            {
                if (re.IsMatch(output)) return msg;
            }

            if (c.KeepLines.Count > 0 || c.StripLines.Count > 0)
            {
                var lines = output.Split('\n');
                var kept = new List<string>(lines.Length);
                foreach (var ln in lines)
                {
                    if (c.KeepLines.Count > 0 && !AnyMatch(c.KeepLines, ln)) continue;
                    if (AnyMatch(c.StripLines, ln)) continue;
                    kept.Add(ln);
                }
                output = string.Join('\n', kept);
            }

            foreach (var (re, with) in c.Replace)
            {
                output = re.Replace(output, with);
            }

            if (TruncateLinesAt > 0)
            {
                var lines = output.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length > TruncateLinesAt)
                        lines[i] = lines[i][..TruncateLinesAt] + "...";
                }
                output = string.Join('\n', lines);
            }

            if (MaxLines > 0)
            {
                var lines = output.Split('\n');
                if (lines.Length > MaxLines)
                {
                    var more = lines.Length - MaxLines;
                    output = string.Join('\n', lines[..MaxLines]) + $"\n... ({more} more lines)";
                }
            }

            if (OnEmpty != "" && string.IsNullOrWhiteSpace(output))
            {
                return OnEmpty;
            }
            return output;
        };
    }

    private static bool AnyMatch(List<Regex> res, string s)
    {
        foreach (var re in res)
        {
            if (re.IsMatch(s)) return true;
        }
        return false;
    }
}

/// <summary>A validated, ready-to-run TOML filter: its match regex, the display filter, and the child exit codes it may run on.</summary>
public sealed class Compiled
{
    public required string Name { get; init; }
    public required Regex Match { get; init; }
    public required FilterFunc Fn { get; init; }
    public required int[] ExitCodes { get; init; }
}

public static class TomlFilter
{
    /// <summary>
    /// Decodes a single TOML filter definition. It is strict: unknown keys
    /// are rejected so a typo in a filter file fails loudly at build/test
    /// time rather than silently disabling an operation. Key mapping is done
    /// by hand over Tomlyn's generic model so strictness is enforced by this
    /// code, not by library binding behavior.
    /// </summary>
    public static Spec Parse(string data)
    {
        var table = Tomlyn.Toml.ToModel(data); // throws TomlException on bad syntax
        var s = new Spec();
        var unknown = new List<string>();
        foreach (var (key, value) in table)
        {
            switch (key)
            {
                case "name": s.Name = AsString(key, value); break;
                case "match_command": s.MatchCommand = AsString(key, value); break;
                case "exit_codes": s.ExitCodesList = AsIntList(key, value); break;
                case "strip_ansi": s.StripAnsi = AsBool(key, value); break;
                case "filter_stderr": s.FilterStderr = AsBool(key, value); break;
                case "strip_lines_matching": s.StripLinesMatching = AsStringList(key, value); break;
                case "keep_lines_matching": s.KeepLinesMatching = AsStringList(key, value); break;
                case "replace":
                    s.Replace = AsTableList(key, value, t => new ReplaceRule(
                        TableString(t, "replace", "pattern"), TableString(t, "replace", "replacement")));
                    break;
                case "match_output":
                    s.MatchOutput = AsTableList(key, value, t => new ShortCircuit(
                        TableString(t, "match_output", "pattern"), TableString(t, "match_output", "message")));
                    break;
                case "truncate_lines_at": s.TruncateLinesAt = AsInt(key, value); break;
                case "max_lines": s.MaxLines = AsInt(key, value); break;
                case "on_empty": s.OnEmpty = AsString(key, value); break;
                case "tests": s.Tests = ParseTests(value); break;
                default: unknown.Add(key); break;
            }
        }
        if (unknown.Count > 0)
            throw new FormatException($"unknown key(s): {string.Join(", ", unknown)}");
        return s;
    }

    private static Dictionary<string, List<Fixture>> ParseTests(object value)
    {
        if (value is not TomlTable tests)
            throw new FormatException("tests: expected a table of [[tests.<name>]] arrays");
        var result = new Dictionary<string, List<Fixture>>();
        foreach (var (group, groupValue) in tests)
        {
            if (groupValue is not TomlTableArray fixtures)
                throw new FormatException($"tests.{group}: expected an array of tables");
            var list = new List<Fixture>(fixtures.Count);
            foreach (var fx in fixtures)
            {
                list.Add(new Fixture(
                    TableString(fx, $"tests.{group}", "input"),
                    TableString(fx, $"tests.{group}", "expected")));
            }
            result[group] = list;
        }
        return result;
    }

    private static string AsString(string key, object value) =>
        value as string ?? throw new FormatException($"{key}: expected a string");

    private static bool AsBool(string key, object value) =>
        value is bool b ? b : throw new FormatException($"{key}: expected a bool");

    private static int AsInt(string key, object value) =>
        value is long l ? checked((int)l) : throw new FormatException($"{key}: expected an integer");

    private static List<string> AsStringList(string key, object value)
    {
        if (value is not TomlArray arr)
            throw new FormatException($"{key}: expected an array of strings");
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
            list.Add(item as string ?? throw new FormatException($"{key}: expected an array of strings"));
        return list;
    }

    private static List<int> AsIntList(string key, object value)
    {
        if (value is not TomlArray arr)
            throw new FormatException($"{key}: expected an array of integers");
        var list = new List<int>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not long l) throw new FormatException($"{key}: expected an array of integers");
            list.Add(checked((int)l));
        }
        return list;
    }

    private static List<T> AsTableList<T>(string key, object value, Func<TomlTable, T> map)
    {
        if (value is not TomlTableArray arr)
            throw new FormatException($"{key}: expected an array of tables ([[{key}]])");
        var list = new List<T>(arr.Count);
        foreach (var t in arr) list.Add(map(t));
        return list;
    }

    private static string TableString(TomlTable t, string ctx, string key) =>
        t.TryGetValue(key, out var v) && v is string s
            ? s
            : throw new FormatException($"{ctx}: missing or non-string \"{key}\"");

    private const string ResourcePrefix = "Vtk.Core.Filter.Toml.Defs.";

    private static IEnumerable<(string File, string Data)> EmbeddedDefs()
    {
        var asm = typeof(TomlFilter).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                     && n.EndsWith(".toml", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"{name}: missing embedded resource");
            using var reader = new StreamReader(stream);
            yield return (name[ResourcePrefix.Length..], reader.ReadToEnd());
        }
    }

    /// <summary>
    /// Parses, validates, and compiles every embedded filter definition,
    /// returning them sorted by resource name for deterministic registration
    /// order. Any bad definition is a hard error — a broken filter file must
    /// fail the build/tests, not ship disabled.
    /// </summary>
    public static List<Compiled> Load()
    {
        var result = new List<Compiled>();
        foreach (var (file, data) in EmbeddedDefs())
        {
            try
            {
                result.Add(Parse(data).Compile());
            }
            catch (Exception ex)
            {
                throw new FormatException($"{file}: {ex.Message}", ex);
            }
        }
        return result;
    }

    /// <summary>
    /// Parses and validates every embedded definition, returning the raw
    /// Specs (with their inline fixtures) keyed by file name so a test
    /// harness can exercise each filter's declared cases. Compilation errors
    /// surface here too.
    /// </summary>
    public static Dictionary<string, Spec> Specs()
    {
        var result = new Dictionary<string, Spec>();
        foreach (var (file, data) in EmbeddedDefs())
        {
            try
            {
                var spec = Parse(data);
                spec.Validate();
                result[file] = spec;
            }
            catch (Exception ex)
            {
                throw new FormatException($"{file}: {ex.Message}", ex);
            }
        }
        return result;
    }
}
