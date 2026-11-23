using System.Text.Json.Nodes;

namespace Rig.Core.Chrome.Extension;

/// <summary>What <see cref="ExtensionInstaller"/> found or changed, one line per file, for people.</summary>
/// <param name="Lines">The lines, in the order the files were handled.</param>
public sealed record InstallReport(IReadOnlyList<string> Lines);

/// <summary>
/// Puts rig's Chrome extension where Chromium loads it and registers its native host, on Linux:
/// <list type="bullet">
/// <item>the extension is copied to <c>~/.local/share/rig/chrome-extension</c>, a folder no runtime update removes;</item>
/// <item>each browser's <c>NativeMessagingHosts</c> folder gets the host manifest naming <paramref name="hostPath"/>;</item>
/// <item><c>~/.config/chromium-flags.conf</c> gets the folder added to its <c>--load-extension</c> list, beside whatever
/// else is there. Google Chrome ignores the flag since version 137, so there the folder is loaded by hand once.</item>
/// </list>
/// Running it again changes only what differs.
/// </summary>
/// <param name="home">The user's home.</param>
/// <param name="hostPath">The program Chrome starts as the host: rig's PATH shim, so a runtime update needs no reinstall.</param>
public sealed class ExtensionInstaller(string home, string hostPath)
{
    /// <summary>The browsers whose config folders get the host manifest, when they exist.</summary>
    public static readonly IReadOnlyList<string> Browsers = ["chromium", "google-chrome", "google-chrome-beta", "google-chrome-unstable"];

    private const string LoadExtension = "--load-extension=";

    /// <summary>The program Chrome starts as the host.</summary>
    public string HostPath => hostPath;

    /// <summary>The same installer with another host program, such as a development build.</summary>
    public ExtensionInstaller WithHost(string program) => new(home, program);

    /// <summary>Where the extension is copied to.</summary>
    public string ExtensionFolder => Path.Join(home, ".local", "share", "rig", "chrome-extension");

    /// <summary>The Chromium launcher's flags file.</summary>
    public string FlagsFile => Path.Join(home, ".config", "chromium-flags.conf");

    /// <summary>The host manifest's path for <paramref name="browser"/>.</summary>
    public string HostManifest(string browser) => Path.Join(home, ".config", browser, "NativeMessagingHosts", ExtensionProtocol.HostName + ".json");

    /// <summary>The browsers installed for this user: those with a config folder.</summary>
    public IReadOnlyList<string> InstalledBrowsers() => [.. Browsers.Where(browser => Directory.Exists(Path.Join(home, ".config", browser)))];

    /// <summary>The host manifest's content.</summary>
    public JsonObject Manifest() => new()
    {
        ["name"] = ExtensionProtocol.HostName,
        ["description"] = "rig chrome-tabs: relays rig requests to its extension",
        ["path"] = hostPath,
        ["type"] = "stdio",
        ["allowed_origins"] = new JsonArray(ExtensionProtocol.Origin),
    };

    /// <summary>Installs from <paramref name="source"/>, the extension folder shipped beside rig.</summary>
    public InstallReport Install(string source)
    {
        if (!File.Exists(Path.Join(source, "manifest.json")))
        {
            throw new DirectoryNotFoundException($"No extension at {source}: its manifest.json is missing");
        }

        var lines = new List<string> { Copy(source, ExtensionFolder) };
        var browsers = InstalledBrowsers();
        if (browsers.Count == 0)
        {
            lines.Add("No Chromium or Chrome config folder under ~/.config: start the browser once, then install again");
        }

        var manifest = Manifest().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        foreach (var browser in browsers)
        {
            var path = HostManifest(browser);
            lines.Add(WriteIfChanged(path, manifest) ? $"Wrote {path}" : $"Unchanged {path}");
        }

        if (Directory.Exists(Path.Join(home, ".config", "chromium")) || File.Exists(FlagsFile))
        {
            var before = File.Exists(FlagsFile) ? File.ReadAllText(FlagsFile) : "";
            var after = WithExtension(before, ExtensionFolder);
            lines.Add(WriteIfChanged(FlagsFile, after) ? $"Added the extension to --load-extension in {FlagsFile}; restart Chromium to load it" : $"Unchanged {FlagsFile}");
        }

        if (browsers.Any(browser => browser.StartsWith("google-chrome", StringComparison.Ordinal)))
        {
            lines.Add($"Google Chrome ignores --load-extension: load {ExtensionFolder} once from chrome://extensions (Developer mode, Load unpacked)");
        }

        return new InstallReport(lines);
    }

    /// <summary>Removes everything <see cref="Install"/> wrote, leaving the rest of the flags file as it was.</summary>
    public InstallReport Uninstall()
    {
        var lines = new List<string>();
        foreach (var browser in Browsers)
        {
            var path = HostManifest(browser);
            if (File.Exists(path))
            {
                File.Delete(path);
                lines.Add($"Removed {path}");
            }
        }

        if (File.Exists(FlagsFile))
        {
            var before = File.ReadAllText(FlagsFile);
            var after = WithoutExtension(before, ExtensionFolder);
            if (after != before)
            {
                File.WriteAllText(FlagsFile, after);
                lines.Add($"Removed the extension from {FlagsFile}");
            }
        }

        if (Directory.Exists(ExtensionFolder))
        {
            Directory.Delete(ExtensionFolder, recursive: true);
            lines.Add($"Removed {ExtensionFolder}");
        }

        if (lines.Count == 0)
        {
            lines.Add("Nothing installed");
        }

        return new InstallReport(lines);
    }

