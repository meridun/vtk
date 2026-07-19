// `vtk version` / `vtk --version` — print the deployed short SHA so an agent
// (or human) can answer "are we on latest?" in one call (#98). Reserved as
// meta subcommands before exec passthrough, like `show`/`gaps`/`install`: a
// wrapped command literally named "version" is not a real use case, and
// without the reservation vtk would try to exec it (#97 telemetry: 25 such
// attempts). No exec, no spool, no gap log — the wrap path is untouched.
using System.Reflection;

namespace Vtk.Cli;

public static class VersionCmd
{
    public static int Run(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"vtk version: unexpected argument \"{args[0]}\"");
            Console.Error.WriteLine("usage: vtk version");
            return 2;
        }
        Console.Out.WriteLine(Describe(InformationalVersion(), ResolveDeployDir()));
        return 0;
    }

    /// <summary>
    /// Renders the version line from the two SHA sources, in precedence order:
    /// a publish-time stamp (assembly informational version "1.0.0+&lt;sha&gt;",
    /// baked by sdlc-maint.ps1 via -p:SourceRevisionId), then the versioned
    /// release-dir name (vtk-releases\&lt;sha&gt;) for binaries published before
    /// the stamp existed. Neither source → "unknown", still exit 0. Pure
    /// function so the matrix is table-testable without a deployed binary.
    /// </summary>
    internal static string Describe(string? informationalVersion, string? deployDir)
    {
        var plus = informationalVersion?.IndexOf('+') ?? -1;
        if (informationalVersion is not null && plus >= 0 && plus + 1 < informationalVersion.Length)
        {
            return $"vtk {informationalVersion[(plus + 1)..]}";
        }
        if (!string.IsNullOrEmpty(deployDir))
        {
            var full = Path.TrimEndingDirectorySeparator(deployDir);
            var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
            if (string.Equals(parent, "vtk-releases", StringComparison.OrdinalIgnoreCase))
            {
                return $"vtk {Path.GetFileName(full)}";
            }
        }
        return "vtk unknown (unstamped build; not a versioned deploy)";
    }

    /// <summary>
    /// Informational version of the Vtk.Cli assembly itself (not the entry
    /// assembly, which under a test host would be testhost.dll with its own
    /// unrelated "+sha" suffix).
    /// </summary>
    private static string? InformationalVersion() =>
        typeof(VersionCmd).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

    /// <summary>
    /// The directory the binary actually runs from, resolved through the
    /// ~/tools/vtk junction to its vtk-releases\&lt;sha&gt; target when deployed.
    /// Any resolution failure degrades to the unresolved path (→ "unknown").
    /// </summary>
    private static string? ResolveDeployDir()
    {
        try
        {
            var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            return Directory.ResolveLinkTarget(dir, returnFinalTarget: true)?.FullName ?? dir;
        }
        catch
        {
            return null;
        }
    }
}
