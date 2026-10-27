using System.Diagnostics;
using System.Globalization;
using System.IO;
using ACCDualSenseFeedback.Diagnostics;
using ACCDualSenseFeedback.Gyro;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Telemetry;

internal static class GyroCaptureChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("Diagnostic CSV drains queued samples and preserves native channels and gyro input source", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"acc-gyro-capture-{Guid.NewGuid():N}.csv");
            try
            {
                long start = Stopwatch.GetTimestamp();
                using (var capture = new TelemetryCsvCapture(path))
                {
                    for (int index = 0; index < 32; index++)
                    {
                        var sample = new TelemetrySnapshot
                        {
                            PacketId = index + 1, ObservedTimestamp = start + (index + 1) * Stopwatch.Frequency / 1000,
                            SpeedKmh = 100, Rpm = 6000, IsEngineRunning = 1, TcIntervention = .37f
                        };
                        var gyro = new GyroRuntimeStatus
                        {
                            State = GyroRuntimeState.Active, InputSource = GyroInputSource.Gyro,
                            DeviceIdentity = @"private-HID-device-path", AngleDegrees = 45,
                            PhysicalX = .1f, GyroX = .5f, OutputX = .5f,
                            DeviceDeltaSeconds = .002, InputValid = true, ImuValid = true,
                            FactoryCalibrated = true, StationaryCalibrated = true, NeedsRecenter = false,
                            Ready = true, RecenterSequence = 7
                        };
                        capture.TryWrite(sample, 123, 45, FeedbackFrame.Off, gyro);
                    }
                    if (capture.DroppedRows != 0) throw new InvalidOperationException("small capture unexpectedly overflowed");
                }
                string[] rows = File.ReadAllLines(path);
                if (rows.Length != 33) throw new InvalidOperationException("shutdown lost pending CSV samples");
                string[] header = rows[0].Split(',');
                int At(string name) => Array.IndexOf(header, name) is int found && found >= 0 ? found
                    : throw new InvalidOperationException($"missing column {name}");
                for (int index = 1; index < rows.Length; index++)
                {
                    string[] row = rows[index].Split(',');
                    if (row.Length != header.Length) throw new InvalidOperationException("CSV header/data width mismatch");
                    if (row[At("packet")] != index.ToString(CultureInfo.InvariantCulture))
                        throw new InvalidOperationException("capture reordered physics packets");
                    if (row[At("native_large")] != "123" || row[At("native_small")] != "45")
                        throw new InvalidOperationException("native rumble no longer preserved");
                    if (At("tc_physics_intervention") != header.Length - 1 ||
                        float.Parse(row[At("tc_physics_intervention")], CultureInfo.InvariantCulture) != .37f)
                        throw new InvalidOperationException("dedicated TC input missing or old CSV columns shifted");
                    if (row[At("gyro_state")] != "Active" || row[At("gyro_source")] != "Gyro")
                        throw new InvalidOperationException("gyro input source is ambiguous");
                    if (float.Parse(row[At("gyro_output_x")], CultureInfo.InvariantCulture) != .5f)
                        throw new InvalidOperationException("final X is not recorded");
                    if (row[At("gyro_device_token")].Length != 12 || row.Contains("private-HID-device-path"))
                        throw new InvalidOperationException("capture leaked private device identity");
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
    }
}
