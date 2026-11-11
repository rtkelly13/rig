using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Core.Otel;

/// <summary>What <c>collector start</c> launched: enough to tell it from whatever later reuses its pid.</summary>
/// <param name="Pid">The process id.</param>
/// <param name="Binary">The binary it ran.</param>
/// <param name="Started"><c>ps -o lstart=</c> at launch; empty where there is no <c>ps</c> (Windows).</param>
public sealed record Launched(int Pid, string Binary, string Started = "")
{
    /// <summary>The pid file's text: tab-separated pid, binary and start time, one line.</summary>
    public string Serialise() => string.Create(CultureInfo.InvariantCulture, $"{Pid}\t{Binary}\t{Started}\n");

    /// <summary>A pid file's record, or null when it does not start with a pid.</summary>
    public static Launched? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fields = text.TrimEnd('\n').Split('\t');
        if (!CollectorProcess.IsDigits(fields[0]) || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return null;
        }

        return new Launched(pid, fields.Length > 1 ? fields[1] : string.Empty, fields.Length > 2 ? fields[2] : string.Empty);
    }
}

/// <summary>
/// The pure parts of running the collector without a service manager, ported from <c>rig.otel_stack.runtime</c>: the
/// forward endpoint's shape, who listens on a port from <c>netstat</c>'s output, and whether a command line is the core
/// collector. Something else can hold the ports, such as a Docker collector publishing 4317/4318, so <c>ensure</c> refuses
/// to treat a foreign collector as this one.
/// </summary>
public static partial class CollectorProcess
{
    /// <summary>The image name (Windows) or executable stem the core collector runs as.</summary>
    public const string CollectorName = "otelcol";

    /// <summary>
    /// <paramref name="endpoint"/> as a forward endpoint: an http(s) base URL with a host and port, given back as scheme and
    /// authority. The scheme is lower-cased and the authority kept as written, as Python's <c>urlsplit</c> did.
    /// A port that is not a number in range gets the "must look like" message, where the Python passed on
    /// <c>urlsplit</c>'s own <c>ValueError</c> text.
    /// </summary>
    /// <exception cref="FormatException">It is not such a URL.</exception>
    public static string ValidateForward(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var text = endpoint.Trim();
        var shape = new FormatException($"forward endpoint must look like http://127.0.0.1:24318, got {PyRepr(endpoint)}");
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var scheme = colon > 0 ? text[..colon].ToLowerInvariant() : string.Empty;
        if (scheme is not ("http" or "https") || !text[(colon + 1)..].StartsWith("//", StringComparison.Ordinal))
        {
            throw shape;
        }

        var rest = text[(colon + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var netloc = end < 0 ? rest : rest[..end];
        var tail = end < 0 ? string.Empty : rest[end..];
        if (!HasHostAndPort(netloc))
        {
            throw shape;
        }

        // urlsplit's parts after the authority: the fragment follows the first '#', the query the first '?' before it.
        var hash = tail.IndexOf('#', StringComparison.Ordinal);
        var fragment = hash < 0 ? string.Empty : tail[(hash + 1)..];
        var beforeHash = hash < 0 ? tail : tail[..hash];
        var mark = beforeHash.IndexOf('?', StringComparison.Ordinal);
        var query = mark < 0 ? string.Empty : beforeHash[(mark + 1)..];
        var path = mark < 0 ? beforeHash : beforeHash[..mark];
        if (path is not ("" or "/") || query.Length > 0 || fragment.Length > 0)
        {
            throw new FormatException($"forward endpoint is a base URL; the collector appends /v1/<signal> itself, got {PyRepr(endpoint)}");
        }

        return $"{scheme}://{netloc}";
    }

    /// <summary>The pid in <c>netstat -ano -p TCP</c> output listening on <paramref name="port"/> on any local address.</summary>
    public static int? ParseNetstatListener(string text, int port)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields is ["TCP", var local, _, "LISTENING", var pid]
                && local.EndsWith(string.Create(CultureInfo.InvariantCulture, $":{port}"), StringComparison.Ordinal)
                && IsDigits(pid)
                && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var found))
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="command"/> (an image name or command line) runs the core <c>otelcol</c>, not contrib or a container.</summary>
    public static bool IsCollector(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CollectorExecutable().IsMatch(command);
    }

    internal static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    // `otelcol` as a whole path component, alone or followed by its arguments; paths may hold spaces.
    [GeneratedRegex(@"(?:^|[/\\])otelcol(?:\.exe)?(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectorExecutable();

    // urlsplit's hostname and port: a host after any user info, then a port that is all digits and at most 65535.
    private static bool HasHostAndPort(string netloc)
    {
        var hostPort = netloc[(netloc.LastIndexOf('@') + 1)..];
        int portAt;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 2)
            {
                return false;
            }

            portAt = hostPort.IndexOf(':', close);
        }
        else
        {
            portAt = hostPort.IndexOf(':', StringComparison.Ordinal);
            if (portAt <= 0)
            {
                return false;
            }
        }

        var port = portAt < 0 ? string.Empty : hostPort[(portAt + 1)..];
        return IsDigits(port) && int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= 65535;
    }

    // Python's repr of a string, as the error messages quote it.
    private static string PyRepr(string text) =>
        text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal)
            ? $"\"{text}\""
            : "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
}
