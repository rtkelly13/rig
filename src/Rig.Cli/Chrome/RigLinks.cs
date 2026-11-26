using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Rig.Core.Chrome;
using Rig.Core.Desktop;
using Rig.Core.Workspace;

namespace Rig.Cli.Chrome;

/// <summary>
/// The pages rig opens, routed into the "Rig links" Chrome window on Windows (<see cref="LinkRouter"/>) and handed to
/// the default browser everywhere else, or when routing fails.
/// </summary>
/// <param name="route">
/// Opens a page in the companion window; built on first use, since it reads the desktop plan, and null where there is
/// no Chrome driver.
/// </param>
/// <param name="fallback">The default browser.</param>
/// <param name="log">Where a routing failure is reported.</param>
internal sealed class RigLinks(Func<Func<Uri, LinkOutcome>?> route, Action<Uri> fallback, TextWriter log)
{
    private readonly Lazy<Func<Uri, LinkOutcome>?> _route = new(route);

    /// <summary>The desktop-plan alias naming the profile a new companion window opens with.</summary>
    public const string ProfileAlias = "chrome-profile-personal";

    /// <summary>
    /// Opens <paramref name="page"/>: in the companion window when it is a web page and routing works, otherwise in the
    /// default browser. A page Chrome opened but the router could not place is not opened again. Returns how it went.
    /// </summary>
    public LinkOutcome Open(Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (LinkRouter.IsWebPage(page) && _route.Value is { } routeTo)
        {
            try
            {
                var outcome = routeTo(page);
                if (!outcome.Landed)
                {
                    log.WriteLine($"rig links: {outcome.Detail}");
                }

                return outcome;
            }
            catch (Exception e) when (e is ChromeReadException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                log.WriteLine($"rig links: could not route {page}: {e.Message}; using the default browser.");
            }
        }

        fallback(page);
        return new LinkOutcome(false, "opened in the default browser");
    }

    /// <summary>
    /// This machine's router: on Windows, Chrome and its profile from the desktop plan's aliases
    /// (<c>chrome-path</c>, <c>chrome-user-data</c>, <see cref="ProfileAlias"/>) with <c>tools.desktop.aliases</c>
    /// applied; null elsewhere, or when the plan cannot name them.
    /// </summary>
    /// <param name="plans">The desktop plans to try, in order; the first that exists is used.</param>
    /// <param name="machineConfig">The machine's <c>~/.workspace/config.json</c>.</param>
    /// <param name="environment">Reads environment variables.</param>
    [ExcludeFromCodeCoverage(Justification = "Wiring of the native Chrome session and process launch.")]
    public static Func<Uri, LinkOutcome>? ForThisMachine(IEnumerable<string> plans, string machineConfig, Func<string, string?> environment)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8, 1) || plans.FirstOrDefault(File.Exists) is not { } planPath)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> values;
        try
        {
            values = PlanFiles.Load(planPath).Substitutions(Placeholders.EnvironmentAliases(environment), MachineConfig.DesktopAliases(machineConfig));
        }
        catch (Exception e) when (e is InvalidPlanException or IOException)
        {
            return null;
        }

        if (values.GetValueOrDefault("chrome-path") is not { Length: > 0 } chrome
            || values.GetValueOrDefault("chrome-user-data") is not { Length: > 0 } userData
            || values.GetValueOrDefault(ProfileAlias) is not { Length: > 0 } profile)
        {
            return null;
        }

        void Launch(IReadOnlyList<string> args)
        {
            var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
            start.ArgumentList.Add($"--user-data-dir={userData}");
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            Process.Start(start)?.Dispose();
        }

        return page =>
        {
            var session = OperatingSystem.IsWindowsVersionAtLeast(8, 1) ? WindowsChrome.Open() : throw new PlatformNotSupportedException("Routing needs the Windows Chrome driver.");
            return new LinkRouter(session.ReadWindows, session.Grouping, Launch, Thread.Sleep).Open(page, profile);
        };
    }
}
