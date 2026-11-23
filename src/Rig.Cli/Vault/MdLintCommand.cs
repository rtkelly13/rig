#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.CommandLine.Parsing;
using Rig.Core;
using Rig.Core.Layout;
using Rig.Core.Processes;

namespace Rig.Cli.Vault;

/// <summary>What <c>rig md-lint</c> needs from the machine.</summary>
/// <param name="Environment">Reads an environment variable, or returns null.</param>
/// <param name="Home">The user's home folder.</param>
/// <param name="Personal"></param>
internal sealed record MdLintHost(Func<string, string?> Environment, string Home, string? Personal = null)
{
    /// <summary>The code root's <c>personal/</c> folder: the one given, else the isolated layout's under <see cref="Home"/>.</summary>
    public string PersonalDir => Personal ?? EstateLayout.Isolated(Home).Personal;
}

/// <summary>
/// <c>rig md-lint [paths...] [--fix]</c>: checks Markdown formatting with mdformat, or rewrites it with
/// <c>--fix</c>. With no paths it targets the vault (<see cref="VaultPath"/>). mdformat runs through <c>uvx</c> with
/// pinned versions of itself and its plugins (<see cref="Pinned"/>), so it needs no rig checkout: a bare mdformat
/// without the frontmatter plugin would reformat every note's YAML header. Exit codes: mdformat's own (0 formatted,
/// 1 needs formatting), 1 when the vault is missing, 2 when uvx cannot be started.
/// </summary>
internal sealed class MdLintCommand(IProc proc, MdLintHost host, TextWriter error)
{
    /// <summary>mdformat and its plugins, pinned to the versions rig formatted with from its uv.lock, so output is stable.</summary>
    private static readonly string[] Pinned = ["mdformat==1.0.0", "mdformat-frontmatter==2.0.10", "mdformat-ruff==0.1.3"];

    /// <summary>The <c>rig md-lint</c> command.</summary>
    public Command Build()
    {
        var paths = new Argument<string[]>("paths") { Description = "Files or folders to check (default: the vault).", Arity = ArgumentArity.ZeroOrMore };
        var fix = new Option<bool>("--fix") { Description = "Rewrite the files instead of checking them." };
        var command = new Command("md-lint", "Check Markdown formatting with mdformat, or fix it with --fix.") { paths, fix };
        // Anything else, an mdformat flag such as --wrap included, goes to mdformat.
        command.TreatUnmatchedTokensAsErrors = false;
        // The targets are the raw tokens in order, so an mdformat flag keeps its value beside it. --fix
        // anywhere means fix, after a `--` too, where the parser would read it as an argument.
        command.SetAction((parsed, ct) =>
        {
            var tokens = parsed.Tokens.Where(t => t.Type != TokenType.Command).Select(t => t.Value).ToList();
            return RunAsync([.. tokens.Where(t => t != "--fix")], tokens.Contains("--fix"), ct);
        });
        return command;
    }

    /// <summary>The program and arguments that run mdformat on <paramref name="targets"/>.</summary>
    public static IReadOnlyList<string> Argv(IReadOnlyList<string> targets, bool fix) =>
        ["uvx", .. Pinned.Skip(1).SelectMany(p => new[] { "--with", p }), Pinned[0], .. fix ? Array.Empty<string>() : ["--check"], .. targets];

    private async Task<int> RunAsync(IReadOnlyList<string> paths, bool fix, CancellationToken cancellationToken)
    {
        var targets = paths;
        if (targets.Count == 0)
        {
            var vault = VaultPath.Resolve(host.Environment, host.Home, host.PersonalDir);
            if (!Directory.Exists(vault) && !File.Exists(vault))
            {
                await error.WriteLineAsync($"Vault not found at {vault} — set VAULT_PATH to override.", cancellationToken).ConfigureAwait(false);
                return (int)ExitCode.Drift;
            }

            targets = [vault];
        }

        var ran = await proc.RunAsync(new ProcRequest(Argv(targets, fix)) { Attached = true }, cancellationToken).ConfigureAwait(false);
        if (ran.Code == Ran.NotFound && ran.Err.Length > 0)
        {
            await error.WriteLineAsync($"rig md-lint: {ran.Err}. mdformat runs through uvx.", cancellationToken).ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        return ran.Code;
    }
}
