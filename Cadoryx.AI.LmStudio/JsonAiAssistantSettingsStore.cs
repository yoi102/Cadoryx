using System.Text.Json;
using Cadoryx.AI.Contracts;

namespace Cadoryx.AI.LmStudio;

public sealed class JsonAiAssistantSettingsStore : IAiAssistantSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public JsonAiAssistantSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cadoryx",
            "ai-assistant-settings.json"))
    {
    }

    public JsonAiAssistantSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public AiAssistantSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
                    File.ReadAllText(_filePath),
                    SerializerOptions);
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }

        return new AiAssistantSettings();
    }

    public void Save(AiAssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(
            _filePath,
            JsonSerializer.Serialize(settings, SerializerOptions));
    }
}
