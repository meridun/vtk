// Shared session-analysis types for the Claude Code session provider
// (decision #43/#44: `learn` and `discover` share one provider).
namespace Vtk.Core.Session;

/// <summary>
/// One executed Bash command mined from a session transcript: the command
/// text, the tool result the agent saw, whether the tool reported an error,
/// and the UTC timestamp of the transcript line carrying the result (null
/// when that line has none). Events are ordered by result arrival within a
/// session. The timestamp backs the discover wrapped-invocation
/// cross-reference (#115); only the time is read — never more transcript
/// metadata than the analysis needs.
/// </summary>
public sealed record CommandEvent(
    string Command, string Output, bool IsError, DateTime? Timestamp = null);
