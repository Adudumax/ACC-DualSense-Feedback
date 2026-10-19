# ACC DualSense Feedback 1.1.0

Adds controller lighting for ACC and fixes false game detection at startup.

- RPM-driven RGB lighting, five progressive white LEDs, and a high-RPM flash.
- Ice-blue breathing at idle, yellow ABS / possible wheel-lock warnings, and white reverse lighting.
- Light on/off and Smooth/Discrete controls in Advanced settings.
- A brief, one-time startup trigger confirmation when ACC is not running.
- Fixed false ACC detection and repeated trigger activation caused by leftover game data.

Existing in-game vibration, adaptive-trigger effects, calibrated presets, and
feedback settings are unchanged from 1.0.1. Both portable languages are retained.
See docs/RELEASE-v1.1.0.md for the release downloads and checksums.

---

# ACC DualSense Feedback 1.1.0-preview.6

Startup confirmation now uses the stable v1.0.1 resting pedal resistance curves,
following each side's existing base-resistance setting across its full range.

- Default L2 zones: 3, 4, 4, 5, 5, 6, 6, 7, 7, 8. Default R2 zones:
  2, 2, 2, 3, 3, 3, 4, 4, 5, 5. This replaces preview.5's uniform 2/8 cue.
- The existing approximately 120 ms release, once-per-process limit, reconnect
  suppression, disabled sides and immediate game-feedback priority are retained.
- Only the standalone startup confirmation changes at runtime. Gameplay haptics,
  lighting, telemetry guards, runtime integration and hardware output are unchanged.
  Ice-blue idle lighting and both portable languages are retained.

Validation checks resting-curve and USB/BT report parity at all 101 user strength
settings, lifecycle protections, existing lighting/telemetry/haptic checks and
the no-game dry run. Physical sound and feel still require controller validation;
matching the curves cannot guarantee the exact historical sound.

---

# ACC DualSense Feedback 1.1.0-preview.5

Adds a bounded startup trigger confirmation and ice-blue stationary idle lighting.

- Before ACC is running, the first opened controller receives gentle trigger-only
  resistance for about 120 ms, then Off. It plays at most once per application
  process; reconnects and runtime retries do not replay it. Existing game feedback
  wins immediately, and ACC already running skips the cue entirely.
- Each trigger respects its base-resistance setting, capped to a gentle level;
  zero leaves that side off. No grip rumble, fabricated telemetry, extra output
  thread, blocking sleep or changes to the gameplay haptic engine are introduced.
- Waits for the first game-process probe. The process/first-packet/stale-telemetry
  protections remain enabled. Dry-run never emits or consumes the physical cue.
