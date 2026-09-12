using System.Text.Json;
using System.IO;
using Cadoryx.ViewModels.Services.Platform.Settings;

namespace Cadoryx.wpf.Services.Application;

internal sealed class JsonApplicationSettingsStore : IApplicationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cadoryx",
        "application-settings.json");

    public CadoryxApplicationSettings Load()
    {
        if (!File.Exists(_filePath))
            return CreateDefault();

        try
        {
            var settings = JsonSerializer.Deserialize<CadoryxApplicationSettings>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            if (settings is null)
                return CreateDefault();

            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return CreateDefault();
        }
    }

    public void Save(CadoryxApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryFilePath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryFilePath, json);
            File.Move(temporaryFilePath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFilePath))
                File.Delete(temporaryFilePath);
        }
    }

    private static CadoryxApplicationSettings CreateDefault()
    {
        var settings = CadoryxApplicationSettings.CreateDefault();
        settings.Normalize();
        return settings;
    }
}
