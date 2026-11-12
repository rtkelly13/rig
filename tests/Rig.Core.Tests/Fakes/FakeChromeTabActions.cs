using Rig.Core.Chrome;

namespace Rig.Core.Tests.Fakes;

/// <summary>Records closes and selection restores; every close succeeds unless <see cref="FailWith"/> is set.</summary>
internal sealed class FakeChromeTabActions : IChromeTabActions
{
    /// <summary>When set, every close is refused with this detail and nothing is closed.</summary>
    public string? FailWith { get; set; }

    /// <summary>Every call, as (action, window, tab index); a restore logs index -1 per window.</summary>
    public List<(string Action, long Window, int Index)> Log { get; } = [];

    /// <summary>The tabs closed, in order.</summary>
    public List<ChromeTab> Closed { get; } = [];

    /// <summary>Each restore's windows and fallbacks.</summary>
    public List<(List<long> Windows, Dictionary<long, ChromeTab>? Fallbacks)> Restored { get; } = [];

    public CloseOutcome CloseTab(ChromeTab tab)
    {
        if (FailWith is { } detail)
        {
            return new CloseOutcome(false, detail, TimeSpan.Zero);
        }

        Closed.Add(tab);
        Log.Add(("close", tab.WindowHandle, tab.Index));
        return new CloseOutcome(true, "closed", TimeSpan.Zero);
    }

    public IReadOnlyDictionary<long, ChromeTab?> RestoreSelection(
        IEnumerable<ChromeWindow> before,
        IReadOnlyDictionary<long, ChromeTab>? fallbacks = null)
    {
        var handles = before.Select(window => window.Handle).ToList();
        Restored.Add((handles, fallbacks?.ToDictionary()));
        Log.AddRange(handles.Select(handle => ("restore", handle, -1)));
        return handles.ToDictionary(handle => handle, _ => (ChromeTab?)null);
    }
}
