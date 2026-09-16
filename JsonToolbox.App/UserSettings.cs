using System.Text.Json;

namespace JsonToolbox.App;

/// <summary>
/// Per-user preferences, kept under <c>%APPDATA%\JsonToolbox\settings.json</c>.
/// </summary>
/// <remarks>
/// Read and written by hand rather than through a serializer: the release build is trimmed,
/// and a reflection-based serializer is exactly the thing trimming cannot see through. Two
/// properties do not justify a source generator either. A file that cannot be read is treated
/// as no file, because a preference is never worth failing to start over.
/// </remarks>
public sealed class UserSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JsonToolbox", "settings.json");

    /// <summary>"Dark" or "Light"; null follows the operating system preference.</summary>
    public string? Theme { get; set; }

    public static UserSettings Load()
    {
        var settings = new UserSettings();

        try
        {
            if (!File.Exists(SettingsPath))
            {
                return settings;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("theme", out JsonElement theme)
                && theme.ValueKind == JsonValueKind.String)
            {
                settings.Theme = theme.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Left at the defaults: see the remarks.
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

            using var stream = File.Create(SettingsPath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            if (Theme is not null)
            {
                writer.WriteString("theme", Theme);
            }

            writer.WriteEndObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that could not be written is a preference for this run only.
        }
    }
}
