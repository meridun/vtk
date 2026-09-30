namespace Vtk.Tests.Smoke;

/// <summary>
/// #165 failure tail-fold on an uncovered family (registry Q2 option B): a
/// command with no registry entry whose nonzero exit produces output at or
/// above the fold floor is captured, spooled, and emitted as a bounded tail
/// with the final stack trace inline plus `OK &lt;id&gt;`; it stays a
/// `no-filter`-class coverage gap (`failure-fold`, filtered=false). The same
/// output on exit 0 is untouched passthrough. Covered-family shapes live
/// beside their filters in <see cref="FilterSmokeTests"/>.
/// </summary>
[Collection("Smoke")]
public class FailureFoldSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> Code(int n) => new() { ["VTK_FAKE_CRASHTOOL_CODE"] = n.ToString() };

    [Fact]
    public void UncoveredBigFailureFoldsToTailWithStackInlineAndStaysAGap()
    {
        var (raw, rawCode) = _h.RunRawTool(Code(1), "crashtool");
        Assert.Equal(1, rawCode);
        Assert.True(raw.Length >= 64 * 1024, $"fixture below the fold floor: {raw.Length} bytes");

        var (outp, err, code) = _h.RunFaked(_h.Repo, Code(1), "crashtool", "--flush");
        Assert.Equal(rawCode, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length <= 8 * 1024 + 16, $"failure fold not bounded ({outp.Length} bytes); stderr: {err}");
        // The end of the run — where the crash is — survives inline.
        Assert.Contains("AssertionError [ERR_ASSERTION]: batch count drifted during flush", outp);
        Assert.Contains("at Module._compile (node:internal/modules/cjs/loader:1871:14)", outp);
        Assert.DoesNotContain("processed batch 0001/1400", outp);

        // The spool recovers the full raw with provenance.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: crashtool --flush", shown);
        Assert.Contains("processed batch 0001/1400", shown);
        Assert.Contains("1400 !== 1399", shown);

        // Telemetry: failure-fold reason, unfiltered (no filter exists), the
        // spool id, and no output content (invariant 3).
        var log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"failure-fold\"", log);
        Assert.Contains("\"filtered\":false", log);
        Assert.Contains($"\"spool_id\":\"{id}\"", log);
        Assert.DoesNotContain("processed batch", log);
        Assert.DoesNotContain("AssertionError", log);
        // Still a coverage gap: no filter ran.
        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.Contains("crashtool", SmokeAssert.GapTable(gaps));
    }

    [Fact]
    public void UncoveredBigSuccessStaysUntouchedPassthrough()
    {
        var (raw, rawCode) = _h.RunRawTool(Code(0), "crashtool");
        Assert.Equal(0, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, Code(0), "crashtool", "--flush");
        Assert.Equal(rawCode, code); // parity
        SmokeAssert.NoOk(outp + err);
        // Exit 0 never takes the failure fold: byte-identical passthrough,
        // logged as the no-filter gap it always was.
        Assert.Equal(raw, outp + err);
        Assert.Contains("\"reason\":\"no-filter\"", _h.InvocationLog());
    }
}
