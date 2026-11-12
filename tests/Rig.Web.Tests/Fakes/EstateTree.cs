using Rig.Core.Tests;

namespace Rig.Web.Tests.Fakes;

/// <summary>
/// The temp tree the Python <c>StudioServer</c> answered from when <c>Recorded/estate.json</c> was recorded: a registry,
/// a retired list, Orca badges and an assets folder exercising every logo rank and the banner patterns.
/// </summary>
internal sealed class EstateTree : IDisposable
{
    private readonly TempDir _dir = new();

    public EstateTree()
    {
        foreach (var asset in new[]
        {
            "parquet-source-generator/production-cuts/parquet-diamond-1000x1000.png",
            "parquet-source-generator/production-cuts/parquet-og-1200x630.png",
            "fluent-assertions/fluentassertions-resultful-logo.jpg",
            "blog/blog-banner.png", "blog/blog-icon.png", "blog/blog-mark.svg", "blog/favicon.svg", "blog/.hidden-logo.svg",
            "rig/rig-avatar.png", "rig/rig-favicon.svg",
        })
        {
            File.WriteAllText(_dir.File(Path.Join("assets", asset)), "x");
        }

        Write("config/managed-projects.json", """
            {"projects": [
              {"name": "Parquet.SourceGenerator", "repo": "git@github.com:o/p.git", "description": "Gen", "domain": "Data", "stack": ".NET", "orca": {"group": "labs"}},
              {"name": "fluent-assertions", "repo": "git@github.com:o/f.git", "description": "FA", "domain": "D", "stack": "C#"},
              {"name": "blog", "repo": "git@github.com:o/b.git", "description": "Blog", "domain": "Web", "stack": "Next"},
              {"name": "rig", "repo": "git@github.com:o/r.git"},
              {"name": "no-assets", "repo": "local/x", "description": "Nothing"}
            ]}
            """);
        Write("config/archived-projects.json", """{"projects": [{"name": "old", "repo": "git@github.com:o/old.git", "description": "Old"}]}""");
        Write("config/orca.json", """{"repos": {"blog": {"color": "#ff0000", "icon": "book"}, "rig": {"icon": "wrench"}}}""");
        _dir.Folder("templates");
    }

    public string Root => _dir.Path;

    public string Assets => Path.Join(_dir.Path, "assets");

    public string Registry => Path.Join(_dir.Path, "config", "managed-projects.json");

    public string Templates => Path.Join(_dir.Path, "templates");

    public WebHostSettings Settings(bool preview = true) =>
        new("127.0.0.1", 0, preview, Templates, Assets, Path.Join(_dir.Path, "rig.db"), Registry, _dir.Path);

    public void Write(string relative, string text) => File.WriteAllText(_dir.File(relative), text);

    public void Dispose() => _dir.Dispose();
}
