using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Diagnostics;

internal enum BrakeHidWriteState
{
    ChangedSuccess, HeartbeatSuccess, Failed, Skipped, DryRun, NoDevice, Stopped
}

// Raw input must come from the output device's physical HID stream. This is not
// ACC Brake or a virtual-controller trigger value. The consumer also checks age.
internal readonly record struct BrakeRawInput(
    byte L2, byte R2, long ObservedTimestamp, bool Valid, bool SameDevice);

internal readonly record struct BrakeSupportSample(
    long Timestamp,
    TelemetrySnapshot Telemetry,
    FeedbackFrame Output,
    FeedbackTuning Tuning,
    BrakeRawInput RawInput,
    BrakeHidWriteState WriteState);

// Only constructed by the explicit brake diagnostic mode. The sole producer is
// the feedback output thread; all formatting, analysis, and file I/O live on a
// separate writer. The preallocated SPSC ring never allocates or waits in Record.
internal sealed class BrakeSupportCapture : IDisposable
{
    private const int Capacity = 4096;
    private readonly BrakeSupportSample[] _ring = new BrakeSupportSample[Capacity];
    private readonly AutoResetEvent _ready = new(false);
    private readonly Thread _thread;
    private long _published, _consumed;
    private long _submitted, _accepted, _throttled, _dropped, _criticalDropped, _invalidTime;
    private long _lastAcceptedAt;
    private BrakeSupportSample _lastAccepted;
    private int _disposed, _recording, _completed;
    private Exception? _failure;

    public string DirectoryPath { get; }
    public long DroppedRows => Interlocked.Read(ref _dropped);
    public Exception? Failure => Volatile.Read(ref _failure);
    public bool Completed => Volatile.Read(ref _completed) != 0;

    public BrakeSupportCapture(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        // Refuse accidental overwrite of an earlier run, including empty captures.
        using (new FileStream(Path.Combine(DirectoryPath, "brake-support.csv"), FileMode.CreateNew)) { }
        _thread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "Brake support diagnostic writer",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
    }

    public static TriggerEffect CreateBaselineResistance(float scale)
        => BrakePedalFeedback.CreateResistance(scale);

