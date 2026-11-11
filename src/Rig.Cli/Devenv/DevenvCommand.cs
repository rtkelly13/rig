using System.CommandLine;
using System.Text.Json;
using Rig.Core.Devenv;
using Rig.Core.Policy;

namespace Rig.Cli.Devenv;

/// <summary>Where <c>rig devenv</c> sets things up.</summary>
/// <param name="Home">The default home folder.</param>
/// <param name="PolicyPath">The checkout's <c>config/cli-allowlists.json</c>, projected into Claude's permissions.</param>
/// <param name="Windows">Whether settings files are written with CRLF and globs match case-insensitively.</param>
internal sealed record DevenvHost(string Home, string PolicyPath, bool Windows);

/// <summary>
/// The ported part of <c>rig devenv</c>: <c>claude</c>. Every other <c>devenv</c> subcommand is still Python's, and
/// <see cref="Forwarding.Forwarded.Owns"/> hands those lines over before parsing. Ported from devenv's
/// <c>claude_code</c> and <c>session_ledger</c>.
/// </summary>
/// <param name="host">Where it sets up.</param>
/// <param name="output">Where progress goes.</param>
/// <param name="error">Where failures go.</param>
internal sealed class DevenvCommand(DevenvHost host, TextWriter output, TextWriter error)
{
    /// <summary>What <c>rig devenv</c> is, matching the Python group's help.</summary>
    public const string Description = "Unified development environment manager (tools, runtimes, fonts, git, AI coding agents).";

    /// <summary>The <c>devenv</c> group, holding the subcommands C# owns.</summary>
    public Command Build()
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would change without writing it." };
        var home = new Option<string>("--home") { Description = "Home folder to set up (default: yours).", DefaultValueFactory = _ => host.Home };
        var check = new Option<bool>("--check") { Description = "Report whether the session-ledger hook is wired and current, changing nothing; exit 1 when it is not." };
        var claude = new Command("claude", "Merge the Claude Code settings baseline and install the session-ledger SessionEnd hook.") { dryRun, home, check };
        claude.SetAction(parsed => parsed.GetValue(check) ? Check(parsed.GetValue(home)!) : Claude(parsed.GetValue(dryRun), parsed.GetValue(home)!));
        return new Command("devenv", Description) { claude };
    }

    private int Check(string home)
    {
        var settings = ClaudeBaseline.Load(ClaudeBaseline.SettingsPath(home));
        var state = settings is null ? LedgerHookState.NotWired : SessionLedger.State(settings, SessionLedger.HookPath(home));
        output.WriteLine($"Session-ledger hook: {state.Describe()}");
        return state == LedgerHookState.Current ? 0 : 1;
    }

    // Merge the baseline and hook, then project the policy; 1 when the settings are unreadable or a write fails.
    private int Claude(bool dryRun, string home)
    {
        var path = ClaudeBaseline.SettingsPath(home);
        var hook = SessionLedger.HookPath(home);
        if (ClaudeBaseline.Load(path) is not { } existing)
        {
            error.WriteLine($"! {path} is not a readable JSON object; fix it by hand, then re-run.");
            return 1;
        }

        var merge = ClaudeBaseline.Merge(existing, SessionLedger.Command(hook));
        var stale = !SessionLedger.ScriptCurrent(hook);
        if (merge.Changes.Count == 0 && !stale)
        {
            output.WriteLine($"✓ Claude Code settings up to date: {path}");
            return Project(dryRun, home);
        }

        if (merge.Changes.Count > 0)
        {
            output.WriteLine($"Applying {merge.Changes.Count} baseline updates to {path}:");
            foreach (var change in merge.Changes)
            {
                output.WriteLine($"  {change}");
            }
        }

        if (stale)
        {
            output.WriteLine($"Installing the session-ledger hook script at {hook}");
        }

        if (dryRun)
        {
            output.WriteLine("Dry run: no changes written.");
            return Project(dryRun, home);
        }

        // Install the script before wiring it, so the hook never points at nothing.
        if (stale && !Install(hook))
        {
            return 1;
        }

        if (merge.Changes.Count > 0)
        {
            try
            {
                ClaudeBaseline.Save(path, merge.Settings, host.Windows);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Failed to write Claude settings: {e.Message}");
                return 1;
            }

            output.WriteLine($"✓ Successfully updated Claude Code settings at {path}");
        }

        return Project(dryRun, home);
    }

    private bool Install(string hook)
    {
        try
        {
            SessionLedger.InstallScript(hook);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Failed to install {hook}: {e.Message}; Claude Code settings left unchanged.");
            return false;
        }

        output.WriteLine($"✓ Session-ledger hook script installed at {hook}");
        return true;
    }

    // The estate policy's command rules, projected into Claude's permissions as `rig policy sync --system claude` does.
    private int Project(bool dryRun, string home)
    {
        try
        {
            var result = NativeProjection.For("claude", home, host.Windows).Apply(CommandPolicy.Load(host.PolicyPath, host.Windows), dryRun);
            output.WriteLine($"{result.System}: {result.Status.Name()} — {result.Path ?? "no native path"} ({result.Detail})");
            return result.Status == ProjectionStatus.Failed ? 1 : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error.WriteLine($"rig devenv claude: {e.Message}");
            return 1;
        }
    }
}
