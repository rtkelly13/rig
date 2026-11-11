using System.Text.Json.Nodes;
using Rig.Core.Orca;

namespace Rig.Core.Tests.Orca;

/// <summary>Policies, repos and snapshots shaped as the Python tests in <c>tests/test_workspace_orca*.py</c> built them.</summary>
internal static class OrcaFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A home and code root no test creates: path checks only compare strings against them.</summary>
    public static readonly OrcaPaths Code = new("/code", "/code-root", "/home/u");

    /// <summary>The checkout's own <c>config/orca.json</c>.</summary>
    public static string CheckedInPolicy => Path.Join(Path.GetDirectoryName(RepoTemp.Root), OrcaPolicy.RelativePath);

    public static OrcaPolicy PolicyOf(
        IReadOnlyDictionary<string, string>? args = null,
        AgentPermissionsPolicy? permissions = null,
        string? workspaceDir = null,
        AgentSleepPolicy? sleep = null,
        IReadOnlyDictionary<string, RepoPolicy>? repos = null) =>
        new(args ?? new Dictionary<string, string>(), permissions, workspaceDir, sleep, repos ?? new Dictionary<string, RepoPolicy>());

    public static OrcaPolicy Repos(params (string Name, RepoPolicy Policy)[] repos) =>
        PolicyOf(repos: repos.ToDictionary(r => r.Name, r => r.Policy, StringComparer.Ordinal));

    public static OrcaPaths Under(string estateRoot) => new(estateRoot, "/code-root", "/home/u");

    /// <summary>One repo as <c>orca repo list --json</c> returns it.</summary>
    public static OrcaRepo Decode(JsonObject raw)
    {
        var output = new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["repos"] = new JsonArray(raw) } };
        var repos = OrcaRepo.ParseList(output.ToJsonString());
        Assert.NotNull(repos);
        return Assert.Single(repos);
    }

    public static OrcaRepo Repo(
        string name,
        string path,
        string setup = "",
        string archive = "",
        string visibility = "hide",
        string baseRef = "main",
        string color = "#737373",
        OrcaRepoIcon? icon = null) => Decode(new JsonObject
        {
            ["path"] = path,
            ["displayName"] = name,
            ["hookSettings"] = new JsonObject { ["scripts"] = new JsonObject { ["setup"] = setup, ["archive"] = archive } },
            ["externalWorktreeVisibility"] = visibility,
            ["worktreeBaseRef"] = baseRef,
            ["badgeColor"] = color,
            ["repoIcon"] = icon?.ToJson() ?? new JsonObject(),
        });

    /// <summary>A repo at <paramref name="path"/> with <paramref name="hooks"/> as its hook settings, as <c>_hooked_repo</c> made it.</summary>
    public static OrcaRepo Hooked(string path, string hooks, string color = "#6366f1") => Decode(new JsonObject
    {
        ["path"] = path,
        ["displayName"] = "rig",
        ["hookSettings"] = JsonNode.Parse(hooks),
        ["externalWorktreeVisibility"] = "hide",
        ["worktreeBaseRef"] = "main",
        ["badgeColor"] = color,
        ["repoIcon"] = new JsonObject { ["type"] = "lucide", ["name"] = "terminal" },
    });

    public static SettingsCopy Copy(IReadOnlyDictionary<string, string> args, string workspaceDir = "~/orca/workspaces", double ageHours = 1) =>
        new("profile-state.db.backup.1.db", Now - TimeSpan.FromHours(ageHours), new OrcaSettings(args, workspaceDir));

    public static OrcaSnapshot Snapshot(SettingsCopy? settings, IReadOnlyList<OrcaRepo>? repos, IReadOnlyDictionary<string, string>? launch = null) =>
        new(settings, repos, "/profile", launch);

    public static IReadOnlyList<OrcaFinding> Diagnose(OrcaPolicy policy, OrcaSnapshot snapshot, OrcaPaths? paths = null, params string[] managed) =>
        OrcaDoctor.Diagnose(policy, snapshot, paths ?? Code, managed, Now);

    public static void WriteOrcaYaml(string repoPath, string setup)
    {
        Directory.CreateDirectory(repoPath);
        File.WriteAllText(Path.Join(repoPath, OrcaHooks.OrcaYaml), $"scripts:\n  setup: |\n    {setup}\n");
    }
}

/// <summary>An <c>orca</c> CLI that answers from a function and records what it was asked.</summary>
internal sealed class FakeOrcaCli(Func<IReadOnlyList<string>, string?>? answer = null) : IOrcaCli
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        Calls.Add(args);
        return Task.FromResult(answer is null ? throw new InvalidOperationException($"unexpected orca {string.Join(' ', args)}") : answer(args));
    }
}

/// <summary>A runtime socket that records each <c>repo.update</c> and launch-line write.</summary>
internal sealed class FakeOrcaLive(bool ok = true, Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?>? launch = null) : IOrcaLive
{
    public List<(string Path, RepoUpdates Updates)> Updates { get; } = [];

    public List<IReadOnlyDictionary<string, string>> Lines { get; } = [];

    public Task<bool> UpdateRepoAsync(string repoPath, RepoUpdates updates, CancellationToken cancellationToken)
    {
        Updates.Add((repoPath, updates));
        return Task.FromResult(ok);
    }

    public Task<IReadOnlyDictionary<string, string>?> SendLaunchLinesAsync(IReadOnlyDictionary<string, string> lines, CancellationToken cancellationToken)
    {
        Lines.Add(lines);
        return Task.FromResult(launch?.Invoke(lines));
    }
}
