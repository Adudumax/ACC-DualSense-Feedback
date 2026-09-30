using System.Windows;

namespace ACCDualSenseFeedback.Ui;

internal static class DesktopApplication
{
    public static int Run()
    {
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/ACCDualSenseFeedback;component/Ui/AppResources.xaml",
                UriKind.RelativeOrAbsolute)
        });

        return application.Run(new MainWindow());
    }
}
