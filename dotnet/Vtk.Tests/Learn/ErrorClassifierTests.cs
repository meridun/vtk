using Vtk.Core.Learn;

namespace Vtk.Tests.Learn;

/// <summary>
/// Table-driven classification of failed-command output into rtk learn's
/// ErrorType set (#43). Real tool error strings, one per branch of the
/// pattern table plus the Other fallback.
/// </summary>
public class ErrorClassifierTests
{
    public static IEnumerable<object[]> Cases()
    {
        // output, expected
        yield return new object[] { "bash: gti: command not found", ErrorType.CommandNotFound };
        yield return new object[] { "git: 'satus' is not a git command. See 'git --help'.", ErrorType.CommandNotFound };
        yield return new object[] { "The term 'gti' is not recognized as the name of a cmdlet, function, script file, or operable program.", ErrorType.CommandNotFound };
        yield return new object[] { "'foo' is not recognized as an internal or external command,\noperable program or batch file.", ErrorType.CommandNotFound };
        yield return new object[] { "npm error unknown option '--ext'", ErrorType.UnknownFlag };
        yield return new object[] { "error: unrecognized option `--frce'", ErrorType.UnknownFlag };
        yield return new object[] { "unknown shorthand flag: 'z' in -z", ErrorType.UnknownFlag };
        yield return new object[] { "error: option '--out' requires a value", ErrorType.MissingArg };
        yield return new object[] { "rm: missing operand", ErrorType.MissingArg };
        yield return new object[] { "bash: /etc/shadow: Permission denied", ErrorType.PermissionDenied };
        yield return new object[] { "Access is denied.", ErrorType.PermissionDenied };
        yield return new object[] { "cat: /tmp/nope.txt: No such file or directory", ErrorType.WrongPath };
        yield return new object[] { "ERROR: Cannot find path 'C:\\missing' because it does not exist.", ErrorType.WrongPath };
        yield return new object[] { "bash: -c: line 1: syntax error near unexpected token `)'", ErrorType.WrongSyntax };
        yield return new object[] { "usage: git commit [-a | --interactive | --patch] ...", ErrorType.WrongSyntax };
        yield return new object[] { "fatal: bad revision 'HEAD~99'", ErrorType.WrongSyntax };
        yield return new object[] { "something failed for reasons unknown", ErrorType.Other };
        yield return new object[] { "", ErrorType.Other };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify_MapsKnownPatterns(string output, ErrorType expected)
    {
        Assert.Equal(expected, ErrorClassifier.Classify(output));
    }

    [Fact]
    public void Name_CoversEveryType()
    {
        foreach (var t in Enum.GetValues<ErrorType>())
        {
            Assert.Matches("^[a-z-]+$", ErrorClassifier.Name(t));
        }
    }
}
