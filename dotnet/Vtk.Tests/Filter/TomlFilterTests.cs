// Port of internal/filter/tomlfilter/tomlfilter_test.go (#40/#61).
using Vtk.Core.Filter.Toml;
using Xunit;

namespace Vtk.Tests.Filter;

public class TomlFilterTests
{
    /// <summary>A minimal valid Spec that the per-op tests extend.</summary>
    private static Spec Base(string name) => new() { Name = name, MatchCommand = "^x" };

    public static IEnumerable<object[]> PipelineCases()
    {
        var strip = Base("strip");
        strip.StripLinesMatching = new List<string> { "^progress" };
        yield return new object[] { "strip_lines drops matching lines", strip, "progress 1\nkeep me\nprogress 2\n", "keep me\n" };

        var keep = Base("keep");
        keep.KeepLinesMatching = new List<string> { "error|warning" };
        // keep-only drops every non-matching line, including the trailing
        // empty line left by the input's terminal newline.
        yield return new object[] { "keep_lines keeps only matching, applied before strip", keep, "info: starting\nerror: boom\nnote\nwarning: heads up\n", "error: boom\nwarning: heads up" };

        var keepStrip = Base("k+s");
        keepStrip.KeepLinesMatching = new List<string> { "^L" };
        keepStrip.StripLinesMatching = new List<string> { "skip" };
        yield return new object[] { "keep then strip compose", keepStrip, "Lkeep\nLskip\nother\n", "Lkeep" };

        var ansi = Base("ansi");
        ansi.StripAnsi = true;
        yield return new object[] { "strip_ansi removes escape sequences", ansi, "\x1b[31merror\x1b[0m here", "error here" };

        var rep = Base("rep");
        rep.Replace = new List<ReplaceRule> { new(@"\d+\.\d+s", "Ns") };
        yield return new object[] { "replace substitutes across output", rep, "done in 4.21s and 0.02s", "done in Ns and Ns" };

        var mo = Base("mo");
        mo.MatchOutput = new List<ShortCircuit> { new(@"All \d+ tests passed", "tests: all green") };
        yield return new object[] { "match_output short-circuits to message", mo, "line 1\nAll 42 tests passed\nline 3", "tests: all green" };

        var trunc = Base("trunc");
        trunc.TruncateLinesAt = 5;
        yield return new object[] { "truncate_lines_at caps line length", trunc, "short\nabcdefghij", "short\nabcde..." };

        var max = Base("max");
        max.MaxLines = 2;
        yield return new object[] { "max_lines caps line count", max, "a\nb\nc\nd", "a\nb\n... (2 more lines)" };

        var empty = Base("empty");
        empty.StripLinesMatching = new List<string> { ".*" };
        empty.OnEmpty = "nothing to report";
        yield return new object[] { "on_empty message when filtered blank", empty, "a\nb\n", "nothing to report" };

        yield return new object[] { "no-op filter returns input unchanged", Base("noop"), "unchanged content", "unchanged content" };
    }

    [Theory]
    [MemberData(nameof(PipelineCases))]
    public void PipelineOps(string name, Spec spec, string input, string want)
    {
        var c = spec.Compile();
        Assert.True(want == c.Fn(input), name);
    }

    public static IEnumerable<object[]> ValidateCases()
    {
        yield return new object[] { "valid minimal", Base("ok"), false };
        yield return new object[] { "missing name", new Spec { MatchCommand = "^x" }, true };
        yield return new object[] { "missing match_command", new Spec { Name = "x" }, true };
        var fs = Base("fs");
        fs.FilterStderr = true;
        yield return new object[] { "filter_stderr rejected", fs, true };
        var trunc = Base("t");
        trunc.TruncateLinesAt = -1;
        yield return new object[] { "negative truncate", trunc, true };
        var max = Base("m");
        max.MaxLines = -1;
        yield return new object[] { "negative max_lines", max, true };
        var re = Base("re");
        re.StripLinesMatching = new List<string> { "(" };
        yield return new object[] { "bad strip regex", re, true };
    }

