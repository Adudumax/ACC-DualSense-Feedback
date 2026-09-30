# ACC DualSense Feedback 1.0.0

Initial public release, based on the approved preview12 calibration.

## Interface refinements

- Shortened the home-page recovery guidance so the status, explanation and retry action remain clearly separated at both 100% and 150% Windows scaling.

## Highlights

- Automatic USB DualSense-to-virtual-Xbox controller bridge for ACC.
- Directional road, kerb, grip-loss and collision feedback.
- Distinct ABS, wheel-lock, traction-control, wheelspin, shift and redline cues.
- Quiet RPM-linked engine texture designed not to mask vehicle-state feedback.
- Progressive adaptive-trigger resistance with independently adjustable modules.
- Default and Custom presets, per-parameter right-click reset, and portable settings.
- In-app diagnostics with a privacy-safe report that can be copied for support.
- No telemetry or diagnostic files are written during normal operation.

## Requirements

- Windows 10 or Windows 11, x64.
- Assetto Corsa Competizione for PC.
- DualSense or DualSense Edge connected by USB.
- ViGEmBus installed.
- Steam Input disabled for ACC.
- DS4Windows closed while this application is running.

HidHide is optional. If it is enabled, add `ACCDualSenseFeedback.exe` to its Applications list so the tool can see the physical controller.

## Portable data

The first launch uses the built-in Default preset. After a user changes a parameter, the Custom choices are stored in `ACCDualSenseFeedback.settings.json` beside the executable. Keep the application in a writable folder and move that settings file with the application when transferring a customized setup to another PC.

## Known release notes

- The executable is not digitally signed, so Windows SmartScreen may display an unknown-publisher warning.
- Integrated controller bridging requires USB; Bluetooth is not supported by the normal app workflow.
- ACC retains several shared-memory compatibility fields that are not populated by the game. Their absence is handled by tested fallbacks and does not indicate a controller fault.
- Only one instance of the application can run at a time.

## Attribution

Adaptive-trigger implementation work was informed by the project identified in `THIRD_PARTY_NOTICES.md`. The required attribution, source link, sponsor link and complete license are included with this distribution and exposed from the Diagnostics & logs page.
