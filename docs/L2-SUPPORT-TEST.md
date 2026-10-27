# L2 support test build

Version: `1.2.0-preview.4-griploss.3-l2.2`, 2026-10-25.

Based on the frozen `griploss.3` feedback, retaining optional brake recording from `brakeprobe.1`. The old builds, rollback archives and user settings are preserved.

At L2 resistance 50, the ten nominal zones change from `[3,4,4,5,5,6,6,7,7,8]` to `[3,4,4,5,6,6,7,7,8,8]`. The early zones are retained. Input mapping is unchanged, and release follows the same position curve.

ABS retains its detection, envelope, start zone and hardware-driven 15 Hz cadence. When base resistance is enabled, pulse amplitudes increase toward the later zones. At resistance 50 / ABS 50 with a fully developed event, amplitudes change from `[0,0,5,5,5,5,5,5,5,5]` to `[0,0,5,6,6,6,7,7,8,8]`.

This is a position-dependent vibration profile, **not independent force plus vibration**. Vibration amplitude and resistance codes do not represent equal physical forces. If it only increases vibration without supporting better brake control, this candidate should be rejected.

ABS-only with base resistance zero retains the old command exactly. ABS zero retains the new resting curve. R2, TC, wheelspin, shift effects, active 8 Hz lockup, grip effects, rear drag, kerb localization, telemetry and input forwarding are preserved. The existing strength mapping and its high-setting saturation remain; L2 100 produces `[4,5,5,7,8,8,8,8,8,8]`.

Test in a newly extracted folder, with other versions closed:

1. Custom mode: L2 resistance 50, ABS 0, all other feedback controls zero. Keep your normal steering/gyro configuration. Try light braking, holding a partial input, heavy braking and gradual release.
2. Change only software ABS to 50 and enable ABS in the game. Check whether support still disappears when ABS starts or ends, and whether holding/releasing the brake remains controllable. ABS retains its existing grip assistance.
3. Restore your usual effects and confirm they remain familiar.

No need to identify tiny force differences or repeat the earlier capture. If a capture is useful, close the normal app, run `Start Brake Capture.cmd`, then finish with Ctrl+C. It records software commands and position, not actual force.

Rollback: run the original `griploss.3`. Its independent archive is `archive/stable-grip-v1.2.0-preview.4-griploss.3-20261025/`. Offline evidence is in `artifacts/l2-support-preview4-20261025/`; it does not establish better physical feel or lap times.
