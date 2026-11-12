using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Workspace;

namespace Rig.Core.Orca;

/// <summary>One scheduled Orca automation as <c>config/orca-automations.json</c> declares it.</summary>
/// <param name="Key">The schedule key its policy names.</param>
/// <param name="Name">The automation's name in Orca; also how an unbound one is found.</param>
/// <param name="Roles">The machine roles it runs on; anywhere else it is left alone.</param>
/// <param name="Manage">Whether rig changes it (<c>manage</c>) or only reports drift (<c>adopt</c>).</param>
/// <param name="Instance">
/// The Orca instance it lives in, by name (<c>personal</c>, <c>work</c>); each machine maps the name to a user-data folder in
/// <c>tools.orca.instances</c>, and an unmapped name is the default instance.
/// </param>
/// <param name="Rrule">When it runs.</param>
/// <param name="Timezone">The IANA zone the rule is read in.</param>
/// <param name="GraceMinutes">How late a missed run may still start.</param>
/// <param name="Agent">The Orca agent id, such as <c>claude</c> or <c>codex</c>.</param>
/// <param name="ReuseSession">Whether a run reuses the previous live session.</param>
/// <param name="WorkspacePath">The existing checkout it runs in; <c>{personal}</c> is this machine's personal estate folder.</param>
/// <param name="Bootstrap">The doc, relative to the checkout, whose <c>text</c> block is the prompt.</param>
/// <param name="PolicyPath">The policy the prompt points at.</param>
/// <param name="MemoryIssue">The issue the run writes to, or null.</param>
/// <param name="Enabled">Whether it is switched on.</param>
public sealed record AutomationSpec(
    string Key,
    string Name,
    IReadOnlyList<MachineRole> Roles,
    bool Manage,
    string Instance,
    string Rrule,
    string Timezone,
    int GraceMinutes,
    string Agent,
    bool ReuseSession,
    string WorkspacePath,
    string Bootstrap,
    string PolicyPath,
    string? MemoryIssue,
    bool Enabled);

/// <summary>Reads <c>config/orca-automations.json</c> and renders each automation's prompt.</summary>
public static partial class AutomationManifest
{
    /// <summary>The manifest's file name under a checkout's <c>config</c> folder.</summary>
    public const string FileName = "orca-automations.json";

    /// <summary>The schema version this code reads.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Reads the manifest at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">It is malformed.</exception>
    public static IReadOnlyList<AutomationSpec> Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Decodes a manifest document.</summary>
    /// <exception cref="InvalidDataException">It is malformed, has a duplicate key or name, or is another schema version.</exception>
    public static IReadOnlyList<AutomationSpec> Parse(string text)
    {
        var root = OrcaJson.Object(text);
        if (OrcaJson.Integer(root, "schema_version") != SchemaVersion)
        {
            throw new InvalidDataException($"schema_version: expected {SchemaVersion}");
        }

        var specs = new List<AutomationSpec>();
        foreach (var node in OrcaJson.Array(root, "automations") ?? [])
        {
            specs.Add(Spec(node as JsonObject ?? throw new InvalidDataException("automations: expected objects")));
        }

        if (specs.GroupBy(s => s.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } key)
        {
            throw new InvalidDataException($"automations: key \"{key.Key}\" is declared twice");
        }

        if (specs.GroupBy(s => (s.Instance, s.Name)).FirstOrDefault(g => g.Count() > 1) is { } name)
        {
            throw new InvalidDataException($"automations: \"{name.Key.Name}\" is declared twice in one Orca instance");
        }

        return specs;
    }

