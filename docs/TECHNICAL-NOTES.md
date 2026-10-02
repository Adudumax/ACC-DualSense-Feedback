# Technical reference archive

The previous repository README is retained below as an implementation and calibration reference, with current licensing clarifications. It describes the preview12-based 1.0.0 engine; it is not the player-facing setup guide.

For current installation and operation, use the [English README](../README.md) or [中文说明](../README.zh-CN.md). For release limitations and portable data, see [release notes](../RELEASE_NOTES.md). Third-party notices are [here](../THIRD_PARTY_NOTICES.md).

Clarifications for the archived text: the GUI exposes startup recovery and diagnostics; normal session logs stay in memory, while custom profile changes write the portable settings file. Feedback-strength controls are 0-100, but the redline-start threshold is a separate 94.0%-99.0% RPM setting. Historical console commands and capture details below are for development/calibration, not required for normal use.

---

# ACC DualSense Feedback

Low-latency, telemetry-driven DualSense feedback for Assetto Corsa Competizione.

Current release: **1.0.0**, based on the approved preview12 calibration.

## Requirements

- Windows 10 or Windows 11, x64.
- Assetto Corsa Competizione for PC.
- DualSense or DualSense Edge connected by USB.
- ViGEmBus installed.
- Steam Input disabled for ACC.
- DS4Windows closed while this application is running.

Extract the complete portable ZIP to a writable folder before launching the executable. Do not run it from inside the ZIP.

The application is the single owner of the controller path. It forwards USB DualSense input through a virtual Xbox 360 controller, then uses ACC telemetry to synthesize spatial road, four-wheel tyre and adaptive-trigger feedback. ACC's continuous character and selected transient detail are retained as bounded layers, with a deliberately faint telemetry-driven engine texture and a short directional impact cue.

## Intended input/output chain

```text
USB DualSense input -> ACC DualSense Feedback -> virtual Xbox 360 -> ACC
ACC native XInput rumble -> engine + detail layers ┐
ACC shared memory -> four-wheel spatial synthesis -├-> physical DualSense
TC/ABS/gear -> trigger/event synthesis ------------┘
```

## Use

1. Connect the DualSense by USB.
2. Exit DS4Windows completely. Its ViGEmBus driver remains installed and is reused by this app.
3. If HidHide hides the physical controller, add `ACCDualSenseFeedback.exe` to HidHide's Applications list.
4. Disable Steam Input for ACC.
5. Start `ACCDualSenseFeedback.exe` before starting ACC. The home screen starts the feedback path automatically.

Do not run DS4Windows and this application together. That would create two virtual controllers and two competing physical-output writers.
Only one ACC DualSense Feedback instance can run at a time. The application rejects a second normal or diagnostic instance so two HID writers cannot corrupt the feedback.

## Release features

