namespace Rig.Web.TestSupport;

/// <summary>Where the frontend sources live in this checkout, found from the test assembly.</summary>
internal static class Repo
{
    /// <summary>The checkout root: the nearest ancestor of the test output holding <c>rig.slnx</c>.</summary>
    public static string Root { get; } = Find();

    /// <summary>The SPA document, its bundle and the studio files the host serves.</summary>
    public static string Templates { get; } = Path.Join(Root, "src", "Rig.Web", "templates");

    /// <summary>The C4 viewer's static files, which also hold the vendored Pele the triage view draws Mermaid with.</summary>
    public static string C4Static { get; } = Path.Join(Root, "src", "Rig.Web", "C4", "static");

    private static string Find()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Join(folder.FullName, "rig.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("rig.slnx is not above the test output folder.");
    }
}
