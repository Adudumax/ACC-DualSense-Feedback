ACC DualSense Feedback 1.2.1
===========================

A Windows companion that adds DualSense feedback, adaptive triggers and optional
USB gyro steering to Assetto Corsa Competizione.

Extract the whole ZIP. Keep ACCGyroFusion.dll and the licenses folder beside
ACCDualSenseFeedback.exe. No separate .NET installation is needed.

REQUIREMENTS

- Windows 10 or Windows 11, x64
- Assetto Corsa Competizione for PC
- DualSense or DualSense Edge connected by USB
- ViGEmBus installed
- Steam Input disabled for ACC
- DS4Windows fully closed while this app runs

ViGEmBus official releases:
https://github.com/nefarius/ViGEmBus/releases

Install ViGEmBus, not ViGEm.NET; the client is already included. If ViGEmBus is
already installed and working, you do not need to reinstall it.

HidHide is optional. If it already hides your controller, allow the current
ACCDualSenseFeedback.exe path in its Applications list. Update that path after
moving or upgrading the app.

FIRST USE

1. Extract to a writable folder. Do not run the EXE inside the ZIP.
2. Install ViGEmBus if needed and restart Windows if requested.
3. Connect the controller, close DS4Windows and disable Steam Input for ACC.
4. Open ACCDualSenseFeedback.exe, then start ACC and enter driving.

Feedback starts automatically when the controller and game are ready.
Close the window to stop feedback and release the virtual controller.
Minimizing keeps the app running.

Outside ACC, a brief trigger resistance on first connection confirms controller
output. It does not confirm that game telemetry is available.

DEFAULT AND CUSTOM

Default uses calibrated feedback and Smooth lighting, with gyro off.
Custom lets you adjust feedback strength, lighting and gyro independently.
Strength controls run from 0 to 100; zero disables that cue. Right-click a row to
restore its default. Changes apply immediately. Returning to Default preserves
your saved Custom choices.

IMPORTANT: Keep "Pulse begins" at its default 96.2% for automatic native HUD
alarm timing on supported cars. Changing it selects manual R2 timing at
94.0%-99.0% of maximum RPM and returns lighting to its generic 97% reference;
they may no longer match the HUD. Right-click the row to restore the default.
Use the separate strength controls to adjust intensity. Unknown cars retain
the generic RPM-based cue.

GYRO STEERING (USB)

1. Choose Custom > Advanced settings > 07 Gyro steering > Adjust settings.
2. Enable gyro. On first use, pause ACC and complete Stationary calibration
   with the controller resting still.
3. Hold the controller naturally, select Set straight ahead, then drive.

Stationary calibration is saved for each controller. Set straight ahead each
time you start the app or reconnect the controller; this confirms your current
grip and does not require repeating stationary calibration.

Adjust steering angle, deadzone, response curve and stabilization to taste.
Choose Continuous, Hold or Toggle activation. Continuous resumes automatically
when you return to driving, unless you intentionally paused it with the bound
button. A manual pause still needs that button or Resume gyro. Hold and Toggle
follow their selected button and resume conditions. Physical-stick takeover is
available. The steering test works in the app without activating game steering.

A bound activation button is reserved for gyro while Custom gyro is enabled.
Keep ACCGyroFusion.dll beside the EXE. Gyro steering is USB-only.

UPGRADING OR MOVING THE APP

Close the old copy and extract the new ZIP to a new folder. Copy these files
from beside the old EXE if they exist:

- ACCDualSenseFeedback.settings.json
- ACCDualSenseFeedback.lights.json
- ACCDualSenseFeedback.gyro.json
- ACCDualSenseFeedback.gyro-calibration.json

Both language editions share these settings. Keep the new EXE and DLL together;
do not replace the new DLL with an old copy. Update HidHide access if needed.
Set straight ahead again after starting the new copy.

TROUBLESHOOTING

- DualSense unavailable: check USB connection and HidHide application access.
- Feedback unavailable: check ViGEmBus, the controller connection and HidHide.
- Action needed: close DS4Windows, then select Check again.
- Waiting for ACC / driving: enter a driving session; menus are not driving.
- Duplicate input: disable Steam Input and close other controller emulators.

For support, open Diagnostics and logs > Copy diagnostic report before closing
the app. Session logs stay in memory and clear on exit. Copied reports redact
private HID paths and Windows user-profile directories.

The EXE is unsigned, so Windows SmartScreen may show an unknown-publisher
warning. Download only from this repository's Release page.

SUPPORT, RELEASES AND LICENSE

https://github.com/Adudumax/ACC-DualSense-Feedback

The project uses the MIT License. The licenses folder contains the project
license, third-party notices and required license texts; keep it with the app.