    // Call after the output decision (success/failure/deduplicated/dry-run).
    // One producer only. This method deliberately never throws on disk failure.
    public void Record(in BrakeSupportSample sample)
    {
        if (Volatile.Read(ref _disposed) != 0 || Failure is not null)
            return;
        Interlocked.Increment(ref _recording);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _submitted++;
            if (sample.Timestamp <= 0 || sample.Timestamp <= _lastAcceptedAt)
            { _invalidTime++; return; }
            bool critical = _lastAcceptedAt == 0 || IsCritical(sample, _lastAccepted);
            if (!critical && sample.Timestamp - _lastAcceptedAt < Stopwatch.Frequency / 50)
            { _throttled++; return; }
            long write = _published;
            if (write - Volatile.Read(ref _consumed) >= Capacity)
            {
                Interlocked.Increment(ref _dropped);
                if (critical) Interlocked.Increment(ref _criticalDropped);
                return;
            }
            _ring[(int)(write % Capacity)] = sample;
            Volatile.Write(ref _published, write + 1);
            _lastAccepted = sample;
            _lastAcceptedAt = sample.Timestamp;
            _accepted++;
            // A disk failure can finish the writer after the initial Failure
            // check. Its signal may already be disposed; capture failure must
            // never escape into the live controller output loop.
            try { _ready.Set(); }
            catch (ObjectDisposedException exception) { SetFailure(exception); }
        }
        finally { Interlocked.Decrement(ref _recording); }
    }

    private static bool IsCritical(in BrakeSupportSample a, in BrakeSupportSample b)
        => a.Output.LeftTrigger.Mode != b.Output.LeftTrigger.Mode ||
           (a.Output.LeftTrigger.Mode == 0x26 && a.Output.LeftTrigger.P8 != b.Output.LeftTrigger.P8) ||
           (a.Output.LeftTrigger != b.Output.LeftTrigger &&
            a.WriteState is BrakeHidWriteState.ChangedSuccess or BrakeHidWriteState.HeartbeatSuccess) ||
           a.WriteState is BrakeHidWriteState.Failed or BrakeHidWriteState.NoDevice or BrakeHidWriteState.Stopped ||
           (a.WriteState != b.WriteState && b.WriteState is BrakeHidWriteState.Failed or BrakeHidWriteState.NoDevice or BrakeHidWriteState.Stopped or BrakeHidWriteState.DryRun) ||
           a.Tuning.BrakeResistanceScale != b.Tuning.BrakeResistanceScale ||
           a.Tuning.AbsPulseScale != b.Tuning.AbsPulseScale || a.Tuning.LockPulseScale != b.Tuning.LockPulseScale ||
           a.RawInput.Valid != b.RawInput.Valid || a.RawInput.SameDevice != b.RawInput.SameDevice ||
           RawFresh(a) != RawFresh(b) || TelemetryFresh(a) != TelemetryFresh(b) ||
           AbsReported(a.Telemetry) != AbsReported(b.Telemetry) ||
           (a.Telemetry.Brake >= .05f) != (b.Telemetry.Brake >= .05f);

    private void WriteLoop()
    {
        var analysis = new BrakeSupportAnalysis();
        try
        {
            using var writer = new StreamWriter(Path.Combine(DirectoryPath, "brake-support.csv"), false,
                new UTF8Encoding(false), 128 * 1024);
            writer.WriteLine(Header);
            while (true)
            {
                long read = _consumed;
                long available = Volatile.Read(ref _published);
                while (read < available)
                {
                    BrakeSupportSample sample = _ring[(int)(read % Capacity)];
                    read++;
                    Volatile.Write(ref _consumed, read);
                    analysis.Add(sample);
                    writer.WriteLine(FormatRow(sample, analysis.FirstTimestamp));
                }
                if (Volatile.Read(ref _disposed) != 0 && Volatile.Read(ref _recording) == 0 &&
                    read == Volatile.Read(ref _published)) break;
                _ready.WaitOne(100);
            }
            writer.Flush();
        }
        catch (Exception exception) { SetFailure(exception); }
        finally
        {
            try
            {
                BrakeSupportReport report = analysis.Finish(_submitted, _accepted, _throttled, DroppedRows,
                    Interlocked.Read(ref _criticalDropped), _invalidTime, Failure?.Message);
                File.WriteAllText(Path.Combine(DirectoryPath, "summary.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(DirectoryPath, "分析摘要.zh-CN.md"), report.ToChineseMarkdown(), new UTF8Encoding(false));
            }
            catch (Exception exception) { SetFailure(exception); }
            Volatile.Write(ref _completed, 1);
            // Dispose only on this thread: a timed-out Dispose must never close
            // a signal or writer still in use by the background thread.
            _ready.Dispose();
        }
    }

    private void SetFailure(Exception exception) => Interlocked.CompareExchange(ref _failure, exception, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _ready.Set(); } catch (ObjectDisposedException) { }
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
            SetFailure(new IOException("Brake capture flush exceeded 5 seconds; report may still be incomplete."));
    }

    internal static bool AbsReported(in TelemetrySnapshot t)
        => (float.IsFinite(t.Abs) && MathF.Abs(t.Abs) > .01f) ||
           (float.IsFinite(t.AbsVibration) && MathF.Abs(t.AbsVibration) > .035f);

    internal static bool TelemetryFresh(in BrakeSupportSample s)
        => s.Telemetry.ObservedTimestamp > 0 && s.Timestamp >= s.Telemetry.ObservedTimestamp &&
           s.Timestamp - s.Telemetry.ObservedTimestamp <= Stopwatch.Frequency * 120 / 1000;

    internal static bool RawFresh(in BrakeSupportSample s)
        => s.RawInput.Valid && s.RawInput.SameDevice && s.RawInput.ObservedTimestamp > 0 &&
           s.Timestamp >= s.RawInput.ObservedTimestamp &&
           s.Timestamp - s.RawInput.ObservedTimestamp <= Stopwatch.Frequency / 10;

    // A nominal normalized bin only. HID sensor position is not calibrated to
    // the motor's physical angle or the ten protocol actuator zones.
    internal static int NominalZone(byte raw) => Math.Min(9, raw * 10 / 256);

    internal static byte ZoneStrength(in TriggerEffect effect, int zone)
    {
        if (zone is < 0 or > 9 || effect.Mode is not (0x21 or 0x26)) return 0;
        int active = effect.P0 | effect.P1 << 8;
        uint packed = (uint)(effect.P2 | effect.P3 << 8 | effect.P4 << 16 | effect.P5 << 24);
        return (active & (1 << zone)) == 0 ? (byte)0 : (byte)(((packed >> (zone * 3)) & 7) + 1);
    }

    private const string Header =
        "time_s,packet,telemetry_age_ms,telemetry_fresh,speed_kmh,acc_brake,abs,abs_vibration,abs_level," +
        "brake_scale,abs_scale,lock_scale,raw_l2,raw_r2,raw_age_ms,raw_valid,raw_same_device,raw_fresh," +
        "raw_nominal_zone,baseline_nominal_strength,requested_nominal_strength,write_state,left_grip,right_grip," +
        "l2_mode,l2_p0,l2_p1,l2_p2,l2_p3,l2_p4,l2_p5,l2_p6,l2_p7,l2_p8,l2_p9," +
        "l2_z0,l2_z1,l2_z2,l2_z3,l2_z4,l2_z5,l2_z6,l2_z7,l2_z8,l2_z9,l2_pulse_hz," +
        "r2_mode,r2_p0,r2_p1,r2_p2,r2_p3,r2_p4,r2_p5,r2_p6,r2_p7,r2_p8,r2_p9," +
        "r2_z0,r2_z1,r2_z2,r2_z3,r2_z4,r2_z5,r2_z6,r2_z7,r2_z8,r2_z9,r2_pulse_hz," +
        "baseline_l2_mode,baseline_z0,baseline_z1,baseline_z2,baseline_z3,baseline_z4,baseline_z5,baseline_z6,baseline_z7,baseline_z8,baseline_z9";

    private static string FormatRow(in BrakeSupportSample s, long firstTimestamp)
    {
        var row = new StringBuilder(768);
        Add(row, (s.Timestamp - firstTimestamp) / (double)Stopwatch.Frequency);
        Add(row, s.Telemetry.PacketId);
        Add(row, AgeMs(s.Timestamp, s.Telemetry.ObservedTimestamp)); Add(row, TelemetryFresh(s) ? 1 : 0);
        Add(row, s.Telemetry.SpeedKmh); Add(row, s.Telemetry.Brake); Add(row, s.Telemetry.Abs);
        Add(row, s.Telemetry.AbsVibration); Add(row, s.Telemetry.AbsLevel);
        Add(row, s.Tuning.BrakeResistanceScale); Add(row, s.Tuning.AbsPulseScale); Add(row, s.Tuning.LockPulseScale);
        Add(row, s.RawInput.L2); Add(row, s.RawInput.R2);
        Add(row, AgeMs(s.Timestamp, s.RawInput.ObservedTimestamp)); Add(row, s.RawInput.Valid ? 1 : 0);
        Add(row, s.RawInput.SameDevice ? 1 : 0); bool rawFresh = RawFresh(s); Add(row, rawFresh ? 1 : 0);
        int zone = rawFresh ? NominalZone(s.RawInput.L2) : -1;
        TriggerEffect baseline = CreateBaselineResistance(s.Tuning.BrakeResistanceScale);
        Add(row, zone); Add(row, zone >= 0 ? ZoneStrength(baseline, zone) : -1);
        Add(row, zone >= 0 ? ZoneStrength(s.Output.LeftTrigger, zone) : -1);
        Add(row, (int)s.WriteState); Add(row, s.Output.LeftActuator); Add(row, s.Output.RightActuator);
        AddTrigger(row, s.Output.LeftTrigger); AddTrigger(row, s.Output.RightTrigger);
        Add(row, baseline.Mode);
        for (int i = 0; i < 10; i++) Add(row, ZoneStrength(baseline, i));
        return row.ToString();
    }

    private static double AgeMs(long now, long observed)
        => observed <= 0 ? -1 : (now - observed) * 1000d / Stopwatch.Frequency;

    private static void Add(StringBuilder row, double value)
    {
        if (row.Length > 0) row.Append(',');
        row.Append(value.ToString("G9", CultureInfo.InvariantCulture));
    }

    private static void AddTrigger(StringBuilder row, in TriggerEffect effect)
    {
        Add(row, effect.Mode); Add(row, effect.P0); Add(row, effect.P1); Add(row, effect.P2); Add(row, effect.P3);
        Add(row, effect.P4); Add(row, effect.P5); Add(row, effect.P6); Add(row, effect.P7); Add(row, effect.P8); Add(row, effect.P9);
        for (int i = 0; i < 10; i++) Add(row, ZoneStrength(effect, i));
        Add(row, effect.Mode == 0x26 ? effect.P8 : 0);
    }
}

