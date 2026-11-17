namespace Rig.Web;

/// <summary>Where the web host listens and the files and state it serves.</summary>
/// <param name="Host">The address to bind: an IP address, or <c>localhost</c>.</param>
/// <param name="Port">The port to bind; 0 picks a free one (tests only).</param>
/// <param name="IsPreview">Whether this is the preview environment rather than production.</param>
/// <param name="TemplatesDir">The SPA document, its assets and the legacy studio files (<c>/static/</c>).</param>
/// <param name="AssetsRoot">The brand assets served read-only under <c>/media/assets/</c>.</param>
/// <param name="DbPath">The shared SQLite database.</param>
/// <param name="RegistryFile">The managed-project registry (<c>config/managed-projects.json</c>).</param>
/// <param name="WorkingDirectory">The directory relative config fallbacks are read from.</param>
/// <param name="CodeRoot">The code root: the estate graph inventories the checkouts under its <c>personal/</c>.</param>
/// <param name="EstateRoot">The personal estate folder: disk usage and trim scans measure it, agent verdicts are read under it.</param>
/// <param name="DecisionsFeed">The decisions feed the <c>estate-decisions</c> job writes (<c>decisions.json</c>).</param>
/// <param name="QueuePath">The triage queue's regenerable Markdown projection.</param>
/// <param name="TracesDir">This checkout's own <c>temp/traces/sdk</c>, one of the roots telemetry reads.</param>
/// <param name="C4StaticDir">The C4 viewer's page and static files (<c>/c4/</c>).</param>
/// <param name="C4SampleBundle">The sample C4 bundle the viewer opens first; its siblings are offered too. Empty when the checkout has no <c>experiments/</c>.</param>
public sealed record WebHostSettings(
    string Host,
    int Port,
    bool IsPreview,
    string TemplatesDir,
    string AssetsRoot,
    string DbPath,
    string RegistryFile,
    string WorkingDirectory,
    string EstateRoot,
    string DecisionsFeed,
    string QueuePath,
    string TracesDir,
    string CodeRoot = "",
    string C4StaticDir = "",
    string C4SampleBundle = "")
{
    /// <summary>The code root, falling back to the parent of EstateRoot if empty.</summary>
    public string EffectiveCodeRoot => string.IsNullOrEmpty(CodeRoot) ? (Path.GetDirectoryName(EstateRoot) ?? "") : CodeRoot;
    /// <summary>The production dashboard's port.</summary>
    public const int ProductionPort = 8090;

    /// <summary>The preview dashboard's port, used when <c>--preview</c> is given without a port.</summary>
    public const int PreviewPort = 8091;

    /// <summary>What <c>/api/status</c> calls the environment.</summary>
    public string Environment => IsPreview ? "PREVIEW" : "PROD";

    /// <summary>The port a launch binds: the preview port when previewing on the production default.</summary>
    public static int EffectivePort(int port, bool preview) => preview && port == ProductionPort ? PreviewPort : port;
}
