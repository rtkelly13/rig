using System.Diagnostics.CodeAnalysis;
using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;
using Rig.Core.Vault;

namespace Rig.Core.Chrome.Windows;

/// <summary>
/// The half of <see cref="ChromeUiaActions"/> that needs the window in front: grouping, moving and ungrouping a tab.
/// Chrome offers these only on context menus and the group editor, which open only for a real right-click on a
/// foreground window, so these move the mouse. Run each inside <see cref="Foreground"/>.
/// </summary>
public sealed partial class ChromeUiaActions : IChromeGroupActions
{
    private const string BubbleButtonClass = "BubbleMenuItemButton";
    private const string TitleFieldClassHint = "TitleField";
    private const string UngroupDialogTitle = "Ungroup tab group?";
    private const string MoveEntry = "move tab to another window";
    private const ushort AltKey = 0x12;
    private const ushort ReturnKey = 0x0D;

    /// <summary>How long <see cref="Foreground"/> waits for the window to come forward.</summary>
    public static TimeSpan ForegroundTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a window that lost the foreground mid-run gets to come back.</summary>
    public static TimeSpan BringForwardTimeout { get; } = TimeSpan.FromSeconds(1);

    /// <summary>The pause for a window that just came forward to lay out its tab strip before anything clicks it.</summary>
    public static TimeSpan LayoutPause { get; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The foreground window's handle, whatever owns it.</summary>
    public long ForegroundWindow() => _win32.ForegroundWindow();

    /// <summary>
    /// Brings the window to the front until the lease is disposed, which puts the cursor, the previous foreground window
    /// and a minimised window's state back. On the lock screen nothing can come forward: the lease has not arrived and
    /// restores nothing.
    /// </summary>
    public ForegroundLease Foreground(long handle, TimeSpan? timeout = null)
    {
        if (_win32.CursorPosition() is not var (x, y))
        {
            return new ForegroundLease(false, null);
        }

        var previous = _win32.ForegroundWindow();
        var wasMinimised = _win32.IsMinimized(handle);
        void Restore()
        {
            _ = _input.MoveCursor(x, y);
            if (wasMinimised)
            {
                _ = _input.Minimize(handle);
            }

            if (previous != 0 && previous != handle && _win32.IsWindow(previous))
            {
                _ = _input.SetForeground(previous);
            }
        }

        try
        {
            // A process may only take the foreground right after input; a bare Alt tap counts.
            PressKey(AltKey);
            if (wasMinimised)
            {
                _ = _input.Restore(handle);
            }

            var arrived = TakeForeground(handle, timeout ?? ForegroundTimeout);
            if (arrived)
            {
                _sleep(LayoutPause);
            }

            return new ForegroundLease(arrived, Restore);
        }
        catch
        {
            Restore();
            throw;
        }
    }

    /// <summary>
    /// Puts the window back in front if something took the foreground, such as Chrome activating the window a tab was
    /// just moved into.
    /// </summary>
    public bool BringForward(long handle, TimeSpan? timeout = null)
    {
        if (_win32.ForegroundWindow() == handle)
        {
            return true;
        }

        PressKey(AltKey);
        if (!TakeForeground(handle, timeout ?? BringForwardTimeout))
        {
            return false;
        }

        _sleep(LayoutPause);
        return true;
    }

    /// <summary>
    /// Puts an ungrouped tab into a new group called <paramref name="title"/>, or into the profile's existing one.
    /// The menu lists every group in the profile, and joining one in another window (<paramref name="homeHandle"/>)
    /// moves the tab there; the tab then has a new runtime id, so arrival is checked by a tab with its title turning up
    /// in the home window's group. Refuses, touching nothing, when the tab has gone, its title changed, it is already
    /// grouped, or a click would not land on it; once a menu is open, any surprise closes it with Escape.
    /// </summary>
    public GroupOutcome GroupTab(ChromeTab tab, string title, bool isNew, long? homeHandle = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(title);
        var wait = timeout ?? StepTimeout;
        var started = _clock.GetTimestamp();
        GroupOutcome Outcome(bool grouped, string detail) => new(grouped, detail, _clock.GetElapsedTime(started));

        if (!Fits(tab, out var element, out var refusal))
        {
            return Outcome(false, refusal);
        }

        if (!BringForward(tab.WindowHandle))
        {
            return Outcome(false, "window is not in the foreground");
        }

        var pid = _win32.ProcessIdOf(tab.WindowHandle);
        var home = homeHandle is { } other && other != tab.WindowHandle ? other : (long?)null;
        var before = 0;
        if (home is { } homeWindow)
        {
            if (TitledInGroup(homeWindow, title, tab.Title) is not { } count)
            {
                return Outcome(false, "home window could not be read to count its group before the move");
            }

            before = count;
        }

        if (OpenTabMenu(tab, element, pid, wait) is { } notOpened)
        {
            return Outcome(false, notOpened);
        }

        if (Poll(() => MenuItem(pid, TabMenus.IsGroupEntry), wait) is not { } entry)
        {
            PressEscape();
            return Outcome(false, "context menu did not offer a group entry");
        }

        var entryName = Lowered(entry);
        if (entryName == "add tab to new group")
        {
            // Chrome offers this flat entry only while the window has no groups at all.
            if (!isNew)
            {
                PressEscape();
                return Outcome(false, $"group {PyText.Repr(title)} no longer exists");
            }

            entry.Invoke();
        }
        else
        {
            if (!entry.Expand())
            {
                PressEscape();
                return Outcome(false, $"unexpected menu entry {PyText.Repr(entryName)}");
            }

            var wanted = isNew ? "new group" : PyText.Lower(title);
            var chosen = Poll(() => MenuItems(pid, name => name == wanted) is { Count: > 0 } items ? items : null, wait);
            if (chosen is not [var only])
            {
                PressEscape();
                PressEscape();
                if (chosen is not null)
                {
                    return Outcome(false, $"{chosen.Count} menu entries named {PyText.Repr(title)}; cannot tell them apart");
                }

                return Outcome(false, isNew ? "new-group entry not offered" : $"group {PyText.Repr(title)} not offered");
            }

            only.Invoke();
        }

        if (isNew)
        {
            if (Poll(() => TitleField(pid), wait) is not { } field)
            {
                return Outcome(false, "group created but its title field did not appear; left unnamed");
            }

            if (!field.HasKeyboardFocus || _win32.ProcessIdOf(_win32.ForegroundWindow()) != pid)
            {
                PressEscape();
                return Outcome(false, "group created but its title field lost focus; left unnamed");
            }

            _ = _input.SendKeys(KeyEvent.Text(title));
            PressKey(ReturnKey);
        }

        if (home is { } target)
        {
            var arrived = PollUntil(() => TitledInGroup(target, title, tab.Title) > before, wait);
            return arrived ? Outcome(true, "moved") : Outcome(false, $"tab did not show up in group {PyText.Repr(title)} in its home window");
        }

        var joined = PollUntil(
            () => _reader.FindTabElement(tab) is { } element && ChromeUiaReader.GroupOf(element)?.Title == title,
            wait);
        return joined ? Outcome(true, isNew ? "created" : "joined") : Outcome(false, $"tab did not show up in group {PyText.Repr(title)}");
    }

    /// <summary>
    /// Moves an ungrouped tab into another window through "Move tab to another window". That submenu names each window
    /// after its active tab (<c>&lt;title&gt; and N other tabs</c>, long titles cut short with an ellipsis), so the entry
    /// must match the target's active tab and tab count and be the only one that does. Answers the tab as it now sits
    /// in the target window, with its new runtime id.
    /// </summary>
    public MoveOutcome MoveToWindow(ChromeTab tab, long targetHandle, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var wait = timeout ?? StepTimeout;
        var started = _clock.GetTimestamp();
        GroupOutcome Outcome(bool done, string detail) => new(done, detail, _clock.GetElapsedTime(started));
        MoveOutcome Refused(string detail) => new(Outcome(false, detail));

        if (!Fits(tab, out var element, out var refusal))
        {
            return Refused(refusal);
        }

        var target = _reader.ReadWindow(targetHandle);
        if (target?.Tabs.FirstOrDefault(each => each.Selected) is not { } active)
        {
            return Refused("target window has gone or has no active tab");
        }

        if (!BringForward(tab.WindowHandle))
        {
            return Refused("window is not in the foreground");
        }

        var pid = _win32.ProcessIdOf(tab.WindowHandle);
        var before = target.Tabs.Select(each => each.RuntimeId).ToHashSet();
        if (OpenTabMenu(tab, element, pid, wait) is { } notOpened)
        {
            return Refused(notOpened);
        }

        if (Poll(() => MenuItem(pid, name => name == MoveEntry), wait) is not { } found)
        {
            PressEscape();
            return Refused("context menu did not offer Move tab to another window");
        }

        if (!found.Expand())
        {
            PressEscape();
            return Refused("Move tab to another window did not open a submenu");
        }

        var chosen = Poll(
            () => MenuItems(pid, name => TabMenus.NamesWindow(name, active.Title, target.Tabs.Count)) is { Count: > 0 } items ? items : null,
            wait);
        if (chosen is not [var only])
        {
            PressEscape();
            PressEscape();
            return Refused(chosen is not null
                ? $"{chosen.Count} windows are named like the target; cannot tell them apart"
                : "target window not offered");
        }

        only.Invoke();
        var moved = Poll(
            () =>
            {
                if (_reader.ReadWindow(targetHandle) is not { } window)
                {
                    return null;
                }

                var fresh = window.Tabs.Where(each => !before.Contains(each.RuntimeId) && each.Title == tab.Title).ToList();
                return fresh is [{ Group: null } arrived] ? arrived : null;
            },
            wait);
        return moved is null ? Refused("tab did not show up in the target window") : new MoveOutcome(Outcome(true, "moved"), moved);
    }

    /// <summary>
    /// Dissolves the group the tab sits in, through the editor bubble its header opens on a right-click; its tabs stay
    /// put. A saved group asks first ("Ungroup tab group?", in a dialog inside the window), because ungrouping deletes it
    /// from the saved groups too; that is confirmed here once the dialog has settled. Refuses, touching nothing, when the
    /// tab has gone or its group is no longer called <paramref name="title"/>.
    /// </summary>
    public GroupOutcome Ungroup(ChromeTab tab, string title, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var wait = timeout ?? StepTimeout;
        var started = _clock.GetTimestamp();
        GroupOutcome Outcome(bool done, string detail) => new(done, detail, _clock.GetElapsedTime(started));

        var element = _reader.FindTabElement(tab);
        if (element is null || ChromeUiaReader.GroupOf(element)?.Title != title)
        {
            return Outcome(false, $"tab is no longer in a group called {PyText.Repr(title)}");
        }

        if (!BringForward(tab.WindowHandle))
        {
            return Outcome(false, "window is not in the foreground");
        }

        if (ChromeUiaReader.GroupHeader(element) is not var (_, header))
        {
            return Outcome(false, "group header not found");
        }

        var pid = _win32.ProcessIdOf(tab.WindowHandle);
        if (!MenusClosed(pid, wait))
        {
            return Outcome(false, "a menu from the previous step is still open");
        }

        if (!RightClickOn(tab, header))
        {
            return Outcome(false, "group header is not visible where a click would land");
        }

        if (Poll(() => BubbleButton(pid, "ungroup"), wait) is not { } ungroup)
        {
            PressEscape();
            return Outcome(false, "group editor did not offer Ungroup");
        }

        ungroup.Invoke();

        // Chrome ignores a dialog's buttons for a moment after it appears, and treats presses closer together than a
        // double-click as accidental, so the confirm is pressed only once the dialog has settled, and retried no faster.
        long? seenAt = null;
        long? pressedAt = null;
        var gone = PollUntil(
            () =>
            {
                if (_reader.FindTabElement(tab) is not { } live || ChromeUiaReader.GroupOf(live) is null)
                {
                    return true;
                }

                if (DialogButton(tab.WindowHandle, UngroupDialogTitle, "ungroup") is not { } confirm)
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
                        // The dialog went as it was pressed; the next probe sees whether the group went with it.
                    }
                }

                return false;
            },
            wait + DialogSettle + DialogSettle);
        return gone ? Outcome(true, "ungrouped") : Outcome(false, $"group {PyText.Repr(title)} is still there");
    }

    /// <summary>Every open menu item whose stripped, lower-cased name matches.</summary>
    public IReadOnlyList<IUiaElement> MenuItems(int pid, Func<string, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return [.. PopupElements(pid, UiaControlType.MenuItem).Where(item => matches(Lowered(item)))];
    }

    /// <summary>
    /// The open bubble's menu button named exactly <paramref name="name"/> (lower-cased), as in the group editor; exact,
    /// because the same bubble offers Delete group and Close group.
    /// </summary>
    public IUiaElement? BubbleButton(int pid, string name) =>
        PopupElements(pid, UiaControlType.Button)
            .FirstOrDefault(button => button.ClassName == BubbleButtonClass && Lowered(button) == name);

    /// <summary>The button named exactly <paramref name="name"/> (lower-cased) in the window's dialog titled <paramref name="dialogTitle"/>.</summary>
    public IUiaElement? DialogButton(long handle, string dialogTitle, string name)
    {
        IUiaElement? dialog;
        try
        {
            dialog = _uia.ElementFromHandle(handle)?.FindFirst(UiaScope.Descendants, new UiaCondition(Name: dialogTitle));
        }
        catch (UiaException)
        {
            return null;
        }

        return dialog?.FindAll(UiaScope.Descendants, new UiaCondition(UiaControlType.Button))
            .FirstOrDefault(button => Lowered(button) == name);
    }

    /// <summary>The new group's title field, in the editor bubble Chrome opens with it.</summary>
    public IUiaElement? TitleField(int pid) =>
        PopupElements(pid, UiaControlType.Edit)
            .FirstOrDefault(field => field.ClassName.Contains(TitleFieldClassHint, StringComparison.Ordinal));

    /// <summary>
    /// How many tabs titled <paramref name="tabTitle"/> sit in the window's group called <paramref name="groupTitle"/>.
    /// Null when the window has gone or will not read cleanly: an unknown count, never 0, because a move is confirmed by
    /// the count rising above a baseline taken from it.
    /// </summary>
    public int? TitledInGroup(long handle, string groupTitle, string tabTitle) =>
        _reader.ReadWindow(handle)?.Tabs.Count(each => each.Title == tabTitle && each.Group?.Title == groupTitle);

    /// <summary>
    /// Whether a click at (<paramref name="x"/>, <paramref name="y"/>) reaches this element of the tab's window: the
    /// point is in the tab's own window, not another window over it, and inside the strip's visible area. UI Automation
    /// hit-testing cannot say this: over a vertical strip it answers a <c>ShadowFrameView</c> overlay instead.
    /// </summary>
    public bool IsAtPoint(ChromeTab tab, IUiaElement element, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(element);
        var under = _win32.WindowFromPoint(x, y);
        if (under == 0 || _win32.Root(under) != tab.WindowHandle)
        {
            return false;
        }

        IUiaElement? strip;
        try
        {
            strip = _uia.ElementFromHandle(tab.WindowHandle) is { } root ? _reader.FindTabStrip(tab.WindowHandle, root) : null;
        }
        catch (UiaException)
        {
            return false;
        }

        return strip is not null && strip.BoundingRectangle.Contains(x, y) && !element.IsOffscreen;
    }

    private static string Lowered(IUiaElement element) => PyText.Lower(PyText.Strip(element.Name));

    /// <summary>Whether the tab can be grouped or moved: it is still there, under the same title, in no group.</summary>
    private bool Fits(ChromeTab tab, [NotNullWhen(true)] out IUiaElement? element, [NotNullWhen(false)] out string? refusal)
    {
        element = _reader.FindTabElement(tab);
        if (element is null)
        {
            refusal = "tab no longer present";
            return false;
        }

        var currentTitle = TabNames.Parse(element.Name).Title;
        refusal = currentTitle != tab.Title ? $"title changed to {PyText.Repr(currentTitle)}"
            : ChromeUiaReader.GroupOf(element) is not null ? "tab is already in a group"
            : null;
        return refusal is null;
    }

    /// <summary>Right-clicks the tab once the previous step's menu has closed; why not, or null once the click is made.</summary>
    private string? OpenTabMenu(ChromeTab tab, IUiaElement element, int pid, TimeSpan wait)
    {
        // A right-click made while the previous step's menu is still closing is swallowed.
        if (!MenusClosed(pid, wait))
        {
            return "a menu from the previous step is still open";
        }

        _ = element.ScrollIntoView();
        return RightClickOn(tab, element) ? null : "tab is not visible where a click would land";
    }

    /// <summary>Right-clicks the middle of the element when a click there would reach it.</summary>
    private bool RightClickOn(ChromeTab tab, IUiaElement element)
    {
        var rect = element.BoundingRectangle;
        var (x, y) = ((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top || !IsAtPoint(tab, element, x, y))
        {
            return false;
        }

        _ = _input.MoveCursor(x, y);
        _ = _input.RightClick();
        return true;
    }

    private void PressKey(ushort virtualKey) => _ = _input.SendKeys(KeyEvent.Tap(virtualKey));

    /// <summary>Asks for the foreground, and asks again half way: a window just moved in from another desktop can refuse the first.</summary>
    private bool TakeForeground(long handle, TimeSpan timeout)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                PressKey(AltKey);
            }

            _ = _input.SetForeground(handle);
            if (PollUntil(() => _win32.ForegroundWindow() == handle, timeout / 2))
            {
                return true;
            }
        }

        return false;
    }
}
