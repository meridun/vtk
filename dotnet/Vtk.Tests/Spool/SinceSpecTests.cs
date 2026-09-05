using Vtk.Core.Spool;
using Xunit;

namespace Vtk.Tests.Spool;

/// <summary>
/// Table-driven parse tests for the `--since` window spec shared by `vtk gaps`
/// and `vtk discover` (#140): durations (`Nd`/`Nh`), ISO-8601 date/datetime
/// forms (bare = UTC, `Z`/offset honored), and the reject set.
/// </summary>
public class SinceSpecTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> AcceptCases()
    {
        // spec, expected UTC bound
        yield return new object[] { "14d", new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc) };
        yield return new object[] { "1d", new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc) };
        yield return new object[] { "36h", new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc) };
        yield return new object[] { " 2h ", new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc) };
        yield return new object[] { "2026-08-01", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) };
        yield return new object[] { "2026-08-01T12:30", new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc) };
        yield return new object[] { "2026-08-01T12:30:15", new DateTime(2026, 8, 1, 12, 30, 15, DateTimeKind.Utc) };
        yield return new object[] { "2026-08-01T12:30:15Z", new DateTime(2026, 8, 1, 12, 30, 15, DateTimeKind.Utc) };
        yield return new object[] { "2026-08-01T12:30:15.250Z", new DateTime(2026, 8, 1, 12, 30, 15, 250, DateTimeKind.Utc) };
        yield return new object[] { "2026-08-01T14:30:15+02:00", new DateTime(2026, 8, 1, 12, 30, 15, DateTimeKind.Utc) };
    }

    [Theory]
    [MemberData(nameof(AcceptCases))]
    public void TryParse_AcceptsDurationsAndIsoDates(string spec, DateTime want)
    {
        Assert.True(SinceSpec.TryParse(spec, Now, out var got));
        Assert.Equal(want, got);
        Assert.Equal(DateTimeKind.Utc, got.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0d")]
    [InlineData("-1d")]
    [InlineData("+1d")]
    [InlineData("d")]
    [InlineData("14")]
    [InlineData("14w")]
    [InlineData("1.5d")]
    [InlineData("abc")]
    [InlineData("2026-13-01")]
    [InlineData("2026-08-01 12:30")]
    [InlineData("08/01/2026")]
    [InlineData("yesterday")]
    public void TryParse_RejectsMalformed(string spec)
    {
        Assert.False(SinceSpec.TryParse(spec, Now, out _));
    }

    [Fact]
    public void Format_RendersUtcSeconds()
    {
        Assert.Equal("2026-08-22T12:00:00Z", SinceSpec.Format(new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void InWindow_NoWindow_KeepsEverythingIncludingUndated()
    {
        Assert.True(SinceSpec.InWindow(default, null));
        Assert.True(SinceSpec.InWindow(Now, null));
    }

    [Fact]
    public void InWindow_WithWindow_KeepsAtOrAfter_DropsBeforeAndUndated()
    {
        Assert.True(SinceSpec.InWindow(Now, Now));
        Assert.True(SinceSpec.InWindow(Now.AddSeconds(1), Now));
        Assert.False(SinceSpec.InWindow(Now.AddSeconds(-1), Now));
        Assert.False(SinceSpec.InWindow(default, Now));
    }
}
