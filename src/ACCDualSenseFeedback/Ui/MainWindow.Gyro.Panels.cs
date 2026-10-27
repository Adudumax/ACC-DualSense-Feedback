using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ACCDualSenseFeedback.Gyro;

namespace ACCDualSenseFeedback.Ui;

public partial class MainWindow
{
    private int _gyroTabIndex;
    private readonly Button[] _gyroTabButtons = new Button[3];
    private readonly double[] _gyroTabOffsets = new double[3];
    private Border _gyroTabIndicator = null!;
    private readonly TranslateTransform _gyroTabIndicatorTranslate = new();
    private readonly TranslateTransform _gyroSectionTranslate = new();
    private long _gyroSectionAnimationUntil;

    private void InitializeGyroTabs()
    {
        string[] titles = ["Steering feel", "Operation", "Calibration & test"];
        string[] names = ["GyroFeelTab", "GyroOperationTab", "GyroCalibrationTab"];
        for (int index = 0; index < titles.Length; index++)
        {
            int tab = index;
            GyroTabsHost.ColumnDefinitions.Add(new ColumnDefinition());
            Button button = GyroButtonControl(titles[index], () => SetGyroTab(tab));
            button.Name = names[index];
            button.Height = double.NaN;
            button.Background = Brushes.Transparent;
            button.Style = GyroFlatButtonStyle();
            button.FontSize = UiTypography.ParameterSize;
            Grid.SetColumn(button, index);
            RegisterName(button.Name, button);
            button.PreviewKeyDown += (_, e) =>
            {
                int next = e.Key switch { Key.Left => (tab + 2) % 3, Key.Right => (tab + 1) % 3, Key.Home => 0, Key.End => 2, _ => -1 };
                if (next < 0) return;
                e.Handled = true;
                SetGyroTab(next);
                _gyroTabButtons[next].Focus();
            };
            _gyroTabButtons[index] = button;
            GyroTabsHost.Children.Add(button);
        }
        var divider = GyroDivider();
        divider.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumnSpan(divider, 3);
        GyroTabsHost.Children.Add(divider);
        _gyroTabIndicator = new Border
        {
            Height = 2, Background = FindBrush("Brush.TextPrimary"), VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, RenderTransform = _gyroTabIndicatorTranslate
        };
        Grid.SetColumnSpan(_gyroTabIndicator, 3);
        GyroTabsHost.Children.Add(_gyroTabIndicator);
        GyroTabsHost.SizeChanged += (_, _) => UpdateGyroTabIndicator(false);
        GyroControlsHost.RenderTransform = _gyroSectionTranslate;
    }

