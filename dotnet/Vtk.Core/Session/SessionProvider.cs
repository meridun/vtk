// Locates Claude Code session transcripts on disk. Analysis tooling is
// agent-aware by decision #43; the wrap path never touches this code.
namespace Vtk.Core.Session;

public static class SessionProvider
{
    /// <summary>
    /// The Claude Code projects root: <c>~/.claude/projects</c>. Home is
    /// %USERPROFILE% on Windows, $HOME elsewhere — same env-first resolution
    /// as the spool store, so tests can redirect it.
    /// </summary>
    public static string ProjectsRoot()
    {
        var home = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("USERPROFILE")
                ?? throw new InvalidOperationException("%USERPROFILE% is not defined")
            : Environment.GetEnvironmentVariable("HOME")
                ?? throw new InvalidOperationException("$HOME is not defined");
        return Path.Combine(home, ".claude", "projects");
    }

    /// <summary>
    /// Maps a project path to its Claude Code session-directory name: every
    /// character outside [A-Za-z0-9] becomes '-' (e.g. <c>C:\Claude\vtk</c>
    /// → <c>C--Claude-vtk</c>).
    /// </summary>
    public static string MungeProjectPath(string projectPath)
    {
        var chars = projectPath.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i])) chars[i] = '-';
        }
        return new string(chars);
    }

    /// <summary>The session directory for a project path, under the projects root.</summary>
    public static string SessionDirFor(string projectPath) =>
        Path.Combine(ProjectsRoot(), MungeProjectPath(projectPath));

    /// <summary>
    /// The session transcript files (*.jsonl) in a directory, oldest first
    /// (last-write time, then name for determinism). Missing directory →
    /// empty list — callers decide whether that is an error.
    /// <paramref name="modifiedSince"/> (UTC, #140) drops files whose
    /// last-write time precedes it — a transcript not touched since the
    /// window opened cannot hold events inside it.
    /// </summary>
    public static List<string> SessionFiles(string dir, DateTime? modifiedSince = null)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFiles(dir, "*.jsonl")
            .Select(p => (Path: p, Time: File.GetLastWriteTimeUtc(p)))
            .Where(f => modifiedSince is not { } s || f.Time >= s)
            .OrderBy(f => f.Time)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => f.Path)
            .ToList();
    }
}
