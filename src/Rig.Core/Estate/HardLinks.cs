using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Rig.Core.Estate;

/// <summary>A file's identity on its volume, shared by every hardlink to it, and how many links it has.</summary>
/// <param name="Device">The volume (<c>st_dev</c>; the volume serial number on Windows).</param>
/// <param name="Inode">The file on that volume (<c>st_ino</c>; the 64-bit file index on Windows).</param>
/// <param name="Links">How many names the file has (<c>st_nlink</c>).</param>
public readonly record struct FileIdentity(ulong Device, ulong Inode, ulong Links);

/// <summary>
/// Link counts and file identity, which .NET has no managed API for: <c>lstat</c> on Unix, the file handle's information
/// on Windows. A link is never followed on Unix; on Windows the walks that ask skip links before asking.
/// </summary>
/// <remarks>
/// <c>struct stat</c> holds <c>st_ino</c> as a u64 at byte 8 everywhere supported, but <c>st_dev</c> and <c>st_nlink</c>
/// move: macOS (the 64-bit-inode layout) has a 32-bit <c>st_dev</c> at 0 and a 16-bit <c>st_nlink</c> at 6; Linux x64 a
/// 64-bit <c>st_dev</c> at 0 and a 64-bit <c>st_nlink</c> at 16; Linux arm64 (the generic layout) a 64-bit
/// <c>st_dev</c> at 0 and a 32-bit <c>st_nlink</c> at 20.
/// </remarks>
public static partial class HardLinks
{
    private const int InodeOffset = 8;

    // Larger than `struct stat` on every supported platform (144 bytes at most).
    private const int StatSize = 256;

    /// <summary>The identity of <paramref name="path"/>, or null when it cannot be read.</summary>
    public static FileIdentity? Identify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return OperatingSystem.IsWindows() ? WindowsIdentity(path) : UnixIdentity(path);
    }

    /// <summary>How many names <paramref name="path"/> has; one when that cannot be read, so the file counts as its own.</summary>
    public static ulong Count(string path) => Identify(path)?.Links ?? 1;

    [UnsupportedOSPlatform("windows")]
    private static unsafe FileIdentity? UnixIdentity(string path)
    {
        var buffer = stackalloc byte[StatSize];
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? LStatInode64(path, buffer)
            : LStat(path, buffer);
        if (result != 0)
        {
            return null;
        }

        var inode = *(ulong*)(buffer + InodeOffset);
        if (OperatingSystem.IsMacOS())
        {
            return new FileIdentity(*(uint*)buffer, inode, *(ushort*)(buffer + 6));
        }

        var links = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? *(ulong*)(buffer + 16) : *(uint*)(buffer + 20);
        return new FileIdentity(*(ulong*)buffer, inode, links);
    }

    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage(Justification = "Windows-only; exercised by the Windows test job.")]
    private static FileIdentity? WindowsIdentity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info)
                ? new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks)
                : throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LStat(string path, byte* buffer);

    // Intel macOS keeps the 32-bit-inode `lstat` for old binaries; the 64-bit-inode one has this name.
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LStatInode64(string path, byte* buffer);

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
