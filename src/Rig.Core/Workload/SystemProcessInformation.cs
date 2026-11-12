using System.Buffers.Binary;
using System.Text;

namespace Rig.Core.Workload;

/// <summary>
/// Decodes the buffer <c>NtQuerySystemInformation(SystemProcessInformation)</c> fills: one variable-length
/// <c>SYSTEM_PROCESS_INFORMATION</c> per process, chained by <c>NextEntryOffset</c>, with each image name's characters
/// stored later in the same buffer. One kernel call returns every process's ids, memory counters and start time, which is
/// what makes a sample cheap. The offsets are the 64-bit layout; rig runs on x64 and arm64 Windows only. Pure, so it is
/// tested with built buffers on any OS.
/// </summary>
public static class SystemProcessInformation
{
    /// <summary>The information class that returns the process list.</summary>
    public const int ProcessListClass = 5;

    /// <summary>The <c>NTSTATUS</c> for a buffer that was too small.</summary>
    public const int InfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>Byte offsets into one 64-bit <c>SYSTEM_PROCESS_INFORMATION</c>: <c>NextEntryOffset</c> (ULONG); 0 on the last entry.</summary>
    private const int NextOffset = 0;

    /// <summary><c>CreateTime</c> (FILETIME as LARGE_INTEGER).</summary>
    private const int CreateTimeOffset = 32;

    /// <summary><c>ImageName.Length</c> (USHORT, in bytes).</summary>
    private const int NameLengthOffset = 56;

    /// <summary><c>ImageName.Buffer</c> (pointer).</summary>
    private const int NameBufferOffset = 64;

    /// <summary><c>UniqueProcessId</c> (HANDLE).</summary>
    private const int PidOffset = 80;

    /// <summary><c>InheritedFromUniqueProcessId</c> (HANDLE).</summary>
    private const int ParentPidOffset = 88;

    /// <summary><c>WorkingSetSize</c> (SIZE_T).</summary>
    private const int WorkingSetOffset = 144;

    /// <summary><c>PagefileUsage</c> (SIZE_T): private committed bytes, as Task Manager's commit size and .NET's <c>PrivateMemorySize64</c>.</summary>
    private const int PrivateOffset = 184;

    /// <summary>The fixed part of an entry, before its thread array.</summary>
    private const int EntrySize = 256;

    /// <summary>
    /// The processes in <paramref name="buffer"/>, which the kernel filled at address <paramref name="baseAddress"/> (image
    /// names are pointers into it). Paths are left empty; the native reader fills them.
    /// </summary>
    public static IReadOnlyList<ProcessRow> Parse(ReadOnlySpan<byte> buffer, long baseAddress)
    {
        var rows = new List<ProcessRow>();
        var offset = 0;
        while (offset >= 0 && offset + EntrySize <= buffer.Length)
        {
            var entry = buffer[offset..];
            var pid = (int)BinaryPrimitives.ReadInt64LittleEndian(entry[PidOffset..]);
            var created = BinaryPrimitives.ReadInt64LittleEndian(entry[CreateTimeOffset..]);
            rows.Add(new ProcessRow(
                pid,
                (int)BinaryPrimitives.ReadInt64LittleEndian(entry[ParentPidOffset..]),
                Name(buffer, baseAddress, entry, pid),
                string.Empty,
                BinaryPrimitives.ReadInt64LittleEndian(entry[PrivateOffset..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[WorkingSetOffset..]),
                created > 0 ? DateTime.FromFileTimeUtc(created) : null));
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry[NextOffset..]);
            if (next == 0)
            {
                break;
            }

            offset += (int)next;
        }

        return rows;
    }

    /// <summary>
    /// The text of a <c>UNICODE_STRING</c> at the start of <paramref name="buffer"/> whose characters lie in the same buffer,
    /// as <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c> returns; empty when it points elsewhere.
    /// </summary>
    public static string UnicodeString(ReadOnlySpan<byte> buffer, long baseAddress) =>
        buffer.Length < 16 ? string.Empty : Text(buffer, baseAddress, BinaryPrimitives.ReadUInt16LittleEndian(buffer), BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]));

    private static string Name(ReadOnlySpan<byte> buffer, long baseAddress, ReadOnlySpan<byte> entry, int pid)
    {
        var name = Text(buffer, baseAddress, BinaryPrimitives.ReadUInt16LittleEndian(entry[NameLengthOffset..]), BinaryPrimitives.ReadInt64LittleEndian(entry[NameBufferOffset..]));
        return name.Length == 0 && pid == 0 ? "Idle" : name;
    }

    private static string Text(ReadOnlySpan<byte> buffer, long baseAddress, int length, long pointer)
    {
        var at = pointer - baseAddress;
        return pointer == 0 || length == 0 || at < 0 || at + length > buffer.Length
            ? string.Empty
            : Encoding.Unicode.GetString(buffer.Slice((int)at, length));
    }
}
