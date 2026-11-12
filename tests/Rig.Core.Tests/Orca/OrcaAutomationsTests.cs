using System.Text.Json.Nodes;
using Rig.Core.Orca;
using Rig.Core.Workspace;
using static Rig.Core.Tests.Orca.OrcaFixtures;

namespace Rig.Core.Tests.Orca;

/// <summary><c>config/orca-automations.json</c>, prompt rendering, and reconciling it against a fake <c>orca automations</c> CLI.</summary>
public sealed class OrcaAutomationsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string CheckedInManifest => Path.Join(Path.GetDirectoryName(CheckedInPolicy), AutomationManifest.FileName);

    private static AutomationSpec Spec(string key = "data", string name = "Data review", MachineRole[]? roles = null, bool manage = true) => new(
        key, name, roles ?? [MachineRole.Personal], manage, "personal", "FREQ=WEEKLY;BYDAY=MO;BYHOUR=2;BYMINUTE=0", "Europe/London", 720, "codex", false,
        "C:/code/personal/rig", "docs/b.md", "docs/data.md", "https://example.test/issues/1", true);

    private static JsonObject Live(string id, string name, string prompt = "Run it.", string agent = "codex", bool enabled = true) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["prompt"] = prompt,
        ["agentId"] = agent,
        ["rrule"] = "FREQ=WEEKLY;BYDAY=MO;BYHOUR=2;BYMINUTE=0",
        ["timezone"] = "Europe/London",
        ["missedRunGraceMinutes"] = 720,
        ["enabled"] = enabled,
        ["reuseSession"] = false,
        ["workspaceMode"] = "existing",
        ["runContext"] = new JsonObject { ["path"] = "C:\\code\\personal\\rig\\" },
    };

    private static string List(params JsonObject[] automations) =>
        new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["automations"] = new JsonArray(automations) } }.ToJsonString();

    private static FakeOrcaCli Cli(string list) => new(args => args[1] switch
    {
        "list" => list,
        "create" => """{"ok": true, "result": {"automation": {"id": "new-id"}}}""",
        "edit" => """{"ok": true}""",
        _ => null,
    });

    private static Task<IReadOnlyList<AutomationChange>> Reconcile(FakeOrcaCli cli, MachineRole role, bool apply, params AutomationSpec[] specs) =>
        new AutomationReconciler(_ => cli).ReconcileAsync([.. specs.Select(s => (s, "Run it."))], role, apply, Token);

    [Fact]
    public void The_checked_in_manifest_decodes_and_every_prompt_renders()
    {
        var specs = AutomationManifest.Load(CheckedInManifest);
        var checkout = Path.GetDirectoryName(Path.GetDirectoryName(CheckedInManifest))!;

        Assert.Equal(["daily-estate", "data", "diffra", "tooling-design", "personal"], specs.Select(s => s.Key));
        Assert.All(specs.Where(s => s.Key != "daily-estate"), s => Assert.Equal([MachineRole.Personal], s.Roles));
        Assert.All(specs, s =>
        {
            var prompt = AutomationManifest.Prompt(s, checkout);
            Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Join(checkout, s.PolicyPath)), $"{s.Key}: {s.PolicyPath} is missing");
        });
        var data = AutomationManifest.Prompt(specs.Single(s => s.Key == "data"), checkout);
        Assert.Contains("git show <sha>:docs/project-reviews/data.md", data, StringComparison.Ordinal);
        Assert.Contains("Schedule key: data", data, StringComparison.Ordinal);
        Assert.Contains("https://github.com/rtkelly13/rig/issues/22", data, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_review_policy_names_the_key_and_issue_its_manifest_entry_does()
    {
        var checkout = Path.GetDirectoryName(Path.GetDirectoryName(CheckedInManifest))!;
        Assert.All(AutomationManifest.Load(CheckedInManifest).Where(s => s.MemoryIssue is not null), s =>
        {
            var policy = File.ReadAllText(Path.Join(checkout, s.PolicyPath));
            Assert.Contains($"Schedule key: `{s.Key}`", policy, StringComparison.Ordinal);
            Assert.Contains($"Canonical memory: {s.MemoryIssue}", policy, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_prompt_is_the_bootstrap_s_text_block_with_placeholders_filled()
    {
        File.WriteAllText(_dir.File("docs/b.md"), "# Bootstrap\r\n\r\n```text\r\nRun {{key}} ({{name}}) from {{policy_path}}; write to {{memory_issue}}.\r\n```\r\nAfter.\r\n");

        Assert.Equal("Run data (Data review) from docs/data.md; write to https://example.test/issues/1.", AutomationManifest.Prompt(Spec(), _dir.Path));
    }

    [Theory]
    [InlineData("```text\nRun {{nope}}.\n```\n", "unknown placeholder {{nope}}")]
    [InlineData("No block.\n", "found 0")]
    [InlineData("```text\na\n```\n```text\nb\n```\n", "found 2")]
    public void A_bootstrap_without_one_valid_block_is_refused(string doc, string message)
    {
        File.WriteAllText(_dir.File("docs/b.md"), doc);

        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => AutomationManifest.Prompt(Spec(), _dir.Path)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"schema_version": 1, "automations": []}""", "schema_version")]
    [InlineData("""{"schema_version": 2, "automations": [{"key": "a"}]}""", "a.orca")]
    [InlineData(
        """{"schema_version": 2, "automations": [{"key": "a", "name": "A", "roles": ["laptop"], "orca": {"instance": "p"}, "trigger": {"rrule": "r", "timezone": "z"}, "provider": {"agent": "codex"}, "workspace": {"path": "p"}, "bootstrap": "b", "policy_path": "p"}]}""",
        "a.roles")]
    [InlineData(
        """{"schema_version": 2, "automations": [{"key": "a", "name": "A", "roles": ["work"], "orca": {"instance": "p"}, "trigger": {"rrule": "r", "timezone": "z"}, "provider": {"agent": "codex"}, "workspace": {"mode": "new-per-run", "path": "p"}, "bootstrap": "b", "policy_path": "p"}]}""",
        "a.workspace.mode")]
    public void A_malformed_manifest_names_what_is_wrong(string json, string field) =>
        Assert.Contains(field, Assert.Throws<InvalidDataException>(() => AutomationManifest.Parse(json)).Message, StringComparison.Ordinal);

    [Fact]
    public void A_key_declared_twice_is_refused()
    {
        const string Entry = """{"key": "a", "name": "A", "roles": ["work"], "orca": {"instance": "p"}, "trigger": {"rrule": "r", "timezone": "z"}, "provider": {"agent": "codex"}, "workspace": {"path": "p"}, "bootstrap": "b", "policy_path": "p"}""";

        Assert.Contains("declared twice", Assert.Throws<InvalidDataException>(() => AutomationManifest.Parse($$"""{"schema_version": 2, "automations": [{{Entry}}, {{Entry}}]}""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Automations_for_another_role_are_skipped_without_asking_Orca()
    {
        var cli = new FakeOrcaCli();

        var changes = await Reconcile(cli, MachineRole.Work, apply: true, Spec());

        Assert.Equal(("skip", "for the personal role; this machine is work"), (changes.Single().Action, changes.Single().Detail));
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task A_matching_automation_is_ok_ignoring_line_endings_and_path_separators()
    {
        var cli = Cli(List(Live("a1", "Data review", prompt: "Run it.\r\n")));

        var change = (await Reconcile(cli, MachineRole.Personal, apply: true, Spec())).Single();

        Assert.Equal(("ok", "a1"), (change.Action, change.Id));
        Assert.Single(cli.Calls);
    }

    [Fact]
    public async Task A_missing_automation_is_planned_then_created_with_every_setting()
    {
        var cli = Cli(List());

        Assert.Equal("create", (await Reconcile(cli, MachineRole.Personal, apply: false, Spec())).Single().Action);
        Assert.Single(cli.Calls);

        var change = (await Reconcile(cli, MachineRole.Personal, apply: true, Spec())).Single();

        Assert.Equal(("create", true, "new-id"), (change.Action, change.Applied, change.Id));
        Assert.Equal(
            [
                "automations", "create", "--name", "Data review", "--prompt", "Run it.", "--provider", "codex",
                "--trigger", "FREQ=WEEKLY;BYDAY=MO;BYHOUR=2;BYMINUTE=0", "--timezone", "Europe/London", "--missed-run-grace-minutes", "720",
                "--fresh-session", "--workspace", "path:C:/code/personal/rig", "--enabled", "--json",
            ],
            cli.Calls[^1]);
    }

    [Fact]
    public async Task A_drifted_automation_is_edited_in_only_the_fields_that_differ()
    {
        var cli = Cli(List(Live("a1", "Data review", prompt: "Old prompt.", agent: "claude", enabled: false)));

        var change = (await Reconcile(cli, MachineRole.Personal, apply: true, Spec())).Single();

        Assert.Equal(("update", true), (change.Action, change.Applied));
        Assert.Equal(["prompt", "agent", "enabled"], change.Fields);
        Assert.Equal(["automations", "edit", "--id", "a1", "--prompt", "Run it.", "--provider", "codex", "--enabled", "--json"], cli.Calls[^1]);
    }

    [Fact]
    public async Task An_adopted_automation_reports_drift_and_is_never_changed()
    {
        var cli = Cli(List(Live("a1", "Data review", prompt: "Old prompt.")));

        var change = (await Reconcile(cli, MachineRole.Personal, apply: true, Spec(manage: false))).Single();

        Assert.Equal(("drift", false), (change.Action, change.Applied));
        Assert.Equal(["prompt"], change.Fields);
        Assert.Single(cli.Calls);
    }

    [Fact]
    public async Task Two_automations_sharing_a_name_are_an_error_and_others_are_listed_as_unmanaged()
    {
        var cli = Cli(List(Live("a1", "Data review"), Live("a2", "Data review"), Live("x", "Something by hand")));

        var changes = await Reconcile(cli, MachineRole.Personal, apply: true, Spec());

        Assert.Equal([("error", "Data review"), ("unmanaged", "Something by hand")], changes.Select(c => (c.Action, c.Name)));
        Assert.Single(cli.Calls);
    }

    [Fact]
    public async Task An_instance_that_does_not_answer_errors_each_of_its_automations()
    {
        var cli = new FakeOrcaCli(_ => null);

        var changes = await Reconcile(cli, MachineRole.Personal, apply: true, Spec(), Spec("diffra", "Diffra review"));

        Assert.All(changes, c => Assert.Equal("error", c.Action));
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task Each_instance_is_asked_through_its_own_CLI()
    {
        var personal = Cli(List());
        var work = Cli(List());
        var reconciler = new AutomationReconciler(name => name == "work" ? work : personal);

        _ = await reconciler.ReconcileAsync(
            [(Spec(roles: [MachineRole.Personal, MachineRole.Work]), "p"), (Spec("w", "Work job", [MachineRole.Work]) with { Instance = "work" }, "p")],
            MachineRole.Work,
            apply: false,
            Token);

        Assert.Single(personal.Calls);
        Assert.Single(work.Calls);
    }
}
