using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Vault;

/// <summary>
/// Orphans as the Obsidian desktop app sees them, from <c>obsidian orphans</c>. The app answers about whichever vault it has
/// open and takes no path, so this answers only for that vault, read from the app's <c>obsidian.json</c>; asked about any
/// other vault, or when the app fails, stalls past the timeout or is missing, it answers with nothing.
/// </summary>
/// <param name="proc">Runs the <c>obsidian</c> binary.</param>
/// <param name="home">The home folder the app's settings live under, <c>Library/Application Support/obsidian</c>.</param>
public sealed class ObsidianOrphans(IProc proc, string home)
{
    /// <summary>How long the desktop app gets to answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The app's orphan list, stripped and without blank lines, or nothing unless <paramref name="vault"/> is the vault it has open.</summary>
    public async Task<IReadOnlyList<string>> ListAsync(string vault, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var open = OpenVault();
        if (open is null || RealPath(open) != RealPath(vault))
        {
            return [];
        }

        var ran = await proc.RunAsync(new ProcRequest(["obsidian", "orphans"]) { Timeout = Timeout }, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            return [];
        }

        var text = ran.Out.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return [.. PyText.SplitLines(text).Select(PyText.Strip).Where(line => line.Length > 0)];
    }

    // obsidian.json holds {"vaults": {id: {"path": str, "open": bool}}}; a misshapen entry is passed over, a misshapen file ignored.
    private string? OpenVault()
    {
        var registry = Path.Join(home, "Library", "Application Support", "obsidian", "obsidian.json");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(registry));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }

        if (root is not JsonObject document || (document["vaults"] is { } vaults && vaults is not JsonObject))
        {
            return null;
        }

        foreach (var (_, entry) in document["vaults"]?.AsObject() ?? [])
        {
            if (entry is JsonObject fields
                && Field(fields, "path", JsonValueKind.String) is { } path
                && Field(fields, "open", JsonValueKind.True, JsonValueKind.False) is { } open
                && open.GetValueKind() == JsonValueKind.True
                && path.GetValue<string>().Length > 0)
            {
                return path.GetValue<string>();
            }
        }

        return null;
    }

    private static JsonNode? Field(JsonObject fields, string name, params JsonValueKind[] kinds) =>
        fields[name] is { } value && kinds.Contains(value.GetValueKind()) ? value : null;

    /// <summary>
    /// The absolute path with every symlink along it followed, as Python's <c>Path.resolve()</c> gives it; a part that does not
    /// exist is kept as written.
    /// </summary>
    internal static string RealPath(string path)
    {
        // Path.GetFullPath would fold "a/link/.." to "a" before the link is followed; resolve() folds it after.
        var full = Path.IsPathFullyQualified(path) ? path
            : Path.IsPathRooted(path) ? Path.GetFullPath(path)
            : Path.Join(Directory.GetCurrentDirectory(), path);
        var resolved = Path.GetPathRoot(full)!;
        var pending = new Stack<string>(Parts(full[resolved.Length..]));
        var hops = 0;
        while (pending.TryPop(out var part))
        {
            if (part == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            var next = Path.Join(resolved, part);
            if (new FileInfo(next).LinkTarget is { } target && hops++ < 40)
            {
                if (Path.IsPathRooted(target))
                {
                    resolved = Path.GetPathRoot(target)!;
                    target = target[resolved.Length..];
                }

                foreach (var step in Parts(target))
                {
                    pending.Push(step);
                }
            }
            else
            {
                resolved = next;
            }
        }

        return resolved;
    }

    // A path's parts last first, ready to push onto the stack of parts still to walk.
    private static IEnumerable<string> Parts(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != ".")
            .Reverse();
}
