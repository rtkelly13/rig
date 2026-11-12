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

    // How long to wait for a killed process tree to be reaped, or a stopped feed to end, before giving up on it.
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public Ran Run(ProcRequest request)
    {
        using var launch = Launch.Start(request);
        if (launch.Process is not { } process)
        {
            return launch.Finish(launch.Missing!);
        }

        // Each pipe gets its own thread, started before the wait, for the reasons the async path drains and feeds
        // beside its wait: neither a child that writes while it reads nor one that never reads can stall the run.
        var output = launch.Captured ? Pipe.Drain(process.StandardOutput) : null;
        var errors = launch.Piped ? Pipe.Drain(process.StandardError) : null;
        var feed = launch.Piped ? Pipe.Feed(process.StandardInput, request.StdinText) : null;
        var deadline = request.Timeout is { } limit ? Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency) : (long?)null;
        try
        {
            // The reads are bounded too: a grandchild that inherited the pipes keeps them open after the child exits.
            if (!process.WaitForExit(Remaining(deadline)) || output?.Ended(Remaining(deadline)) == false || errors?.Ended(Remaining(deadline)) == false)
            {
                Stop(process);
                return launch.Finish(TimedOut(request));
            }
        }
        finally
        {
            // Never leaves the feed running past the process it writes to: once the child is gone its pipe breaks.
            _ = feed?.Ended(KillGrace);
        }

        return launch.Finish(new Ran(process.ExitCode, output?.Text ?? string.Empty, errors?.Text ?? string.Empty));
    }

    /// <inheritdoc />
    public async Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
    {
        using var launch = Launch.Start(request);
        if (launch.Process is not { } process)
        {
            return launch.Finish(launch.Missing!);
        }

        // Drain both outputs and arm the timeout before feeding stdin: a child that writes while it reads would
        // otherwise fill its output pipe while rig is still filling its input pipe, and neither would move.
        var stdout = launch.Captured ? process.StandardOutput.ReadToEndAsync(CancellationToken.None) : Task.FromResult(string.Empty);
        var stderr = launch.Piped ? process.StandardError.ReadToEndAsync(CancellationToken.None) : Task.FromResult(string.Empty);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        // The feed runs beside the wait, not before it: a child that never reads would block a write into a full
        // pipe, and the timeout must still fire. Once the child is gone the pipe breaks and the feed ends.
#pragma warning disable CA2025 // The finally below awaits the feed before process and timeout are disposed.
        var feed = launch.Piped ? FeedAsync(process, request.StdinText, timeout.Token) : Task.CompletedTask;
#pragma warning restore CA2025
        string output, errors;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            // The reads are bounded too: a grandchild that inherited the pipes keeps them open after the child exits.
            output = await stdout.WaitAsync(timeout.Token).ConfigureAwait(false);
            errors = await stderr.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Whoever stopped waiting, the child stops too: a cancelled git must not go on holding locks.
            Stop(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return launch.Finish(TimedOut(request));
        }
        finally
        {
            // Never leaves the feed running past the process it writes to.
            await feed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return launch.Finish(new Ran(process.ExitCode, output, errors));
    }

    /// <summary>
    /// The <c>cmd.exe</c> arguments that run a Windows batch file, such as an npm <c>.cmd</c> shim, or null when the
    /// program is not one: Windows cannot start a batch file without its command interpreter. Arguments are quoted
    /// whole, so spaces and <c>&amp; | &lt; &gt;</c> pass through as text; <c>%</c> is still expanded by cmd.
    /// </summary>
    public static string? BatchLine(IReadOnlyList<string> argv, bool windows)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var program = argv[0];
        if (!windows || !(program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return $"/d /s /c \"{string.Join(' ', argv.Select(Quote))}\"";
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')')
            ? argument
            : $"\"{argument.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static Ran TimedOut(ProcRequest request) =>
        new(Ran.TimedOut, string.Empty, $"{request.Argv[0]} timed out after {request.Timeout!.Value.TotalSeconds:0}s");

    private static TimeSpan Remaining(long? deadline) =>
        deadline is { } end ? TimeSpan.FromTicks(Math.Max(0, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), end).Ticks)) : Timeout.InfiniteTimeSpan;

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

    /// <summary>A started process and its span, or the 127 for a program that is not on PATH.</summary>
    private sealed class Launch : IDisposable
    {
        private readonly Activity? _activity;

        private Launch(Activity? activity, Process? process, bool captured, bool piped, Ran? missing)
        {
            _activity = activity;
            Process = process;
            Captured = captured;
            Piped = piped;
            Missing = missing;
        }

        public Process? Process { get; }

        /// <summary>Standard output is read.</summary>
        public bool Captured { get; }

        /// <summary>Standard input is fed and standard error read.</summary>
        public bool Piped { get; }

        public Ran? Missing { get; }

        public static Launch Start(ProcRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if ((request.Attached || request.ConsoleInput) && request.StdinText is not null)
            {
                throw new ArgumentException("A process reading this console's input cannot be given text.", nameof(request));
            }

            var activity = Source.StartActivity(request.Argv[0]);
            activity?.SetTag("process.command_args", string.Join(' ', request.Argv));

            var captured = !request.Attached;
            var piped = captured && !request.ConsoleInput;
            var info = new ProcessStartInfo
            {
                RedirectStandardInput = piped,
                RedirectStandardOutput = captured,
                RedirectStandardError = piped,
                StandardOutputEncoding = captured ? Encoding.UTF8 : null,
                StandardErrorEncoding = piped ? Encoding.UTF8 : null,
                UseShellExecute = false,
                CreateNoWindow = piped,
                WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            };
            if (BatchLine(request.Argv, OperatingSystem.IsWindows()) is { } line)
            {
                info.FileName = "cmd.exe";
                info.Arguments = line;
            }
            else
            {
                info.FileName = request.Argv[0];
                foreach (var argument in request.Argv.Skip(1))
                {
                    info.ArgumentList.Add(argument);
                }
            }

            foreach (var name in request.Unset)
            {
                _ = info.Environment.Remove(name);
            }

            foreach (var (name, value) in request.Environment)
            {
                info.Environment[name] = value;
            }

            var process = new Process { StartInfo = info };
            try
            {
                process.Start();
            }
            catch (Win32Exception)
            {
                process.Dispose();
                return new Launch(activity, null, captured, piped, new Ran(Ran.NotFound, string.Empty, $"{request.Argv[0]} is not on PATH"));
            }

            return new Launch(activity, process, captured, piped, null);
        }

        public Ran Finish(Ran ran)
        {
            _activity?.SetTag("process.exit.code", ran.Code);
            if (!ran.Ok)
            {
                _activity?.SetStatus(ActivityStatusCode.Error);
            }

            return ran;
        }

        public void Dispose()
        {
            Process?.Dispose();
            _activity?.Dispose();
        }
    }

    /// <summary>One pipe read to its end, or written and closed, on a background thread of its own.</summary>
    private sealed class Pipe
    {
        private readonly Thread _thread;

        private Pipe(Action<Pipe> work)
        {
            _thread = new Thread(() => work(this)) { IsBackground = true };
            _thread.Start();
        }

        /// <summary>What a drained pipe held; empty until it has ended.</summary>
        public string Text { get; private set; } = string.Empty;

        public static Pipe Drain(StreamReader reader) => new(pipe =>
        {
            try
            {
                pipe.Text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The process was stopped and disposed while a grandchild still held the pipe; the run timed out.
            }
        });

        public static Pipe Feed(StreamWriter writer, string? text) => new(_ =>
        {
            try
            {
                if (text is not null)
                {
                    writer.Write(text);
                }

                writer.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The child exited or closed its input before reading it all; its exit code says how that went.
            }
        });

        /// <summary>Whether the pipe's work finished within <paramref name="wait"/>.</summary>
        public bool Ended(TimeSpan wait) => _thread.Join(wait);
    }
}
