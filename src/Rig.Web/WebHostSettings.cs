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
public sealed record WebHostSettings(
    string Host,
    int Port,
    bool IsPreview,
    string TemplatesDir,
    string AssetsRoot,
    string DbPath,
    string RegistryFile,
    string WorkingDirectory)
{
    /// <summary>The production dashboard's port.</summary>
    public const int ProductionPort = 8090;

    /// <summary>The preview dashboard's port, used when <c>--preview</c> is given without a port.</summary>
    public const int PreviewPort = 8091;

    /// <summary>What <c>/api/status</c> calls the environment.</summary>
    public string Environment => IsPreview ? "PREVIEW" : "PROD";

    /// <summary>The port a launch binds: the preview port when previewing on the production default.</summary>
    public static int EffectivePort(int port, bool preview) => preview && port == ProductionPort ? PreviewPort : port;
}
