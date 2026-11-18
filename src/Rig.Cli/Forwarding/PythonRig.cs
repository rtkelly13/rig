using Rig.Core.Roots;

namespace Rig.Cli.Forwarding;

/// <summary>
/// The rig checkout's Python environment, for the Python modules rig still starts (the web upstream):
/// <c>uv run --no-sync --project &lt;rig checkout&gt; python -m &lt;module&gt; …</c>.
/// </summary>
internal sealed class PythonRig(string checkout)
{
    /// <summary>The environment variable naming the rig checkout.</summary>
    public const string HomeVariable = "RIG_HOME";

    /// <summary>The rig checkout whose uv environment runs the modules.</summary>
    public string Checkout { get; } = checkout;

    /// <summary>
    /// The rig checkout: <c>RIG_HOME</c> when set, else <c>&lt;code root&gt;/personal/rig</c>, where the estate
    /// clones it.
    /// </summary>
    public static string ResolveCheckout(string? configured, string codeRoot) =>
        string.IsNullOrEmpty(configured) ? Path.Combine(codeRoot, CodeRoot.PersonalFolder, "rig") : configured;

    /// <summary>The program and arguments that run <paramref name="module"/> as a Python module in the rig environment.</summary>
    /// <param name="module">The dotted module name, such as <c>desktop_manager.web_upstream</c>.</param>
    /// <param name="args">The arguments the module's entry point reads.</param>
    public IReadOnlyList<string> Module(string module, IReadOnlyList<string> args) =>
        ["uv", "run", "--no-sync", "--project", Checkout, "python", "-m", module, .. args];
}
