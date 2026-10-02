ACC DualSense Feedback 1.0.0
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
