using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Rig.Core.Processes;

namespace Rig.Core.Otel;

/// <summary>
/// Starts a program that outlives rig: standard input from the null device, output and errors appended to a log, and
/// out of rig's session so closing the terminal does not stop it. The Python did this with <c>Popen</c>
/// (<c>start_new_session</c>, or <c>DETACHED_PROCESS</c> on Windows).
/// </summary>
public interface IDetachedStart
{
    /// <summary>Starts <paramref name="argv"/> appending to <paramref name="log"/>; returns its pid.</summary>
    /// <param name="argv">The program and its arguments.</param>
    /// <param name="log">The log its output and errors are appended to; its folder must exist.</param>
    /// <param name="workingDirectory">Its working directory, or null for rig's.</param>
    /// <param name="environment">Variables added to the inherited environment.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <exception cref="FileNotFoundException">The program does not exist.</exception>
    Task<int> StartAsync(
        IReadOnlyList<string> argv,
        string log,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Unix: <c>/bin/sh</c> starts the program in the background and prints its pid. <c>setsid</c> where there is one
/// (Linux) gives it a session of its own; elsewhere (macOS) job control gives it its own process group. Hang-ups are
/// ignored either way, and the program replaces the shell's child, so the pid printed is the program's.
/// </summary>
/// <param name="proc">Runs the shell.</param>
public sealed class UnixDetachedStart(IProc proc) : IDetachedStart
{
    /// <summary>The shell script: <c>$1</c> is the log, the rest the program and its arguments.</summary>
    public const string Script =
        "log=$1; shift; trap '' HUP; "
        + "if command -v setsid >/dev/null 2>&1; then setsid \"$@\" </dev/null >>\"$log\" 2>&1 & "
        + "else set -m; \"$@\" </dev/null >>\"$log\" 2>&1 & fi; echo $!";

    /// <inheritdoc/>
    public async Task<int> StartAsync(
        IReadOnlyList<string> argv,
        string log,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentOutOfRangeException.ThrowIfZero(argv.Count);
        if (!File.Exists(argv[0]))
        {
            throw new FileNotFoundException($"no such program: {argv[0]}", argv[0]);
        }

        var request = new ProcRequest(["/bin/sh", "-c", Script, "sh", log, .. argv])
        {
            WorkingDirectory = workingDirectory,
            Environment = environment ?? new Dictionary<string, string>(),
        };
        var ran = (await proc.RunAsync(request, cancellationToken).ConfigureAwait(false)).EnsureOk(request.Argv);
        return int.TryParse(ran.Out.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : throw new ProcException(request.Argv, ran);
    }
}

/// <summary>
/// Windows: <c>CreateProcessW</c> with no console of its own and only the null device and the log inherited, so rig's
/// own handles (and any pipe a caller is reading) are not held open by the collector.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Windows only; the Windows CI job starts a real process through it, the ubuntu coverage run cannot.")]
public sealed class WindowsDetachedStart : IDetachedStart
{
    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">An environment was given; only the collector starts here, and it needs none.</exception>
    public Task<int> StartAsync(
        IReadOnlyList<string> argv,
        string log,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentOutOfRangeException.ThrowIfZero(argv.Count);
        if (environment is { Count: > 0 })
        {
            throw new NotSupportedException("a detached start on Windows takes no environment");
        }

        if (!File.Exists(argv[0]))
        {
            throw new FileNotFoundException($"no such program: {argv[0]}", argv[0]);
        }

        return Task.FromResult(NativeMethods.Start(WindowsCommandLine.Join(argv), workingDirectory, log));
    }
}

/// <summary>How Windows programs split a command line back into arguments, run the other way.</summary>
public static class WindowsCommandLine
{
    /// <summary>
    /// <paramref name="argv"/> as one command line, quoted as Python's <c>subprocess.list2cmdline</c> did: an
    /// argument with a space or tab, or an empty one, is quoted; backslashes are doubled only before a quote.
    /// </summary>
    public static string Join(IEnumerable<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var line = new StringBuilder();
        foreach (var arg in argv)
        {
            if (line.Length > 0)
            {
                _ = line.Append(' ');
            }

            var quote = arg.Length == 0 || arg.Contains(' ', StringComparison.Ordinal) || arg.Contains('\t', StringComparison.Ordinal);
            _ = quote ? line.Append('"') : line;
            var backslashes = 0;
            foreach (var c in arg)
            {
                switch (c)
                {
                    case '\\':
                        backslashes++;
                        break;
                    case '"':
                        _ = line.Append('\\', (backslashes * 2) + 1).Append('"');
                        backslashes = 0;
                        break;
                    default:
                        _ = line.Append('\\', backslashes).Append(c);
                        backslashes = 0;
                        break;
                }
            }

            _ = line.Append('\\', quote ? backslashes * 2 : backslashes);
            _ = quote ? line.Append('"') : line;
        }

        return line.ToString();
    }
}

// Win32 process creation. Runs only on Windows, where CI exercises it through the collector tests.
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; exercised by the Windows CI job, not measurable on the ubuntu coverage run.")]
internal static partial class NativeMethods
{
    private const uint GenericRead = 0x80000000;
    private const uint FileAppendData = 0x4;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;
    private const uint DetachedProcess = 0x8;
    private const uint CreateNewProcessGroup = 0x200;
    private const uint ExtendedStartupInfoPresent = 0x80000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint UseStdHandles = 0x100;
    private const nint HandleListAttribute = 0x20002;

    public static int Start(string commandLine, string? workingDirectory, string log)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        nint nul = -1, output = -1, list = 0, handles = 0, command = 0;
        try
        {
            nul = Check(CreateFileW("NUL", GenericRead, ShareAll, ref security, OpenExisting, 0, 0), "NUL");
            output = Check(CreateFileW(log, FileAppendData, ShareAll, ref security, OpenAlways, FileAttributeNormal, 0), log);

            nint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            handles = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, nul);
            Marshal.WriteIntPtr(handles, IntPtr.Size, output);
            if (!UpdateProcThreadAttribute(list, 0, HandleListAttribute, handles, 2 * IntPtr.Size, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = UseStdHandles,
                    StdInput = nul,
                    StdOutput = output,
                    StdError = output,
                },
                AttributeList = list,
            };
            command = Marshal.StringToHGlobalUni(commandLine);
            const uint flags = DetachedProcess | CreateNewProcessGroup | CreateNoWindow | ExtendedStartupInfoPresent;
            if (!CreateProcessW(null, command, 0, 0, true, flags, 0, workingDirectory, ref startup, out var process))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"could not start {commandLine}");
            }

            _ = CloseHandle(process.Thread);
            _ = CloseHandle(process.Process);
            return process.ProcessId;
        }
        finally
        {
            if (list != 0)
            {
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
            }

            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(command);
            _ = nul != -1 && CloseHandle(nul);
            _ = output != -1 && CloseHandle(output);
        }
    }

    private static nint Check(nint handle, string name) =>
        handle != -1 ? handle : throw new Win32Exception(Marshal.GetLastPInvokeError(), $"could not open {name}");

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? application,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoEx startup,
        out ProcessInformation process);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
