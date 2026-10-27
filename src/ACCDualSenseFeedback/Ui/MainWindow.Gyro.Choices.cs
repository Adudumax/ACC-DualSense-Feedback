using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ACCDualSenseFeedback.Gyro;

namespace ACCDualSenseFeedback.Ui;

public partial class MainWindow
{
    private Popup? _gyroChoicePopup;
    private Button? _gyroChoiceTrigger;
    private readonly List<Button> _gyroChoiceItems = [];
    private ScrollViewer? _gyroChoiceScroll;
    private int _gyroChoiceFocus;
    private Action<int>? _gyroChoiceCommit;
    private readonly Dictionary<Button, RotateTransform> _gyroChoiceChevrons = [];

    private void AddGyroChoice<T>(Panel parent, string name, string label, string help, T[] values, string[] names,
        Func<GyroSteeringSettings, T> get, Func<GyroSteeringSettings, T, GyroSteeringSettings> set, T defaultValue)
    {
        Grid row = GyroRow(parent, name, label, help, out TextBlock readout);
        row.Children.Remove(readout);
        var button = GyroButtonControl("", () => { });
        button.Name = name + "Choice";
        button.Height = 36;
        button.MinWidth = 112;
        button.Padding = new Thickness(12, 0, 12, 0);
        button.Background = Brushes.Transparent;
        button.Style = GyroFlatButtonStyle();
        button.HorizontalAlignment = HorizontalAlignment.Right;
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        TextBlock valueText = GyroText("", "Type.ParameterReadout"); valueText.Foreground = FindBrush("Brush.TextPrimary"); valueText.HorizontalAlignment = HorizontalAlignment.Right;
        var chevronRotation = new RotateTransform();
        var chevron = new Polyline
        {
            Points = [new Point(0, 0), new Point(4, 4), new Point(8, 0)], Width = 8, Height = 4,
            Stroke = FindBrush("Brush.TextPrimary"), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = chevronRotation
        };
        _gyroChoiceChevrons.Add(button, chevronRotation);
        Grid.SetColumn(chevron, 1); content.Children.Add(valueText); content.Children.Add(chevron); button.Content = content;
        Grid.SetColumn(button, 1); Grid.SetColumnSpan(button, 2); row.Children.Add(button); RegisterName(button.Name, button);
        button.Click += (_, _) =>
        {
            if (_gyroChoiceTrigger == button) { CloseGyroChoice(true); return; }
            int selected = Math.Max(0, Array.IndexOf(values, get(GyroSettingsState.Current)));
            OpenGyroChoice(button, UiText.Get(label), names.Select(UiText.Get).ToArray(), selected,
                index => CommitGyro(set(GyroSettingsState.Current, values[index])));
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (_gyroChoicePopup is not null) return;
            if (e.Key is not (Key.Down or Key.Up or Key.Home or Key.End)) return;
            e.Handled = true;
            int selected = Math.Max(0, Array.IndexOf(values, get(GyroSettingsState.Current)));
            OpenGyroChoice(button, UiText.Get(label), names.Select(UiText.Get).ToArray(), selected,
                index => CommitGyro(set(GyroSettingsState.Current, values[index])));
            if (e.Key == Key.Home) FocusGyroChoice(0);
            else if (e.Key == Key.End) FocusGyroChoice(values.Length - 1);
        };
        _gyroControlRefresh.Add(s =>
        {
            int selected = Math.Max(0, Array.IndexOf(values, get(s)));
            valueText.Text = UiText.Get(names[selected]);
            AutomationProperties.SetName(button, UiText.Get(label) + ": " + valueText.Text);
        });
        SetGyroReset(row, () => CommitGyro(set(GyroSettingsState.Current, defaultValue)));
    }

