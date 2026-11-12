using System.CommandLine;

namespace Rig.Cli.Devenv;

/// <summary>One <c>devenv setup</c> step: the rig command lines it runs, and how it is confirmed.</summary>
/// <param name="Name">What the header calls it.</param>
/// <param name="Description">The line under the header.</param>
/// <param name="Question">What full mode asks before running it.</param>
/// <param name="Default">The answer express mode takes, and full mode's default.</param>
/// <param name="Lines">The rig command lines it runs, given the dry-run flag, the mode and the extra flags.</param>
internal sealed record SetupStep(string Name, string Description, string Question, bool Default, Func<SetupRun, IReadOnlyList<IReadOnlyList<string>>> Lines);

/// <summary>What one <c>devenv setup</c> run was asked for.</summary>
/// <param name="Mode"><c>full</c> asks before each step, <c>express</c> takes each step's default and <c>agent</c> runs every step without asking.</param>
/// <param name="DryRun">Whether every step only reports what it would change.</param>
/// <param name="Interactive">Whether the runtimes and tools installers may prompt.</param>
/// <param name="Home">The home folder every step sets up.</param>
internal sealed record SetupRun(string Mode, bool DryRun, bool Interactive, string Home)
{
    /// <summary><c>--dry-run</c> when this is one.</summary>
    public IEnumerable<string> DryRunFlag => DryRun ? ["--dry-run"] : [];

    /// <summary><c>--interactive</c> when the installers may prompt.</summary>
    public IEnumerable<string> InteractiveFlag => Interactive ? ["--interactive"] : [];

    /// <summary><c>--dry-run</c> when this is one, and <c>--home</c>.</summary>
    public IReadOnlyList<string> Common => [.. DryRunFlag, "--home", Home];
}

/// <summary>
/// <c>rig devenv setup</c>, the onboarding wizard, and <c>rig devenv ai</c>, its first step alone. Each step is a list of rig command lines, run in this process
/// through the root command, so a step is exactly what typing those commands does. Ported from devenv's
/// <c>cli.py</c> setup, <c>engine/runner.py</c> and <c>steps/ai_tools</c>.
/// </summary>
/// <param name="host">Where it sets up, and where prompts are answered.</param>
/// <param name="output">Where headers and progress go.</param>
/// <param name="error">Where failures go.</param>
internal sealed class SetupCommand(DevenvHost host, TextWriter output, TextWriter error)
{
    private static readonly string[] Modes = ["full", "express", "agent"];

    /// <summary>The steps in the order setup runs them, which is what <c>--step N</c> counts.</summary>
    public static readonly IReadOnlyList<SetupStep> Steps =
    [
        new(
            "AI Coding Tools & Skills",
            "Configure AI coding assistants (AGY, Claude, Codex, OpenCode) & link skills into them",
            "Would you like to configure AI coding tools and link skills into them?",
            true,
            run =>
            [
                ["devenv", "agents"],
                ["devenv", "agy", .. run.Common],
                ["devenv", "claude", .. run.Common],
                ["devenv", "opencode", .. run.Common],
                ["browser", "profiles", .. run.Common],
                ["devenv", "estate-rules", .. run.Common],
                ["devenv", "obsidian-plugins", .. run.Common],
                ["skills", "sync", "--system", "all", .. run.Common],
                ["devenv", "agent-launcher", .. run.Common],
            ]),
        new(
            "Font Installation",
            "Install developer Nerd Fonts (JetBrainsMono, CascadiaCode, FiraCode)",
            "Would you like to install Nerd Fonts?",
            true,
            run => [["devenv", "fonts", .. run.Common]]),
        new(
            "Git & SSH Setup",
            "Configure Git author info, conditional directory routing, and SSH keys",
            "Would you like to configure Git and SSH credentials?",
            true,
            run => [["devenv", "git", .. run.Common, "--mode", run.Mode]]),
        new(
            "GitHub CLI Scopes",
            "Install the personal gh scope overlay read by the workspace devenv",
            "Would you like to install the gh scope overlay?",
            true,
            run => [["devenv", "gh", .. run.Common]]),
        new(
            "Terminal Customization",
            "Configure prompt themes, shell profile integration, and terminal settings",
            "Would you like to configure terminal customization?",
            false,
            run => [["devenv", "terminal", .. run.Common]]),
        new(
            "Runtimes",
            "Configure Python (uv) and Node.js (fnm) runtime environments",
            "Would you like to configure Python and Node.js runtimes?",
            true,
            run => [["devenv", "runtimes", .. run.DryRunFlag, .. run.InteractiveFlag]]),
        new(
            "Development Tools",
            "Install and update curated developer tools & CLI utilities",
            "Would you like to install/update development tools?",
            true,
            run => [["devenv", "tools", .. run.Common, "--mode", run.Mode, .. run.InteractiveFlag]]),
    ];

