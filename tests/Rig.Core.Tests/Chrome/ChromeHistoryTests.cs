using Microsoft.Data.Sqlite;
using Rig.Core.Chrome;

namespace Rig.Core.Tests.Chrome;

/// <summary>History lookups against a throwaway copy of Chrome's <c>urls</c> table. Replaces <c>test_chrome_history</c>.</summary>
public sealed class ChromeHistoryTests : IDisposable
{
    private readonly TempDir _temp = new();

    private Func<string, string?> Environment => name => name == "LOCALAPPDATA" ? _temp.Path : null;

    public void Dispose() => _temp.Dispose();

    private string History(params (string? Url, string? Title)[] rows)
    {
        var path = ChromeHistory.HistoryPath(Environment, "Profile 4")!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "create table urls (id integer primary key, url text, title text)";
            create.ExecuteNonQuery();
            foreach (var (url, title) in rows)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "insert into urls (url, title) values ($url, $title)";
                insert.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
                insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }

        return path;
    }

    [Fact]
    public void A_title_seen_on_one_host_maps_to_it()
    {
        var path = History(
            ("https://claude.ai/artifact/1", "Linking Triage"),
            ("https://claude.ai/artifact/1?v=2", "Linking Triage"),
            ("https://meet.google.com/abc", "Standup"),
            ("https://calendar.google.com/x", "Standup"),
            ("not a url", "Notes"),
            (null, "Notes"),
            ("https://example.com/", null));

        Assert.Equal(
            new Dictionary<string, string> { ["Linking Triage"] = "claude.ai" },
            ChromeHistory.HostsByTitle(path, ["Linking Triage", "Standup", "Never seen", "Notes", "", "Linking Triage"]));
    }

    [Fact]
    public void Titles_are_looked_up_in_chunks()
    {
        var path = History(("https://claude.ai/a", "Title 0"), ("https://github.com/b", "Title 700"));

        var hosts = ChromeHistory.HostsByTitle(path, Enumerable.Range(0, 1200).Select(index => $"Title {index}"));

        Assert.Equal(new Dictionary<string, string> { ["Title 0"] = "claude.ai", ["Title 700"] = "github.com" }, hosts);
    }

    [Fact]
    public void A_missing_or_unreadable_history_finds_nothing()
    {
        var path = ChromeHistory.HistoryPath(Environment, "Profile 4")!;
        Assert.Empty(ChromeHistory.HostsByTitle(path, ["Linking Triage"]));
        Assert.Empty(ChromeHistory.HostsByTitle(null, ["Linking Triage"]));
        Assert.Empty(ChromeHistory.HostsByTitle(History(), []));

        File.WriteAllText(path, "not a database, but long enough that sqlite reads a header from it and rejects it");
        Assert.Empty(ChromeHistory.HostsByTitle(path, ["Linking Triage"]));
    }

    [Fact]
    public void Without_local_app_data_there_is_no_history()
    {
        Assert.Null(ChromeHistory.HistoryPath(_ => null, "Profile 4"));
        Assert.Null(ChromeHistory.HistoryPath(_ => "", "Profile 4"));
        Assert.EndsWith(Path.Join("Google", "Chrome", "User Data", "Default", "History"), ChromeHistory.HistoryPath(Environment, "Default"), StringComparison.Ordinal);
    }
}