    private void OpenGyroChoice(Button trigger, string label, string[] names, int selected, Action<int> commit)
    {
        CloseGyroChoice(false);
        GyroControlState.CancelButtonLearning();
        const double rowHeight = 32, chrome = 10, gap = 4, edge = 6, shadowMargin = 8;
        Point anchor = trigger.TranslatePoint(new Point(), this);
        double below = Math.Max(0, ActualHeight - anchor.Y - trigger.ActualHeight - gap - edge);
        double above = Math.Max(0, anchor.Y - gap - edge);
        double natural = names.Length * rowHeight + chrome;
        bool upward = below < Math.Min(natural, 2 * rowHeight + chrome) + shadowMargin * 2 && above > below;
        double limit = Math.Min(natural, Math.Max(rowHeight + chrome, (upward ? above : below) - shadowMargin * 2));
        double width = Math.Max(172, Math.Min(240, trigger.ActualWidth + 12));
        var options = new StackPanel();
        _gyroChoiceItems.Clear();
        for (int index = 0; index < names.Length; index++)
        {
            int option = index;
            Button item = GyroButtonControl("", () => SelectGyroChoice(option));
            item.Height = rowHeight; item.Padding = new Thickness(0); item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            item.Style = GyroFlatButtonStyle(); item.Background = index == selected ? FindBrush("Brush.Surface") : Brushes.Transparent;
            var itemGrid = new Grid { Width = width - 10, Height = rowHeight };
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition());
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
            if (index == selected)
            {
                var stripe = new Border { Width = 2, Height = 16, CornerRadius = new CornerRadius(1), Background = FindBrush("Brush.Brand"), HorizontalAlignment = HorizontalAlignment.Left };
                itemGrid.Children.Add(stripe);
            }
            TextBlock text = GyroText(names[index], "Type.ParameterReadout"); text.Foreground = FindBrush("Brush.TextPrimary"); Grid.SetColumn(text, 1); itemGrid.Children.Add(text);
            TextBlock mark = GyroText(index == selected ? "✓" : "", "Type.ParameterReadout"); mark.Foreground = FindBrush("Brush.Brand"); mark.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(mark, 2); itemGrid.Children.Add(mark);
            item.Content = itemGrid;
            AutomationProperties.SetName(item, names[index]); AutomationProperties.SetItemStatus(item, UiText.Get(index == selected ? "Selected" : "Not selected"));
            item.GotKeyboardFocus += (_, _) => { _gyroChoiceFocus = option; item.BringIntoView(); };
            _gyroChoiceItems.Add(item); options.Children.Add(item);
        }
        var scroll = new ScrollViewer
        {
            Content = options, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = limit - chrome, CanContentScroll = false, PanningMode = PanningMode.VerticalOnly
        };
        var scrollbarStyle = new Style(typeof(ScrollBar), (Style)FindResource("ScrollBar.Module"));
        scroll.Resources.Add(typeof(ScrollBar), scrollbarStyle);
        var surface = new Border
        {
            Child = scroll, Width = width, Padding = new Thickness(4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Background = FindBrush("Brush.Window"), BorderBrush = FindBrush("Brush.Border"), Margin = new Thickness(shadowMargin),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 4, Opacity = .14, Color = Colors.Black },
            RenderTransform = new TranslateTransform(0, upward ? 2 : -2)
        };
        var popup = new Popup
        {
            Child = surface, PlacementTarget = trigger, Placement = PlacementMode.Custom,
            AllowsTransparency = true, StaysOpen = false, Focusable = false, PopupAnimation = PopupAnimation.None
        };
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            // Keep the entire branded surface inside the app; long lists scroll
            // downward instead of unexpectedly flipping above the control.
            double x = targetSize.Width - popupSize.Width + shadowMargin;
            double maxX = ActualWidth - anchor.X - popupSize.Width - edge;
            x = Math.Max(edge - anchor.X, Math.Min(x, maxX));
            double y = upward ? -popupSize.Height + shadowMargin - gap : targetSize.Height - shadowMargin + gap;
            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
        };
        surface.PreviewKeyDown += (_, e) => HandleGyroOverlayKey(e);
        popup.Closed += (_, _) =>
        {
            if (_gyroChoicePopup != popup) return;
            AnimateGyroChoiceChevron(trigger, false);
            _gyroChoicePopup = null; _gyroChoiceTrigger = null; _gyroChoiceCommit = null; _gyroChoiceScroll = null; _gyroChoiceItems.Clear();
            AutomationProperties.SetHelpText(trigger, "");
        };
        _gyroChoicePopup = popup; _gyroChoiceTrigger = trigger; _gyroChoiceCommit = commit; _gyroChoiceScroll = scroll; _gyroChoiceFocus = selected;
        AutomationProperties.SetName(surface, label);
        AutomationProperties.SetHelpText(trigger, UiText.Get("Options open. Use arrows to choose; Escape closes."));
        popup.IsOpen = true;
        AnimateGyroChoiceChevron(trigger, true);
        if (SystemParameters.ClientAreaAnimation)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
            ((TranslateTransform)surface.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
        }
        else ((TranslateTransform)surface.RenderTransform).Y = 0;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { if (_gyroChoicePopup == popup) FocusGyroChoice(_gyroChoiceFocus); }));
    }

    private void FocusGyroChoice(int index)
    {
        if (_gyroChoiceItems.Count == 0) return;
        _gyroChoiceFocus = Math.Clamp(index, 0, _gyroChoiceItems.Count - 1);
        _gyroChoiceItems[_gyroChoiceFocus].Focus();
        _gyroChoiceItems[_gyroChoiceFocus].BringIntoView();
    }

    private void SelectGyroChoice(int index)
    {
        Action<int>? commit = _gyroChoiceCommit;
        Button? trigger = _gyroChoiceTrigger;
        CloseGyroChoice(false);
        commit?.Invoke(index);
        trigger?.Focus();
    }

    private void CloseGyroChoice(bool restoreFocus)
    {
        Popup? popup = _gyroChoicePopup; Button? trigger = _gyroChoiceTrigger;
        if (popup is null) return;
        popup.IsOpen = false;
        if (restoreFocus) trigger?.Focus();
    }

    private void AnimateGyroChoiceChevron(Button trigger, bool open)
    {
        if (!_gyroChoiceChevrons.TryGetValue(trigger, out RotateTransform? rotation)) return;
        double target = open ? 180 : 0;
        if (SystemParameters.ClientAreaAnimation)
            rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { rotation.BeginAnimation(RotateTransform.AngleProperty, null); rotation.Angle = target; }
    }

    private bool HandleGyroOverlayKey(KeyEventArgs e)
    {
        if (_gyroChoicePopup is not null)
        {
            switch (e.Key)
            {
                case Key.Escape: CloseGyroChoice(true); break;
                case Key.Down: FocusGyroChoice((_gyroChoiceFocus + 1) % _gyroChoiceItems.Count); break;
                case Key.Up: FocusGyroChoice((_gyroChoiceFocus + _gyroChoiceItems.Count - 1) % _gyroChoiceItems.Count); break;
                case Key.Home: FocusGyroChoice(0); break;
                case Key.End: FocusGyroChoice(_gyroChoiceItems.Count - 1); break;
                case Key.Enter: case Key.Space: SelectGyroChoice(_gyroChoiceFocus); break;
                case Key.Tab:
                    Button? trigger = _gyroChoiceTrigger;
                    CloseGyroChoice(false);
                    trigger?.Focus();
                    trigger?.MoveFocus(new TraversalRequest(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                    break;
                default: return false;
            }
            e.Handled = true; return true;
        }
        if (e.Key == Key.Escape && GyroControlState.Current.PendingBindingTarget != GyroBindingTarget.None)
        {
            GyroControlState.CancelButtonLearning(); e.Handled = true; return true;
        }
        return false;
    }
}
