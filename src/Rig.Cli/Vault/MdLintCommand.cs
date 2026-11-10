using System.CommandLine;
using System.CommandLine.Parsing;
using Rig.Core;
using Rig.Core.Processes;

namespace Rig.Cli.Vault;

/// <summary>What <c>rig md-lint</c> needs from the machine.</summary>
/// <param name="Checkout">The rig checkout whose uv environment holds mdformat and its plugins.</param>
/// <param name="Environment">Reads an environment variable, or returns null.</param>
/// <param name="Home">The user's home folder.</param>
internal sealed record MdLintHost(string Checkout, Func<string, string?> Environment, string Home);

/// <summary>
/// <c>rig md-lint [paths...] [--fix]</c>: checks Markdown formatting with mdformat, or rewrites it with
/// <c>--fix</c>. With no paths it targets the vault (<see cref="VaultPath"/>). mdformat runs from rig's own uv
/// environment, because the frontmatter and ruff plugins live there: a bare mdformat without the frontmatter plugin
/// would reformat every note's YAML header. Exit codes: mdformat's own (0 formatted, 1 needs formatting), 1 when the
/// vault is missing, 2 when uv cannot be started.
/// </summary>
internal sealed class MdLintCommand(IProc proc, MdLintHost host, TextWriter error)
{
    /// <summary>The <c>rig md-lint</c> command.</summary>
    public Command Build()
    {
        var paths = new Argument<string[]>("paths") { Description = "Files or folders to check (default: the vault).", Arity = ArgumentArity.ZeroOrMore };
        var fix = new Option<bool>("--fix") { Description = "Rewrite the files instead of checking them." };
        var command = new Command("md-lint", "Check Markdown formatting with mdformat, or fix it with --fix.") { paths, fix };
        // Anything else, an mdformat flag such as --wrap included, goes to mdformat as it did from Python.
        command.TreatUnmatchedTokensAsErrors = false;
        // The targets are the raw tokens in order, so an mdformat flag keeps its value beside it.
        command.SetAction((parsed, ct) => RunAsync(
            [.. parsed.Tokens.Where(t => t.Type != TokenType.Command && t.Value != "--fix").Select(t => t.Value)], parsed.GetValue(fix), ct));
        return command;
    }

    /// <summary>The program and arguments that run mdformat on <paramref name="targets"/>.</summary>
    public IReadOnlyList<string> Argv(IReadOnlyList<string> targets, bool fix) =>
        ["uv", "run", "--no-sync", "--project", host.Checkout, "mdformat", .. fix ? Array.Empty<string>() : ["--check"], .. targets];

    private async Task<int> RunAsync(IReadOnlyList<string> paths, bool fix, CancellationToken cancellationToken)
    {
        var targets = paths;
        if (targets.Count == 0)
        {
            var vault = VaultPath.Resolve(host.Environment, host.Home);
            if (!Directory.Exists(vault) && !File.Exists(vault))
            {
                await error.WriteLineAsync($"Vault not found at {vault} — set VAULT_PATH to override.").ConfigureAwait(false);
                return (int)ExitCode.Drift;
            }

            targets = [vault];
        }

        var ran = await proc.RunAsync(new ProcRequest(Argv(targets, fix)) { Attached = true }, cancellationToken).ConfigureAwait(false);
        if (ran.Code == Ran.NotFound && ran.Err.Length > 0)
        {
            await error.WriteLineAsync($"rig md-lint: {ran.Err}. mdformat runs from rig's uv environment.").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        return ran.Code;
    }
}
