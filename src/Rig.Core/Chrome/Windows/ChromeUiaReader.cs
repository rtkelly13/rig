using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;
using Rig.Core.Vault;

namespace Rig.Core.Chrome.Windows;

/// <summary>
/// Reads Chrome's browser windows and their tabs through UI Automation: every window on every desktop and profile,
/// each tab's selection, pinning and group. The read side of <c>desktop_manager.chrome.uia_core</c>.
/// </summary>
/// <remarks>
/// Chrome rebuilds tab views as tabs load, move or close, so an element can go stale mid-read. A window is re-read until
/// one pass gets through cleanly; one that never does raises <see cref="ChromeReadException"/> rather than report a
/// tab with a property defaulted, because pinned and grouped tabs are protected.
/// </remarks>
public sealed class ChromeUiaReader
{
    /// <summary>Chrome's top-level window class.</summary>
    public const string WindowClass = "Chrome_WidgetWin_1";

    /// <summary>Chrome's browser process image.</summary>
    public const string ProcessName = "chrome.exe";

    /// <summary>Reads of one window before giving up on it.</summary>
    public const int ReadAttempts = 3;

    /// <summary>The age rank of a window whose root view has no id: behind every real id, in z-order.</summary>
    public const long UnrankedAge = 1_000_000_000;

    private const string TabStripClass = "TabStripView";
    private const string TabClass = "TabView";
    private const string RootViewClass = "BrowserRootView";
    private const string GroupViewClass = "TabGroupView";
    private const string GroupHeaderClass = "TabGroupHeaderView";
    private const int StripSearchMaxDepth = 12;
    private const int PinnedSearchDepth = 4;

    private static readonly UiaCondition TabCondition = new(UiaControlType.TabItem, TabClass);

    private readonly IUiAutomation _uia;
    private readonly IWin32Windows _win32;
    private readonly IWindowDesktops _desktops;
    private readonly IChromeWindowShell _shell;
    private readonly WindowInventory _inventory;
    private readonly Action<TimeSpan> _sleep;
    private readonly Dictionary<long, IUiaElement> _strips = [];

    /// <summary>Creates the reader: one per command.</summary>
    public ChromeUiaReader(
        IUiAutomation uia,
        IWin32Windows win32,
        IWindowDesktops desktops,
        IChromeWindowShell shell,
        Action<TimeSpan>? sleep = null)
    {
        _uia = uia;
        _win32 = win32;
        _desktops = desktops;
        _shell = shell;
        _inventory = new WindowInventory(win32, desktops);
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>The pause between reads of a window.</summary>
    public static TimeSpan ReadRetryPause { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// <paramref name="read"/>, retried while a UI Automation call fails. Null when the window itself has gone
    /// (<paramref name="alive"/> is false), which is an honest answer; when it stays but no attempt reads cleanly, throws
    /// <see cref="ChromeReadException"/>.
    /// </summary>
    public static T? ReadSettled<T>(
        Func<T?> read,
        Func<bool> alive,
        Action onRetry,
        Action<TimeSpan> sleep,
        int attempts = ReadAttempts)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(alive);
        ArgumentNullException.ThrowIfNull(onRetry);
        ArgumentNullException.ThrowIfNull(sleep);
        UiaException? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                sleep(ReadRetryPause);
            }

            try
            {
                return read();
            }
            catch (UiaException error)
            {
                last = error;
                onRetry();
                if (!alive())
                {
                    return null;
                }
            }
        }