    [Theory]
    [MemberData(nameof(ValidateCases))]
    public void Validate(string name, Spec spec, bool wantErr)
    {
        var ex = Record.Exception(spec.Validate);
        Assert.True(wantErr == (ex is not null), $"{name}: Validate() err = {ex?.Message}, wantErr = {wantErr}");
    }

    [Fact]
    public void ExitCodes_DefaultToZero()
    {
        var c = Base("d").Compile();
        Assert.Equal(new[] { 0 }, c.ExitCodes);

        var s = Base("r");
        s.ExitCodesList = new List<int> { 0, 1 };
        c = s.Compile();
        Assert.Equal(new[] { 0, 1 }, c.ExitCodes);
    }

    [Fact]
    public void Parse_RejectsUnknownKey()
    {
        var ex = Assert.Throws<FormatException>(() =>
            TomlFilter.Parse("name = \"x\"\nmatch_command = \"^x\"\nbogus = true\n"));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Parse_BadMatchCommand_FailsAtCompile()
    {
        var s = TomlFilter.Parse("name = \"x\"\nmatch_command = \"(\"\n");
        Assert.Throws<FormatException>(() => s.Compile());
    }

    [Fact]
    public void Parse_FullSchema_RoundTrips()
    {
        var s = TomlFilter.Parse("""
            name = "full"
            match_command = '^tool\b'
            exit_codes = [0, 1]
            strip_ansi = true
            strip_lines_matching = ['^noise']
            keep_lines_matching = ['keep']
            truncate_lines_at = 80
            max_lines = 100
            on_empty = "clean"

            [[replace]]
            pattern = '\d+s'
            replacement = "Ns"

            [[match_output]]
            pattern = "all good"
            message = "ok"

            [[tests.smoke]]
            input = "in"
            expected = "out"
            """);
        Assert.Equal("full", s.Name);
        Assert.Equal(new List<int> { 0, 1 }, s.ExitCodesList);
        Assert.True(s.StripAnsi);
        Assert.Equal(80, s.TruncateLinesAt);
        Assert.Equal(100, s.MaxLines);
        Assert.Equal("clean", s.OnEmpty);
        var r = Assert.Single(s.Replace);
        Assert.Equal("Ns", r.Replacement);
        var m = Assert.Single(s.MatchOutput);
        Assert.Equal("ok", m.Message);
        var fx = Assert.Single(s.Tests["smoke"]);
        Assert.Equal(new Fixture("in", "out"), fx);
    }

    /// <summary>Confirms the shipped embedded defs all parse, validate, and compile.</summary>
    [Fact]
    public void Load_EmbeddedDefs()
    {
        var compiled = TomlFilter.Load();
        Assert.NotEmpty(compiled);
        foreach (var c in compiled)
        {
            Assert.False(c.Name == "", "compiled filter missing name");
            Assert.NotNull(c.Match);
            Assert.NotNull(c.Fn);
        }
    }

    /// <summary>
    /// Runs every embedded filter's inline [[tests.*]] cases — the rtk
    /// "validate fixtures at build" analog. A filter file ships broken if any
    /// of its declared fixtures do not reproduce.
    /// </summary>
    [Fact]
    public void EmbeddedFixtures_AllReproduce()
    {
        var specs = TomlFilter.Specs();
        var total = 0;
        foreach (var (file, spec) in specs)
        {
            var c = spec.Compile();
            foreach (var (group, fixtures) in spec.Tests)
            {
                for (var i = 0; i < fixtures.Count; i++)
                {
                    total++;
                    var got = c.Fn(fixtures[i].Input);
                    Assert.True(fixtures[i].Expected == got,
                        $"{file} {group}[{i}]: Fn produced {got}");
                }
            }
        }
        Assert.True(total > 0, "no inline fixtures found in embedded filters");
    }
}
