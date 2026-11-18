using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Rig.Core.Jobs;

/// <summary>
/// Starts a job as a process with no window and no input, its stdout and stderr appended to its log as they come.
/// </summary>
/// <param name="clock">Stamps the header in local time.</param>
/// <param name="environment">Variables each job gets unless this process already has them (<see cref="JobsSite.Environment"/>).</param>
/// <param name="timeout">Overrides each job's <see cref="Job.TimeoutMinutes"/>; for tests.</param>
public sealed class ProcessJobExecutor(TimeProvider clock, IReadOnlyDictionary<string, string>? environment = null, TimeSpan? timeout = null) : IJobExecutor
{
    /// <summary>The variable that tells a job which coordinator run started it.</summary>
    public const string RunIdVariable = "SHARED_UTILITIES_JOB_RUN";

    /// <inheritdoc/>
    public long Execute(Job job, string logsDir, string runId)
    {
        Directory.CreateDirectory(logsDir);
        using var log = new JobLog(JobState.LogPath(logsDir, job.Name));
        void Write(string text) => log.Write(Encoding.UTF8.GetBytes(text));

        Write(string.Create(CultureInfo.InvariantCulture, $"--- {clock.GetLocalNow():yyyy-MM-dd HH:mm:ss} [{runId}] {string.Join(' ', job.Argv)}{Environment.NewLine}"));
        var start = new ProcessStartInfo(job.Argv[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in job.Argv.Skip(1))
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            _ = start.Environment.TryAdd(name, value);
        }

        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment[RunIdVariable] = runId;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{job.Argv[0]} did not start");
        process.StandardInput.Close();
        var copies = new[] { process.StandardOutput.BaseStream, process.StandardError.BaseStream }
            .Select(stream => new Thread(() => Copy(stream, log)) { IsBackground = true, Name = "rig-job-log" })
            .ToArray();
        foreach (var copy in copies)
        {
            copy.Start();
        }

        var limit = timeout ?? TimeSpan.FromMinutes(job.TimeoutMinutes);
        if (limit <= TimeSpan.Zero || !process.WaitForExit(limit))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended on its own just as its time ran out; it still ran out of time.
            }

            process.WaitForExit();
            WaitFor(copies);
            Write($"--- timed out after {job.TimeoutMinutes} min{Environment.NewLine}");
            return JobRunner.TimedOut;
        }

        WaitFor(copies);
        var code = OperatingSystem.IsWindows() ? (long)(uint)process.ExitCode : process.ExitCode;
        Write(string.Create(CultureInfo.InvariantCulture, $"--- exit {code}{Environment.NewLine}"));
        return code;
    }

    // A grandchild that keeps the pipes open must not hold the run up past its exit.
    private static void WaitFor(Thread[] copies)
    {
        var deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
        foreach (var copy in copies)
        {
            var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            _ = copy.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }
    }

    // The raw bytes, so the log keeps whatever the job wrote, in the order each pipe delivered it.
    // It runs on its own thread, where an exception that escaped would end the process: a pipe that fails is the
    // end of that pipe, and nothing more.
    private static void Copy(Stream from, JobLog to)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = from.Read(buffer)) > 0)
            {
                to.Write(buffer.AsSpan(0, read));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
        {
            // The run gave up waiting for a pipe a grandchild kept open, and the process closed it.
        }
    }

    /// <summary>
    /// The log file, written by the run and by both pipe threads. Closing takes the same lock as writing, so a
    /// thread still copying after the run stopped waiting for it finds the log closed instead of half disposed.
    /// </summary>
    private sealed class JobLog(string path) : IDisposable
    {
        private readonly FileStream _file = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        private readonly Lock _gate = new();
        private bool _closed;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _file.Write(bytes);
                _file.Flush();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _file.Dispose();
            }
        }
    }
}