    private TextReader Input => host.Input ?? TextReader.Null;

    /// <summary>The <c>setup</c> and <c>ai</c> subcommands, sharing <c>devenv</c>'s <c>--dry-run</c> and <c>--home</c>.</summary>
    public IEnumerable<Command> Build(Option<bool> dryRun, Option<string> home)
    {
        var mode = new Option<string>("--mode", "-m") { Description = "Setup mode: full, express, agent.", DefaultValueFactory = _ => "full" };
        mode.AcceptOnlyFromAmong(Modes);
        var step = new Option<int>("--step", "-s") { Description = "Start from step N.", DefaultValueFactory = _ => 1 };
        var interactive = new Option<bool>("--interactive") { Description = "Let the runtimes and tools installers prompt (winget's interactive mode)." };
        var setup = new Command("setup", "Run the development environment onboarding wizard.") { mode, step, dryRun, interactive, home };
        setup.SetAction((parsed, cancellationToken) => SetupAsync(
            parsed.RootCommandResult.Command,
            new SetupRun(parsed.GetValue(mode)!, parsed.GetValue(dryRun), parsed.GetValue(interactive), parsed.GetValue(home)!),
            parsed.GetValue(step),
            cancellationToken));
        var ai = new Command("ai", "Configure AI coding assistants (AGY, Claude Code baseline, OpenCode, browser profiles, skills).") { dryRun, home };
        ai.SetAction((parsed, cancellationToken) => AiAsync(
            parsed.RootCommandResult.Command,
            new SetupRun("full", parsed.GetValue(dryRun), false, parsed.GetValue(home)!),
            cancellationToken));
        return [setup, ai];
    }

    // The first setup step on its own, without asking.
    private async Task<int> AiAsync(Command root, SetupRun run, CancellationToken cancellationToken)
    {
        var failures = await RunStepAsync(root, Steps[0].Lines(run), cancellationToken).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            await error.WriteLineAsync($"✗ {string.Join("; ", failures)}").ConfigureAwait(false);
        }

        return failures.Count > 0 ? 1 : 0;
    }

    private async Task<int> SetupAsync(Command root, SetupRun run, int start, CancellationToken cancellationToken)
    {
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        await output.WriteLineAsync($"Starting Development Environment Setup ({platform} - {run.Mode} mode)").ConfigureAwait(false);
        var failed = false;
        for (var number = Math.Max(start, 1); number <= Steps.Count; number++)
        {
            var step = Steps[number - 1];
            await output.WriteLineAsync($"\n── Step {number}/{Steps.Count}: {step.Name} ──\n   {step.Description}").ConfigureAwait(false);
            if (!await ShouldRunAsync(step, run.Mode).ConfigureAwait(false))
            {
                await output.WriteLineAsync($"Skipped: {step.Name}").ConfigureAwait(false);
                continue;
            }

            var failures = await RunStepAsync(root, step.Lines(run), cancellationToken).ConfigureAwait(false);
            if (failures.Count == 0)
            {
                await output.WriteLineAsync($"✓ Completed: {step.Name}").ConfigureAwait(false);
                continue;
            }

            failed = true;
            await error.WriteLineAsync($"✗ Step '{step.Name}' failed: {string.Join("; ", failures)}").ConfigureAwait(false);
            // Agent mode has no one to answer: record the failure and carry on.
            if (run.Mode != "agent" && !await ConfirmAsync("Continue with remaining steps?", true).ConfigureAwait(false))
            {
                return 1;
            }
        }

        await output.WriteLineAsync(failed ? "\n! Setup finished with failed steps." : "\n✓ Setup sequence complete!").ConfigureAwait(false);
        return failed ? 1 : 0;
    }

    private Task<bool> ShouldRunAsync(SetupStep step, string mode) => mode switch
    {
        "express" => Task.FromResult(step.Default),
        "agent" => Task.FromResult(true),
        _ => ConfirmAsync(step.Question, step.Default),
    };

    // Every line runs even after one fails; the step then reports each failure.
    private async Task<List<string>> RunStepAsync(Command root, IReadOnlyList<IReadOnlyList<string>> lines, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var line in lines)
        {
            var code = await root.Parse(line).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken).ConfigureAwait(false);
            if (code != 0)
            {
                failures.Add($"rig {string.Join(' ', line.TakeWhile(word => !word.StartsWith('-')))} exited {code}");
            }
        }

        return failures;
    }

    private async Task<bool> ConfirmAsync(string question, bool fallback)
    {
        await output.WriteAsync($"{question} [{(fallback ? "Y/n" : "y/N")}]: ").ConfigureAwait(false);
        var answer = (await Input.ReadLineAsync().ConfigureAwait(false))?.Trim();
        return string.IsNullOrEmpty(answer) ? fallback : answer.StartsWith('y') || answer.StartsWith('Y');
    }
}
