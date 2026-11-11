using System.Diagnostics.CodeAnalysis;
using Rig.Cli.Forwarding;
using Rig.Cli.Latex;
using Rig.Cli.Vault;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Services;
using Rig.Core.Vault;

namespace Rig.Cli;

/// <summary>The composition root: real processes, the real console and this machine's rig checkout.</summary>
[ExcludeFromCodeCoverage(Justification = "Wiring only; RigApp and PythonRig carry the behaviour and are tested.")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var codeRoot = CodeRoot.Current();
        var checkout = PythonRig.ResolveCheckout(Environment.GetEnvironmentVariable(PythonRig.HomeVariable), codeRoot);
        if (Forwarded.Owns(args))
        {
            // The Python CLI shares this console and handles Ctrl+C itself; rig waits for it and returns its code.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
        }

        var proc = new SystemProc();
        var git = new Git(proc, codeRoot);
        var fromPython = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PythonRig.BridgeVariable));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var macOs = OperatingSystem.IsMacOS();
        // Created per command; HttpClient opens no connection until the health probe asks for one.
        using var http = new HttpClient();
        var app = new RigApp(
            new PythonRig(proc, checkout, fromPython),
            new GhApi(proc),
            async _ => RepoSlug.FromRemote(await git.OutAsync(null, "remote", "get-url", "origin").ConfigureAwait(false)),
            Console.Out,
            Console.Error,
            [
                new LatexCommand(
                    proc,
                    new LatexHost(ProgramPath.Find, Environment.GetEnvironmentVariable("PATH") ?? string.Empty, codeRoot, Environment.CurrentDirectory),
                    Console.Out,
                    Console.Error).Build,
                new MdLintCommand(
                    proc,
                    new MdLintHost(checkout, Environment.GetEnvironmentVariable, home),
                    Console.Error).Build,
                new VaultCommand(
                    proc,
                    new VaultHost(Environment.GetEnvironmentVariable, home),
                    new KnowledgeServerCommand(
                        proc,
                        async ct => new KnowledgeServer(
                            proc,
                            new Launchd(proc, Launchd.AgentsDirUnder(home), Launchd.GuiDomain(macOs ? await KnowledgeServerCommand.UserIdAsync(proc, ct).ConfigureAwait(false) : 0)),
                            new KnowledgeServerPlaces(home, AppData.Current()),
                            ProgramPath.Find,
                            http),
                        macOs,
                        ProgramPath.Find,
                        Console.Out,
                        Console.Error),
                    Console.Out,
                    Console.Error).Build,
            ]);
        return await app.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
    }
}
