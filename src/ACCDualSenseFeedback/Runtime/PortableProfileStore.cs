using System.Text.Json;
using System.Text.Json.Serialization;
using ACCDualSenseFeedback.Haptics;

namespace ACCDualSenseFeedback.Runtime;

internal static class PortableProfileStore
{
    private const string FileName = "ACCDualSenseFeedback.settings.json";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static FeedbackProfile Load(out bool recoveredFromInvalidFile)
        => LoadFrom(SettingsPath, out recoveredFromInvalidFile);

    internal static FeedbackProfile LoadFrom(string path, out bool recoveredFromInvalidFile)
    {
        recoveredFromInvalidFile = false;
        if (!File.Exists(path))
            return FeedbackProfile.Default;

        try
        {
            string json = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(json);
            FeedbackProfile? profile = JsonSerializer.Deserialize<FeedbackProfile>(json, SerializerOptions);
            if (profile is null)
                throw new JsonException("The settings file was empty.");

            JsonElement root = document.RootElement;
            bool hasModularSettings = root.TryGetProperty(nameof(FeedbackProfile.BrakeResistance), out _);
            if (!hasModularSettings)
            {
                int road = ReadOldStrength(root, "RoadFeel", "Vibration");
                int grip = ReadOldStrength(root, "GripCues", "Vibration");
                int native = ReadOldStrength(root, "AccDetail", "Vibration");
                int trigger = ReadOldStrength(root, "TriggerFeel", "Triggers");

                profile = profile with
                {
                    BrakeResistance = trigger,
                    ThrottleResistance = trigger,
                    AbsPulse = trigger,
                    LockPulse = trigger,
                    ShiftKick = trigger,
                    RedlinePulse = trigger,
                    TcPulse = trigger,
                    WheelspinPulse = trigger,
                    RoadSurface = road,
                    GripLoss = grip,
                    NativeAcc = native
                };
            }

            profile = MigrateRemovedModuleSwitches(root, profile);

            return profile.Sanitize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            recoveredFromInvalidFile = true;
            return FeedbackProfile.Default;
        }
    }

    private static int ReadOldStrength(JsonElement root, string currentName, string legacyName)
    {
        if (root.TryGetProperty(currentName, out JsonElement current) &&
            current.ValueKind == JsonValueKind.Number &&
            current.TryGetInt32(out int numeric))
            return Math.Clamp(numeric, 0, 100);

        return root.TryGetProperty(legacyName, out JsonElement legacy)
            ? ReadLegacyLevel(legacy)
            : FeedbackProfile.NeutralValue;
    }

    private static FeedbackProfile MigrateRemovedModuleSwitches(
        JsonElement root,
        FeedbackProfile profile)
    {
        if (WasDisabled(root, "BrakeResistanceEnabled"))
            profile = profile with { BrakeResistance = 0 };
        if (WasDisabled(root, "ThrottleResistanceEnabled"))
            profile = profile with { ThrottleResistance = 0 };
        if (WasDisabled(root, "AbsPulseEnabled"))
            profile = profile with { AbsPulse = 0 };
        if (WasDisabled(root, "LockPulseEnabled"))
            profile = profile with { LockPulse = 0 };
        if (WasDisabled(root, "ShiftKickEnabled"))
            profile = profile with { ShiftKick = 0 };
        if (WasDisabled(root, "RedlinePulseEnabled"))
            profile = profile with { RedlinePulse = 0 };
        if (WasDisabled(root, "TcPulseEnabled"))
            profile = profile with { TcPulse = 0 };
        if (WasDisabled(root, "WheelspinPulseEnabled"))
            profile = profile with { WheelspinPulse = 0 };
        if (WasDisabled(root, "RoadSurfaceEnabled"))
            profile = profile with { RoadSurface = 0 };
        if (WasDisabled(root, "GripLossEnabled"))
            profile = profile with { GripLoss = 0 };
        if (WasDisabled(root, "NativeAccEnabled"))
            profile = profile with { NativeAcc = 0 };

        return profile;
    }

    private static bool WasDisabled(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out JsonElement value) &&
           value.ValueKind == JsonValueKind.False;

    private static int ReadLegacyLevel(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.ToUpperInvariant() switch
            {
                "GENTLE" => 25,
                "STRONG" => 75,
                _ => FeedbackProfile.NeutralValue
            };
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
        {
            return numeric switch
            {
                0 => 25,
                2 => 75,
                _ => FeedbackProfile.NeutralValue
            };
        }

        return FeedbackProfile.NeutralValue;
    }

    public static bool TrySave(FeedbackProfile profile)
        => TrySaveTo(SettingsPath, profile);

    internal static bool TrySaveTo(string path, FeedbackProfile profile)
    {
        string temporaryPath = path + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(profile.Sanitize(), SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return false;
        }
    }
}
