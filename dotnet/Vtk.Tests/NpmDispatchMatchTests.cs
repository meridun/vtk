using Xunit;

namespace Vtk.Tests;

/// <summary>
/// Table-driven tests for the npm dispatch predicate (#120): `npm run
/// &lt;script&gt;` and the lifecycle aliases (`npm test`/`t`/`tst`,
/// `start`/`stop`/`restart`) route through the dispatch layer; every other
/// npm subcommand — and bare `npm run`/`npm` — falls through to normal
/// gap-logged passthrough.
/// </summary>
public class NpmDispatchMatchTests
{
    public static IEnumerable<object[]> Cases()
    {
        // argv, expected, description
        yield return new object[] { new[] { "npm", "run", "test" }, true, "canonical npm run" };
        yield return new object[] { new[] { "npm", "run", "lint", "--", "--fix" }, true, "npm run with script args" };
        yield return new object[] { new[] { "npm", "run" }, false, "bare npm run (script listing)" };
        yield return new object[] { new[] { "npm", "test" }, true, "test lifecycle alias (#120, the 299MB shape)" };
        yield return new object[] { new[] { "npm", "test", "--", "--grep", "cart" }, true, "test alias with forwarded args" };
        yield return new object[] { new[] { "npm", "t" }, true, "t shorthand" };
        yield return new object[] { new[] { "npm", "tst" }, true, "tst shorthand" };
        yield return new object[] { new[] { "npm", "start" }, true, "start lifecycle alias" };
        yield return new object[] { new[] { "npm", "stop" }, true, "stop lifecycle alias" };
        yield return new object[] { new[] { "npm", "restart" }, true, "restart lifecycle alias" };
        yield return new object[] { new[] { "npm", "install" }, false, "install is not a script runner" };
        yield return new object[] { new[] { "npm", "ci" }, false, "ci is not a script runner" };
        yield return new object[] { new[] { "npm", "ls" }, false, "ls is not a script runner" };
        yield return new object[] { new[] { "npm", "audit" }, false, "audit is not a script runner" };
        yield return new object[] { new[] { "npm" }, false, "bare npm" };
        yield return new object[] { new[] { "npx", "test" }, false, "not npm at all" };
        yield return new object[] { System.Array.Empty<string>(), false, "empty argv" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsNpmScript_MatchesDispatchShapes(string[] argv, bool expected, string because)
    {
        Assert.Equal(expected, Vtk.Cli.Program.IsNpmScript(argv));
        _ = because;
    }
}
