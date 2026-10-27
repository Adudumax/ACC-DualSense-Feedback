using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACCDualSenseFeedback.Gyro;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Lighting;
using ACCDualSenseFeedback.Ui;

internal static class GyroUiChecks
{
    // Program.RenderUi supplies a detached tree. Never Show the Window: Loaded starts HID/runtime.
    public static void Render(string directory, MainWindow window, FrameworkElement root)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo profileField = typeof(MainWindow).GetField("_profile", flags)!;
        var originalProfile = (FeedbackProfile)profileField.GetValue(window)!;
        var profileState = (FeedbackProfileState)typeof(MainWindow).GetField("_profileState", flags)!.GetValue(window)!;
        var lightState = (RpmLightSettingsState)typeof(MainWindow).GetField("_lightSettingsState", flags)!.GetValue(window)!;
        RpmLightSettings originalLight = lightState.Current;
        GyroSteeringSettings originalGyro = GyroSettingsState.Current;
        GyroRuntimeStatus originalStatus = GyroControlState.Current;
        var savedGyro = originalGyro with
        {
            Enabled = true, FullLockDegrees = 60, DeadzoneDegrees = 1.4f,
            Curve = GyroResponseCurve.Linear, Smoothing = GyroSmoothing.Light,
            StickPriority = true, ActivationMode = GyroActivationMode.Always
        };
        var settingsPage = Required<Grid>("SettingsPanelContent");
        var gyroPage = Required<Grid>("GyroPanelContent");
        var settingsScroll = Required<ScrollViewer>("SettingsScrollViewer");
        var gyroScroll = Required<ScrollViewer>("GyroScrollViewer");
        var switchPage = Required<AnimatedSwitch>("GyroPageEnabledSwitch");
        var ready = new GyroRuntimeStatus
        {
            State = GyroRuntimeState.Active, Message = "Gyro active",
            InputValid = true, ImuValid = true, FactoryCalibrated = true,
            StationaryCalibrated = true, NeedsRecenter = false, Ready = true,
            LearnedSequence = originalStatus.LearnedSequence, AngleDegrees = 45, OutputX = 44f / 89,
            SteeringPreview = new(true, 45, 44f / 89, 44f / 89)
        };

