# Native ACC shift parameters

`acc-1.10.4.json` contains vehicle IDs and the scalar `EngineData.RpmSuggestedShift`,
`RpmLimiterAlarm`, and `Limiter` values read from the canonical ACC 1.10.4 CarData
assets. The values are not hand-tuned percentages. No game assets, private keys,
player information, or installation paths are included.

Run `python tools/shift-profiles/generate.py` from the repository to regenerate
`src/ACCDualSenseFeedback/Haptics/AccShiftProfiles.g.cs`. The committed generated
C# file is compiled into the executable; users and normal .NET builds do not
need Python, this JSON, or a game resource parser.

The runtime resolves a profile only when the telemetry vehicle ID changes. Both
the static and physical RPM limits must match the profile, and the user's pulse
start must retain its default value. Otherwise the existing generic calculation
is used. Explicit manual timing takes precedence. The raw telemetry is never
rewritten.

The alarm uses `RpmLimiterAlarm`, not `RpmSuggestedShift` or the actual limiter.
Its onset uses the native strict greater-than comparison, retaining the existing
80 RPM release hysteresis, R2 envelope, strength, frequency, arbitration, and
lighting cadence. The native BasicCar01 HUD's sixth-gear exception is preserved
for the shared automatic cue. This is threshold alignment, not a subscription to
the HUD's live flash state or a claim of matching every cockpit dashboard.

When adding vehicles or updating data, repeat the source extraction and verify
the corresponding HUD behavior. Do not infer missing entries from similar car
names or maximum RPM ratios. The 55 entries have been structurally checked;
software checks do not replace driving tests across vehicles or BoP variants.

Run `dotnet run --project tests/ACCDualSenseFeedback.Tests.csproj -c Release`
after regeneration. Do not ship this development directory in portable packages.
