using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Rig.Core.Otel;

/// <summary>
/// A file's identity that survives a rename: its inode on Unix, its NTFS file id on Windows. This is Python's
/// <c>os.stat().st_ino</c>, so state the Python wrote keeps meaning the same file. .NET has no managed API for it.
/// </summary>
public static partial class FileIds
{
    // `struct stat` holds st_ino as a u64 at byte 8 on macOS (the 64-bit-inode layout) and on Linux x64 and arm64.
    private const int InodeOffset = 8;

    // Larger than `struct stat` on every supported platform (144 bytes at most).
    private const int StatSize = 256;

    /// <summary>The inode of <paramref name="path"/> (following links, as <c>os.stat</c>), or null when it cannot be read.</summary>
    public static ulong? Inode(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return OperatingSystem.IsWindows() ? WindowsFileId(path) : UnixInode(path);
    }

    [UnsupportedOSPlatform("windows")]
    private static unsafe ulong? UnixInode(string path)
    {
        var buffer = stackalloc byte[StatSize];
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? StatInode64(path, buffer)
            : Stat(path, buffer);
        return result == 0 ? *(ulong*)(buffer + InodeOffset) : null;
    }

    // Windows has no inode; Python's st_ino there is the 64-bit file index, high word first.
    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage(Justification = "Windows-only; exercised by the Windows test job.")]
    private static ulong? WindowsFileId(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info)
                ? ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow
                : throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int Stat(string path, byte* buffer);

    // Intel macOS keeps the 32-bit-inode `stat` for old binaries; the 64-bit-inode one has this name.
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatInode64(string path, byte* buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    // FILETIME is two DWORDs, so the times sit on 4-byte boundaries.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
