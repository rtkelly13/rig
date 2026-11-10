using System.Diagnostics.CodeAnalysis;
using Rig.Cli.Forwarding;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Roots;

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
        var app = new RigApp(
            new PythonRig(proc, checkout, fromPython),
            new GhApi(proc),
            async _ => RepoSlug.FromRemote(await git.OutAsync(null, "remote", "get-url", "origin").ConfigureAwait(false)),
            Console.Out,
            Console.Error);
        return await app.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
    }
}
