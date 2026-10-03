using System.IO;
using System.IO.Compression;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Reads one number out of a gzipped NBT file, the format Minecraft saves its world data in.
/// </summary>
/// <remarks>
/// <para>
/// Only reading, and only a long at a known path. Everything else in the file is skipped without
/// being kept. That is all the app needs from it, and not reason enough to take on a library.
/// </para>
/// <para>
/// <strong>It never throws.</strong> A world file is somebody's save: it can be truncated by a
/// crash, still being written by the server, or from a version that lays it out differently. Any
/// of those answers "not found", never an exception in the middle of opening a dialog. Lengths and
/// nesting are bounded for the same reason, so a damaged file cannot ask for gigabytes.
/// </para>
/// </remarks>
public static class NbtReader
{
    private const int MaxDepth = 64;
    private const int MaxArrayLength = 64 * 1024 * 1024;

    /// <summary>
    /// The long at <paramref name="path"/> (compound names from the root, root excluded), or null.
    /// </summary>
    public static long? ReadLong(string file, params string[] path)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadLong(stream, path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The same, from a stream holding the gzipped file.</summary>
    public static long? ReadLong(Stream gzipped, params string[] path)
    {
        try
        {
            using var gz = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(new BufferedStream(gz), Encoding.UTF8, leaveOpen: false);

            var rootType = reader.ReadByte();
            if (rootType != TagCompound) return null;
            SkipString(reader);

            return Find(reader, path, 0);
        }
        catch
        {
            return null;
        }
    }

    private const byte TagEnd = 0, TagByte = 1, TagShort = 2, TagInt = 3, TagLong = 4, TagFloat = 5,
        TagDouble = 6, TagByteArray = 7, TagString = 8, TagList = 9, TagCompound = 10,
        TagIntArray = 11, TagLongArray = 12;

    /// <summary>Walks the compound the reader is positioned in, looking for path[index].</summary>
    private static long? Find(BinaryReader reader, string[] path, int index)
    {
        if (index >= path.Length) return null;

        while (true)
        {
            var type = reader.ReadByte();
            if (type == TagEnd) return null;

            var name = ReadString(reader);
            var wanted = name == path[index];

            if (wanted && index == path.Length - 1)
                return type == TagLong ? ReadBigEndianLong(reader) : null;

            if (wanted && type == TagCompound)
                return Find(reader, path, index + 1);

            Skip(reader, type, 1);
        }
    }

    private static void Skip(BinaryReader reader, byte type, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("NBT nested too deep");

        switch (type)
        {
            case TagByte: Discard(reader, 1); break;
            case TagShort: Discard(reader, 2); break;
            case TagInt: case TagFloat: Discard(reader, 4); break;
            case TagLong: case TagDouble: Discard(reader, 8); break;
            case TagByteArray: Discard(reader, Length(reader)); break;
            case TagIntArray: Discard(reader, Length(reader) * 4L); break;
            case TagLongArray: Discard(reader, Length(reader) * 8L); break;
            case TagString: SkipString(reader); break;
            case TagList:
                {
                    var element = reader.ReadByte();
                    var count = Length(reader);
                    for (var i = 0; i < count; i++) Skip(reader, element, depth + 1);
                    break;
                }
            case TagCompound:
                while (true)
                {
                    var inner = reader.ReadByte();
                    if (inner == TagEnd) break;
                    SkipString(reader);
                    Skip(reader, inner, depth + 1);
                }
                break;
            default:
                throw new InvalidDataException($"Unknown NBT tag {type}");
        }
    }

    private static int Length(BinaryReader reader)
    {
        var n = ReadBigEndianInt(reader);
        if (n < 0 || n > MaxArrayLength) throw new InvalidDataException("NBT length out of range");
        return n;
    }

    private static void Discard(BinaryReader reader, long count)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (count > 0)
        {
            var chunk = (int)Math.Min(count, buffer.Length);
            var read = reader.Read(buffer[..chunk]);
            if (read <= 0) throw new EndOfStreamException();
            count -= read;
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = (reader.ReadByte() << 8) | reader.ReadByte();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        // Java's "modified UTF-8" differs only for NUL and characters outside the BMP, neither of
        // which appears in the key names this looks for.
        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipString(BinaryReader reader) =>
        Discard(reader, (reader.ReadByte() << 8) | reader.ReadByte());

    private static int ReadBigEndianInt(BinaryReader reader)
    {
        var b = reader.ReadBytes(4);
        if (b.Length != 4) throw new EndOfStreamException();
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b);
    }

    private static long ReadBigEndianLong(BinaryReader reader)
    {
        var b = reader.ReadBytes(8);
        if (b.Length != 8) throw new EndOfStreamException();
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(b);
    }
}
