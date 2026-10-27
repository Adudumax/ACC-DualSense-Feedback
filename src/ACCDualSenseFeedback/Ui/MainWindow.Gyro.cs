using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ACCDualSenseFeedback.Gyro;
using ACCDualSenseFeedback.Haptics;

namespace ACCDualSenseFeedback.Ui;

public partial class MainWindow
{
    private readonly List<Action<GyroSteeringSettings>> _gyroControlRefresh = [];
    private readonly List<FrameworkElement> _gyroEditableRows = [];
    private readonly StackPanel[] _gyroSections = [new(), new(), new()];
    private bool _updatingGyro = true;
    private bool _gyroPreviewSubscribed;
    private long _gyroPreviewAt;
    private long _gyroLearnedSequence;
    private double _settingsScrollBeforeGyro;
    private CancellationTokenSource? _gyroSaveCancellation;
    private Expander _gyroTest = null!;
    private TextBlock _gyroPreviewAngle = null!;
    private TextBlock _gyroPreviewOutput = null!;
    private TextBlock _gyroCalibrationGuide = null!;
    private TextBlock _gyroCalibrationStep1 = null!;
    private TextBlock _gyroCalibrationStep2 = null!;
    private TextBlock _gyroBindingHint = null!;
    private Button _gyroRecenter = null!;
    private Button _gyroCalibrate = null!;
    private Button _gyroCancelLearning = null!;
    private Button _gyroLearnButton = null!;

    private void InitializeGyroControls()
    {
        _gyroLearnedSequence = GyroControlState.Current.LearnedSequence;
        // The removed shortcuts must not continue consuming a controller button
        // after a portable installation is upgraded from an earlier preview.
        GyroSettingsState.Current = GyroSettingsState.Current with { RecenterButton = GyroButton.None, ForwardShortcutButtons = false };
        SetGyroReset(GyroPageEnabledSwitch, () => CommitGyro(GyroSettingsState.Current with { Enabled = false }));
        InitializeGyroTabs();
        foreach (StackPanel panel in _gyroSections) GyroControlsHost.Children.Add(panel);
        BuildGyroFeelPanel(_gyroSections[0]);
        BuildGyroOperationPanel(_gyroSections[1]);
        BuildGyroCalibrationPanel(_gyroSections[2]);
        GyroScrollViewer.ScrollChanged += (_, e) => { if (Math.Abs(e.VerticalChange) > .01 || Math.Abs(e.HorizontalChange) > .01) CloseGyroChoice(false); };
        Deactivated += (_, _) => CloseGyroChoice(false);
        LocationChanged += (_, _) => CloseGyroChoice(false);
        SizeChanged += (_, _) => CloseGyroChoice(false);
        _updatingGyro = false;
        SetGyroTab(0, false);
        UpdateGyroControls();
    }

    private TextBlock GyroText(string text, string style) => new()
    {
        Text = UiText.Get(text), Style = (Style)FindResource(style), VerticalAlignment = VerticalAlignment.Center
    };

    private Border GyroDivider() => new() { Height = 1, Background = FindBrush("Brush.Border") };

