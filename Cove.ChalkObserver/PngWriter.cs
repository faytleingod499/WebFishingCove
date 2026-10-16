using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

/// <summary>
/// Minimal PNG encoder (8-bit RGB). Written by hand so the plugin stays a single dll with no dependencies,
/// System.Drawing only exists on Windows and would need extra files in the plugins folder.
/// </summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <param name="rgb">width * height * 3 bytes, top row first</param>
    public static byte[] EncodeRgb(byte[] rgb, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The image must have a size.");
        if (rgb.Length != checked(width * height * 3))
            throw new ArgumentException("The pixel buffer does not match the image size.");

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type 2 = RGB
        // compression, filter and interlace methods stay 0
        WriteChunk(png, "IHDR", header, header.Length);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 3;
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter type "none" for this row
                zlib.Write(rgb, y * stride, stride);
            }
        } // disposing flushes the final block, only then is the buffer complete

        byte[] idat = compressed.ToArray();
        WriteChunk(png, "IDAT", idat, idat.Length);
        WriteChunk(png, "IEND", Array.Empty<byte>(), 0);

        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data, int length)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);

        byte[] word = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)length);
        stream.Write(word, 0, 4);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, length);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in typeBytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        for (int i = 0; i < length; i++)
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;

        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        stream.Write(word, 0, 4);
    }
}