    /// <summary>Whether each piece is in place, for <c>extension status</c>.</summary>
    public InstallReport Check()
    {
        var lines = new List<string>
        {
            File.Exists(Path.Join(ExtensionFolder, "manifest.json")) ? $"Extension: {ExtensionFolder}" : $"Extension: missing ({ExtensionFolder})",
        };
        foreach (var browser in InstalledBrowsers())
        {
            var path = HostManifest(browser);
            var state = !File.Exists(path) ? "missing"
                : JsonNode.Parse(File.ReadAllText(path))?["path"]?.GetValue<string>() is { } named && named == hostPath ? "ok"
                : "names another program";
            lines.Add($"Host manifest ({browser}): {state}");
        }

        if (File.Exists(FlagsFile))
        {
            lines.Add(Folders(File.ReadAllText(FlagsFile)).Contains(ExtensionFolder, StringComparer.Ordinal)
                ? $"Chromium flags: loads the extension"
                : $"Chromium flags: --load-extension does not name the extension");
        }

        return new InstallReport(lines);
    }

    /// <summary>
    /// <paramref name="flags"/> with <paramref name="folder"/> in its <c>--load-extension</c> list, adding the line if
    /// there is none. Only that argument changes: a line can hold several, and the others keep their text and quoting.
    /// </summary>
    public static string WithExtension(string flags, string folder)
    {
        ArgumentNullException.ThrowIfNull(flags);
        var lines = Lines(flags);
        if (FindLoad(lines) is not { } load)
        {
            lines.Add(LoadExtension + folder);
            return string.Join('\n', lines) + "\n";
        }

        if (load.Folders.Contains(folder, StringComparer.Ordinal))
        {
            return flags;
        }

        lines[load.Line] = load.Replace([.. load.Folders, folder]);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// <paramref name="flags"/> without <paramref name="folder"/>. The argument goes when it names nothing else, and
    /// its line when nothing else is on it.
    /// </summary>
    public static string WithoutExtension(string flags, string folder)
    {
        ArgumentNullException.ThrowIfNull(flags);
        var lines = Lines(flags);
        if (FindLoad(lines) is not { } load || !load.Folders.Contains(folder, StringComparer.Ordinal))
        {
            return flags;
        }

        var rest = load.Folders.Where(other => other != folder).ToList();
        var line = load.Replace(rest);
        if (line.Trim().Length == 0)
        {
            lines.RemoveAt(load.Line);
        }
        else
        {
            lines[load.Line] = line;
        }

        return lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
    }

    private static List<string> Lines(string flags)
    {
        var text = flags.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        return text.Length == 0 ? [] : [.. text.Split('\n')];
    }

    private static List<string> Folders(string flags) => FindLoad(Lines(flags))?.Folders ?? [];

    /// <summary>
    /// The first <c>--load-extension=</c> argument outside a comment: an argument starts the line or follows
    /// whitespace, and runs to the next whitespace outside quotes.
    /// </summary>
    private static LoadArgument? FindLoad(List<string> lines)
    {
        for (var number = 0; number < lines.Count; number++)
        {
            var line = lines[number];
            if (line.AsSpan().TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var at = 0;
            while (at < line.Length)
            {
                while (at < line.Length && char.IsWhiteSpace(line[at]))
                {
                    at++;
                }

                var start = at;
                char? quote = null;
                while (at < line.Length && (quote is not null || !char.IsWhiteSpace(line[at])))
                {
                    quote = line[at] is '"' or '\'' ? (quote is null ? line[at] : quote == line[at] ? null : quote) : quote;
                    at++;
                }

                var argument = line[start..at];
                var bare = argument.Replace("\"", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
                if (bare.StartsWith(LoadExtension, StringComparison.Ordinal))
                {
                    var folders = bare[LoadExtension.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return new LoadArgument(number, line, start, at, argument.Length != bare.Length, [.. folders]);
                }
            }
        }

        return null;
    }

    /// <summary>One <c>--load-extension=</c> argument: where it sits in its line and the folders it names.</summary>
    private sealed record LoadArgument(int Line, string Text, int Start, int End, bool Quoted, List<string> Folders)
    {
        /// <summary>The line with this argument naming <paramref name="folders"/>, or gone when they are none.</summary>
        public string Replace(List<string> folders)
        {
            var argument = folders.Count == 0 ? "" : LoadExtension + string.Join(',', folders);
            if (Quoted && argument.Length > 0)
            {
                argument = $"\"{argument}\"";
            }

            var before = Text[..Start];
            var after = Text[End..];
            return argument.Length > 0 ? before + argument + after : (before.TrimEnd() + (before.Trim().Length > 0 && after.Trim().Length > 0 ? " " : "") + after.TrimStart());
        }
    }

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return true;
    }

    private static string Copy(string source, string target)
    {
        var changed = 0;
        Directory.CreateDirectory(target);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            wanted.Add(relative);
            var destination = Path.Join(target, relative);
            if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            changed++;
        }

        foreach (var stale in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Where(file => !wanted.Contains(Path.GetRelativePath(target, file))).ToList())
        {
            File.Delete(stale);
            changed++;
        }

        return changed == 0 ? $"Unchanged {target}" : $"Copied the extension to {target}; reload it in chrome://extensions if Chromium is running";
    }
}
