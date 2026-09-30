using System.Diagnostics;
using System.Runtime.InteropServices;
using ACCDualSenseFeedback.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Runtime;
using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            Expect(Marshal.SizeOf<AccPhysicsPage>() == AccSharedMemoryReader.PhysicsPageSize,
                $"ACC physics page size was {Marshal.SizeOf<AccPhysicsPage>()}, expected 800.");

            Span<byte> inputReport = stackalloc byte[64];
            inputReport[0] = 0x01;
            inputReport[1] = 0x80;
            inputReport[2] = 0x00;
            inputReport[3] = 0xFF;
            inputReport[4] = 0x80;
            inputReport[5] = 37;
            inputReport[6] = 211;
            inputReport[8] = 0x21; // Cross + up/right.
            inputReport[9] = 0x23; // L1 + R1 + Options.
            inputReport[10] = 0x01; // PS/Guide.
            Expect(DualSenseInputState.TryParseUsb(inputReport, out DualSenseInputState input),
                "USB DualSense input report was not parsed.");
            Expect(input.LeftX == 0 && input.LeftY > 32000 && input.RightX > 32000,
                "DualSense stick axis conversion is invalid.");
            Expect(input.LeftTrigger == 37 && input.RightTrigger == 211,
                "DualSense analog trigger conversion is invalid.");
            Expect(input.Buttons == 0x1719, "DualSense-to-Xbox button mapping is invalid.");

            var telemetry = new TelemetrySnapshot
            {
                PacketId = 1,
                ObservedTimestamp = Stopwatch.GetTimestamp(),
                Gas = 0.72f,
                Brake = 0.48f,
                Gear = 3,
                Rpm = 6500,
                CurrentMaxRpm = 8000,
                SpeedKmh = 142f,
                Tc = 0.35f,
                Abs = 0.55f,
                TcLevel = 4f,
                AbsLevel = 4f,
                KerbVibration = 0.22f,
                SlipVibration = 0.16f,
                GVibration = 0.08f,
                AbsVibration = 0.31f,
                WheelLoadFl = 3500f,
                WheelLoadFr = 3500f,
                WheelLoadRl = 3500f,
                WheelLoadRr = 3500f,
                SlipRatioFl = 0.12f,
                SlipRatioFr = 0.15f,
                SlipRatioRl = 0.18f,
                SlipRatioRr = 0.20f,
                SlipAngleFl = 0.06f,
                SlipAngleFr = 0.07f,
                SlipAngleRl = 0.09f,
                SlipAngleRr = 0.08f
            };

            var engine = new CompetitionFeedbackEngine();
            long effectStart = Stopwatch.GetTimestamp();
            var brakeBaseline = TriggerEffect.FeedbackZones(3, 4, 4, 5, 5, 6, 6, 7, 7, 8);
            var throttleBaseline = TriggerEffect.FeedbackZones(2, 2, 2, 3, 3, 3, 4, 4, 5, 5);
            FeedbackFrame frame = default;
            bool absCurveChanged = false;
            for (int i = 0; i < 80; i++)
            {
                frame = engine.Compute(
                    telemetry,
                    effectStart + i * Stopwatch.Frequency / 250,
                    73,
                    41);
                absCurveChanged |= frame.LeftTrigger != brakeBaseline;
            }
            Expect(frame.LeftTrigger.Mode == 0x26,
                "ABS intervention must enter the official trigger-vibration mode.");
            Expect(frame.LeftTrigger == TriggerEffect.Vibration(2, 5, 15),
                "ABS intervention must reach the stronger competition pulse.");
            Expect(absCurveChanged, "Enabled ABS intervention should modulate the brake curve.");
            Expect(frame.LeftActuator > 0 && frame.RightActuator > 0,
                "Telemetry-driven feedback must produce body output during active vehicle events.");

            var absDisabledEngine = new CompetitionFeedbackEngine();
            FeedbackTuning absDisabledTuning = new FeedbackProfile
            {
                ActivePreset = FeedbackPreset.Custom,
                AbsPulse = 0
            }.ToTuning();
            FeedbackFrame absDisabledFrame = default;
            for (int i = 0; i < 80; i++)
            {
                absDisabledFrame = absDisabledEngine.Compute(
                    telemetry,
                    effectStart + i * Stopwatch.Frequency / 250,
                    73,
                    41,
                    absDisabledTuning);
            }
            Expect(absDisabledFrame.LeftTrigger == brakeBaseline,
                "Disabling ABS pulse must preserve only the independent brake resistance baseline.");

            var resistanceDisabledEngine = new CompetitionFeedbackEngine();
            FeedbackTuning resistanceDisabledTuning = new FeedbackProfile
            {
                ActivePreset = FeedbackPreset.Custom,
                BrakeResistance = 0
            }.ToTuning();
            FeedbackFrame resistanceDisabledFrame = default;
            for (int i = 0; i < 80; i++)
            {
                resistanceDisabledFrame = resistanceDisabledEngine.Compute(
                    telemetry,
                    effectStart + i * Stopwatch.Frequency / 250,
                    73,
                    41,
                    resistanceDisabledTuning);
            }
            Expect(resistanceDisabledFrame.LeftTrigger.Mode == 0x26,
                "Disabling resting brake resistance must not disable the independent ABS event pulse.");

            FeedbackTuning defaultWithStoredCustomChoices = new FeedbackProfile
            {
                ActivePreset = FeedbackPreset.Default,
                BrakeResistance = 100,
                AbsPulse = 0,
                ShiftKick = 12,
                RoadSurface = 0,
                NativeAcc = 100
            }.ToTuning();
            Expect(defaultWithStoredCustomChoices == FeedbackTuning.Neutral,
                "The Default preset must ignore stored Custom slider positions.");

            FeedbackTuning midpointCustom = new FeedbackProfile
            {
                ActivePreset = FeedbackPreset.Custom
            }.ToTuning();
            Expect(midpointCustom == FeedbackTuning.Neutral,
                "Midpoint Custom sliders must preserve the calibrated feedback mechanism.");

            FeedbackTuning offTuning = new FeedbackProfile
            {
                ActivePreset = FeedbackPreset.Custom,
                BrakeResistance = 0,
                ThrottleResistance = 0,
                AbsPulse = 0,
                LockPulse = 0,
                ShiftKick = 0,
                RedlinePulse = 0,
                TcPulse = 0,
                WheelspinPulse = 0,
                RoadSurface = 0,
                GripLoss = 0,
                NativeAcc = 0,
                EngineTexture = 0,
                CollisionFeedback = 0
            }.ToTuning();
            Expect(offTuning.BrakeResistanceScale == 0f &&
                   offTuning.AbsPulseScale == 0f &&
                   offTuning.RoadSurfaceScale == 0f &&
                   offTuning.NativeAccScale == 0f &&
                   offTuning.EngineTextureScale == 0f &&
                   offTuning.CollisionFeedbackScale == 0f,
                "A zero Custom value must produce a real off state.");
            Expect(FeedbackTuning.ScaleTrigger(
                    TriggerEffect.Vibration(2, 5, 15),
                    offTuning.AbsPulseScale) == TriggerEffect.Off,
                "A disabled trigger module must send the official off effect.");

            FeedbackTuning strongTuning = new FeedbackProfile
            {
                ActivePreset = FeedbackPreset.Custom,
                BrakeResistance = 100,
                ThrottleResistance = 100,
                AbsPulse = 100,
                LockPulse = 100,
                ShiftKick = 100,
                RedlinePulse = 100,
                TcPulse = 100,
                WheelspinPulse = 100,
                RoadSurface = 100,
                GripLoss = 100,
                NativeAcc = 100,
                EngineTexture = 100,
                CollisionFeedback = 100
            }.ToTuning();
            Expect(Math.Abs(strongTuning.RoadSurfaceScale - 1.45f) < 0.0001f &&
                   Math.Abs(strongTuning.GripLossScale - 1.45f) < 0.0001f &&
                   Math.Abs(strongTuning.NativeAccScale - 1.35f) < 0.0001f &&
                   Math.Abs(strongTuning.EngineTextureScale - 2.00f) < 0.0001f &&
                   Math.Abs(strongTuning.CollisionFeedbackScale - 1.50f) < 0.0001f &&
                   Math.Abs(strongTuning.AbsPulseScale - 1.55f) < 0.0001f,
                "High Custom slider scaling is invalid.");
            Expect(FeedbackTuning.ScaleTrigger(
                    TriggerEffect.Vibration(2, 5, 15),
                    strongTuning.AbsPulseScale) == TriggerEffect.Vibration(2, 8, 15),
                "High Custom trigger scaling is invalid.");

            var legacyPathEngine = new CompetitionFeedbackEngine();
            var neutralTuningEngine = new CompetitionFeedbackEngine();
            for (int i = 0; i < 80; i++)
            {
                long sampleTime = effectStart + i * Stopwatch.Frequency / 250;
                FeedbackFrame legacyFrame = legacyPathEngine.Compute(telemetry, sampleTime, 73, 41);
                FeedbackFrame neutralFrame = neutralTuningEngine.Compute(
                    telemetry,
                    sampleTime,
                    73,
                    41,
                    FeedbackTuning.Neutral);
                Expect(neutralFrame == legacyFrame,
                    "Default tuning must match the calibrated engine output bit-for-bit.");
            }

            string settingsTestPath = Path.Combine(
                Path.GetTempPath(),
                $"ACCDualSenseFeedback-self-test-{Guid.NewGuid():N}.json");
            try
            {
                var savedProfile = new FeedbackProfile
                {
                    ActivePreset = FeedbackPreset.Custom,
                    BrakeResistance = 88,
                    ThrottleResistance = 43,
                    AbsPulse = 0,
                    LockPulse = 61,
                    ShiftKick = 84,
                    RedlinePulse = 37,
                    RedlineStartPermille = 975,
                    TcPulse = 29,
                    WheelspinPulse = 0,
                    RoadSurface = 31,
                    GripLoss = 67,
                    NativeAcc = 44,
                    EngineTexture = 13,
                    CollisionFeedback = 72
                };
                Expect(PortableProfileStore.TrySaveTo(settingsTestPath, savedProfile),
                    "Portable profile settings could not be written.");
                FeedbackProfile loadedProfile = PortableProfileStore.LoadFrom(
                    settingsTestPath,
                    out bool recoveredProfile);
                Expect(!recoveredProfile && loadedProfile == savedProfile,
                    "Portable profile settings did not round-trip.");
                Expect(!File.ReadAllText(settingsTestPath).Contains("Enabled", StringComparison.Ordinal),
                    "New portable settings must not persist removed module switches.");

                File.WriteAllText(
                    settingsTestPath,
                    """
                    {
                      "ActivePreset": "Custom",
                      "BrakeResistance": 88,
                      "BrakeResistanceEnabled": true,
                      "AbsPulse": 72,
                      "AbsPulseEnabled": false,
                      "WheelspinPulse": 64,
                      "WheelspinPulseEnabled": false
                    }
                    """);
                FeedbackProfile migratedSwitchProfile = PortableProfileStore.LoadFrom(
                    settingsTestPath,
                    out bool recoveredSwitchProfile);
                Expect(!recoveredSwitchProfile &&
                       migratedSwitchProfile.BrakeResistance == 88 &&
                       migratedSwitchProfile.AbsPulse == 0 &&
                       migratedSwitchProfile.WheelspinPulse == 0 &&
                       migratedSwitchProfile.EngineTexture == FeedbackProfile.DefaultEngineTexture &&
                       migratedSwitchProfile.CollisionFeedback == FeedbackProfile.DefaultCollisionFeedback,
                    "Removed module switches must migrate disabled values to zero.");

                File.WriteAllText(
                    settingsTestPath,
                    """
                    {
                      "ActivePreset": "Custom",
                      "Vibration": "Gentle",
                      "Triggers": "Strong"
                    }
                    """);
                FeedbackProfile migratedProfile = PortableProfileStore.LoadFrom(
                    settingsTestPath,
                    out bool recoveredLegacyProfile);
                Expect(!recoveredLegacyProfile &&
                       migratedProfile.ActivePreset == FeedbackPreset.Custom &&
                       migratedProfile.RoadSurface == 25 &&
                       migratedProfile.GripLoss == 25 &&
                       migratedProfile.NativeAcc == 25 &&
                       migratedProfile.BrakeResistance == 75 &&
                       migratedProfile.AbsPulse == 75 &&
                       migratedProfile.ShiftKick == 75 &&
                       migratedProfile.WheelspinPulse == 75,
                    "Legacy two-choice settings did not migrate to the modular controls.");

                File.WriteAllText(
                    settingsTestPath,
                    """
                    {
                      "ActivePreset": "Custom",
                      "RoadFeel": 0,
                      "GripCues": 31,
                      "TriggerFeel": 100,
                      "AccDetail": 44
                    }
                    """);
                FeedbackProfile migratedSliderProfile = PortableProfileStore.LoadFrom(
                    settingsTestPath,
                    out bool recoveredSliderProfile);
                Expect(!recoveredSliderProfile &&
                       migratedSliderProfile.RoadSurface == 0 &&
                       migratedSliderProfile.GripLoss == 31 &&
                       migratedSliderProfile.NativeAcc == 44 &&
                       migratedSliderProfile.BrakeResistance == 100 &&
                       migratedSliderProfile.ThrottleResistance == 100 &&
                       migratedSliderProfile.AbsPulse == 100 &&
                       migratedSliderProfile.RedlinePulse == 100 &&
                       migratedSliderProfile.TcPulse == 100,
                    "The previous four-slider settings did not migrate to all matching modules.");
            }
            finally
            {
                if (File.Exists(settingsTestPath))
                    File.Delete(settingsTestPath);
                if (File.Exists(settingsTestPath + ".tmp"))
                    File.Delete(settingsTestPath + ".tmp");
            }

            var spatialEngine = new CompetitionFeedbackEngine();
            var spatialTelemetry = telemetry;
            spatialTelemetry.KerbVibration = 0f;
            spatialTelemetry.Gas = 0.35f;
            spatialTelemetry.Brake = 0f;
            spatialTelemetry.Tc = 0f;
            spatialTelemetry.Abs = 0f;
            spatialTelemetry.AbsVibration = 0f;
            spatialTelemetry.SlipVibration = 0f;
            spatialTelemetry.WheelSlipFl = spatialTelemetry.WheelSlipFr = 0f;
            spatialTelemetry.WheelSlipRl = spatialTelemetry.WheelSlipRr = 0f;
            spatialTelemetry.SlipRatioFl = spatialTelemetry.SlipRatioFr = 0f;
            spatialTelemetry.SlipRatioRl = spatialTelemetry.SlipRatioRr = 0f;
            spatialTelemetry.SlipAngleFl = spatialTelemetry.SlipAngleFr = 0f;
            spatialTelemetry.SlipAngleRl = spatialTelemetry.SlipAngleRr = 0f;
            spatialTelemetry.SuspensionFl = spatialTelemetry.SuspensionFr = 0.03f;
            spatialTelemetry.SuspensionRl = spatialTelemetry.SuspensionRr = 0.03f;
            FeedbackFrame steadyRumble = spatialEngine.Compute(
                spatialTelemetry,
                effectStart,
                80,
                45);
            Expect(steadyRumble.LeftActuator == 80 && steadyRumble.RightActuator == 45,
                "A calm telemetry frame must preserve ACC's native engine baseline.");
            FeedbackFrame changingNativeRumble = spatialEngine.Compute(
                spatialTelemetry,
                effectStart + Stopwatch.Frequency / 250,
                112,
                73);
            Expect(changingNativeRumble.LeftActuator == 112 && changingNativeRumble.RightActuator == 73,
                "Calm native road/engine detail must pass through without smoothing or attenuation.");
            spatialTelemetry.KerbVibration = 0.09f;
            int rightDominantFrames = 0;
            int rightIsolatedFrames = 0;
            byte[] frontSignature = new byte[90];
            for (int i = 1; i <= 90; i++)
            {
                spatialTelemetry.SuspensionFr += (i & 1) == 0 ? 0.006f : -0.006f;
                // A real kerb also moves the opposite side through the chassis.
                // This lower-energy movement must not flip spatial ownership.
                if (i % 3 == 0)
                    spatialTelemetry.SuspensionFl += (i & 1) == 0 ? 0.0018f : -0.0018f;
                FeedbackFrame rightFrontKerb = spatialEngine.Compute(
                    spatialTelemetry,
                    effectStart + i * Stopwatch.Frequency / 250,
                    180,
                    0);
                if (rightFrontKerb.RightActuator > rightFrontKerb.LeftActuator)
                    rightDominantFrames++;
                if (i > 20 &&
                    rightFrontKerb.RightActuator >= 24 &&
                    rightFrontKerb.LeftActuator <= Math.Max(4, rightFrontKerb.RightActuator * 0.12f))
                    rightIsolatedFrames++;
                frontSignature[i - 1] = rightFrontKerb.RightActuator;
            }
            Expect(rightDominantFrames >= 72,
                "A sustained right-front kerb must be clearly right-grip dominant.");
            Expect(rightIsolatedFrames >= 55,
                $"A right-front kerb must leave only trace output in the left grip " +
                $"({rightIsolatedFrames}/70 isolated frames).");

            var leftFrontEngine = new CompetitionFeedbackEngine();
            var leftFrontTelemetry = spatialTelemetry;
            leftFrontTelemetry.KerbVibration = 0f;
            leftFrontTelemetry.SuspensionFl = leftFrontTelemetry.SuspensionFr = 0.03f;
            leftFrontTelemetry.SuspensionRl = leftFrontTelemetry.SuspensionRr = 0.03f;
            // Captured ACC sessions can hold a strong native small/right motor.
            // A real left kerb must still take ownership of the left grip.
            leftFrontEngine.Compute(leftFrontTelemetry, effectStart, 0, 160);
            leftFrontTelemetry.KerbVibration = 0.09f;
            int leftDominantFrames = 0;
            for (int i = 1; i <= 90; i++)
            {
                leftFrontTelemetry.SuspensionFl += (i & 1) == 0 ? 0.006f : -0.006f;
                FeedbackFrame leftFrontKerb = leftFrontEngine.Compute(
                    leftFrontTelemetry,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    160);
                if (leftFrontKerb.LeftActuator > leftFrontKerb.RightActuator)
                    leftDominantFrames++;
            }
            Expect(leftDominantFrames >= 72,
                "A left-front kerb must overcome a strong native right-channel background.");

            var rightRearEngine = new CompetitionFeedbackEngine();
            var rightRearTelemetry = spatialTelemetry;
            rightRearTelemetry.KerbVibration = 0f;
            rightRearTelemetry.SuspensionFl = rightRearTelemetry.SuspensionFr = 0.03f;
            rightRearTelemetry.SuspensionRl = rightRearTelemetry.SuspensionRr = 0.03f;
            rightRearEngine.Compute(rightRearTelemetry, effectStart, 80, 0);
            rightRearTelemetry.KerbVibration = 0.09f;
            long axleSignatureDifference = 0;
            for (int i = 1; i <= 90; i++)
            {
                rightRearTelemetry.SuspensionRr += (i & 1) == 0 ? 0.006f : -0.006f;
                FeedbackFrame rightRearKerb = rightRearEngine.Compute(
                    rightRearTelemetry,
                    effectStart + i * Stopwatch.Frequency / 250,
                    180,
                    0);
                axleSignatureDifference += Math.Abs(frontSignature[i - 1] - rightRearKerb.RightActuator);
            }
            Expect(axleSignatureDifference > 40,
                "Front and rear kerb cues must have distinguishable cadence signatures.");

            var grassEngine = new CompetitionFeedbackEngine();
            var rightGrass = spatialTelemetry;
            rightGrass.KerbVibration = 0f;
            rightGrass.GVibration = 1.20f;
            rightGrass.SuspensionFl = rightGrass.SuspensionFr = 0.03f;
            rightGrass.SuspensionRl = rightGrass.SuspensionRr = 0.03f;
            grassEngine.Compute(rightGrass, effectStart, 70, 0);
            FeedbackFrame rightGrassFrame = default;
            for (int i = 1; i <= 80; i++)
            {
                rightGrass.SuspensionRr += (i & 1) == 0 ? 0.006f : -0.006f;
                rightGrassFrame = grassEngine.Compute(
                    rightGrass,
                    effectStart + i * Stopwatch.Frequency / 250,
                    180,
                    0);
            }
            Expect(rightGrassFrame.RightActuator > rightGrassFrame.LeftActuator,
                "Sustained right-side grass must be synthesized in the right grip.");

            var rightFrontSlipEngine = new CompetitionFeedbackEngine();
            var rightFrontSlip = spatialTelemetry;
            rightFrontSlip.KerbVibration = 0f;
            rightFrontSlip.SlipRatioFr = 0.42f;
            rightFrontSlip.WheelSlipFr = 0.62f;
            int rightFrontSlipDominant = 0;
            int rightFrontSlipIsolated = 0;
            byte[] rightFrontSlipSignature = new byte[100];
            for (int i = 0; i < 100; i++)
            {
                FeedbackFrame slipFrame = rightFrontSlipEngine.Compute(
                    rightFrontSlip,
                    effectStart + i * Stopwatch.Frequency / 250);
                if (slipFrame.RightActuator > slipFrame.LeftActuator)
                    rightFrontSlipDominant++;
                if (i >= 20 &&
                    slipFrame.RightActuator >= 20 &&
                    slipFrame.LeftActuator <= Math.Max(4, slipFrame.RightActuator * 0.12f))
                    rightFrontSlipIsolated++;
                rightFrontSlipSignature[i] = slipFrame.RightActuator;
            }
            Expect(rightFrontSlipDominant >= 85,
                "Right-front tyre slip must remain clearly right-grip dominant.");
            Expect(rightFrontSlipIsolated >= 58,
                $"Right-front tyre slip must be strongly isolated from the left grip " +
                $"({rightFrontSlipIsolated}/80 isolated frames).");

            var rightRearSlipEngine = new CompetitionFeedbackEngine();
            var rightRearSlip = spatialTelemetry;
            rightRearSlip.KerbVibration = 0f;
            rightRearSlip.SlipRatioRr = 0.42f;
            rightRearSlip.WheelSlipRr = 0.62f;
            int rightRearSlipDominant = 0;
            long slipAxleSignatureDifference = 0;
            for (int i = 0; i < 100; i++)
            {
                FeedbackFrame slipFrame = rightRearSlipEngine.Compute(
                    rightRearSlip,
                    effectStart + i * Stopwatch.Frequency / 250);
                if (slipFrame.RightActuator > slipFrame.LeftActuator)
                    rightRearSlipDominant++;
                slipAxleSignatureDifference += Math.Abs(
                    rightFrontSlipSignature[i] - slipFrame.RightActuator);
            }
            Expect(rightRearSlipDominant >= 85,
                "Right-rear tyre slip must remain clearly right-grip dominant.");
            Expect(slipAxleSignatureDifference > 80,
                "Front and rear tyre slip must use distinguishable cadence signatures.");

            var leftRearSlipEngine = new CompetitionFeedbackEngine();
            var leftRearSlip = spatialTelemetry;
            leftRearSlip.KerbVibration = 0f;
            leftRearSlip.SlipRatioRl = 0.42f;
            leftRearSlip.WheelSlipRl = 0.62f;
            int leftRearSlipDominant = 0;
            for (int i = 0; i < 100; i++)
            {
                FeedbackFrame slipFrame = leftRearSlipEngine.Compute(
                    leftRearSlip,
                    effectStart + i * Stopwatch.Frequency / 250);
                if (slipFrame.LeftActuator > slipFrame.RightActuator)
                    leftRearSlipDominant++;
            }
            Expect(leftRearSlipDominant >= 85,
                "Left-rear tyre slip must remain clearly left-grip dominant.");

            var centeredSlipEngine = new CompetitionFeedbackEngine();
            var centeredRearSlip = spatialTelemetry;
            centeredRearSlip.KerbVibration = 0f;
            centeredRearSlip.SlipVibration = 1f;
            centeredRearSlip.SlipRatioRl = centeredRearSlip.SlipRatioRr = 0.42f;
            centeredRearSlip.WheelSlipRl = centeredRearSlip.WheelSlipRr = 0.62f;
            centeredSlipEngine.Compute(spatialTelemetry, effectStart, 0, 160);
            int centeredSlipActive = 0;
            int centeredSlipBalanced = 0;
            for (int i = 1; i <= 100; i++)
            {
                FeedbackFrame slipFrame = centeredSlipEngine.Compute(
                    centeredRearSlip,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    160);
                if (Math.Max(slipFrame.LeftActuator, slipFrame.RightActuator) >= 20)
                {
                    centeredSlipActive++;
                    if (Math.Abs(slipFrame.LeftActuator - slipFrame.RightActuator) <= 24)
                        centeredSlipBalanced++;
                }
            }
            Expect(centeredSlipActive >= 35 && centeredSlipBalanced >= centeredSlipActive * 3 / 4,
                $"Symmetric rear slip pulses must not be masked by ACC's strong native right channel " +
                $"({centeredSlipBalanced}/{centeredSlipActive} balanced active frames).");

            var calmFullThrottle = telemetry;
            calmFullThrottle.Gas = 1f;
            calmFullThrottle.Brake = 0f;
            calmFullThrottle.Tc = 0f;
            calmFullThrottle.Abs = 0f;
            calmFullThrottle.TcLevel = 4f;
            calmFullThrottle.AbsLevel = 4f;
            calmFullThrottle.AbsVibration = 0f;
            calmFullThrottle.SlipVibration = 0f;
            calmFullThrottle.SlipRatioFl = calmFullThrottle.SlipRatioFr = 0f;
            calmFullThrottle.SlipRatioRl = calmFullThrottle.SlipRatioRr = 0f;
            calmFullThrottle.SlipAngleFl = calmFullThrottle.SlipAngleFr = 0f;
            calmFullThrottle.SlipAngleRl = calmFullThrottle.SlipAngleRr = 0f;
            calmFullThrottle.Rpm = 5000;
            FeedbackFrame calmFrame = new CompetitionFeedbackEngine().Compute(calmFullThrottle, Stopwatch.GetTimestamp());
            Expect(calmFrame.RightTrigger == throttleBaseline,
                "Full throttle below redline should retain only the light R2 pedal curve.");
            Expect(calmFrame.LeftTrigger.Mode == 0x21, "Normal braking should use full-travel pedal zones.");

            FeedbackTuning engineOnly = offTuning with { EngineTextureScale = 1f };
            var engineTextureTelemetry = calmFullThrottle;
            engineTextureTelemetry.IsEngineRunning = 1;
            engineTextureTelemetry.PacketId = 100;
            engineTextureTelemetry.KerbVibration = 0f;
            engineTextureTelemetry.GVibration = 0f;
            var engineTextureEngine = new CompetitionFeedbackEngine();
            byte engineMidLeft = 0;
            byte engineMidRight = 0;
            for (int i = 0; i < 100; i++)
            {
                engineTextureTelemetry.PacketId++;
                FeedbackFrame engineFrame = engineTextureEngine.Compute(
                    engineTextureTelemetry,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    0,
                    engineOnly);
                engineMidLeft = Math.Max(engineMidLeft, engineFrame.LeftActuator);
                engineMidRight = Math.Max(engineMidRight, engineFrame.RightActuator);
            }
            Expect(engineMidLeft <= 2 && engineMidRight is >= 1 and <= 3,
                $"Mid-RPM engine texture must stay faint ({engineMidLeft}/{engineMidRight}).");

            var idleTexture = engineTextureTelemetry;
            idleTexture.PacketId = 150;
            idleTexture.Rpm = 3037;
            idleTexture.Gas = 0f;
            idleTexture.SpeedKmh = 0f;
            var idleTextureEngine = new CompetitionFeedbackEngine();
            byte engineIdleLeft = 0;
            byte engineIdleRight = 0;
            for (int i = 0; i < 100; i++)
            {
                idleTexture.PacketId++;
                FeedbackFrame engineFrame = idleTextureEngine.Compute(
                    idleTexture,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    0,
                    engineOnly);
                engineIdleLeft = Math.Max(engineIdleLeft, engineFrame.LeftActuator);
                engineIdleRight = Math.Max(engineIdleRight, engineFrame.RightActuator);
            }
            Expect(engineIdleLeft <= 1 && engineIdleRight <= 2,
                $"Stationary zero-throttle idle must remain below the handling cues " +
                $"({engineIdleLeft}/{engineIdleRight}).");

            var highRpmTexture = engineTextureTelemetry;
            highRpmTexture.Rpm = 7985;
            var highRpmTextureEngine = new CompetitionFeedbackEngine();
            byte engineRedlineLeft = 0;
            byte engineRedlineRight = 0;
            for (int i = 0; i < 100; i++)
            {
                highRpmTexture.PacketId++;
                FeedbackFrame engineFrame = highRpmTextureEngine.Compute(
                    highRpmTexture,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    0,
                    engineOnly);
                engineRedlineLeft = Math.Max(engineRedlineLeft, engineFrame.LeftActuator);
                engineRedlineRight = Math.Max(engineRedlineRight, engineFrame.RightActuator);
            }
            Expect(engineRedlineLeft is >= 20 and <= 24 &&
                   engineRedlineRight is >= 26 and <= 30 &&
                   engineRedlineRight - engineRedlineLeft <= 8,
                $"Loaded redline texture must become clearly perceptible in both hands " +
                $"without becoming a dominant cue ({engineRedlineLeft}/{engineRedlineRight}).");

            var coastingRedlineTexture = highRpmTexture;
            coastingRedlineTexture.Gas = 0f;
            coastingRedlineTexture.SpeedKmh = 120f;
            var coastingRedlineEngine = new CompetitionFeedbackEngine();
            byte coastingRedlineLeft = 0;
            byte coastingRedlineRight = 0;
            for (int i = 0; i < 100; i++)
            {
                coastingRedlineTexture.PacketId++;
                FeedbackFrame engineFrame = coastingRedlineEngine.Compute(
                    coastingRedlineTexture,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    0,
                    engineOnly);
                coastingRedlineLeft = Math.Max(coastingRedlineLeft, engineFrame.LeftActuator);
                coastingRedlineRight = Math.Max(coastingRedlineRight, engineFrame.RightActuator);
            }
            Expect(engineRedlineLeft > coastingRedlineLeft &&
                   engineRedlineRight > coastingRedlineRight,
                $"The extra redline presence must require throttle load " +
                $"(loaded {engineRedlineLeft}/{engineRedlineRight}, " +
                $"coasting {coastingRedlineLeft}/{coastingRedlineRight}).");

            int[] engineCurveRpm = [3037, 4800, 6000, 7040, 7520, 7760, 7985];
            byte[] engineCurveLeft = new byte[engineCurveRpm.Length];
            byte[] engineCurveRight = new byte[engineCurveRpm.Length];
            for (int point = 0; point < engineCurveRpm.Length; point++)
            {
                var curveTelemetry = engineTextureTelemetry;
                curveTelemetry.PacketId = 700 + point * 200;
                curveTelemetry.Rpm = engineCurveRpm[point];
                curveTelemetry.Gas = 1f;
                curveTelemetry.SpeedKmh = 80f;
                var curveEngine = new CompetitionFeedbackEngine();
                for (int sampleIndex = 0; sampleIndex < 100; sampleIndex++)
                {
                    curveTelemetry.PacketId++;
                    FeedbackFrame curveFrame = curveEngine.Compute(
                        curveTelemetry,
                        effectStart + (point * 100L + sampleIndex) * Stopwatch.Frequency / 250,
                        0,
                        0,
                        engineOnly);
                    engineCurveLeft[point] = Math.Max(
                        engineCurveLeft[point], curveFrame.LeftActuator);
                    engineCurveRight[point] = Math.Max(
                        engineCurveRight[point], curveFrame.RightActuator);
                }
            }
            for (int point = 1; point < engineCurveRpm.Length; point++)
            {
                Expect(engineCurveLeft[point] >= engineCurveLeft[point - 1] &&
                       engineCurveRight[point] >= engineCurveRight[point - 1],
                    $"Engine texture must rise monotonically with RPM " +
                    $"({engineCurveRpm[point - 1]}: " +
                    $"{engineCurveLeft[point - 1]}/{engineCurveRight[point - 1]}, " +
                    $"{engineCurveRpm[point]}: " +
                    $"{engineCurveLeft[point]}/{engineCurveRight[point]}). ");
            }
            Expect(engineCurveRight[0] <= 1 &&
                   engineCurveRight[3] <= 4 &&
                   engineCurveLeft[^1] >= 20 &&
                   engineCurveRight[^1] >= 26,
                $"Engine curve must remain quiet below the redline and clearly rise at its end " +
                $"({string.Join(", ", engineCurveRpm.Zip(
                    engineCurveLeft.Zip(engineCurveRight),
                    static (rpm, output) => $"{rpm}:{output.First}/{output.Second}"))}).");

            FeedbackTuning collisionOnly = offTuning with { CollisionFeedbackScale = 1f };
            var frontImpact = calmFullThrottle;
            frontImpact.IsEngineRunning = 0;
            frontImpact.Rpm = 0;
            frontImpact.Gas = 0f;
            frontImpact.SpeedKmh = 40f;
            frontImpact.PacketId = 200;
            frontImpact.LocalVelocityZ = 22f;
            frontImpact.GForceX = frontImpact.GForceY = frontImpact.GForceZ = 0f;
            frontImpact.KerbVibration = 0f;
            frontImpact.GVibration = 0f;
            var frontImpactEngine = new CompetitionFeedbackEngine();
            frontImpactEngine.Compute(frontImpact, effectStart, 0, 0, collisionOnly);
            frontImpact.PacketId++;
            frontImpact.GForceZ = -58f;
            frontImpact.CarDamageFront = 0.75f;
            FeedbackFrame frontImpactFrame = frontImpactEngine.Compute(
                frontImpact,
                effectStart + Stopwatch.Frequency / 250,
                0,
                0,
                collisionOnly);
            Expect(frontImpactFrame.LeftActuator >= 130 && frontImpactFrame.RightActuator >= 130 &&
                   Math.Abs(frontImpactFrame.LeftActuator - frontImpactFrame.RightActuator) <= 4,
                $"A front collision must create a clear centered hit " +
                $"({frontImpactFrame.LeftActuator}/{frontImpactFrame.RightActuator}).");

            var leftImpact = frontImpact;
            leftImpact.PacketId = 300;
            leftImpact.CarDamageFront = 0f;
            leftImpact.GForceX = leftImpact.GForceZ = 0f;
            var leftImpactEngine = new CompetitionFeedbackEngine();
            leftImpactEngine.Compute(leftImpact, effectStart, 0, 0, collisionOnly);
            leftImpact.PacketId++;
            leftImpact.GForceX = -13f;
            leftImpact.GForceZ = -37f;
            leftImpact.CarDamageFront = 0.75f;
            FeedbackFrame leftImpactFrame = leftImpactEngine.Compute(
                leftImpact,
                effectStart + Stopwatch.Frequency / 250,
                0,
                0,
                collisionOnly);
            Expect(leftImpactFrame.LeftActuator >= leftImpactFrame.RightActuator + 35,
                $"A left-side collision must be left dominant " +
                $"({leftImpactFrame.LeftActuator}/{leftImpactFrame.RightActuator}).");

            var rightImpact = leftImpact;
            rightImpact.PacketId = 400;
            rightImpact.CarDamageFront = 0f;
            rightImpact.GForceX = rightImpact.GForceZ = 0f;
            var rightImpactEngine = new CompetitionFeedbackEngine();
            rightImpactEngine.Compute(rightImpact, effectStart, 0, 0, collisionOnly);
            rightImpact.PacketId++;
            rightImpact.GForceX = 8f;
            rightImpact.GForceZ = -2f;
            rightImpact.CarDamageRight = 0.75f;
            FeedbackFrame rightImpactFrame = default;
            for (int i = 1; i <= 4; i++)
            {
                rightImpact.PacketId++;
                rightImpactFrame = rightImpactEngine.Compute(
                    rightImpact,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    0,
                    collisionOnly);
            }
            Expect(rightImpactFrame.RightActuator >= rightImpactFrame.LeftActuator + 70,
                $"A right-side collision must be right dominant " +
                $"({rightImpactFrame.LeftActuator}/{rightImpactFrame.RightActuator}).");

            var rightScrape = rightImpact;
            rightScrape.PacketId = 450;
            rightScrape.CarDamageRight = 0f;
            rightScrape.GForceX = rightScrape.GForceZ = 0f;
            var rightScrapeEngine = new CompetitionFeedbackEngine();
            rightScrapeEngine.Compute(rightScrape, effectStart, 0, 0, collisionOnly);
            rightScrape.PacketId++;
            rightScrape.CarDamageRight = 0.50f;
            rightScrape.GForceX = 4f;
            rightScrape.GForceZ = -1f;
            FeedbackFrame rightScrapeFrame = rightScrapeEngine.Compute(
                rightScrape,
                effectStart + Stopwatch.Frequency / 250,
                0,
                0,
                collisionOnly);
            Expect(rightScrapeFrame.RightActuator >= 45 &&
                   rightScrapeFrame.RightActuator >= rightScrapeFrame.LeftActuator + 25,
                $"A damage-producing side scrape must create an immediate directional texture " +
                $"({rightScrapeFrame.LeftActuator}/{rightScrapeFrame.RightActuator}).");

            var grassStrike = frontImpact;
            grassStrike.PacketId = 500;
            grassStrike.CarDamageFront = 0f;
            grassStrike.GForceX = grassStrike.GForceY = grassStrike.GForceZ = 0f;
            grassStrike.GVibration = 1f;
            var grassStrikeEngine = new CompetitionFeedbackEngine();
            for (int i = 0; i < 30; i++)
            {
                grassStrike.PacketId++;
                grassStrikeEngine.Compute(
                    grassStrike,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    0,
                    collisionOnly);
            }
            grassStrike.PacketId++;
            grassStrike.GForceX = 8.84f;
            grassStrike.GForceZ = -10.96f;
            grassStrike.LocalVelocityZ = 18.4f;
            FeedbackFrame grassStrikeFrame = grassStrikeEngine.Compute(
                grassStrike,
                effectStart + 30 * Stopwatch.Frequency / 250,
                0,
                0,
                collisionOnly);
            for (int i = 31; i < 58; i++)
            {
                grassStrike.PacketId++;
                grassStrike.GForceX = 0f;
                grassStrike.GForceZ = 0f;
                grassStrike.LocalVelocityZ = 17.3f;
                grassStrikeFrame = grassStrikeEngine.Compute(
                    grassStrike,
                    effectStart + i * Stopwatch.Frequency / 250,
                    0,
                    0,
                    collisionOnly);
                Expect(grassStrikeFrame.LeftActuator == 0 && grassStrikeFrame.RightActuator == 0,
                    "A captured grass strike must not be promoted to collision feedback.");
            }

            var damageOffImpact = grassStrike;
            damageOffImpact.PacketId = 600;
            damageOffImpact.GVibration = 0f;
            damageOffImpact.GForceX = damageOffImpact.GForceY = damageOffImpact.GForceZ = 0f;
            damageOffImpact.LocalVelocityZ = 22f;
            var damageOffImpactEngine = new CompetitionFeedbackEngine();
            damageOffImpactEngine.Compute(damageOffImpact, effectStart, 0, 0, collisionOnly);
            damageOffImpact.PacketId++;
            damageOffImpact.GForceZ = -60f;
            FeedbackFrame damageOffImpactFrame = damageOffImpactEngine.Compute(
                damageOffImpact,
                effectStart + Stopwatch.Frequency / 250,
                0,
                0,
                collisionOnly);
            Expect(damageOffImpactFrame.LeftActuator >= 160 && damageOffImpactFrame.RightActuator >= 160,
                "An extreme collision must remain detectable with vehicle damage disabled.");

            FeedbackFrame impactFinished = damageOffImpactEngine.Compute(
                damageOffImpact,
                effectStart + (long)(0.210 * Stopwatch.Frequency),
                0,
                0,
                collisionOnly);
            Expect(impactFinished.LeftActuator == 0 && impactFinished.RightActuator == 0,
                "Collision feedback must fully decay instead of becoming a continuous vibration.");

            // ACC can report a non-zero/high WheelSlip value during ordinary
            // rolling. Without ratio or angle corroboration it must not create
            // a continuous grip-loss vibration.
            var rollingNoise = calmFullThrottle;
            rollingNoise.Gas = 0.35f;
            rollingNoise.KerbVibration = 0f;
            rollingNoise.GVibration = 0f;
            rollingNoise.WheelSlipFl = rollingNoise.WheelSlipFr = 4.0f;
            rollingNoise.WheelSlipRl = rollingNoise.WheelSlipRr = 4.0f;
            var rollingNoiseEngine = new CompetitionFeedbackEngine();
            byte maximumRollingOutput = 0;
            for (int i = 0; i < 100; i++)
            {
                FeedbackFrame rollingFrame = rollingNoiseEngine.Compute(
                    rollingNoise,
                    effectStart + i * Stopwatch.Frequency / 250);
                maximumRollingOutput = Math.Max(
                    maximumRollingOutput,
                    Math.Max(rollingFrame.LeftActuator, rollingFrame.RightActuator));
            }
            Expect(maximumRollingOutput <= 4,
                "WheelSlip alone must not masquerade as continuous tyre slip.");

            // Values taken from the user's ACC capture: these are ordinary
            // loaded-corner readings and must not saturate the tyre layer.
            var capturedNormalCorner = rollingNoise;
            capturedNormalCorner.WheelSlipFl = 2.26f;
            capturedNormalCorner.WheelSlipFr = 2.26f;
            capturedNormalCorner.WheelSlipRl = 1.10f;
            capturedNormalCorner.WheelSlipRr = 1.14f;
            capturedNormalCorner.SlipRatioFl = 0.023f;
            capturedNormalCorner.SlipRatioFr = 0.030f;
            capturedNormalCorner.SlipRatioRl = 0.047f;
            capturedNormalCorner.SlipRatioRr = 0.047f;
            capturedNormalCorner.SlipAngleFl = 0.167f;
            capturedNormalCorner.SlipAngleFr = 0.167f;
            capturedNormalCorner.SlipAngleRl = 0.073f;
            capturedNormalCorner.SlipAngleRr = 0.073f;
            capturedNormalCorner.SlipVibration = 0.062f;
            var capturedNormalEngine = new CompetitionFeedbackEngine();
            byte maximumCapturedNormalOutput = 0;
            for (int i = 0; i < 100; i++)
            {
                FeedbackFrame normalFrame = capturedNormalEngine.Compute(
                    capturedNormalCorner,
                    effectStart + i * Stopwatch.Frequency / 250);
                maximumCapturedNormalOutput = Math.Max(
                    maximumCapturedNormalOutput,
                    Math.Max(normalFrame.LeftActuator, normalFrame.RightActuator));
            }
            Expect(maximumCapturedNormalOutput <= 6,
                "Captured normal-corner tyre values must not create a synthetic tyre layer.");

            // A car/compound with naturally higher rolling and cornering slip
            // must establish its own quiet baseline, while a subsequent real
            // loss of grip remains immediate and spatially readable.
            var highNaturalSlip = rollingNoise;
            highNaturalSlip.SlipVibration = 0.05f;
            highNaturalSlip.SlipRatioFl = highNaturalSlip.SlipRatioFr = 0.10f;
            highNaturalSlip.SlipRatioRl = highNaturalSlip.SlipRatioRr = 0.13f;
            highNaturalSlip.SlipAngleFl = highNaturalSlip.SlipAngleFr = 0.20f;
            highNaturalSlip.SlipAngleRl = highNaturalSlip.SlipAngleRr = 0.15f;
            var adaptiveSlipEngine = new CompetitionFeedbackEngine();
            byte maximumAdaptiveBaseline = 0;
            for (int i = 0; i < 120; i++)
            {
                FeedbackFrame normalFrame = adaptiveSlipEngine.Compute(
                    highNaturalSlip,
                    effectStart + i * Stopwatch.Frequency / 250);
                maximumAdaptiveBaseline = Math.Max(
                    maximumAdaptiveBaseline,
                    Math.Max(normalFrame.LeftActuator, normalFrame.RightActuator));
            }
            Expect(maximumAdaptiveBaseline <= 6,
                "A high-slip car's learned normal baseline must remain quiet.");

            var adaptiveRightRearLoss = highNaturalSlip;
            adaptiveRightRearLoss.SlipVibration = 1f;
            adaptiveRightRearLoss.SlipRatioRr = 0.52f;
            adaptiveRightRearLoss.SlipAngleRr = 0.40f;
            int adaptiveLossRightDominant = 0;
            byte maximumAdaptiveLoss = 0;
            for (int i = 120; i < 220; i++)
            {
                FeedbackFrame lossFrame = adaptiveSlipEngine.Compute(
                    adaptiveRightRearLoss,
                    effectStart + i * Stopwatch.Frequency / 250);
                if (lossFrame.RightActuator > lossFrame.LeftActuator)
                    adaptiveLossRightDominant++;
                maximumAdaptiveLoss = Math.Max(
                    maximumAdaptiveLoss,
                    Math.Max(lossFrame.LeftActuator, lossFrame.RightActuator));
            }
            Expect(adaptiveLossRightDominant >= 85 && maximumAdaptiveLoss >= 35,
                "Adaptive calibration must not learn away a real right-rear grip loss.");

            var lockedWithAbsOff = calmFullThrottle;
            lockedWithAbsOff.Gas = 0f;
            lockedWithAbsOff.Brake = 0.86f;
            lockedWithAbsOff.AbsLevel = 0f;
            lockedWithAbsOff.SlipRatioFl = 0.52f;
            lockedWithAbsOff.SlipRatioFr = 0.48f;
            var lockEngine = new CompetitionFeedbackEngine();
            bool lockPulseSeen = false;
            bool strongLockPulseSeen = false;
            for (int i = 0; i < 100; i++)
            {
                FeedbackFrame lockFrame = lockEngine.Compute(
                    lockedWithAbsOff,
                    effectStart + i * Stopwatch.Frequency / 250);
                lockPulseSeen |= lockFrame.LeftTrigger.Mode == 0x26;
                strongLockPulseSeen |= lockFrame.LeftTrigger == TriggerEffect.Vibration(2, 6, 8);
            }
            Expect(lockPulseSeen,
                "Wheel lock with ABS disabled must still pulse the brake trigger.");
            Expect(strongLockPulseSeen,
                "Wheel lock with ABS disabled must reach the stronger low-frequency pulse.");

            var redlineTelemetry = calmFullThrottle;
            redlineTelemetry.Rpm = 7985;
            var redlineEngine = new CompetitionFeedbackEngine();
            bool redlinePulseSeen = false;
            bool strongRedlinePulseSeen = false;
            for (int i = 0; i < 100; i++)
            {
                FeedbackFrame redlineFrame = redlineEngine.Compute(
                    redlineTelemetry,
                    effectStart + i * Stopwatch.Frequency / 250);
                redlinePulseSeen |= redlineFrame.RightTrigger.Mode == 0x26;
                strongRedlinePulseSeen |= redlineFrame.RightTrigger == TriggerEffect.Vibration(1, 4, 18);
            }
            Expect(redlinePulseSeen, "Redline must produce a real R2 vibration effect.");
            Expect(strongRedlinePulseSeen, "Redline must reach the stronger R2 pulse.");

            var shiftTelemetry = calmFullThrottle;
            shiftTelemetry.Gear = 3;
            var shiftEngine = new CompetitionFeedbackEngine();
            shiftEngine.Compute(shiftTelemetry, effectStart);
            shiftTelemetry.Gear = 4;
            bool shiftPulseSeen = false;
            bool strongShiftPulseSeen = false;
            for (int i = 1; i < 70; i++)
            {
                FeedbackFrame shiftFrame = shiftEngine.Compute(
                    shiftTelemetry,
                    effectStart + i * Stopwatch.Frequency / 250);
                shiftPulseSeen |= shiftFrame.RightTrigger.Mode == 0x26;
                strongShiftPulseSeen |= shiftFrame.RightTrigger == TriggerEffect.Vibration(1, 5, 28);
            }
            Expect(shiftPulseSeen, "Upshifts must produce a short R2 vibration effect.");
            Expect(strongShiftPulseSeen, "Upshifts must reach the stronger recoil pulse.");

            Span<byte> usb = stackalloc byte[DualSenseReportBuilder.UsbReportLength];
            int usbLength = DualSenseReportBuilder.Build(frame, false, usb);
            Expect(usbLength == 48 && usb[0] == 0x02 && usb[1] == 0x0F && usb[2] == 0x55,
                "USB report header is invalid.");
            Expect(usb[3] == frame.RightActuator && usb[4] == frame.LeftActuator,
                "USB report does not carry the merged ACC motor values.");
            Expect(usb[11] == frame.RightTrigger.Mode && usb[22] == frame.LeftTrigger.Mode,
                "USB trigger offsets are invalid.");
            Expect(usb[39] == 0x06 && usb[42] == 0x02 && usb[43] == 0x02,
                "USB report does not select DS4Windows-compatible Accurate rumble.");

            Span<byte> bluetooth = stackalloc byte[DualSenseReportBuilder.BluetoothReportLength];
            int btLength = DualSenseReportBuilder.Build(frame, true, bluetooth);
            Expect(btLength == 78 && bluetooth[0] == 0x31 && bluetooth[1] == 0x02 &&
                   bluetooth[2] == 0x0F && bluetooth[3] == 0x55,
                "Bluetooth report header is invalid.");
            Expect(bluetooth[4] == frame.RightActuator && bluetooth[5] == frame.LeftActuator,
                "Bluetooth report does not carry the merged ACC motor values.");
            Expect(bluetooth[40] == 0x06 && bluetooth[43] == 0x02 && bluetooth[44] == 0x02,
                "Bluetooth report does not select DS4Windows-compatible Accurate rumble.");
            uint expectedCrc = Crc32.ComputeBluetoothOutput(bluetooth[..74]);
            uint actualCrc = (uint)(bluetooth[74] | bluetooth[75] << 8 | bluetooth[76] << 16 | bluetooth[77] << 24);
            Expect(expectedCrc == actualCrc, "Bluetooth CRC is invalid.");

            string captureTestPath = Path.Combine(
                Path.GetTempPath(),
                $"ACCDualSenseFeedback-capture-self-test-{Guid.NewGuid():N}.csv");
            try
            {
                var captureTelemetry = telemetry;
                long firstCaptureTimestamp = Stopwatch.GetTimestamp();
                captureTelemetry.ObservedTimestamp = firstCaptureTimestamp;
                captureTelemetry.IsEngineRunning = 1;
                captureTelemetry.CarDamageFront = 0.125f;
                captureTelemetry.FxFr = 4210.5f;
                captureTelemetry.ContactNormalRrY = 0.998f;
                using (var capture = new TelemetryCsvCapture(captureTestPath))
                {
                    capture.TryWrite(captureTelemetry, 73, 41, frame);
                    capture.TryWrite(captureTelemetry, 73, 41, frame);
                    captureTelemetry.PacketId++;
                    captureTelemetry.ObservedTimestamp = firstCaptureTimestamp + Stopwatch.Frequency / 400;
                    capture.TryWrite(captureTelemetry, 73, 41, frame);
                }

                string[] captureLines = File.ReadAllLines(captureTestPath);
                Expect(captureLines.Length == 3,
                    "Capture must write every unique high-rate telemetry packet exactly once.");
                int captureColumns = captureLines[0].Split(',').Length;
                Expect(captureLines[1].Split(',').Length == captureColumns &&
                       captureLines[2].Split(',').Length == captureColumns,
                    "Capture CSV header and data rows have different column counts.");
                Expect(captureLines[0].Contains("sample_dt_ms", StringComparison.Ordinal) &&
                       captureLines[0].Contains("damage_front", StringComparison.Ordinal) &&
                       captureLines[0].Contains("fx_fl", StringComparison.Ordinal) &&
                       captureLines[0].Contains("contact_normal_rr_z", StringComparison.Ordinal),
                    "Capture CSV is missing extended collision or four-wheel fields.");
            }
            finally
            {
                if (File.Exists(captureTestPath))
                    File.Delete(captureTestPath);
            }

            const int iterations = 250_000;
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
                frame = engine.Compute(telemetry, started + i);
            double elapsedUs = (Stopwatch.GetTimestamp() - started) * 1_000_000.0 / Stopwatch.Frequency;
            double perCallUs = elapsedUs / iterations;
            Expect(perCallUs < 50.0, $"Feedback computation is unexpectedly slow: {perCallUs:F3} µs.");

            var diagnosticContext = new DiagnosticReportContext(
                "self-test",
                "Test failure",
                "Diagnostic report verification",
                false,
                false,
                false,
                false,
                null,
                FeedbackProfile.Default,
                new RuntimeDiagnosticException(
                    "SELF_TEST_DIAGNOSTIC",
                    "Synthetic diagnostic failure.",
                    new InvalidOperationException("Synthetic inner failure.")),
                DateTimeOffset.Now,
                new[] { "00:00:00  Synthetic event" });
            string diagnosticReport = DiagnosticReportBuilder.Build(diagnosticContext);
            Expect(diagnosticReport.Contains("Report format: 2", StringComparison.Ordinal),
                "Diagnostic report format marker is missing.");
            Expect(diagnosticReport.Contains("Diagnostic code: SELF_TEST_DIAGNOSTIC", StringComparison.Ordinal),
                "Diagnostic report did not preserve the structured error code.");
            Expect(diagnosticReport.Contains("System.InvalidOperationException", StringComparison.Ordinal),
                "Diagnostic report did not preserve the inner exception type.");
            Expect(diagnosticReport.Contains("[Driver checks]", StringComparison.Ordinal),
                "Diagnostic report driver checks are missing.");

            var missingEnvironment = new DiagnosticEnvironmentSnapshot(
                "missing",
                "missing",
                "not installed",
                "not found",
                "unknown",
                "unknown",
                "unknown",
                "unknown",
                null,
                Array.Empty<DiagnosticDualSenseProbe>());
            string missingEnvironmentReport = DiagnosticReportBuilder.Build(
                diagnosticContext with
                {
                    Status = "Feedback unavailable",
                    StatusDetail = "Check ViGEmBus, USB and HidHide.",
                    LastError = new RuntimeDiagnosticException(
                        "VIGEM_VIRTUAL_CONTROLLER_START_FAILED",
                        "The ViGEm virtual Xbox 360 controller could not be started.")
                },
                missingEnvironment);
            Expect(missingEnvironmentReport.Contains(
                    "ViGEmBus service registration: missing",
                    StringComparison.Ordinal) &&
                   missingEnvironmentReport.Contains(
                    "HidHide service registration: missing",
                    StringComparison.Ordinal) &&
                   missingEnvironmentReport.Contains(
                    "Visible and openable gamepad interfaces: 0",
                    StringComparison.Ordinal) &&
                   missingEnvironmentReport.Contains(
                    "Diagnostic code: VIGEM_VIRTUAL_CONTROLLER_START_FAILED",
                    StringComparison.Ordinal),
                "Missing-driver diagnostic scenario is incomplete.");

            var hiddenControllerEnvironment = new DiagnosticEnvironmentSnapshot(
                "present; start=1; driver file present=True",
                "present; start=3; driver file present=True",
                "1.5.230",
                "available",
                "on",
                "off",
                "False",
                "1",
                null,
                Array.Empty<DiagnosticDualSenseProbe>());
            string hiddenControllerReport = DiagnosticReportBuilder.Build(
                diagnosticContext with
                {
                    Status = "DualSense unavailable",
                    StatusDetail = "Connect by USB. Check HidHide access.",
                    LastError = new RuntimeDiagnosticException(
                        "DUALSENSE_USB_INPUT_NOT_VISIBLE",
                        "No visible USB DualSense was found.")
                },
                hiddenControllerEnvironment);
            Expect(hiddenControllerReport.Contains("HidHide cloaking: on", StringComparison.Ordinal) &&
                   hiddenControllerReport.Contains(
                    "This executable listed in HidHide applications: False",
                    StringComparison.Ordinal) &&
                   hiddenControllerReport.Contains("HidHide hidden-device entries: 1", StringComparison.Ordinal) &&
                   hiddenControllerReport.Contains(
                    "Diagnostic code: DUALSENSE_USB_INPUT_NOT_VISIBLE",
                    StringComparison.Ordinal),
                "HidHide-blocked-controller diagnostic scenario is incomplete.");

            var visibleControllerEnvironment = hiddenControllerEnvironment with
            {
                CurrentExecutableListed = "True",
                DualSenseDevices = new[]
                {
                    new DiagnosticDualSenseProbe(0x0CE6, "USB", 64, 48)
                }
            };
            string visibleControllerReport = DiagnosticReportBuilder.Build(
                diagnosticContext with
                {
                    Status = "Ready for ACC",
                    StatusDetail = "DualSense connected. Start ACC when ready.",
                    LastError = null,
                    LastErrorAt = null
                },
                visibleControllerEnvironment);
            Expect(visibleControllerReport.Contains(
                    "This executable listed in HidHide applications: True",
                    StringComparison.Ordinal) &&
                   visibleControllerReport.Contains(
                    "Visible and openable gamepad interfaces: 1",
                    StringComparison.Ordinal) &&
                   visibleControllerReport.Contains(
                    "Device 1: PID=0x0CE6; transport=USB; input report=64; output report=48",
                    StringComparison.Ordinal),
                "Whitelisted visible-controller diagnostic scenario is incomplete.");

            Console.WriteLine("Self-test passed.");
            Console.WriteLine($"ACC physics layout: {Marshal.SizeOf<AccPhysicsPage>()} bytes");
            Console.WriteLine($"Feedback compute mean: {perCallUs:F3} µs over {iterations:N0} iterations");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Self-test failed: {exception.Message}");
            return 1;
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
