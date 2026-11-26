using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Runtime;

/// <summary>What a <c>rig</c> found on PATH is.</summary>
public enum RigCopyKind
{
    /// <summary>The shim <c>rig runtime install</c> writes (<see cref="RigShim.PathFor"/>).</summary>
    Shim,

    /// <summary>The runtime's <c>current</c> copy, reached through the link, so it follows installs and rollbacks.</summary>
    Current,

    /// <summary>A copy under the runtime folder reached by its own name, which stays on that commit.</summary>
    Pinned,

    /// <summary>A Python virtualenv's <c>rig</c>: the old Python CLI, which no longer runs.</summary>
    PythonVenv,

    /// <summary>A <c>dotnet build</c> output under <c>bin/Debug</c> or <c>bin/Release</c>.</summary>
    BuildOutput,

    /// <summary>Anything else named <c>rig</c>.</summary>
    Other,
}

/// <summary>One <c>rig</c> on PATH.</summary>
/// <param name="Path">The file, as PATH names it.</param>
/// <param name="Kind">What it is.</param>
public sealed record RigCopy(string Path, RigCopyKind Kind)
{
    /// <summary>Whether running it runs the installed copy: the shim or the <c>current</c> link.</summary>
    public bool Healthy => Kind is RigCopyKind.Shim or RigCopyKind.Current;

    /// <summary>The kind in a few words, for a report.</summary>
    public string Label => Kind switch
    {
        RigCopyKind.Shim => "rig shim",
        RigCopyKind.Current => "runtime copy (current)",
        RigCopyKind.Pinned => "pinned runtime copy",
        RigCopyKind.PythonVenv => "old Python rig (venv)",
        RigCopyKind.BuildOutput => "dotnet build output",
        _ => "other",
    };
}

/// <summary>One thing the check has to say. An issue needs fixing; the rest is for information.</summary>
/// <param name="Issue">Whether it needs fixing.</param>
/// <param name="Message">Where the copy is and what to do, in a sentence or two.</param>
public sealed record RigFinding(bool Issue, string Message);

/// <summary>Every <c>rig</c> on a PATH, in the order a shell tries them, and what is wrong with that.</summary>
/// <param name="Copies">The copies, first (the one <c>rig</c> runs) first.</param>
/// <param name="Findings">The issues and notes.</param>
public sealed record RigCopiesReport(IReadOnlyList<RigCopy> Copies, IReadOnlyList<RigFinding> Findings)
{
    /// <summary>How many findings need fixing.</summary>
    public int Issues => Findings.Count(finding => finding.Issue);
}

