using System.Buffers.Binary;

namespace Cadoryx.Db;

/// <summary>Bounded top-down, tightly packed, unpremultiplied sRGB RGBA8 bytes.</summary>
public static class CadTexturePayload
{
    public const string MediaType = "application/vnd.cadoryx.texture-rgba8";
    public const int HeaderBytes = 24;
    public const int MaximumPixels = 16_777_216;
    public const int MaximumBytes = HeaderBytes + MaximumPixels * 4;
    public static AssetFormat CurrentFormat { get; } = new(MediaType, "rgba8-envelope", 1, Writer: "Cadoryx", WriterVersion: "0.7.10");
    public static void ValidateDimensions(int width, int height)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height > MaximumPixels)
            throw new CadValidationException("Texture dimensions exceed the supported pixel budget.");
    }
    public static void RequireFormat(AssetFormat format)
    {
        format.Validate();
        if (format.MediaType != MediaType || format.Encoding != "rgba8-envelope" || format.FormatVersion != 1 || format.Kernel is not null)
            throw new CadValidationException("Unsupported texture media type, encoding or version.");
    }
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ValidateDimensions(width, height);
        int length = checked(width * height * 4);
        if (rgba.Length != length) throw new CadValidationException("Texture pixels do not match their dimensions.");
        var result = new byte[HeaderBytes + length]; "CDXRGBA1"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), height);
        // Bytes 20..23 remain zero: reserved; readers reject rather than reinterpret unknown flags.
        rgba.CopyTo(result.AsSpan(HeaderBytes)); return result;
    }
    public static (int Width, int Height) Verify(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || bytes.Length > MaximumBytes || !bytes.StartsWith("CDXRGBA1"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != 1 || BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]) != 0)
            throw new CadValidationException("Malformed texture envelope.");
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]); int height = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        ValidateDimensions(width, height);
        if (bytes.Length != HeaderBytes + checked(width * height * 4)) throw new CadValidationException("Texture payload length mismatch.");
        return (width, height);
    }
    public static void Verify(CadTextureRef reference, ReadOnlySpan<byte> bytes)
    {
        reference.Validate(); var size = Verify(bytes);
        if (size.Width != reference.Width || size.Height != reference.Height)
            throw new CadValidationException("Texture descriptor differs from its owned pixels.");
    }
}
