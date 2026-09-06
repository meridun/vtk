using System.Text.RegularExpressions;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Real-run smoke for #137: `vtk show` logs a metadata-only row, the
/// fold→show join surfaces as the recovered-folds section of `vtk gaps`,
/// and `vtk gain` reports net beside gross. Through the real binary with
/// the fake eslint (exit 1 problems report) as the fold source.
/// </summary>
[Collection("Smoke")]
public class ShowTelemetrySmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> Code(int n) => new() { ["VTK_FAKE_ESLINT_CODE"] = n.ToString() };

    private static readonly Regex OutBytesRe = new(@"""out_bytes"":(\d+)", RegexOptions.Compiled);
    private static readonly Regex RawBytesRe = new(@"""raw_bytes"":(\d+)", RegexOptions.Compiled);

    private List<string> ShowRows() =>
        _h.InvocationLog().Split('\n').Where(l => l.Contains("\"reason\":\"show\"")).ToList();

    private static long Field(Regex re, string row) => long.Parse(re.Match(row).Groups[1].Value);

    [Fact]
    public void ShowLogsMetadataRowAndJoinsToItsFold()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, Code(1), "eslint", "src/");
        Assert.Equal(1, code);
        var id = SmokeHarness.MustOkId(outp);

        // Fold row carries the spool id it emitted; no show row yet.
        var log = _h.InvocationLog();
        Assert.Contains($"\"spool_id\":\"{id}\"", log);
        Assert.Empty(ShowRows());

        // Plain show: same output as before (provenance header first), exit 0,
        // one new row with reason=show, grep=false, byte counts only.
        var (shown, showErr, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Equal("", showErr);
        Assert.StartsWith("# vtk spool", shown);
        Assert.Contains("'foo' is not defined", shown);
        var plain = Assert.Single(ShowRows());
        Assert.Contains($"\"cmd\":\"show {id}\"", plain);
        Assert.Contains($"\"spool_id\":\"{id}\"", plain);
        Assert.Contains("\"grep\":false", plain);
        Assert.Contains("\"filtered\":false", plain);
        var plainRaw = Field(RawBytesRe, plain);
        var plainOut = Field(OutBytesRe, plain);
        Assert.True(plainRaw > 0);
        Assert.True(plainOut >= plainRaw, $"plain show emitted {plainOut} < spool size {plainRaw}");
        Assert.DoesNotContain("is not defined", _h.InvocationLog()); // invariant 3

        // Grep'd show: grep=true, pattern text never written, fewer bytes.
        var (grepped, _, grepCode) = _h.Run(_h.Repo, "show", id, "--grep", "no-undef-zz|is not defined");
        Assert.Equal(0, grepCode);
        Assert.Contains("'foo' is not defined", grepped);
        var rows = ShowRows();
        Assert.Equal(2, rows.Count);
        Assert.Contains("\"grep\":true", rows[1]);
        Assert.DoesNotContain("no-undef-zz", _h.InvocationLog());
        var grepOut = Field(OutBytesRe, rows[1]);
        Assert.True(grepOut > 0 && grepOut < plainOut, $"grep show emitted {grepOut}, plain {plainOut}");
        var recoveredBytes = plainOut + grepOut;

        // gaps: the recovered-folds section names the eslint fold as
        // recovered once (two shows, one fold) with both shows' bytes;
        // show rows are not gap entries.
        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.Contains("RECOVERED FOLDS", gaps);
        var line = Assert.Single(gaps.Split('\n'), l => l.StartsWith("eslint "));
        var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "eslint", "1", "1", "100.0%", recoveredBytes.ToString() }, cols);
        Assert.DoesNotContain("show", SmokeAssert.GapTable(gaps));
        Assert.DoesNotContain("no-undef-zz", gaps);

        // gain: gross unchanged by the shows; net = gross - recovered bytes;
        // no "show" family.
        var (gain, _, gainCode) = _h.Run(_h.Repo, "gain");
        Assert.Equal(0, gainCode);
        Assert.Contains("over 1 calls", gain);
        Assert.Contains($"after {recoveredBytes} bytes recovered via vtk show (1 folds)", gain);
        Assert.Contains("NET", gain);
        var famLine = Assert.Single(gain.Split('\n'), l => l.StartsWith("eslint "));
        var famCols = famLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var saved = long.Parse(famCols[3]);
        Assert.Equal((saved - recoveredBytes).ToString(), famCols[5]);
        Assert.DoesNotContain("\nshow ", gain);

        // A --since window in the future hides the fold (windowed on the fold row).
        var (future, _, futureCode) = _h.Run(_h.Repo, "gaps", "--since", "2099-01-01");
        Assert.Equal(0, futureCode);
        Assert.DoesNotContain("RECOVERED FOLDS", future);
    }

    [Fact]
    public void ShowOfUnknownIdLogsNothingAndKeepsExitCode()
    {
        var (_, err, code) = _h.Run(_h.Repo, "show", "dead");
        Assert.Equal(1, code);
        Assert.Contains("no spool entry", err);
        Assert.False(File.Exists(Path.Combine(_h.Home, "vtk", "invocations.jsonl")));
    }
}
