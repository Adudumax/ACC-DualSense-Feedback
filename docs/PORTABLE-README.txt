ACC DualSense Feedback 1.1.0
================================

A lightweight companion that adds telemetry-driven DualSense feedback to
Assetto Corsa Competizione.

REQUIREMENTS

- Windows 10 or Windows 11, x64
- Assetto Corsa Competizione for PC
- DualSense or DualSense Edge connected by USB
- ViGEmBus installed
- Steam Input disabled for ACC
- DS4Windows fully closed while this application is running

ViGEmBus releases:
https://github.com/nefarius/ViGEmBus/releases

HidHide is optional. If you already use HidHide, add
ACCDualSenseFeedback.exe to its Applications list so this app can see the
physical controller.

FIRST USE

1. Extract the complete ZIP to a writable folder. Do not run it inside the ZIP.
2. Install ViGEmBus if it is not already installed.
3. Connect the DualSense by USB.
4. Close DS4Windows and disable Steam Input for ACC.
5. Start ACCDualSenseFeedback.exe, then start ACC.

The feedback path starts automatically while the app is open. Close the app to
stop the virtual controller and feedback safely.

STARTUP CONFIRMATION

When ACC is not running, the first successful controller connection briefly
applies the stable v1.0.1 resting trigger resistance for about 120 ms, then releases it. It plays
at most once per application process; reconnecting or Check again cannot replay
it. ACC already running skips it; live game feedback takes priority immediately.
A trigger follows its existing base-resistance setting, including stronger custom
settings; a side set to zero stays off. Grip motors are not
used. This confirms the output path, not working game telemetry. Process checks,
first-packet verification and stale-frame clearing remain enabled.

CONTROLLER LIGHTS

Open Advanced settings and scroll to "06 Controller lights". There are only
two controls: Enable and Smooth/Discrete. Both use the app's native styles;
the switch and mode selection have short animations.

A running engine has a dim low-RPM floor. Stationary idle breathes ice blue
(base #5AAFD6); departure fades back to RPM colours. Low-RPM driving keeps a steady
green floor. Smooth uses HSV hue; Discrete uses
green/yellow/red stages. All five white LEDs light at 97% of static.maxRpm,
where RGB flashes at 3 Hz. This is a generic visual cue, not an optimal per-car
shift point. White-LED hardware brightness adjusts automatically.

ABS feedback vibration while braking at speed triggers a 4 Hz yellow RGB cue.
Suspected lock requires a learned rolling reference, wheel slowdown and sustained
longitudinal slip. Missing wheel data never produces a slip-only lock warning.
White LEDs keep showing RPM during braking. Reverse uses white. Engine-off,
replay, disabled lighting, stale telemetry and exit clear lights. Fuel, countdown,
flame, TC and flag effects are not included. Full rules and limitations:
docs/ACC-LIGHTING.zh-CN.md in the source repository.

Settings are saved separately in ACCDualSenseFeedback.lights.json beside the
EXE. Old disabled/zero-brightness choices stay off. Haptic settings, calibration
and normal-driving HID send decisions are unchanged; startup adds the single
confirmation described above. CSP's MIT license is included under licenses.

TROUBLESHOOTING

- DualSense unavailable: check the USB cable and HidHide application access.
- Feedback unavailable: check ViGEmBus, USB and HidHide.
- Action needed: close DS4Windows, then select Check again.

Open Diagnostics and logs inside the app and select Copy diagnostic report when
requesting support. The report includes driver and runtime information without
including private HID device paths or user-profile directories in error messages.

The executable is currently unsigned, so Windows SmartScreen may show an
unknown-publisher warning.

SUPPORT AND RELEASES

https://github.com/Adudumax/ACC-DualSense-Feedback

This project's code is licensed under the MIT License. The project license,
third-party notices and required license texts are in the licenses folder.
