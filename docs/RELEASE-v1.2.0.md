# ACC DualSense Feedback 1.2.0

Adds USB gyro steering and clearer traction-control, rear grip-loss and braking feedback.

> [!IMPORTANT]
> **Gyro steering requires a USB connection. Please test other connection methods yourself.** Gyro is off in Default. Enable it under Custom → Advanced settings → Gyro steering.
>
> Complete stationary calibration on first use. **Set straight ahead after each app start or controller reconnection.** Your stationary calibration is saved.

## ✨ Added

- USB gyro steering for DualSense / DualSense Edge, with adjustable steering feel, Continuous / Hold / Toggle activation, and physical-stick takeover.
- A pulsing drag sensation in the grips for rear grip loss and recovery.
- More distinct TC pulses on R2, with a brief grip cue when TC intervenes and a separate wheelspin warning.
- More progressive L2 brake resistance, with firmer middle and end-of-travel support that is retained during ABS feedback.

## ✅ Fixed

- Corrected TC intervention and wheelspin signals for more consistent cues alongside other feedback.
- Replaced misleading gyro Resume prompts outside driving with the appropriate waiting or activation guidance.
- Improved controller-input interruption protection to clear virtual controls and stop feedback.

Existing kerb/grass localization, shift reminders and shift impacts are retained. Actual shift impact keeps priority; TC takes priority over other R2 reminders while active. Strong rear-slide feedback may dominate a simultaneous kerb effect. Brake input mapping is unchanged.

Default uses Smooth lighting with gyro off. Custom preserves your personal settings.

## 📦 Downloads

- **English:** `ACCDualSenseFeedback-v1.2.0-win-x64-portable.zip`
- **简体中文:** `ACCDualSenseFeedback-v1.2.0-win-x64-zh-CN-portable.zip`

Both builds are portable and self-contained. Extract the ZIP before running the app, and keep the bundled DLL and licenses folder beside the EXE.

## 🔐 SHA-256

```text
8084fd985f0d5c764512da3a992864e6ce48197275711d0c7a195ea70a0e1220  ACCDualSenseFeedback-v1.2.0-win-x64-portable.zip
c2fb096839cd36328d3a96291fca28b29e5e6e69e10165f94eb9a7440a8dd139  ACCDualSenseFeedback-v1.2.0-win-x64-zh-CN-portable.zip
```

See the [README](https://github.com/Adudumax/ACC-DualSense-Feedback#readme) for setup and troubleshooting.
