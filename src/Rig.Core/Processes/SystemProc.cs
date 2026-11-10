using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Rig.Core.Processes;

/// <summary>
/// Runs real processes: no shell, standard input closed unless text is given, UTF-8 output, no console window,
/// and a trace span per call. A missing program gives 127 and a timeout 124.
/// </summary>
public sealed class SystemProc : IProc
{
    /// <summary>The trace source every process span is recorded under.</summary>
    public static readonly ActivitySource Source = new("Rig.Proc");

    // How long to wait for a killed process tree to be reaped before giving up on it.
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = Source.StartActivity(request.Argv[0]);
        activity?.SetTag("process.command_args", string.Join(' ', request.Argv));

        var info = new ProcessStartInfo(request.Argv[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in request.Argv.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.Environment)
        {
            info.Environment[name] = value;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return Finish(activity, new Ran(Ran.NotFound, string.Empty, $"{request.Argv[0]} is not on PATH"));
        }

        // Drain both outputs and arm the timeout before feeding stdin: a child that writes while it reads would
        // otherwise fill its output pipe while rig is still filling its input pipe, and neither would move.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        // The feed runs beside the wait, not before it: a child that never reads would block a write into a full
        // pipe, and the timeout must still fire. Once the child is gone the pipe breaks and the feed ends.
#pragma warning disable CA2025 // The finally below awaits the feed before process and timeout are disposed.
        var feed = FeedAsync(process, request.StdinText, timeout.Token);
#pragma warning restore CA2025
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Whoever stopped waiting, the child stops too: a cancelled git must not go on holding locks.
            Stop(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return Finish(activity, new Ran(Ran.TimedOut, string.Empty, $"{request.Argv[0]} timed out after {request.Timeout!.Value.TotalSeconds:0}s"));
        }
        finally
        {
            // Never leaves the feed running past the process it writes to.
            await feed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return Finish(activity, new Ran(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false)));
    }

    private static async Task FeedAsync(Process process, string? text, CancellationToken cancellationToken)
    {
        try
        {
            if (text is not null)
            {
                await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child exited or closed its input before reading it all; its exit code says how that went.
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(KillGrace);
        }
        catch (InvalidOperationException)
        {
            // It had already exited.
        }
    }

    private static Ran Finish(Activity? activity, Ran ran)
    {
        activity?.SetTag("process.exit.code", ran.Code);
        if (!ran.Ok)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        return ran;
    }
}
