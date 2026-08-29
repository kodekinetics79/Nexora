using System.Buffers.Binary;
using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Tests;

public sealed class OcrPixelSafetyPolicyTests
{
    [Fact]
    public void Dimensions_over_pixel_budget_are_typed_permanent_refusals()
    {
        var error = Assert.Throws<OcrPixelLimitException>(() =>
            OcrPixelSafetyPolicy.ValidateDimensions(6_000, 6_000, "test image"));

        Assert.IsAssignableFrom<DocumentParsingException>(error);
        Assert.Contains(OcrPixelLimitException.Code, error.Message);
    }

    [Fact]
    public void Huge_png_dimensions_are_rejected_before_decode()
    {
        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 2);

        var error = Assert.Throws<OcrPixelLimitException>(() =>
            OcrPixelSafetyPolicy.ReadAndValidateImageDimensions(png, "png"));
        Assert.Contains("cannot be represented safely", error.Message);
    }

    [Fact]
    public void Bmp_int_minimum_height_does_not_overflow_abs()
    {
        var bmp = new byte[26];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), int.MinValue);

        Assert.Throws<OcrPixelLimitException>(() =>
            OcrPixelSafetyPolicy.ReadAndValidateImageDimensions(bmp, "bmp"));
    }

    [Fact]
    public void Gif_and_webp_dimensions_remain_supported_and_bounded()
    {
        var gif = new byte[10];
        "GIF89a"u8.CopyTo(gif);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 320);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8), 240);
        Assert.Equal((320, 240), OcrPixelSafetyPolicy.ReadAndValidateImageDimensions(gif, "gif"));

        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        "VP8X"u8.CopyTo(webp.AsSpan(12));
        webp[24] = 0xff; webp[25] = 0xff; // 65,536 wide: refused before native decode
        Assert.Throws<OcrPixelLimitException>(() =>
            OcrPixelSafetyPolicy.ReadAndValidateImageDimensions(webp, "webp"));
    }

    [Fact]
    public void Tiff_aggregate_pixel_expansion_is_bounded_before_native_decode()
    {
        var tiff = TwoFrameLittleEndianTiff(5_000, 3_000, 5_000, 3_000);

        var error = Assert.Throws<OcrPixelLimitException>(() =>
            OcrPixelSafetyPolicy.ValidateTiff(tiff));
        Assert.Contains("frames expand", error.Message);
    }

    private static byte[] TwoFrameLittleEndianTiff(uint width1, uint height1, uint width2, uint height2)
    {
        const int first = 8;
        const int directorySize = 2 + 2 * 12 + 4;
        var second = first + directorySize;
        var bytes = new byte[second + directorySize];
        bytes[0] = (byte)'I'; bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), first);
        WriteDirectory(bytes, first, width1, height1, (uint)second);
        WriteDirectory(bytes, second, width2, height2, 0);
        return bytes;
    }

    private static void WriteDirectory(byte[] bytes, int offset, uint width, uint height, uint next)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), 2);
        WriteLongEntry(bytes, offset + 2, 256, width);
        WriteLongEntry(bytes, offset + 14, 257, height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 26), next);
    }

    private static void WriteLongEntry(byte[] bytes, int offset, ushort tag, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), value);
    }
}
