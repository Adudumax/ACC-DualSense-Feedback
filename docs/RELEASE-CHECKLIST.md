# Version 1.0.0 release checklist

## Automated gates

- [x] Release build succeeds with no compiler errors or compiler warnings.
- [x] NuGet audit reports no known vulnerable direct or transitive package.
- [x] Published executable reports product and file versions `1.0.0` / `1.0.0.0`.
- [x] Deterministic self-test passes from the packaged executable.
- [x] All nine accepted telemetry captures replay successfully.
- [x] Home, controller-error, advanced-settings and diagnostics snapshots render cleanly at the development machine's 150% DPI.
- [x] The home page was manually checked at 100% DPI; the shared one-line error-state crowding found at both scales was corrected.
- [ ] All four UI snapshots pass the clean GitHub-hosted 100% DPI job.
- [x] Portable ZIP contents and SHA-256 manifests are verified.
- [x] Third-party notices and both required license files are present.
- [x] Public package contains no developer or personal settings file.
- [x] Deterministic diagnostics cover missing ViGEmBus, HidHide blocking an unlisted executable, and a whitelisted visible USB controller.
- [x] A standalone environment report verifies the live driver/device probes without exposing a private HID path.
- [x] Haptics, hardware, runtime and telemetry source files remain byte-identical to the archived `v1.0.0` calibration baseline.

## Manual gates

- [ ] Fresh Windows 10/11 x64 machine starts the portable build.
- [ ] Missing ViGEmBus produces an actionable diagnostic report.
- [ ] HidHide-enabled machine can see the controller after whitelisting the executable.
- [ ] DS4Windows-running protection prevents duplicate virtual controllers.
- [ ] USB DualSense input, virtual controller output, ACC telemetry and feedback all become active.
- [ ] Default and Custom presets persist after restarting the application.
- [ ] Every advanced parameter reaches `0`, changes in real time and resets by right-click.
- [ ] Copy diagnostic report contains enough information for support and no private device path.
- [ ] Final hardware drive confirms preview12-equivalent feel.

## Distribution decision

- [ ] Decide whether to code-sign the executable. An unsigned build is distributable but may trigger SmartScreen.
- [ ] Upload only the versioned ZIP and publish its SHA-256 value.
- [ ] Keep the archived preview12 package and source snapshot as the rollback baseline.
