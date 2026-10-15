using System.IO.Compression;

namespace Cove.Server.Utils
{
    public class GzipHelper
    {
        // Upper bound for a single decompressed packet. Legitimate game packets (including large chalk
        // updates) are far below this; anything above it is treated as a decompression bomb.
        public const int MaxDecompressedSize = 8 * 1024 * 1024;

        // Function to decompress a GZIP-encoded byte array
        public static byte[] DecompressGzip(byte[] gzipBytes)
        {
            using (var compressedStream = new MemoryStream(gzipBytes))
            using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
            using (var resultStream = new MemoryStream())
            {
                byte[] buffer = new byte[16 * 1024];
                int read;
                while ((read = gzipStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (resultStream.Length + read > MaxDecompressedSize)
                        throw new InvalidDataException($"Decompressed packet exceeds {MaxDecompressedSize} bytes.");

                    resultStream.Write(buffer, 0, read);
                }
                return resultStream.ToArray();
            }
        }

        // Function to compress a byte array into a GZIP-encoded byte array
        public static byte[] CompressGzip(byte[] data)
        {
            using (var outputStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(outputStream, CompressionMode.Compress))
                {
                    gzipStream.Write(data, 0, data.Length);
                }
                return outputStream.ToArray();
            }
        }
    }
}
