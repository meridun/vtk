using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// The shared size-floored fold (#93 mechanism, extended to `powershell
/// -File` by #131) against a fixture captured from a real
/// `powershell -ExecutionPolicy Bypass -File &lt;script&gt;` run — CRLF-real
/// bytes, the run-e2e-local.ps1 output shape (server log lines ending in a
/// summary block).
/// </summary>
public class FoldTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "powershell");

    [Fact]
    public void Tail_RealPsFileCapture_MatchesGolden()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "ps_file_real.raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, "ps_file_real.want.txt"));

        // The capture is the fold shape: a successful run at or above the floor.
        Assert.True(raw.Length >= Fold.FloorBytes,
            $"fixture below the fold floor: {raw.Length} bytes");

        var tail = Fold.Tail(raw);
        Assert.Equal(want, tail);

        // The summary block survives inline; the bulk folds away. CRLF line
        // endings pass through byte-exact (vtk never rewrites output bytes).
        Assert.Contains("42 passed (312.4s)", tail);
        Assert.Contains("\r\n", tail);
        Assert.DoesNotContain("request 0001", tail);

        // Measured savings: the tail is capped, so folding this capture
        // elides well beyond the #52 bar (256 bytes / 20%).
        Assert.True(tail.Length <= Fold.TailMaxBytes);
        var saved = raw.Length - tail.Length;
        Assert.True(saved >= raw.Length * 0.99,
            $"fold saved only {saved} of {raw.Length} bytes");
    }

    [Fact]
    public void NpmAliases_DelegateToSharedFold()
    {
        // #131 extracted the #93 mechanism into Fold; npm's public surface
        // must stay bound to the shared values.
        Assert.Equal(Fold.FloorBytes, Npm.FoldFloorBytes);
        Assert.Equal(Fold.TailMaxLines, Npm.FoldTailMaxLines);
        Assert.Equal(Fold.TailMaxBytes, Npm.FoldTailMaxBytes);
        Assert.Equal(Fold.Tail("a\nb\nc\n"), Npm.FoldTail("a\nb\nc\n"));
    }
}