        T Required<T>(string name) where T : class => window.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing {typeof(T).Name} {name}");
        void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Invoke(string method, params object[] args)
            => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
        void CancelSave()
        {
            // UI actions are real event paths, but synthetic QA choices must never reach disk.
            ((CancellationTokenSource?)typeof(MainWindow).GetField("_gyroSaveCancellation", flags)!.GetValue(window))?.Cancel();
        }
        void Layout()
        {
            root.Measure(new Size(920, 580));
            root.Arrange(new Rect(0, 0, 920, 580));
            root.UpdateLayout();
        }
        void Pump(int milliseconds = 210)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Layout();
        }
        void Preset(FeedbackPreset preset)
        {
            FeedbackProfile next = originalProfile with { ActivePreset = preset };
            profileField.SetValue(window, next);
            profileState.Update(next);
            Invoke("ApplyProfileVisuals", false);
            Layout();
        }
        void Page(Grid page)
        {
            Invoke("SetNavigationState", page);
            Invoke("CompletePageTransition", page);
            Layout();
        }
        void Tab(int index)
        {
            Invoke("SetGyroTab", index, false);
            Layout();
        }
        void Settings(GyroSteeringSettings settings)
        {
            GyroSettingsState.Current = settings;
            Invoke("UpdateGyroControls");
            Layout();
        }
        void Status(GyroRuntimeStatus status)
        {
            GyroControlState.Publish(status);
            Invoke("UpdateGyroRuntime", status);
            Layout();
        }
        void Capture(string name, double scale = 1)
        {
            CancelSave();
            Pump();
            // Detached visual trees have no presentation clock; snapshot settled production states.
            foreach (AnimatedSwitch toggle in Descendants<AnimatedSwitch>(root))
                typeof(AnimatedSwitch).GetMethod("UpdateVisual", flags)!.Invoke(toggle, [false]);
            var bitmap = new RenderTargetBitmap((int)(920 * scale), (int)(580 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name));
            encoder.Save(output);
        }
        bool PreviewSubscribed() => (bool)typeof(MainWindow).GetField("_gyroPreviewSubscribed", flags)!.GetValue(window)!;
        bool Presented(FrameworkElement element)
        {
            for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            return true;
        }

        try
        {
            Status(ready);
            Settings(savedGyro);
            lightState.Update(originalLight with { Enabled = false, Mode = RpmLightMode.Discrete });
            Assert(window.FindName("GyroEnabledSwitch") is null, "advanced entry still contains a second master switch");
            Assert(window.FindName("GyroMoreSettings") is null, "nested More settings remains");
            Assert(window.FindName("GyroRecenterBindingButton") is null && window.FindName("GyroForwardButtonsSwitch") is null,
                "removed recenter/forward shortcut controls remain");
            Assert(Descendants<Button>(Required<Grid>("GyroTabsHost")).Count() == 3, "gyro needs three sibling tabs");
            Assert(Descendants<AnimatedSwitch>(gyroPage).Count(t => t.Name == "GyroPageEnabledSwitch") == 1,
                "gyro page has no unique master switch");

            Preset(FeedbackPreset.Default);
            Page(settingsPage);
            settingsScroll.ScrollToEnd(); Layout();
            var lights = Required<AnimatedSwitch>("LightsEnabledSwitch");
            Assert(!lights.IsEnabled && lights.IsChecked == true, "Default must show disabled lighting on");
            Assert(!Required<Grid>("LightModeOptions").IsEnabled, "Default must lock lighting mode");
            Assert(GyroSettingsState.Current == savedGyro && !lightState.Current.Enabled, "Default overwrote saved Custom choices");
            Capture("gyro-advanced-default.png");

            Page(gyroPage); Tab(0);
            Assert(!switchPage.IsEnabled && switchPage.IsChecked == false, "Default gyro page bypasses preset");
            Assert(!Required<Slider>("GyroFullLockSlider").IsEnabled, "Default permits gyro edits");
            Capture("gyro-default.png");

            Preset(FeedbackPreset.Custom);
            Assert(switchPage.IsEnabled && switchPage.IsChecked == true, "Custom did not restore enabled gyro");
            Assert(lights.IsEnabled && lights.IsChecked == false && lightState.Current.Mode == RpmLightMode.Discrete,
                "Custom did not restore saved lighting");
            var angleSlider = Required<Slider>("GyroFullLockSlider");
            Assert(angleSlider.Value == 60, "Custom lost gyro angle");
            Assert(Required<Grid>("GyroExponentRow").Visibility == Visibility.Collapsed, "linear curve exposes exponent");
            Assert(Required<Grid>("GyroSmoothingTimeRow").Visibility == Visibility.Collapsed, "preset smoothing exposes milliseconds");
            Assert(Presented(angleSlider) && !Presented(Required<Button>("GyroActivationChoice")), "feel tab includes operation settings");
            Assert(!PreviewSubscribed(), "feel tab starts a preview subscription");
            Capture("gyro-feel.png"); Capture("gyro-feel-150.png", 1.5);

            Tab(1);
            Assert(Presented(Required<Button>("GyroActivationChoice")) && !Presented(angleSlider), "operation tab shows another partition");
            Assert(Presented(Required<Button>("GyroActivationBindingButton")), "Continuous activation must retain the enable/resume binding");
            Assert(Descendants<TextBlock>(Required<Grid>("GyroActivationBindingRow")).Any(text =>
                text.ToolTip?.ToString() == UiText.Get("If gyro stops responding or becomes locked, try pressing the bound button to resume. Right-click to clear the binding.")),
                "binding hover help does not explain recovery with the bound button");
            Assert(Descendants<TextBlock>(Required<Grid>("GyroActivationBindingRow")).Any(text => text.Text == UiText.Get("Gyro shortcut only")),
                "binding's existing inline helper was replaced by an unsolicited reminder");
            Assert(Required<Grid>("GyroTakeoverRow").Visibility == Visibility.Visible, "stick priority hides takeover threshold");
            Assert(!PreviewSubscribed(), "operation tab starts a preview subscription");
            Capture("gyro-operation.png");
            Settings(savedGyro with { ActivationButton = GyroButton.Mute });
            Assert(Required<Button>("GyroActivationBindingButton").Content?.ToString() == UiText.Get("Mute button"),
                "learned mute button has no readable binding label");
            Capture("gyro-mute-binding.png");
            Settings(savedGyro with { ActivationButton = GyroButton.EdgePaddleLeft });
            Assert(Required<Button>("GyroActivationBindingButton").Content?.ToString() == UiText.Get("Left rear button"),
                "learned Edge rear button has no readable binding label");
            Settings(savedGyro);
            Tab(0);
            Assert(ReferenceEquals(angleSlider, Required<Slider>("GyroFullLockSlider")), "tab switch recreates persistent controls");
            Button[] tabs = Descendants<Button>(Required<Grid>("GyroTabsHost")).ToArray();
            foreach (int index in new[] { 1, 2, 0 }) tabs[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert(Presented(angleSlider) && !Presented(Required<Button>("GyroActivationChoice")) &&
                !Presented(Required<TextBlock>("GyroCalibrationStep1State")), "rapid tab changes leave stale content visible");

            Settings(savedGyro with { Curve = GyroResponseCurve.Custom, Smoothing = GyroSmoothing.Custom, StickPriority = false });
            Assert(Required<Grid>("GyroExponentRow").Visibility == Visibility.Visible, "custom curve hides exponent");
            Assert(Required<Grid>("GyroSmoothingTimeRow").Visibility == Visibility.Visible, "custom smoothing hides milliseconds");
            Assert(Required<Grid>("GyroTakeoverRow").Visibility == Visibility.Collapsed, "gyro-only mode exposes takeover threshold");
            gyroScroll.ScrollToTop(); Layout(); Capture("gyro-feel-custom.png");
            gyroScroll.ScrollToEnd(); Layout(); Capture("gyro-feel-custom-bottom.png");
            Assert(switchPage.ActualWidth >= 44 && switchPage.ActualHeight >= 44, "gyro switch target is too small");
            Assert(Required<Grid>("GyroFullLockRow").ActualWidth <= 534, "gyro controls overflow left pane");

            Settings(savedGyro with { FullLockDegrees = 90, DeadzoneDegrees = 1 });
            Tab(2);
            var test = Required<Expander>("GyroTestSteering");
            test.IsExpanded = true; Layout();
            Assert(PreviewSubscribed(), "calibration test has no preview subscription");
            Status(ready with { State = GyroRuntimeState.Suspended, Message = "Gyro suspended", OutputX = 0 });
            Assert(Required<TextBlock>("GyroSteeringAngle").Text.Contains("45"), "test angle does not use sensor preview");
            Assert(Required<TextBlock>("GyroSteeringOutput").Text.Contains("49"), "paused test displays game output 0 instead of mapped 49%");
            gyroScroll.ScrollToEnd(); Layout(); Capture("gyro-test-paused.png");
            Status(ready with
            {
                State = GyroRuntimeState.StickTakeover, OutputX = .87f, PhysicalX = .87f,
                SteeringPreview = new(true, -30, -29f / 89, -29f / 89)
            });
            Assert(Required<TextBlock>("GyroSteeringAngle").Text.Contains("30") && Required<TextBlock>("GyroSteeringOutput").Text.Contains("33"),
                "physical stick takeover replaces the independent gyro test readout");
            Capture("gyro-test-stick-takeover.png");
            Status(ready with { State = GyroRuntimeState.SensorInvalid, InputValid = false, ImuValid = false, Ready = false, NeedsRecenter = true, SteeringPreview = default });
            Assert(Required<TextBlock>("GyroSteeringAngle").Text.Contains('—') && Required<TextBlock>("GyroSteeringOutput").Text.Contains('—'),
                "unavailable preview retains stale angle/output");
            Capture("gyro-test-unavailable.png");
            test.IsExpanded = false;
            Assert(!PreviewSubscribed(), "collapsing test retains preview subscription");
            test.IsExpanded = true; Tab(0);
            Assert(!PreviewSubscribed(), "leaving calibration tab retains preview subscription");
            Tab(2); Page(settingsPage);
            Assert(!PreviewSubscribed(), "leaving gyro page retains preview subscription");

            Status(ready);
            settingsScroll.ScrollToEnd(); Layout();
            string summary = Required<TextBlock>("GyroEntryState").Text;
            Assert(!summary.Contains('·') && !summary.Contains(UiText.Get("Ready"), StringComparison.Ordinal), "ready entry repeats Ready after On");
            Capture("gyro-advanced-custom.png");
            Assert(Required<Grid>("RightPanel").ActualWidth == 288, "right navigation rail width changed");

            Page(gyroPage); Tab(0);
            Settings(savedGyro with { Enabled = false });
            Status(ready with { State = GyroRuntimeState.WaitingForCalibration, StationaryCalibrated = false, NeedsRecenter = true, Ready = false, SteeringPreview = default });
            switchPage.IsChecked = true; CancelSave(); Pump();
            Assert(Presented(Required<TextBlock>("GyroCalibrationStep1State")) && !Presented(angleSlider), "first enable does not open two-step calibration guide");
            Assert(!Required<Button>("GyroRecenterButton").IsEnabled, "set straight ahead is enabled before stationary calibration");
            gyroScroll.ScrollToTop(); Layout(); Capture("gyro-first-enable.png");
            var calibrationAction = Required<Button>("GyroCalibrateButton");
            TextBlock actionLabel = Descendants<TextBlock>(calibrationAction).Single(text => text.Text == UiText.Get("Start calibration"));
            Assert(actionLabel.Foreground is SolidColorBrush ink && ink.Color.R > 220 && ink.Color.G > 220 && ink.Color.B > 220,
                "primary calibration action has dark text on its dark surface");
            Status(ready with { State = GyroRuntimeState.WaitingForCenter, NeedsRecenter = true, Ready = false, SteeringPreview = default });
            Assert(Required<Button>("GyroRecenterButton").IsEnabled, "existing stationary calibration cannot proceed to straight ahead");
            Capture("gyro-guide-straight-ahead.png");
            Status(ready);
            Capture("gyro-calibration-ready.png");

            Tab(0);
            Settings(savedGyro with { Enabled = false });
            switchPage.IsChecked = true; CancelSave();
            Assert(Presented(angleSlider), "already prepared enable unnecessarily restarts calibration guide");
            Assert(typeof(MainWindow).GetField("_runtimeTask", flags)!.GetValue(window) is null, "UI render unexpectedly started runtime");
        }
        finally
        {
            CancelSave();
            Invoke("LeaveGyroPage");
            while (GyroControlState.TryDequeue(out _)) { }
            GyroControlState.Publish(originalStatus);
            GyroSettingsState.Current = originalGyro;
            lightState.Update(originalLight);
            profileField.SetValue(window, originalProfile);
            profileState.Update(originalProfile);
            Invoke("ApplyProfileVisuals", false);
            Page(settingsPage);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
