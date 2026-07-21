// Rule registry for `vtk discover` (#44): known-compressible command shapes.
// Each rule names a recurring verbose-output pattern that has no shipped
// filter yet — a candidate for a future filter. Coverage by the real filter
// registry is checked first by the miner, so a shape that later ships a
// filter automatically stops reporting as a candidate (dedupe against
// shipped filters).
using System.Text.RegularExpressions;

namespace Vtk.Core.Discover;

/// <summary>
/// A known-compressible command shape: <paramref name="Match"/> runs against
/// a normalized command segment (argv joined by spaces), like the registry's
/// declarative-TOML match path. <paramref name="Hint"/> is a one-line note on
/// what a filter would compact.
/// </summary>
public sealed record DiscoverRule(string Name, Regex Match, string Hint);

public static class DiscoverRules
{
    private static DiscoverRule Rule(string name, string pattern, string hint) =>
        new(name, new Regex(pattern, RegexOptions.Compiled), hint);

    private static readonly DiscoverRule[] Rules =
    {
        Rule("dotnet-build", @"^dotnet build\b", "MSBuild progress + per-project chatter; keep warnings/errors + summary"),
        Rule("dotnet-test", @"^dotnet test\b", "test-runner progress; keep failures + summary"),
        Rule("go-build", @"^go build\b", "package compile chatter"),
        Rule("go-test", @"^go test\b", "per-package ok lines; keep failures + summary"),
        Rule("npm-install", @"^npm (install|ci)\b", "resolution/progress + audit boilerplate; keep result + vulnerabilities"),
        Rule("pip-install", @"^pip3? install\b", "download/progress bars; keep installed set + errors"),
        Rule("docker-build", @"^docker build\b", "layer progress; keep tags + errors"),
        Rule("terraform-plan", @"^terraform (plan|apply)\b", "refresh chatter; keep plan summary + changes"),
        Rule("make", @"^make\b", "recipe echo; keep warnings/errors + result"),
        Rule("winget-install", @"^winget (install|upgrade)\b", "progress + license spam; keep result"),
        Rule("choco-install", @"^choco install\b", "progress + boilerplate; keep result"),
    };

    /// <summary>The seeded rule set. Extend as gap-log / discover evidence justifies.</summary>
    public static IReadOnlyList<DiscoverRule> Default() => Rules;
}
