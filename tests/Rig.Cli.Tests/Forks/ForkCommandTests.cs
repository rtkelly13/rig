using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Forks;
using Rig.Core.Forks;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Estate;

namespace Rig.Cli.Tests.Forks;

/// <summary><c>rig fork assess</c> through its command line, against real git repositories.</summary>
public sealed class ForkCommandTests : IDisposable
{
    private const string Mit = "MIT License\n\nCopyright (c) 2020 Jo\n\nPermission is hereby granted, free of charge, to any person obtaining a copy\n";

    private static readonly string ConfigPath = Path.Join(Path.GetDirectoryName(RepoTemp.Root)!, "config", ForkAssessmentConfig.FileName);

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        _temp.Dispose();
        _out.Dispose();
        _err.Dispose();
    }

    private Task<int> Run(string workingDirectory, params string[] args) =>
        new ForkCommand(new Git(Repos.Proc, Path.GetTempPath()), ConfigPath, workingDirectory, _out, _err).Build().Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);

    private async Task<string> RepoAsync(string? licence)
    {
        var repo = await Repos.InitAsync(_temp.Folder("repo"));
        if (licence is not null)
        {
            await File.WriteAllTextAsync(Path.Join(repo, "LICENSE"), licence, TestContext.Current.CancellationToken);
            await Repos.CommitAsync(repo, "add");
        }

        return repo;
    }

    [Fact]
    public async Task A_licensed_project_reports_its_findings_and_exits_clean()
    {
        var repo = await RepoAsync(Mit);

        Assert.Equal(0, await Run(repo, "fork", "assess"));

        var text = _out.ToString();
        Assert.Contains("No blockers", text, StringComparison.Ordinal);
        Assert.Contains("LICENSE: MIT", text, StringComparison.Ordinal);
        Assert.Contains("licence.terms", text, StringComparison.Ordinal);
        Assert.Contains("Responsibilities", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public async Task A_project_without_a_licence_exits_one()
    {
        var repo = await RepoAsync(null);

        Assert.Equal(1, await Run(_temp.Path, "fork", "assess", repo));

        Assert.Contains("Blocked", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("licence.missing", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_output_is_the_assessment_alone()
    {
        var repo = await RepoAsync(Mit);
        var head = await Repos.GitAsync(repo, "rev-parse", "HEAD");

        Assert.Equal(0, await Run(repo, "fork", "assess", "--json", "--rev", "HEAD", "--blame", "--refs", "refs/heads"));

        using var json = JsonDocument.Parse(_out.ToString());
        Assert.Equal(head, json.RootElement.GetProperty("revision").GetString());
        Assert.False(json.RootElement.GetProperty("blocked").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("people").GetProperty("commits").GetInt32());
    }

    [Fact]
    public async Task As_of_reports_a_stale_project_without_reading_the_clock()
    {
        var repo = await RepoAsync(Mit);

        Assert.Equal(0, await Run(repo, "fork", "assess", "--json", "--as-of", "2999-01-01"));

        using var json = JsonDocument.Parse(_out.ToString());
        Assert.Equal("2999-01-01", json.RootElement.GetProperty("as_of").GetString());
        Assert.Contains(json.RootElement.GetProperty("findings").EnumerateArray(), f => f.GetProperty("id").GetString() == "activity.stale");
    }

    [Theory]
    [InlineData("01/02/2026")]
    [InlineData("2000-01-01")]
    public async Task An_as_of_that_is_not_a_date_or_precedes_the_revision_is_an_error(string asOf)
    {
        var repo = await RepoAsync(Mit);

        Assert.Equal(2, await Run(repo, "fork", "assess", "--as-of", asOf));

        Assert.StartsWith("rig fork assess: --as-of", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_clone_is_an_error()
    {
        Assert.Equal(2, await Run(_temp.Folder("empty"), "fork", "assess"));

        Assert.StartsWith("rig fork assess:", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_configuration_is_an_error()
    {
        var repo = await RepoAsync(Mit);

        Assert.Equal(2, await Run(repo, "fork", "assess", "--config", "missing.json"));

        Assert.Contains("does not exist", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_group_alone_asks_for_a_subcommand()
    {
        Assert.NotEqual(0, await Run(_temp.Path, "fork"));
    }
}
