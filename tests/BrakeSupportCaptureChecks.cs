using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ACCDualSenseFeedback.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Telemetry;

internal static class BrakeSupportCaptureChecks
{
    private static long At(int ms) => Stopwatch.Frequency + (long)(ms * Stopwatch.Frequency / 1000d);
    private static void Assert(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }

    private static void RemoveTestDirectory(string directory)
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(directory);
        Assert(target.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(target).StartsWith("acc-brake-", StringComparison.Ordinal), "test cleanup escaped its temporary directory");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    private static BrakeSupportSample Sample(int ms, TriggerEffect left,
        BrakeHidWriteState state = BrakeHidWriteState.Skipped, bool absEnabled = true)
    {
        var t = new TelemetrySnapshot
        {
            PacketId = ms + 1, ObservedTimestamp = At(ms), SpeedKmh = 120,
            Brake = .85f, Abs = 1, AbsVibration = .7f, AbsLevel = 3
        };
        return new BrakeSupportSample(At(ms), t,
            new FeedbackFrame(20, 30, left, TriggerEffect.FeedbackZones(2, 2, 2, 3, 3, 3, 4, 4, 5, 5)),
            FeedbackTuning.Neutral with { AbsPulseScale = absEnabled ? 1 : 0 },
            new BrakeRawInput(240, 0, At(ms), true, true), state);
    }

