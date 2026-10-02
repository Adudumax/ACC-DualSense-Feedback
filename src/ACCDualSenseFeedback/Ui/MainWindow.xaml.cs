using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ACCDualSenseFeedback.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Runtime;

namespace ACCDualSenseFeedback.Ui;

public partial class MainWindow : Window
{
    private static readonly TimeSpan StateTransitionDuration = TimeSpan.FromMilliseconds(180);
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _toastCancellation;
    private CancellationTokenSource? _profileSaveCancellation;
    private Task? _runtimeTask;
    private readonly FeedbackProfileState _profileState;
    private FeedbackProfile _profile;
    private readonly bool _recoveredFromInvalidSettings;
    private bool _closing;
    private bool _closeCommitted;
    private bool _controllerConnected;
    private bool _virtualControllerActive;
    private bool _accConnected;
    private bool _feedbackLive;
    private bool _updatingSliders;
    private FeedbackRuntimeStatus? _lastRuntimeStatus;
    private Exception? _lastRuntimeError;
    private DateTimeOffset? _lastRuntimeErrorAt;
    private string _visualState = string.Empty;
    private readonly Queue<string> _sessionLog = new();

    public MainWindow()
    {
        _profile = PortableProfileStore.Load(out bool recovered);
        _profileState = new FeedbackProfileState(_profile);
        _recoveredFromInvalidSettings = recovered;
        InitializeComponent();
        ApplyProfileVisuals(animate: false);
        BrakeResistanceSlider.ValueChanged += FeedbackSlider_ValueChanged;
        ThrottleResistanceSlider.ValueChanged += FeedbackSlider_ValueChanged;
        AbsPulseSlider.ValueChanged += FeedbackSlider_ValueChanged;
        LockPulseSlider.ValueChanged += FeedbackSlider_ValueChanged;
        ShiftKickSlider.ValueChanged += FeedbackSlider_ValueChanged;
        RedlinePulseSlider.ValueChanged += FeedbackSlider_ValueChanged;
        RedlineStartSlider.ValueChanged += FeedbackSlider_ValueChanged;
        EngineTextureSlider.ValueChanged += FeedbackSlider_ValueChanged;
        TcPulseSlider.ValueChanged += FeedbackSlider_ValueChanged;
        WheelspinPulseSlider.ValueChanged += FeedbackSlider_ValueChanged;
        RoadSurfaceSlider.ValueChanged += FeedbackSlider_ValueChanged;
        GripLossSlider.ValueChanged += FeedbackSlider_ValueChanged;
        NativeAccSlider.ValueChanged += FeedbackSlider_ValueChanged;
        CollisionFeedbackSlider.ValueChanged += FeedbackSlider_ValueChanged;
        AppendSessionLog("App ready");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlayEntranceAnimation();
        _ = StartFeedbackAsync();
        if (_recoveredFromInvalidSettings)
            ShowToast("Settings reset", "The saved choices could not be read. Balanced defaults are active.");
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        int rounded = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
    }

    private void PlayEntranceAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;

        LeftPanelContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        LeftPanelTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        HeroMark.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private async Task StartFeedbackAsync()
    {
        if (_runtimeTask is { IsCompleted: false })
            return;

        if (Process.GetProcessesByName("DS4Windows").Length != 0)
        {
            ApplyState(
                "ds4windows",
                "Action needed",
                "Close DS4Windows before starting.",
                FindBrush("Brush.Warning"),
                showRetry: true);
            SetConnectionState(false, false, false, false);
            return;
        }

        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _runCancellation.Token;

        ApplyState(
            "starting",
            "Starting",
            "Preparing the controller path.",
            FindBrush("Brush.Warning"));
        SetConnectionState(false, false, false, false);

        _runtimeTask = RunRuntimeAsync(cancellationToken);
        await _runtimeTask;
    }

    private async Task RunRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() =>
            {
                using var timerResolution = WindowsTimerResolution.Request1Millisecond();
                using var runtime = new FeedbackRuntime(
                    dryRun: false,
                    statusSink: OnRuntimeStatus,
                    useConsoleUi: false,
                    tuningProvider: () => _profileState.CurrentTuning);
                runtime.Run(cancellationToken);
                if (runtime.BackgroundFailure is not null)
                    throw new InvalidOperationException(runtime.BackgroundFailure.Message, runtime.BackgroundFailure);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_closing)
                return;

