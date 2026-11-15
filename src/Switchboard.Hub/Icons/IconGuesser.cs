using System.Text.RegularExpressions;

namespace Switchboard.Hub.Icons;

/// <summary>
/// Picks a tile icon for a session nobody chose one for, from the project folder's name and the
/// start of its README. Words in the folder name count three times as much as README words,
/// because "kafka-pipeline" says more about a project than a README that mentions "tests".
/// No match: null, and the UI shows initials.
/// </summary>
public sealed partial class IconGuesser(IconCatalog catalog, ILogger<IconGuesser> log)
{
    private const int FolderWeight = 3;
    private const int ReadmeBytes = 4000;

    // icon -> keywords. Keywords of 4+ letters also match words that start with them
    // ("reserv" matches "reservation"); shorter ones must match a whole word ("ai", "db").
    // Earlier rows win ties.
    private static readonly (string Icon, string[] Keywords)[] Table =
    [
        ("ticket", ["reserv", "booking", "bookit", "ticket", "cinema", "concert", "theater", "theatre", "event"]),
        ("calendar-days", ["calendar", "schedul", "appointment", "agenda"]),
        ("workflow", ["kafka", "pipeline", "stream", "etl", "queue", "rabbitmq", "broker", "ingest", "airflow"]),
        // "notes", not "note": a 4+ letter keyword matches as a prefix, and "note" would also
        // match "notification".
        ("notebook-pen", ["notes", "journal", "diary", "obsidian", "vault", "notebook"]),
        ("brain", ["brain", "knowledge", "zettel"]),
        ("list-todo", ["todo", "task", "tasks", "checklist"]),
        ("kanban", ["kanban", "sprint", "jira", "trello"]),
        ("bot", ["bot", "agent", "llm", "ai", "gpt", "claude", "chatbot", "assistant", "mux"]),
        ("waypoints", ["switchboard", "orchestr", "router", "routing"]),
        ("database", ["db", "database", "postgres", "sql", "sqlite", "mongo", "redis", "migration"]),
        ("server", ["api", "backend", "server", "service", "gateway", "microservice"]),
        ("container", ["docker", "container", "k8s", "kubernetes", "helm"]),
        ("cloud", ["cloud", "aws", "azure", "gcp", "terraform", "infra", "devops", "deploy"]),
        ("globe", ["web", "website", "site", "landing", "frontend", "html", "portfolio"]),
        ("smartphone", ["mobile", "ios", "android", "flutter", "swift", "kotlin", "pwa"]),
        ("gamepad-2", ["game", "roblox", "unity", "godot", "rpg"]),
        ("graduation-cap", ["study", "course", "school", "exam", "university", "czu", "learn", "lesson", "homework"]),
        ("briefcase", ["job", "career", "cv", "resume", "hiring", "interview"]),
        ("book-open", ["doc", "docs", "documentation", "manual", "guide", "wiki"]),
        ("flask-conical", ["test", "tests", "qa", "e2e"]),
        ("square-terminal", ["cli", "terminal", "shell", "script", "scripts", "dotfiles"]),
        ("chart-line", ["analytics", "dashboard", "metrics", "chart", "stats", "report", "telemetry"]),
        ("shopping-cart", ["shop", "store", "ecommerce", "cart", "checkout"]),
        ("wallet", ["pay", "payment", "bank", "finance", "budget", "money", "wallet", "invoice"]),
        ("mail", ["mail", "email", "newsletter", "smtp"]),
        ("messages-square", ["chat", "message", "messages", "slack", "discord", "telegram"]),
        ("shield-check", ["authentic", "authoriz", "login", "security", "sso", "oauth"]), // not "auth": "author"
        ("music", ["music", "audio", "sound", "spotify"]),
        ("image", ["photo", "image", "gallery"]),
        ("video", ["video", "youtube", "shorts", "film"]),
        ("newspaper", ["blog", "news"]),
        ("dumbbell", ["gym", "fitness", "workout", "training"]),
        ("heart-pulse", ["health", "medical", "clinic"]),
        ("utensils", ["food", "recipe", "restaurant", "meal"]),
        ("plane", ["travel", "trip", "flight"]),
        ("map", ["map", "geo", "location", "gis"]),
        ("microscope", ["research", "science", "lab"]),
        ("palette", ["design", "figma", "ui", "css", "theme"]),
        ("bug", ["debug", "bugs", "issue"]),
        ("package", ["package", "library", "sdk", "nuget", "npm"]),
        ("search", ["search", "index", "crawler"]),
        ("lightbulb", ["idea", "ideas", "brainstorm"]),
    ];

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();

    /// <summary>Checks the table against the sprite once, so a renamed icon is noticed at startup.</summary>
    public void Validate()
    {
        foreach (var (icon, _) in Table.Where(row => !catalog.Contains(row.Icon)))
        {
            log.LogWarning("IconGuesser uses '{Icon}', which isn't in the icon sprite", icon);
        }
    }

    public string? Guess(string projectDir)
    {
        var scores = new Dictionary<string, int>();
        Score(Path.GetFileName(projectDir.TrimEnd('/')), FolderWeight, scores);
        Score(ReadReadme(projectDir), 1, scores);

        // Highest score wins; on a tie, the row that comes first in the table.
        return Table
            .Where(row => scores.ContainsKey(row.Icon) && catalog.Contains(row.Icon))
            .OrderByDescending(row => scores[row.Icon])
            .Select(row => row.Icon)
            .FirstOrDefault();
    }

    private static void Score(string text, int weight, Dictionary<string, int> scores)
    {
        // "BookIt" and "book_it" both become words; camelCase is split first ("BookIt" -> "book it")
        // but the joined form is kept too, so the keyword "bookit" still matches.
        var spaced = CamelBoundary().Replace(text, "$1 $2");
        var words = Word().Matches((text + " " + spaced).ToLowerInvariant()).Select(m => m.Value).ToList();
        foreach (var (icon, keywords) in Table)
        {
            var hits = words.Count(w => keywords.Any(k => k.Length >= 4 ? w.StartsWith(k, StringComparison.Ordinal) : w == k));
            if (hits > 0)
            {
                scores[icon] = scores.GetValueOrDefault(icon) + hits * weight;
            }
        }
    }

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelBoundary();

    private static string ReadReadme(string projectDir)
    {
        foreach (var name in new[] { "README.md", "readme.md", "README", "README.txt", "Readme.md" })
        {
            var path = Path.Combine(projectDir, name);
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var stream = File.OpenRead(path);
                var buffer = new byte[ReadmeBytes];
                var read = stream.Read(buffer, 0, buffer.Length);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (IOException)
            {
                return "";
            }
            catch (UnauthorizedAccessException)
            {
                return "";
            }
        }

        return "";
    }
}