    private void SetGyroTab(int index, bool animate = true)
    {
        if (index < 0 || index >= _gyroSections.Length) return;
        bool changed = _gyroTabIndex != index;
        if (changed) _gyroTabOffsets[_gyroTabIndex] = GyroScrollViewer.VerticalOffset;
        CloseGyroChoice(false);
        if (changed) GyroControlState.CancelButtonLearning();
        _gyroTabIndex = index;
        for (int i = 0; i < _gyroSections.Length; i++)
        {
            _gyroSections[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            _gyroTabButtons[i].Foreground = FindBrush(i == index ? "Brush.TextPrimary" : "Brush.TextSecondary");
            _gyroTabButtons[i].FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            AutomationProperties.SetItemStatus(_gyroTabButtons[i], UiText.Get(i == index ? "Selected" : "Not selected"));
        }
        GyroScrollViewer.ScrollToVerticalOffset(_gyroTabOffsets[index]);
        UpdateGyroTabIndicator(changed && animate);
        if (changed && animate && SystemParameters.ClientAreaAnimation)
        {
            // Animate only the changed section, never sliders, the page frame or
            // the master switch. Replace running clocks on rapid tab changes.
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            bool interrupted = Environment.TickCount64 < _gyroSectionAnimationUntil;
            double fromOpacity = interrupted ? GyroControlsHost.Opacity : .9;
            double fromY = interrupted ? _gyroSectionTranslate.Y : 2;
            _gyroSectionAnimationUntil = Environment.TickCount64 + 160;
            GyroControlsHost.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            _gyroSectionTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        }
        else
        {
            GyroControlsHost.BeginAnimation(OpacityProperty, null);
            _gyroSectionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            GyroControlsHost.Opacity = 1;
            _gyroSectionTranslate.Y = 0;
            _gyroSectionAnimationUntil = 0;
        }
        UpdateGyroRuntime(GyroControlState.Current);
    }

    private void UpdateGyroTabIndicator(bool animate)
    {
        if (_gyroTabIndicator is null) return;
        double width = GyroTabsHost.ActualWidth / 3;
        _gyroTabIndicator.Width = Math.Max(0, width - 30);
        double target = width * _gyroTabIndex + 15;
        if (animate && SystemParameters.ClientAreaAnimation)
            _gyroTabIndicatorTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { _gyroTabIndicatorTranslate.BeginAnimation(TranslateTransform.XProperty, null); _gyroTabIndicatorTranslate.X = target; }
    }

    private Style GyroFlatButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, FindBrush("Brush.TextPrimary")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, UiTypography.TextFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, UiTypography.ParameterSize));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Surface");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, FindBrush("Brush.Surface"), "Surface"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = Button.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BackgroundProperty, FindBrush("Brush.Surface"), "Surface"));
        template.Triggers.Add(focus);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private void BuildGyroFeelPanel(StackPanel panel)
    {
        AddGyroSlider(panel, "GyroFullLock", "Single-side full lock", "Controller angle, not vehicle steering lock",
            30, 120, 1, s => s.FullLockDegrees, (s, v) => s with { FullLockDegrees = v }, v => $"{v:0}°", 90);
        TextBlock angleHint = GyroText("", "Type.Caption");
        angleHint.Margin = new Thickness(0, -2, 0, 6);
        panel.Children.Add(angleHint);
        _gyroControlRefresh.Add(s => angleHint.Text = UiText.Format("Each side {0}° · Total {1}°", s.FullLockDegrees, s.FullLockDegrees * 2));
        AddGyroSlider(panel, "GyroDeadzone", "Center deadzone", "Ignore small movements around straight ahead",
            0, 5, .1, s => s.DeadzoneDegrees, (s, v) => s with { DeadzoneDegrees = v }, v => $"{v:0.0}°", 1);
        AddGyroChoice(panel, "GyroSmoothing", "Stabilization", "More smoothing can increase lag",
            new[] { GyroSmoothing.Off, GyroSmoothing.Light, GyroSmoothing.Medium, GyroSmoothing.Strong, GyroSmoothing.Custom },
            new[] { "Off", "Light", "Moderate", "Strong stabilization", "Custom value" }, s => s.Smoothing, (s, v) => s with { Smoothing = v }, GyroSmoothing.Light);
        Grid smoothing = AddGyroSlider(panel, "GyroSmoothingTime", "Smoothing time", "Time constant, not total input delay",
            0, 30, 1, s => s.CustomSmoothingMilliseconds, (s, v) => s with { CustomSmoothingMilliseconds = v }, v => $"{v:0} ms", 8);
        _gyroControlRefresh.Add(s => smoothing.Visibility = s.Smoothing == GyroSmoothing.Custom ? Visibility.Visible : Visibility.Collapsed);
        AddGyroChoice(panel, "GyroCurve", "Response curve", "Shape the response near the center",
            new[] { GyroResponseCurve.Linear, GyroResponseCurve.SoftCenter, GyroResponseCurve.Custom },
            new[] { "Linear", "Soft center", "Custom value" }, s => s.Curve, (s, v) => s with { Curve = v }, GyroResponseCurve.Linear);
        Grid exponent = AddGyroSlider(panel, "GyroExponent", "Response exponent", "Above 1 makes the center softer",
            .5, 3, .05, s => s.ResponseExponent, (s, v) => s with { ResponseExponent = v }, v => $"{v:0.00}", 1);
        var curve = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var curveCaption = new Grid();
        TextBlock title = GyroText("Angle and output", "Type.Caption");
        TextBlock midpoint = GyroText("", "Type.Caption");
        midpoint.HorizontalAlignment = HorizontalAlignment.Right;
        curveCaption.Children.Add(title); curveCaption.Children.Add(midpoint); curve.Children.Add(curveCaption);
        var canvas = new Canvas { Height = 56, ClipToBounds = true };
        var axis = new Polyline { Stroke = FindBrush("Brush.BorderStrong"), StrokeThickness = 1 };
        var reference = new Line { Stroke = FindBrush("Brush.BorderStrong"), StrokeThickness = 1, StrokeDashArray = [3, 3] };
        var response = new Polyline { Stroke = FindBrush("Brush.Brand"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        canvas.Children.Add(axis); canvas.Children.Add(reference); canvas.Children.Add(response); curve.Children.Add(canvas); panel.Children.Add(curve);
        void RefreshCurve(GyroSteeringSettings settings)
        {
            double width = Math.Max(40, canvas.ActualWidth - 8), height = 46;
            axis.Points = [new Point(4, 4), new Point(4, height), new Point(width, height)];
            reference.X1 = 4; reference.Y1 = height; reference.X2 = width; reference.Y2 = 4;
            var points = new PointCollection();
            for (int i = 0; i <= 60; i++)
            {
                double fraction = i / 60d;
                double mapped = Math.Pow(Math.Max(0, (fraction * settings.FullLockDegrees - settings.DeadzoneDegrees) / (settings.FullLockDegrees - settings.DeadzoneDegrees)), settings.ResponseExponent);
                points.Add(new Point(4 + fraction * (width - 4), height - mapped * (height - 4)));
            }
            response.Points = points;
            double half = Math.Pow(Math.Max(0, (settings.FullLockDegrees / 2 - settings.DeadzoneDegrees) / (settings.FullLockDegrees - settings.DeadzoneDegrees)), settings.ResponseExponent);
            midpoint.Text = UiText.Format("Half range → {0}% output", Math.Round(half * 100));
        }
        canvas.SizeChanged += (_, _) => RefreshCurve(GyroSettingsState.Current);
        _gyroControlRefresh.Add(s => { exponent.Visibility = curve.Visibility = s.Curve == GyroResponseCurve.Custom ? Visibility.Visible : Visibility.Collapsed; RefreshCurve(s); });
        AddGyroSwitch(panel, "GyroInvert", "Invert steering", "Swap left and right", s => s.Invert, (s, v) => s with { Invert = v }, false);
    }

    private void BuildGyroOperationPanel(StackPanel panel)
    {
        AddGyroChoice(panel, "GyroActivation", "Activation", "Hold and toggle modes need a button binding",
            new[] { GyroActivationMode.Always, GyroActivationMode.Hold, GyroActivationMode.Toggle },
            new[] { "Continuous", "Hold button to enable", "Button toggles activation" }, s => s.ActivationMode, (s, v) => s with { ActivationMode = v }, GyroActivationMode.Always);
        var bindingSlot = new ContentControl();
        panel.Children.Add(bindingSlot);
        AddGyroChoice(panel, "GyroReference", "Neutral reference", "Changing this requires a new straight-ahead pose",
            new[] { GyroReferenceMode.CurrentPose, GyroReferenceMode.Gravity }, new[] { "Current grip", "Gravity direction" },
            s => s.ReferenceMode, (s, v) => s with { ReferenceMode = v }, GyroReferenceMode.CurrentPose);
        AddGyroSwitch(panel, "GyroStickMode", "Physical stick priority", "Physical stick remains available when gyro is invalid", s => s.StickPriority, (s, v) => s with { StickPriority = v }, true);
        Grid takeover = AddGyroSlider(panel, "GyroTakeover", "Stick takeover threshold", "Only selects the input source",
            10, 50, 1, s => s.TakeoverPercent, (s, v) => s with { TakeoverPercent = v }, v => $"{v:0}%", 20);
        _gyroControlRefresh.Add(s => takeover.Visibility = s.StickPriority ? Visibility.Visible : Visibility.Collapsed);
        Grid binding = BuildGyroBinding();
        var bottomSlot = new ContentControl { Content = binding };
        panel.Children.Add(bottomSlot);
        _gyroControlRefresh.Add(s =>
        {
            ContentControl destination = s.ActivationMode == GyroActivationMode.Always ? bottomSlot : bindingSlot;
            if (destination.Content == binding) return;
            bindingSlot.Content = null; bottomSlot.Content = null; destination.Content = binding;
        });
        _gyroBindingHint = GyroText("", "Type.Caption");
        _gyroBindingHint.TextWrapping = TextWrapping.Wrap;
        _gyroBindingHint.Margin = new Thickness(0, 4, 0, 4);
        panel.Children.Add(_gyroBindingHint);
        _gyroCancelLearning = GyroButtonControl("Cancel button learning", () => GyroControlState.CancelButtonLearning());
        _gyroCancelLearning.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(_gyroCancelLearning);
    }

    private Grid BuildGyroBinding()
    {
        var scratch = new StackPanel();
        Grid row = GyroRow(scratch, "GyroActivationBinding", "Enable / disable button", "If gyro stops responding or becomes locked, try pressing the bound button to resume. Right-click to clear the binding.", out TextBlock readout);
        scratch.Children.Remove(row); row.Height = 56; row.Children.Remove(readout);
        TextBlock label = (TextBlock)row.Children[0];
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Remove(label); labels.Children.Add(label);
        TextBlock sub = GyroText("Gyro shortcut only", "Type.Caption"); sub.Margin = new Thickness(0, 2, 0, 0); labels.Children.Add(sub); row.Children.Add(labels);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(actions, 1); Grid.SetColumnSpan(actions, 2); row.Children.Add(actions);
        Button clear = GyroButtonControl("Clear", () => CommitGyro(GyroSettingsState.Current with { ActivationButton = GyroButton.None }));
        clear.Background = Brushes.Transparent; clear.Margin = new Thickness(0, 0, 4, 0); actions.Children.Add(clear);
        Button learn = GyroButtonControl("Set button", () => { CloseGyroChoice(false); GyroControlState.RequestLearnButton(GyroBindingTarget.Activation); });
        _gyroLearnButton = learn;
        learn.Name = "GyroActivationBindingButton"; RegisterName(learn.Name, learn); actions.Children.Add(learn);
        _gyroControlRefresh.Add(s =>
        {
            label.Text = UiText.Get(s.ActivationMode == GyroActivationMode.Always ? "Enable / pause / resume button" : "Enable / disable button");
            learn.Content = s.ActivationButton == GyroButton.None ? UiText.Get("Set button") : DescribeGyroButton(s.ActivationButton);
            clear.Visibility = s.ActivationButton == GyroButton.None ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(learn, label.Text + ": " + DescribeGyroButton(s.ActivationButton));
        });
        SetGyroReset(row, () => CommitGyro(GyroSettingsState.Current with { ActivationButton = GyroButton.None }));
        return row;
    }

    private void BuildGyroCalibrationPanel(StackPanel panel)
    {
        _gyroCalibrationGuide = GyroText("", "Type.Caption");
        _gyroCalibrationGuide.TextWrapping = TextWrapping.Wrap;
        _gyroCalibrationGuide.Margin = new Thickness(0, 5, 0, 4);
        panel.Children.Add(_gyroCalibrationGuide);
        _gyroCalibrate = GyroButtonControl("Start calibration", () => GyroControlState.RequestCalibration());
        _gyroCalibrate.Name = "GyroCalibrateButton"; RegisterName(_gyroCalibrate.Name, _gyroCalibrate);
        AddGyroSetupStep(panel, 1, "Stationary calibration", "Pause ACC and place the controller flat to calibrate sensor bias.", _gyroCalibrate, out _gyroCalibrationStep1);
        _gyroCalibrationStep1.Name = "GyroCalibrationStep1State"; RegisterName(_gyroCalibrationStep1.Name, _gyroCalibrationStep1);
        _gyroRecenter = GyroButtonControl("Set straight ahead", () => GyroControlState.RequestRecenter());
        _gyroRecenter.Name = "GyroRecenterButton"; RegisterName(_gyroRecenter.Name, _gyroRecenter);
        AddGyroSetupStep(panel, 2, "Set straight ahead", "Hold naturally. Set this grip as your straight-ahead pose.", _gyroRecenter, out _gyroCalibrationStep2);
        _gyroCalibrationStep2.Name = "GyroCalibrationStep2State"; RegisterName(_gyroCalibrationStep2.Name, _gyroCalibrationStep2);
        TextBlock note = GyroText("Stationary calibration and straight-ahead setup are different. Complete both to steer.", "Type.Caption");
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 8, 0, 4); panel.Children.Add(note);
        var preview = new StackPanel();
        var readout = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        readout.ColumnDefinitions.Add(new ColumnDefinition()); readout.ColumnDefinitions.Add(new ColumnDefinition());
        TextBlock AddReadout(string caption, string name, int column)
        {
            var group = new StackPanel(); Grid.SetColumn(group, column);
            group.Children.Add(GyroText(caption, "Type.Caption"));
            TextBlock value = GyroText("—", "Type.ParameterReadout"); value.FontSize = 22; value.Margin = new Thickness(0, 4, 0, 2); value.Name = name;
            RegisterName(name, value); group.Children.Add(value); readout.Children.Add(group); return value;
        }
        _gyroPreviewAngle = AddReadout("Controller angle", "GyroSteeringAngle", 0);
        _gyroPreviewOutput = AddReadout("Gyro output", "GyroSteeringOutput", 1);
        preview.Children.Add(readout);
        TextBlock explanation = GyroText("Test shows gyro mapping. Steering is not sent to ACC while paused.", "Type.Caption");
        explanation.TextWrapping = TextWrapping.Wrap; preview.Children.Add(explanation);
        _gyroTest = AddGyroExpander(panel, "GyroTestSteering", "Test steering", preview);
        _gyroTest.Expanded += (_, _) => UpdateGyroPreviewSubscription();
        _gyroTest.Collapsed += (_, _) => UpdateGyroPreviewSubscription();
    }

    private void AddGyroSetupStep(Panel parent, int step, string label, string help, Button action, out TextBlock state)
    {
        var row = new Grid { MinHeight = 72, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock number = GyroText(step.ToString(), "Type.Caption"); number.Foreground = FindBrush("Brush.Brand"); number.FontWeight = FontWeights.SemiBold; row.Children.Add(number);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(copy, 1); row.Children.Add(copy);
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        TextBlock title = GyroText(label, "Type.ParameterReadout"); title.Foreground = FindBrush("Brush.TextPrimary"); title.FontWeight = FontWeights.Medium;
        heading.Children.Add(title);
        state = GyroText("", "Type.Caption"); state.Margin = new Thickness(8, 0, 0, 0); heading.Children.Add(state); copy.Children.Add(heading);
        TextBlock detail = GyroText(help, "Type.Caption"); detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 4, 0, 0); copy.Children.Add(detail);
        Grid.SetColumn(action, 2); action.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(action); parent.Children.Add(row); parent.Children.Add(GyroDivider());
    }

    private Expander AddGyroExpander(Panel parent, string name, string title, UIElement body)
    {
        var expander = new Expander
        {
            Name = name, Header = "›  " + UiText.Get(title), Content = body, IsExpanded = false,
            FontFamily = UiTypography.TextFont, FontSize = UiTypography.ParameterSize,
            Foreground = FindBrush("Brush.TextPrimary"), BorderThickness = new Thickness(0), Background = Brushes.Transparent
        };
        var headerTemplate = new ControlTemplate(typeof(ToggleButton));
        var headerGrid = new FrameworkElementFactory(typeof(Grid)); headerGrid.SetValue(HeightProperty, 40d); headerGrid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left); content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); headerGrid.AppendChild(content); headerTemplate.VisualTree = headerGrid;
        var headerStyle = new Style(typeof(ToggleButton)); headerStyle.Setters.Add(new Setter(TemplateProperty, headerTemplate)); headerStyle.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        var expanderTemplate = new ControlTemplate(typeof(Expander)); var stack = new FrameworkElementFactory(typeof(StackPanel));
        var toggle = new FrameworkElementFactory(typeof(ToggleButton)); toggle.SetValue(StyleProperty, headerStyle); toggle.SetValue(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        toggle.SetBinding(ContentControl.ContentProperty, new Binding("Header") { RelativeSource = RelativeSource.TemplatedParent });
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsExpanded") { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay }); stack.AppendChild(toggle);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetBinding(VisibilityProperty, new Binding("IsExpanded") { RelativeSource = RelativeSource.TemplatedParent, Converter = new BooleanToVisibilityConverter() }); stack.AppendChild(presenter); expanderTemplate.VisualTree = stack; expander.Template = expanderTemplate;
        expander.Expanded += (_, _) => expander.Header = "⌄  " + UiText.Get(title); expander.Collapsed += (_, _) => expander.Header = "›  " + UiText.Get(title);
        AutomationProperties.SetName(expander, UiText.Get(title)); RegisterName(name, expander); parent.Children.Add(expander); return expander;
    }
}

