using System.Globalization;
using System.Text;

namespace Rig.Core.Desktop;

/// <summary>
/// A restore's run log on disk: every line is appended to the run's own timestamped file, and the file holding the
/// latest run is rewritten with all of them. Ported from <c>desktop_manager.models.RestoreRunLogger</c> and
/// <c>plan.restore_log_paths</c>. The caller chooses the folder; reading <c>DESKTOP_MANAGER_LOG_DIR</c> stays with it.
/// </summary>
public sealed class FileRunLog : IRunLog
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _time;
    private readonly List<string> _lines = [];

    /// <summary>A log for a run starting now in <paramref name="directory"/>.</summary>
    public FileRunLog(string directory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        RunPath = Path.Combine(directory, $"workspace-restore-{stamp}.log");
        LatestPath = Path.Combine(directory, "workspace-restore-latest.log");
    }

    /// <summary>This run's own file.</summary>
    public string RunPath { get; }

    /// <summary>The file that always holds the latest run.</summary>
    public string LatestPath { get; }

    /// <summary>Every line logged so far, stamped.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <inheritdoc/>
    public void Log(string message)
    {
        var line = $"[{_time.GetLocalNow().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)}] {message}";
        Directory.CreateDirectory(Path.GetDirectoryName(RunPath)!);
        _lines.Add(line);
        File.AppendAllText(RunPath, line + "\n", Utf8);
        File.WriteAllText(LatestPath, string.Join('\n', _lines) + "\n", Utf8);
    }

    /// <inheritdoc/>
    public void LogFailure(string message, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Log($"{message}\n{failure.ToString().TrimEnd()}");
    }
}
