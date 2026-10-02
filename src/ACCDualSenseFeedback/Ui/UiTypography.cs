using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace ACCDualSenseFeedback.Ui;

// Native typography tokens, matching the approved Chinese HTML direction.
// Keep native text shaping/hinting: do not simulate tracking with per-glyph
// transforms, which would reintroduce the title rasterization problem.
public static class UiTypography
{
    public static FontFamily LatinFont { get; } = new("Segoe UI Variable Text, Segoe UI");
    public static FontFamily TextFont { get; } = UiText.IsChinese
        ? new FontFamily(new Uri("pack://application:,,,/"),
            "/ACCDualSenseFeedback;component/Assets/Fonts/#ACC Noto UI SC")
        : LatinFont;
    public static FontFamily DisplayFont { get; } = UiText.IsChinese
        ? TextFont : new FontFamily("Segoe UI Variable Display, Segoe UI");
    public static XmlLanguage Language => XmlLanguage.GetLanguage(UiText.IsChinese ? "zh-CN" : "en-US");
    public static double StatusSize => UiText.IsChinese ? 12 : 12.5;
    public static FontWeight StatusWeight => UiText.IsChinese ? FontWeights.Medium : FontWeights.SemiBold;
    public static double SectionLabelSize => UiText.IsChinese ? 15 : 18;
    public static FontWeight SectionLabelWeight => StatusWeight;
    public static double DisplaySize => UiText.IsChinese ? 60 : 68;
    public static FontWeight DisplayWeight => UiText.IsChinese ? FontWeights.SemiBold : FontWeights.Bold;
    public static double DisplayLineHeight => UiText.IsChinese ? 73.2 : 72;
    public static double BodySize => UiText.IsChinese ? 14 : 14.5;
    public static double BodyLineHeight => UiText.IsChinese ? 23 : 21;
    public static double CaptionSize => UiText.IsChinese ? 12 : 11.5;
    public static double PageTitleSize => UiText.IsChinese ? 32 : 36;
    public static FontWeight PageTitleWeight => UiText.IsChinese ? FontWeights.Medium : FontWeights.SemiBold;
    public static double PageTitleLineHeight => UiText.IsChinese ? 42 : double.NaN;
    public static double ParameterSize => UiText.IsChinese ? 13 : 12.5;
    public static FontWeight ParameterWeight => StatusWeight;
    public static double ParameterRowHeight => UiText.IsChinese ? 56 : 50;
    public static double ModuleHeaderHeight => UiText.IsChinese ? 47 : 43;
    public static GridLength ParameterLabelWidth => new(UiText.IsChinese ? 164 : 178);
    public static GridLength ParameterValueWidth => new(UiText.IsChinese ? 85 : 88);
    public static Thickness SliderMargin => UiText.IsChinese ? new Thickness(10, 0, 10, 0) : new Thickness(0);
    public static double ValueSize => UiText.IsChinese ? 13 : 11.5;
    public static double TagSize => UiText.IsChinese ? 11 : 11.5;
    public static FontWeight TagWeight => UiText.IsChinese ? FontWeights.Medium : FontWeights.Bold;
    public static Color CaptionColor => (Color)ColorConverter.ConvertFromString(UiText.IsChinese ? "#FF68727C" : "#FF7A838C");
    public static TextRenderingMode RenderingMode => UiText.IsChinese ? TextRenderingMode.Auto : TextRenderingMode.Grayscale;
}
