using Rig.Core.Devenv;

namespace Rig.Cli.Devenv;

/// <summary>
/// The "gh extension releases" section that <c>rig devenv gh --check --outdated</c> and
/// <c>rig devenv tools --check --outdated</c> both print: each installed extension against its latest release.
/// </summary>
internal static class GhExtensionReleaseReport
{
    /// <summary>Writes the section header.</summary>
    public static Task HeaderAsync(TextWriter output, CancellationToken cancellationToken) =>
        output.WriteLineAsync("gh extension releases:", cancellationToken);

    /// <summary>
    /// Writes one row per installed extension and a summary. A lookup that fails is a "?" row, never up to date.
    /// Informational: it decides no exit code.
    /// </summary>
    public static async Task RowsAsync(TextWriter output, GhExtensionManager extensions, IReadOnlyList<InstalledGhExtension> installed, CancellationToken cancellationToken)
    {
        if (installed.Count == 0)
        {
            await output.WriteLineAsync("  - No gh extensions installed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var releases = await extensions.ReleasesAsync(installed, cancellationToken).ConfigureAwait(false);
        foreach (var release in releases)
        {
            var row = release switch
            {
                { Problem: { } problem } => $"? {problem}",
                { Outdated: true } => $"! {release.Extension.Version} -> {release.Latest}",
                _ => $"✓ {release.Extension.Version} (latest)",
            };
            await output.WriteLineAsync($"  {release.Extension.Repo,-28} {row}", cancellationToken).ConfigureAwait(false);
        }

        var stale = releases.Count(release => release.Outdated);
        await output.WriteLineAsync(
            stale > 0 ? $"  {stale} gh extension(s) can be upgraded. Run 'rig devenv update' to upgrade them." : "  ✓ No gh extension upgrades found.",
            cancellationToken).ConfigureAwait(false);
    }
}
