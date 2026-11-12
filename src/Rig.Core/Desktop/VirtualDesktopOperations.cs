using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>
/// The rules rig layers over <see cref="IVirtualDesktops"/>: range checks, verified renames, adding desktops up to a
/// count and retiring switched-off desktops. Ported from <c>desktop_manager.win32.desktops</c>.
/// </summary>
/// <remarks>
/// None of these throw for a <see cref="DesktopException"/>: each reports what happened, as the Python did, so restore
/// can fall back to hotkeys or carry on.
/// </remarks>
public static class VirtualDesktopOperations
{
    /// <summary>Whether <paramref name="number"/> names a desktop that exists.</summary>
    public static bool Exists(IVirtualDesktops desktops, int number)
    {
        ArgumentNullException.ThrowIfNull(desktops);
        return number >= 1 && number <= desktops.Count;
    }

    /// <summary>Show desktop <paramref name="number"/>; whether it is now current. An out-of-range number does nothing.</summary>
    public static bool GoTo(IVirtualDesktops desktops, int number)
    {
        ArgumentNullException.ThrowIfNull(desktops);
        try
        {
            if (!Exists(desktops, number))
            {
                return false;
            }

            if (desktops.CurrentNumber != number)
            {
                desktops.SwitchTo(number);
            }

            return desktops.CurrentNumber == number;
        }
        catch (DesktopException)
        {
            return false;
        }
    }

    /// <summary>
    /// Name desktop <paramref name="number"/> <paramref name="name"/>; whether it bears that name afterwards. A desktop
    /// already named so is left alone.
    /// </summary>
    public static bool Rename(IVirtualDesktops desktops, int number, string name)
    {
        ArgumentNullException.ThrowIfNull(desktops);
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            if (!Exists(desktops, number))
            {
                return false;
            }

            if (desktops.Names()[number - 1] == name)
            {
                return true;
            }

            desktops.Rename(number, name);
            var names = desktops.Names();
            return number <= names.Count && names[number - 1] == name;
        }
        catch (DesktopException)
        {
            return false;
        }
    }

    /// <summary>Move window <paramref name="handle"/> to desktop <paramref name="number"/>; whether the move was made.</summary>
    public static bool MoveWindow(IVirtualDesktops desktops, long handle, int number)
    {
        ArgumentNullException.ThrowIfNull(desktops);
        try
        {
            if (!Exists(desktops, number))
            {
                return false;
            }

            desktops.MoveWindow(handle, number);
            return true;
        }
        catch (DesktopException)
        {
            return false;
        }
    }

    /// <summary>Add desktops until there are at least <paramref name="target"/>; whether there are now.</summary>
    public static bool EnsureCount(IVirtualDesktops desktops, int target)
    {
        ArgumentNullException.ThrowIfNull(desktops);
        try
        {
            for (var count = desktops.Count; count < target; count++)
            {
                desktops.Create();
            }

            return desktops.Count >= target;
        }
        catch (DesktopException)
        {
            return false;
        }
    }

    /// <summary>
    /// Remove every desktop named in <paramref name="retiredNames"/>, ignoring case, last first. Their windows go to the
    /// <paramref name="keepCount"/>th desktop left, or the last one left when fewer remain. Nothing is removed when
    /// that would leave no desktop. Returns the names removed, last first; on a failure, those removed before it.
    /// </summary>
    public static IReadOnlyList<string> RetireSwitchedOff(IVirtualDesktops desktops, int keepCount, IReadOnlyList<string> retiredNames)
    {
        ArgumentNullException.ThrowIfNull(desktops);
        ArgumentNullException.ThrowIfNull(retiredNames);
        var wanted = retiredNames.Where(name => name.Length > 0).Select(PyText.CaseFold).ToHashSet(StringComparer.Ordinal);
        List<string> removed = [];
        if (wanted.Count == 0 || keepCount < 1)
        {
            return removed;
        }

        try
        {
            var names = desktops.Names();
            var doomed = Enumerable.Range(1, names.Count).Where(number => wanted.Contains(PyText.CaseFold(names[number - 1]))).ToList();
            var kept = Enumerable.Range(1, names.Count).Except(doomed).ToList();
            if (doomed.Count == 0 || kept.Count == 0)
            {
                return removed;
            }

            var fallback = kept[Math.Min(keepCount, kept.Count) - 1];
            for (var index = doomed.Count - 1; index >= 0; index--)
            {
                // Removal runs last first, so this one keeps its number; the fallback moves down once for each removed
                // desktop that stood before it.
                var number = doomed[index];
                var fallbackNow = fallback - doomed.Skip(index + 1).Count(gone => gone < fallback);
                desktops.Remove(number, fallbackNow);
                removed.Add(names[number - 1]);
            }
        }
        catch (DesktopException)
        {
        }

        return removed;
    }
}
