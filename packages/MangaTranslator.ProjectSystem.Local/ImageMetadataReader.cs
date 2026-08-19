using System.Buffers.Binary;

namespace MangaTranslator.ProjectSystem.Local;

internal static class ImageMetadataReader
{
    public static (int Width, int Height) Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".png" => ReadPng(path),
            ".jpg" or ".jpeg" => ReadJpeg(path),
            ".gif" => ReadGif(path),
            ".bmp" => ReadBitmap(path),
            ".webp" => ReadWebP(path),
            _ => (0, 0)
        };
    }

    private static (int Width, int Height) ReadPng(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length || !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return (0, 0);
        }

        return (BinaryPrimitives.ReadInt32BigEndian(header[16..20]), BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    private static (int Width, int Height) ReadGif(string path)
    {
        Span<byte> header = stackalloc byte[10];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length || (!header[..6].SequenceEqual("GIF87a"u8) && !header[..6].SequenceEqual("GIF89a"u8)))
        {
            return (0, 0);
        }

        return (BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]));
    }

    private static (int Width, int Height) ReadBitmap(string path)
    {
        Span<byte> header = stackalloc byte[26];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length || header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            return (0, 0);
        }

        return (Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(header[18..22])), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(header[22..26])));
    }

    private static (int Width, int Height) ReadJpeg(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != 0xff || reader.ReadByte() != 0xd8)
        {
            return (0, 0);
        }

        while (stream.Position < stream.Length)
        {
            byte markerStart;
            do
            {
                markerStart = reader.ReadByte();
            } while (markerStart != 0xff && stream.Position < stream.Length);

            byte marker;
            do
            {
                marker = reader.ReadByte();
            } while (marker == 0xff && stream.Position < stream.Length);
            if (marker is 0xd8 or 0xd9)
            {
                continue;
            }

            var segmentLength = ReadUInt16BigEndian(reader);
            if (segmentLength < 2)
            {
                return (0, 0);
            }

            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                _ = reader.ReadByte();
                var height = ReadUInt16BigEndian(reader);
                var width = ReadUInt16BigEndian(reader);
                return (width, height);
            }

            stream.Seek(segmentLength - 2, SeekOrigin.Current);
        }

        return (0, 0);
    }

    private static (int Width, int Height) ReadWebP(string path)
    {
        Span<byte> header = stackalloc byte[30];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < 30 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WEBP"u8) || !header[12..16].SequenceEqual("VP8X"u8))
        {
            return (0, 0);
        }

        var width = 1 + header[24] + (header[25] << 8) + (header[26] << 16);
        var height = 1 + header[27] + (header[28] << 8) + (header[29] << 16);
        return (width, height);
    }

    private static ushort ReadUInt16BigEndian(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (reader.Read(bytes) != bytes.Length)
        {
            return 0;
        }

        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }
}
