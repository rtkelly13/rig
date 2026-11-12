using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Rig.Core.Jobs;

/// <summary>
/// Starts a job as a process with no window and no input, its stdout and stderr appended to its log as they come.
/// Ported from <c>desktop_manager.jobs.runner.execute_subprocess</c>: the same header, end markers and environment.
/// </summary>
/// <param name="clock">Stamps the header in local time.</param>
/// <param name="timeout">Overrides each job's <see cref="Job.TimeoutMinutes"/>; for tests.</param>
public sealed class ProcessJobExecutor(TimeProvider clock, TimeSpan? timeout = null) : IJobExecutor
{
    /// <summary>The variable that tells a job which coordinator run started it.</summary>
    public const string RunIdVariable = "SHARED_UTILITIES_JOB_RUN";

    /// <inheritdoc/>
    public long Execute(Job job, string logsDir, string runId)
    {
        ArgumentNullException.ThrowIfNull(job);
        Directory.CreateDirectory(logsDir);
        using var log = new FileStream(JobState.LogPath(logsDir, job.Name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var gate = new Lock();
        void Write(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            lock (gate)
            {
                log.Write(bytes);
                log.Flush();
            }
        }

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

        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment[RunIdVariable] = runId;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{job.Argv[0]} did not start");
        process.StandardInput.Close();
        var copies = new[] { process.StandardOutput.BaseStream, process.StandardError.BaseStream }
            .Select(stream => Task.Run(() => Copy(stream, log, gate)))
            .ToArray();
        var limit = timeout ?? TimeSpan.FromMinutes(job.TimeoutMinutes);
        if (!process.WaitForExit(limit))
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
    private static void WaitFor(Task[] copies) => _ = Task.WaitAll(copies, TimeSpan.FromSeconds(5));

    // The raw bytes, so the log keeps whatever the job wrote, in the order each pipe delivered it.
    private static void Copy(Stream from, FileStream to, Lock gate)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = from.Read(buffer)) > 0)
            {
                lock (gate)
                {
                    to.Write(buffer, 0, read);
                    to.Flush();
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The run gave up waiting for a pipe a grandchild kept open, and closed the log.
        }
    }
}
