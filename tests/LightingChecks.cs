using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Lighting;
using ACCDualSenseFeedback.Runtime;
using ACCDualSenseFeedback.Telemetry;

internal static class LightingChecks
{
    private static long At(double seconds) => (long)((seconds + 1) * Stopwatch.Frequency);
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed record ReferenceCase(string Name, double Normalized, RpmLightMode Mode,
        double Flashing, double Clock, int Gear, CspLightContext Context, double[] Expected);

    public static void Run(Action<string, Action> check)
    {
        var settings = RpmLightSettings.Default;
        check("C# output matches all 862 cases executed by the real CSP Lua functions", () =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Reference", "Csp", "fixtures.json");
            var cases = JsonSerializer.Deserialize<ReferenceCase[]>(File.ReadAllText(path))!;
            Assert(cases.Length == 862, "incomplete Lua reference fixture");
            foreach (var row in cases)
            {
                var actual = CspLightMath.Compute(row.Normalized, row.Gear, row.Mode,
                    row.Flashing, row.Clock, row.Context);
                byte[] channels = [actual.Red, actual.Green, actual.Blue];
                for (int i = 0; i < 3; i++)
                    Assert(Math.Abs(channels[i] / 255d - row.Expected[i]) <= 0.500001 / 255,
                        $"{row.Name}: channel {i}, {channels[i]}/255 != {row.Expected[i]}");
                Assert(actual.PlayerMask == row.Expected[3], $"{row.Name}: player LED mask");
                Assert(actual.PlayerBrightness == row.Expected[4], $"{row.Name}: player LED brightness");
            }
        });
        check("Smooth mode follows CSP HSV hue and low-RPM fade exactly", () =>
        {
            var atDown = CspLightMath.Compute(0, 1, RpmLightMode.Smooth, 0, 0, default);
            Assert(atDown.Red == 128 && atDown.Green == 255 && atDown.Blue == 0, "HSV 90 degrees");
            var low = CspLightMath.Compute(-0.25, 1, RpmLightMode.Smooth, 0, 0, default);
            Assert(low.Red == 64 && low.Green == 128, "CSP mix=rpm*2+1");
            var dark = CspLightMath.Compute(-0.5, 1, RpmLightMode.Smooth, 0, 0, default);
            Assert(dark.Red == 0 && dark.Green == 0 && dark.Blue == 0, "idle invented a colour");
        });
        check("Discrete mode uses strict 0.2 and 0.6 thresholds", () =>
        {
            RpmLightFrame Color(double rpm) => CspLightMath.Compute(rpm, 1, RpmLightMode.Discrete, 0, 0, default);
            Assert(Color(0.2).Red == 0 && Color(0.2).Green == 255, "0.2 boundary");
            Assert(Color(0.200001).Red == 255 && Color(0.200001).Green == 255, "yellow onset");
            Assert(Color(0.6).Green == 255 && Color(0.600001).Green == 0, "red onset");
        });
        check("White LED steps and hardware brightness retain CSP strict comparisons", () =>
        {
            foreach (var (rpm, mask, brightness) in new[] {
                (0d,0,2), (0.0001,1,2), (0.25,1,2), (0.2501,3,1),
                (0.5,3,1), (0.5001,7,0), (0.75,7,0), (0.7501,15,0), (1,15,0), (1.0001,31,0) })
            {
                var frame = CspLightMath.Compute(rpm, 1, RpmLightMode.Smooth, 0, 0, default);
                Assert(frame.PlayerMask == mask && frame.PlayerBrightness == brightness, $"step {rpm}");
            }
        });
        check("Disable and missing static RPM clear output; reserved physics maxRpm is not trusted", () =>
        {
            var e = new RpmLightEngine();
            var car = new TelemetrySnapshot { Rpm = 7800, CurrentMaxRpm = 12000, Gear = 3, IsEngineRunning = 1 };
            Assert(e.Compute(car, At(0), settings) == RpmLightFrame.Off, "reserved field used as max RPM");
            Assert(e.Compute(car, At(0.01), settings, 8000).Red > 0, "valid static max rejected");
            Assert(e.Compute(car, At(0.02), settings with { Enabled = false }, 8000) == RpmLightFrame.Off, "disabled");
            Assert(car.CurrentMaxRpm == 12000, "haptic snapshot was changed");
        });
        check("Assist settings and unsupported intervention fields alone do not trigger a warning", () =>
        {
            var car = new TelemetrySnapshot { Rpm = 7000, Gear = 4, IsEngineRunning = 1, SpeedKmh = 100 };
            var before = new RpmLightEngine().Compute(car, At(0), settings, 8000);
            car.Abs = 1; car.Tc = 1; car.AbsLevel = 10; car.TcLevel = 10;
            Assert(new RpmLightEngine().Compute(car, At(0), settings, 8000) == before, "assist level used as event");
        });
        check("Lighting settings migrate legacy files and persist only enable and colour mode", () =>
        {
            string file = Path.Combine(Path.GetTempPath(), $"acc-light-test-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(file, "{\"Enabled\":false,\"Brightness\":5,\"ShiftFlash\":false,\"ShiftPermille\":900}");
                var legacy = PortableLightSettingsStore.LoadFrom(file, out bool recovered);
                Assert(!recovered && !legacy.Enabled && legacy.Mode == RpmLightMode.Smooth, "legacy file migration");
                File.WriteAllText(file, "{\"Enabled\":true,\"Brightness\":0}");
                Assert(!PortableLightSettingsStore.LoadFrom(file, out _).Enabled, "legacy zero-brightness Off lost");
                var expected = settings with { Mode = RpmLightMode.Discrete };
                Assert(PortableLightSettingsStore.TrySaveTo(file, expected), "save");
                Assert(PortableLightSettingsStore.LoadFrom(file, out recovered) == expected && !recovered, "round trip");
                using (var json = JsonDocument.Parse(File.ReadAllText(file)))
                    Assert(json.RootElement.EnumerateObject().Count() == 2, "complex settings persisted");
                File.WriteAllText(file, "{bad json");
                Assert(PortableLightSettingsStore.LoadFrom(file, out recovered) == settings && recovered, "corrupt file");
                Assert((settings with { Mode = (RpmLightMode)77 }).Sanitize().Mode == RpmLightMode.Smooth, "invalid mode");
            }
            finally { File.Delete(file); File.Delete(file + ".tmp"); }
            Assert(FeedbackProfile.Default.ToTuning() == FeedbackTuning.Neutral, "haptic default changed");
        });
        check("Optional static reader follows maxRpm changes without guessing top gear", () =>
        {
            string name = @"Local\ACC-Csp-Static-Test-" + Guid.NewGuid().ToString("N");
            using var map = MemoryMappedFile.CreateNew(name, 416);
            using var view = map.CreateViewAccessor();
            using var reader = new AccLightingMetadataReader([name], []);
            view.Write(412, 7500); reader.Refresh(At(0)); Assert(reader.MaxRpm == 7500, "static offset");
            view.Write(412, 8200); reader.Refresh(At(0.6)); Assert(reader.MaxRpm == 8200, "car change");
            view.Write(412, 0); reader.Refresh(At(1.2)); Assert(reader.MaxRpm == 0, "invalid max retained");
        });
        check("Optional graphics reader requires advancing packets and drops frozen replay state", () =>
        {
            string name = @"Local\ACC-Csp-Graphics-Test-" + Guid.NewGuid().ToString("N");
            using var map = MemoryMappedFile.CreateNew(name, 8);
            using var view = map.CreateViewAccessor();
            using var reader = new AccLightingGraphicsReader([name]);
            view.Write(0, 1); view.Write(4, 1); reader.Refresh(At(0));
            Assert(!reader.Context.IsReplay, "unverified first graphics sample");
            view.Write(0, 2); reader.Refresh(At(0.02)); Assert(reader.Context.IsReplay, "replay enum");
            view.Write(0, 3); view.Write(4, 2); reader.Refresh(At(0.04)); Assert(!reader.Context.IsReplay, "live enum");
            view.Write(0, 4); view.Write(4, 1); reader.Refresh(At(0.06));
            reader.Refresh(At(0.7)); Assert(!reader.Context.IsReplay, "frozen state retained");
        });
        check("Merged USB reports preserve all pre-lighting haptic bytes at every LED brightness", () =>
        {
            byte[] golden = Convert.FromHexString(
                "020F553BA6000000000000260102030405060708090A210B0C0D0E0F1011121314000000000000060000020202000000");
            var frame = new FeedbackFrame(0xA6, 0x3B,
                new TriggerEffect(0x21, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20),
                new TriggerEffect(0x26, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
            foreach (byte brightness in new byte[] { 0, 1, 2 })
            foreach (bool first in new[] { false, true })
            {
                var lights = new RpmLightFrame(23, 47, 81, 31, brightness);
                byte[] actual = new byte[48];
                DualSenseReportBuilder.Build(frame, false, actual, lights, first);
                for (int i = 0; i < actual.Length; i++)
                    if (i is not (42 or 43 or 44 or 45 or 46 or 47))
                        Assert(actual[i] == golden[i], $"haptic byte {i} changed");
                Assert(actual[43] == brightness && actual[44] == 31 && actual[45] == 23 && actual[46] == 47 && actual[47] == 81, "LED fields");
                Assert(actual[42] == (first ? 2 : 0), "LED animation initialization");
            }
        });
        check("Off output clears motors, trigger effects and every light", () =>
        {
            byte[] report = new byte[48]; DualSenseReportBuilder.Build(FeedbackFrame.Off, false, report);
            Assert(report[3] == 0 && report[4] == 0 && report[11] == 5 && report[22] == 5, "haptic cleanup");
            Assert(report.AsSpan(44, 4).IndexOfAnyExcept((byte)0) < 0, "light cleanup");
        });
    }
}
