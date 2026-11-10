using System.Text.RegularExpressions;

namespace Rig.Core.GitHub;

/// <summary>A GitHub repository's owner and name, as spelled in the remote, parsed from any form a remote URL takes.</summary>
/// <param name="Owner">The owner (user or organisation).</param>
/// <param name="Name">The repository name.</param>
public sealed partial record RepoSlug(string Owner, string Name)
{
    /// <summary>Hosts that serve github.com repos. Enterprise hosts are not part of the estate and parse to null.</summary>
    public static readonly IReadOnlySet<string> GitHubHosts =
        new HashSet<string>(["github.com", "www.github.com", "ssh.github.com"], StringComparer.Ordinal);

    /// <summary><c>owner/name</c> as spelled in the remote.</summary>
    public string FullName => $"{Owner}/{Name}";

    /// <summary><c>owner/name</c> in lowercase, for comparing slugs: GitHub ignores case.</summary>
    public string Key => FullName.ToLowerInvariant();

    /// <summary>
    /// Parses a GitHub remote, or returns null for anything else. Accepts <c>https://github.com/o/r(.git)</c>,
    /// <c>ssh://git@github.com[:port]/o/r.git</c>, <c>git@github.com:o/r.git</c>, <c>git+https://…</c> and Orca's
    /// scheme-less <c>github.com/o/r</c>.
    /// </summary>
    /// <param name="remote">A git remote; not always a URI (scp-like forms are not).</param>
    public static RepoSlug? FromRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        var trimmed = remote.Trim();
        var (host, path) = Split(trimmed.StartsWith("git+", StringComparison.Ordinal) ? trimmed[4..] : trimmed);
        return GitHubHosts.Contains(host.ToLowerInvariant()) ? FromPath(path) : null;
    }

    /// <summary>Parses <c>owner/name</c>; an optional <c>.git</c> and surrounding slashes are ignored.</summary>
    public static RepoSlug? FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bare = path.Trim('/');
        if (bare.EndsWith(".git", StringComparison.Ordinal))
        {
            bare = bare[..^4];
        }

        var parts = bare.Split('/');
        return parts.Length == 2 && parts.All(part => Segment().IsMatch(part)) ? new RepoSlug(parts[0], parts[1]) : null;
    }

    /// <inheritdoc />
    public override string ToString() => FullName;

    private static (string Host, string Path) Split(string url)
    {
        if (url.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? (uri.Host, Uri.UnescapeDataString(uri.AbsolutePath))
                : (string.Empty, string.Empty);
        }

        var scp = ScpLike().Match(url);
        if (scp.Success)
        {
            return (scp.Groups["host"].Value, scp.Groups["path"].Value);
        }

        var slash = url.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (url, string.Empty) : (url[..slash], url[(slash + 1)..]);
    }

    [GeneratedRegex(@"^[\w.-]+\z")]
    private static partial Regex Segment();

    [GeneratedRegex(@"^(?:[\w.-]+@)?(?<host>[\w.-]+):(?!//)(?<path>.+)\z")]
    private static partial Regex ScpLike();
}
