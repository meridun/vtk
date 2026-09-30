using System.Diagnostics;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for the #171 never-filter predicate through the real
/// published vtk binary and real git: `git archive` and `git bundle create`
/// stream their tar/zip/pack bytes to stdout byte-identical to bare git, exit
/// codes match on the success, failure (128), and command-not-found (127)
/// paths, the invocation log carries the distinct `binary` reason (byte
/// counts only, never content), and `vtk gaps` no longer ranks the family.
/// Sibling subcommands (`git bundle verify`, `git cat-file`) stay `no-filter`
/// so the predicate is exactly as narrow as the issue body asks.
/// </summary>
[Collection("Smoke")]
public class BinaryPassthroughSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    /// <summary>
    /// Runs <paramref name="file"/> with stdout captured as raw bytes (a tar
    /// or pack stream is not text; a string round-trip would be lossy) and
    /// stderr as text, under the harness's isolated home so a vtk run logs
    /// to the test spool; <paramref name="env"/> adds or overrides variables.
    /// </summary>
    private (byte[] stdout, string stderr, int code) RunBytes(string dir, string file, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.EnvironmentVariables["LOCALAPPDATA"] = _h.Home;
        psi.EnvironmentVariables["XDG_CACHE_HOME"] = _h.Home;
        psi.EnvironmentVariables["HOME"] = _h.Home;
        if (env != null)
            foreach (var (k, v) in env)
                psi.EnvironmentVariables[k] = v;
        using var proc = Process.Start(psi)!;
        using var buf = new MemoryStream();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.StandardOutput.BaseStream.CopyTo(buf);
        proc.WaitForExit();
        return (buf.ToArray(), stderrTask.Result, proc.ExitCode);
    }

    private (byte[] stdout, string stderr, int code) RawGit(string dir, params string[] args) => RunBytes(dir, "git", null, args);
    private (byte[] stdout, string stderr, int code) Vtk(string dir, params string[] args) => RunBytes(dir, _h.Bin, null, args);

    [Fact]
    public void ArchiveStreamsByteIdenticalWithBinaryReasonAndNoGapRow()
    {
        // tar and zip to stdout: the bytes and exit code are bare git's.
        var rawTar = RawGit(_h.Repo, "archive", "--format=tar", "HEAD");
        Assert.Equal(0, rawTar.code);
        Assert.True(rawTar.stdout.Length > 0, "raw tar stream empty");
        var tar = Vtk(_h.Repo, "git", "archive", "--format=tar", "HEAD");
        Assert.Equal(rawTar.code, tar.code);
        Assert.Equal(rawTar.stdout, tar.stdout);
        Assert.Equal(rawTar.stderr, tar.stderr);

        var rawZip = RawGit(_h.Repo, "archive", "--format=zip", "HEAD");
        var zip = Vtk(_h.Repo, "git", "archive", "--format=zip", "HEAD");
        Assert.Equal(rawZip.code, zip.code);
        Assert.Equal(rawZip.stdout, zip.stdout);

        // Global option ahead of the subcommand (GitOptions.Strip, #152) and
        // the -o <file> form (stdout near-empty) both classify as binary.
        var viaC = Vtk(_h.Other, "git", "-C", _h.Repo, "archive", "--format=tar", "HEAD");
        Assert.Equal(0, viaC.code);
        Assert.Equal(rawTar.stdout, viaC.stdout);
        var outFile = Path.Combine(_h.Other, "out.zip");
        var toFile = Vtk(_h.Other, "git", "-C", _h.Repo, "archive", "-o", outFile, "HEAD");
        Assert.Equal(0, toFile.code);
        Assert.Empty(toFile.stdout);
        Assert.Equal(rawZip.stdout.Length, new FileInfo(outFile).Length);

        var log = _h.InvocationLog();
        Assert.Equal(4, CountOccurrences(log, "\"reason\":\"binary\""));
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        Assert.DoesNotContain("\"filtered\":true", log);
        Assert.Contains($"\"cmd\":\"git archive --format=tar HEAD\",\"raw_bytes\":{rawTar.stdout.Length},\"out_bytes\":{rawTar.stdout.Length}", log);
        // Invariant 3: the row holds byte counts, never the stream content.
        Assert.DoesNotContain("file1.txt", log);
        Assert.DoesNotContain("line 1 content", log);

        // AC: `vtk gaps` no longer lists `git archive`; `vtk gain` counts the
        // rows honestly as raw = emitted, 0 saved.
        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("git archive", SmokeAssert.GapTable(gaps));
        var (gain, _, gainCode) = _h.Run(_h.Repo, "gain");
        Assert.Equal(0, gainCode);
        Assert.Contains("over 4 calls", gain);
        Assert.Contains("saved 0 bytes", gain);
    }

    [Fact]
    public void BundleCreateIsBinaryWhileSiblingSubcommandsStayNoFilter()
    {
        var rawBundle = RawGit(_h.Repo, "bundle", "create", "-", "HEAD");
        Assert.Equal(0, rawBundle.code);
        Assert.True(rawBundle.stdout.Length > 0, "raw bundle stream empty");
        var bundle = Vtk(_h.Repo, "git", "bundle", "create", "-", "HEAD");
        Assert.Equal(rawBundle.code, bundle.code);
        Assert.Equal(rawBundle.stdout, bundle.stdout);

        var bundleFile = Path.Combine(_h.Other, "x.bundle");
        var toFile = Vtk(_h.Repo, "git", "bundle", "create", bundleFile, "HEAD");
        Assert.Equal(0, toFile.code);
        Assert.True(File.Exists(bundleFile), "bundle file not written");

        // Not binary: `bundle verify` and `cat-file` (per the issue body)
        // keep their ordinary no-filter gap rows.
        var rawVerify = RawGit(_h.Repo, "bundle", "verify", bundleFile);
        var verify = Vtk(_h.Repo, "git", "bundle", "verify", bundleFile);
        Assert.Equal(rawVerify.code, verify.code);
        Assert.Equal(rawVerify.stdout, verify.stdout);
        var rawCat = RawGit(_h.Repo, "cat-file", "-p", "HEAD");
        var cat = Vtk(_h.Repo, "git", "cat-file", "-p", "HEAD");
        Assert.Equal(rawCat.code, cat.code);
        Assert.Equal(rawCat.stdout, cat.stdout);

        var log = _h.InvocationLog();
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"binary\""));
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"no-filter\""));

        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        var table = SmokeAssert.GapTable(gaps);
        // The bundle family still ranks — from the verify row only (1 call).
        Assert.Matches(@"(?m)^git bundle\s+1\s", table);
        Assert.Matches(@"(?m)^git cat-file\s+1\s", table);
    }

    [Fact]
    public void ExitCodeParityOnFailureAndCommandNotFoundPaths()
    {
        // Bad ref: git exits 128 with the fatal line; vtk mirrors both.
        var raw = RawGit(_h.Repo, "archive", "nosuchref");
        Assert.Equal(128, raw.code);
        var bad = Vtk(_h.Repo, "git", "archive", "nosuchref");
        Assert.Equal(raw.code, bad.code);
        Assert.Equal(raw.stdout, bad.stdout);
        Assert.Equal(raw.stderr, bad.stderr);

        // No git on PATH: the Passthrough helper's spawn-fail override wins
        // over `binary`, so the row never ranks and the exit is 127.
        var env = new Dictionary<string, string> { ["PATH"] = Path.Combine(_h.Other, "empty-bin") };
        var missing = RunBytes(_h.Repo, _h.Bin, env, "git", "archive", "HEAD");
        Assert.Equal(127, missing.code);
        Assert.Empty(missing.stdout);

        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"git archive nosuchref\"", log);
        Assert.Equal(1, CountOccurrences(log, "\"reason\":\"binary\""));
        Assert.Equal(1, CountOccurrences(log, "\"reason\":\"spawn-fail\""));
        Assert.DoesNotContain("not a valid object name", log);
    }

    [Fact]
    public async Task ConcurrentArchiveInvocationsStayByteIdenticalAndBothLog()
    {
        var raw = RawGit(_h.Repo, "archive", "--format=tar", "HEAD");
        Assert.Equal(0, raw.code);
        var t1 = Task.Run(() => Vtk(_h.Repo, "git", "archive", "--format=tar", "HEAD"));
        var t2 = Task.Run(() => Vtk(_h.Repo, "git", "archive", "--format=tar", "HEAD"));
        var results = await Task.WhenAll(t1, t2);
        foreach (var r in results)
        {
            Assert.Equal(0, r.code);
            Assert.Equal(raw.stdout, r.stdout);
            Assert.DoesNotContain("vtk: gap log failed:", r.stderr);
        }
        // Passthrough never touches the spool; the only shared file is the
        // invocation log, which #155 made append-safe: exactly two rows.
        var log = _h.InvocationLog();
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"binary\""));
    }

    private static int CountOccurrences(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