// Pure analysis is separately testable without a controller or file writer.
internal sealed class BrakeSupportAnalysis
{
    private BrakeSupportSample _previous;
    private TriggerEffect _lastSuccessfulCommand;
    private bool _hasPrevious, _hasSuccessfulCommand, _previousConfirmed;
    private long _rows, _validBrakeRows, _rawFreshRows, _validBrakeRawRows, _highZoneRows;
    private long _pulseRows, _absRows, _lockRows, _absEvents, _lockEvents, _pulseEvents;
    private long _absSoftwareEnabledRows, _lockSoftwareEnabledRows, _absSignalRows, _absDisabledSignalRows;
    private long _failures, _successfulWrites, _dryRunRows, _noDeviceRows, _staleTelemetryRows, _gaps;
    private long _nominalPulseLowerRows, _nominalPulseHigherRows, _nominalPulseEqualRows;
    private double _validSeconds, _pulseSeconds, _absSeconds, _lockSeconds, _confirmedPulseSeconds, _confirmedAbsSeconds;
    public long FirstTimestamp { get; private set; }

    public void Add(in BrakeSupportSample sample)
    {
        if (sample.Timestamp <= 0 || (_hasPrevious && sample.Timestamp <= _previous.Timestamp)) return;
        if (!_hasPrevious) FirstTimestamp = sample.Timestamp;
        bool fresh = BrakeSupportCapture.TelemetryFresh(sample);
        bool braking = IsBraking(sample);
        bool rawFresh = BrakeSupportCapture.RawFresh(sample);
        bool pulse = sample.Output.LeftTrigger.Mode == 0x26;
        bool abs = pulse && sample.Output.LeftTrigger.P8 == 15 && sample.Tuning.AbsPulseScale > 0;
        bool locked = pulse && sample.Output.LeftTrigger.P8 == 8 && sample.Tuning.LockPulseScale > 0;
        double dt = _hasPrevious ? (sample.Timestamp - _previous.Timestamp) / (double)Stopwatch.Frequency : 0;
        bool continuous = _hasPrevious && dt <= .100001;
        if (_hasPrevious && !continuous) _gaps++;
        if (continuous && IsBraking(_previous))
        {
            _validSeconds += dt;
            if (_previous.Output.LeftTrigger.Mode == 0x26)
            {
                _pulseSeconds += dt;
                if (_previousConfirmed) _confirmedPulseSeconds += dt;
                if (_previous.Output.LeftTrigger.P8 == 15 && _previous.Tuning.AbsPulseScale > 0)
                {
                    _absSeconds += dt;
                    if (_previousConfirmed) _confirmedAbsSeconds += dt;
                }
                if (_previous.Output.LeftTrigger.P8 == 8 && _previous.Tuning.LockPulseScale > 0) _lockSeconds += dt;
            }
        }
        _rows++;
        if (!fresh) _staleTelemetryRows++;
        if (rawFresh) _rawFreshRows++;
        if (sample.Tuning.AbsPulseScale > 0) _absSoftwareEnabledRows++;
        if (sample.Tuning.LockPulseScale > 0) _lockSoftwareEnabledRows++;
        if (fresh && BrakeSupportCapture.AbsReported(sample.Telemetry))
        {
            _absSignalRows++;
            if (sample.Tuning.AbsPulseScale <= 0) _absDisabledSignalRows++;
        }
        if (braking)
        {
            _validBrakeRows++;
            if (pulse) _pulseRows++;
            if (abs) _absRows++;
            if (locked) _lockRows++;
            if (pulse && (!continuous || !IsBraking(_previous) || _previous.Output.LeftTrigger.Mode != 0x26)) _pulseEvents++;
            if (abs && (!continuous || !IsBraking(_previous) || _previous.Output.LeftTrigger.Mode != 0x26 || _previous.Output.LeftTrigger.P8 != 15)) _absEvents++;
            if (locked && (!continuous || !IsBraking(_previous) || _previous.Output.LeftTrigger.Mode != 0x26 || _previous.Output.LeftTrigger.P8 != 8)) _lockEvents++;
            if (rawFresh)
            {
                _validBrakeRawRows++;
                int zone = BrakeSupportCapture.NominalZone(sample.RawInput.L2);
                if (zone >= 7) _highZoneRows++;
                if (pulse)
                {
                    int baseline = BrakeSupportCapture.ZoneStrength(BrakeSupportCapture.CreateBaselineResistance(sample.Tuning.BrakeResistanceScale), zone);
                    int amplitude = BrakeSupportCapture.ZoneStrength(sample.Output.LeftTrigger, zone);
                    if (amplitude < baseline) _nominalPulseLowerRows++;
                    else if (amplitude > baseline) _nominalPulseHigherRows++;
                    else _nominalPulseEqualRows++;
                }
            }
        }
        switch (sample.WriteState)
        {
            case BrakeHidWriteState.ChangedSuccess:
            case BrakeHidWriteState.HeartbeatSuccess:
                _successfulWrites++; _hasSuccessfulCommand = true; _lastSuccessfulCommand = sample.Output.LeftTrigger; break;
            case BrakeHidWriteState.Failed:
                _failures++; _hasSuccessfulCommand = false; break;
            case BrakeHidWriteState.NoDevice:
            case BrakeHidWriteState.Stopped:
                _noDeviceRows++; _hasSuccessfulCommand = false; break;
            case BrakeHidWriteState.DryRun:
                _dryRunRows++; _hasSuccessfulCommand = false; break;
        }
        _previousConfirmed = _hasSuccessfulCommand && _lastSuccessfulCommand == sample.Output.LeftTrigger;
        _previous = sample; _hasPrevious = true;
    }