- Stationary idle breathes ice blue (#5AAFD6), fading to the original RPM colours
  on departure. Low-RPM driving remains green; braking yellow, reverse white,
  shift-flash dark phases, white LEDs and the two-control settings stay intact.
- Keeps ViGEmBus, bilingual self-contained portable ZIPs and the user-tested
  preview.4 packages. Does not add fuel, start lights, flame or flag effects.

Validation includes startup state/timing/priority and report tests, ice-blue
colour/transition tests, existing telemetry guards, haptic-byte checks and
offscreen WPF checks. Actual startup sound/feel and idle brightness need a new
controller test; the historical startup sound has not been conclusively identified.
See docs/STARTUP-CONFIRMATION.zh-CN.md and docs/ACC-LIGHTING.zh-CN.md.

---

# ACC DualSense Feedback 1.1.0-preview.4

ACC-specific lighting preview. ViGEmBus and portable ZIP delivery are retained.

- Running engines have a dim green low-RPM floor; stationary idle adds a gentle
  three-second breath. Driving at low RPM keeps a steady floor instead of going black.
- A generic 60%–97% static-max-RPM colour/white-LED range replaces the CSP fallback
  interval. At 97%, all five white LEDs light and RGB flashes at 3 Hz. These are visual
  defaults, not verified optimal per-car shift points.
- ABS feedback vibration while braking at speed triggers a 4 Hz yellow RGB cue.
  Suspected lock requires a learned rolling reference, wheel-speed deficit and sustained
  longitudinal slip. Assist levels, sideways slip and duplicate packets cannot trigger it alone.
  White LEDs retain RPM meaning while RGB displays the braking warning.
- Settings remain Enable and Smooth/Discrete, with the existing styles/animations.
  Engine-off, replay, disable and stale telemetry clear lights. No fuel, countdown,
  flame, TC or race-flag effect is included in this iteration.
- This revision leaves Haptics, Telemetry, Runtime and Hardware sources byte-identical
  to preview.3. Existing preview connection guards and HID send timing are unchanged.

Validation uses ACC lighting scenarios, the retained 862-case CSP math reference,
no-game dry-run, haptic byte checks, bilingual offscreen WPF rendering, source hashes
and portable-package checks. Real controller brightness and in-game thresholds still
require driving validation. See docs/ACC-LIGHTING.zh-CN.md for exact rules/limitations.

---

# ACC DualSense Feedback 1.1.0-preview.3

CSP-source lighting preview with a two-control settings section.

- Ported the published CSP HSV/discrete colour function, 3 Hz RGB shift flash,
  strict five-player-LED thresholds and dynamic LED brightness. Reverse uses white.
  Removed the earlier custom hysteresis, time smoothing, white-LED flash and top-gear suppression.
- Added an optional, lighting-only graphics-prefix reader for replay/pause state.
  Shift-range inputs use ACC static.maxRpm and the CSP fallback interval ratio;
  AC per-car ai.ini shift points and other unavailable CSP signals are not invented.
- Settings now expose only Enable and Smooth/Discrete, with matching native styles
  and short switch/selection animations. Removed the simulated preview panel/timer.
  Legacy disabled or zero-brightness choices remain disabled after migration.
- No ABS/TC or flag effects were added. See docs/CSP-LIGHTING-ANALYSIS.zh-CN.md
  for source revision, priority order, precise formulas and ACC input limitations.
- Haptic algorithms, physics fields, calibration, bridge and HID send decisions are unchanged.
  The preview.2 connection guards were not expanded in this revision and remain preview behavior.

Validation includes 862 fixtures executed by the original CSP Lua functions,
state/settings/memory tests, pre-lighting haptic-byte comparisons and bilingual WPF
layout/animation checks. Physical-controller, firmware and in-game validation remains pending.

---

# ACC DualSense Feedback 1.1.0-preview.2

Fixes startup detection and repeated feedback from retained shared memory.

- ACC detection now checks the running `AC2-Win64-Shipping` process. A named mapping by itself no longer reports that the game is running.
- The first physics sample after startup, process replacement or a mapping retry only establishes a baseline. Feedback requires a subsequent packet change, so a frozen page cannot be replayed every two seconds.
- ACC exit clears the published telemetry. The existing 120 ms stale-data cutoff, feedback engine, calibration, trigger curves, virtual-controller bridge and HID output path are unchanged from preview.1.

Validation: 19 automated checks, including a controller-free runtime check against the retained mapping with ACC closed, frozen-packet retries, live recovery, process replacement, counter wrap and unchanged haptic report bytes. In-game and physical-controller validation remains pending.

---

# ACC DualSense Feedback 1.1.0-preview.1

First RPM-light preview; portable Windows x64, with ViGEmBus unchanged.

- Smooth RPM-driven RGB light bar, five progressive white LEDs and a 2.5 Hz shift reminder with hysteresis.
- Separate lighting controls and portable `ACCDualSenseFeedback.lights.json`; existing haptic presets and settings are unchanged.
- Software preview with manual RPM, neutral/top-gear examples and an automatic acceleration/upshift sweep. The preview never sends simulated output to the controller.
- Optional, lighting-only ACC static-page reader provides a maximum-RPM fallback and recognizes common six/seven-speed cars for top-gear suppression. Unknown models do not have their highest gear guessed from observed driving.
- Stopped engines, stale telemetry, disabled lighting and shutdown clear the LEDs. No flag integration.
- The haptic engine, trigger effects, calibration, physics reader, virtual-controller bridge, wake timing and original HID send decision remain unchanged. Lighting rides existing reports; only LED-related report bytes differ.

Validation: deterministic state-transition/settings/static-memory tests, a golden pre-lighting USB-report comparison and offscreen WPF rendering in both languages. Actual controller brightness, firmware behavior and perceived shift timing still require driving validation.

---

# ACC DualSense Feedback 1.0.1

Maintenance release focused on reliable application shutdown.

## Fixed

- Fixed a WPF closing re-entry crash when the feedback runtime had already stopped, including the `DualSense unavailable` state and controller-disconnect errors.
- Preserved graceful motor, adaptive-trigger and virtual-controller cleanup when closing during an active session.
- Preserved portable settings before the final window close.

The feedback engine, calibrated defaults and user interface are unchanged from 1.0.0.

---

# ACC DualSense Feedback 1.0.0

Initial public release of ACC DualSense Feedback.

## Highlights

- Automatic USB DualSense-to-virtual-Xbox controller bridge for ACC.
- Directional road, kerb, grip-loss and collision feedback.
- Distinct ABS, wheel-lock, traction-control, wheelspin, shift and redline cues.
- Quiet RPM-linked engine texture designed not to mask vehicle-state feedback.
- Progressive adaptive-trigger resistance with independently adjustable modules.
- Default and Custom presets, per-parameter right-click reset, and portable settings.
- In-app diagnostics with a privacy-safe report that can be copied for support.
- Copied reports redact user-profile directories and private HID device paths in error messages while retaining error codes and call stacks.
- No telemetry or diagnostic files are written during normal operation.
- Refined status layout remains readable at both 100% and 150% Windows scaling.

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

## Credits and third-party licenses

The project's own code is distributed under the MIT License. Its full text is included in the portable package's `licenses` folder.

Third-party license texts for the bundled ViGEm.NET client and, in the Chinese build, the Noto Sans SC font are included with this distribution.
