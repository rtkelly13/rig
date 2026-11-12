using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;
using Rig.Core.Vault;

namespace Rig.Core.Chrome.Windows;

/// <summary>
/// Acts on Chrome's tabs and saved groups through UI Automation: select a tab, close one, put each window's selection
/// back, delete a duplicate saved group from the bookmarks bar, and (with the window in front, in
/// <c>ChromeUiaActions.Grouping.cs</c>) group, move and ungroup tabs. Ported from <c>desktop_manager.chrome.uia</c>;
/// callers decide what to act on, this only knows how to do it safely.
/// </summary>
/// <remarks>
/// <para>
/// Closing is select-then-invoke: with many tabs Chrome hides the close button of every inactive tab, so a tab is
/// selected to expose its <c>TabCloseButton</c>, which is pressed only while the tab is still selected. Identity is the
/// runtime id, and the title is re-checked before acting.
/// </para>
/// <para>
/// Closing a group's last tab makes Chrome ask "Close tab and delete group?" in a bubble hosted in a child window of
/// the browser window, not under its root element; it is found through the child windows and confirmed.
/// </para>
/// <para>
/// A saved-group chip's menu opens through <c>ShowContextMenu</c>, even on a background window. A closed chip is never
/// invoked: that reopens the group with its old tabs.
/// </para>
/// </remarks>
public sealed partial class ChromeUiaActions : IChromeTabActions
{
    /// <summary>The accessible names Chrome gives a tab's close button. English only.</summary>
    public static readonly IReadOnlySet<string> CloseButtonNames = new HashSet<string>(StringComparer.Ordinal) { "Close" };

    private const string CloseButtonClass = "TabCloseButton";
    private const string DialogRootClass = "RootView";
    private const string DeleteGroupQuestion = "Close tab and delete group?";
    private const string DeleteGroupButton = "Delete group";
    private const string SavedBarClass = "SavedTabGroupBar";
    private const string SavedChipClass = "SavedTabGroupButton";
    private const string TabsInGroupItem = "tabs in group";
    private const ushort EscapeKey = 0x1B;

    private static readonly UiaCondition CloseButtonCondition = new(UiaControlType.Button, CloseButtonClass);
    private static readonly HashSet<string> SavedConfirmNames = new(StringComparer.Ordinal) { "delete group", "delete" };

    private readonly ChromeUiaReader _reader;
    private readonly IUiAutomation _uia;
    private readonly IWin32Windows _win32;
    private readonly IWin32Actions _input;
    private readonly TimeProvider _clock;
    private readonly Action<TimeSpan> _sleep;

