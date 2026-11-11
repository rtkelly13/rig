using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Cli.Gh.Threads;
using Rig.Core.Processes;

namespace Rig.Cli.Agent;

/// <summary>The agent settings in <c>~/.workspace/config.json</c> (<c>tools.agent</c>).</summary>
/// <param name="Preferred">The agent a bare <c>rig</c> launches.</param>
/// <param name="Danger">Whether every launch skips permission prompts.</param>
/// <param name="ClaudeModel">Claude Code's model when the line names none; opus when null.</param>
/// <param name="ClaudeRemoteControl">Whether Claude Code always starts with Remote Control.</param>
internal sealed record AgentSettings(string? Preferred = null, bool Danger = false, string? ClaudeModel = null, bool ClaudeRemoteControl = false)
{
    /// <summary>
    /// The settings in <paramref name="json"/>, a workspace config. A file that is not JSON, or whose
    /// <c>tools.agent</c> has a value of the wrong kind, gives the defaults, as the Python's validation did.
    /// </summary>
    public static AgentSettings Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["tools"] is not JsonObject tools || tools["agent"] is not JsonObject agent)
            {
                return new AgentSettings();
            }

            var defaults = agent["defaults"] as JsonObject;
            var claude = (defaults?["claude-code"] ?? defaults?["claude_code"]) as JsonObject;
            return new AgentSettings(
                Optional<string>(agent["preferred"]),
                Optional<bool>(agent["danger"]),
                Optional<string>(claude?["model"]),
                Optional<bool>(claude?["remote_control"]));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return new AgentSettings();
        }
    }

    // Absent or null is the default; any other kind than T throws, and Parse falls back to the defaults.
    private static T? Optional<T>(JsonNode? node) =>
        node is null ? default : node.GetValue<T>();
}

/// <summary>What the launcher does with one command line.</summary>
/// <param name="Agent">The agent id from a shortcut or <c>--use</c>, or null for the preferred one.</param>
/// <param name="PrintConfig">Print the resolved agent and command instead of launching.</param>
/// <param name="Help">Print the usage instead of launching.</param>
/// <param name="RemoteControl">Start Claude Code with Remote Control.</param>
/// <param name="Danger">Skip the agent's permission prompts.</param>
/// <param name="Passthrough">Everything after the launcher's own flags, for the agent.</param>
internal sealed record AgentArgs(string? Agent, bool PrintConfig, bool Help, bool RemoteControl, bool Danger, IReadOnlyList<string> Passthrough);

/// <summary>The agent to launch and the full command line that launches it.</summary>
/// <param name="Id">The canonical agent id.</param>
/// <param name="DisplayName">Its human name.</param>
/// <param name="Executable">The program the command starts.</param>
/// <param name="Command">The program and its arguments.</param>
internal sealed record AgentLaunch(string Id, string DisplayName, string Executable, IReadOnlyList<string> Command);

/// <summary>Where the launcher runs: its config file, folder and PATH.</summary>
/// <param name="ConfigPath">The workspace config: <c>$WORKSPACE_HOME/config.json</c>, else <c>~/.workspace/config.json</c>.</param>
/// <param name="WorkingDirectory">The folder an agent starts in.</param>
/// <param name="Which">Finds a program on PATH, or null.</param>
internal sealed record AgentHost(string ConfigPath, string WorkingDirectory, Func<string, string?> Which)
{
    /// <summary>The workspace config path for <paramref name="workspaceHome"/> (may be null) and <paramref name="home"/>.</summary>
    public static string ConfigUnder(string? workspaceHome, string home)
    {
        var folder = string.IsNullOrEmpty(workspaceHome)
            ? Path.Join(home, ".workspace")
            : workspaceHome.StartsWith('~') ? Path.Join(home, workspaceHome[1..]) : workspaceHome;
        return Path.Join(folder, "config.json");
    }
}

