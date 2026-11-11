using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Orca;

/// <summary>A worktree's setup and archive scripts.</summary>
/// <param name="Setup">The setup script.</param>
/// <param name="Archive">The archive script.</param>
public sealed record Scripts(string Setup = "", string Archive = "")
{
    /// <summary>The script of <paramref name="kind"/>: <c>setup</c> or <c>archive</c>.</summary>
    public string Of(string kind) => kind == "setup" ? Setup : Archive;

    /// <summary>Scripts from a JSON object, or null when a script is not a string.</summary>
    internal static Scripts? FromJson(JsonObject? json) => json is null ? null : OrcaJson.Lenient(() =>
        new Scripts(OrcaJson.String(json, "setup") ?? "", OrcaJson.String(json, "archive") ?? ""));
}

/// <summary>Orca's per-repo <c>hookSettings</c>. <c>repo.update</c> replaces it whole, so writes carry every field.</summary>
/// <param name="Mode">Orca's <c>mode</c>.</param>
/// <param name="SetupRunPolicy">Orca's <c>setupRunPolicy</c>.</param>
/// <param name="SetupAgentStartupPolicy">Orca's <c>setupAgentStartupPolicy</c>.</param>
/// <param name="Scripts">The local scripts, or null when absent or unreadable.</param>
/// <param name="CommandSourcePolicy"><c>local-only</c>, <c>shared-only</c>, <c>run-both</c>, or null.</param>
public sealed record HookSettings(
    string? Mode = null,
    string? SetupRunPolicy = null,
    string? SetupAgentStartupPolicy = null,
    Scripts? Scripts = null,
    string? CommandSourcePolicy = null)
{
    /// <summary>Hook settings from Orca's JSON.</summary>
    /// <exception cref="InvalidDataException">A field other than <c>scripts</c> has the wrong type.</exception>
    internal static HookSettings FromJson(JsonObject json) => new(
        OrcaJson.String(json, "mode"),
        OrcaJson.String(json, "setupRunPolicy"),
        OrcaJson.String(json, "setupAgentStartupPolicy"),
        OrcaJson.Lenient(() => Scripts.FromJson(OrcaJson.Object(json, "scripts"))),
        OrcaJson.String(json, "commandSourcePolicy"));

    /// <summary>The settings as <c>repo.update</c> takes them: null fields left out, empty strings kept.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        Put(json, "mode", Mode);
        Put(json, "setupRunPolicy", SetupRunPolicy);
        Put(json, "setupAgentStartupPolicy", SetupAgentStartupPolicy);
        if (Scripts is not null)
        {
            json["scripts"] = new JsonObject { ["setup"] = Scripts.Setup, ["archive"] = Scripts.Archive };
        }

        Put(json, "commandSourcePolicy", CommandSourcePolicy);
        return json;
    }

    private static void Put(JsonObject json, string key, string? value)
    {
        if (value is not null)
        {
            json[key] = value;
        }
    }
}

/// <summary>One script kind as Orca will resolve it for a new worktree.</summary>
/// <param name="Shared">The repo's <c>orca.yaml</c> script.</param>
/// <param name="Local">Orca's own per-repo script.</param>
/// <param name="Policy"><c>local-only</c>, <c>shared-only</c> or <c>run-both</c>.</param>
public sealed record HookSource(string Shared, string Local, string Policy)
{
    /// <summary>The script a new worktree runs.</summary>
    public string Effective => Policy switch
    {
        "local-only" => Local,
        "run-both" => string.Join('\n', new[] { Shared, Local }.Where(script => script.Length > 0)),
        _ => Shared,
    };
}

/// <summary>
/// Where an Orca worktree's setup and archive scripts come from. Orca merges a repo's committed <c>orca.yaml</c> (the
/// shared source) with its own <c>hookSettings</c> (the local one); <c>commandSourcePolicy</c> picks between them, and
/// when it is unset any local script wins.
/// </summary>
public static class OrcaHooks
{
    /// <summary>The file a repo declares its shared scripts in.</summary>
    public const string OrcaYaml = "orca.yaml";

    /// <summary>The scripts a repo's <c>orca.yaml</c> declares, or null when it has no readable file.</summary>
    public static Scripts? ReadOrcaYaml(string repoPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Join(repoPath, OrcaYaml));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        PyValue? parsed;
        try
        {
            parsed = PyYaml.Load(text);
        }
        catch (PyYamlException)
        {
            return null;
        }

        return parsed is PyDict document ? ScriptsOf(Get(document, "scripts")) ?? new Scripts() : null;
    }

    /// <summary>Mirrors Orca's choice between <c>orca.yaml</c> and its local script for <paramref name="kind"/>.</summary>
    public static HookSource Resolve(string kind, HookSettings? local, Scripts? shared)
    {
        var localScript = (local?.Scripts?.Of(kind) ?? "").Trim();
        var sharedScript = (shared?.Of(kind) ?? "").Trim();
        return new(sharedScript, localScript, Policy(local, localScript.Length > 0));
    }

    /// <summary><paramref name="local"/> with its scripts cleared and <c>orca.yaml</c> as the only source; other fields kept.</summary>
    public static HookSettings DeferToOrcaYaml(HookSettings? local) =>
        (local ?? new HookSettings()) with { Scripts = new Scripts(), CommandSourcePolicy = "shared-only" };

    private static string Policy(HookSettings? local, bool hasLocal) => local?.CommandSourcePolicy switch
    {
        "local-only" => "local-only",
        "run-both" => "run-both",
        null when hasLocal => "local-only",
        _ => "shared-only",
    };

    private static Scripts? ScriptsOf(PyValue? value)
    {
        if (value is not PyDict scripts)
        {
            return null;
        }

        var setup = Get(scripts, "setup");
        var archive = Get(scripts, "archive");
        return setup is null or PyNone or PyStr && archive is null or PyNone or PyStr
            ? new Scripts((setup as PyStr)?.Text ?? "", (archive as PyStr)?.Text ?? "")
            : null;
    }

    private static PyValue? Get(PyDict dict, string key) =>
        dict.Entries.FirstOrDefault(entry => entry.Key is PyStr { } name && name.Text == key).Value;
}