    private static bool IsBraking(in BrakeSupportSample s)
        => BrakeSupportCapture.TelemetryFresh(s) && float.IsFinite(s.Telemetry.Brake) && s.Telemetry.Brake >= .05f &&
           float.IsFinite(s.Telemetry.SpeedKmh) && MathF.Abs(s.Telemetry.SpeedKmh) >= 5;

    public BrakeSupportReport Finish(long submitted = 0, long accepted = 0, long throttled = 0,
        long dropped = 0, long criticalDropped = 0, long invalidTime = 0, string? failure = null)
        => new()
        {
            Submitted = submitted, Accepted = accepted, Throttled = throttled, DroppedRows = dropped,
            CriticalDroppedRows = criticalDropped, InvalidTimestampRows = invalidTime, WriterFailure = failure,
            Rows = _rows, ValidBrakingRows = _validBrakeRows, RawFreshRows = _rawFreshRows,
            ValidBrakingRawRows = _validBrakeRawRows, HighNominalZoneRows = _highZoneRows,
            RequestedPulseRows = _pulseRows, RequestedAbs15HzRows = _absRows, RequestedLock8HzRows = _lockRows,
            RequestedPulseEvents = _pulseEvents, RequestedAbs15HzEvents = _absEvents, RequestedLock8HzEvents = _lockEvents,
            AbsSoftwareEnabledRows = _absSoftwareEnabledRows, LockSoftwareEnabledRows = _lockSoftwareEnabledRows,
            AbsSignalRows = _absSignalRows, AbsDisabledSignalRows = _absDisabledSignalRows,
            FailedWrites = _failures, SuccessfulWrites = _successfulWrites, DryRunRows = _dryRunRows, NoDeviceRows = _noDeviceRows,
            StaleTelemetryRows = _staleTelemetryRows, GapsOver100Ms = _gaps,
            ValidBrakingSeconds = _validSeconds, RequestedPulseSeconds = _pulseSeconds,
            RequestedAbs15HzSeconds = _absSeconds, RequestedLock8HzSeconds = _lockSeconds,
            SuccessfulCommandPulseSeconds = _confirmedPulseSeconds,
            SuccessfulCommandAbs15HzSeconds = _confirmedAbsSeconds,
            NominalPulseLowerRows = _nominalPulseLowerRows, NominalPulseHigherRows = _nominalPulseHigherRows,
            NominalPulseEqualRows = _nominalPulseEqualRows
        };
}

