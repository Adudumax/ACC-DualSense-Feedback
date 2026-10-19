using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Lighting;

// A lighting-only classifier. ABS level and final haptic intensity are never
// event inputs. Lock is inferred conservatively; ACC exposes no lock boolean.
internal sealed class AccBrakeLightDetector
{
    private readonly double[] _rolling = new double[4];
    private readonly double[] _lockSince = new double[4];
    private int? _packet;
    private double _lastSample;
    private double _until = double.NegativeInfinity;
    public AccBrakeLightDetector() => Reset();
    public double StartedAt { get; private set; }
    public bool IsActive(double time) => time < _until;

    public void Update(in TelemetrySnapshot car, double time)
    {
        // A repeated output report must not relearn a wheel baseline, confirm
        // a one-frame lock, or indefinitely extend an old intervention.
        if (_packet == car.PacketId) return;
        double gap = _packet is null ? 0 : Math.Max(0, time - _lastSample);
        double dt = Math.Min(gap, 0.12);
        if (gap > 0.12) ClearLocks();
        _packet = car.PacketId; _lastSample = time;
        double speed = Math.Abs(car.SpeedKmh);
        if (!float.IsFinite(car.Brake) || !float.IsFinite(car.Gas) || !double.IsFinite(speed))
        {
            ClearLocks();
            return;
        }
        Span<float> wheels = [car.WheelAngularSpeedFl, car.WheelAngularSpeedFr,
            car.WheelAngularSpeedRl, car.WheelAngularSpeedRr];
        Span<float> slip = [car.SlipRatioFl, car.SlipRatioFr, car.SlipRatioRl, car.SlipRatioRr];
        Span<float> loads = [car.WheelLoadFl, car.WheelLoadFr, car.WheelLoadRl, car.WheelLoadRr];
        bool braking = speed >= 12 && car.Brake >= 0.16;
        bool abs = braking && float.IsFinite(car.AbsVibration) && Math.Abs(car.AbsVibration) >= 0.06;

        // Learn effective rolling speed only from quiet, almost straight
        // driving. This avoids guessing a tyre radius (unused in ACC static).
        bool rolling = speed >= 20 && car.Brake < 0.08 && car.Gas < 0.65 &&
            float.IsFinite(car.SteerAngle) && Math.Abs(car.SteerAngle) < 0.15;
        double minimum = double.PositiveInfinity, maximum = 0, maxLoad = 0;
        for (int i = 0; i < 4; i++)
        {
            double wheel = Math.Abs(wheels[i]);
            rolling &= double.IsFinite(wheel) && wheel > 5 &&
                float.IsFinite(slip[i]) && Math.Abs(slip[i]) < 0.08;
            minimum = Math.Min(minimum, wheel); maximum = Math.Max(maximum, wheel);
            if (float.IsFinite(loads[i])) maxLoad = Math.Max(maxLoad, loads[i]);
        }
        rolling &= minimum >= maximum * 0.80;
        bool locked = false;
        for (int i = 0; i < 4; i++)
        {
            if (rolling)
            {
                double sample = Math.Abs(wheels[i]) / speed;
                _rolling[i] = _rolling[i] == 0 ? sample :
                    _rolling[i] + (sample - _rolling[i]) * (1 - Math.Exp(-dt / 2));
            }
            // Slip alone includes sideways/kerb events. Require an observed
            // rolling reference, a substantial wheel-speed deficit, sustained
            // longitudinal slip, and no known severe wheel unloading.
            bool candidate = braking && car.Gas < 0.20 && speed >= 20 && _rolling[i] > 0 &&
                float.IsFinite(wheels[i]) && float.IsFinite(slip[i]) && Math.Abs(slip[i]) >= 0.15 &&
                Math.Abs(wheels[i]) < _rolling[i] * speed * 0.72 &&
                (maxLoad <= 0 || (float.IsFinite(loads[i]) && loads[i] >= maxLoad * 0.15));
            if (!candidate) _lockSince[i] = double.NaN;
            else
            {
                if (double.IsNaN(_lockSince[i])) _lockSince[i] = time;
                if (time - _lockSince[i] >= 0.06) locked = true;
            }
        }
        if (speed < 3) Array.Clear(_rolling);
        if (abs || locked)
        {
            if (!IsActive(time)) StartedAt = time;
            _until = time + 0.18;
        }
    }

    public void Reset()
    {
        Array.Clear(_rolling); ClearLocks(); _packet = null;
        _lastSample = 0; _until = double.NegativeInfinity; StartedAt = 0;
    }
    private void ClearLocks() => Array.Fill(_lockSince, double.NaN);
}
