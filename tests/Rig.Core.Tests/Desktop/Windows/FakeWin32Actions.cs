using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;

namespace Rig.Core.Tests.Desktop.Windows;

/// <summary>Win32 writes that only record what was asked; <see cref="Refuse"/> names calls that fail.</summary>
internal sealed class FakeWin32Actions : IWin32Actions
{
    public List<string> Calls { get; } = [];

    public HashSet<string> Refuse { get; } = [];

    public int? KeysTaken { get; set; }

    public bool? Exited { get; set; } = true;

    public bool Restore(long handle) => Record("Restore", $"{handle}");

    public bool ShowWithoutActivating(long handle) => Record("ShowWithoutActivating", $"{handle}");

    public bool MoveWindow(long handle, Rect rect) => Record("MoveWindow", $"{handle} {Edges(rect)}");

    public bool Reposition(long handle, Rect rect) => Record("Reposition", $"{handle} {Edges(rect)}");

    public bool Restack(long handle, ZOrder order) => Record("Restack", $"{handle} {order}");

    public bool PostClose(long handle) => Record("PostClose", $"{handle}");

    public bool SetForeground(long handle)
    {
        var taken = Record("SetForeground", $"{handle}");
        OnSetForeground?.Invoke(handle);
        return taken;
    }

    /// <summary>Runs on each <see cref="SetForeground"/>, as Windows granting (or not) the foreground.</summary>
    public Action<long>? OnSetForeground { get; set; }

    public bool Minimize(long handle) => Record("Minimize", $"{handle}");

    public bool MoveCursor(int x, int y)
    {
        Cursor = (x, y);
        return Record("MoveCursor", $"{x},{y}");
    }

    /// <summary>Where <see cref="MoveCursor"/> last put the cursor.</summary>
    public (int X, int Y)? Cursor { get; private set; }

    /// <summary>Runs on each <see cref="RightClick"/> with the cursor position, as the window opening its menu.</summary>
    public Action<(int X, int Y)?>? OnRightClick { get; set; }

    public int RightClick()
    {
        _ = Record("RightClick", $"{Cursor}");
        OnRightClick?.Invoke(Cursor);
        return 2;
    }

    /// <summary>Every <see cref="SendKeys"/> batch, in order.</summary>
    public List<IReadOnlyList<KeyEvent>> Keys { get; } = [];

    /// <summary>Runs on each <see cref="SendKeys"/>, as the window reacting to the keys.</summary>
    public Action<IReadOnlyList<KeyEvent>>? OnKeys { get; set; }

    public int SendKeys(IReadOnlyList<KeyEvent> events)
    {
        _ = Record("SendKeys", $"{events.Count}");
        Keys.Add(events);
        OnKeys?.Invoke(events);
        return KeysTaken ?? events.Count;
    }

    public bool Terminate(int pid, int exitCode) => Record("Terminate", $"{pid} {exitCode}");

    public bool? WaitForExit(int pid, TimeSpan timeout)
    {
        _ = Record("WaitForExit", $"{pid} {timeout}");
        return Exited;
    }

    private static string Edges(Rect rect) => $"{rect.Left},{rect.Top},{rect.Width},{rect.Height}";

    private bool Record(string call, string args)
    {
        Calls.Add($"{call} {args}");
        return !Refuse.Contains(call);
    }
}