    /// <summary>Creates the actions over a reader of the same session: one per command.</summary>
    public ChromeUiaActions(
        ChromeUiaReader reader,
        IUiAutomation uia,
        IWin32Windows win32,
        IWin32Actions input,
        TimeProvider? clock = null,
        Action<TimeSpan>? sleep = null)
    {
        _reader = reader;
        _uia = uia;
        _win32 = win32;
        _input = input;
        _clock = clock ?? TimeProvider.System;
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>The pause between probes while waiting on Chrome.</summary>
    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a confirm dialog stays up before it is pressed, and between presses.</summary>
    public static TimeSpan DialogSettle { get; } = TimeSpan.FromMilliseconds(800);

    /// <summary>How long a deleted saved group gets to leave the bar: Chrome takes seconds to drop its chip.</summary>
    public static TimeSpan SavedDeleteTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>The default wait for one step of an action.</summary>
    public static TimeSpan StepTimeout { get; } = TimeSpan.FromSeconds(3);

    /// <summary>Selects the tab; false when it has gone.</summary>
    public bool SelectTab(ChromeTab tab)
    {
        if (_reader.FindTabElement(tab) is not { } element)
        {
            return false;
        }

        element.SelectItem();
        return true;
    }

    /// <inheritdoc/>
    CloseOutcome IChromeTabActions.CloseTab(ChromeTab tab) => CloseTab(tab);

    /// <summary>
    /// Selects the tab, presses its close button and waits for it to leave the strip. Refuses, touching nothing, when
    /// the tab has gone, its title no longer matches what the caller saw, or the close button does not appear.
    /// Selecting a discarded tab makes Chrome start reloading it before it closes.
    /// </summary>
    public CloseOutcome CloseTab(ChromeTab tab, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var wait = timeout ?? StepTimeout;
        var started = _clock.GetTimestamp();
        CloseOutcome Outcome(bool closed, string detail) => new(closed, detail, _clock.GetElapsedTime(started));

        if (_reader.FindTabElement(tab) is not { } element)
        {
            return Outcome(false, "tab no longer present");
        }

        var currentTitle = TabNames.Parse(element.Name).Title;
        if (currentTitle != tab.Title)
        {
            return Outcome(false, $"title changed to {PyText.Repr(currentTitle)}");
        }

        element.SelectItem();
        if (Poll(() => CloseButton(element), wait) is not { } button)
        {
            return Outcome(false, "close button did not appear after selecting");
        }

        if (!element.IsSelected)
        {
            return Outcome(false, "tab lost selection before close");
        }

        if (!CloseButtonNames.Contains(button.Name))
        {
            return Outcome(false, $"unexpected close button name {PyText.Repr(button.Name)}");
        }

        var grouped = ChromeUiaReader.GroupOf(element) is not null;
        button.Invoke();
        var gone = PollUntil(
            () =>
            {
                if (_reader.FindTabElement(tab) is null)
                {
                    return true;
                }

                if (grouped)
                {
                    ConfirmGroupDelete(tab.WindowHandle);
                }

                return false;
            },
            wait);
        return Outcome(gone, gone ? "closed" : "invoked close but the tab is still present");
    }

    /// <summary>
    /// Re-selects each window's originally selected tab, or its fallback when that tab closed. Answers the tab re-selected
    /// (or null) per window; a window whose selected tab closed with no fallback keeps whichever neighbour Chrome picked.
    /// </summary>
    public IReadOnlyDictionary<long, ChromeTab?> RestoreSelection(
        IEnumerable<ChromeWindow> before,
        IReadOnlyDictionary<long, ChromeTab>? fallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        var restored = new Dictionary<long, ChromeTab?>();
        foreach (var window in before)
        {
            ChromeTab?[] candidates = [window.SelectedTab, fallbacks?.GetValueOrDefault(window.Handle)];
            restored[window.Handle] = candidates.OfType<ChromeTab>().FirstOrDefault(SelectTab);
        }

        return restored;
    }

    /// <summary>
    /// The saved-group chips on the window's bookmarks bar, left to right. Empty when the bar is hidden; chips pushed into
    /// the overflow are not read, because it opens only on a foreground window.
    /// </summary>
    public IReadOnlyList<SavedGroup> SavedGroups(long handle, string profile) =>
        [.. SavedChips(handle, profile).Select(chip => chip.Group)];

    /// <summary>
    /// Deletes the rightmost closed saved group titled <paramref name="title"/> from the window's bar. Refuses, touching
    /// nothing, unless the bar still shows another saved copy of the title, so the last copy of a group is never deleted.
    /// Records the tabs the chip's menu lists, picks Delete group, and presses a confirmation once it has settled.
    /// </summary>
    public SavedDeleteOutcome DeleteSavedGroup(long handle, string profile, string title, TimeSpan? timeout = null)
    {
        var wait = timeout ?? StepTimeout;
        var started = _clock.GetTimestamp();
        IReadOnlyList<string> tabs = [];
        SavedDeleteOutcome Outcome(bool deleted, string detail) =>
            new(deleted, detail, tabs, _clock.GetElapsedTime(started));
        List<SavedChip> Copies() => [.. SavedChips(handle, profile).Where(chip => chip.Group.Title == title)];

        var before = Copies();
        var closed = before.Where(chip => !chip.Group.Opened).ToList();
        if (closed.Count == 0)
        {
            return Outcome(false, $"no closed saved group called {PyText.Repr(title)} on the bar");
        }

        if (before.Count < 2)
        {
            return Outcome(false, $"the only saved copy of {PyText.Repr(title)}; kept");
        }

        var pid = _win32.ProcessIdOf(handle);
        if (!MenusClosed(pid, wait))
        {
            return Outcome(false, "a menu from the previous step is still open");
        }

        closed[^1].Element.ShowContextMenu();
        var found = Poll(() => MenuItem(pid, name => name == "delete group"), wait);
        var items = PopupElements(pid, UiaControlType.MenuItem).Select(item => PyText.Strip(item.Name)).ToList();
        var lowered = items.Select(PyText.Lower).ToList();
        var listed = lowered.IndexOf(TabsInGroupItem);
        if (listed >= 0)
        {
            tabs = [.. items.Skip(listed + 1).Where(item => item.Length > 0)];
        }

        if (found is null || !lowered.Contains("open group"))
        {
            PressEscape();
            return Outcome(false, "the chip's menu did not offer Delete group");
        }

        found.Invoke();
        long? seenAt = null;
        long? pressedAt = null;
        var gone = PollUntil(
            () =>
            {
                if (Copies().Count < before.Count)
                {
                    return true;
                }

                if (ConfirmButton(handle, SavedConfirmNames) is not { } confirm)
                {
                    return false;
                }

                var now = _clock.GetTimestamp();
                seenAt ??= now;
                if (_clock.GetElapsedTime(seenAt.Value, now) >= DialogSettle
                    && (pressedAt is null || _clock.GetElapsedTime(pressedAt.Value, now) >= DialogSettle))
                {
                    pressedAt = now;
                    try
                    {
                        confirm.Invoke();
                    }
                    catch (UiaException)
                    {
                        // The dialog went as it was pressed; the next probe sees whether the chip went with it.
                    }
                }

                return false;
            },
            SavedDeleteTimeout);
        if (!gone)
        {
            if (PopupElements(pid, UiaControlType.MenuItem).Count > 0)
            {
                PressEscape();
            }

            return Outcome(false, $"saved group {PyText.Repr(title)} is still on the bar");
        }

        return Outcome(true, "deleted");
    }

    /// <summary>
    /// Elements of one control type in the process's popups (menus, bubbles): its visible top-level windows that are not
    /// titled browser windows.
    /// </summary>
    public IReadOnlyList<IUiaElement> PopupElements(int pid, UiaControlType type)
    {
        var found = new List<IUiaElement>();
        foreach (var handle in _win32.TopLevelWindows())
        {
            var ours = _win32.IsVisible(handle) && _win32.ProcessIdOf(handle) == pid;
            var browser = _win32.ClassName(handle) == ChromeUiaReader.WindowClass
                && PyText.Strip(_win32.Title(handle) ?? string.Empty).Length > 0;
            if (!ours || browser)
            {
                continue;
            }

            try
            {
                if (_uia.ElementFromHandle(handle) is { } root)
                {
                    found.AddRange(root.FindAll(UiaScope.Subtree, new UiaCondition(type)));
                }
            }
            catch (UiaException)
            {
                // The popup closed while it was read.
            }
        }

        return found;
    }

    /// <summary>The first open menu item whose stripped, lower-cased name is non-empty and matches.</summary>
    public IUiaElement? MenuItem(int pid, Func<string, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return PopupElements(pid, UiaControlType.MenuItem).FirstOrDefault(item =>
        {
            var name = PyText.Lower(PyText.Strip(item.Name));
            return name.Length > 0 && matches(name);
        });
    }

    /// <summary>
    /// A button named one of <paramref name="names"/> (stripped, lower-cased) in a dialog over the window, which Chrome
    /// hosts in the window's own tree or in a child window of it.
    /// </summary>
    public IUiaElement? ConfirmButton(long handle, IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var host in (IEnumerable<long>)[handle, .. _win32.ChildWindows(handle)])
        {
            try
            {
                if (_uia.ElementFromHandle(host)?.FindFirst(UiaScope.Descendants, new UiaCondition(UiaControlType.Window)) is not { } dialog)
                {
                    continue;
                }

                var button = dialog.FindAll(UiaScope.Descendants, new UiaCondition(UiaControlType.Button))
                    .FirstOrDefault(button => names.Contains(PyText.Lower(PyText.Strip(button.Name))));
                if (button is not null)
                {
                    return button;
                }
            }
            catch (UiaException)
            {
                // The host went while it was read.
            }
        }

        return null;
    }

