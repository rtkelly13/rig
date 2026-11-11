using System.Text.Json.Nodes;

namespace Rig.Core.Devenv;

/// <summary>A git identity routed to by folder: commits under <see cref="MatchDirectory"/> use this name and email.</summary>
/// <param name="Id">The identity's id; its profile is <c>~/.gitconfig-{Id}</c>.</param>
/// <param name="Name">The commit name.</param>
/// <param name="Email">The commit email.</param>
/// <param name="SigningKey">The signing key, if any.</param>
/// <param name="MatchDirectory">The folder whose repositories use it; an identity without one is not routed.</param>
public sealed record GitIdentity(string Id, string Name, string Email, string? SigningKey, string? MatchDirectory);

/// <summary>
/// devenv's git setup: the identities in <c>~/.config/devenv/config.json</c> and the <c>includeIf</c> blocks that
/// route each folder to one. Ported from devenv's <c>steps/git.py</c> and <c>config/loader.py</c>.
/// </summary>
public static class GitSetup
{
    private static readonly string[] SshKeys = ["id_ed25519", "id_rsa"];

    /// <summary>The global defaults setup applies: key, then value. <c>core.autocrlf</c> is added per platform.</summary>
    public static IReadOnlyList<(string Key, string Value)> Defaults(bool windows) =>
        [("init.defaultBranch", "main"), ("pull.rebase", "false"), ("push.autoSetupRemote", "true"), ("core.autocrlf", windows ? "true" : "input")];

    /// <summary>The devenv config file under <paramref name="home"/>.</summary>
    public static string ConfigPath(string home) => Path.Join(home, ".config", "devenv", "config.json");

    /// <summary>The identities the config lists; none when it is missing or unreadable, and entries without an id, name or email are skipped.</summary>
    public static IReadOnlyList<GitIdentity> Identities(string home)
    {
        if (SettingsFile.Load(ConfigPath(home))?["identities"] is not JsonArray list)
        {
            return [];
        }

        static string? Text(JsonObject entry, string field) => entry[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        return
        [
            .. list.OfType<JsonObject>()
                .Where(entry => Text(entry, "id") is not null && Text(entry, "name") is not null && Text(entry, "email") is not null)
                .Select(entry => new GitIdentity(Text(entry, "id")!, Text(entry, "name")!, Text(entry, "email")!, Text(entry, "signing_key"), Text(entry, "match_directory"))),
        ];
    }

    /// <summary>
    /// Appends <paramref name="identity"/> to the config's identities, keeping everything else in it. Returns false,
    /// writing nothing, when the config is not a JSON object.
    /// </summary>
    public static bool AddIdentity(string home, GitIdentity identity, bool windows)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var path = ConfigPath(home);
        if (SettingsFile.Load(path) is not { } config)
        {
            return false;
        }

        if (config["identities"] is not JsonArray list)
        {
            list = [];
            config["identities"] = list;
        }

        var entry = new JsonObject { ["id"] = identity.Id, ["name"] = identity.Name, ["email"] = identity.Email, ["is_default"] = false };
        if (identity.SigningKey is not null)
        {
            entry["signing_key"] = identity.SigningKey;
        }

        if (identity.MatchDirectory is not null)
        {
            entry["match_directory"] = identity.MatchDirectory;
        }

        list.Add(entry);
        SettingsFile.Save(path, config, windows);
        return true;
    }

    /// <summary>The <c>~/.gitconfig-{id}</c> profile an identity's folder includes.</summary>
    public static string Profile(GitIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var text = $"[user]\n\tname = {identity.Name}\n\temail = {identity.Email}\n";
        return identity.SigningKey is null ? text : text + $"\tsigningkey = {identity.SigningKey}\n";
    }

    /// <summary>The <c>includeIf</c> block routing <paramref name="matchDirectory"/> to <paramref name="profile"/>.</summary>
    public static string IncludeBlock(string matchDirectory, string profile)
    {
        ArgumentNullException.ThrowIfNull(matchDirectory);
        return $"[includeIf \"gitdir:{matchDirectory.TrimEnd('/', '\\')}/\"]\n\tpath = {profile}\n";
    }

    /// <summary>
    /// <paramref name="gitconfig"/> with an <c>includeIf</c> block appended for each routed identity it lacks; the same
    /// text when every one is already there.
    /// </summary>
    public static string WithIncludes(string gitconfig, string home, IEnumerable<GitIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(gitconfig);
        ArgumentNullException.ThrowIfNull(identities);
        var text = gitconfig;
        foreach (var identity in identities.Where(i => !string.IsNullOrEmpty(i.MatchDirectory)))
        {
            var block = IncludeBlock(identity.MatchDirectory!, Path.Join(home, $".gitconfig-{identity.Id}"));
            if (text.Contains(block, StringComparison.Ordinal))
            {
                continue;
            }

            if (!text.EndsWith('\n'))
            {
                text += "\n";
            }

            text += $"\n# Managed identity: {identity.Name} ({identity.Id})\n{block}";
        }

        return text;
    }

    /// <summary>The SSH key in <c>~/.ssh</c> (ed25519, then RSA), or null.</summary>
    public static string? SshKey(string home) =>
        SshKeys.Select(name => Path.Join(home, ".ssh", name)).FirstOrDefault(File.Exists);
}
