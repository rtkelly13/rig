using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Forwarding;

/// <summary>
/// The Python rig CLI that every command not yet ported runs on. It is started the way the generated
/// <c>rig.sh</c> shim starts it: <c>uv run --no-sync --project &lt;rig checkout&gt; rig …</c>, attached to this
/// console so prompts, colour and the agent launcher's terminal all behave as before.
/// </summary>
internal sealed class PythonRig(IProc proc, string checkout, bool fromPython = false)
{
    /// <summary>
    /// Set by the Python CLI when it hands a ported command to C#. A line arriving with it set must not go back to
    /// Python: the two sides disagree on who owns the command, and forwarding again would loop.
    /// </summary>
    public const string BridgeVariable = "RIG_FROM_PYTHON";

    /// <summary>The environment variable naming the rig checkout the Python CLI runs from.</summary>
    public const string HomeVariable = "RIG_HOME";

    /// <summary>The rig checkout the Python CLI runs from.</summary>
    public string Checkout { get; } = checkout;

    /// <summary>
    /// The rig checkout: <c>RIG_HOME</c> when set, else <c>&lt;code root&gt;/personal/rig</c>, where the estate
    /// clones it.
    /// </summary>
    public static string ResolveCheckout(string? configured, string codeRoot) =>
        string.IsNullOrEmpty(configured) ? Path.Combine(codeRoot, CodeRoot.PersonalFolder, "rig") : configured;

    /// <summary>The program and arguments that run <paramref name="args"/> on the Python CLI.</summary>
    public IReadOnlyList<string> Argv(IReadOnlyList<string> args) =>
        ["uv", "run", "--no-sync", "--project", Checkout, "rig", .. args];

    /// <summary>Runs <paramref name="args"/> on the Python CLI and returns its exit code.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (fromPython)
        {
            var name = args.Count > 0 ? args[0] : "rig";
            await error.WriteLineAsync($"rig: the Python CLI handed '{name}' to C#, which hands it back to Python; refusing to loop. "
                + "PORTED in src/rig/rig_cli.py and Forwarded in src/Rig.Cli disagree.").ConfigureAwait(false);
            return (int)Core.ExitCode.Error;
        }


        // Never cancelled: Ctrl+C reaches the child through the shared console, and rig exits with its code.
        var ran = await proc.RunAsync(new ProcRequest(Argv(args)) { Attached = true }, CancellationToken.None).ConfigureAwait(false);
        if (ran.Code == Ran.NotFound && ran.Err.Length > 0)
        {
            await error.WriteLineAsync($"rig: {ran.Err}. The commands not yet in C# run on the Python CLI through uv.").ConfigureAwait(false);
            return (int)Core.ExitCode.Error;
        }

        return ran.Code;
    }
}
