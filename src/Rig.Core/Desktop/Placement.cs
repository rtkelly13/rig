using System.Globalization;
using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>A plan that places windows on more screens than are connected.</summary>
/// <param name="Required">The highest screen any placement names.</param>
/// <param name="Live">How many screens are connected.</param>
public sealed record MonitorShortfall(int Required, int Live);

/// <summary>Whether the paths a plan names exist on this machine. The seam <see cref="Placement.ValidatePlan"/> checks paths through.</summary>
public interface IPathProbe
{
    /// <summary>Whether <paramref name="path"/> names an existing file or folder, after expanding variables and <c>~</c>.</summary>
    bool Exists(string path);

    /// <summary>
    /// The executable <paramref name="path"/> resolves to, for an app whose install folder carries a version; the path
    /// unchanged when there is nothing better.
    /// </summary>
    string ResolveExecutable(string path);
}

/// <summary>The local filesystem as an <see cref="IPathProbe"/>. Executables resolve to themselves.</summary>
public sealed class LocalPathProbe : IPathProbe
{
    /// <inheritdoc />
    public bool Exists(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        }

        return File.Exists(expanded) || Directory.Exists(expanded);
    }

    /// <inheritdoc />
    public string ResolveExecutable(string path) => path;
}

/// <summary>
/// Checking a plan's entries and placements, and fitting <c>columns</c> layouts to the screens. Ported from
/// <c>desktop_manager.placement</c>. Where Python asks Win32 for the monitors, these take the count or widths as
/// arguments.
/// </summary>
public static class Placement
{
    private static readonly string[] Modes = ["full", "halves", "thirds", "two-thirds", "quarters", "columns"];

    private static readonly string[] CommandAtLogonValues = [LaunchEntry.CommandAtLogonDeferred, "run"];

