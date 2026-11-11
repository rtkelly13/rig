using System.CommandLine;
using System.Globalization;
using Rig.Core.Processes;
using Rig.Core.Vault;

namespace Rig.Cli.Vault;

/// <summary>
/// Builds the <see cref="KnowledgeServer"/> when a command needs it, so the user id and launchd are looked up only by
/// <c>vault knowledge-server</c>, not by every rig command.
/// </summary>
internal delegate Task<KnowledgeServer> KnowledgeServerFactory(CancellationToken cancellationToken);

/// <summary>
/// <c>rig vault knowledge-server</c>: the one OpenKnowledge server for the vault, shared by every Claude session as a
/// macOS LaunchAgent. Ported from <c>rig.obsidian.commands.knowledge_server</c>. <c>install</c>, <c>restart</c> and
/// <c>uninstall</c> exit 2 off macOS; <c>connect</c> and <c>status</c> run anywhere.
/// </summary>
internal sealed class KnowledgeServerCommand(
    IProc proc, KnowledgeServerFactory server, bool macOs, Func<string, string?> which, TextWriter output, TextWriter error)
{
    /// <summary><c>status</c> exits with this when the server is down or Claude does not use it.</summary>
    public const int UnhealthyExit = 1;

    /// <summary>A LaunchAgent command off macOS exits with this.</summary>
    public const int NotMacOsExit = 2;

    /// <summary><c>connect</c> exits with this when <c>claude</c> is not on PATH.</summary>
    public const int ClaudeMissingExit = 127;

    /// <summary>How long each <c>claude mcp</c> call in <c>connect</c> may take.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The <c>knowledge-server</c> command.</summary>
    public Command Build()
    {
        var apply = new Option<bool>("--apply") { Description = "Make the change; without it, print what would run" };
        var connect = new Command(
            "connect",
            "Point Claude Code's user-scope open-knowledge entry at the shared server. Sessions already running keep the connector they started with until they restart.")
        {
            apply,
        };
        connect.SetAction((parsed, ct) => ConnectAsync(parsed.GetValue(apply), ct));

        var status = new Command("status", "Whether the server is installed, running and healthy, and whether Claude Code uses it.");
        status.SetAction((_, ct) => StatusAsync(ct));

        return new Command("knowledge-server", "One OpenKnowledge server for the vault, shared by every Claude session (macOS LaunchAgent).")
        {
            OnMacOs("install", "Install the pinned package under rig's data folder and load the LaunchAgent. Idempotent.", InstallAsync),
            OnMacOs("restart", "Restart the server, for example after the vault's .ok/config.yml changes.", async (ks, ct) =>
            {
                await ks.RestartAsync(ct).ConfigureAwait(false);
                await output.WriteLineAsync("restarted").ConfigureAwait(false);
                return 0;
            }),
            OnMacOs(
                "uninstall",
                "Unload and remove the LaunchAgent. Claude's entry then points at nothing: `ok init` in the vault registers the per-session one again.",
                async (ks, ct) =>
                {
                    var removed = await ks.UninstallServiceAsync(ct).ConfigureAwait(false);
                    await output.WriteLineAsync(removed ? "removed" : "no LaunchAgent installed").ConfigureAwait(false);
                    return 0;
                }),
            connect,
            status,
        };
    }

    /// <summary>The user id that names launchd's GUI domain, from <c>id -u</c>; 0 when that cannot be read.</summary>
    public static async Task<uint> UserIdAsync(IProc proc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proc);
        var ran = await proc.RunAsync(new ProcRequest(["id", "-u"]), cancellationToken).ConfigureAwait(false);
        return ran.Code == 0 && uint.TryParse(ran.Out.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : 0;
    }

    private Command OnMacOs(string name, string description, Func<KnowledgeServer, CancellationToken, Task<int>> run)
    {
        var command = new Command(name, description);
        command.SetAction(async (_, ct) =>
        {
            if (!macOs)
            {
                await output.WriteLineAsync("the shared server runs as a macOS LaunchAgent; nothing to do here").ConfigureAwait(false);
                return NotMacOsExit;
            }

            try
            {
                return await run(await server(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FileNotFoundException or ProcException)
            {
                await error.WriteLineAsync($"rig vault knowledge-server {name}: {e.Message}").ConfigureAwait(false);
                return 1;
            }
        });
        return command;
    }

    private async Task<int> InstallAsync(KnowledgeServer ks, CancellationToken cancellationToken)
    {
        var script = await ks.InstallAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var path = await ks.InstallServiceAsync(script, cancellationToken: cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"open-knowledge {KnowledgeServer.Version} LaunchAgent loaded: {path}").ConfigureAwait(false);
        await output.WriteLineAsync($"editor {KnowledgeServer.EditorUrl()}  connector {KnowledgeServer.McpUrl()}  log {ks.Places.LogPath}").ConfigureAwait(false);
        if (!ks.CurrentRegistration().Shared)
        {
            await output.WriteLineAsync("Claude still starts its own copy per session: run `vault knowledge-server connect`.").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> ConnectAsync(bool apply, CancellationToken cancellationToken)
    {
        var ks = await server(cancellationToken).ConfigureAwait(false);
        var have = ks.CurrentRegistration();
        if (have.Shared)
        {
            await output.WriteLineAsync($"already shared: {KnowledgeServer.McpUrl()}").ConfigureAwait(false);
            return 0;
        }

        if (which("claude") is not { } claude)
        {
            await error.WriteLineAsync("claude is not on PATH").ConfigureAwait(false);
            return ClaudeMissingExit;
        }

        await output.WriteLineAsync(Describe(have, "now: ")).ConfigureAwait(false);
        var commands = KnowledgeServer.ConnectCommands(have, claude);
        foreach (var argv in commands)
        {
            await output.WriteLineAsync($"  {string.Join(' ', argv)}").ConfigureAwait(false);
        }

        if (!apply)
        {
            await output.WriteLineAsync("dry run: pass --apply to change ~/.claude.json").ConfigureAwait(false);
            return 0;
        }

        foreach (var argv in commands)
        {
            var ran = await proc.RunAsync(new ProcRequest(argv) { Timeout = ConnectTimeout }, cancellationToken).ConfigureAwait(false);
            if (ran.Code != 0)
            {
                var why = ran.Err.Trim() is { Length: > 0 } err ? err : ran.Out.Trim();
                await error.WriteLineAsync($"{string.Join(' ', argv.Skip(1))} failed: {why}").ConfigureAwait(false);
                return 1;
            }
        }

        await output.WriteLineAsync("connected; new sessions use the shared server").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        var ks = await server(cancellationToken).ConfigureAwait(false);
        var installed = KnowledgeServer.InstalledVersion(ks.Places.Prefix);
        var have = ks.CurrentRegistration();
        (string Name, bool Ok, string Detail)[] rows =
        [
            ("package", installed == KnowledgeServer.Version, $"{installed ?? "not installed"} (pinned {KnowledgeServer.Version})"),
            ("LaunchAgent", ks.ServiceInstalled && await ks.ServiceLoadedAsync(cancellationToken).ConfigureAwait(false), ks.PlistPath),
            ("health", await ks.HealthyAsync(cancellationToken: cancellationToken).ConfigureAwait(false), $"{KnowledgeServer.EditorUrl()}healthz"),
            ("claude entry", have.Shared, Describe(have, string.Empty)),
        ];
        foreach (var (name, ok, detail) in rows)
        {
            await output.WriteLineAsync($"{(ok ? "ok  " : "fail")} {name,-13} {detail}").ConfigureAwait(false);
        }

        if (rows.All(r => r.Ok))
        {
            return 0;
        }

        await output.WriteLineAsync("fix: `vault knowledge-server install`, then `vault knowledge-server connect --apply`").ConfigureAwait(false);
        return UnhealthyExit;
    }

    private static string Describe(Registration have, string prefix) =>
        $"{prefix}{have.Transport ?? "absent"} {have.Target ?? string.Empty}".TrimEnd();
}