internal sealed record BrakeSupportReport
{
    public string Baseline { get; init; } = "1.2.0-preview.4-griploss.3";
    public string BrakeProfile { get; init; } = BrakePedalFeedback.ProfileVersion;
    public long Submitted { get; init; }
    public long Accepted { get; init; }
    public long Throttled { get; init; }
    public long DroppedRows { get; init; }
    public long CriticalDroppedRows { get; init; }
    public long InvalidTimestampRows { get; init; }
    public string? WriterFailure { get; init; }
    public long Rows { get; init; }
    public long ValidBrakingRows { get; init; }
    public long RawFreshRows { get; init; }
    public long ValidBrakingRawRows { get; init; }
    public long HighNominalZoneRows { get; init; }
    public long RequestedPulseRows { get; init; }
    public long RequestedAbs15HzRows { get; init; }
    public long RequestedLock8HzRows { get; init; }
    public long RequestedPulseEvents { get; init; }
    public long RequestedAbs15HzEvents { get; init; }
    public long RequestedLock8HzEvents { get; init; }
    public long AbsSoftwareEnabledRows { get; init; }
    public long LockSoftwareEnabledRows { get; init; }
    public long AbsSignalRows { get; init; }
    public long AbsDisabledSignalRows { get; init; }
    public long FailedWrites { get; init; }
    public long SuccessfulWrites { get; init; }
    public long DryRunRows { get; init; }
    public long NoDeviceRows { get; init; }
    public long StaleTelemetryRows { get; init; }
    public long GapsOver100Ms { get; init; }
    public double ValidBrakingSeconds { get; init; }
    public double RequestedPulseSeconds { get; init; }
    public double RequestedAbs15HzSeconds { get; init; }
    public double RequestedLock8HzSeconds { get; init; }
    public double SuccessfulCommandPulseSeconds { get; init; }
    public double SuccessfulCommandAbs15HzSeconds { get; init; }
    public long NominalPulseLowerRows { get; init; }
    public long NominalPulseHigherRows { get; init; }
    public long NominalPulseEqualRows { get; init; }
    public bool HasSufficientModeEvidence => ValidBrakingRows >= 10 && RequestedAbs15HzRows > 0 &&
        ValidBrakingRawRows > 0 && HighNominalZoneRows > 0 && SuccessfulCommandAbs15HzSeconds > 0 && DroppedRows == 0 &&
        CriticalDroppedRows == 0 && GapsOver100Ms == 0 && InvalidTimestampRows == 0 &&
        FailedWrites == 0 && WriterFailure is null;

