using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Rig.Cli.Flags;
using Rig.Core.Flags;
using Rig.Core.Tests;

namespace Rig.Cli.Tests.Flags;

/// <summary>
/// <c>rig flags</c> replayed against <c>flags-oracle.json</c>, recorded from Python's <c>rig.flags.cli</c> on a fresh
/// <c>rig.db</c>: the same declared flags, the same table and the same messages and exit codes, step by step. The
/// Python test <c>test_the_csharp_oracle_records_this_registry</c> holds Python's own registry to the same file while
/// <c>desktop_manager</c> still reads it.
/// </summary>
public sealed partial class FlagsOracleTests : IDisposable
{
    private static readonly JsonElement Oracle = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Flags", "flags-oracle.json"))).RootElement;

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();

    private string Db => Path.Join(_temp.Path, "rig.db");

    public void Dispose()
    {
        _out.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void The_declared_flags_are_the_recorded_ones_in_order()
    {
        var recorded = Oracle.GetProperty("flags").EnumerateArray()
            .Select(flag => new Flag(
                flag.GetProperty("name").GetString()!,
                flag.GetProperty("default").GetBoolean(),
                flag.GetProperty("description").GetString()!));

        Assert.Equal(recorded, FeatureFlags.All);
    }

    [Fact]
    public void The_table_is_the_one_python_migrates()
    {
        new FlagStore(Db).States();

        Assert.Equal(Normalise(Oracle.GetProperty("schema").GetString()!), Normalise(TableSql()));
    }

    [Fact]
    public async Task Every_recorded_step_prints_and_exits_as_python_did()
    {
        foreach (var step in Oracle.GetProperty("steps").EnumerateArray())
        {
            if (step.TryGetProperty("note", out _))
            {
                InsertUndeclaredRow();
            }

            string[] args = [.. step.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()!)];
            _out.GetStringBuilder().Clear();
            var exit = await Run(args);

            Assert.Equal(step.GetProperty("exit").GetInt32(), exit);
            if (step.TryGetProperty("rows", out var rows))
            {
                Assert.Equal(
                    rows.EnumerateArray().Select(row => string.Join(" | ", row.EnumerateArray().Select(cell => cell.GetString()))),
                    TableRows());
            }
            else
            {
                Assert.Equal(step.GetProperty("output").GetString(), _out.ToString());
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Columns();

    private static string Normalise(string sql) =>
        Spaces().Replace(sql.Replace("IF NOT EXISTS ", string.Empty, StringComparison.Ordinal), " ").Replace("( ", "(", StringComparison.Ordinal).Replace(" )", ")", StringComparison.Ordinal).Trim();

    private IEnumerable<string> TableRows()
    {
        var lines = _out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["Flag", "Effective", "Default", "Override", "Description"], Columns().Split(lines[0]));
        return lines.Skip(1).Select(line => string.Join(" | ", Columns().Split(line)));
    }

    private string TableSql()
    {
        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'feature_flags'";
        return (string)command.ExecuteScalar()!;
    }

    private void InsertUndeclaredRow()
    {
        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO feature_flags (name, enabled, updated_at) VALUES ('retired-flag', 1, '2026-01-01T00:00:00+00:00')";
        command.ExecuteNonQuery();
    }

    private Task<int> Run(params string[] args) =>
        new FlagsCommand(new FlagStore(Db), _out).Build().Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = _out, Error = _out }, TestContext.Current.CancellationToken);
}