/// <summary>
/// The <c>agent</c> launcher, also reached as a bare <c>rig</c>, <c>rig agent</c> and a leading shortcut flag
/// (<c>rig -ccc</c>): starts the preferred AI coding agent, or the one a flag picks, with the rest of the line
/// passed on untouched. Ported from <c>rig.agent_launcher</c>. Exit codes: the agent's own; 2 for a bad line or
/// agent id; 127 when the agent is not on PATH.
/// </summary>
/// <param name="proc">Starts the agent, attached to this console.</param>
/// <param name="host">The config, folder and PATH.</param>
/// <param name="output">Where help and <c>--print-config</c> go.</param>
/// <param name="error">Where problems go.</param>
internal sealed partial class AgentLauncher(IProc proc, AgentHost host, TextWriter output, TextWriter error)
{
    /// <summary>The <c>rig</c> subcommand that reaches the launcher.</summary>
    public const string CommandName = "agent";

    /// <summary>The one-line description <c>rig --help</c> shows.</summary>
    public const string Description = "Launch preferred AI coding agent or selected flag (-agy, -ccc, -oc, -codex, -dng).";

    /// <summary>The shortcut flags that launch an agent straight from <c>rig</c>, as in <c>rig -ccc</c>.</summary>
    public static readonly IReadOnlySet<string> ShortcutFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "-agy", "-ccc", "-agywsl", "-oc", "-opencode", "-codex", "-cpl", "-copilot", "-dng", "--dng",
        "--dangerously-skip-permissions", "-r", "--remote-control", "--print-config",
    };

    /// <summary>Each agent id, with its name and the program it runs, in the order errors list them.</summary>
    public static readonly IReadOnlyList<(string Id, string DisplayName, string Executable)> Agents =
    [
        ("antigravity", "Antigravity CLI", "agy"),
        ("claude-code", "Claude Code", "claude"),
        ("agywsl", "Antigravity CLI (WSL Ubuntu)", "wsl"),
        ("opencode", "OpenCode", "opencode"),
        ("codex", "OpenAI Codex", "codex"),
        ("github-copilot", "GitHub Copilot", "copilot"),
    ];

    private const string SkipPermissions = "--dangerously-skip-permissions";

    // The flag that skips each agent's permission prompts, and every spelling that already does.
    private static readonly Dictionary<string, (string Flag, string[] Equivalents)> DangerFlags = new(StringComparer.Ordinal)
    {
        ["antigravity"] = (SkipPermissions, [SkipPermissions]),
        ["claude-code"] = (SkipPermissions, [SkipPermissions]),
        ["agywsl"] = (SkipPermissions, [SkipPermissions]),
        ["opencode"] = ("--auto", ["--auto"]),
        ["codex"] = ("--dangerously-bypass-approvals-and-sandbox", ["--dangerously-bypass-approvals-and-sandbox", "--yolo"]),
        ["github-copilot"] = ("--allow-all", ["--allow-all", "--yolo"]),
    };

    // Other names an agent's program goes by, tried in order when it is not on PATH. OpenCode 2 installs as opencode,
    // except beside Orca, which names it opencode2.
    private static readonly Dictionary<string, string[]> Alternates = new(StringComparer.Ordinal) { ["opencode"] = ["opencode2"] };

    private static readonly string ValidIds = string.Join(", ", Agents.Select(a => a.Id));

    /// <summary>The launcher's help.</summary>
    public static readonly string Usage = $"""
        Usage: agent [-agy | -ccc | -agywsl | -oc | -codex | -cpl] [-dng] [-r]
                     [--use <agent-id>] [--print-config] [<agent args>...]

        Launches your preferred AI coding agent or the specified agent.

        Agent shortcuts:
          -agy               Launch Antigravity CLI on Windows (agy)
          -ccc               Launch Claude Code (claude)
          -agywsl            Launch Antigravity CLI in WSL2 Ubuntu
          -oc, -opencode     Launch OpenCode
          -codex             Launch OpenAI Codex
          -cpl, -copilot     Launch GitHub Copilot CLI

        Options:
          -dng, --dng        Dangerously run with permissions skipped / auto-approved
                             for the target agent.
          -r, --remote-control
                             Enable Remote Control for this run (Claude Code only), so
                             the session is reachable from the Claude mobile app / claude.ai.
          --use <agent-id>   Launch a specific agent for this run instead of the stored
                             preference. One of: {ValidIds}.
          --print-config     Print the resolved agent and exact command, then exit.
          -h, --help         Show this help and exit.

        Note: `-r` and `-dng` are consumed by this launcher, not forwarded. Use the long form
        `--resume` to resume a Claude Code session.

        Any other arguments are forwarded verbatim to the underlying agent CLI.

        """;

    /// <summary>
    /// The launcher's arguments when <paramref name="args"/>, a whole <c>rig</c> line, is a launch: none at all, a
    /// leading shortcut flag, or <c>agent …</c>. Null when the line is some other command.
    /// </summary>
    public static IReadOnlyList<string>? LauncherArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count == 0 || ShortcutFlags.Contains(args[0]) ? args
            : args[0] == CommandName ? [.. args.Skip(1)]
            : null;
    }

    /// <summary>The canonical id for an alias: gemini, claude and copilot name their agents.</summary>
    public static string CanonicalId(string id) => id switch
    {
        "gemini" => "antigravity",
        "claude" => "claude-code",
        "copilot" => "github-copilot",
        _ => id,
    };

    /// <summary>Splits the launcher's leading flags from the agent's arguments; the first other token ends them.</summary>
    /// <exception cref="ArgumentException"><c>--use</c> ends the line.</exception>
    public static AgentArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var parsed = new AgentArgs(null, false, false, false, false, []);
        var at = 0;
        while (at < args.Count)
        {
            var token = args[at];
            AgentArgs? next = token switch
            {
                "-h" or "--help" => parsed with { Help = true },
                "-r" or "--remote-control" => parsed with { RemoteControl = true },
                "-dng" or "--dng" or SkipPermissions => parsed with { Danger = true },
                "--print-config" => parsed with { PrintConfig = true },
                "-agy" => parsed with { Agent = "antigravity" },
                "-ccc" => parsed with { Agent = "claude-code" },
                "-agywsl" => parsed with { Agent = "agywsl" },
                "-oc" or "-opencode" => parsed with { Agent = "opencode" },
                "-codex" => parsed with { Agent = "codex" },
                "-cpl" or "-copilot" => parsed with { Agent = "github-copilot" },
                "--use" when at + 1 < args.Count => parsed with { Agent = args[++at] },
                "--use" => throw new ArgumentException("--use requires an agent id"),
                _ when token.StartsWith("--use=", StringComparison.Ordinal) => parsed with { Agent = token["--use=".Length..] },
                _ => null,
            };
            if (next is null)
            {
                break;
            }

            parsed = next;
            at++;
        }

        return parsed with { Passthrough = [.. args.Skip(at)] };
    }

    /// <summary>A Windows path as WSL sees it: <c>C:\code</c> is <c>/mnt/c/code</c>; a POSIX path is kept.</summary>
    public static string WslPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var raw = path.Replace('\\', '/');
        var drive = DrivePath().Match(raw);
        return drive.Success ? $"/mnt/{drive.Groups[1].Value.ToLowerInvariant()}/{drive.Groups[2].Value}" : raw;
    }

    /// <summary>The agent and full command for <paramref name="parsed"/>, given the stored settings.</summary>
    /// <exception cref="ArgumentException">The line names an agent id rig does not know.</exception>
    public static AgentLaunch Resolve(AgentArgs parsed, AgentSettings settings, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(settings);
        string id;
        if (parsed.Agent is not null)
        {
            id = CanonicalId(parsed.Agent);
            if (!Agents.Any(a => a.Id == id))
            {
                throw new ArgumentException($"Unknown agent id {PyRepr.Of(parsed.Agent)}. Valid ids: {ValidIds}.");
            }
        }
        else
        {
            var preferred = string.IsNullOrEmpty(settings.Preferred) ? null : CanonicalId(settings.Preferred);
            id = preferred is not null && Agents.Any(a => a.Id == preferred) ? preferred : "claude-code";
        }

        var (_, name, executable) = Agents.Single(a => a.Id == id);
        var rest = parsed.Passthrough;
        var (dangerFlag, equivalents) = DangerFlags[id];
        var addDanger = (parsed.Danger || settings.Danger) && !rest.Any(equivalents.Contains);
        List<string> command = [executable];

        switch (id)
        {
            case "claude-code":
                if ((parsed.RemoteControl || settings.ClaudeRemoteControl) && !rest.Contains("--remote-control"))
                {
                    command.Add("--remote-control");
                }

                if (addDanger)
                {
                    command.Add(dangerFlag);
                }

                if (!HasOption(rest, "--model"))
                {
                    command.AddRange(["--model", string.IsNullOrEmpty(settings.ClaudeModel) ? "opus" : settings.ClaudeModel]);
                }

                break;
            case "agywsl":
                command.AddRange(["-d", "Ubuntu", "--cd", WslPath(workingDirectory), "agy"]);
                if (!HasOption(rest, "--mode"))
                {
                    command.AddRange(["--mode", "auto"]);
                }

                if (addDanger)
                {
                    command.Add(dangerFlag);
                }

                break;
            default:
                if (addDanger)
                {
                    command.Add(dangerFlag);
                }

                break;
        }

        return new AgentLaunch(id, name, executable, [.. command, .. rest]);
    }

    /// <summary>Runs one launcher line (without <c>agent</c>) and returns its exit code.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        AgentLaunch launch;
        AgentArgs parsed;
        try
        {
            parsed = Parse(args);
            if (parsed.Help)
            {
                await output.WriteAsync(Usage.ReplaceLineEndings("\n")).ConfigureAwait(false);
                return 0;
            }

            launch = Resolve(parsed, LoadSettings(), host.WorkingDirectory);
        }
        catch (ArgumentException e)
        {
            await error.WriteLineAsync($"error: {e.Message}").ConfigureAwait(false);
            return (int)Core.ExitCode.Error;
        }

        string[] names = [launch.Executable, .. Alternates.GetValueOrDefault(launch.Executable, [])];
        var found = names.Select(n => (Name: n, Path: host.Which(n))).FirstOrDefault(f => f.Path is not null);

        if (parsed.PrintConfig)
        {
            await output.WriteLineAsync($"agent: {launch.DisplayName} ({launch.Id})").ConfigureAwait(false);
            await output.WriteLineAsync($"command: {string.Join(' ', [found.Name ?? launch.Executable, .. launch.Command.Skip(1)])}").ConfigureAwait(false);
            return 0;
        }

        if (found.Path is null)
        {
            await error.WriteLineAsync($"{launch.DisplayName} ('{launch.Executable}') was not found on PATH. Install it and try again.").ConfigureAwait(false);
            return Ran.NotFound;
        }

        // Never cancelled: the agent shares this console, Ctrl+C reaches it directly, and rig returns its exit code.
        var ran = await proc.RunAsync(new ProcRequest([found.Path, .. launch.Command.Skip(1)]) { Attached = true }, CancellationToken.None).ConfigureAwait(false);
        return ran.Code;
    }

    private static bool HasOption(IReadOnlyList<string> args, string option) =>
        args.Any(a => a == option || a.StartsWith(option + "=", StringComparison.Ordinal));

    private AgentSettings LoadSettings()
    {
        try
        {
            return File.Exists(host.ConfigPath) ? AgentSettings.Parse(File.ReadAllText(host.ConfigPath)) : new AgentSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AgentSettings();
        }
    }

    [GeneratedRegex("^([a-zA-Z]):/(.*)")]
    private static partial Regex DrivePath();
}