    /// <summary>Waits until the process shows no menu, as after a pick or an Escape.</summary>
    public bool MenusClosed(int pid, TimeSpan timeout) =>
        PollUntil(() => PopupElements(pid, UiaControlType.MenuItem).Count == 0, timeout);

    /// <summary>Presses Escape, which closes an open menu or bubble.</summary>
    public void PressEscape() => _ = _input.SendKeys(KeyEvent.Tap(EscapeKey));

    /// <summary><paramref name="probe"/> until it answers, or null once <paramref name="timeout"/> has passed.</summary>
    public T? Poll<T>(Func<T?> probe, TimeSpan timeout)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        var started = _clock.GetTimestamp();
        while (true)
        {
            if (probe() is { } result)
            {
                return result;
            }

            if (_clock.GetElapsedTime(started) >= timeout)
            {
                return null;
            }

            _sleep(PollInterval);
        }
    }

    /// <summary><paramref name="probe"/> until it holds; false once <paramref name="timeout"/> has passed.</summary>
    public bool PollUntil(Func<bool> probe, TimeSpan timeout) =>
        Poll(() => probe() ? string.Empty : null, timeout) is not null;

    private static IUiaElement? CloseButton(IUiaElement tab)
    {
        try
        {
            return tab.FindFirst(UiaScope.Children, CloseButtonCondition);
        }
        catch (UiaException)
        {
            return null;
        }
    }

    /// <summary>Closing a group's last tab asks first, in a bubble of its own; the group would be left empty, so confirm.</summary>
    private void ConfirmGroupDelete(long handle)
    {
        foreach (var host in _win32.ChildWindows(handle))
        {
            try
            {
                if (_uia.ElementFromHandle(host)?.FindFirst(UiaScope.Children, new UiaCondition(ClassName: DialogRootClass)) is not { } dialog)
                {
                    continue;
                }

                var question = dialog.FindFirst(UiaScope.Descendants, new UiaCondition(Name: DeleteGroupQuestion));
                var button = dialog.FindFirst(UiaScope.Descendants, new UiaCondition(UiaControlType.Button, Name: DeleteGroupButton));
                if (question is not null && button is not null)
                {
                    button.Invoke();
                    return;
                }
            }
            catch (UiaException)
            {
                // The host went while it was read.
            }
        }
    }

    private List<SavedChip> SavedChips(long handle, string profile)
    {
        IUiaElement? bar;
        try
        {
            bar = _uia.ElementFromHandle(handle) is { } root ? ChromeUiaReader.FindView(root, SavedBarClass) : null;
        }
        catch (UiaException)
        {
            return [];
        }

        if (bar is null)
        {
            return [];
        }

        var chips = new List<SavedChip>();
        foreach (var element in bar.FindAll(UiaScope.Descendants, new UiaCondition(ClassName: SavedChipClass)))
        {
            if (Chrome.SavedGroups.ParseChipName(element.Name) is { } parsed)
            {
                chips.Add(new SavedChip(new SavedGroup(profile, parsed.Title, parsed.Opened, chips.Count), element));
            }
        }

        return chips;
    }

    private sealed record SavedChip(SavedGroup Group, IUiaElement Element);
}
