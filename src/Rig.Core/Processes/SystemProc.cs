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

        if (request.StdinText is not null)
        {
            await process.StandardInput.WriteAsync(request.StdinText.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return Finish(activity, new Ran(Ran.TimedOut, string.Empty, $"{request.Argv[0]} timed out after {request.Timeout!.Value.TotalSeconds:0}s"));
        }

        return Finish(activity, new Ran(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false)));
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
