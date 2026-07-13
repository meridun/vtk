using Xunit;

namespace Vtk.Tests;

/// <summary>
/// Table-driven tests for the spool/`OK` savings gate (#52): a compact result
/// must clear both an absolute-byte floor and a savings ratio (option C) before
/// vtk spools the raw and emits the recover-me signal.
/// </summary>
public class SavingsGateTests
{
    public static IEnumerable<object[]> Cases()
    {
        // rawLen, shownLen, expectedClears, description
        yield return new object[] { 2836, 2820, false, "git-branch newline-only reformat: 16B saved, below floor" };
        yield return new object[] { 1000, 900, false, "100B saved (below 256 floor) even though 10% ratio" };
        yield return new object[] { 50000, 49700, false, "300B saved clears floor but 0.6% ratio below 20%" };
        yield return new object[] { 1000, 700, true, "300B and 30%: clears both" };
        yield return new object[] { 1000, 744, true, "exactly 256B and exactly 25.6%: clears floor, above ratio" };
        yield return new object[] { 1280, 1024, true, "exactly 256B and exactly 20%: both boundaries met" };
        yield return new object[] { 1300, 1044, false, "exactly 256B but 19.7% ratio: floor met, ratio just under" };
        yield return new object[] { 0, 0, false, "empty raw: no savings, no divide-by-zero" };
        yield return new object[] { 100, 200, false, "compact larger than raw: negative savings" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClearsSavingsBar_MatchesThreshold(int rawLen, int shownLen, bool expected, string because)
    {
        Assert.Equal(expected, Vtk.Cli.Program.ClearsSavingsBar(rawLen, shownLen));
        _ = because;
    }

    [Fact]
    public void Thresholds_AreOptionCDefaults()
    {
        Assert.Equal(256, Vtk.Cli.Program.MinSavingsBytes);
        Assert.Equal(0.20, Vtk.Cli.Program.MinSavingsRatio);
    }
}
