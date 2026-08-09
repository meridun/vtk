using Vtk.Core.Discover;
using Vtk.Core.Spool;

namespace Vtk.Tests.Discover;

/// <summary>
/// Tests for the wrapped-invocation cross-reference (#115): (argv, timestamp
/// window) matching against invocation-log entries, quote/redaction
/// normalization, one-to-one consumption, and the conservative no-timestamp
/// path.
/// </summary>
public class WrappedInvocationsTests
{
    private static readonly DateTime T0 = new(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);

    private static Invocation Inv(string cmd, DateTime time) => new() { Cmd = cmd, Time = time };

    private static string[] Argv(string cmd) =>
        cmd.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void TryConsume_MatchingCmdAndTime_Matches()
    {
        var w = new WrappedInvocations(new[] { Inv("gh issue list --label bug", T0) });

        Assert.True(w.TryConsume(Argv("gh issue list --label bug"), T0.AddSeconds(2)));
        Assert.Equal(1, w.Matched);
    }

    public static IEnumerable<object[]> NoMatchCases()
    {
        // logged cmd, logged time offset (s), event cmd, event time offset (s)
        yield return new object[] { "gh issue list", 0, "gh issue view 7", 0 };      // different argv
        yield return new object[] { "git status", 0, "git status", 600 };            // outside ±5 min
        yield return new object[] { "git status", 600, "git status", 0 };            // outside, other side
    }

    [Theory]
    [MemberData(nameof(NoMatchCases))]
    public void TryConsume_NoMatch_ReturnsFalse(string logged, int logOfs, string ev, int evOfs)
    {
        var w = new WrappedInvocations(new[] { Inv(logged, T0.AddSeconds(logOfs)) });

        Assert.False(w.TryConsume(Argv(ev), T0.AddSeconds(evOfs)));
        Assert.Equal(0, w.Matched);
    }

    [Fact]
    public void TryConsume_NullTimestamp_NeverMatches()
    {
        // Conservative: an event without a result timestamp stays an
        // opportunity (pre-#115 behavior) rather than guess-matching.
        var w = new WrappedInvocations(new[] { Inv("git status", T0) });

        Assert.False(w.TryConsume(Argv("git status"), null));
    }

    [Fact]
    public void TryConsume_ConsumesOneToOne()
    {
        // One log entry satisfies exactly one event: counts subtract, a
        // single wrapped call never suppresses a whole family.
        var w = new WrappedInvocations(new[] { Inv("git status", T0) });

        Assert.True(w.TryConsume(Argv("git status"), T0.AddSeconds(1)));
        Assert.False(w.TryConsume(Argv("git status"), T0.AddSeconds(2)));
        Assert.Equal(1, w.Matched);
    }

    [Fact]
    public void TryConsume_RepeatedCommand_ConsumesNearestEntry()
    {
        var w = new WrappedInvocations(new[]
        {
            Inv("git status", T0),
            Inv("git status", T0.AddMinutes(30)),
        });

        // Each event consumes the log entry nearest its own time.
        Assert.True(w.TryConsume(Argv("git status"), T0.AddMinutes(30).AddSeconds(1)));
        Assert.True(w.TryConsume(Argv("git status"), T0.AddSeconds(1)));
        Assert.False(w.TryConsume(Argv("git status"), T0.AddSeconds(3)));
        Assert.Equal(2, w.Matched);
    }

    [Fact]
    public void TryConsume_QuotedTranscriptArgs_MatchArgvJoinedLog()
    {
        // The transcript keeps shell quoting (`-m "msg with spaces"`); the
        // log's cmd is an argv join without quotes. Normalization bridges it.
        var w = new WrappedInvocations(new[] { Inv("git commit -m msg with spaces", T0) });

        Assert.True(w.TryConsume(Argv("git commit -m \"msg with spaces\""), T0.AddSeconds(1)));
    }

    [Fact]
    public void TryConsume_RedactedLogEntry_MatchesRawTranscript()
    {
        // The log redacted the credential at write time; the transcript side
        // is redacted by the same pass before comparison.
        var w = new WrappedInvocations(new[] { Inv("curl -H authorization: [REDACTED]", T0) });

        Assert.True(w.TryConsume(Argv("curl -H authorization: Bearer hunter2"), T0.AddSeconds(1)));
    }

    [Theory]
    [InlineData("git status", "git status")]
    [InlineData("git  status \t", "git status")]
    [InlineData("git commit -m \"a b\"", "git commit -m a b")]
    [InlineData("echo 'x y'", "echo x y")]
    [InlineData("", "")]
    public void Normalize_CanonicalizesQuotesAndWhitespace(string cmd, string expected)
    {
        Assert.Equal(expected, WrappedInvocations.Normalize(cmd));
    }
}
