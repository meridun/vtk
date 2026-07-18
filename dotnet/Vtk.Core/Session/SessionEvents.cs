// Shared session-analysis types for the Claude Code session provider
// (decision #43/#44: `learn` and `discover` share one provider).
namespace Vtk.Core.Session;

/// <summary>
/// One executed Bash command mined from a session transcript: the command
/// text, the tool result the agent saw, and whether the tool reported an
/// error. Events are ordered by result arrival within a session.
/// </summary>
public sealed record CommandEvent(string Command, string Output, bool IsError);
