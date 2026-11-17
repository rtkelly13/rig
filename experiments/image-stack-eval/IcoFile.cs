namespace ImageStackEval;

/// <summary>A minimal PNG-in-ICO container: what a port needs because neither ImageSharp nor SkiaSharp writes ICO.</summary>
public static class IcoFile
{
    public sealed record Entry(int Size, byte[] Png);

    public static byte[] Write(IReadOnlyList<(int Size, byte[] Png)> entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)entries.Count);
        int offset = 6 + (16 * entries.Count);
        foreach (var (size, png) in entries)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in entries)
        {
            w.Write(png);
        }

        return ms.ToArray();
    }

    public static List<Entry> Read(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        r.ReadUInt16();
        r.ReadUInt16();
        int count = r.ReadUInt16();
        var dir = new List<(int Size, int Length, int Offset)>();
        for (int i = 0; i < count; i++)
        {
            int w = r.ReadByte();
            r.ReadByte();
            r.ReadBytes(6);
            int length = r.ReadInt32(), offset = r.ReadInt32();
            dir.Add((w == 0 ? 256 : w, length, offset));
        }

        return dir.Select(d => new Entry(d.Size, bytes.AsSpan(d.Offset, d.Length).ToArray())).ToList();
    }
}