- Exact 800-byte ACC physics shared-memory layout.
- Torn-frame-resistant latest-snapshot reader.
- Direct USB DualSense-to-Xbox 360 input bridge using the installed ViGEmBus driver.
- ACC rumble capture with the same DualSense Accurate-rumble report mode used by the locally installed DS4Windows 4.0.3-beta.7. Calm and unclassified native waveforms pass through without smoothing or attenuation; one-sided conflicts are separated into a slow baseline and re-routed transient detail.
- A single merged physical report, avoiding DS4Windows/trigger-writer races.
- GT-style progressive L2 resistance and a lighter progressive R2 throttle curve.
- Progressive pedal resistance at rest; real events use the official low-amplitude `0x26` trigger-vibration mode with smoothed attack envelopes.
- Stronger fast/fine L2 pulsing when ABS intervenes; slower and heavier pulsing when ABS is off and the tyres lock.
- TC intervention and raw wheelspin use different R2 cadences.
- A stronger progressive redline pulse and a short, fast-attack R2 recoil pulse on upshifts.
- Independent left/right road synthesis using short-window per-wheel shock energy, suspension movement, kerb intensity and per-wheel tyre dirt. Direction is held through whole-chassis kerb oscillation; confirmed one-sided events impose a final 1.5% opposing-grip ceiling and raw spatial crossfeed is limited to 1.8%.
- Four-wheel grip-loss synthesis from per-wheel slip ratio, slip angle and reported wheel slip, with normalized wheel load used when ACC provides it and a tested neutral fallback otherwise. Front slip is encoded as a quick sharp pulse texture; rear slip uses slow, heavy grouped thumps so axle position remains readable as well as left/right direction.
- Per-session adaptive tyre baselines learn each car/compound's normal rolling and loaded-corner slip without learning from TC, ABS, kerbs, off-track running or confirmed grip loss. Static competition floors remain in place so true understeer, oversteer, wheelspin and lock-up still trigger immediately.
- A deliberately quiet engine texture follows a monotonic nonlinear RPM curve without masking tyre, brake or chassis cues. Idle retains the original tiny presence, low-RPM output does not jump with initial throttle, mid/high RPM crosses perceptible actuator steps progressively, and high throttle in the true redline band reaches a deliberately clearer bilateral peak while remaining far below collision and handling cues. The module can be disabled independently.
- Directional impact feedback combines damage changes with short-window G-force and local-velocity evidence. It distinguishes front impacts from left/right contact, conserves total impulse energy while panning, adds a bounded continuation texture for damage-producing side scrapes, remains available for discrete impacts when ACC damage is disabled, and rejects the recorded grass/off-track transients.
- Native-first mixing preserves raw ACC engine, road, chassis and collision detail during ordinary driving. Centered grip events preserve total native energy while centering its arbitrary XInput-channel imbalance; confirmed one-sided events deeply suppress only the conflicting grip and re-route useful transient energy to the telemetry-selected side. A detected impact briefly ducks the background beneath its short crack/thud envelope rather than raising continuous vibration.
- Stale-telemetry watchdog that clears all generated feedback instead of leaking unlocalized XInput rumble into the left grip.
- Optional `--capture-telemetry` CSV mode records every distinct ACC physics packet observed by the app, native ACC motor values and final left/right output. The extended capture includes damage, local motion, four-wheel load, ACC's reserved `Mz/Fx/Fy` slots, wheel speed, brake pressure, suspension and tyre-contact-normal data. Normal operation performs no file I/O.
- Diagnostic captures include the complete 11-byte state of each adaptive trigger, allowing amplitude, active zones and cadence to be compared instead of checking only the effect mode.
- Fail-safe shutdown clears both actuators and adaptive triggers after telemetry/output/input-thread faults. A disconnected USB input stream ends the virtual-controller session instead of leaving stale steering or pedal input active.
- Startup failures for a missing/hidden USB DualSense or unavailable ViGEmBus are reported in the console with actionable checks instead of escaping as an unhandled .NET exception.
- A low-resource native WPF home screen shows the controller, virtual-controller, ACC telemetry and feedback states without turning the companion into a telemetry dashboard.
- The default executable opens the GUI and starts automatically. Diagnostic arguments still use the console and preserve the existing capture, replay and hardware-test workflows.
- Default preserves the calibrated competition output exactly. The optional Custom preset separates pedal resistance, ABS, wheel lock, upshift, redline, engine texture, traction-control, wheelspin, surface, grip-loss, collision and native-ACC cues into independent 0–100 modules. Set a cue to `0` to turn only that cue off, or right-click its parameter row to restore the calibrated default. Every choice is saved beside the portable executable.
- A separate in-app Diagnostics & logs page shows the controller, virtual-controller, ACC telemetry and feedback path plus a short memory-only session log. Its copied diagnostic report includes privacy-safe driver probes, the runtime state, detailed exception chains and recent events without writing diagnostic files to disk.

USB is required by the integrated bridge and is the recommended competition connection.

## Support and diagnostics

Open **Diagnostics and logs**, then select **Copy diagnostic report**. The report contains driver checks, runtime state, detailed exception chains and recent in-memory events, without including the private HID device path. Send that copied text when reporting a problem.

The application does not create log files during normal use. `--capture-telemetry` is a developer calibration mode and should only be used when specifically requested.

See `RELEASE_NOTES.md` for portable-data details and known release limitations.

## Development commands

```powershell
dotnet build .\ACCDualSenseFeedback.sln -c Release
dotnet .\src\ACCDualSenseFeedback\bin\x64\Release\net8.0-windows\ACCDualSenseFeedback.dll --list-devices
dotnet .\src\ACCDualSenseFeedback\bin\x64\Release\net8.0-windows\ACCDualSenseFeedback.dll --test-feedback
dotnet .\src\ACCDualSenseFeedback\bin\x64\Release\net8.0-windows\ACCDualSenseFeedback.dll --headless
dotnet .\src\ACCDualSenseFeedback\bin\x64\Release\net8.0-windows\ACCDualSenseFeedback.dll --dry-run
dotnet .\src\ACCDualSenseFeedback\bin\x64\Release\net8.0-windows\ACCDualSenseFeedback.dll --capture-telemetry

dotnet publish .\src\ACCDualSenseFeedback\ACCDualSenseFeedback.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\win-x64
```

Running without arguments opens the native home screen, connects to ACC shared memory and uses the first visible DualSense/DualSense Edge. It is intentionally a small companion: the primary state and recovery action stay visible, while a separate in-app page provides concise connection diagnostics. Capture, replay and hardware-test diagnostics remain available through command-line modes.

Use `--headless` for the live console dashboard. It prints XInput motor values, gear, RPM, pedals, TC/ABS intervention, per-side tyre dirt, and the active L2/R2 effect modes. `21` means progressive resistance, `26` means an automatic trigger pulse, and `05` means cleared/off because telemetry is stale. Run with `--capture-telemetry` only when calibration data is requested; the CSV is written next to the executable.

For a requested calibration run, launch the executable with `--capture-telemetry`, record one driving condition, then press Ctrl+C. Each launch writes a separate timestamped `ACC-telemetry-*.csv` beside the executable. Rename that file for the condition before starting the next run, then send back all captures. Separate files are preferred for normal driving, heavy braking, kerbs, front impacts and side impacts.

## Attribution

Thanks to Hamza Yeşilmen (HamzaYslmn) and the Forza Horizon DualSense Python project for technical reference on DualSense adaptive-trigger behavior:

https://github.com/HamzaYslmn/Forza-Horizon-DualSense-Python