    /// <summary>
    /// Everything wrong with <paramref name="plan"/>'s desktops and entries: duplicate names, missing commands, folders
    /// and paths, unknown <c>command_at_logon</c> modes and bad placements. Placeholders resolve from
    /// <paramref name="substitutions"/>. With <paramref name="paths"/>, the folders, apps and local-path arguments must
    /// also exist; a placement must fit <paramref name="monitorCount"/> screens when that is known (non-zero).
    /// </summary>
    public static IReadOnlyList<string> ValidatePlan(
        WorkspacePlan plan,
        IReadOnlyDictionary<string, string> substitutions,
        int monitorCount = 0,
        IPathProbe? paths = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(substitutions);
        var errors = new List<string>();
        if (plan.Desktops.Count == 0)
        {
            errors.Add("Workspace plan must define at least one desktop");
        }

        var desktopNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var desktop in plan.Desktops)
        {
            if (!desktopNames.Add(desktop.Name))
            {
                errors.Add($"Duplicate desktop name: {desktop.Name}");
            }

            var entryNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in desktop.Launch)
            {
                if (!entryNames.Add(entry.Name))
                {
                    errors.Add($"{desktop.Name}: duplicate launch entry `{entry.Name}`");
                }

                errors.AddRange(EntryErrors(entry, desktop.Name, substitutions, paths));
                errors.AddRange(CommandAtLogonErrors(entry, desktop.Name));
                if (entry.Placement is not null)
                {
                    errors.AddRange(ValidatePlacementSpec(entry.Placement, desktop.Name, entry.Name, monitorCount));
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// How well each desktop's placements fill its screens: an empty desktop or screen, a screen past the count, and
    /// the share of a screen left empty, or (when <paramref name="strict"/>) covered twice. Entries switched off, in
    /// the plan or by an off feature, are left out. The screen count is <see cref="ResolveScreenCount"/>'s.
    /// </summary>
    public static IReadOnlyList<string> ValidatePlanStructure(WorkspacePlan plan, int? expectedScreenCount = null, bool strict = false, int detectedScreenCount = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<string>();
        var screenCount = ResolveScreenCount(plan, expectedScreenCount, detectedScreenCount);
        foreach (var desktop in plan.Desktops)
        {
            if (desktop.Launch.Count == 0)
            {
                issues.Add($"{desktop.Name}: virtual desktop is empty");
            }

            var placed = desktop.Launch
                .Where(entry => entry.Placement is not null && IsActive(entry, plan.Features))
                .Select(entry => (entry.Placement!.Screen, Rect: Geometry.NormalizedPlacementRect(entry.Placement)))
                .ToList();
            if (placed.Count == 0)
            {
                continue;
            }

            var referenced = placed.Select(item => item.Screen).Distinct().Order().ToList();
            if (screenCount > 0)
            {
                issues.AddRange(Enumerable.Range(1, screenCount).Except(referenced).Select(screen => $"{desktop.Name}: screen {screen} is empty"));
            }

            foreach (var screen in referenced)
            {
                if (screenCount > 0 && screen > screenCount)
                {
                    issues.Add($"{desktop.Name}: screen {screen} exceeds expected screen count {screenCount}");
                    continue;
                }

                var coverage = Geometry.CoverageSummary([.. placed.Where(item => item.Screen == screen).Select(item => item.Rect)]);
                if (coverage.Empty > Fraction.Zero)
                {
                    issues.Add($"{desktop.Name}: screen {screen} leaves {Percent(coverage.Empty)} empty");
                }

                if (strict && coverage.Overlap > Fraction.Zero)
                {
                    issues.Add($"{desktop.Name}: screen {screen} overlaps {Percent(coverage.Overlap)}");
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// The shortfall when fewer screens are connected (<paramref name="liveScreenCount"/>) than the plan places windows
    /// on. <see langword="null"/> when the plan fits, needs one screen at most, or the live count is unknown (zero).
    /// </summary>
    public static MonitorShortfall? FindMonitorShortfall(WorkspacePlan plan, int liveScreenCount)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var required = HighestScreen(plan);
        return required > 1 && liveScreenCount > 0 && liveScreenCount < required ? new(required, liveScreenCount) : null;
    }

    /// <summary>Everything wrong with one placement, each message prefixed <c>desktop/entry:</c>.</summary>
    public static IReadOnlyList<string> ValidatePlacementSpec(PlacementSpec placement, string desktopName, string entryName, int monitorCount = 0)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var prefix = $"{desktopName}/{entryName}";
        var errors = new List<string>();
        if (placement.Screen < 1)
        {
            errors.Add($"{prefix}: placement screen must be >= 1");
        }

        if (monitorCount != 0 && placement.Screen > monitorCount)
        {
            errors.Add($"{prefix}: placement screen {placement.Screen} exceeds detected monitor count {monitorCount}");
        }

        errors.AddRange(LayoutErrors(placement.Layout, prefix));
        if (placement.Subdivide is not null)
        {
            errors.AddRange(LayoutErrors(placement.Subdivide, prefix));
            if (placement.Subdivide.Mode == "columns")
            {
                errors.Add($"{prefix}: columns applies to the screen, not a subdivide");
            }
        }

        return errors;
    }

    /// <summary>
    /// How many screens the plan is checked against: <paramref name="expectedScreenCount"/> when given, else the
    /// <paramref name="detectedScreenCount"/> when non-zero, else the highest screen any placement names.
    /// </summary>
    public static int ResolveScreenCount(WorkspacePlan plan, int? expectedScreenCount = null, int detectedScreenCount = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (expectedScreenCount is { } expected)
        {
            return Math.Max(0, expected);
        }

        return detectedScreenCount > 0 ? detectedScreenCount : HighestScreen(plan);
    }

    /// <summary>A placement in one line, such as <c>screen 1: halves#1/vertical -&gt; halves#2/horizontal</c>.</summary>
    public static string FormatPlacement(PlacementSpec placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var text = $"screen {placement.Screen}: {Format(placement.Layout)}";
        return placement.Subdivide is null ? text : $"{text} -> {Format(placement.Subdivide)}";

        static string Format(PlacementLayout layout) =>
            string.IsNullOrEmpty(layout.Orientation) ? $"{layout.Mode}#{layout.Slot}" : $"{layout.Mode}#{layout.Slot}/{layout.Orientation}";
    }

    /// <summary>
    /// Settles each <c>columns</c> placement's count from its screen's width, and switches off entries that do not
    /// fit, so the same plan opens a third terminal on a 2560px screen and leaves it out on a 1920px one. A screen
    /// missing from <paramref name="widths"/> keeps the plan as written: as many columns as the highest slot any entry
    /// on that desktop and screen asks for.
    /// </summary>
    public static WorkspacePlan FitColumns(WorkspacePlan plan, IReadOnlyDictionary<int, int> widths)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(widths);
        return plan with { Desktops = [.. plan.Desktops.Select(desktop => FitDesktop(desktop, widths))] };
    }

    private static DesktopPlan FitDesktop(DesktopPlan desktop, IReadOnlyDictionary<int, int> widths)
    {
        var written = new Dictionary<int, int>();
        foreach (var placement in desktop.Launch.Select(entry => entry.Placement).OfType<PlacementSpec>().Where(IsColumns))
        {
            written[placement.Screen] = Math.Max(written.GetValueOrDefault(placement.Screen), placement.Layout.Slot);
        }

        if (written.Count == 0)
        {
            return desktop;
        }

        return desktop with
        {
            Launch =
            [
                .. desktop.Launch.Select(entry =>
                {
                    if (entry.Placement is not { } placement || !IsColumns(placement))
                    {
                        return entry;
                    }

                    var count = widths.TryGetValue(placement.Screen, out var width)
                        ? Geometry.ColumnsForWidth(width, placement.Layout.MinWidth)
                        : written[placement.Screen];
                    return entry with
                    {
                        Placement = placement with { Layout = placement.Layout with { Count = count } },
                        Disabled = entry.Disabled || placement.Layout.Slot > count,
                    };
                }),
            ],
        };
    }

    private static bool IsColumns(PlacementSpec placement) => placement.Layout.Mode == "columns";

    private static List<string> EntryErrors(LaunchEntry entry, string desktopName, IReadOnlyDictionary<string, string> substitutions, IPathProbe? paths)
    {
        var where = $"{desktopName}/{entry.Name}";
        switch (entry.Type)
        {
            case "terminal":
                var errors = new List<string>();
                if (string.IsNullOrEmpty(entry.Command))
                {
                    errors.Add($"{where}: terminal missing command");
                }

                if (string.IsNullOrEmpty(entry.Cwd))
                {
                    errors.Add($"{where}: terminal missing cwd");
                }
                else if (paths is not null)
                {
                    errors.AddRange(FolderErrors(entry.Cwd, substitutions, where, paths));
                }

                return errors;
            case "app" when string.IsNullOrEmpty(entry.Path):
                return [$"{where}: app missing path"];
            case "app" when paths is not null:
                return AppErrors(entry.Path!, entry.Args, substitutions, where, paths);
            case "command" when string.IsNullOrEmpty(entry.Command):
                return [$"{where}: command entry missing command"];
            case "command" when !string.IsNullOrEmpty(entry.Cwd) && paths is not null:
                return FolderErrors(entry.Cwd, substitutions, where, paths);
            default:
                return [];
        }
    }

    private static List<string> CommandAtLogonErrors(LaunchEntry entry, string desktopName)
    {
        if (entry.CommandAtLogon is not { } mode)
        {
            return [];
        }

        var where = $"{desktopName}/{entry.Name}";
        if (!CommandAtLogonValues.Contains(mode, StringComparer.Ordinal))
        {
            return [$"{where}: command_at_logon must be one of {string.Join(", ", CommandAtLogonValues)}, not {PyText.Repr(mode)}"];
        }

        return entry.DefersCommandAtLogon && (entry.Type != LaunchEntry.Terminal || entry.Shell != LaunchEntry.DefaultTerminalShell)
            ? [$"{where}: command_at_logon \"deferred\" is only for terminal entries with shell wt-pwsh"]
            : [];
    }

    private static List<string> AppErrors(string rawPath, IReadOnlyList<string> rawArgs, IReadOnlyDictionary<string, string> substitutions, string where, IPathProbe paths)
    {
        string path;
        try
        {
            path = Placeholders.Substitute(rawPath, substitutions)!;
        }
        catch (InvalidPlanException error)
        {
            return [$"{where}: {error.Message}"];
        }

        var errors = new List<string>();
        var executable = paths.ResolveExecutable(path);
        if (!paths.Exists(executable))
        {
            errors.Add($"{where}: app path does not exist: {executable}");
        }

        var localArgs = new List<string>();
        foreach (var rawArg in rawArgs)
        {
            try
            {
                var arg = Placeholders.Substitute(rawArg, substitutions)!;
                if (LocalPathArg(arg) is not null)
                {
                    localArgs.Add(arg);
                }
            }
            catch (InvalidPlanException error)
            {
                errors.Add($"{where}: {error.Message}");
            }
        }

        errors.AddRange(localArgs.Where(arg => !paths.Exists(arg) && !LooksLikeObsidianVault(path, arg)).Select(arg => $"{where}: arg does not exist: {arg}"));
        return errors;
    }

    private static List<string> FolderErrors(string rawValue, IReadOnlyDictionary<string, string> substitutions, string where, IPathProbe paths)
    {
        try
        {
            var folder = Placeholders.Substitute(rawValue, substitutions)!;
            return paths.Exists(folder) ? [] : [$"{where}: cwd does not exist: {folder}"];
        }
        catch (InvalidPlanException error)
        {
            return [$"{where}: {error.Message}"];
        }
    }

    // The argument as a local path when it reads as one (absolute, drive-rooted, ./, ../ or ~/), quotes stripped;
    // null for a URL, a flag or a bare word.
    private static string? LocalPathArg(string arg)
    {
        var cleaned = arg.Trim().Trim('"');
        if (cleaned.Length == 0 || cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || cleaned.StartsWith('-'))
        {
            return null;
        }

        if (cleaned.Length >= 3 && cleaned[1] == ':' && cleaned[2] is '\\' or '/')
        {
            return cleaned;
        }

        string[] prefixes = ["./", "../", "/", "~/", "~\\"];
        return prefixes.Any(prefix => cleaned.StartsWith(prefix, StringComparison.Ordinal)) ? cleaned : null;
    }

    // Obsidian takes a vault name, not a folder, so an argument ending "vault" is not checked on disk.
    private static bool LooksLikeObsidianVault(string path, string arg) =>
        path.EndsWith("obsidian.exe", StringComparison.OrdinalIgnoreCase) && arg.EndsWith("vault", StringComparison.OrdinalIgnoreCase);

    // Mirrors what PlanFeatures.Apply bakes into Disabled, so a plan read straight off disk validates the same way.
    private static bool IsActive(LaunchEntry entry, IReadOnlyDictionary<string, bool> features) =>
        !entry.Disabled && (entry.Feature is null || features.GetValueOrDefault(entry.Feature));

    private static int HighestScreen(WorkspacePlan plan) =>
        plan.Desktops.SelectMany(desktop => desktop.Launch).Select(entry => entry.Placement).OfType<PlacementSpec>().Select(placement => placement.Screen).DefaultIfEmpty(0).Max();

    private static List<string> LayoutErrors(PlacementLayout layout, string prefix)
    {
        if (!Modes.Contains(layout.Mode, StringComparer.Ordinal))
        {
            return [$"{prefix}: invalid layout mode `{layout.Mode}`"];
        }

        var errors = new List<string>();
        if (layout.MinWidth is not null && layout.Mode != "columns")
        {
            errors.Add($"{prefix}: min_width only applies to columns");
        }

        switch (layout.Mode)
        {
            case "full":
                return errors;
            case "halves":
                if (layout.Slot is not (1 or 2))
                {
                    errors.Add($"{prefix}: halves slot must be 1 or 2");
                }

                if (layout.Orientation is not ("vertical" or "horizontal"))
                {
                    errors.Add($"{prefix}: halves orientation must be vertical or horizontal");
                }

                return errors;
        }

        if (layout.Orientation is not null)
        {
            errors.Add($"{prefix}: orientation only applies to halves");
        }

        string? slotError = layout.Mode switch
        {
            "columns" when layout.Slot < 1 => "columns slot must be >= 1",
            "thirds" when layout.Slot is not (1 or 2 or 3) => "thirds slot must be 1, 2, or 3",
            "two-thirds" when layout.Slot is not (1 or 2) => "two-thirds slot must be 1 or 2",
            "quarters" when layout.Slot is not (1 or 2 or 3 or 4) => "quarters slot must be 1, 2, 3, or 4",
            _ => null,
        };
        if (slotError is not null)
        {
            errors.Add($"{prefix}: {slotError}");
        }

        if (layout.Mode == "columns" && layout.MinWidth < 1)
        {
            errors.Add($"{prefix}: columns min_width must be >= 1");
        }

        return errors;
    }

    // Python's f"{float(value * 100):.1f}%".
    private static string Percent(Fraction value) => (value * 100).ToDouble().ToString("F1", CultureInfo.InvariantCulture) + "%";
}