    private Grid GyroRow(Panel parent, string name, string label, string help, out TextBlock readout)
    {
        var row = new Grid { Name = name + "Row", Height = 44, Background = Brushes.Transparent };
        RegisterName(row.Name, row);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = UiTypography.ParameterLabelWidth });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = UiTypography.ParameterValueWidth });
        var title = new TextBlock
        {
            Text = UiText.Get(label), FontSize = UiTypography.ParameterSize, FontWeight = UiTypography.ParameterWeight,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = UiText.Get(help)
        };
        row.Children.Add(title);
        readout = GyroText("", "Type.ParameterReadout");
        readout.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(readout, 2);
        row.Children.Add(readout);
        parent.Children.Add(row);
        _gyroEditableRows.Add(row);
        return row;
    }

    private Grid AddGyroSlider(Panel parent, string name, string label, string help,
        double min, double max, double step, Func<GyroSteeringSettings, float> get,
        Func<GyroSteeringSettings, float, GyroSteeringSettings> set, Func<float, string> format, float defaultValue)
    {
        Grid row = GyroRow(parent, name, label, help, out TextBlock readout);
        var slider = new Slider
        {
            Name = name + "Slider", Style = (Style)FindResource("Slider.Compact"), Minimum = min, Maximum = max,
            TickFrequency = step, SmallChange = step, LargeChange = Math.Max(step, (max - min) / 10), IsSnapToTickEnabled = true
        };
        RegisterName(slider.Name, slider);
        AutomationProperties.SetName(slider, UiText.Get(label));
        AutomationProperties.SetHelpText(slider, UiText.Get(help) + ". " + UiText.Get("Right-click a parameter to reset it."));
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        slider.ValueChanged += (_, _) => { if (!_updatingGyro) CommitGyro(set(GyroSettingsState.Current, (float)slider.Value)); };
        _gyroControlRefresh.Add(s => { slider.Value = get(s); readout.Text = format(get(s)); });
        SetGyroReset(row, () => CommitGyro(set(GyroSettingsState.Current, defaultValue)));
        return row;
    }

    private void AddGyroSwitch(Panel parent, string name, string label, string help,
        Func<GyroSteeringSettings, bool> get, Func<GyroSteeringSettings, bool, GyroSteeringSettings> set, bool defaultValue)
    {
        Grid row = GyroRow(parent, name, label, help, out TextBlock readout);
        row.Children.Remove(readout);
        var toggle = new AnimatedSwitch { Name = name + "Switch", Style = (Style)FindResource("Switch.Lighting") };
        Grid.SetColumn(toggle, 2);
        row.Children.Add(toggle);
        RegisterName(toggle.Name, toggle);
        AutomationProperties.SetName(toggle, UiText.Get(label));
        toggle.Checked += (_, _) => { if (!_updatingGyro) CommitGyro(set(GyroSettingsState.Current, true)); };
        toggle.Unchecked += (_, _) => { if (!_updatingGyro) CommitGyro(set(GyroSettingsState.Current, false)); };
        _gyroControlRefresh.Add(s => toggle.IsChecked = get(s));
        SetGyroReset(row, () => CommitGyro(set(GyroSettingsState.Current, defaultValue)));
    }

    private static string DescribeGyroButton(GyroButton button) => button switch
    {
        GyroButton.Cross => "×", GyroButton.Circle => "○", GyroButton.Square => "□", GyroButton.Triangle => "△",
        GyroButton.None => UiText.Get("Unbound"), GyroButton.Touchpad => UiText.Get("Touchpad press"),
        GyroButton.Share => UiText.Get("Create"), GyroButton.Options => UiText.Get("Options"),
        GyroButton.Mute => UiText.Get("Mute button"),
        GyroButton.DpadUp => UiText.Get("D-pad up"), GyroButton.DpadRight => UiText.Get("D-pad right"),
        GyroButton.DpadDown => UiText.Get("D-pad down"), GyroButton.DpadLeft => UiText.Get("D-pad left"),
        GyroButton.EdgeFnLeft => UiText.Get("Left Fn"), GyroButton.EdgeFnRight => UiText.Get("Right Fn"),
        GyroButton.EdgePaddleLeft => UiText.Get("Left rear button"), GyroButton.EdgePaddleRight => UiText.Get("Right rear button"),
        _ => button.ToString()
    };

    private Button GyroButtonControl(string text, Action action)
    {
        var button = new Button { Content = UiText.Get(text), Style = (Style)FindResource("Button.GyroAction"), Height = 36, Padding = new Thickness(12, 0, 12, 0) };
        AutomationProperties.SetName(button, UiText.Get(text));
        button.Click += (_, _) => { if (!_closing) action(); };
        return button;
    }

    private void SetGyroReset(FrameworkElement element, Action reset)
    {
        element.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (_profile.ActivePreset != FeedbackPreset.Custom) return;
            e.Handled = true;
            CloseGyroChoice(false);
            reset();
        };
    }

    private void UpdatePresetDependentControls(bool animate)
    {
        UpdateLightingControls(animate);
        UpdateGyroControls();
    }

    private void GyroEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingGyro || _closing || _profile.ActivePreset != FeedbackPreset.Custom) return;
        bool enabled = sender is AnimatedSwitch toggle && toggle.IsChecked == true;
        CommitGyro(GyroSettingsState.Current with { Enabled = enabled });
        GyroRuntimeStatus status = GyroControlState.Current;
        if (enabled && (!status.StationaryCalibrated || status.NeedsRecenter)) SetGyroTab(2);
    }

    private void CommitGyro(GyroSteeringSettings settings)
    {
        if (_updatingGyro || _closing || _profile.ActivePreset != FeedbackPreset.Custom) return;
        GyroSettingsState.Current = settings with { RecenterButton = GyroButton.None, ForwardShortcutButtons = false };
        UpdateGyroControls();
        GyroSaveCaption.Text = UiText.Get("Changes saved automatically · Right-click to reset an item");
        _gyroSaveCancellation?.Cancel();
        _gyroSaveCancellation?.Dispose();
        _gyroSaveCancellation = new CancellationTokenSource();
        _ = SaveGyroAfterDelayAsync(_gyroSaveCancellation.Token);
    }

    private void UpdateGyroControls()
    {
        if (_gyroRecenter is null) return;
        bool custom = _profile.ActivePreset == FeedbackPreset.Custom;
        GyroSteeringSettings settings = GyroSettingsState.Current;
        bool enabled = custom && settings.Enabled;
        _updatingGyro = true;
        GyroPageEnabledSwitch.IsChecked = enabled;
        GyroPageEnabledSwitch.IsEnabled = custom;
        GyroMasterState.Text = UiText.Get(enabled ? "Enabled" : "Disabled");
        GyroPresetCaption.Text = UiText.Get(custom ? "Custom preset" : "Default preset");
        GyroMasterState.Foreground = FindBrush(enabled ? "Brush.GyroReady" : "Brush.TextSecondary");
        GyroPresetHint.Text = UiText.Get(custom ? "Rotate the controller to steer." : "Switch to Custom to adjust.");
        foreach (Action<GyroSteeringSettings> update in _gyroControlRefresh) update(settings);
        foreach (FrameworkElement element in _gyroEditableRows)
        {
            element.IsEnabled = custom;
            element.Opacity = custom ? 1 : .45;
        }
        ResetGyroButton.IsEnabled = custom;
        _updatingGyro = false;
        UpdateGyroRuntime(GyroControlState.Current);
        if (_feedbackLive) StatusDetail.Text = UiText.Get(GyroHomeDetail());
    }

    private void UpdateGyroRuntime(GyroRuntimeStatus status)
    {
        if (_gyroRecenter is null) return;
        bool custom = _profile.ActivePreset == FeedbackPreset.Custom;
        GyroSteeringSettings settings = GyroSettingsState.Current;
        bool enabled = custom && settings.Enabled;
        GyroPausePresentation pause = GyroStatusPresentation.Pause(settings, status);
        bool fault = status.State is GyroRuntimeState.SensorInvalid or GyroRuntimeState.NativeUnavailable;
        string entry = !custom ? "Off in Default" : !enabled ? "Disabled"
            : status.IsCalibrating ? "Calibrating…" : !status.StationaryCalibrated ? "Calibration needed"
            : status.NeedsRecenter ? "Set straight ahead first" : "Enabled";
        GyroEntryState.Text = UiText.Get(entry);
        GyroEntryState.Foreground = FindBrush(enabled && status.StationaryCalibrated && !status.NeedsRecenter ? "Brush.GyroReady"
            : enabled ? "Brush.Brand" : "Brush.TextSecondary");
        GyroPageHint.Text = UiText.Get(!custom ? "Switch to Custom to adjust."
            : !enabled ? "Your parameters are kept. Enable gyro to use them."
            : status.IsCalibrating ? "Keep the controller still. Feedback pauses during calibration."
            : fault ? status.Message
            : !status.StationaryCalibrated ? "First use: complete stationary calibration."
            : status.NeedsRecenter ? "Calibration complete. Set your straight-ahead grip."
            : settings.ActivationMode != GyroActivationMode.Always && settings.ActivationButton == GyroButton.None ? "Bind an activation button under Operation."
            : status.State == GyroRuntimeState.Suspended ? pause.Hint
            : "Ready. Gyro takes effect while driving in ACC.");
        if (enabled && status.Message is "Pause ACC before calibration" or "Calibration cancelled" or "Hold controller upright" or "Calibration timed out" or "Calibration complete; bias not saved")
            GyroPageHint.Text = UiText.Get(status.Message);
        if (enabled && status.IsCalibrating) GyroPageHint.Text += " " + $"{status.CalibrationProgress * 100:0}%";
        bool needsSetup = enabled && (!status.StationaryCalibrated || status.NeedsRecenter);
        bool needsBinding = enabled && settings.ActivationMode != GyroActivationMode.Always && settings.ActivationButton == GyroButton.None;
        bool resume = enabled && !needsSetup && !needsBinding && pause.ShowResume;
        GyroStatusAction.Visibility = !status.IsCalibrating && !fault && (resume || needsSetup && _gyroTabIndex != 2 || !needsSetup && needsBinding && _gyroTabIndex != 1) ? Visibility.Visible : Visibility.Collapsed;
        GyroStatusAction.Content = UiText.Get(resume ? "Resume gyro" : "Continue setup ›");
        GyroStatusAction.Tag = resume ? "resume" : needsBinding && !needsSetup ? "binding" : "setup";
        _gyroRecenter.IsEnabled = enabled && status.ImuValid && status.StationaryCalibrated && !status.IsCalibrating;
        _gyroCalibrate.IsEnabled = enabled && status.FactoryCalibrated && status.ImuValid && !status.IsCalibrating;
        _gyroCalibrate.Tag = enabled && !status.StationaryCalibrated ? "Primary" : null;
        _gyroRecenter.Tag = enabled && status.StationaryCalibrated && status.NeedsRecenter ? "Primary" : null;
        _gyroCalibrate.Content = UiText.Get(status.IsCalibrating ? "Keep still…" : status.StationaryCalibrated ? "Calibrate again" : "Start calibration");
        _gyroCalibrationStep1.Text = UiText.Get(status.IsCalibrating ? "Calibrating…" : status.StationaryCalibrated ? "Complete" : "Not complete");
        _gyroCalibrationStep2.Text = UiText.Get(!status.NeedsRecenter && status.StationaryCalibrated ? "Complete" : "Not complete");
        _gyroCalibrationStep1.Foreground = FindBrush(status.StationaryCalibrated ? "Brush.GyroReady" : "Brush.TextSecondary");
        _gyroCalibrationStep2.Foreground = FindBrush(!status.NeedsRecenter && status.StationaryCalibrated ? "Brush.GyroReady" : "Brush.TextSecondary");
        _gyroCalibrationGuide.Visibility = enabled && !status.IsCalibrating ? Visibility.Visible : Visibility.Collapsed;
        _gyroCalibrationGuide.Text = UiText.Get(!status.StationaryCalibrated ? "First use · Complete these two steps in order"
            : status.NeedsRecenter ? "Stationary calibration is saved. Set your natural grip as straight ahead each time you start the app or reconnect the controller."
            : "Straight-ahead pose is set.");
        _gyroTest.Visibility = status.StationaryCalibrated && !status.NeedsRecenter ? Visibility.Visible : Visibility.Collapsed;
        bool learning = custom && status.PendingBindingTarget == GyroBindingTarget.Activation;
        _gyroLearnButton.Content = learning ? UiText.Get("Press any controller button…") : settings.ActivationButton == GyroButton.None ? UiText.Get("Set button") : DescribeGyroButton(settings.ActivationButton);
        _gyroCancelLearning.Visibility = learning ? Visibility.Visible : Visibility.Collapsed;
        _gyroBindingHint.Visibility = learning ? Visibility.Visible : Visibility.Collapsed;
        _gyroBindingHint.Text = UiText.Get("Press the controller button to bind. It will not reach ACC while learning.");
        if (status.LearnedSequence > _gyroLearnedSequence)
        {
            _gyroLearnedSequence = status.LearnedSequence;
            if (custom && status.LearnedButton != GyroButton.None && status.LearnedBindingTarget == GyroBindingTarget.Activation)
                CommitGyro(settings with { ActivationButton = status.LearnedButton });
        }
        UpdateGyroPreviewSubscription();
        UpdateGyroPreview(status);
    }

    private void GyroStatusAction_Click(object sender, RoutedEventArgs e)
    {
        if ((string?)GyroStatusAction.Tag == "resume") GyroControlState.RequestResume();
        else SetGyroTab((string?)GyroStatusAction.Tag == "binding" ? 1 : 2);
    }

    private string GyroHomeDetail()
    {
        if (_profile.ActivePreset != FeedbackPreset.Custom || !GyroSettingsState.Current.Enabled) return "DualSense connected. ACC telemetry live.";
        return GyroControlState.Current.State switch
        {
            GyroRuntimeState.Active => "ACC live · Gyro active.", GyroRuntimeState.StickTakeover => "ACC live · Stick control.",
            GyroRuntimeState.Suspended => "ACC live · Gyro paused.", _ => "ACC live · Gyro needs attention."
        };
    }

    private async void GyroSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsScrollBeforeGyro = SettingsScrollViewer.VerticalOffset;
        UpdateGyroControls();
        await ShowPageAsync(GyroPanelContent, GyroPanelTranslate);
    }

    private async void GyroBackButton_Click(object sender, RoutedEventArgs e) => await ReturnFromGyroAsync();
    private async Task ReturnFromGyroAsync()
    {
        CloseGyroChoice(false);
        GyroControlState.CancelButtonLearning();
        await ShowSettingsAsync();
        SettingsScrollViewer.ScrollToVerticalOffset(_settingsScrollBeforeGyro);
    }
    private void ResetGyroButton_Click(object sender, RoutedEventArgs e) => CommitGyro(GyroSettingsState.Current.ResetSteeringParameters());

    private void UpdateGyroPreviewSubscription()
    {
        bool wanted = !_closing && GyroPanelContent.Visibility == Visibility.Visible && _gyroTabIndex == 2 && _gyroTest.IsExpanded && _gyroTest.Visibility == Visibility.Visible;
        if (wanted == _gyroPreviewSubscribed) return;
        _gyroPreviewSubscribed = wanted;
        if (wanted) { _gyroPreviewAt = 0; CompositionTarget.Rendering += GyroPreview_Rendering; UpdateGyroPreview(GyroControlState.Current); }
        else CompositionTarget.Rendering -= GyroPreview_Rendering;
    }
    private void LeaveGyroPage()
    {
        CloseGyroChoice(false);
        GyroControlState.CancelButtonLearning();
        if (_gyroPreviewSubscribed) CompositionTarget.Rendering -= GyroPreview_Rendering;
        _gyroPreviewSubscribed = false;
    }
    private void GyroPreview_Rendering(object? sender, EventArgs e)
    {
        if (GyroPanelContent.Visibility != Visibility.Visible || _gyroTabIndex != 2 || !_gyroTest.IsExpanded || _closing) { UpdateGyroPreviewSubscription(); return; }
        long now = Stopwatch.GetTimestamp();
        if (now - _gyroPreviewAt < Stopwatch.Frequency / 30) return;
        _gyroPreviewAt = now;
        UpdateGyroPreview(GyroControlState.Current);
    }
    private void UpdateGyroPreview(GyroRuntimeStatus status)
    {
        // Preview and game output intentionally have separate contracts: the
        // foreground/pause safety gate must not make this test appear frozen.
        GyroSteeringPreview preview = status.SteeringPreview;
        bool available = preview.IsAvailable && status.ImuValid && status.StationaryCalibrated && !status.NeedsRecenter;
        _gyroPreviewAngle.Text = available ? preview.AngleDegrees.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°" : "—";
        _gyroPreviewOutput.Text = available ? (preview.OutputX * 100).ToString("+0;-0;0", CultureInfo.CurrentCulture) + "%" : "—";
    }
    private async Task SaveGyroAfterDelayAsync(CancellationToken token)
    {
        try { await Task.Delay(280, token); } catch (OperationCanceledException) { return; }
        if (!PortableGyroSettingsStore.TrySave(GyroSettingsState.Current, out _))
            ShowToast("Change active", "This choice works now, but the app folder did not allow it to be saved.");
    }
    private void FlushGyroSettingsOnClose()
    {
        _gyroSaveCancellation?.Cancel(); _gyroSaveCancellation?.Dispose(); _gyroSaveCancellation = null;
        LeaveGyroPage();
        _ = PortableGyroSettingsStore.TrySave(GyroSettingsState.Current, out _);
    }
}

