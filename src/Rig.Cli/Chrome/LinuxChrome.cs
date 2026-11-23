using System.Diagnostics.CodeAnalysis;
using Rig.Core.Chrome;
using Rig.Core.Chrome.Extension;
using Rig.Core.Chrome.MacOS;

namespace Rig.Cli.Chrome;

/// <summary>The Linux Chrome driver: rig's extension in each profile, reached through its native host's socket.</summary>
[ExcludeFromCodeCoverage(Justification = "Wiring; the driver is covered with a fake bridge and the host by its relay test.")]
internal static class LinuxChrome
{
    /// <summary>How long one request to an extension may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The bridge to every running profile's extension.</summary>
    public static SocketExtensionBridge Bridge(Func<string, string?> environment, string home) =>
        new(ExtensionPlaces.SocketFolder(environment, home), Timeout);

    /// <summary>Opens the session over the extensions connected now.</summary>
    public static ChromeSession Open(Func<string, string?> environment, string home)
    {
        var chrome = new ChromeExtension(
            Bridge(environment, home),
            folder => ChromeProfiles.ReadFile(Path.Join(home, ".config", folder, "Local State")),
            TimeProvider.System);
        return new ChromeSession(
            chrome.ListWindows,
            chrome,
            chrome,
            chrome,
            new NoChromeDesktops(),
            chrome.ForegroundWindow,
            new AwayPresence(TimeProvider.System),
            static (_, _) => new Dictionary<string, string>());
    }
}
