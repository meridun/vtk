using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests;

/// <summary>
/// Table-driven tests for the `powershell -File` dispatch predicate (#131):
/// `powershell`/`pwsh` invocations with an explicit `-File &lt;script&gt;` route
/// through the size-floored fold; `-Command`/`-EncodedCommand` forms,
/// positional-script spellings, and every other command fall through to
/// normal gap-logged passthrough.
/// </summary>
public class PowershellDispatchMatchTests
{
    public static IEnumerable<object[]> Cases()
    {
        // argv, expected, description
        yield return new object[] { new[] { "powershell", "-ExecutionPolicy", "Bypass", "-File", "scripts/run-e2e-local.ps1" }, true, "the measured #131 shape" };
        yield return new object[] { new[] { "powershell", "-File", "build.ps1" }, true, "bare -File" };
        yield return new object[] { new[] { "powershell", "-File", "build.ps1", "-Config", "Release" }, true, "-File with script args" };
        yield return new object[] { new[] { "pwsh", "-File", "x.ps1" }, true, "pwsh spelling" };
        yield return new object[] { new[] { "pwsh", "-NoProfile", "-File", "x.ps1" }, true, "pwsh with host flag" };
        yield return new object[] { new[] { "powershell.exe", "-File", "x.ps1" }, true, ".exe suffix" };
        yield return new object[] { new[] { "PowerShell", "-file", "x.ps1" }, true, "case-insensitive host and parameter" };
        yield return new object[] { new[] { "powershell", "-File" }, false, "-File without a script" };
        yield return new object[] { new[] { "powershell", "-Command", "Get-Date" }, false, "-Command form" };
        yield return new object[] { new[] { "powershell", "-Command", "foo", "-File", "x.ps1" }, false, "-File inside command text" };
        yield return new object[] { new[] { "pwsh", "-EncodedCommand", "ZQBjAGgAbwA=" }, false, "-EncodedCommand form" };
        yield return new object[] { new[] { "powershell", "x.ps1", "extra" }, false, "positional script (no explicit -File)" };
        yield return new object[] { new[] { "powershell" }, false, "bare powershell (interactive shape)" };
        yield return new object[] { new[] { "cmd", "/c", "-File", "x.ps1" }, false, "not a powershell host" };
        yield return new object[] { new[] { "powershellx", "-File", "x.ps1" }, false, "host name is not a prefix match" };
        yield return new object[] { System.Array.Empty<string>(), false, "empty argv" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_DispatchShapes(string[] argv, bool expected, string because)
    {
        Assert.Equal(expected, PowershellFile.Matches(argv));
        _ = because;
    }
}