            await Dispatcher.InvokeAsync(() => ApplyRuntimeError(exception));
        }
    }

    private void OnRuntimeStatus(FeedbackRuntimeStatus status)
    {
        _ = Dispatcher.BeginInvoke(() => ApplyRuntimeStatus(status));
    }

    private void ApplyRuntimeStatus(FeedbackRuntimeStatus status)
    {
        if (_closing)
            return;

        _lastRuntimeStatus = status;
        bool feedbackLive = status.AccConnected && status.DualSenseConnected && !status.TelemetryStale;
        SetConnectionState(
            status.DualSenseConnected,
            status.AccConnected,
            feedbackLive,
            status.InputBridgeConnected);

        if (!status.DualSenseConnected)
        {
            ApplyState(
                "waiting-controller",
                "Waiting for DualSense",
                "Connect the controller by USB.",
                FindBrush("Brush.Warning"));
            return;
        }

        if (!status.AccConnected)
        {
            ApplyState(
                "waiting-acc",
                "Ready for ACC",
                "DualSense connected. Start ACC when ready.",
                FindBrush("Brush.Success"));
            return;
        }

        if (status.TelemetryStale)
        {
            ApplyState(
                "telemetry-paused",
                "Waiting for session",
                "ACC detected. Enter a driving session.",
                FindBrush("Brush.Warning"));
            return;
        }

        ApplyState(
            "live",
            "Feedback active",
            "DualSense connected. ACC telemetry live.",
            FindBrush("Brush.Success"));
    }

    private void ApplyRuntimeError(Exception exception)
    {
        _lastRuntimeError = exception;
        _lastRuntimeErrorAt = DateTimeOffset.Now;
        bool controllerMissing = exception.Message.Contains("DualSense", StringComparison.OrdinalIgnoreCase);
        ApplyState(
            "error",
            controllerMissing ? "DualSense unavailable" : "Feedback unavailable",
            controllerMissing
                ? "Connect by USB. Check HidHide access."
                : "Check ViGEmBus, USB and HidHide.",
            FindBrush("Brush.Warning"),
            showRetry: true);
        SetConnectionState(false, false, false, false);
    }

    private void ApplyState(string key, string status, string detail, Brush accent, bool showRetry = false)
    {
        status = UiText.Get(status);
        detail = UiText.Get(detail);
        bool changed = _visualState != key;

        void Update()
        {
            StatusStrong.Text = status;
            StatusDetail.Text = detail;
            StatusDot.Fill = accent;
            RetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_visualState == key || !SystemParameters.ClientAreaAnimation)
        {
            Update();
            _visualState = key;
            if (changed)
                AppendSessionLog($"{status} — {detail}");
            return;
        }

        _visualState = key;
        Update();
        AppendSessionLog($"{status} — {detail}");
        StatusRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.48, 1, StateTransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        StatusTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(4, 0, StateTransitionDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void SetConnectionState(bool controller, bool acc, bool feedback, bool virtualController)
    {
        _controllerConnected = controller;
        _virtualControllerActive = virtualController;
        _accConnected = acc;
        _feedbackLive = feedback;

        ControllerFooterDetail.Text = UiText.Get(controller ? "USB" : "Waiting");
        ControllerFooterDetail.Foreground = FindBrush(controller ? "Brush.TextSecondary" : "Brush.TextTertiary");
        AccFooterDetail.Text = feedback ? UiText.Get("Telemetry live") : acc ? UiText.Get("Game detected") : UiText.WaitingForAcc;
        AccFooterDetail.Foreground = FindBrush(feedback ? "Brush.TextSecondary" : "Brush.TextTertiary");
        UpdateDiagnosticsVisuals();
    }

    private Brush FindBrush(string key) => (Brush)FindResource(key);

    private void DefaultPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile.ActivePreset == FeedbackPreset.Default)
        {
            ShowToast("Default preset", "Balanced response is already active.");
            return;
        }

        CommitProfile(_profile with { ActivePreset = FeedbackPreset.Default });
        ShowToast("Default preset", "Balanced response is active automatically.");
    }

    private void CustomPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile.ActivePreset == FeedbackPreset.Custom)
        {
            ShowToast("Custom preset", "Your saved feel is already active.");
            return;
        }

        CommitProfile(_profile with { ActivePreset = FeedbackPreset.Custom });
        ShowToast("Custom preset", "Your saved vibration and trigger feel is active.");
    }

    private async void AdvancedSettingsButton_Click(object sender, RoutedEventArgs e) =>
        await ShowSettingsAsync();

    private async void SettingsBackButton_Click(object sender, RoutedEventArgs e) =>
        await ShowHomeAsync();

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
        await ShowDiagnosticsAsync();

    private async void DiagnosticsBackButton_Click(object sender, RoutedEventArgs e) =>
        await ShowHomeAsync();

    private void FeedbackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSliders)
            return;

        ApplyCustomProfileChange(_profile with
        {
            BrakeResistance = (int)Math.Round(BrakeResistanceSlider.Value),
            ThrottleResistance = (int)Math.Round(ThrottleResistanceSlider.Value),
            AbsPulse = (int)Math.Round(AbsPulseSlider.Value),
            LockPulse = (int)Math.Round(LockPulseSlider.Value),
            ShiftKick = (int)Math.Round(ShiftKickSlider.Value),
            RedlinePulse = (int)Math.Round(RedlinePulseSlider.Value),
            RedlineStartPermille = (int)Math.Round(RedlineStartSlider.Value),
            EngineTexture = (int)Math.Round(EngineTextureSlider.Value),
            TcPulse = (int)Math.Round(TcPulseSlider.Value),
            WheelspinPulse = (int)Math.Round(WheelspinPulseSlider.Value),
            RoadSurface = (int)Math.Round(RoadSurfaceSlider.Value),
            GripLoss = (int)Math.Round(GripLossSlider.Value),
            NativeAcc = (int)Math.Round(NativeAccSlider.Value),
            CollisionFeedback = (int)Math.Round(CollisionFeedbackSlider.Value)
        });
    }

    private void FeedbackParameter_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_updatingSliders || sender is not FrameworkElement element || element.Tag is not string setting)
            return;

        (FeedbackProfile Updated, string Label, string DefaultText) reset = setting switch
        {
            nameof(FeedbackProfile.BrakeResistance) =>
                (_profile with { BrakeResistance = FeedbackProfile.NeutralValue }, "Brake resistance", "50"),
            nameof(FeedbackProfile.ThrottleResistance) =>
                (_profile with { ThrottleResistance = FeedbackProfile.NeutralValue }, "Throttle resistance", "50"),
            nameof(FeedbackProfile.AbsPulse) =>
                (_profile with { AbsPulse = FeedbackProfile.NeutralValue }, "ABS pulse", "50"),
            nameof(FeedbackProfile.LockPulse) =>
                (_profile with { LockPulse = FeedbackProfile.NeutralValue }, "Wheel lock", "50"),
            nameof(FeedbackProfile.ShiftKick) =>
                (_profile with { ShiftKick = FeedbackProfile.NeutralValue }, "Upshift kick", "50"),
            nameof(FeedbackProfile.RedlinePulse) =>
                (_profile with { RedlinePulse = FeedbackProfile.NeutralValue }, "Redline pulse", "50"),
            nameof(FeedbackProfile.RedlineStartPermille) =>
                (_profile with { RedlineStartPermille = FeedbackProfile.DefaultRedlineStartPermille }, "Pulse begins", "96.2%"),
            nameof(FeedbackProfile.EngineTexture) =>
                (_profile with { EngineTexture = FeedbackProfile.DefaultEngineTexture }, "Engine texture", "20"),
            nameof(FeedbackProfile.TcPulse) =>
                (_profile with { TcPulse = FeedbackProfile.NeutralValue }, "TC pulse", "50"),
            nameof(FeedbackProfile.WheelspinPulse) =>
                (_profile with { WheelspinPulse = FeedbackProfile.NeutralValue }, "Wheelspin warning", "50"),
            nameof(FeedbackProfile.RoadSurface) =>
                (_profile with { RoadSurface = FeedbackProfile.NeutralValue }, "Road and kerbs", "50"),
            nameof(FeedbackProfile.GripLoss) =>
                (_profile with { GripLoss = FeedbackProfile.NeutralValue }, "Grip loss", "50"),
            nameof(FeedbackProfile.NativeAcc) =>
                (_profile with { NativeAcc = FeedbackProfile.NeutralValue }, "Original ACC", "50"),
            nameof(FeedbackProfile.CollisionFeedback) =>
                (_profile with { CollisionFeedback = FeedbackProfile.DefaultCollisionFeedback }, "Collision impact", "60"),
            _ => (_profile, string.Empty, string.Empty)
        };

        if (string.IsNullOrEmpty(reset.Label))
            return;

        e.Handled = true;
        if (reset.Updated == _profile)
        {
            ShowToast("Already at default", UiText.Format("{0} is already {1}.", UiText.Get(reset.Label), reset.DefaultText));
            return;
        }

        ApplyCustomProfileChange(reset.Updated);
        SyncFeedbackSliders();
        ShowToast("Parameter reset", UiText.Format("{0} is back to {1}.", UiText.Get(reset.Label), reset.DefaultText));
    }

    private void ApplyCustomProfileChange(FeedbackProfile profile)
    {
        bool becameCustom = _profile.ActivePreset != FeedbackPreset.Custom;
        _profile = (profile with { ActivePreset = FeedbackPreset.Custom }).Sanitize();
        _profileState.Update(_profile);
        ApplyHomeProfileVisuals(animate: becameCustom);
        UpdateModuleControls();
        ScheduleProfileSave();
    }

    private void ResetFeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        CommitProfile(FeedbackProfile.Default with { ActivePreset = FeedbackPreset.Custom });
        ShowToast("Modules reset", "Every feedback module is back at the calibrated standard.");
    }

    private void CommitProfile(FeedbackProfile profile)
    {
        _profile = profile.Sanitize();
        _profileState.Update(_profile);
        ApplyProfileVisuals(animate: true);

        _profileSaveCancellation?.Cancel();

        if (!PortableProfileStore.TrySave(_profile))
            ShowToast("Change active", "This choice works now, but the app folder did not allow it to be saved.");
    }

    private void ApplyProfileVisuals(bool animate)
    {
        ApplyHomeProfileVisuals(animate);
        SyncFeedbackSliders();
    }

    private void SyncFeedbackSliders()
    {
        _updatingSliders = true;
        BrakeResistanceSlider.Value = _profile.BrakeResistance;
        ThrottleResistanceSlider.Value = _profile.ThrottleResistance;
        AbsPulseSlider.Value = _profile.AbsPulse;
        LockPulseSlider.Value = _profile.LockPulse;
        ShiftKickSlider.Value = _profile.ShiftKick;
        RedlinePulseSlider.Value = _profile.RedlinePulse;
        RedlineStartSlider.Value = _profile.RedlineStartPermille;
        EngineTextureSlider.Value = _profile.EngineTexture;
        TcPulseSlider.Value = _profile.TcPulse;
        WheelspinPulseSlider.Value = _profile.WheelspinPulse;
        RoadSurfaceSlider.Value = _profile.RoadSurface;
        GripLossSlider.Value = _profile.GripLoss;
        NativeAccSlider.Value = _profile.NativeAcc;
        CollisionFeedbackSlider.Value = _profile.CollisionFeedback;

        _updatingSliders = false;
        UpdateModuleControls();
    }

    private void ApplyHomeProfileVisuals(bool animate)
    {
        bool custom = _profile.ActivePreset == FeedbackPreset.Custom;
        ActivePresetName.Text = UiText.Get(custom ? "Custom" : "Default");
        ActivePresetTag.Text = UiText.Get(custom ? "PERSONAL" : "AUTOMATIC");
        ActivePresetDescription.Text = UiText.Get(custom
            ? "Your saved feel. Feedback still runs automatically while the app is open."
            : "Balanced vibration and trigger response. Feedback runs automatically while the app is open.");

        MoveSelection(PresetSelectionTranslate, custom ? 114 : 0, animate);
        DefaultPresetLabel.Foreground = FindBrush(custom ? "Brush.TextSecondary" : "Brush.Window");
        CustomPresetLabel.Foreground = FindBrush(custom ? "Brush.Window" : "Brush.TextSecondary");
    }

    private void UpdateModuleControls()
    {
        SetModuleControl(BrakeResistanceSlider, BrakeResistanceValue, _profile.BrakeResistance);
        SetModuleControl(ThrottleResistanceSlider, ThrottleResistanceValue, _profile.ThrottleResistance);
        SetModuleControl(AbsPulseSlider, AbsPulseValue, _profile.AbsPulse);
        SetModuleControl(LockPulseSlider, LockPulseValue, _profile.LockPulse);
        SetModuleControl(ShiftKickSlider, ShiftKickValue, _profile.ShiftKick);
        SetModuleControl(RedlinePulseSlider, RedlinePulseValue, _profile.RedlinePulse);
        SetModuleControl(EngineTextureSlider, EngineTextureValue, _profile.EngineTexture);
        SetModuleControl(TcPulseSlider, TcPulseValue, _profile.TcPulse);
        SetModuleControl(WheelspinPulseSlider, WheelspinPulseValue, _profile.WheelspinPulse);
        SetModuleControl(RoadSurfaceSlider, RoadSurfaceValue, _profile.RoadSurface);
        SetModuleControl(GripLossSlider, GripLossValue, _profile.GripLoss);
        SetModuleControl(NativeAccSlider, NativeAccValue, _profile.NativeAcc);
        SetModuleControl(CollisionFeedbackSlider, CollisionFeedbackValue, _profile.CollisionFeedback);

        RedlineStartValue.Text = $"{_profile.RedlineStartPermille / 10f:0.0}%";
    }

    private static void SetModuleControl(Slider slider, TextBlock readout, int value)
    {
        slider.IsEnabled = true;
        slider.Opacity = 1;
        readout.Opacity = 1;
        readout.Text = value > 0
            ? $"{value} · {DescribeStrength(value)}"
            : UiText.Get("0 · Off");
#if ZH_CN
        readout.Inlines.Clear();
        readout.Inlines.Add(new Run($"{value} · ") { FontFamily = UiTypography.LatinFont });
        readout.Inlines.Add(new Run(value > 0 ? DescribeStrength(value) : "关闭")
        {
            FontFamily = UiTypography.TextFont
        });
#endif
    }

    private static string DescribeStrength(int value) => UiText.Get(value switch
    {
        <= 20 => "Light",
        <= 40 => "Soft",
        <= 60 => "Standard",
        <= 80 => "Strong",
        _ => "Intense"
    });

    private void ScheduleProfileSave()
    {
        _profileSaveCancellation?.Cancel();
        _profileSaveCancellation?.Dispose();
        _profileSaveCancellation = new CancellationTokenSource();
        _ = SaveProfileAfterDelayAsync(_profileSaveCancellation.Token);
    }

    private async Task SaveProfileAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(280, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!PortableProfileStore.TrySave(_profile))
            ShowToast("Change active", "The feel works now, but the app folder did not allow it to be saved.");
    }

    private static void MoveSelection(TranslateTransform transform, double target, bool animate)
    {
        double current = transform.X;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = target;

        if (!animate || !SystemParameters.ClientAreaAnimation)
            return;

        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private async Task ShowSettingsAsync()
        => await ShowPageAsync(SettingsPanelContent, SettingsPanelTranslate);

    private async Task ShowDiagnosticsAsync()
    {
        UpdateDiagnosticsVisuals();
        await ShowPageAsync(DiagnosticsPanelContent, DiagnosticsPanelTranslate);
    }

    private async Task ShowHomeAsync()
        => await ShowPageAsync(LeftPanelContent, LeftPanelTranslate);

    private async Task ShowPageAsync(Grid target, TranslateTransform translate)
    {
        if (target.Visibility == Visibility.Visible)
            return;

        Grid previous = GetVisiblePage();
        target.BeginAnimation(OpacityProperty, null);
        target.Opacity = 0;
        target.Visibility = Visibility.Visible;
        SetNavigationState(target);

        if (!SystemParameters.ClientAreaAnimation)
        {
            CompletePageTransition(target);
            return;
        }

        previous.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)));
        target.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        await Task.Delay(205);
        CompletePageTransition(target);
    }

    private Grid GetVisiblePage()
        => DiagnosticsPanelContent.Visibility == Visibility.Visible
            ? DiagnosticsPanelContent
            : SettingsPanelContent.Visibility == Visibility.Visible
                ? SettingsPanelContent
                : LeftPanelContent;

    private void CompletePageTransition(Grid target)
    {
        foreach (Grid page in new[] { LeftPanelContent, SettingsPanelContent, DiagnosticsPanelContent })
        {
            page.BeginAnimation(OpacityProperty, null);
            page.Visibility = page == target ? Visibility.Visible : Visibility.Collapsed;
            page.Opacity = page == target ? 1 : 0;
        }
    }

    private void SetNavigationState(Grid page)
    {
        AdvancedSettingsButton.Tag = page == SettingsPanelContent ? "Active" : null;
        DiagnosticsButton.Tag = page == DiagnosticsPanelContent ? "Active" : null;
    }

    private void AppendSessionLog(string message)
    {
        message = UiText.Get(message);
        _sessionLog.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_sessionLog.Count > 100)
            _sessionLog.Dequeue();

        SessionLogText.Text = string.Join(Environment.NewLine, _sessionLog.TakeLast(5));
    }

    private void UpdateDiagnosticsVisuals()
    {
        DiagnosticControllerStatus.Text = UiText.Get(_controllerConnected ? "Connected · USB" : "Waiting");
        DiagnosticBridgeStatus.Text = UiText.Get(_virtualControllerActive ? "Active" : "Waiting");
        DiagnosticAccStatus.Text = _feedbackLive ? UiText.Get("Telemetry live") : _accConnected ? UiText.Get("Game detected") : UiText.WaitingForAcc;
        DiagnosticFeedbackStatus.Text = UiText.Get(_feedbackLive ? "Active" : "Idle");

        DiagnosticControllerStatus.Foreground = FindBrush(_controllerConnected ? "Brush.TextSecondary" : "Brush.TextTertiary");
        DiagnosticBridgeStatus.Foreground = FindBrush(_virtualControllerActive ? "Brush.TextSecondary" : "Brush.TextTertiary");
        DiagnosticAccStatus.Foreground = FindBrush(_feedbackLive ? "Brush.TextSecondary" : "Brush.TextTertiary");
        DiagnosticFeedbackStatus.Foreground = FindBrush(_feedbackLive ? "Brush.Success" : "Brush.TextTertiary");
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildDiagnosticSummary());
            ShowToast("Diagnostic report copied", "Driver checks, runtime state and recent session events are on the clipboard.");
        }
        catch (ExternalException)
        {
            ShowToast("Could not copy", "The clipboard is busy. Try again in a moment.");
        }
    }

    private void ExternalLink_RequestNavigate(
        object sender,
        System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowToast("Could not open the link", "See licenses\\THIRD_PARTY_NOTICES.md instead.");
        }

        e.Handled = true;
    }

    private string BuildDiagnosticSummary()
    {
        return DiagnosticReportBuilder.Build(new DiagnosticReportContext(
            _visualState,
            StatusStrong.Text,
            StatusDetail.Text,
            _controllerConnected,
            _virtualControllerActive,
            _accConnected,
            _feedbackLive,
            _lastRuntimeStatus,
            _profile,
            _lastRuntimeError,
            _lastRuntimeErrorAt,
            _sessionLog.ToArray()));
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = StartFeedbackAsync();

    private void ShowToast(string title, string message) => _ = ShowToastAsync(title, message);

    private async Task ShowToastAsync(string title, string message)
    {
        _toastCancellation?.Cancel();
        _toastCancellation?.Dispose();
        _toastCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _toastCancellation.Token;

        ToastTitle.Text = UiText.Get(title);
        ToastMessage.Text = UiText.Get(message);
        ToastBorder.Visibility = Visibility.Visible;

        if (SystemParameters.ClientAreaAnimation)
        {
            ToastBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            ToastTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            ToastBorder.Opacity = 1;
        }

        try
        {
            await Task.Delay(1900, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (SystemParameters.ClientAreaAnimation)
        {
            ToastBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)));
            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 3, TimeSpan.FromMilliseconds(130)));
            try
            {
                await Task.Delay(140, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        ToastBorder.Visibility = Visibility.Collapsed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && LeftPanelContent.Visibility != Visibility.Visible)
        {
            e.Handled = true;
            _ = ShowHomeAsync();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeCommitted)
            return;

        e.Cancel = true;
        if (_closing)
            return;

        _closing = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = CompleteClosingAsync()));
    }

    private async Task CompleteClosingAsync()
    {
        try
        {
            _toastCancellation?.Cancel();
            _profileSaveCancellation?.Cancel();
            _runCancellation?.Cancel();

            Task? runtimeTask = _runtimeTask;
            if (runtimeTask is not null)
                await Task.WhenAny(runtimeTask, Task.Delay(2000));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Shutdown cleanup failed: {exception}");
        }
        finally
        {
            _ = PortableProfileStore.TrySave(_profile);
            _toastCancellation?.Dispose();
            _profileSaveCancellation?.Dispose();
            _runCancellation?.Dispose();
            _closeCommitted = true;
            Close();
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
