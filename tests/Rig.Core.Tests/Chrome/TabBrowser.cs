using Rig.Core.Chrome;

namespace Rig.Core.Tests.Chrome;

/// <summary>One tab to build: its title and flags.</summary>
internal sealed record TabSpec(
    string Title,
    bool Selected = false,
    bool Pinned = false,
    bool Loaded = false,
    bool Audible = false,
    double? Mb = null,
    string? Url = null);

/// <summary>Builds windows tab by tab; handles count from 1000 and runtime ids are unique, in creation order.</summary>
internal sealed class TabBrowser
{
    public const string Repo = "acme-corp/data-platform";
    public const string Sep = "·";

    private int _nextId = 100;

    public List<ChromeWindow> Windows { get; } = [];

    public static string PrTitle(int number, string title = "Some change", string repo = Repo) =>
        $"{title} by rtkelly13 {Sep} Pull Request #{number} {Sep} {repo}";

    public ChromeWindow Window(params TabSpec[] tabs) => Window(tabs, "Profile 4");

    public ChromeWindow Window(TabSpec[] tabs, string profile = "Profile 4", long? age = null, bool minimized = false)
    {
        var handle = 1000L + Windows.Count;
        var built = tabs.Select((spec, index) => new ChromeTab(
            new RuntimeId([42, (int)handle, ++_nextId]),
            handle,
            index,
            spec.Title,
            spec.Title,
            spec.Selected,
            spec.Pinned,
            spec.Loaded,
            spec.Audible)
        {
            MemoryMb = spec.Mb,
            Url = spec.Url is null ? null : new Uri(spec.Url),
        }).ToList();
        var window = new ChromeWindow(handle, "w", profile, null, age ?? Windows.Count, minimized, 1, built);
        Windows.Add(window);
        return window;
    }
}
