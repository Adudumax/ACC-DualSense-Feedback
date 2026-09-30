using System.Diagnostics;
using System.Globalization;
using System.Text;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Diagnostics;

// Deterministic offline playback for captured ACC telemetry. This keeps tuning
// away from the real-time path and makes successive mixer versions comparable
// against exactly the same driving sequence.
internal static class TelemetryReplay
{
    public static int Run(string inputPath, string? outputPath)
    {
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Telemetry capture was not found: {inputPath}");
            return 1;
        }

        outputPath ??= Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? AppContext.BaseDirectory,
            $"{Path.GetFileNameWithoutExtension(inputPath)}-replay.csv");

        try
        {
            using var reader = new StreamReader(inputPath, Encoding.UTF8, true, 128 * 1024);
            string? headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                throw new InvalidDataException("Telemetry capture has no CSV header.");

            string[] header = headerLine.Split(',');
            var columns = new Dictionary<string, int>(header.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++)
                columns[header[i].Trim()] = i;

            Require(columns, "time_s", "packet", "speed_kmh", "native_large", "native_small");

            using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false), 128 * 1024);
            writer.WriteLine(
                "time_s,packet,native_large,native_small,captured_left,captured_right," +
                "replay_left,replay_right,left_delta,right_delta," +
                "without_collision_left,without_collision_right,collision_left,collision_right," +
                "left_trigger_mode,right_trigger_mode");

            var engine = new CompetitionFeedbackEngine();
            var engineWithoutCollision = new CompetitionFeedbackEngine();
            FeedbackTuning withoutCollision = FeedbackTuning.Neutral with { CollisionFeedbackScale = 0f };
            long baseTimestamp = Stopwatch.Frequency;
            long rows = 0;
            long leftAbsoluteDelta = 0;
            long rightAbsoluteDelta = 0;
            int maximumLeftDelta = 0;
            int maximumRightDelta = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                string[] values = line.Split(',');
                double time = Double(values, columns, "time_s");
                long now = baseTimestamp + (long)Math.Round(time * Stopwatch.Frequency);
                TelemetrySnapshot telemetry = ReadTelemetry(values, columns, now);
                byte nativeLarge = Byte(values, columns, "native_large");
                byte nativeSmall = Byte(values, columns, "native_small");
                byte capturedLeft = Byte(values, columns, "output_left");
                byte capturedRight = Byte(values, columns, "output_right");
                FeedbackFrame replayed = engine.Compute(telemetry, now, nativeLarge, nativeSmall);
                FeedbackFrame replayedWithoutCollision = engineWithoutCollision.Compute(
                    telemetry,
                    now,
                    nativeLarge,
                    nativeSmall,
                    withoutCollision);

                int leftDelta = replayed.LeftActuator - capturedLeft;
                int rightDelta = replayed.RightActuator - capturedRight;
                leftAbsoluteDelta += Math.Abs(leftDelta);
                rightAbsoluteDelta += Math.Abs(rightDelta);
                maximumLeftDelta = Math.Max(maximumLeftDelta, Math.Abs(leftDelta));
                maximumRightDelta = Math.Max(maximumRightDelta, Math.Abs(rightDelta));

                writer.Write(time.ToString("F6", CultureInfo.InvariantCulture));
                Write(writer, telemetry.PacketId);
                Write(writer, nativeLarge); Write(writer, nativeSmall);
                Write(writer, capturedLeft); Write(writer, capturedRight);
                Write(writer, replayed.LeftActuator); Write(writer, replayed.RightActuator);
                Write(writer, leftDelta); Write(writer, rightDelta);
                Write(writer, replayedWithoutCollision.LeftActuator);
                Write(writer, replayedWithoutCollision.RightActuator);
                Write(writer, replayed.LeftActuator - replayedWithoutCollision.LeftActuator);
                Write(writer, replayed.RightActuator - replayedWithoutCollision.RightActuator);
                Write(writer, replayed.LeftTrigger.Mode); Write(writer, replayed.RightTrigger.Mode);
                writer.WriteLine();
                rows++;
            }

            if (rows == 0)
                throw new InvalidDataException("Telemetry capture contains no data rows.");

            Console.WriteLine($"Replayed {rows:N0} telemetry rows.");
            Console.WriteLine(
                $"Mean |delta| versus captured build: left={leftAbsoluteDelta / (double)rows:F2}, " +
                $"right={rightAbsoluteDelta / (double)rows:F2}; max={maximumLeftDelta}/{maximumRightDelta}.");
            Console.WriteLine($"Replay output: {outputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Telemetry replay failed: {exception.Message}");
            return 1;
        }
    }

    private static TelemetrySnapshot ReadTelemetry(
        string[] values,
        IReadOnlyDictionary<string, int> columns,
        long observedTimestamp)
        => new()
        {
            PacketId = Int(values, columns, "packet"),
            ObservedTimestamp = observedTimestamp,
            SpeedKmh = Float(values, columns, "speed_kmh"),
            Rpm = Int(values, columns, "rpm"),
            CurrentMaxRpm = Int(values, columns, "max_rpm"),
            IsEngineRunning = Int(values, columns, "engine_running"),
            Gas = Float(values, columns, "gas"),
            Brake = Float(values, columns, "brake"),
            Gear = Int(values, columns, "gear"),
            SteerAngle = Float(values, columns, "steer_angle"),
            Pitch = Float(values, columns, "pitch"),
            Roll = Float(values, columns, "roll"),
            Tc = Float(values, columns, "tc"),
            Abs = Float(values, columns, "abs"),
            TcLevel = Float(values, columns, "tc_level"),
            AbsLevel = Float(values, columns, "abs_level"),
            GForceX = Float(values, columns, "g_x"),
            GForceY = Float(values, columns, "g_y"),
            GForceZ = Float(values, columns, "g_z"),
            LocalVelocityX = Float(values, columns, "local_v_x"),
            LocalVelocityY = Float(values, columns, "local_v_y"),
            LocalVelocityZ = Float(values, columns, "local_v_z"),
            LocalAngularVelocityX = Float(values, columns, "local_av_x"),
            LocalAngularVelocityY = Float(values, columns, "local_av_y"),
            LocalAngularVelocityZ = Float(values, columns, "local_av_z"),
            LocalYawRate = Float(values, columns, "yaw_rate"),
            FinalFf = Float(values, columns, "final_ff"),
            CarDamageFront = Float(values, columns, "damage_front"),
            CarDamageRear = Float(values, columns, "damage_rear"),
            CarDamageLeft = Float(values, columns, "damage_left"),
            CarDamageRight = Float(values, columns, "damage_right"),
            CarDamageCenter = Float(values, columns, "damage_center"),
            SuspensionDamageFl = Float(values, columns, "susp_damage_fl"),
            SuspensionDamageFr = Float(values, columns, "susp_damage_fr"),
            SuspensionDamageRl = Float(values, columns, "susp_damage_rl"),
            SuspensionDamageRr = Float(values, columns, "susp_damage_rr"),
            KerbVibration = Float(values, columns, "kerb"),
            SlipVibration = Float(values, columns, "slip_vibration"),
            GVibration = Float(values, columns, "g_vibration"),
            AbsVibration = Float(values, columns, "abs_vibration"),
            NumberOfTyresOut = Int(values, columns, "tyres_out"),
            WheelLoadFl = Float(values, columns, "load_fl"),
            WheelLoadFr = Float(values, columns, "load_fr"),
            WheelLoadRl = Float(values, columns, "load_rl"),
            WheelLoadRr = Float(values, columns, "load_rr"),
            FxFl = FloatEither(values, columns, "fx_fl", "fz_fl"),
            FxFr = FloatEither(values, columns, "fx_fr", "fz_fr"),
            FxRl = FloatEither(values, columns, "fx_rl", "fz_rl"),
            FxRr = FloatEither(values, columns, "fx_rr", "fz_rr"),
            MzFl = Float(values, columns, "mz_fl"),
            MzFr = Float(values, columns, "mz_fr"),
            MzRl = Float(values, columns, "mz_rl"),
            MzRr = Float(values, columns, "mz_rr"),
            FyFl = FloatEither(values, columns, "fy_fl", "my_fl"),
            FyFr = FloatEither(values, columns, "fy_fr", "my_fr"),
            FyRl = FloatEither(values, columns, "fy_rl", "my_rl"),
            FyRr = FloatEither(values, columns, "fy_rr", "my_rr"),
            WheelAngularSpeedFl = Float(values, columns, "wheel_angular_fl"),
            WheelAngularSpeedFr = Float(values, columns, "wheel_angular_fr"),
            WheelAngularSpeedRl = Float(values, columns, "wheel_angular_rl"),
            WheelAngularSpeedRr = Float(values, columns, "wheel_angular_rr"),
            BrakePressureFl = Float(values, columns, "brake_pressure_fl"),
            BrakePressureFr = Float(values, columns, "brake_pressure_fr"),
            BrakePressureRl = Float(values, columns, "brake_pressure_rl"),
            BrakePressureRr = Float(values, columns, "brake_pressure_rr"),
            SuspensionFl = Float(values, columns, "susp_fl"),
            SuspensionFr = Float(values, columns, "susp_fr"),
            SuspensionRl = Float(values, columns, "susp_rl"),
            SuspensionRr = Float(values, columns, "susp_rr"),
            RideHeightFront = Float(values, columns, "ride_height_front"),
            RideHeightRear = Float(values, columns, "ride_height_rear"),
            ContactNormalFlX = Float(values, columns, "contact_normal_fl_x"),
            ContactNormalFlY = Float(values, columns, "contact_normal_fl_y"),
            ContactNormalFlZ = Float(values, columns, "contact_normal_fl_z"),
            ContactNormalFrX = Float(values, columns, "contact_normal_fr_x"),
            ContactNormalFrY = Float(values, columns, "contact_normal_fr_y"),
            ContactNormalFrZ = Float(values, columns, "contact_normal_fr_z"),
            ContactNormalRlX = Float(values, columns, "contact_normal_rl_x"),
            ContactNormalRlY = Float(values, columns, "contact_normal_rl_y"),
            ContactNormalRlZ = Float(values, columns, "contact_normal_rl_z"),
            ContactNormalRrX = Float(values, columns, "contact_normal_rr_x"),
            ContactNormalRrY = Float(values, columns, "contact_normal_rr_y"),
            ContactNormalRrZ = Float(values, columns, "contact_normal_rr_z"),
            TyreDirtyFl = Float(values, columns, "dirty_fl"),
            TyreDirtyFr = Float(values, columns, "dirty_fr"),
            TyreDirtyRl = Float(values, columns, "dirty_rl"),
            TyreDirtyRr = Float(values, columns, "dirty_rr"),
            WheelSlipFl = Float(values, columns, "wheel_slip_fl"),
            WheelSlipFr = Float(values, columns, "wheel_slip_fr"),
            WheelSlipRl = Float(values, columns, "wheel_slip_rl"),
            WheelSlipRr = Float(values, columns, "wheel_slip_rr"),
            SlipRatioFl = Float(values, columns, "slip_ratio_fl"),
            SlipRatioFr = Float(values, columns, "slip_ratio_fr"),
            SlipRatioRl = Float(values, columns, "slip_ratio_rl"),
            SlipRatioRr = Float(values, columns, "slip_ratio_rr"),
            SlipAngleFl = Float(values, columns, "slip_angle_fl"),
            SlipAngleFr = Float(values, columns, "slip_angle_fr"),
            SlipAngleRl = Float(values, columns, "slip_angle_rl"),
            SlipAngleRr = Float(values, columns, "slip_angle_rr")
        };

    private static void Require(IReadOnlyDictionary<string, int> columns, params string[] names)
    {
        foreach (string name in names)
            if (!columns.ContainsKey(name))
                throw new InvalidDataException($"Telemetry capture is missing required column '{name}'.");
    }

    private static float Float(string[] values, IReadOnlyDictionary<string, int> columns, string name)
        => (float)Double(values, columns, name);

    private static float FloatEither(
        string[] values,
        IReadOnlyDictionary<string, int> columns,
        string currentName,
        string legacyName)
        => columns.ContainsKey(currentName)
            ? Float(values, columns, currentName)
            : Float(values, columns, legacyName);

    private static double Double(string[] values, IReadOnlyDictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out int index) || index >= values.Length ||
            !double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            return 0;
        return result;
    }

    private static int Int(string[] values, IReadOnlyDictionary<string, int> columns, string name)
        => (int)Math.Clamp(Math.Round(Double(values, columns, name)), int.MinValue, int.MaxValue);

    private static byte Byte(string[] values, IReadOnlyDictionary<string, int> columns, string name)
        => (byte)Math.Clamp(Int(values, columns, name), byte.MinValue, byte.MaxValue);

    private static void Write(StreamWriter writer, IFormattable value)
    {
        writer.Write(',');
        writer.Write(value.ToString(null, CultureInfo.InvariantCulture));
    }
}
