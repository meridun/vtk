using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>Parity tests against the Go filter's golden fixtures (internal/filter/files/testdata).</summary>
public class FilesTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "files");

    [Theory]
    [InlineData("ls_serverjs", "Ls", 0.60)]
    [InlineData("grep_rn", "Grep", 0.25)]
    [InlineData("grep_l", "Grep", 0.60)]
    [InlineData("find_walk", "Find", 0.10)]
    public void MatchesGoldenOutput(string name, string filterName, double minSavings)
    {
        Func<string, string> filter = filterName switch
        {
            "Ls" => Files.Ls,
            "Grep" => Files.Grep,
            "Find" => Files.Find,
            _ => throw new ArgumentException(filterName),
        };
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));
        var got = filter(raw);
        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    [Fact]
    public void LsLongFormat_PassesThrough()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "ls_long.raw.txt"));
        Assert.Equal(raw, Files.Ls(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void EmptyInput_PassesThrough(string raw)
    {
        Assert.Equal(raw, Files.Ls(raw));
        Assert.Equal(raw, Files.Grep(raw));
        Assert.Equal(raw, Files.Find(raw));
    }

    [Fact]
    public void Grep_KeepsUnrecognizedLinesVerbatim()
    {
        const string raw = "a.go:1:x\nBinary file b.bin matches\n";
        Assert.Contains("Binary file b.bin matches", Files.Grep(raw));
    }

    [Fact]
    public void Ls_KeepsAllEntriesUnderCap()
    {
        const string raw = "one.txt\ntwo.txt\nthree.txt\n";
        var got = Files.Ls(raw);
        Assert.Contains("one.txt", got);
        Assert.Contains("two.txt", got);
        Assert.Contains("three.txt", got);
    }
}
