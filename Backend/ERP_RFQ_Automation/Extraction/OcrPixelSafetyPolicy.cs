using System.Buffers.Binary;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>Rejects hostile image dimensions before native OCR libraries allocate pixel buffers.</summary>
public static class OcrPixelSafetyPolicy
{
    public const string Code = OcrPixelLimitException.Code;
    public const int MaximumDimension = 12_000;
    public const long MaximumPixels = 25_000_000;
    public const int MaximumTiffFrames = 50;

    public static long ValidateDimensions(int width, int height, string source)
    {
        if (width <= 0 || height <= 0)
            throw new OcrPixelLimitException($"{source} has invalid dimensions {width}x{height}.");
        if (width > MaximumDimension || height > MaximumDimension)
            throw new OcrPixelLimitException(
                $"{source} dimensions {width}x{height} exceed the OCR dimension limit {MaximumDimension}.");

        long pixels;
        try { pixels = checked((long)width * height); }
        catch (OverflowException exception)
        {
            throw new OcrPixelLimitException($"{source} dimensions overflow the OCR pixel calculation.", exception);
        }
        if (pixels > MaximumPixels)
            throw new OcrPixelLimitException(
                $"{source} expands to {pixels} pixels, exceeding the OCR limit {MaximumPixels}.");
        return pixels;
    }

    public static (int Width, int Height) ReadAndValidateImageDimensions(
        ReadOnlySpan<byte> bytes, string extension)
    {
        var dimensions = extension.ToLowerInvariant() switch
        {
            "png" => ReadPng(bytes),
            "jpg" or "jpeg" => ReadJpeg(bytes),
            "bmp" => ReadBmp(bytes),
            "gif" => ReadGif(bytes),
            "webp" => ReadWebp(bytes),
            _ => throw new OcrPixelLimitException(
                $"The {extension} image dimensions cannot be verified safely before OCR.")
        };
        ValidateDimensions(dimensions.Width, dimensions.Height, extension.ToUpperInvariant());
        return dimensions;
    }

    public static (int Frames, long TotalPixels) ValidateTiff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            throw new OcrPixelLimitException("TIFF header is missing or truncated.");
        var littleEndian = bytes[0] == (byte)'I' && bytes[1] == (byte)'I';
        if (!littleEndian && !(bytes[0] == (byte)'M' && bytes[1] == (byte)'M'))
            throw new OcrPixelLimitException("TIFF byte order marker is invalid.");
        if (ReadUInt16(bytes, 2, littleEndian) != 42)
            throw new OcrPixelLimitException("BigTIFF or an invalid TIFF header cannot be decoded safely.");
        var ifdOffset = ReadUInt32(bytes, 4, littleEndian);
        var frames = 0;
        long totalPixels = 0;
        while (ifdOffset != 0)
        {
            if (++frames > MaximumTiffFrames)
                throw new OcrPixelLimitException($"TIFF contains more than {MaximumTiffFrames} frames.");
            if (ifdOffset > int.MaxValue || (long)ifdOffset + 2 > bytes.Length)
                throw new OcrPixelLimitException("TIFF directory offset is outside the file.");
            var directory = (int)ifdOffset;
            var entries = ReadUInt16(bytes, directory, littleEndian);
            var entriesEnd = checked((long)directory + 2 + entries * 12L);
            if (entriesEnd + 4 > bytes.Length)
                throw new OcrPixelLimitException("TIFF directory is truncated.");

            int? width = null;
            int? height = null;
            for (var i = 0; i < entries; i++)
            {
                var entry = checked(directory + 2 + i * 12);
                var tag = ReadUInt16(bytes, entry, littleEndian);
                if (tag is not (256 or 257)) continue;
                var type = ReadUInt16(bytes, entry + 2, littleEndian);
                var count = ReadUInt32(bytes, entry + 4, littleEndian);
                if (count != 1 || type is not (3 or 4))
                    throw new OcrPixelLimitException("TIFF dimensions use an unsupported field encoding.");
                var value = type == 3
                    ? ReadUInt16(bytes, entry + 8, littleEndian)
                    : CheckedUInt32(ReadUInt32(bytes, entry + 8, littleEndian), "TIFF dimension");
                if (tag == 256) width = value; else height = value;
            }
            if (width is null || height is null)
                throw new OcrPixelLimitException("TIFF frame dimensions are missing.");
            var framePixels = ValidateDimensions(width.Value, height.Value, $"TIFF frame {frames}");
            totalPixels = checked(totalPixels + framePixels);
            if (totalPixels > MaximumPixels)
                throw new OcrPixelLimitException(
                    $"TIFF frames expand to {totalPixels} pixels, exceeding the OCR limit {MaximumPixels}.");
            ifdOffset = ReadUInt32(bytes, (int)entriesEnd, littleEndian);
        }
        if (frames == 0)
            throw new OcrPixelLimitException("TIFF contains no image directories.");
        return (frames, totalPixels);
    }

    private static (int Width, int Height) ReadPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new OcrPixelLimitException("PNG header is missing or truncated.");
        return (CheckedUInt32(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]), "PNG width"),
            CheckedUInt32(BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]), "PNG height"));
    }

    private static (int Width, int Height) ReadBmp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
            throw new OcrPixelLimitException("BMP header is missing or truncated.");
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..22]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..26]);
        if (height == int.MinValue)
            throw new OcrPixelLimitException("BMP height cannot be represented safely.");
        return (width, Math.Abs(height));
    }

    private static (int Width, int Height) ReadGif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 10
            || !(bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            throw new OcrPixelLimitException("GIF header is missing or truncated.");
        return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]));
    }

    private static (int Width, int Height) ReadWebp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30 || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes[8..12].SequenceEqual("WEBP"u8))
            throw new OcrPixelLimitException("WebP header is missing or truncated.");
        var kind = bytes[12..16];
        if (kind.SequenceEqual("VP8X"u8))
            return (ReadUInt24LittleEndian(bytes[24..27]) + 1,
                ReadUInt24LittleEndian(bytes[27..30]) + 1);
        if (kind.SequenceEqual("VP8L"u8))
        {
            if (bytes[20] != 0x2f)
                throw new OcrPixelLimitException("WebP lossless dimensions are malformed.");
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..25]);
            return ((int)(packed & 0x3fff) + 1, (int)((packed >> 14) & 0x3fff) + 1);
        }
        if (kind.SequenceEqual("VP8 "u8))
        {
            if (bytes.Length < 30 || bytes[23] != 0x9d || bytes[24] != 0x01 || bytes[25] != 0x2a)
                throw new OcrPixelLimitException("WebP lossy dimensions are malformed.");
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..28]) & 0x3fff,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..30]) & 0x3fff);
        }
        throw new OcrPixelLimitException("WebP encoding is not recognized safely.");
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
        => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..])
            : BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..])
            : BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);

    private static (int Width, int Height) ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            throw new OcrPixelLimitException("JPEG header is missing or truncated.");
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] != 0xff) offset++;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || offset + length > bytes.Length) break;
            if (IsStartOfFrame(marker))
            {
                if (length < 7) break;
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
            }
            offset += length;
        }
        throw new OcrPixelLimitException("JPEG dimensions could not be verified before OCR.");
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static int CheckedUInt32(uint value, string field)
    {
        if (value > int.MaxValue)
            throw new OcrPixelLimitException($"{field} cannot be represented safely.");
        return (int)value;
    }
}

public sealed class OcrPixelLimitException : DocumentParsingException
{
    public const string Code = "ocr_pixel_limit_exceeded";
    public OcrPixelLimitException(string message, Exception? innerException = null)
        : base($"[{Code}] {message}", innerException) { }
}
