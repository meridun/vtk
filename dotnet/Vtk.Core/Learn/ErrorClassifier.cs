// Classifies a failed command's output into a coarse error type (#43,
// rtk learn parity). Heuristic by design: patterns are conservative and
// ordered most-specific first; anything unmatched is Other.
using System.Text.RegularExpressions;

namespace Vtk.Core.Learn;

/// <summary>Coarse cause of a failed CLI invocation (rtk learn's ErrorType set).</summary>
public enum ErrorType
{
    UnknownFlag,
    CommandNotFound,
    WrongSyntax,
    WrongPath,
    MissingArg,
    PermissionDenied,
    Other,
}

public static class ErrorClassifier
{
    private static readonly (Regex Re, ErrorType Type)[] Table =
    {
        (new Regex(@"(?im)command not found|not recognized as (an internal or external command|the name of a cmdlet)|executable file not found|'[^']+' is not a \S+ command", RegexOptions.Compiled), ErrorType.CommandNotFound),
        (new Regex(@"(?im)unknown (option|flag|shorthand flag)|unrecognized option|invalid option|unexpected argument '?--", RegexOptions.Compiled), ErrorType.UnknownFlag),
        (new Regex(@"(?im)requires a value|requires an argument|missing (required )?(argument|operand)|(option|flag|argument) .* (is )?required", RegexOptions.Compiled), ErrorType.MissingArg),
        (new Regex(@"(?im)permission denied|access is denied|\bEACCES\b", RegexOptions.Compiled), ErrorType.PermissionDenied),
        (new Regex(@"(?im)no such file or directory|cannot find (the )?(path|file)|does not exist|\bENOENT\b", RegexOptions.Compiled), ErrorType.WrongPath),
        (new Regex(@"(?im)syntax error|^usage:|unexpected token|bad revision", RegexOptions.Compiled), ErrorType.WrongSyntax),
    };

    /// <summary>Maps a failed command's output to the first matching error type; Other when nothing matches.</summary>
    public static ErrorType Classify(string output)
    {
        foreach (var (re, type) in Table)
        {
            if (re.IsMatch(output)) return type;
        }
        return ErrorType.Other;
    }

    /// <summary>Kebab-case display name for a type (rules-file rendering).</summary>
    public static string Name(ErrorType t) => t switch
    {
        ErrorType.UnknownFlag => "unknown-flag",
        ErrorType.CommandNotFound => "command-not-found",
        ErrorType.WrongSyntax => "wrong-syntax",
        ErrorType.WrongPath => "wrong-path",
        ErrorType.MissingArg => "missing-arg",
        ErrorType.PermissionDenied => "permission-denied",
        _ => "other",
    };
}
