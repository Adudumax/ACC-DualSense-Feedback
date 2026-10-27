using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ACCDualSenseFeedback.Gyro;

internal static class PortableGyroSettingsStore
{
    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "ACCDualSenseFeedback.gyro.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object BiasWriteLock = new();
    private static Task<GyroBiasSaveResult> _biasWriteTail = Task.FromResult(new GyroBiasSaveResult(null));
    public static GyroSteeringSettings Load() => Load(out _);
    public static GyroSteeringSettings Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(SettingsPath)) return GyroSteeringSettings.Default;
            return (JsonSerializer.Deserialize<GyroSteeringSettings>(File.ReadAllText(SettingsPath), Options) ?? GyroSteeringSettings.Default).Sanitize();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { error = e.Message; return GyroSteeringSettings.Default; }
    }
    public static bool TrySave(GyroSteeringSettings value, out string? error)
        => TryWrite(SettingsPath, JsonSerializer.Serialize(value.Sanitize(), Options), out error);

    internal static string AnonymousDeviceIdentity(string deviceIdentity)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceIdentity))).Substring(0, 20);

    internal static Vector3? LoadBias(string deviceIdentity)
    {
        if (string.IsNullOrEmpty(deviceIdentity)) return null;
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, SensorBias>>(File.ReadAllText(CalibrationPath), Options);
            if (entries?.TryGetValue(AnonymousDeviceIdentity(deviceIdentity), out var bias) == true && bias is not null)
            {
                var v = new Vector3(bias.X, bias.Y, bias.Z);
                if (float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && v.Length() < 10) return v;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return null;
    }
    internal static bool TrySaveBias(string deviceIdentity, Vector3 value, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(deviceIdentity)) { error = "无有效设备标识，校准仅在本次连接生效"; return false; }
        try
        {
            Dictionary<string, SensorBias>? entries = null;
            if (File.Exists(CalibrationPath))
            {
                try { entries = JsonSerializer.Deserialize<Dictionary<string, SensorBias>>(File.ReadAllText(CalibrationPath), Options); }
                catch (JsonException) { }
            }
            entries ??= new();
            entries[AnonymousDeviceIdentity(deviceIdentity)] = new(value.X, value.Y, value.Z);
            return TryWrite(CalibrationPath, JsonSerializer.Serialize(entries, Options), out error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        { error = e.Message; return false; }
    }
    /// <summary>Serialized FIFO persistence. Work captures only values, never a processor/native handle or UI object.</summary>
    internal static Task<GyroBiasSaveResult> SaveBiasAsync(string deviceIdentity, Vector3 value)
    {
        lock (BiasWriteLock)
        {
            _biasWriteTail = _biasWriteTail.ContinueWith(_ =>
            {
                try { TrySaveBias(deviceIdentity, value, out var error); return new GyroBiasSaveResult(error); }
                catch (Exception e) { return new GyroBiasSaveResult(e.Message); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _biasWriteTail;
        }
    }
    private static string CalibrationPath => Path.Combine(AppContext.BaseDirectory, "ACCDualSenseFeedback.gyro-calibration.json");
    private sealed record SensorBias(float X, float Y, float Z);
    private static bool TryWrite(string path, string content, out string? error)
    {
        error = null;
        try
        {
            File.WriteAllText(path + ".tmp", content, new UTF8Encoding(false));
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        { error = e.Message; return false; }
    }
}

internal sealed record GyroBiasSaveResult(string? Error);
