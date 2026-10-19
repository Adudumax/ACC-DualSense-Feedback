using System.Text.Json;
using ACCDualSenseFeedback.Lighting;

namespace ACCDualSenseFeedback.Runtime;

internal static class PortableLightSettingsStore
{
    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "ACCDualSenseFeedback.lights.json");
    public static RpmLightSettings Load(out bool recovered) => LoadFrom(SettingsPath, out recovered);

    internal static RpmLightSettings LoadFrom(string path, out bool recovered)
    {
        recovered = false;
        if (!File.Exists(path)) return RpmLightSettings.Default;
        try
        {
            string json = File.ReadAllText(path);
            var settings = (JsonSerializer.Deserialize<RpmLightSettings>(json)
                ?? throw new JsonException("Empty lighting settings.")).Sanitize();
            using var document = JsonDocument.Parse(json);
            // A legacy zero-brightness choice was also an Off state. Preserve
            // that intent when replacing the old slider with one master switch.
            if (!document.RootElement.TryGetProperty("Mode", out _) &&
                document.RootElement.TryGetProperty("Brightness", out var brightness) &&
                brightness.ValueKind == JsonValueKind.Number &&
                brightness.TryGetInt32(out int value) && value <= 0)
                settings = settings with { Enabled = false };
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            recovered = true;
            return RpmLightSettings.Default;
        }
    }

    public static bool TrySave(RpmLightSettings settings) => TrySaveTo(SettingsPath, settings);
    internal static bool TrySaveTo(string path, RpmLightSettings settings)
    {
        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings.Sanitize(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
