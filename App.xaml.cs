using System.Windows;
using GiftDeck.Services;

namespace GiftDeck;

public partial class App : Application
{
    Mutex _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, "GiftDeck.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("GiftDeck is already running. Look for it in the taskbar.", "GiftDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (s, a) =>
        {
            Log.Write("Unhandled error: " + a.Exception);
            MessageBox.Show(a.Exception.Message, "GiftDeck ran into a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, a) => Log.Write("Fatal error: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (s, a) =>
        {
            Log.Write("Background error: " + a.Exception.InnerException?.Message);
            a.SetObserved();
        };

        Hub.Init();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        Hub.PageReader.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Hub.Shutdown(); } catch { }
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
