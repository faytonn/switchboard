using System.Text.RegularExpressions;

namespace Switchboard.Hub.Icons;

/// <summary>
/// The icons a session tile may use: exactly the &lt;symbol&gt; ids in wwwroot/icons/lucide.svg.
/// Reading them from the sprite keeps one source of truth: an icon the browser can't draw can't
/// be stored either.
/// </summary>
public sealed partial class IconCatalog
{
    [GeneratedRegex("<symbol id=\"([a-z0-9-]+)\"")]
    private static partial Regex SymbolId();

    public IconCatalog(IWebHostEnvironment env)
    {
        var sprite = Path.Combine(env.WebRootPath, "icons", "lucide.svg");
        Names = SymbolId().Matches(File.ReadAllText(sprite)).Select(m => m.Groups[1].Value).ToList();
        _set = Names.ToHashSet();
    }

    private readonly HashSet<string> _set;

    public IReadOnlyList<string> Names { get; }

    public bool Contains(string name) => _set.Contains(name);
}
