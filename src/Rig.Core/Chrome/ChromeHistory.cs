using Microsoft.Data.Sqlite;

namespace Rig.Core.Chrome;

/// <summary>
/// Where a tab's title came from, looked up in the profile's Chrome history. UI Automation shows a tab's title but
/// never its URL, and some sites title their pages with nothing but the page's own name. The profile's <c>History</c>
/// database records the URL of every title it has seen, so a title history maps to exactly one host names that host.
/// Chrome keeps the database open; it is read in place, read-only and immutable, so a lookup can miss the newest
/// visits but never disturbs Chrome. Ported from <c>desktop_manager.chrome.history</c>.
/// </summary>
public static class ChromeHistory
{
    private const int MaxTitlesPerQuery = 500;

    /// <summary>
    /// <paramref name="profileDir"/>'s <c>History</c> under <c>%LOCALAPPDATA%</c>, or null when
    /// <paramref name="environment"/> has no <c>LOCALAPPDATA</c>.
    /// </summary>
    public static string? HistoryPath(Func<string, string?> environment, string profileDir)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment("LOCALAPPDATA") is { Length: > 0 } local
            ? Path.Join(local, "Google", "Chrome", "User Data", profileDir, "History")
            : null;
    }

    /// <summary>
    /// The host each of <paramref name="titles"/> was visited on, for the titles the history at
    /// <paramref name="path"/> ties to a single host. A missing or unreadable history finds nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> HostsByTitle(string? path, IEnumerable<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var wanted = titles.Where(title => title.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
        if (path is null || !File.Exists(path) || wanted.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        Dictionary<string, HashSet<string>> seen = [];
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1",
                    Pooling = false,
                }.ToString());
            connection.Open();
            foreach (var chunk in wanted.Chunk(MaxTitlesPerQuery))
            {
                using var command = connection.CreateCommand();
                var marks = new List<string>();
                for (var index = 0; index < chunk.Length; index++)
                {
                    marks.Add($"$t{index}");
                    command.Parameters.AddWithValue($"$t{index}", chunk[index]);
                }

#pragma warning disable CA2100 // Only the generated parameter names are interpolated.
                command.CommandText = $"select title, url from urls where title in ({string.Join(", ", marks)})";
#pragma warning restore CA2100
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetValue(0) is string title
                        && reader.GetValue(1) is string url
                        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                        && parsed.Host.Length > 0)
                    {
                        if (!seen.TryGetValue(title, out var hosts))
                        {
                            seen[title] = hosts = [];
                        }

                        hosts.Add(parsed.Host.ToLowerInvariant());
                    }
                }
            }
        }
        catch (SqliteException)
        {
            return new Dictionary<string, string>();
        }

        return seen.Where(pair => pair.Value.Count == 1).ToDictionary(pair => pair.Key, pair => pair.Value.Single());
    }
}