    public static void Run(Action<string, Action> check)
    {
        check("Brake diagnostic baseline uses current L2 support zones and decoded pulse frequency", () =>
        {
            var baseline = BrakeSupportCapture.CreateBaselineResistance(1);
            Assert(baseline == TriggerEffect.FeedbackZones(3, 4, 4, 5, 6, 6, 7, 7, 8, 8), "baseline differs from current L2 support");
            Assert(BrakeSupportCapture.CreateBaselineResistance(0) == TriggerEffect.Off, "disabled resistance changed");
            var maximum = BrakeSupportCapture.CreateBaselineResistance(1.35f);
            Assert(maximum == TriggerEffect.FeedbackZones(4, 5, 5, 7, 8, 8, 8, 8, 8, 8), "maximum setting baseline is wrong");
            Assert(new BrakeSupportAnalysis().Finish().BrakeProfile == "1.2.0-preview.4-griploss.3-l2.2",
                "diagnostic summary identifies the wrong L2 profile");
            var pulse = TriggerEffect.Vibration(2, 5, 15);
            Assert(pulse.P8 == 15 && BrakeSupportCapture.ZoneStrength(pulse, 0) == 0 &&
                BrakeSupportCapture.ZoneStrength(pulse, 9) == 5, "pulse fields decoded incorrectly");
            Assert(BrakeSupportCapture.NominalZone(0) == 0 && BrakeSupportCapture.NominalZone(255) == 9,
                "nominal raw bins escape bounds");
        });

        check("Disabled ABS cannot be blamed for game ABS signal or ordinary curve lightness", () =>
        {
            var analysis = new BrakeSupportAnalysis();
            for (int ms = 0; ms <= 400; ms += 20)
                analysis.Add(Sample(ms, BrakeSupportCapture.CreateBaselineResistance(1),
                    ms == 0 ? BrakeHidWriteState.ChangedSuccess : BrakeHidWriteState.Skipped, false));
            var report = analysis.Finish();
            Assert(report.AbsSignalRows > 0 && report.AbsDisabledSignalRows == report.Rows,
                "did not retain game ABS while software ABS disabled");
            Assert(report.RequestedAbs15HzRows == 0 && report.RequestedPulseSeconds == 0 &&
                !report.HasSufficientModeEvidence, "invented an ABS pulse or force conclusion");
            Assert(report.ToChineseMarkdown().Contains("不能把本次基础阻力的轻感归因"),
                "disabled-ABS caveat missing");
        });

        check("Brake diagnostic measures command replacement events and successful-write coverage", () =>
        {
            var analysis = new BrakeSupportAnalysis();
            var baseline = BrakeSupportCapture.CreateBaselineResistance(1);
            var pulse = TriggerEffect.Vibration(2, 5, 15);
            for (int ms = 0; ms <= 400; ms += 20)
            {
                bool pulsing = ms is >= 100 and < 300;
                bool changed = ms is 0 or 100 or 300;
                analysis.Add(Sample(ms, pulsing ? pulse : baseline,
                    changed ? BrakeHidWriteState.ChangedSuccess : BrakeHidWriteState.Skipped));
            }
            var report = analysis.Finish();
            Assert(report.RequestedAbs15HzEvents == 1 && report.RequestedPulseEvents == 1 &&
                report.RequestedLock8HzEvents == 0, "mode events misclassified");
            Assert(Math.Abs(report.RequestedAbs15HzSeconds - .2) < 1e-6 &&
                Math.Abs(report.SuccessfulCommandPulseSeconds - .2) < 1e-6 &&
                Math.Abs(report.SuccessfulCommandAbs15HzSeconds - .2) < 1e-6, "held command durations wrong");
            Assert(report.NominalPulseLowerRows == 10 && report.HighNominalZoneRows == report.ValidBrakingRows,
                "nominal values or raw coverage missing");
            Assert(report.HasSufficientModeEvidence && report.ToChineseMarkdown().Contains("不能据此证明手指受到的力下降"),
                "mode evidence was confused with actual force evidence");
        });

        check("Failed writes, mechanical lock and dry-run cannot become successful ABS evidence", () =>
        {
            var analysis = new BrakeSupportAnalysis();
            var pulse = TriggerEffect.Vibration(2, 5, 15);
            analysis.Add(Sample(0, pulse, BrakeHidWriteState.Failed));
            analysis.Add(Sample(20, pulse));
            analysis.Add(Sample(40, pulse, BrakeHidWriteState.HeartbeatSuccess));
            analysis.Add(Sample(60, pulse));
            analysis.Add(Sample(80, TriggerEffect.Vibration(2, 6, 8), BrakeHidWriteState.DryRun));
            analysis.Add(Sample(100, TriggerEffect.Vibration(2, 6, 8), BrakeHidWriteState.DryRun));
            var report = analysis.Finish();
            Assert(report.FailedWrites == 1 && report.RequestedLock8HzEvents == 1 &&
                report.RequestedAbs15HzEvents == 1 && report.DryRunRows == 2, "write/mode classification lost");
            Assert(Math.Abs(report.SuccessfulCommandPulseSeconds - .04) < 1e-6,
                "failed or dry-run command treated as applied");
            Assert(!report.HasSufficientModeEvidence, "failure was ignored in evidence quality");
            var dryAbs = new BrakeSupportAnalysis();
            dryAbs.Add(Sample(0, pulse, BrakeHidWriteState.DryRun));
            dryAbs.Add(Sample(20, pulse, BrakeHidWriteState.DryRun));
            for (int ms = 40; ms <= 400; ms += 20)
                dryAbs.Add(Sample(ms, TriggerEffect.Vibration(2, 6, 8),
                    ms == 40 ? BrakeHidWriteState.ChangedSuccess : BrakeHidWriteState.Skipped));
            var mixed = dryAbs.Finish();
            Assert(mixed.SuccessfulCommandPulseSeconds > 0 && mixed.RequestedAbs15HzRows > 0 &&
                mixed.SuccessfulCommandAbs15HzSeconds == 0 && !mixed.HasSufficientModeEvidence,
                "successful mechanical lock was substituted for successful ABS evidence");
        });

        check("Brake capture rejects stale or other-device raw input and avoids extrapolating gaps", () =>
        {
            var analysis = new BrakeSupportAnalysis();
            var pulse = TriggerEffect.Vibration(2, 5, 15);
            var a = Sample(0, pulse, BrakeHidWriteState.ChangedSuccess);
            analysis.Add(a with { RawInput = a.RawInput with { SameDevice = false } });
            var b = Sample(2000, pulse, BrakeHidWriteState.ChangedSuccess);
            analysis.Add(b with { RawInput = b.RawInput with { ObservedTimestamp = At(0) } });
            var report = analysis.Finish();
            Assert(report.ValidBrakingRawRows == 0 && report.GapsOver100Ms == 1 &&
                report.RequestedPulseSeconds == 0 && !report.HasSufficientModeEvidence,
                "stale/mismatched position or 2-second gap invented evidence");
            var final = analysis.Finish(dropped: 1, criticalDropped: 1);
            Assert(!final.HasSufficientModeEvidence && final.ToChineseMarkdown().Contains("不能声称完整捕获"),
                "dropped critical event was not disclosed");
            var stale = Sample(150, pulse);
            var staleTelemetry = stale.Telemetry;
            staleTelemetry.ObservedTimestamp = At(0);
            stale = stale with { Telemetry = staleTelemetry };
            Assert(!BrakeSupportCapture.TelemetryFresh(stale), "120-200ms telemetry incorrectly treated as fresh");
        });

        check("Brake diagnostic retains sub-20ms mode transitions and failures, flushes CSV and both reports", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "acc-brake-capture-" + Guid.NewGuid().ToString("N"));
            try
            {
                var capture = new BrakeSupportCapture(directory);
                var baseline = BrakeSupportCapture.CreateBaselineResistance(1);
                var pulse = TriggerEffect.Vibration(2, 5, 15);
                capture.Record(Sample(0, baseline, BrakeHidWriteState.ChangedSuccess));
                capture.Record(Sample(4, pulse, BrakeHidWriteState.ChangedSuccess));
                capture.Record(Sample(8, baseline, BrakeHidWriteState.ChangedSuccess));
                capture.Record(Sample(10, baseline, BrakeHidWriteState.Failed));
                capture.Record(Sample(12, baseline, BrakeHidWriteState.ChangedSuccess));
                capture.Record(Sample(14, baseline)); // ordinary redundant frame is throttled
                capture.Dispose();
                Assert(capture.Completed && capture.Failure is null, "writer did not flush cleanly");
                var lines = File.ReadAllLines(Path.Combine(directory, "brake-support.csv"));
                Assert(lines.Length == 6 && lines.All(line => line.Split(',').Length == lines[0].Split(',').Length),
                    "mode/failure sample dropped or CSV column mismatch");
                var report = JsonSerializer.Deserialize<BrakeSupportReport>(File.ReadAllText(Path.Combine(directory, "summary.json")))!;
                Assert(report.Rows == 5 && report.Throttled == 1 && report.RequestedAbs15HzEvents == 1 &&
                    report.FailedWrites == 1 && report.DroppedRows == 0, "queued report differs from samples");
                Assert(File.ReadAllText(Path.Combine(directory, "分析摘要.zh-CN.md")).Contains("未标定为机械角"),
                    "nominal-zone limitation missing");
            }
            finally { RemoveTestDirectory(directory); }
        });

        check("Brake diagnostic producer does not allocate per recorded/throttled frame", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "acc-brake-allocation-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var capture = new BrakeSupportCapture(directory);
                var sample = Sample(0, BrakeSupportCapture.CreateBaselineResistance(1), BrakeHidWriteState.ChangedSuccess);
                capture.Record(sample);
                for (int i = 1; i <= 2000; i++) capture.Record(sample with { Timestamp = sample.Timestamp + i * Stopwatch.Frequency / 1000 });
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 2001; i <= 10000; i++) capture.Record(sample with { Timestamp = sample.Timestamp + i * Stopwatch.Frequency / 1000 });
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert(bytes == 0, $"producer allocated {bytes} bytes");
            }
            finally { RemoveTestDirectory(directory); }
        });

        check("Brake capture shutdown and writer failure never escape into the output producer", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "acc-brake-race-" + Guid.NewGuid().ToString("N"));
            try
            {
                var capture = new BrakeSupportCapture(directory);
                var sample = Sample(0, BrakeSupportCapture.CreateBaselineResistance(1), BrakeHidWriteState.ChangedSuccess);
                using var entered = new ManualResetEventSlim();
                Task producer = Task.Run(() =>
                {
                    entered.Set();
                    for (int i = 1; i <= 20000; i++)
                        capture.Record(sample with { Timestamp = sample.Timestamp + i * Stopwatch.Frequency / 1000 });
                });
                entered.Wait();
                capture.Dispose();
                producer.GetAwaiter().GetResult();
                capture.Record(sample); // stopping is terminal and non-throwing
                Assert(capture.Completed && capture.Failure is null, "normal concurrent stop failed");
            }
            finally { RemoveTestDirectory(directory); }

            directory = Path.Combine(Path.GetTempPath(), "acc-brake-fault-" + Guid.NewGuid().ToString("N"));
            try
            {
                var capture = new BrakeSupportCapture(directory);
                // Fault injection models the writer retiring its signal between
                // Record's initial failure check and notification. No hardware.
                var signal = (AutoResetEvent)typeof(BrakeSupportCapture)
                    .GetField("_ready", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture)!;
                signal.Dispose();
                var sample = Sample(0, BrakeSupportCapture.CreateBaselineResistance(1), BrakeHidWriteState.ChangedSuccess);
                for (int i = 1; i <= 1000; i++)
                    capture.Record(sample with { Timestamp = sample.Timestamp + i * Stopwatch.Frequency / 1000 });
                capture.Dispose();
                capture.Record(sample);
                Assert(capture.Failure is not null && capture.Completed, "writer failure was neither retained nor isolated");
            }
            finally { RemoveTestDirectory(directory); }
        });
    }
}
