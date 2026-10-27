using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Telemetry;

// Runs the production engine offline: no runtime, device discovery or HID writes.
// The archive engine is a source-frozen tcgrip.4 reference, sharing only unchanged
// trigger/TC helpers. CSV snapshots are not a reconstruction of every HID report.
internal static class GripLossReplay
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly (string Key, string File)[] Cases =
    [
        ("normal", "正常参照.csv"),
        ("mixed", "后段第一次过弯时候推头第二次出弯甩尾并恢复.csv"),
        ("rear", "出弯甩尾并恢复.csv"),
        ("tc", "tc开启时过弯触发tc.csv")
    ];
    private static readonly FeedbackTuning OnlyGrip = FeedbackTuning.Neutral with
    {
        BrakeResistanceScale = 0, ThrottleResistanceScale = 0,
        AbsPulseScale = 0, LockPulseScale = 0, ShiftKickScale = 0,
        RedlinePulseScale = 0, TcPulseScale = 0, WheelspinPulseScale = 0,
        RoadSurfaceScale = 0, NativeAccScale = 0, EngineTextureScale = 0,
        CollisionFeedbackScale = 0
    };

    public static void Run(string sourceFolder, string outputFolder)
    {
        sourceFolder = Path.GetFullPath(sourceFolder); outputFolder = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(outputFolder);
        var failures = new List<string>();
        var reports = new List<object>();
        foreach (var (key, file) in Cases)
        {
            string source = Path.Combine(sourceFolder, file);
            string initialHash = Hash(source);
            using var input = new StreamReader(source, Encoding.UTF8);
            string[] header = input.ReadLine()!.Split(',');
            var index = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
            var read = CreateSnapshotReader(header);
            var current = new CompetitionFeedbackEngine(); var frozen = new FrozenTcGrip4CompetitionFeedbackEngine();
            var frozenGrip3 = new FrozenGrip3CompetitionFeedbackEngine();
            var zeroCurrent = new CompetitionFeedbackEngine(); var zeroFrozen = new FrozenTcGrip4CompetitionFeedbackEngine();
            var gripCurrent = new CompetitionFeedbackEngine(); var gripFrozen = new FrozenTcGrip4CompetitionFeedbackEngine();
            FeedbackTuning zeroGrip = FeedbackTuning.Neutral with { GripLossScale = 0 };
            long rows = 0, triggerDifferences = 0, zeroDifferences = 0, weakened = 0, addedRows = 0;
            long frozenGrip3FrameDifferences = 0, frozenGrip3RearCueDifferences = 0;
            long normalCueRows = 0, frontOnlyRows = 0, frontOnlyDifferences = 0, rearEventRows = 0, recoveryRows = 0;
            long pureGripTriggerDifferences = 0, finiteFailures = 0;
            long rearAfterSpinRows = 0, rearAfterSpinWeakRows = 0;
            long noDragRows = 0, noDragFullFrameDifferences = 0, isolatedAdditiveDifferences = 0;
            long referenceRows = 0, referenceStartedAt = 0;
            double referenceLastRaw = double.NaN, highConfidenceSeconds = 0;
            double newVoiceLeftSum = 0, newVoiceRightSum = 0, referenceLeftSum = 0, referenceRightSum = 0;
            float referenceStrength = 0, referenceMaxError = 0;
            float newVoiceLeftPeak = 0, newVoiceRightPeak = 0, referenceLeftPeak = 0, referenceRightPeak = 0;
            bool referenceWasActive = false;
            double isolatedNewActuatorSum = 0, isolatedOldActuatorSum = 0;
            byte isolatedNewPeak = 0, isolatedOldPeak = 0;
            double first = double.NaN, last = 0, firstAdded = double.NaN, lastAdded = double.NaN;
            float rearPeak = 0;
            using (var output = new StreamWriter(Path.Combine(outputFolder, key + "-replay.csv"), false, new UTF8Encoding(false)))
            {
                output.WriteLine("elapsed_s,raw_time_s,packet,speed_kmh,gas,brake,tc,front_angle_mean,rear_angle_mean,body_slip_proxy_deg,rear_strength,rear_active,rear_recovery,isolated_new_left,isolated_new_right,isolated_old_left,isolated_old_right,new_left,new_right,old_left,old_right,r2_mode,r2_frequency,l2_equal,r2_equal,grip_zero_frame_equal,no_drag_frame_equal,isolated_additive_equal");
                string? line;
                while ((line = input.ReadLine()) is not null)
                {
                    var cells = line.Split(',');
                    if (cells.Length != header.Length) throw new InvalidDataException($"{file}: bad row {rows + 2}");
                    double raw = double.Parse(cells[index["time_s"]], Inv);
                    if (double.IsNaN(first)) first = raw;
                    double elapsed = raw - first; last = raw;
                    long now = (long)((raw + 1) * Stopwatch.Frequency);
                    var sample = read(cells); sample.ObservedTimestamp = now;
                    byte large = byte.Parse(cells[index["native_large"]], Inv);
                    byte small = byte.Parse(cells[index["native_small"]], Inv);
                    var a = current.Compute(sample, now, large, small, FeedbackTuning.Neutral);
                    var exactGrip3 = frozenGrip3.Compute(sample, now, large, small, FeedbackTuning.Neutral);
                    if (a != exactGrip3) frozenGrip3FrameDifferences++;
                    if (current.RearDragCue != frozenGrip3.RearDragCue) frozenGrip3RearCueDifferences++;
                    var b = frozen.Compute(sample, now, large, small, FeedbackTuning.Neutral);
                    var za = zeroCurrent.Compute(sample, now, large, small, zeroGrip);
                    var zb = zeroFrozen.Compute(sample, now, large, small, zeroGrip);
                    var ga = gripCurrent.Compute(sample, now, 0, 0, OnlyGrip);
                    var gb = gripFrozen.Compute(sample, now, 0, 0, OnlyGrip);
                    var cue = current.RearDragCue;
                    // Historical .1 strength/renderer comparison is conditional
                    // on this test's event entry, not its old broad frontend.
                    // Only sustained high-confidence rows are reported below.
                    float referenceDt = double.IsNaN(referenceLastRaw) ? .004f
                        : Math.Clamp((now - (long)((referenceLastRaw + 1) * Stopwatch.Frequency)) / (float)Stopwatch.Frequency, .001f, .050f);
                    referenceLastRaw = raw;
                    bool highConfidence = HighConfidenceRear(sample);
                    highConfidenceSeconds = highConfidence ? highConfidenceSeconds + referenceDt : 0;
                    if (cue.Active)
                    {
                        if (!referenceWasActive) { referenceStartedAt = now; referenceStrength = 0; }
                        float target = Griploss1RearTarget(sample);
                        referenceStrength += (target - referenceStrength)
                            * (1f - MathF.Exp(-referenceDt / (target > referenceStrength ? .012f : .100f)));
                        if (highConfidenceSeconds >= .200)
                        {
                            var historical = Griploss1RearVoice(referenceStrength,
                                MathF.Max(0, (now - referenceStartedAt) / (float)Stopwatch.Frequency));
                            referenceRows++;
                            newVoiceLeftSum += cue.Left; newVoiceRightSum += cue.Right;
                            referenceLeftSum += historical.Left; referenceRightSum += historical.Right;
                            newVoiceLeftPeak = MathF.Max(newVoiceLeftPeak, cue.Left);
                            newVoiceRightPeak = MathF.Max(newVoiceRightPeak, cue.Right);
                            referenceLeftPeak = MathF.Max(referenceLeftPeak, historical.Left);
                            referenceRightPeak = MathF.Max(referenceRightPeak, historical.Right);
                            referenceMaxError = MathF.Max(referenceMaxError,
                                MathF.Max(MathF.Abs(cue.Left - historical.Left), MathF.Abs(cue.Right - historical.Right)));
                        }
                    }
                    else referenceStrength = 0;
                    referenceWasActive = cue.Active;
                    isolatedNewActuatorSum += ga.LeftActuator + ga.RightActuator;
                    isolatedOldActuatorSum += gb.LeftActuator + gb.RightActuator;
                    isolatedNewPeak = Math.Max(isolatedNewPeak, Math.Max(ga.LeftActuator, ga.RightActuator));
                    isolatedOldPeak = Math.Max(isolatedOldPeak, Math.Max(gb.LeftActuator, gb.RightActuator));
                    var isolatedCue = gripCurrent.RearDragCue;
                    bool additive = ga.LeftActuator == ToByte(Combine(gripFrozen.LastLeftOutput, isolatedCue.Left))
                        && ga.RightActuator == ToByte(Combine(gripFrozen.LastRightOutput, isolatedCue.Right));
                    if (!additive) isolatedAdditiveDifferences++;
                    bool l2 = a.LeftTrigger == b.LeftTrigger, r2 = a.RightTrigger == b.RightTrigger, z = za == zb;
                    if (!l2 || !r2) triggerDifferences++;
                    if (!z) zeroDifferences++;
                    if (ga.LeftTrigger != gb.LeftTrigger || ga.RightTrigger != gb.RightTrigger) pureGripTriggerDifferences++;
                    if (a.LeftActuator < b.LeftActuator || a.RightActuator < b.RightActuator) weakened++;
                    bool active = cue.Strength > 0 || cue.Left > 0 || cue.Right > 0;
                    if (active)
                    {
                        addedRows++; if (double.IsNaN(firstAdded)) firstAdded = elapsed; lastAdded = elapsed;
                        if (key == "normal") normalCueRows++;
                    }
                    if (!active)
                    {
                        noDragRows++;
                        if (a != b) noDragFullFrameDifferences++;
                    }
                    if (key == "mixed" && elapsed is >= 53.26 and <= 54.985)
                    {
                        frontOnlyRows++;
                        if (a != b || active) frontOnlyDifferences++;
                    }
                    if ((key == "mixed" && elapsed is >= 61.14 and <= 62.60 || key == "rear" && elapsed is >= 57.67 and <= 58.75)
                        && cue.Strength > .1f) rearEventRows++;
                    if (cue.Recovery) recoveryRows++;
                    // The annotated mixed event's stable R2 7 Hz command ends at
                    // elapsed 61.856011. Body/rear lateral slide remains after it.
                    if (key == "mixed" && elapsed is >= 61.956011 and <= 62.056011)
                    {
                        rearAfterSpinRows++;
                        if (cue.Strength <= .1f) rearAfterSpinWeakRows++;
                    }
                    rearPeak = MathF.Max(rearPeak, cue.Strength);
                    if (!float.IsFinite(cue.Strength)
                        || !float.IsFinite(cue.Left) || !float.IsFinite(cue.Right)) finiteFailures++;
                    float fa = (MathF.Abs(sample.SlipAngleFl) + MathF.Abs(sample.SlipAngleFr)) * .5f;
                    float ra = (MathF.Abs(sample.SlipAngleRl) + MathF.Abs(sample.SlipAngleRr)) * .5f;
                    double beta = Math.Atan2(sample.LocalVelocityX, Math.Max(Math.Abs(sample.LocalVelocityZ), 1)) * 180 / Math.PI;
                    output.WriteLine(string.Join(',',
                        elapsed.ToString("F6", Inv), raw.ToString("F6", Inv), sample.PacketId,
                        sample.SpeedKmh.ToString("F4", Inv), sample.Gas.ToString("F5", Inv), sample.Brake.ToString("F5", Inv),
                        sample.TcIntervention.ToString("F4", Inv), fa.ToString("F6", Inv), ra.ToString("F6", Inv),
                        beta.ToString("F4", Inv), cue.Strength.ToString("F6", Inv), cue.Active ? 1 : 0, cue.Recovery ? 1 : 0,
                        ga.LeftActuator, ga.RightActuator, gb.LeftActuator, gb.RightActuator,
                        a.LeftActuator, a.RightActuator, b.LeftActuator, b.RightActuator,
                        a.RightTrigger.Mode, a.RightTrigger.P8, l2 ? 1 : 0, r2 ? 1 : 0, z ? 1 : 0,
                        !active && a == b ? 1 : 0, additive ? 1 : 0));
                    rows++;
                }
            }
            string finalHash = Hash(source);
            if (triggerDifferences != 0) failures.Add($"{key}: {triggerDifferences} complete-trigger mismatches");
            if (frozenGrip3FrameDifferences != 0 || frozenGrip3RearCueDifferences != 0)
                failures.Add($"{key}: frozen .3 feedback/cue changed: {frozenGrip3FrameDifferences}/{frozenGrip3RearCueDifferences}");
            if (zeroDifferences != 0) failures.Add($"{key}: {zeroDifferences} GripLoss=0 full-frame mismatches");
            if (weakened != 0) failures.Add($"{key}: {weakened} rows reduced an existing actuator");
            if (noDragFullFrameDifferences != 0) failures.Add($"{key}: {noDragFullFrameDifferences} no-drag rows changed full baseline frames");
            if (isolatedAdditiveDifferences != 0) failures.Add($"{key}: {isolatedAdditiveDifferences} isolated rows failed exact baseline-plus-drag mixing");
            if (pureGripTriggerDifferences != 0) failures.Add($"{key}: isolated grip changed triggers");
            if (normalCueRows != 0) failures.Add($"{key}: {normalCueRows} normal-reference false cue rows");
            if (finiteFailures != 0) failures.Add($"{key}: nonfinite cue outputs");
            if (key == "mixed" && (frontOnlyRows == 0 || frontOnlyDifferences != 0 || rearEventRows == 0))
                failures.Add("mixed: front-only baseline preservation or rear-event detection failed");
            if (key == "mixed" && (rearAfterSpinRows < 10 || rearAfterSpinWeakRows != 0))
                failures.Add("mixed: rear grip cue failed to persist 100-200 ms after R2 spin ended");
            if (key == "rear" && rearEventRows == 0) failures.Add("rear: expected rear event not identified");
            if (initialHash != finalHash) failures.Add($"{key}: original CSV was modified");
            reports.Add(new
            {
                key, source, sha256Before = initialHash, sha256After = finalHash, rows,
                rawFirstTimeSeconds = first, rawLastTimeSeconds = last, durationSeconds = last - first,
                triggerDifferences, zeroGripFullFrameDifferences = zeroDifferences,
                frozenGrip3FrameDifferences, frozenGrip3RearCueDifferences,
                weakenedGripRows = weakened, pureGripTriggerDifferences, finiteFailures,
                addedCueRows = addedRows, normalCueRows, frontOnlyRows, frontOnlyDifferences, rearEventRows, recoveryRows,
                rearAfterSpinRows, rearAfterSpinWeakRows,
                noDragRows, noDragFullFrameDifferences, isolatedAdditiveDifferences,
                isolatedNewMeanActuator = isolatedNewActuatorSum / (rows * 2),
                isolatedOldMeanActuator = isolatedOldActuatorSum / (rows * 2),
                isolatedNewPeak, isolatedOldPeak,
                firstAddedSeconds = double.IsNaN(firstAdded) ? (double?)null : firstAdded,
                lastAddedSeconds = double.IsNaN(lastAdded) ? (double?)null : lastAdded,
                rearPeak,
                griploss1SustainedReference = new
                {
                    comparedRows = referenceRows,
                    selection = "This event's gate active; rear qualification remains high for at least 200 ms. Same entry phase; independent .1 rear target, envelope and voice; excludes old frontend and final mixed feeling.",
                    newMeanLeft = referenceRows == 0 ? 0 : newVoiceLeftSum / referenceRows,
                    newMeanRight = referenceRows == 0 ? 0 : newVoiceRightSum / referenceRows,
                    originalFormulaMeanLeft = referenceRows == 0 ? 0 : referenceLeftSum / referenceRows,
                    originalFormulaMeanRight = referenceRows == 0 ? 0 : referenceRightSum / referenceRows,
                    newVoiceLeftPeak, newVoiceRightPeak, referenceLeftPeak, referenceRightPeak,
                    maxAbsoluteNormalizedError = referenceMaxError
                }
            });
            Console.WriteLine($"REPLAY {key}: {rows} rows; triggers {triggerDifferences}, GripLoss0 {zeroDifferences}, no-drag diff {noDragFullFrameDifferences}, additive diff {isolatedAdditiveDifferences}, rear cue {addedRows}, normal false {normalCueRows}");
        }
        File.WriteAllText(Path.Combine(outputFolder, "replay-summary.json"), JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTime.UtcNow, purpose = "Offline rear-only drag replay against frozen tcgrip.4 source; no hardware access",
            timeBasis = "elapsed_s is time_s minus each source first row; replay clock is original time_s plus one second",
            limits = "Snapshot replays cannot establish perceived strength, actual HID waveform, latency or universal car thresholds.",
            cases = reports, failures
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static float Combine(float baseline, float cue)
        => 1f - (1f - Math.Clamp(baseline, 0f, 1f)) * (1f - Math.Clamp(cue, 0f, 1f));
    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    private static float Step(float low, float high, float value)
    {
        float x = Math.Clamp((value - low) / (high - low), 0, 1);
        return x * x * (3 - 2 * x);
    }
    private static bool HighConfidenceRear(in TelemetrySnapshot t)
    {
        float rl = MathF.Abs(t.SlipAngleRl), rr = MathF.Abs(t.SlipAngleRr);
        float front = (MathF.Abs(t.SlipAngleFl) + MathF.Abs(t.SlipAngleFr)) * .5f;
        float rear = (rl + rr) * .5f;
        float body = MathF.Abs(t.LocalVelocityX) / MathF.Max(t.LocalVelocityZ, 4);
        float turn = MathF.Max(Step(.035f, .14f, MathF.Abs(t.SteerAngle)),
            MathF.Max(Step(.06f, .28f, MathF.Abs(t.LocalYawRate)), Step(.025f, .08f, body)));
        float qualification = Step(.12f, .29f, rear) * turn * Step(14, 35, MathF.Abs(t.SpeedKmh))
            * MathF.Max(Step(.025f, .075f, rear - front), MathF.Max(Step(.070f, .17f, body), Step(.20f, .34f, rear)));
        return qualification > .055f && (MathF.Min(rl, rr) >= .075f || (body >= .075f && rear >= .14f));
    }
    // Pure reference formulas from the preserved .1 rear renderer. No .1 front
    // layer, old event gates or final mixed output is used for this comparison.
    private static float Griploss1RearTarget(in TelemetrySnapshot t)
    {
        float front = (MathF.Abs(t.SlipAngleFl) + MathF.Abs(t.SlipAngleFr)) * .5f;
        float rear = (MathF.Abs(t.SlipAngleRl) + MathF.Abs(t.SlipAngleRr)) * .5f;
        float body = MathF.Abs(t.LocalVelocityX) / MathF.Max(t.LocalVelocityZ, 4);
        float speed = Step(14, 35, MathF.Abs(t.SpeedKmh));
        float turn = MathF.Max(Step(.035f, .14f, MathF.Abs(t.SteerAngle)),
            MathF.Max(Step(.06f, .28f, MathF.Abs(t.LocalYawRate)), Step(.025f, .08f, body)));
        float rearCandidate = Step(.10f, .27f, rear)
            * MathF.Max(Step(.018f, .065f, rear - front), MathF.Max(Step(.045f, .12f, body), Step(.18f, .32f, rear)))
            * turn * speed;
        float recovery = Step(.055f, .17f, rear) * Step(.025f, .075f, body) * .68f * speed;
        return MathF.Max(rearCandidate, recovery);
    }
    private static (float Left, float Right) Griploss1RearVoice(float strength, float age)
    {
        float grain36 = .5f + .5f * MathF.Cos(age * 36 * MathF.Tau);
        float grain45 = .5f + .5f * MathF.Cos(age * 4.5f * MathF.Tau);
        float rear = strength * (.64f + .36f * grain36) * (.88f + .12f * grain45);
        return (1 - (1 - rear * .52f), 1 - (1 - rear * .31f));
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static Func<string[], TelemetrySnapshot> CreateSnapshotReader(string[] header)
    {
        static string Canonical(string text) => text.Replace("_", "").ToLowerInvariant();
        var byName = header.Select((name, i) => (Name: Canonical(name), i)).ToDictionary(x => x.Name, x => x.i);
        var aliases = new Dictionary<string, string>
        {
            ["PacketId"] = "packet", ["CurrentMaxRpm"] = "max_rpm", ["IsEngineRunning"] = "engine_running",
            ["TcIntervention"] = "tc_physics_intervention", ["NumberOfTyresOut"] = "tyres_out", ["KerbVibration"] = "kerb",
            ["LocalYawRate"] = "yaw_rate"
        };
        foreach (string axis in new[] { "X", "Y", "Z" })
        {
            aliases["GForce" + axis] = "g_" + axis;
            aliases["LocalVelocity" + axis] = "local_v_" + axis;
            aliases["LocalAngularVelocity" + axis] = "local_av_" + axis;
        }
        foreach (string corner in new[] { "Fl", "Fr", "Rl", "Rr" })
        {
            aliases["WheelLoad" + corner] = "load_" + corner;
            aliases["WheelAngularSpeed" + corner] = "wheel_angular_" + corner;
            aliases["TyreDirty" + corner] = "dirty_" + corner;
            aliases["Suspension" + corner] = "susp_" + corner;
            aliases["SuspensionDamage" + corner] = "susp_damage_" + corner;
        }
        foreach (string side in new[] { "Front", "Rear", "Left", "Right", "Center" })
            aliases["CarDamage" + side] = "damage_" + side;
        var cells = Expression.Parameter(typeof(string[]), "cells");
        var bindings = new List<MemberBinding>();
        foreach (var field in typeof(TelemetrySnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.Name == "ObservedTimestamp") continue;
            string sourceName = aliases.GetValueOrDefault(field.Name, field.Name);
            if (!byName.TryGetValue(Canonical(sourceName), out int column))
                throw new InvalidDataException($"CSV lacks a mapping for TelemetrySnapshot.{field.Name}");
            var parser = typeof(GripLossReplay).GetMethod(field.FieldType == typeof(int) ? nameof(ParseInt) : nameof(ParseFloat),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            bindings.Add(Expression.Bind(field, Expression.Call(parser, Expression.ArrayIndex(cells, Expression.Constant(column)))));
        }
        return Expression.Lambda<Func<string[], TelemetrySnapshot>>(
            Expression.MemberInit(Expression.New(typeof(TelemetrySnapshot)), bindings), cells).Compile();
    }
    private static int ParseInt(string value) => int.Parse(value, Inv);
    private static float ParseFloat(string value) => float.Parse(value, Inv);
}