    /// <summary>
    /// The prompt for <paramref name="spec"/>: the single <c>```text</c> block of its bootstrap doc under
    /// <paramref name="checkout"/>, with <c>{{key}}</c>, <c>{{name}}</c>, <c>{{policy_path}}</c> and <c>{{memory_issue}}</c> filled in.
    /// </summary>
    /// <exception cref="InvalidDataException">The doc has no single text block, or the block names an unknown placeholder.</exception>
    public static string Prompt(AutomationSpec spec, string checkout)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var doc = File.ReadAllText(Path.Join(checkout, spec.Bootstrap)).ReplaceLineEndings("\n");
        var blocks = TextBlock().Matches(doc);
        if (blocks.Count != 1)
        {
            throw new InvalidDataException($"{spec.Bootstrap}: expected one ```text block, found {blocks.Count}");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key"] = spec.Key,
            ["name"] = spec.Name,
            ["policy_path"] = spec.PolicyPath,
            ["memory_issue"] = spec.MemoryIssue ?? "(none)",
        };
        return Placeholder().Replace(blocks[0].Groups["body"].Value, m =>
            values.TryGetValue(m.Groups["name"].Value, out var value)
                ? value
                : throw new InvalidDataException($"{spec.Bootstrap}: unknown placeholder {m.Value}")).Trim();
    }

    private static AutomationSpec Spec(JsonObject entry)
    {
        var key = Required(entry, "key");
        var orca = OrcaJson.Object(entry, "orca") ?? throw new InvalidDataException($"{key}.orca: required");
        var trigger = OrcaJson.Object(entry, "trigger") ?? throw new InvalidDataException($"{key}.trigger: required");
        var provider = OrcaJson.Object(entry, "provider") ?? throw new InvalidDataException($"{key}.provider: required");
        var workspace = OrcaJson.Object(entry, "workspace") ?? throw new InvalidDataException($"{key}.workspace: required");
        if ((OrcaJson.String(workspace, "mode") ?? "existing") != "existing")
        {
            throw new InvalidDataException($"{key}.workspace.mode: only \"existing\" is supported");
        }

        var roles = (OrcaJson.Array(entry, "roles") ?? throw new InvalidDataException($"{key}.roles: required"))
            .Select(r => r?.GetValueKind() == System.Text.Json.JsonValueKind.String ? MachineConfig.ParseRole(r.GetValue<string>()) : null)
            .Select(r => r ?? throw new InvalidDataException($"{key}.roles: each must be \"personal\" or \"work\""))
            .Distinct()
            .ToList();
        if (roles.Count == 0)
        {
            throw new InvalidDataException($"{key}.roles: name at least one role");
        }

        var grace = OrcaJson.Integer(trigger, "missed_run_grace_minutes") ?? 0;
        return new(
            key,
            Required(entry, "name"),
            roles,
            (OrcaJson.OneOf(entry, "management", ["manage", "adopt"]) ?? "manage") == "manage",
            Required(orca, "instance", key + ".orca"),
            Required(trigger, "rrule", key + ".trigger"),
            Required(trigger, "timezone", key + ".trigger"),
            grace is >= 0 and <= 7 * 24 * 60 ? (int)grace : throw new InvalidDataException($"{key}.trigger.missed_run_grace_minutes: must be 0 to 10080"),
            Required(provider, "agent", key + ".provider"),
            OrcaJson.Bool(provider, "reuse_session") ?? false,
            Required(workspace, "path", key + ".workspace"),
            Required(entry, "bootstrap", key),
            Required(entry, "policy_path", key),
            OrcaJson.String(entry, "memory_issue"),
            OrcaJson.Bool(entry, "enabled") ?? true);
    }

    private static string Required(JsonObject parent, string field, string? at = null) =>
        OrcaJson.String(parent, field) is { Length: > 0 } value ? value : throw new InvalidDataException($"{(at is null ? field : at + "." + field)}: required");

    [GeneratedRegex(@"^```text\n(?<body>.*?)^```", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex TextBlock();

    [GeneratedRegex(@"\{\{(?<name>[a-z_]+)\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>One automation as <c>orca automations list --json</c> reports it.</summary>
/// <param name="Id">Orca's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Prompt">Its prompt, as saved.</param>
/// <param name="Agent">Its agent id.</param>
/// <param name="Rrule">When it runs.</param>
/// <param name="Timezone">The zone the rule is read in.</param>
/// <param name="GraceMinutes">How late a missed run may still start, when set.</param>
/// <param name="Enabled">Whether it is on.</param>
/// <param name="ReuseSession">Whether runs reuse the previous session.</param>
/// <param name="WorkspacePath">The checkout it runs in, when it runs in an existing one.</param>
public sealed record LiveAutomation(
    string Id,
    string Name,
    string Prompt,
    string Agent,
    string Rrule,
    string Timezone,
    int? GraceMinutes,
    bool Enabled,
    bool ReuseSession,
    string? WorkspacePath)
{
    /// <summary>The automations in a <c>list --json</c> reply, or null when it is missing or not that shape.</summary>
    public static IReadOnlyList<LiveAutomation>? ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var result = OrcaJson.Object(OrcaJson.Object(json), "result");
            if (result is null || OrcaJson.Array(result, "automations") is not { } list)
            {
                return null;
            }

            return [.. list.OfType<JsonObject>().Select(Parse)];
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The automation's id in a <c>create --json</c> reply, or null.</summary>
    public static string? CreatedId(string? json)
    {
        try
        {
            return json is null ? null
                : OrcaJson.Object(OrcaJson.Object(json), "result") is { } result && OrcaJson.Object(result, "automation") is { } automation ? OrcaJson.String(automation, "id")
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static LiveAutomation Parse(JsonObject a) => new(
        OrcaJson.String(a, "id") ?? string.Empty,
        OrcaJson.String(a, "name") ?? string.Empty,
        OrcaJson.String(a, "prompt") ?? string.Empty,
        OrcaJson.String(a, "agentId") ?? string.Empty,
        OrcaJson.String(a, "rrule") ?? string.Empty,
        OrcaJson.String(a, "timezone") ?? string.Empty,
        OrcaJson.Integer(a, "missedRunGraceMinutes") is { } grace ? (int)grace : null,
        OrcaJson.Bool(a, "enabled") ?? false,
        OrcaJson.Bool(a, "reuseSession") ?? false,
        OrcaJson.String(a, "workspaceMode") == "existing" && OrcaJson.Object(a, "runContext") is { } run ? OrcaJson.String(run, "path") : null);
}

/// <summary>What reconciling one automation found, and did.</summary>
/// <param name="Key">The manifest key, or empty for a live automation the manifest does not declare.</param>
/// <param name="Name">The automation's name.</param>
/// <param name="Instance">The Orca instance's name.</param>
/// <param name="Action"><c>ok</c>, <c>create</c>, <c>update</c>, <c>drift</c> (adopted, so not changed), <c>skip</c> (another role), <c>unmanaged</c> or <c>error</c>.</param>
/// <param name="Fields">The fields that differ, for <c>update</c> and <c>drift</c>.</param>
/// <param name="Id">The Orca id, when there is one.</param>
/// <param name="Applied">Whether the change was made.</param>
/// <param name="Detail">Why, or what happened.</param>
public sealed record AutomationChange(
    string Key, string Name, string Instance, string Action, IReadOnlyList<string> Fields, string? Id, bool Applied, string Detail);

/// <summary>
/// Brings each Orca instance's automations in line with the manifest, through the <c>orca automations</c> CLI. Only
/// automations for this machine's role are touched; with <c>apply</c> off nothing is changed.
/// </summary>
/// <param name="cliFor">The <c>orca</c> CLI for an instance name.</param>
public sealed class AutomationReconciler(Func<string, IOrcaCli> cliFor)
{
    /// <summary>Plans, and with <paramref name="apply"/> makes, each change.</summary>
    /// <param name="wanted">Each declared automation with its rendered prompt.</param>
    /// <param name="role">This machine's role.</param>
    /// <param name="apply">Whether to change Orca.</param>
    /// <param name="cancellationToken">Cancels the CLI calls.</param>
    public async Task<IReadOnlyList<AutomationChange>> ReconcileAsync(
        IReadOnlyList<(AutomationSpec Spec, string Prompt)> wanted, MachineRole role, bool apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var changes = new List<AutomationChange>();
        foreach (var instance in wanted.GroupBy(w => w.Spec.Instance, StringComparer.Ordinal))
        {
            var label = instance.Key;
            var ours = instance.Where(w => w.Spec.Roles.Contains(role)).ToList();
            changes.AddRange(instance.Except(ours).Select(w => new AutomationChange(
                w.Spec.Key, w.Spec.Name, label, "skip", [], null, false,
                $"for the {string.Join("/", w.Spec.Roles.Select(MachineConfig.RoleName))} role; this machine is {MachineConfig.RoleName(role)}")));
            if (ours.Count == 0)
            {
                continue;
            }

            var cli = cliFor(instance.Key);
            if (LiveAutomation.ParseList(await cli.RunAsync(["automations", "list", "--json"], cancellationToken).ConfigureAwait(false)) is not { } live)
            {
                changes.AddRange(ours.Select(w => new AutomationChange(
                    w.Spec.Key, w.Spec.Name, label, "error", [], null, false, "Orca did not list its automations; is this instance running?")));
                continue;
            }

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (spec, prompt) in ours)
            {
                var change = await OneAsync(spec, prompt, label, live, claimed, cli, apply, cancellationToken).ConfigureAwait(false);
                changes.Add(change);
            }

            changes.AddRange(live.Where(a => !claimed.Contains(a.Id)).Select(a => new AutomationChange(
                string.Empty, a.Name, label, "unmanaged", [], a.Id, false, "not in the manifest; left alone")));
        }

        return changes;
    }

    /// <summary>The fields of <paramref name="live"/> that differ from <paramref name="spec"/> and its <paramref name="prompt"/>.</summary>
    public static IReadOnlyList<string> Differences(AutomationSpec spec, string prompt, LiveAutomation live)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(prompt);
        var fields = new List<string>();
        Add(fields, "name", spec.Name == live.Name);
        Add(fields, "prompt", Text(prompt) == Text(live.Prompt));
        Add(fields, "agent", string.Equals(spec.Agent, live.Agent, StringComparison.OrdinalIgnoreCase));
        Add(fields, "trigger", string.Equals(spec.Rrule, live.Rrule, StringComparison.OrdinalIgnoreCase));
        Add(fields, "timezone", spec.Timezone == live.Timezone);
        Add(fields, "grace", spec.GraceMinutes == (live.GraceMinutes ?? 0));
        Add(fields, "session", spec.ReuseSession == live.ReuseSession);
        Add(fields, "workspace", live.WorkspacePath is not null && SamePath(spec.WorkspacePath, live.WorkspacePath));
        Add(fields, "enabled", spec.Enabled == live.Enabled);
        return fields;
    }

    private static async Task<AutomationChange> OneAsync(
        AutomationSpec spec, string prompt, string label, IReadOnlyList<LiveAutomation> live, HashSet<string> claimed, IOrcaCli cli, bool apply, CancellationToken cancellationToken)
    {
        // Bound by name: ids differ on every machine, and the manifest is shared by all of them.
        var named = live.Where(a => a.Name == spec.Name).ToList();
        if (named.Count > 1)
        {
            claimed.UnionWith(named.Select(a => a.Id));
            return new(spec.Key, spec.Name, label, "error", [], null, false, $"{named.Count} automations share this name; remove the extras in Orca");
        }

        var found = named.FirstOrDefault();

        if (found is null)
        {
            if (!spec.Manage)
            {
                return new(spec.Key, spec.Name, label, "drift", ["missing"], null, false, "adopted, so not created");
            }

            if (!apply)
            {
                return new(spec.Key, spec.Name, label, "create", [], null, false, "planned");
            }

            var created = await cli.RunAsync(["automations", "create", .. Settings(spec, prompt, ["name", "prompt", "agent", "trigger", "timezone", "grace", "session", "workspace", "enabled"]), "--json"], cancellationToken).ConfigureAwait(false);
            return created is null
                ? new(spec.Key, spec.Name, label, "create", [], null, false, "orca automations create failed")
                : new(spec.Key, spec.Name, label, "create", [], LiveAutomation.CreatedId(created), true, "created");
        }

        _ = claimed.Add(found.Id);
        var fields = Differences(spec, prompt, found);
        if (fields.Count == 0)
        {
            return new(spec.Key, spec.Name, label, "ok", [], found.Id, false, string.Empty);
        }

        if (!spec.Manage)
        {
            return new(spec.Key, spec.Name, label, "drift", fields, found.Id, false, "adopted, so not changed");
        }

        if (!apply)
        {
            return new(spec.Key, spec.Name, label, "update", fields, found.Id, false, "planned");
        }

        var edited = await cli.RunAsync(["automations", "edit", "--id", found.Id, .. Settings(spec, prompt, fields), "--json"], cancellationToken).ConfigureAwait(false);
        return new(spec.Key, spec.Name, label, "update", fields, found.Id, edited is not null, edited is null ? "orca automations edit failed" : "updated");
    }

    // The CLI flags that set each named field.
    private static List<string> Settings(AutomationSpec spec, string prompt, IReadOnlyList<string> fields)
    {
        var args = new List<string>();
        foreach (var field in fields)
        {
            args.AddRange(field switch
            {
                "name" => ["--name", spec.Name],
                "prompt" => ["--prompt", prompt],
                "agent" => ["--provider", spec.Agent],
                "trigger" => ["--trigger", spec.Rrule],
                "timezone" => ["--timezone", spec.Timezone],
                "grace" => ["--missed-run-grace-minutes", spec.GraceMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                "session" => [spec.ReuseSession ? "--reuse-session" : "--fresh-session"],
                "workspace" => ["--workspace", "path:" + spec.WorkspacePath],
                "enabled" => [spec.Enabled ? "--enabled" : "--disabled"],
                _ => [],
            });
        }

        return args;
    }

    private static void Add(List<string> fields, string field, bool same)
    {
        if (!same)
        {
            fields.Add(field);
        }
    }

    private static string Text(string prompt) => prompt.ReplaceLineEndings("\n").Trim();

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