    public string ToChineseMarkdown()
    {
        var text = new StringBuilder();
        text.AppendLine("# L2 刹车支撑诊断").AppendLine();
        text.AppendLine($"反馈基线：`{Baseline}`；L2 曲线：`{BrakeProfile}`。记录模式不额外改变反馈参数或反馈算法。").AppendLine();
        text.AppendLine("**本报告只分析软件命令、原始位置和 HID 写入结果。手柄没有实际反力传感器；不能据此证明手指受到的力下降，也不能把阻力档位与振动振幅直接比较为轻重。**").AppendLine();
        text.AppendLine(FormattableString.Invariant($"- 已写入 {Rows} 行；有效制动 {ValidBrakingSeconds:F3} 秒（{ValidBrakingRows} 行；新鲜遥测、刹车输入≥0.05、速度≥5 km/h）。"));
        text.AppendLine(FormattableString.Invariant($"- 有效制动内请求 L2 脉冲模式 0x26 共 {RequestedPulseEvents} 段、{RequestedPulseSeconds:F3} 秒；其中 ABS 15 Hz {RequestedAbs15HzEvents} 段、{RequestedAbs15HzSeconds:F3} 秒，抱死提示 8 Hz {RequestedLock8HzEvents} 段、{RequestedLock8HzSeconds:F3} 秒。"));
        text.AppendLine(FormattableString.Invariant($"- 上述脉冲中，有成功 HID 写入、且后续命令保持一致的记录区间为 {SuccessfulCommandPulseSeconds:F3} 秒，其中 ABS 15 Hz 为 {SuccessfulCommandAbs15HzSeconds:F3} 秒。这仍不代表测到了实际反力。"));
        text.AppendLine($"- 同一物理输出设备的新鲜原始位置：{RawFreshRows} 行；有效制动中 {ValidBrakingRawRows} 行；名义第 7–9 区 {HighNominalZoneRows} 行。");
        text.AppendLine("- 基础阻力档位与脉冲振幅已分别保存在 CSV；两种模式的数字不直接换算为力差，本摘要不根据数字大小判断实际轻重。");
        text.AppendLine($"- 已记录的 HID 写入：成功 {SuccessfulWrites} 次、失败 {FailedWrites} 次；无设备／停止 {NoDeviceRows} 行，dry-run {DryRunRows} 行。常规记录存在节流，成功次数不是全部底层写入的计数。");
        text.AppendLine($"- 队列丢弃 {DroppedRows} 行（关键事件 {CriticalDroppedRows}）；超过 100 ms 的记录间隔 {GapsOver100Ms} 处；无效／倒退时间戳 {InvalidTimestampRows} 行；陈旧遥测 {StaleTelemetryRows} 行。");
        text.AppendLine();
        if (AbsSoftwareEnabledRows == 0)
            text.AppendLine("软件 ABS 提示全程关闭。即使游戏报告 ABS 介入，也不能把本次基础阻力的轻感归因于软件 ABS 脉冲替换。");
        else if (RequestedAbs15HzRows == 0)
            text.AppendLine("开启过软件 ABS 提示，但有效制动内未采到 ABS 15 Hz 命令；本次不足以验证 ABS 替换过程。");
        else
            text.AppendLine("已记录软件 ABS 15 Hz 命令。该模式在协议上替代十区基础阻力，没有与基础阻力同时叠加；本报告确认的是这条软件路径。");
        if (ValidBrakingRows < 10) text.AppendLine("有效制动样本不足，请正常行驶并完成几次刹车后再结束采集。");
        if (ValidBrakingRawRows == 0) text.AppendLine("未获得有效制动时的新鲜、同设备物理 L2 原始值；不能判断实际按压是否进入曲线后段。");
        else if (HighNominalZoneRows == 0) text.AppendLine("有效制动中没有进入名义后三区；无法评估后段指令在常用行程的覆盖。");
        if (DroppedRows > 0 || GapsOver100Ms > 0 || InvalidTimestampRows > 0)
            text.AppendLine("记录存在丢弃或间断，持续时间只累计相邻可靠区间，不能声称完整捕获了每次介入。");
        if (WriterFailure is not null) text.AppendLine($"存储或退出错误：{WriterFailure}");
        text.AppendLine(HasSufficientModeEvidence
            ? "本次具备检查 ABS 模式替换和名义位置的基础证据；是否真实变轻仍未被测量。"
            : "本次证据不足以完成 ABS 模式替换与位置覆盖的联合定位；请先核对上述不足。");
        text.AppendLine().AppendLine("原始 0–255 仅按归一化位置划为十个名义区间，未标定为机械角，也未证明与执行器十区物理位置精确一致。持续时间按相邻采样左端命令累计；单段大于 100 ms 的间隔不外推，最后一行也不外推。");
        text.AppendLine().AppendLine("CSV write_state：0=ChangedSuccess，1=HeartbeatSuccess，2=Failed，3=Skipped，4=DryRun，5=NoDevice，6=Stopped。原始值无效时 nominal_zone=-1。JSON 中的 Requested 指请求命令，SuccessfulCommand 指有成功写入依据的命令区间。");
        return text.ToString();
    }
}
