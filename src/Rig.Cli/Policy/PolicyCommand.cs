using System.CommandLine;
using System.Text.Json;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core.Policy;

namespace Rig.Cli.Policy;

/// <summary>Where <c>rig policy</c> reads from: the checked-in policy, the user's home, the estate, the platform.</summary>
/// <param name="PolicyPath">The checkout's <c>config/cli-allowlists.json</c>.</param>
/// <param name="Home">The home folder native permission files and shell history live under.</param>
/// <param name="EstateRoot">What <c>audit</c> inspects unless given <c>--root</c>.</param>
/// <param name="Windows">Whether globs match case-insensitively and files are written with CRLF.</param>
internal sealed record PolicyHost(string PolicyPath, string Home, string EstateRoot, bool Windows);

/// <summary>
/// <c>rig policy</c>: classifies commands against the estate policy, audits the estate's manifest commands, and
/// projects the policy into agents' native permission files.
/// </summary>
/// <param name="host">Where it reads and writes.</param>
/// <param name="output">Where reports go.</param>
/// <param name="error">Where a malformed policy is reported.</param>
[CliCommand(Name = "policy", Description = "Audit and manage estate-wide CLI allowlists.")]
internal sealed class PolicyCommand(PolicyHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal PolicyCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The <c>policy</c> command.</summary>
    public Command Build()
    {
        var policy = DeclarativeCommand.BuildGroup(this);
        policy.Add(DeclarativeCommand.Build(new CheckCommand(this)));
        policy.Add(DeclarativeCommand.Build(new AuditCommand(this)).WithDefault("--root", () => host.EstateRoot));
        policy.Add(DeclarativeCommand.Build(new ListCommand(this)));
        policy.Add(DeclarativeCommand.Build(new SyncCommand(this)).WithDefault("--home", () => host.Home));
        return policy;
    }

    /// <summary>The systems <paramref name="names"/> select, or null when one is unknown; <c>agy</c> is Antigravity and <c>all</c> or none is every one.</summary>
    public static IReadOnlyList<string>? ResolveSystems(IReadOnlyList<string> names, out string? unknown)
    {
        ArgumentNullException.ThrowIfNull(names);
        unknown = null;
        if (names.Count == 0)
        {
            return NativeProjection.Systems;
        }

        var resolved = new List<string>();
        foreach (var value in names)
        {
            var name = value.Trim().ToLowerInvariant() is "agy" ? "antigravity" : value.Trim().ToLowerInvariant();
            if (name == "all")
            {
                return NativeProjection.Systems;
            }

            if (!NativeProjection.Systems.Contains(name))
            {
                unknown = value;
                return null;
            }

            resolved.Add(name);
        }

        return resolved;
    }

    private CommandPolicy Load() => CommandPolicy.Load(host.PolicyPath, host.Windows);

    // A malformed or missing policy is reported, not thrown.
    internal int Guarded(Func<int> run)
    {
        try
        {
            return run();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error.WriteLine($"rig policy: {e.Message}");
            return 1;
        }
    }

    internal int Check(string command)
    {
        var risk = Load().Check(command);
        output.WriteLine($"{risk.Name()}: {command}");
        return risk == CommandRisk.Denied ? 1 : 0;
    }

    internal int Audit(string root)
    {
        var commands = ProjectAudit.Commands(root);
        var table = new TextTable(
            $"Estate Commands ({commands.Count})",
            [("Project", false), ("Command", false), ("Risk", false)]);
        foreach (var command in commands)
        {
            _ = table.Add(command.Project, command.Invocation, command.Risk.Name());
        }

        output.Write(table.Render());

        output.WriteLine();
        output.WriteLine("Normalized shell-history families:");
        var history = ProjectAudit.History(Path.Join(host.Home, ".zsh_history"));
        if (history.Count == 0)
        {
            output.WriteLine("  no matching history entries found");
        }

        foreach (var (family, count) in history)
        {
            output.WriteLine($"  {family}: {count}");
        }

        return 0;
    }

    internal int List()
    {
        var policy = Load();
        output.WriteLine($"Active skills: {policy.SkillsAllowlist.Count}");
        foreach (var rule in policy.CommandRules)
        {
            output.WriteLine($"{rule.Risk.Name(),-8} {rule.Pattern}");
        }

        return 0;
    }

    internal int Sync(IReadOnlyList<string> names, bool dryRun, string home)
    {
        if (ResolveSystems(names, out var unknown) is not { } systems)
        {
            output.WriteLine($"Unknown CLI system: {unknown}");
            return 2;
        }

        var results = NativeProjection.ApplyAll(Load(), systems, home, dryRun, host.Windows);
        foreach (var result in results)
        {
            output.WriteLine($"{result.System}: {result.Status.Name()} — {result.Path ?? "no native path"} ({result.Detail})");
        }

        return results.Any(r => r.Status == ProjectionStatus.Failed) ? 1 : 0;
    }

    /// <summary><c>rig policy check</c>.</summary>
    [CliCommand(Name = "check", Description = "Classify a command as safe, local, external, or denied.")]
    internal sealed class CheckCommand(PolicyCommand policy)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal CheckCommand()
            : this(null!)
        {
        }

        /// <summary>The command string to classify.</summary>
        [CliArgument(Name = "command", Description = "Command string to classify.")]
        public string Command { get; set; } = null!;

        /// <summary>Classifies the command.</summary>
        public int Run() => policy.Guarded(() => policy.Check(Command));
    }

    /// <summary><c>rig policy audit</c>.</summary>
    [CliCommand(Name = "audit", Description = "Show manifest commands and normalized command-family history counts.")]
    internal sealed class AuditCommand(PolicyCommand policy)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal AuditCommand()
            : this(null!)
        {
        }

        /// <summary>The estate root to inspect.</summary>
        [CliOption(Name = "--root", Required = false, Description = "Estate root to inspect.")]
        public string Root { get; set; } = null!;

        /// <summary>Shows the estate's commands.</summary>
        public int Run() => policy.Guarded(() => policy.Audit(Root));
    }

    /// <summary><c>rig policy list</c>.</summary>
    [CliCommand(Name = "list", Description = "List the configured command rules and active skill count.")]
    internal sealed class ListCommand(PolicyCommand policy)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ListCommand()
            : this(null!)
        {
        }

        /// <summary>Lists the rules.</summary>
        public int Run() => policy.Guarded(policy.List);
    }

    /// <summary><c>rig policy sync</c>.</summary>
    [CliCommand(Name = "sync", Description = "Project the checked-in policy into native agent permission files.")]
    internal sealed class SyncCommand(PolicyCommand policy)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal SyncCommand()
            : this(null!)
        {
        }

        /// <summary>Permission surfaces to update.</summary>
        [CliOption(Name = "--system", Aliases = ["-s"], Required = false, AllowMultipleArgumentsPerToken = false, Description = "Permission surfaces to update (opencode, claude, codex, agy, all).")]
        public string[] Systems { get; set; } = [];

        /// <summary>Preview changes without writing.</summary>
        [CliOption(Name = "--dry-run", Description = "Preview changes without writing native config.")]
        public bool DryRun { get; set; }

        /// <summary>The home folder holding the native config.</summary>
        [CliOption(Name = "--home", Required = false, Description = "Home folder holding the native config (default: yours).")]
        public string Home { get; set; } = null!;

        /// <summary>Projects the policy.</summary>
        public int Run() => policy.Guarded(() => policy.Sync(Systems, DryRun, Home));
    }
}
