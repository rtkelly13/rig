using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Rig.Core.Chrome;
using Rig.Core.Chrome.MacOS;
using Rig.Core.Processes;

namespace Rig.Cli.Chrome;

/// <summary>The macOS Chrome driver: its scripting dictionary and accessibility tree, with Spaces left unaddressed.</summary>
[SupportedOSPlatform("macos")]
[ExcludeFromCodeCoverage(Justification = "Wiring of native readers; the readers and actions are covered by their fakes.")]
internal static class MacOSChrome
{
    /// <summary>Opens the native session over <paramref name="home"/>'s Chrome profiles.</summary>
    public static ChromeSession Open(string home)
    {
        var proc = new SystemProc();
        var chrome = new ChromeMac(
            new ChromeJxa(proc),
            static () => new NativeAxApi(),
            () => ChromeProfiles.Read(home),
            TimeProvider.System);
        return new ChromeSession(
            chrome.ListWindows,
            chrome,
            chrome,
            chrome,
            new NoChromeDesktops(),
            chrome.ForegroundWindow,
            new MacPresence(),
            static (_, _) => new Dictionary<string, string>());
    }
}
