using System.Diagnostics.CodeAnalysis;
using Rig.Cli.Forwarding;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli;

/// <summary>The composition root: real processes, the real console and this machine's rig checkout.</summary>
[ExcludeFromCodeCoverage(Justification = "Wiring only; RigApp and PythonRig carry the behaviour and are tested.")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var checkout = PythonRig.ResolveCheckout(Environment.GetEnvironmentVariable(PythonRig.HomeVariable), CodeRoot.Current());
        if (Forwarded.Owns(args))
        {
            // The Python CLI shares this console and handles Ctrl+C itself; rig waits for it and returns its code.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
        }

        var app = new RigApp(new PythonRig(new SystemProc(), checkout), Console.Out, Console.Error);
        return await app.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
    }
}
