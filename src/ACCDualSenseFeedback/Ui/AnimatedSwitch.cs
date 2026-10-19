using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ACCDualSenseFeedback.Ui;

public sealed class AnimatedSwitch : ToggleButton
{
    private TranslateTransform? _thumb;
    private SolidColorBrush? _track;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Thumb") is Border thumb)
        {
            // Template Freezables can be shared and frozen by WPF. Give each
            // switch its own mutable transform before assigning/animating it.
            _thumb = new TranslateTransform();
            thumb.RenderTransform = _thumb;
        }
        if (GetTemplateChild("PART_Track") is Border track)
        {
            _track = new SolidColorBrush();
            track.Background = _track;
        }
        UpdateVisual(false);
    }

    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); UpdateVisual(true); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); UpdateVisual(true); }

    private void UpdateVisual(bool animate)
    {
        if (_thumb is null || _track is null) return;
        double target = IsChecked == true ? 18 : 0;
        Color color = (Color)FindResource(IsChecked == true ? "Color.Brand" : "Color.BorderStrong");
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            _thumb.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target,
                TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
            _track.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(color, TimeSpan.FromMilliseconds(160)));
        }
        else
        {
            _thumb.BeginAnimation(TranslateTransform.XProperty, null);
            _track.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _thumb.X = target;
            _track.Color = color;
        }
    }
}
