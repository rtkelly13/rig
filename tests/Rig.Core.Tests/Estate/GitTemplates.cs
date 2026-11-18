using System.Collections.Concurrent;
using Rig.Core.Roots;

namespace Rig.Core.Tests.Estate;

/// <summary>
/// Git layouts built once per test process and copied into each test's folder. Building a clone and its origin costs a
/// dozen git processes; copying the result costs none, and the copy is the same repository because the template's own
/// path is the only thing git wrote into it.
/// </summary>
internal static class GitTemplates
{
    // Unique to this process, so it finds the template's path however git spelled it.
    private static readonly string Marker = $"templates-{Guid.NewGuid():N}";

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "rig-tests", Marker);

    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Built = new(StringComparer.Ordinal);

    private static readonly string[] PathBearing = ["config", "gitdir", "commondir", ".git"];

    static GitTemplates() => AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteHome();

    /// <summary>
    /// Copies the layout <paramref name="build"/> makes under the folder it is handed into <paramref name="destination"/>,
    /// building it on the first call for <paramref name="key"/>. Git's own files that name the template folder are
    /// rewritten to name <paramref name="destination"/> instead.
    /// </summary>
    public static async Task CopyAsync(string key, Func<string, Task> build, string destination)
    {
        var template = await Built.GetOrAdd(key, name => new Lazy<Task<string>>(() => BuildAsync(name, build))).Value;
        Copy(template, destination, text => Rehome(text, template, destination));
    }

    // Git's config writes a backslash as \\, so a Windows path appears escaped there and as written in gitdir files. A
    // copy still naming the template would share its origin with every other test, so that fails loudly.
    private static string Rehome(string text, string template, string destination)
    {
        var rehomed = text
            .Replace(Escaped(template), Escaped(destination), StringComparison.Ordinal)
            .Replace(template, destination, StringComparison.Ordinal);
        Assert.False(rehomed.Contains(Marker, StringComparison.Ordinal), $"expected every path in the copy rehomed to {destination}, found the template's in: {rehomed}");
        return rehomed;
    }

    private static string Escaped(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static async Task<string> BuildAsync(string key, Func<string, Task> build)
    {
        var root = PathResolution.Resolve(Directory.CreateDirectory(Path.Combine(Home, key)).FullName);
        await build(root);
        return root;
    }

    private static void Copy(string from, string to, Func<string, string> rehome)
    {
        _ = Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Join(to, Path.GetFileName(file));
            if (PathBearing.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            {
                File.WriteAllText(target, rehome(File.ReadAllText(file)));
            }
            else
            {
                File.Copy(file, target);
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            Copy(folder, Path.Join(to, Path.GetFileName(folder)), rehome);
        }
    }

    private static void DeleteHome()
    {
        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the OS temp cleaner, as TempDir does.
        }
    }
}
