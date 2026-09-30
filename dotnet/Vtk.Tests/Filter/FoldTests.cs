using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// The shared size-floored fold (#93 mechanism, extended to `powershell
/// -File` by #131) against a fixture captured from a real
/// `powershell -ExecutionPolicy Bypass -File &lt;script&gt;` run — CRLF-real
/// bytes, the run-e2e-local.ps1 output shape (server log lines ending in a
/// summary block) — and, for the #134 pinned-summary extension, a real
/// mocha run whose summary is followed by leftover console noise.
/// </summary>
public class FoldTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "powershell");

    private static readonly string NpmFixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "npm");

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

    // ---- Pinned summary lines (#134, option B) ------------------------------

    [Fact]
    public void Tail_RealMochaWithTrailingNoise_PinsSummaryAboveTail()
    {
        // Captured from a real `mocha` run (non-TTY, no --exit) whose after
        // hook leaves timers that log after the summary — the IsekaiOnline
        // `npm run test:unit` shape from #134. Positionally the summary sits
        // six lines from the end, outside the 5-line tail.
        var raw = File.ReadAllText(Path.Combine(NpmFixtureDir, "test_unit_noise_real.raw.txt"));
        var want = File.ReadAllText(Path.Combine(NpmFixtureDir, "test_unit_noise_real.want.txt"));
        Assert.True(raw.Length >= Fold.FloorBytes,
            $"fixture below the fold floor: {raw.Length} bytes");

        var tail = Fold.Tail(raw);
        Assert.Equal(want, tail);
        Assert.StartsWith("  1200 passing", tail);
        Assert.DoesNotContain("validates the incoming payload", tail);

        Assert.True(tail.Length <= Fold.TailMaxBytes);
        var saved = raw.Length - tail.Length;
        Assert.True(saved >= raw.Length * 0.99,
            $"fold saved only {saved} of {raw.Length} bytes");
    }

    public static TheoryData<string, string, string> PinCases => new()
    {
        {
            "mocha summary above five noise lines",
            "  ✔ a\n  ✔ b\n\n  7 passing (5ms)\n\nn1\nn2\nn3\nn4\nn5\n",
            "  7 passing (5ms)\nn1\nn2\nn3\nn4\nn5"
        },
        {
            "mocha passing + failing + pending all pinned",
            "tree\n  7 passing (5ms)\n  2 pending\n  1 failing\n\nn1\nn2\nn3\nn4\nn5\n",
            "  7 passing (5ms)\n  2 pending\n  1 failing\nn1\nn2\nn3\nn4\nn5"
        },
        {
            "jest Tests: line",
            "PASS src/a.test.js\nTests:       1 failed, 5 passed, 6 total\nn1\nn2\nn3\nn4\nn5\n",
            "Tests:       1 failed, 5 passed, 6 total\nn1\nn2\nn3\nn4\nn5"
        },
        {
            "eslint problems line",
            "/src/a.js\n  1:1  warning  x\n\n✖ 2 problems (0 errors, 2 warnings)\n\nn1\nn2\nn3\nn4\nn5\n",
            "✖ 2 problems (0 errors, 2 warnings)\nn1\nn2\nn3\nn4\nn5"
        },
        {
            // #168: the no-banner `npm test` fold of a node --test run —
            // the 8-line summary block sits above the 5-line positional
            // tail, so `tests`/`pass`/`fail` pin (three nearest the tail;
            // suites/cancelled/skipped/todo/duration_ms do not qualify).
            "node --test summary block: tests/pass/fail pinned above the tail",
            "▶ s\n  ✔ a (1ms)\n✔ s (2ms)\nℹ tests 10\nℹ suites 4\nℹ pass 8\nℹ fail 0\nℹ cancelled 0\nℹ skipped 1\nℹ todo 1\nℹ duration_ms 81.3\n",
            "ℹ tests 10\nℹ pass 8\nℹ fail 0\nℹ cancelled 0\nℹ skipped 1\nℹ todo 1\nℹ duration_ms 81.3"
        },
        {
            "node --test TAP summary lines pin too",
            "TAP version 13\nok 1 - a\n1..1\n# tests 1\n# suites 0\n# pass 1\n# fail 0\nn1\nn2\nn3\nn4\nn5\n",
            "# tests 1\n# pass 1\n# fail 0\nn1\nn2\nn3\nn4\nn5"
        },
        {
            "bare tests/pass/fail prose without a prefix is not a summary",
            "tests 3\npass 2\nfail 1\nn1\nn2\nn3\nn4\nn5\n",
            "n1\nn2\nn3\nn4\nn5"
        },
        {
            "summary already inside the positional tail is not moved or duplicated",
            "tree\nn1\n  7 passing (5ms)\nn2\nn3\n",
            "tree\nn1\n  7 passing (5ms)\nn2\nn3"
        },
        {
            "no summary line: positional tail unchanged",
            "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\n",
            "line 4\nline 5\nline 6\nline 7\nline 8"
        },
        {
            "more than three candidates: the three nearest the tail",
            "  1 passing\n  2 passing\n  3 passing\n  4 passing\n  5 passing\nn1\nn2\nn3\nn4\nn5\n",
            "  3 passing\n  4 passing\n  5 passing\nn1\nn2\nn3\nn4\nn5"
        },
        {
            "prose that mentions the words is not a summary",
            "tests passing now\nproblems: none\nTests: none passed yet\nn1\nn2\nn3\nn4\nn5\n",
            "n1\nn2\nn3\nn4\nn5"
        },
        {
            // #163: the real `mocha --color` summary line. Matched on the
            // ANSI-stripped text, emitted verbatim with its codes.
            "colored mocha summary pins and is emitted verbatim",
            "tree\n\x1b[92m \x1b[0m\x1b[32m 7 passing\x1b[0m\x1b[90m (3ms)\x1b[0m\n\nn1\nn2\nn3\nn4\nn5\n",
            "\x1b[92m \x1b[0m\x1b[32m 7 passing\x1b[0m\x1b[90m (3ms)\x1b[0m\nn1\nn2\nn3\nn4\nn5"
        },
        {
            "CRLF body keeps the CR on the pinned line",
            "tree\r\n  7 passing (5ms)\r\n\r\nn1\r\nn2\r\nn3\r\nn4\r\nn5\r\n",
            "  7 passing (5ms)\r\nn1\r\nn2\r\nn3\r\nn4\r\nn5\r"
        },
    };

    [Theory]
    [MemberData(nameof(PinCases))]
    public void Tail_PinsSummaryLines(string name, string body, string want)
    {
        Assert.Equal(want, Fold.Tail(body));
        Assert.True(Fold.Tail(body).Length <= Fold.TailMaxBytes, name);
    }

    [Fact]
    public void Tail_PinnedBytesCountInsideTheCap()
    {
        // Five 100-char noise lines fill the positional budget (504 chars);
        // the pinned summary takes priority and the positional tail shrinks
        // so the whole result stays under TailMaxBytes.
        var noise = new string('n', 100);
        var body = "  7 passing (5ms)\n" + string.Join("\n", Enumerable.Repeat(noise, 5)) + "\n";
        var tail = Fold.Tail(body);
        Assert.StartsWith("  7 passing (5ms)\n", tail);
        Assert.True(tail.Length <= Fold.TailMaxBytes, $"tail is {tail.Length} chars");
        Assert.Equal("  7 passing (5ms)\n" + string.Join("\n", Enumerable.Repeat(noise, 4)), tail);
    }

    [Fact]
    public void Tail_OversizedSummaryLine_IsDroppedNotTruncated()
    {
        var huge = "  7 passing " + new string('x', Fold.TailMaxBytes);
        var body = huge + "\nn1\nn2\nn3\nn4\nn5\n";
        Assert.Equal("n1\nn2\nn3\nn4\nn5", Fold.Tail(body));
    }
}
