using System.CommandLine;
using System.Text.Json;
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
/// projects the policy into agents' native permission files. Ported from <c>rig.policy.cli</c> (<c>workspace policy</c>).
/// </summary>
/// <param name="host">Where it reads and writes.</param>
/// <param name="output">Where reports go.</param>
/// <param name="error">Where a malformed policy is reported.</param>
internal sealed class PolicyCommand(PolicyHost host, TextWriter output, TextWriter error)
{
    /// <summary>The <c>policy</c> command.</summary>
    public Command Build()
    {
        var command = new Argument<string>("command") { Description = "Command string to classify." };
        var check = new Command("check", "Classify a command as safe, local, external, or denied.") { command };
        check.SetAction(parsed => Guarded(() => Check(parsed.GetValue(command)!)));

        var root = new Option<string>("--root") { Description = "Estate root to inspect.", DefaultValueFactory = _ => host.EstateRoot };
        var audit = new Command("audit", "Show manifest commands and normalized command-family history counts.") { root };
        audit.SetAction(parsed => Guarded(() => Audit(parsed.GetValue(root)!)));

        var list = new Command("list", "List the configured command rules and active skill count.");
        list.SetAction(_ => Guarded(List));

        var system = new Option<string[]>("--system", "-s") { Description = "Permission surfaces to update (opencode, claude, codex, agy, all).", AllowMultipleArgumentsPerToken = false };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview changes without writing native config." };
        var home = new Option<string>("--home") { Description = "Home folder holding the native config (default: yours).", DefaultValueFactory = _ => host.Home };
        var sync = new Command("sync", "Project the checked-in policy into native agent permission files.") { system, dryRun, home };
        sync.SetAction(parsed => Guarded(() => Sync(parsed.GetValue(system) ?? [], parsed.GetValue(dryRun), parsed.GetValue(home)!)));

        return new Command("policy", "Audit and manage estate-wide CLI allowlists.") { check, audit, list, sync };
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

    // A malformed or missing policy is reported, not thrown: the Python raised it as a traceback and exit 1.
    private int Guarded(Func<int> run)
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

    private int Check(string command)
    {
        var risk = Load().Check(command);
        output.WriteLine($"{risk.Name()}: {command}");
        return risk == CommandRisk.Denied ? 1 : 0;
    }

    private int Audit(string root)
    {
        var commands = ProjectAudit.Commands(root);
        output.WriteLine($"Estate Commands ({commands.Count})");
        string[][] rows = [["Project", "Command", "Risk"], .. commands.Select(c => new[] { c.Project, c.Invocation, c.Risk.Name() })];
        var widths = Enumerable.Range(0, 3).Select(c => rows.Max(r => r[c].Length)).ToArray();
        foreach (var row in rows)
        {
            output.WriteLine(string.Join("  ", row.Select((cell, c) => cell.PadRight(widths[c]))).TrimEnd());
        }

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

    private int List()
    {
        var policy = Load();
        output.WriteLine($"Active skills: {policy.SkillsAllowlist.Count}");
        foreach (var rule in policy.CommandRules)
        {
            output.WriteLine($"{rule.Risk.Name(),-8} {rule.Pattern}");
        }

        return 0;
    }

    private int Sync(IReadOnlyList<string> names, bool dryRun, string home)
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
}
