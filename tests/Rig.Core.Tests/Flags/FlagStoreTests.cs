using Microsoft.Data.Sqlite;
using Rig.Core.Flags;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Flags;

/// <summary><see cref="FlagStore"/> against a <c>rig.db</c> in a temp folder.</summary>
public sealed class FlagStoreTests : IDisposable
{
    private readonly TempDir _temp = new();

    private string Db => Path.Join(_temp.Path, "nested", "rig.db");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Flag_names_are_unique()
    {
        Assert.Equal(FeatureFlags.All.Count, FeatureFlags.All.Select(flag => flag.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_fresh_database_gives_every_default_and_creates_its_folder()
    {
        var store = new FlagStore(Db);

        Assert.Equal(FeatureFlags.All.ToDictionary(flag => flag.Name, flag => flag.Default), store.Effective());
        Assert.All(store.States(), state => Assert.Null(state.Override));
        Assert.True(File.Exists(Db));
    }

    [Fact]
    public void An_override_wins_until_it_is_reset()
    {
        var store = new FlagStore(Db);

        store.Set("claude-session-restore", true);
        Assert.True(store.IsEnabled("claude-session-restore"));
        store.Set("claude-session-restore", false);
        Assert.False(store.IsEnabled("claude-session-restore"));

        Assert.True(store.Clear("claude-session-restore"));
        Assert.False(store.Clear("claude-session-restore"));
        Assert.False(store.IsEnabled("claude-session-restore"));
    }

    [Fact]
    public void An_override_is_stamped_in_python_isoformat_utc()
    {
        new FlagStore(Db, new ManualClock(new DateTimeOffset(2026, 11, 12, 9, 30, 5, 123, TimeSpan.Zero))).Set("work-fork", true);

        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled, updated_at FROM feature_flags WHERE name = 'work-fork'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("2026-11-12T09:30:05.123000+00:00", reader.GetString(1));
    }

    [Fact]
    public void An_undeclared_name_is_refused_with_the_declared_ones_listed()
    {
        var store = new FlagStore(Db);

        Assert.Throws<UnknownFlagException>(() => store.Set("no-such-flag", true));
        Assert.Throws<UnknownFlagException>(() => store.Clear("no-such-flag"));
        var error = Assert.Throws<UnknownFlagException>(() => store.IsEnabled("no-such-flag"));
        Assert.StartsWith("Unknown flag: no-such-flag. Declared flags: claude-session-restore, ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_error_keeps_the_usual_constructors()
    {
        var inner = new InvalidOperationException("cause");

        Assert.NotNull(new UnknownFlagException().Message);
        Assert.Same(inner, new UnknownFlagException("m", inner).InnerException);
    }
}
