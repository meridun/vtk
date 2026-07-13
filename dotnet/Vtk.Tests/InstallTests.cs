using Vtk.Cli;
using Xunit;

namespace Vtk.Tests;

/// <summary>
/// Parity tests against cmd/vtk/install_test.go. All file I/O happens under
/// a per-test temp dir — never the real .bashrc/pwsh profile.
/// </summary>
public class InstallTests
{
    [Fact]
    public void InstallBlock_IsIdempotent()
    {
        var block = Install.RenderBash("/c/tools/vtk/vtk.exe");

        // Fresh file: block appended, single trailing newline.
        var got = Install.InstallBlockContent("", block);
        Assert.Equal(block + "\n", got);

        // Re-running with the same block is a fixed point.
        var again = Install.InstallBlockContent(got, block);
        Assert.Equal(got, again);

        // Existing unrelated content is preserved above the block, separated
        // by one blank line.
        var withPrior = Install.InstallBlockContent("export FOO=1\n", block);
        Assert.StartsWith("export FOO=1\n\n# >>> vtk wrappers >>>", withPrior);
        Assert.Equal(1, CountOccurrences(withPrior, "# >>> vtk wrappers >>>"));

        // And still idempotent with prior content.
        var again2 = Install.InstallBlockContent(withPrior, block);
        Assert.Equal(withPrior, again2);
    }

    [Fact]
    public void InstallBlock_UpdatesInPlace()
    {
        var oldBlock = Install.RenderBash("/c/old/vtk.exe");
        var newBlock = Install.RenderBash("/c/new/vtk.exe");
        var installed = Install.InstallBlockContent("keep me\n", oldBlock);

        var updated = Install.InstallBlockContent(installed, newBlock);
        Assert.Equal(1, CountOccurrences(updated, "# >>> vtk wrappers >>>"));
        Assert.DoesNotContain("/c/old/vtk.exe", updated);
        Assert.Contains("/c/new/vtk.exe", updated);
        Assert.StartsWith("keep me\n", updated);
    }

    [Fact]
    public void UninstallBlock_RemovesCleanly()
    {
        var block = Install.RenderPwsh(@"C:\tools\vtk\vtk.exe");

        // Block-only file uninstalls to empty.
        var (res, found) = Install.UninstallBlock(Install.InstallBlockContent("", block));
        Assert.True(found);
        Assert.Equal("", res);

        // Surrounding content survives; no doubled blanks left behind.
        var installed = Install.InstallBlockContent("line A\nline B\n", block);
        (res, found) = Install.UninstallBlock(installed);
        Assert.True(found);
        Assert.Equal("line A\nline B\n", res);

        // No block present -> not found, content untouched.
        (res, found) = Install.UninstallBlock("nothing here\n");
        Assert.False(found);
        Assert.Equal("nothing here\n", res);
    }

    [Fact]
    public void WinToMsys_ConvertsWindowsPaths()
    {
        Assert.Equal("/c/Users/me/tools/vtk/vtk.exe", Install.WinToMsys(@"C:\Users\me\tools\vtk\vtk.exe"));
        Assert.Equal("/d/vtk.exe", Install.WinToMsys(@"D:\vtk.exe"));
        Assert.Equal("/already/posix", Install.WinToMsys("/already/posix"));
    }

    [Fact]
    public void RenderPwsh_QuotesPathVerbatim()
    {
        var block = Install.RenderPwsh(@"C:\Program Files\vtk\vtk.exe");
        Assert.Contains(@"'C:\Program Files\vtk\vtk.exe'", block);
        Assert.Contains("$env:CLAUDECODE", block);
    }

    [Fact]
    public void ApplyTarget_WritesAndUninstalls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtk-install-test-" + Path.GetRandomFileName());
        try
        {
            var path = Path.Combine(dir, "nested", ".bashrc"); // parent dir does not exist yet
            var tgt = new Install.ShellTarget("bash", path, Install.RenderBash("/c/tools/vtk/vtk.exe"));

            // Dry-run must not create the file.
            var action = Install.ApplyTarget(tgt, uninstall: false, dryRun: true);
            Assert.Contains("dry-run", action);
            Assert.False(File.Exists(path));

            // Real install creates parent dir + file.
            action = Install.ApplyTarget(tgt, uninstall: false, dryRun: false);
            Assert.Equal("installed", action);
            var data = File.ReadAllText(path);
            Assert.Contains("# >>> vtk wrappers >>>", data);

            // Second install is unchanged (no write needed).
            action = Install.ApplyTarget(tgt, uninstall: false, dryRun: false);
            Assert.Equal("unchanged", action);

            // Uninstall removes the block.
            action = Install.ApplyTarget(tgt, uninstall: true, dryRun: false);
            Assert.Equal("removed", action);
            data = File.ReadAllText(path);
            Assert.DoesNotContain("# >>> vtk wrappers >>>", data);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
