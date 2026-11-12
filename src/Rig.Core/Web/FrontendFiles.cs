namespace Rig.Core.Web;

/// <summary>When the served frontend last changed, for live reload: every watched file, and the stylesheets alone.</summary>
/// <param name="Overall">The newest modification time of any watched file, in Unix seconds; 0 when none exists.</param>
/// <param name="Css">The newest modification time of a watched stylesheet, in Unix seconds; 0 when none exists.</param>
public sealed record ChangeTimestamps(double Overall, double Css);

/// <summary>The bounded set of frontend files live reload watches: the compiled SPA and the legacy studio.</summary>
public static class FrontendFiles
{
    /// <summary>The watched files, relative to the templates folder.</summary>
    public static readonly IReadOnlyList<string> Watched =
        ["index.html", "studio.js", "studio.css", "app_assets/app.js", "app_assets/app.css"];

    /// <summary>The newest modification times under <paramref name="templates"/>; a missing file is skipped.</summary>
    public static ChangeTimestamps Changed(string templates)
    {
        double overall = 0, css = 0;
        foreach (var relative in Watched)
        {
            var file = new FileInfo(Path.Join(templates, relative));
            if (!file.Exists)
            {
                continue;
            }

            var seconds = (file.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
            overall = Math.Max(overall, seconds);
            if (file.Extension == ".css")
            {
                css = Math.Max(css, seconds);
            }
        }

        return new(overall, css);
    }
}