/// <summary>
/// Checks that <c>rig</c> on PATH is the installed one: the shim, or the runtime's <c>current</c> copy. A stale copy that
/// comes first (a Python-era venv, say) shadows it, and <c>rig</c> then fails in ways that look like rig's own bugs.
/// Pure: everything it reads from the environment is a parameter; it only looks at files.
/// </summary>
public static class RigCopies
{
    /// <summary>Lists and classifies every <c>rig</c> on <paramref name="path"/> and says what to fix.</summary>
    /// <param name="path">The PATH value.</param>
    /// <param name="extensions">The <c>PATHEXT</c> value on Windows; null elsewhere.</param>
    /// <param name="home">The home folder the shim lives under.</param>
    /// <param name="runtimeRoot">The runtime folder (<see cref="RuntimeManager.RuntimeRoot"/>).</param>
    /// <param name="windows">Whether the shim is <c>rig.cmd</c> and the copies <c>rig.exe</c>.</param>
    public static RigCopiesReport Check(string? path, string? extensions, string home, string runtimeRoot, bool windows)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(runtimeRoot);
        var shim = RigShim.PathFor(home, windows);
        var copies = ProgramPath.FindAll("rig", path, extensions)
            .Select(found => OnDisk(Full(found)))
            .Distinct(PathResolution.Comparer)
            .Select(found => new RigCopy(found, Classify(found, shim, runtimeRoot, windows)))
            .ToList();
        return new RigCopiesReport(copies, Findings(copies, path, shim));
    }

    /// <summary>What the <c>rig</c> at <paramref name="file"/> is.</summary>
    /// <param name="file">The file.</param>
    /// <param name="shim">The shim's path (<see cref="RigShim.PathFor"/>).</param>
    /// <param name="runtimeRoot">The runtime folder.</param>
    /// <param name="windows">Whether the runtime's copies are <c>rig.exe</c>.</param>
    public static RigCopyKind Classify(string file, string shim, string runtimeRoot, bool windows)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(shim);
        ArgumentNullException.ThrowIfNull(runtimeRoot);
        var full = Full(file);
        var root = Full(runtimeRoot);
        if (PathResolution.Same(full, Full(shim)))
        {
            return RigCopyKind.Shim;
        }

        if (PathResolution.Same(full, Full(RuntimeManager.Published(RuntimeManager.CurrentLink(root), "rig", windows))))
        {
            return RigCopyKind.Current;
        }

        if (PathResolution.IsUnder(full, root))
        {
            return RigCopyKind.Pinned;
        }

        if (Path.GetDirectoryName(full) is { } folder && PythonVenvScripts(folder))
        {
            return RigCopyKind.PythonVenv;
        }

        return BuildOutput(full) ? RigCopyKind.BuildOutput : RigCopyKind.Other;
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is a Python virtualenv's <c>Scripts</c> (Windows) or <c>bin</c> folder: the folder
    /// above it holds <c>pyvenv.cfg</c>.
    /// </summary>
    public static bool PythonVenvScripts(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var name = Path.GetFileName(trimmed);
        return (name.Equals("Scripts", StringComparison.OrdinalIgnoreCase) || name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            && Path.GetDirectoryName(trimmed) is { } venv
            && File.Exists(Path.Join(venv, "pyvenv.cfg"));
    }

    // Paths in findings use forward slashes, so a report reads the same on every platform.
    private static string Shown(string path) => path.Replace('\\', '/');

    private static string Full(string path) => Path.GetFullPath(path);

    // The file name as the folder spells it: PATHEXT is upper-case, so Windows finds `rig.cmd` as `rig.CMD`.
    private static string OnDisk(string file) =>
        Directory.EnumerateFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file)).FirstOrDefault() ?? file;

    private static bool BuildOutput(string file)
    {
        var parts = file.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Zip(parts.Skip(1)).Any(pair =>
            pair.First.Equals("bin", StringComparison.OrdinalIgnoreCase)
            && (pair.Second.Equals("Debug", StringComparison.OrdinalIgnoreCase) || pair.Second.Equals("Release", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<RigFinding> Findings(List<RigCopy> copies, string? path, string shim)
    {
        var findings = new List<RigFinding>();
        var shimFolder = Path.GetDirectoryName(shim)!;
        if (!copies.Any(copy => copy.Healthy))
        {
            findings.Add(new(true, NoHealthyCopy(path, shim, shimFolder)));
        }

        if (copies is [{ Healthy: false } first, ..])
        {
            var folder = Shown(Path.GetDirectoryName(first.Path)!);
            findings.Add(new(true, $"`rig` runs {Shown(first.Path)} ({first.Label}), not the installed rig. Put {Shown(shimFolder)} before {folder} on PATH, or remove {folder} from PATH."));
        }

        foreach (var copy in copies.Where(copy => copy.Kind == RigCopyKind.PythonVenv))
        {
            var scripts = Path.GetDirectoryName(copy.Path)!;
            var venv = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(scripts))!;
            findings.Add(new(true, $"{Shown(scripts)} holds the old Python rig CLI, which no longer runs. Delete the virtualenv {Shown(venv)}, or remove {Shown(scripts)} from PATH."));
        }

        foreach (var copy in copies.Skip(1).Where(copy => copy.Kind != RigCopyKind.PythonVenv))
        {
            findings.Add(new(false, $"{Shown(copy.Path)} ({copy.Label}) is also on PATH but never runs as `rig`; nothing to do unless you call it by path."));
        }

        return findings;
    }

    private static string NoHealthyCopy(string? path, string shim, string shimFolder)
    {
        if (!File.Exists(shim))
        {
            return $"The rig shim {Shown(shim)} is missing. Run `rig runtime install` to write it.";
        }

        var listed = (path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => PathResolution.Same(Full(folder), Full(shimFolder)));
        return listed
            ? $"The rig shim {Shown(shim)} is on PATH but cannot run. Run `rig runtime install` to rewrite it."
            : $"{Shown(shimFolder)} is not on PATH, so the rig shim there never runs. Add it to PATH, ahead of any other rig.";
    }
}
