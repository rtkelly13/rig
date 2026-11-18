using System.Buffers.Binary;

namespace Rig.Image;

/// <summary>One image of an icon: a square size and its PNG bytes.</summary>
/// <param name="Size">Pixels per side.</param>
/// <param name="Png">The PNG-encoded image.</param>
public sealed record IcoEntry(int Size, ReadOnlyMemory<byte> Png);

/// <summary>A PNG-in-ICO container, the form Pillow writes; neither SkiaSharp nor a single-frame encoder can write a multi-size icon.</summary>
public static class IcoFile
{
    /// <summary>The sizes production_cuts.py writes, largest first, each only if the source is at least that large.</summary>
    public static readonly IReadOnlyList<int> Sizes = [48, 32, 16];

    private const int HeaderLength = 6;
    private const int DirectoryEntryLength = 16;

    /// <summary>Builds the favicon: <paramref name="favicon"/> resampled to each of <see cref="Sizes"/> that fits, PNG-compressed in the container.</summary>
    public static byte[] FromFavicon(Raster favicon)
    {
        ArgumentNullException.ThrowIfNull(favicon);
        return Write([.. Sizes.Where(size => size <= favicon.Width).Select(size => new IcoEntry(size, RasterPipeline.EncodePng(RasterPipeline.ResizeLanczos(favicon, size))))]);
    }

    /// <summary>Writes the container.</summary>
    public static byte[] Write(IReadOnlyList<IcoEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var total = HeaderLength + (DirectoryEntryLength * entries.Count) + entries.Sum(entry => entry.Png.Length);
        var bytes = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)entries.Count));
        var offset = HeaderLength + (DirectoryEntryLength * entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var directory = bytes.AsSpan(HeaderLength + (DirectoryEntryLength * i), DirectoryEntryLength);
            var side = (byte)(entry.Size >= 256 ? 0 : entry.Size);
            directory[0] = side;
            directory[1] = side;
            BinaryPrimitives.WriteUInt16LittleEndian(directory[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[6..], 32);
            BinaryPrimitives.WriteInt32LittleEndian(directory[8..], entry.Png.Length);
            BinaryPrimitives.WriteInt32LittleEndian(directory[12..], offset);
            entry.Png.Span.CopyTo(bytes.AsSpan(offset));
            offset += entry.Png.Length;
        }

        return bytes;
    }

    /// <summary>Reads a container of PNG images; anything else, or a directory that points outside the file, is an <see cref="InvalidDataException"/>.</summary>
    public static IReadOnlyList<IcoEntry> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength || BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != 1)
        {
            throw new InvalidDataException("expected an ICO file (type 1), found something else");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var entries = new List<IcoEntry>();
        for (var i = 0; i < count; i++)
        {
            var directoryStart = HeaderLength + (DirectoryEntryLength * i);
            if (directoryStart + DirectoryEntryLength > bytes.Length)
            {
                throw new InvalidDataException($"expected {count} directory entries, the file ends after {i}");
            }

            var directory = bytes.Slice(directoryStart, DirectoryEntryLength);
            var length = BinaryPrimitives.ReadInt32LittleEndian(directory[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(directory[12..]);
            if (offset < 0 || length < 0 || offset + length > bytes.Length)
            {
                throw new InvalidDataException($"entry {i} points at {offset}+{length}, outside a {bytes.Length}-byte file");
            }

            entries.Add(new IcoEntry(directory[0] == 0 ? 256 : directory[0], bytes.Slice(offset, length).ToArray()));
        }

        return entries;
    }
}
