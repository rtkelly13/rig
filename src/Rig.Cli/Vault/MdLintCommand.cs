using System.CommandLine;
using System.CommandLine.Parsing;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Core.Processes;

namespace Rig.Cli.Vault;

/// <summary>What <c>rig md-lint</c> needs from the machine.</summary>
/// <param name="Environment">Reads an environment variable, or returns null.</param>
/// <param name="Home">The user's home folder.</param>
internal sealed record MdLintHost(Func<string, string?> Environment, string Home);

/// <summary>
/// <c>rig md-lint [paths...] [--fix]</c>: checks Markdown formatting with mdformat, or rewrites it with
/// <c>--fix</c>. With no paths it targets the vault (<see cref="VaultPath"/>). mdformat runs through <c>uvx</c> with
/// pinned versions of itself and its plugins (<see cref="Pinned"/>), so it needs no rig checkout: a bare mdformat
/// without the frontmatter plugin would reformat every note's YAML header. Exit codes: mdformat's own (0 formatted,
/// 1 needs formatting), 1 when the vault is missing, 2 when uvx cannot be started.
/// </summary>
[CliCommand(Name = "md-lint", Description = "Check Markdown formatting with mdformat, or fix it with --fix.", TreatUnmatchedTokensAsErrors = false)]
internal sealed class MdLintCommand(IProc proc, MdLintHost host, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal MdLintCommand()
        : this(null!, null!, TextWriter.Null)
    {
    }

    /// <summary>mdformat and its plugins, pinned to the versions rig formatted with from its uv.lock, so output is stable.</summary>
    private static readonly string[] Pinned = ["mdformat==1.0.0", "mdformat-frontmatter==2.0.10", "mdformat-ruff==0.1.3"];

    /// <summary>Files or folders to check.</summary>
    [CliArgument(Name = "paths", Arity = CliArgumentArity.ZeroOrMore, Description = "Files or folders to check (default: the vault).")]
    public string[] Paths { get; set; } = [];

    /// <summary>Rewrite the files instead of checking them.</summary>
    [CliOption(Name = "--fix", Description = "Rewrite the files instead of checking them.")]
    public bool Fix { get; set; }

    /// <summary>The <c>rig md-lint</c> command.</summary>
    public Command Build() => DeclarativeCommand.Build(this);

    /// <summary>
    /// The handler. Anything else on the line, an mdformat flag such as <c>--wrap</c> included, goes to mdformat. The
    /// targets are the raw tokens in order, so an mdformat flag keeps its value beside it. <c>--fix</c> anywhere means
    /// fix, after a <c>--</c> too, where the parser would read it as an argument.
    /// </summary>
    public Task<int> RunAsync(CliContext context)
    {
        var tokens = context.Result.ParseResult.Tokens.Where(t => t.Type != TokenType.Command).Select(t => t.Value).ToList();
        return RunAsync([.. tokens.Where(t => t != "--fix")], tokens.Contains("--fix"), context.CancellationToken);
    }

    /// <summary>The program and arguments that run mdformat on <paramref name="targets"/>.</summary>
    public static IReadOnlyList<string> Argv(IReadOnlyList<string> targets, bool fix) =>
        ["uvx", .. Pinned.Skip(1).SelectMany(p => new[] { "--with", p }), Pinned[0], .. fix ? Array.Empty<string>() : ["--check"], .. targets];

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
            await error.WriteLineAsync($"rig md-lint: {ran.Err}. mdformat runs through uvx.").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        return ran.Code;
    }
}
