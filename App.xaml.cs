using System.Windows;
using GiftDeck.Services;

namespace GiftDeck;

public partial class App : Application
{
    Mutex _single;
    bool _owned;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(false, "GiftDeck.SingleInstance");
        if (TryOwn()) { Begin(); return; }

        // Another GiftDeck has the lock. With a window, it's open: bring it forward. Without one, it's
        // closing (it waits for its hidden OBS to quit, which can take half a minute): wait, then open.
        var other = OtherGiftDeck();
        if (other != null && other.MainWindowHandle != IntPtr.Zero)
        {
            ShowWindow(other.MainWindowHandle, 9 /* SW_RESTORE */);
            SetForegroundWindow(other.MainWindowHandle);
            Shutdown();
            return;
        }
        WaitForPreviousToClose();
    }

    bool TryOwn()
    {
        try { return _owned = _single.WaitOne(0); }
        catch (AbandonedMutexException) { return _owned = true; } // the previous GiftDeck ended without letting go; it's ours now
    }

    static System.Diagnostics.Process OtherGiftDeck()
    {
        int me = Environment.ProcessId;
        return System.Diagnostics.Process.GetProcessesByName("MayhemDeck").Concat(System.Diagnostics.Process.GetProcessesByName("GiftDeck"))
            .FirstOrDefault(p => p.Id != me);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    void WaitForPreviousToClose()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the note mustn't end this GiftDeck
        var note = new Window
        {
            Title = "MayhemDeck",
            Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("PanelBrush"),
            Content = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(22, 18, 22, 20),
                Children =
                {
                    new System.Windows.Controls.TextBlock { Text = "MayhemDeck is still closing", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("TextBrush") },
                    new System.Windows.Controls.TextBlock { Text = "It's shutting down OBS and putting your own OBS setup back. It will open again by itself in a moment.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") },
                },
            },
        };
        bool cancelled = false;
        note.Closed += (_, _) => cancelled = true;
        note.Show();

        var started = DateTime.Now;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (cancelled) { timer.Stop(); Shutdown(); return; }
            if (TryOwn())
            {
                timer.Stop();
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                Begin();
                note.Close();
                return;
            }
            // The other one opened a window after all (it wasn't closing): show it instead.
            var other = OtherGiftDeck();
            if (other != null && other.MainWindowHandle != IntPtr.Zero || (DateTime.Now - started).TotalSeconds > 90)
            {
                timer.Stop();
                note.Close();
                if (other != null && other.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(other.MainWindowHandle, 9);
                    SetForegroundWindow(other.MainWindowHandle);
                }
                else
                    MessageBox.Show("The previous MayhemDeck still hasn't closed. Wait a little, or end MayhemDeck in Task Manager, then open it again.", "MayhemDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
            }
        };
        timer.Start();
    }

    void Begin()
    {
        DispatcherUnhandledException += (s, a) =>
        {
            Log.Write("Unhandled error: " + a.Exception);
            MessageBox.Show(a.Exception.Message, "MayhemDeck ran into a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (!_owned) { base.OnExit(e); return; } // never started (the other GiftDeck is the real one)
        try { Hub.Shutdown(); } catch { }
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
