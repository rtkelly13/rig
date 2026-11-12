using Rig.Core.Desktop;

namespace Rig.Core.Tests.Fakes;

/// <summary>Virtual desktops held in memory, each with an id so tests can see which one a number named.</summary>
public sealed class FakeVirtualDesktops : IVirtualDesktops
{
    private int _nextId;

    public FakeVirtualDesktops(params string[] names)
    {
        foreach (var name in names)
        {
            Desktops.Add(new FakeVirtualDesktop(++_nextId, name));
        }

        Current = Desktops.FirstOrDefault();
    }

    public List<FakeVirtualDesktop> Desktops { get; } = [];

    public FakeVirtualDesktop? Current { get; private set; }

    /// <summary>Which desktop each window is on, by handle.</summary>
    public Dictionary<long, FakeVirtualDesktop> Windows { get; } = [];

    /// <summary>Every removal made, as (removed id, fallback id).</summary>
    public List<(int Removed, int Fallback)> Removals { get; } = [];

    /// <summary>The call that throws next, by name, then stops throwing.</summary>
    public string? FailOn { get; set; }

    /// <summary>A rename that reports success but leaves the name as it was.</summary>
    public bool IgnoreRenames { get; set; }

    public int Count => Desktops.Count;

    public int CurrentNumber
    {
        get
        {
            Fail(nameof(CurrentNumber));
            return Current is null ? 0 : Desktops.IndexOf(Current) + 1;
        }
    }

    public IReadOnlyList<string> Names()
    {
        Fail(nameof(Names));
        return [.. Desktops.Select(desktop => desktop.Name)];
    }

    public void SwitchTo(int number)
    {
        Fail(nameof(SwitchTo));
        Current = At(number);
    }

    public void Create()
    {
        Fail(nameof(Create));
        Desktops.Add(new FakeVirtualDesktop(++_nextId, ""));
    }

    public void Rename(int number, string name)
    {
        Fail(nameof(Rename));
        var desktop = At(number);
        if (!IgnoreRenames)
        {
            desktop.Name = name;
        }
    }

    public void Remove(int number, int fallbackNumber)
    {
        Fail(nameof(Remove));
        var removed = At(number);
        var fallback = At(fallbackNumber);
        if (removed == fallback)
        {
            throw new DesktopException("a desktop cannot be its own fallback");
        }

        foreach (var handle in Windows.Where(pair => pair.Value == removed).Select(pair => pair.Key).ToList())
        {
            Windows[handle] = fallback;
        }

        if (Current == removed)
        {
            Current = fallback;
        }

        Removals.Add((removed.Id, fallback.Id));
        _ = Desktops.Remove(removed);
    }

    public void MoveWindow(long handle, int number)
    {
        Fail(nameof(MoveWindow));
        Windows[handle] = At(number);
    }

    public bool IsOnCurrentDesktop(long handle) => !Windows.TryGetValue(handle, out var desktop) || desktop == Current;

    public int? DesktopNumberOf(long handle) => Windows.TryGetValue(handle, out var desktop) ? Desktops.IndexOf(desktop) + 1 : null;

    private FakeVirtualDesktop At(int number) =>
        number >= 1 && number <= Desktops.Count ? Desktops[number - 1] : throw new DesktopException($"there is no desktop {number}");

    private void Fail(string call)
    {
        if (FailOn == call)
        {
            FailOn = null;
            throw new DesktopException($"{call} failed");
        }
    }
}

/// <summary>One fake desktop: an id that survives renumbering, and a name.</summary>
public sealed class FakeVirtualDesktop(int id, string name)
{
    public int Id { get; } = id;

    public string Name { get; set; } = name;
}
