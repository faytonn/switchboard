namespace Switchboard.Core;

/// <summary>
/// Sessions run inside WSL, but the owner often copies folder paths from Windows.
/// This turns "C:\Users\me\proj" into "/mnt/c/Users/me/proj" and leaves Linux paths alone.
/// </summary>
public static class WslPath
{
    public static string ToLinux(string path)
    {
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            var rest = path[2..].Replace('\\', '/').TrimStart('/');
            return $"/mnt/{char.ToLowerInvariant(path[0])}/{rest}";
        }

        return path;
    }
}
