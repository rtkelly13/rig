using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Rig.Core.Estate;

/// <summary>A file's identity on its volume, shared by every hardlink to it, and how many links it has.</summary>
/// <param name="Device">The volume (<c>st_dev</c>; the volume serial number on Windows).</param>
/// <param name="Inode">The file on that volume (<c>st_ino</c>; the 64-bit file index on Windows).</param>
/// <param name="Links">How many names the file has (<c>st_nlink</c>).</param>
public readonly record struct FileIdentity(ulong Device, ulong Inode, ulong Links)
{
    /// <summary>The copy-on-write clone group the file's blocks belong to; null where clones are not read.</summary>
    public FileClone? Cloned { get; init; }

    /// <summary>Whether the file shares blocks with another clone.</summary>
    public bool IsCloned => Cloned is { References: > 1 };

    /// <summary>
    /// What deleting this one name frees out of <paramref name="allocated"/>: nothing while another hardlink remains,
    /// only the clone's private blocks while another clone remains, and all of it otherwise.
    /// </summary>
    public long Alone(long allocated) => Links > 1 ? 0 : Cloned is { References: > 1 } clone ? Math.Min(clone.Private, allocated) : allocated;
}

/// <summary>A file's place in a copy-on-write clone group (APFS <c>clonefile</c>).</summary>
/// <param name="Id">The group, shared by every clone in it: the inode of the file first cloned.</param>
/// <param name="References">How many files share the group's blocks; one is a file with no clone.</param>
/// <param name="Private">The bytes this file alone holds: what deleting it frees while another clone remains.</param>
public readonly record struct FileClone(ulong Id, ulong References, long Private);

/// <summary>
/// Link counts, clone groups and file identity, which .NET has no managed API for: <c>lstat</c> on Linux, one
/// <c>getattrlist</c> on macOS (identity, links and the APFS clone group together), and the file handle's information on
/// Windows. A link is never followed on Unix; on Windows the walks that ask skip links before asking.
/// </summary>
/// <remarks>
/// Linux's <c>struct stat</c> holds <c>st_dev</c> as a u64 at 0 and <c>st_ino</c> as a u64 at 8, but <c>st_nlink</c>
/// moves: a u64 at 16 on x64, a u32 at 20 on arm64 (the generic layout).
/// <para>
/// .NET's <c>File.Copy</c> clones on APFS, so a <c>bin</c> folder is mostly clones of the NuGet cache and of its sibling
/// projects' output: one <c>st_nlink</c>, yet deleting it frees only each file's private blocks. Clones are read only on
/// macOS. Linux reflinks (btrfs, XFS) and ReFS block clones on Windows are not detected, so there a cloned file counts
/// as wholly its own and the freeable figure overstates.
/// </para>
/// </remarks>
public static partial class HardLinks
{
    private const int InodeOffset = 8;

    // Larger than Linux's `struct stat` (144 bytes at most).
    private const int StatSize = 256;

    /// <summary>The identity of <paramref name="path"/>, or null when it cannot be read.</summary>
    public static FileIdentity? Identify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return OperatingSystem.IsWindows() ? WindowsIdentity(path) : UnixIdentity(path);
    }

    [UnsupportedOSPlatform("windows")]
    private static unsafe FileIdentity? UnixIdentity(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacIdentity(path);
        }

        var buffer = stackalloc byte[StatSize];
        if (LStat(path, buffer) != 0)
        {
            return null;
        }

        var links = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? *(ulong*)(buffer + 16) : *(uint*)(buffer + 20);
        return new FileIdentity(*(ulong*)buffer, *(ulong*)(buffer + InodeOffset), links);
    }

    // getattrlist(2) answers what lstat would and the clone group in one call: FSOPT_NOFOLLOW | FSOPT_PACK_INVAL_ATTRS
    // | FSOPT_ATTR_CMN_EXTENDED, the last so forkattr names the extended common attributes. The reply packs on 4-byte
    // boundaries: a u32 length, the five returned bitmaps, then each group's values in bit order: ATTR_CMN_DEVID
    // (i32), ATTR_CMN_FILEID (u64), ATTR_FILE_LINKCOUNT (u32), ATTR_CMNEXT_PRIVATESIZE (off_t), ATTR_CMNEXT_CLONEID
    // (u64), ATTR_CMNEXT_CLONE_REFCNT (u32). Invalid values keep their slots, so the offsets are fixed.
    private const uint GetAttrOptions = 0x1 | 0x8 | 0x20;
    private const uint ReturnedAttrs = 0x80000000;
    private const uint DevId = 0x2;
    private const uint FileId = 0x02000000;
    private const uint LinkCount = 0x1;
    private const uint PrivateSize = 0x8;
    private const uint CloneId = 0x100;
    private const uint CloneRefCount = 0x1000;
    private const int ReplySize = 64;

    [SupportedOSPlatform("macos")]
    [ExcludeFromCodeCoverage(Justification = "macOS-only; CI has no macOS runner. The grouping it feeds is tested through WalkTree's seam.")]
    private static unsafe FileIdentity? MacIdentity(string path)
    {
        var request = new AttrList
        {
            BitmapCount = 5,
            CommonAttr = ReturnedAttrs | DevId | FileId,
            FileAttr = LinkCount,
            ForkAttr = PrivateSize | CloneId | CloneRefCount,
        };
        var reply = stackalloc byte[ReplySize];
        if (GetAttrList(path, &request, reply, ReplySize, GetAttrOptions) != 0)
        {
            return null;
        }

        var common = *(uint*)(reply + 4);
        if ((common & (DevId | FileId)) != (DevId | FileId))
        {
            return null;
        }

        // A folder has no ATTR_FILE_LINKCOUNT; volumes without clone mapping (HFS+, network shares) leave out the
        // fork bits.
        var links = (*(uint*)(reply + 16) & LinkCount) != 0 ? *(uint*)(reply + 36) : 1u;
        var fork = *(uint*)(reply + 20);
        const uint clone = PrivateSize | CloneId | CloneRefCount;
        return new FileIdentity((uint)*(int*)(reply + 24), Unsafe.ReadUnaligned<ulong>(reply + 28), links)
        {
            Cloned = (fork & clone) == clone
                ? new FileClone(Unsafe.ReadUnaligned<ulong>(reply + 48), *(uint*)(reply + 56), Unsafe.ReadUnaligned<long>(reply + 40))
                : null,
        };
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

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "getattrlist", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int GetAttrList(string path, AttrList* request, byte* buffer, nuint size, uint options);

    // struct attrlist: a u16 count, a u16 reserved, then the five attribute groups.
    [StructLayout(LayoutKind.Sequential)]
    private struct AttrList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint CommonAttr;
        public uint VolAttr;
        public uint DirAttr;
        public uint FileAttr;
        public uint ForkAttr;
    }

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
