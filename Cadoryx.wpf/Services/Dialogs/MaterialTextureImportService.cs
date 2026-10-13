using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels;
using Microsoft.Win32;

namespace Cadoryx.wpf.Services.Dialogs;

public sealed class MaterialTextureImportService : IMaterialTextureImportService
{
    public async Task<ImportedMaterialTexture?> ImportAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new OpenFileDialog
        { Title = Strings.MaterialImportTexture, Filter = Strings.MaterialTextureFilter, CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return null;
        var path = dialog.FileName;
        return await Task.Run(() => Read(path, token), token);
    }
    public static ImportedMaterialTexture Read(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 67_108_864) throw new CadValidationException(Strings.MaterialTextureTooLarge);
        // Restrict codecs to the two formats offered by the picker; delayed decoding permits a dimension check before copying pixels.
        Span<byte> header = stackalloc byte[8]; int count = stream.Read(header); stream.Position = 0;
        BitmapDecoder decoder = count == 8 && header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ?
            new PngBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None) :
            count >= 2 && header[0] == 255 && header[1] == 216 ?
            new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None) :
            throw new CadValidationException(Strings.MaterialTextureUnsupported);
        if (decoder.Frames.Count != 1) throw new CadValidationException(Strings.MaterialTextureUnsupported);
        var source = decoder.Frames[0]; CadTexturePayload.ValidateDimensions(source.PixelWidth, source.PixelHeight);
        token.ThrowIfCancellationRequested();
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(source.PixelWidth * 4); var pixels = new byte[checked(stride * source.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0); token.ThrowIfCancellationRequested();
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return new(source.PixelWidth, source.PixelHeight, pixels);
    }
}
