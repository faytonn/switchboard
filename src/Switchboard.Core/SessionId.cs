using System.Text.RegularExpressions;

namespace Switchboard.Core;

/// <summary>
/// A session id is the short name of a session ("manager", "api-fix"). It is also the tmux
/// window name and, from phase 2, the primary key in Postgres.
/// </summary>
public static partial class SessionId
{
    // Lowercase letters, digits and hyphens only. This keeps ids safe inside tmux targets,
    // where ':' separates session from window and '.' separates window from pane.
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string id) => Pattern().IsMatch(id);

    public static void Validate(string id)
    {
        if (!IsValid(id))
        {
            throw new ArgumentException(
                $"Invalid session id '{id}'. Use 1-32 lowercase letters, digits or hyphens, starting with a letter or digit.");
        }
    }
}
