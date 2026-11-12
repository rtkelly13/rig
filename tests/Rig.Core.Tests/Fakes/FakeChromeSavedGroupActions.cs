using Rig.Core.Chrome;

namespace Rig.Core.Tests.Fakes;

/// <summary>Bookmarks bars held in memory; a delete takes the rightmost closed copy of a title.</summary>
internal sealed class FakeChromeSavedGroupActions : IChromeSavedGroupActions
{
    /// <summary>Each window's bar, by handle, left to right.</summary>
    public Dictionary<long, List<SavedGroup>> Bars { get; } = [];

    /// <summary>The tabs each title's menu lists.</summary>
    public Dictionary<string, string[]> Tabs { get; } = [];

    /// <summary>Titles whose deletes fail, with the detail.</summary>
    public Dictionary<string, string> Refuses { get; } = [];

    /// <summary>Every delete, as <c>handle profile title</c>.</summary>
    public List<string> Log { get; } = [];

    /// <summary>Runs inside every delete.</summary>
    public Action? DuringDelete { get; set; }

    /// <summary>Gives window <paramref name="handle"/> a bar for <paramref name="profile"/>: <c>+Title</c> open, <c>-Title</c> closed.</summary>
    public FakeChromeSavedGroupActions Bar(long handle, string profile, params string[] chips)
    {
        Bars[handle] = [.. chips.Select((chip, index) => new SavedGroup(profile, chip[1..], chip[0] == '+', index))];
        return this;
    }

    public IReadOnlyList<SavedGroup> SavedGroups(long handle, string profile) =>
        Bars.TryGetValue(handle, out var bar) ? [.. bar] : [];

    public SavedDeleteOutcome DeleteSavedGroup(long handle, string profile, string title, TimeSpan? timeout = null)
    {
        Log.Add($"{handle} {profile} {title}");
        DuringDelete?.Invoke();
        if (Refuses.TryGetValue(title, out var detail))
        {
            return new SavedDeleteOutcome(false, detail, [], TimeSpan.Zero);
        }

        var bar = Bars[handle];
        bar.RemoveAt(bar.FindLastIndex(group => group.Title == title && !group.Opened));
        return new SavedDeleteOutcome(true, "deleted", Tabs.GetValueOrDefault(title, []), TimeSpan.Zero);
    }
}
