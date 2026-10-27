using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ACCDualSenseFeedback.Lighting;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Runtime;

namespace ACCDualSenseFeedback.Ui;

public partial class MainWindow
{
    private readonly RpmLightSettingsState _lightSettingsState = new(PortableLightSettingsStore.Load(out _));
    private bool _updatingLighting = true;
    private CancellationTokenSource? _lightSaveCancellation;

    private void InitializeLightingControls()
    {
        LightsEnabledSwitch.IsChecked = _lightSettingsState.Current.Enabled;
        _updatingLighting = false;
        UpdateLightingControls(false);
    }

    private void LightingOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingLighting || _closing || _profile.ActivePreset != FeedbackPreset.Custom) return;
        CommitLighting(_lightSettingsState.Current with { Enabled = LightsEnabledSwitch.IsChecked == true });
    }

    private void SmoothLights_Click(object sender, RoutedEventArgs e)
        => CommitLighting(_lightSettingsState.Current with { Mode = RpmLightMode.Smooth });
    private void DiscreteLights_Click(object sender, RoutedEventArgs e)
        => CommitLighting(_lightSettingsState.Current with { Mode = RpmLightMode.Discrete });

    private void CommitLighting(RpmLightSettings settings)
    {
        if (_updatingLighting || _closing || _profile.ActivePreset != FeedbackPreset.Custom) return;
        _lightSettingsState.Update(settings);
        UpdateLightingControls(true);
        _lightSaveCancellation?.Cancel();
        _lightSaveCancellation?.Dispose();
        _lightSaveCancellation = new CancellationTokenSource();
        _ = SaveLightingAfterDelayAsync(_lightSaveCancellation.Token);
    }

    private void UpdateLightingControls(bool animate)
    {
        bool custom = _profile.ActivePreset == FeedbackPreset.Custom;
        RpmLightSettings settings = custom ? _lightSettingsState.Current : RpmLightSettings.Default;
        _updatingLighting = true;
        LightsEnabledSwitch.IsChecked = settings.Enabled;
        LightsEnabledSwitch.IsEnabled = custom;
        LightingPresetHint.Text = UiText.Get(custom
            ? "RPM, idle and braking cues" : "Switch to Custom to adjust.");
        _updatingLighting = false;
        bool smooth = settings.Mode == RpmLightMode.Smooth;
        LightModeOptions.IsEnabled = custom && settings.Enabled;
        LightModeOptions.Opacity = custom && settings.Enabled ? 1 : 0.45;
        SmoothLightLabel.Foreground = FindBrush(smooth ? "Brush.Window" : "Brush.TextSecondary");
        DiscreteLightLabel.Foreground = FindBrush(smooth ? "Brush.TextSecondary" : "Brush.Window");
        double target = smooth ? 0 : 100;
        if (animate && IsLoaded && SystemParameters.ClientAreaAnimation)
            LightModeSelectionTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            LightModeSelectionTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            LightModeSelectionTranslate.X = target;
        }
        System.Windows.Automation.AutomationProperties.SetItemStatus(SmoothLightButton,
            UiText.Get(smooth ? "Selected" : "Not selected"));
        System.Windows.Automation.AutomationProperties.SetItemStatus(DiscreteLightButton,
            UiText.Get(smooth ? "Not selected" : "Selected"));
    }

    private async Task SaveLightingAfterDelayAsync(CancellationToken token)
    {
        try { await Task.Delay(280, token); }
        catch (OperationCanceledException) { return; }
        if (!PortableLightSettingsStore.TrySave(_lightSettingsState.Current))
            ShowToast("Change active", "This choice works now, but the app folder did not allow it to be saved.");
    }

    private void FlushLightingSettingsOnClose()
    {
        _lightSaveCancellation?.Cancel();
        _lightSaveCancellation?.Dispose();
        _lightSaveCancellation = null;
        _ = PortableLightSettingsStore.TrySave(_lightSettingsState.Current);
    }
}
