using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Text;

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

    /// <summary>
    /// The app's orphan list, stripped and without blank lines, or nothing unless <paramref name="vault"/> is the vault it has open.
    /// <paramref name="problem"/> hears <c>obsidian.json: $.json.path: expected X, found Y</c> for each part of the app's settings it skipped.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAsync(string vault, Action<string>? problem = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var open = OpenVault(problem);
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
        return [.. UnicodeText.SplitLines(text).Select(UnicodeText.Strip).Where(line => line.Length > 0)];
    }

    // obsidian.json holds {"vaults": {id: {"path": str, "open": bool}}}, and is the app's own format, so fields rig does not read are
    // ignored. A misshapen file or entry is passed over and reported; a missing or unreadable file is not a fault.
    private string? OpenVault(Action<string>? problem)
    {
        var registry = Path.Join(home, "Library", "Application Support", "obsidian", "obsidian.json");
        string text;
        try
        {
            text = File.ReadAllText(registry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        try
        {
            return JsonProblem.Run(registry, () => OpenVaultIn(JsonNode.Parse(text), registry, problem));
        }
        catch (JsonInputException e)
        {
            problem?.Invoke(e.Message);
            return null;
        }
    }

    private static string? OpenVaultIn(JsonNode? root, string registry, Action<string>? problem)
    {
        var document = root as JsonObject ?? throw JsonProblem.Expected("$", "an object", JsonProblem.Describe(root));
        if (document["vaults"] is null)
        {
            return null;
        }

        var vaults = document["vaults"] as JsonObject ?? throw JsonProblem.Expected(document, "vaults", "an object", document["vaults"]);
        foreach (var (name, entry) in vaults)
        {
            try
            {
                if (OpenPath(entry as JsonObject ?? throw JsonProblem.Expected(JsonProblem.Child(vaults.GetPath(), name), "an object", JsonProblem.Describe(entry))) is { } path)
                {
                    return path;
                }
            }
            catch (JsonInputException e)
            {
                problem?.Invoke(e.Within(registry).Message);
            }
        }

        return null;
    }

    // The vault's path when it is the open one; a vault with no "open" is not open.
    private static string? OpenPath(JsonObject fields)
    {
        var path = fields["path"] is JsonValue { } value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw JsonProblem.Expected(fields, "path", "a string", fields["path"]);
        var open = fields["open"] switch
        {
            null => false,
            { } flag when flag.GetValueKind() == JsonValueKind.True => true,
            { } flag when flag.GetValueKind() == JsonValueKind.False => false,
            var other => throw JsonProblem.Expected(fields, "open", "a boolean", other),
        };
        return open && path.Length > 0 ? path : null;
    }

    /// <summary>
    /// The absolute path with every symlink along it followed, ; a part that does not
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
