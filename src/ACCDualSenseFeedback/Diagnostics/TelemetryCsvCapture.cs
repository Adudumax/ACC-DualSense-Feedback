using System.Diagnostics;
using System.Globalization;
using System.Text;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Diagnostics;

// Diagnostic-only capture. Normal competition mode never constructs this class
// and therefore performs no file I/O on either real-time thread.
internal sealed class TelemetryCsvCapture : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private long _lastWrittenAt;
    private int _rowsSinceFlush;

    public TelemetryCsvCapture(string path)
    {
        _writer = new StreamWriter(path, false, new UTF8Encoding(false), 128 * 1024);
        _writer.WriteLine(
            "time_s,sample_dt_ms,packet,speed_kmh,rpm,max_rpm,engine_running,gas,brake,gear,steer_angle,pitch,roll," +
            "tc,abs,tc_level,abs_level,g_x,g_y,g_z,local_v_x,local_v_y,local_v_z," +
            "local_av_x,local_av_y,local_av_z,yaw_rate,final_ff," +
            "damage_front,damage_rear,damage_left,damage_right,damage_center," +
            "susp_damage_fl,susp_damage_fr,susp_damage_rl,susp_damage_rr," +
            "kerb,slip_vibration,g_vibration,abs_vibration,tyres_out," +
            "load_fl,load_fr,load_rl,load_rr,fx_fl,fx_fr,fx_rl,fx_rr," +
            "mz_fl,mz_fr,mz_rl,mz_rr,fy_fl,fy_fr,fy_rl,fy_rr," +
            "wheel_angular_fl,wheel_angular_fr,wheel_angular_rl,wheel_angular_rr," +
            "brake_pressure_fl,brake_pressure_fr,brake_pressure_rl,brake_pressure_rr," +
            "susp_fl,susp_fr,susp_rl,susp_rr,ride_height_front,ride_height_rear," +
            "contact_normal_fl_x,contact_normal_fl_y,contact_normal_fl_z," +
            "contact_normal_fr_x,contact_normal_fr_y,contact_normal_fr_z," +
            "contact_normal_rl_x,contact_normal_rl_y,contact_normal_rl_z," +
            "contact_normal_rr_x,contact_normal_rr_y,contact_normal_rr_z," +
            "dirty_fl,dirty_fr,dirty_rl,dirty_rr,wheel_slip_fl,wheel_slip_fr,wheel_slip_rl,wheel_slip_rr," +
            "slip_ratio_fl,slip_ratio_fr,slip_ratio_rl,slip_ratio_rr,slip_angle_fl,slip_angle_fr,slip_angle_rl,slip_angle_rr," +
            "native_large,native_small,output_left,output_right," +
            "left_trigger_mode,left_trigger_p0,left_trigger_p1,left_trigger_p2,left_trigger_p3,left_trigger_p4," +
            "left_trigger_p5,left_trigger_p6,left_trigger_p7,left_trigger_p8,left_trigger_p9," +
            "right_trigger_mode,right_trigger_p0,right_trigger_p1,right_trigger_p2,right_trigger_p3,right_trigger_p4," +
            "right_trigger_p5,right_trigger_p6,right_trigger_p7,right_trigger_p8,right_trigger_p9");
    }

    public void TryWrite(
        in TelemetrySnapshot t,
        byte nativeLarge,
        byte nativeSmall,
        in FeedbackFrame output)
    {
        long now = t.ObservedTimestamp;
        if (now <= 0 || now <= _lastWrittenAt)
            return;
        long previousWrittenAt = _lastWrittenAt;
        _lastWrittenAt = now;

        var row = new StringBuilder(1536);
        Add(row, (now - _startedAt) / (double)Stopwatch.Frequency, "F6");
        Add(row, previousWrittenAt == 0
            ? 0d
            : (now - previousWrittenAt) * 1000d / Stopwatch.Frequency, "F3");
        Add(row, t.PacketId); Add(row, t.SpeedKmh, "F4"); Add(row, t.Rpm); Add(row, t.CurrentMaxRpm);
        Add(row, t.IsEngineRunning);
        Add(row, t.Gas, "F5"); Add(row, t.Brake, "F5"); Add(row, t.Gear);
        Add(row, t.SteerAngle, "F6"); Add(row, t.Pitch, "F6"); Add(row, t.Roll, "F6");
        Add(row, t.Tc, "F4"); Add(row, t.Abs, "F4");
        Add(row, t.TcLevel, "F3"); Add(row, t.AbsLevel, "F3");
        Add(row, t.GForceX, "F5"); Add(row, t.GForceY, "F5"); Add(row, t.GForceZ, "F5");
        Add(row, t.LocalVelocityX, "F6"); Add(row, t.LocalVelocityY, "F6"); Add(row, t.LocalVelocityZ, "F6");
        Add(row, t.LocalAngularVelocityX, "F6"); Add(row, t.LocalAngularVelocityY, "F6");
        Add(row, t.LocalAngularVelocityZ, "F6");
        Add(row, t.LocalYawRate, "F6"); Add(row, t.FinalFf, "F6");
        Add(row, t.CarDamageFront, "F6"); Add(row, t.CarDamageRear, "F6");
        Add(row, t.CarDamageLeft, "F6"); Add(row, t.CarDamageRight, "F6");
        Add(row, t.CarDamageCenter, "F6");
        Add(row, t.SuspensionDamageFl, "F6"); Add(row, t.SuspensionDamageFr, "F6");
        Add(row, t.SuspensionDamageRl, "F6"); Add(row, t.SuspensionDamageRr, "F6");
        Add(row, t.KerbVibration, "F6"); Add(row, t.SlipVibration, "F6");
        Add(row, t.GVibration, "F6"); Add(row, t.AbsVibration, "F6"); Add(row, t.NumberOfTyresOut);
        Add(row, t.WheelLoadFl, "F4"); Add(row, t.WheelLoadFr, "F4");
        Add(row, t.WheelLoadRl, "F4"); Add(row, t.WheelLoadRr, "F4");
        Add(row, t.FxFl, "F4"); Add(row, t.FxFr, "F4"); Add(row, t.FxRl, "F4"); Add(row, t.FxRr, "F4");
        Add(row, t.MzFl, "F5"); Add(row, t.MzFr, "F5"); Add(row, t.MzRl, "F5"); Add(row, t.MzRr, "F5");
        Add(row, t.FyFl, "F5"); Add(row, t.FyFr, "F5"); Add(row, t.FyRl, "F5"); Add(row, t.FyRr, "F5");
        Add(row, t.WheelAngularSpeedFl, "F5"); Add(row, t.WheelAngularSpeedFr, "F5");
        Add(row, t.WheelAngularSpeedRl, "F5"); Add(row, t.WheelAngularSpeedRr, "F5");
        Add(row, t.BrakePressureFl, "F5"); Add(row, t.BrakePressureFr, "F5");
        Add(row, t.BrakePressureRl, "F5"); Add(row, t.BrakePressureRr, "F5");
        Add(row, t.SuspensionFl, "F7"); Add(row, t.SuspensionFr, "F7");
        Add(row, t.SuspensionRl, "F7"); Add(row, t.SuspensionRr, "F7");
        Add(row, t.RideHeightFront, "F7"); Add(row, t.RideHeightRear, "F7");
        Add(row, t.ContactNormalFlX, "F6"); Add(row, t.ContactNormalFlY, "F6"); Add(row, t.ContactNormalFlZ, "F6");
        Add(row, t.ContactNormalFrX, "F6"); Add(row, t.ContactNormalFrY, "F6"); Add(row, t.ContactNormalFrZ, "F6");
        Add(row, t.ContactNormalRlX, "F6"); Add(row, t.ContactNormalRlY, "F6"); Add(row, t.ContactNormalRlZ, "F6");
        Add(row, t.ContactNormalRrX, "F6"); Add(row, t.ContactNormalRrY, "F6"); Add(row, t.ContactNormalRrZ, "F6");
        Add(row, t.TyreDirtyFl, "F5"); Add(row, t.TyreDirtyFr, "F5");
        Add(row, t.TyreDirtyRl, "F5"); Add(row, t.TyreDirtyRr, "F5");
        Add(row, t.WheelSlipFl, "F6"); Add(row, t.WheelSlipFr, "F6");
        Add(row, t.WheelSlipRl, "F6"); Add(row, t.WheelSlipRr, "F6");
        Add(row, t.SlipRatioFl, "F6"); Add(row, t.SlipRatioFr, "F6");
        Add(row, t.SlipRatioRl, "F6"); Add(row, t.SlipRatioRr, "F6");
        Add(row, t.SlipAngleFl, "F6"); Add(row, t.SlipAngleFr, "F6");
        Add(row, t.SlipAngleRl, "F6"); Add(row, t.SlipAngleRr, "F6");
        Add(row, nativeLarge); Add(row, nativeSmall);
        Add(row, output.LeftActuator); Add(row, output.RightActuator);
        AddTrigger(row, output.LeftTrigger);
        AddTrigger(row, output.RightTrigger);
        _writer.WriteLine(row);
        if (++_rowsSinceFlush >= 100)
        {
            _writer.Flush();
            _rowsSinceFlush = 0;
        }
    }

    private static void Add(StringBuilder row, IFormattable value, string? format = null)
    {
        if (row.Length != 0)
            row.Append(',');
        row.Append(value.ToString(format, CultureInfo.InvariantCulture));
    }

    private static void AddTrigger(StringBuilder row, in TriggerEffect effect)
    {
        Add(row, effect.Mode);
        Add(row, effect.P0); Add(row, effect.P1); Add(row, effect.P2); Add(row, effect.P3); Add(row, effect.P4);
        Add(row, effect.P5); Add(row, effect.P6); Add(row, effect.P7); Add(row, effect.P8); Add(row, effect.P9);
    }

    public void Dispose() => _writer.Dispose();
}
