using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ACCDualSenseFeedback.Ui;

internal static class UiSnapshot
{
    public static int Run(
        string outputPath,
        bool showSettings = false,
        bool showDiagnostics = false,
        bool showControllerError = false)
    {
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/ACCDualSenseFeedback;component/Ui/AppResources.xaml",
                UriKind.RelativeOrAbsolute)
        });

        var window = new MainWindow(snapshotMode: true)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };
        if (showControllerError)
            window.ShowControllerErrorForSnapshot();
        else if (showDiagnostics)
            window.ShowDiagnosticsForSnapshot();
        else if (showSettings)
            window.ShowSettingsForSnapshot();
        window.Show();
        window.UpdateLayout();

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int pixelWidth = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        int pixelHeight = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(fullPath))
            encoder.Save(stream);

        window.Close();
        application.Shutdown();
        int dpiPercent = (int)Math.Round(dpi.DpiScaleX * 100);
        Console.WriteLine($"UI snapshot written to {fullPath} ({pixelWidth}x{pixelHeight}, {dpiPercent}% DPI)");
        return 0;
    }
}