        throw new ChromeReadException(
            $"Chrome's tab strip kept changing during the read ({attempts} attempts): {last?.Message}",
            last ?? new UiaException());
    }

    /// <summary>Top-level Chrome browser-process windows with a title, on every desktop, in z-order.</summary>
    public IReadOnlyList<long> BrowserWindows()
    {
        var names = new Dictionary<int, string?>();
        var handles = new List<long>();
        foreach (var handle in _win32.TopLevelWindows())
        {
            if (_win32.ClassName(handle) != WindowClass || PyText.Strip(_win32.Title(handle) ?? string.Empty).Length == 0)
            {
                continue;
            }

            var pid = _win32.ProcessIdOf(handle);
            if (!names.TryGetValue(pid, out var name))
            {
                name = _inventory.ProcessDetailsOf(pid).Name;
                names[pid] = name;
            }

            if (string.Equals(name, ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                handles.Add(handle);
            }
        }

        return handles;
    }

    /// <summary>Every Chrome browser window with its tabs, oldest first.</summary>
    public IReadOnlyList<ChromeWindow> ListWindows()
    {
        var windows = new List<ChromeWindow>();
        foreach (var (handle, index) in BrowserWindows().Select((handle, index) => (handle, index)))
        {
            if (ReadWindow(handle, index) is { } window)
            {
                windows.Add(window);
            }
        }

        return [.. windows.OrderBy(window => window.AgeRank).ThenBy(window => window.Handle)];
    }

    /// <summary>
    /// One window afresh, as <see cref="ListWindows"/> reads it (its age rank aside); null when it has gone or will not
    /// read cleanly, because callers are actions checking their own result.
    /// </summary>
    public ChromeWindow? ReadWindow(long handle)
    {
        try
        {
            return ReadWindow(handle, 0);
        }
        catch (ChromeReadException)
        {
            return null;
        }
    }

    /// <summary>A tab's live element, re-found by runtime id; null when it has gone.</summary>
    public IUiaElement? FindTabElement(ChromeTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_uia.ElementFromHandle(tab.WindowHandle) is not { } root || FindTabStrip(tab.WindowHandle, root) is not { } strip)
        {
            return null;
        }

        return strip.FindAll(UiaScope.Descendants, TabCondition)
            .FirstOrDefault(element => new RuntimeId(element.RuntimeId).Equals(tab.RuntimeId));
    }

    /// <summary>The tab's window strip, cached while its element stays live.</summary>
    public IUiaElement? FindTabStrip(long handle, IUiaElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (_strips.TryGetValue(handle, out var cached))
        {
            try
            {
                _ = cached.ClassName;
                return cached;
            }
            catch (UiaException)
            {
                _ = _strips.Remove(handle);
            }
        }

        var strip = FindView(root, TabStripClass);
        if (strip is not null)
        {
            _strips[handle] = strip;
        }

        return strip;
    }

    /// <summary>The group whose <c>TabGroupView</c> holds the tab, titled from its header, which only the raw view shows.</summary>
    /// <remarks>The tab's own "Part of group" suffix is not used: Chrome leaves it stale after a rename.</remarks>
    public static TabGroup? GroupOf(IUiaElement tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (GroupHeader(tab) is not var (view, header))
        {
            return null;
        }

        return TabNames.ParseGroupHeader(header.Name) is { } title ? new TabGroup(new RuntimeId(view.RuntimeId), title) : null;
    }

    /// <summary>The header of the group holding the tab, and the group's view; null when it is in none.</summary>
    public static (IUiaElement View, IUiaElement Header)? GroupHeader(IUiaElement tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var parent = tab.Parent(UiaView.Raw);
        if (parent is null || parent.ClassName != GroupViewClass)
        {
            return null;
        }

        for (var child = parent.FirstChild(UiaView.Raw); child is not null; child = child.NextSibling(UiaView.Raw))
        {
            if (child.ClassName == GroupHeaderClass)
            {
                return (parent, child);
            }
        }

        return null;
    }

    private ChromeWindow? ReadWindow(long handle, int enumIndex) =>
        ReadSettled(
            () => ReadWindowOnce(handle, enumIndex),
            () => _win32.IsWindow(handle),
            () => _strips.Remove(handle),
            _sleep);

    private ChromeWindow? ReadWindowOnce(long handle, int enumIndex)
    {
        if (_uia.ElementFromHandle(handle) is not { } root || FindTabStrip(handle, root) is not { } strip)
        {
            return null;
        }

        var profile = _shell.Profile(handle);
        var minimized = _win32.IsMinimized(handle);
        return new ChromeWindow(
            handle,
            _win32.Title(handle) ?? string.Empty,
            profile.Directory,
            profile.Name,
            AgeRank(root, enumIndex),
            minimized,
            _desktops.DesktopNumberOf(handle),
            ReadTabs(handle, strip))
        {
            Rect = minimized ? _shell.RestoredRect(handle) : ToWindowRect(_win32.WindowRect(handle)),
            Monitor = _win32.MonitorOf(handle) is { } monitor ? new ScreenPoint(monitor.Bounds.Left, monitor.Bounds.Top) : null,
        };
    }

    private static WindowRect? ToWindowRect(Rect? rect) =>
        rect is null ? null : new WindowRect(rect.Left, rect.Top, rect.Left + rect.Width, rect.Top + rect.Height);

    private static List<ChromeTab> ReadTabs(long handle, IUiaElement strip)
    {
        var tabs = new List<ChromeTab>();
        foreach (var (element, index) in strip.FindAll(UiaScope.Descendants, TabCondition).Select((element, index) => (element, index)))
        {
            var name = element.Name;
            var parsed = TabNames.Parse(name);
            tabs.Add(new ChromeTab(
                new RuntimeId(element.RuntimeId),
                handle,
                index,
                name,
                parsed.Title,
                element.IsSelected,
                parsed.Pinned || IsPinned(element, strip),
                parsed.Loaded,
                parsed.Audible)
            {
                Alerts = parsed.Alerts,
                MemoryMb = parsed.MemoryMb,
                Group = GroupOf(element),
            });
        }

        return tabs;
    }

    /// <summary>Breadth-first search for a browser view by class that never descends into web content.</summary>
    private static IUiaElement? FindView(IUiaElement root, string className)
    {
        List<IUiaElement> level = [root];
        for (var depth = 0; depth < StripSearchMaxDepth && level.Count > 0; depth++)
        {
            var below = new List<IUiaElement>();
            foreach (var element in level)
            {
                for (var child = element.FirstChild(); child is not null; child = child.NextSibling())
                {
                    if (child.ClassName == className)
                    {
                        return child;
                    }

                    if (child.ControlType != UiaControlType.Document)
                    {
                        below.Add(child);
                    }
                }
            }

            level = below;
        }

        return null;
    }

    /// <summary>
    /// Chrome assigns accessibility ids process-wide in creation order, so the root view's id orders windows by age;
    /// a window without one ranks behind every real id, in z-order.
    /// </summary>
    private static long AgeRank(IUiaElement root, int enumIndex)
    {
        for (var child = root.FirstChild(); child is not null; child = child.NextSibling())
        {
            if (child.ClassName == RootViewClass && child.RuntimeId is { Count: > 0 } id)
            {
                return id[^1];
            }
        }

        return UnrankedAge + enumIndex;
    }

    /// <summary>Pinned tabs sit in a <c>PinnedTabContainerView</c> (vertical strip) between the tab and the strip.</summary>
    private static bool IsPinned(IUiaElement tab, IUiaElement strip)
    {
        var parent = tab.Parent();
        for (var step = 0; step < PinnedSearchDepth; step++)
        {
            if (parent is null || parent.SameAs(strip))
            {
                return false;
            }

            var className = parent.ClassName;
            if (className.Contains("Pinned", StringComparison.Ordinal) && !className.Contains("Unpinned", StringComparison.Ordinal))
            {
                return true;
            }

            parent = parent.Parent();
        }

        return false;
    }
}
