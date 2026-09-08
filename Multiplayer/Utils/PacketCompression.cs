using System;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class PacketCompression
{
    public static byte[] Compress(byte[] data)
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

    public static byte[] Decompress(byte[] compressedData)
    {
        return Decompress(compressedData, int.MaxValue);
    }

    public static byte[] Decompress(byte[] compressedData, int maxDecompressedBytes)
    {
        if (maxDecompressedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDecompressedBytes));
        using (var inputStream = new MemoryStream(compressedData))
        using (var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress))
        using (var outputStream = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = gzipStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (outputStream.Length + count > maxDecompressedBytes)
                    throw new InvalidDataException("Decompressed packet exceeds the size limit.");
                outputStream.Write(buffer, 0, count);
            }
            return outputStream.ToArray();
        }
    }
}
