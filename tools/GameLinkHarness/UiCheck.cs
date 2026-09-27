using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GiftDeck.Models;
using GiftDeck.Services;
using GiftDeck.Views;

// Drives the "Run a game command" editor without a mouse: builds the real ActionEditor in an off-screen
// window (GiftDeck's own theme), types in the search box, picks a command, changes a field, presses Test
// through the control's own Click event, and saves screenshots.
static class UiCheck
{
    public static int Run(GameLinkService svc, string shotsDir, Action<bool, string> check)
    {
        // Only the services the editor touches; nothing else of GiftDeck is started.
        typeof(Hub).GetProperty("GameLink").SetValue(null, svc);
        typeof(Hub).GetProperty("Obs").SetValue(null, new ObsService());
        Directory.CreateDirectory(shotsDir);
        int result = 0;
        var t = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GiftDeck;component/Theme.xaml") });
            app.Startup += async (s, e) =>
            {
                try { await Steps(svc, shotsDir, check); }
                catch (Exception ex) { check(false, "UI check crashed: " + ex); result = 1; }
                app.Shutdown();
            };
            app.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    static async Task Steps(GameLinkService svc, string shotsDir, Action<bool, string> check)
    {
        var fresh = new RuleAction { Type = ActionType.GameCommand };
        var saved = new RuleAction { Type = ActionType.GameCommand, Text = "gta5:chaosmod", Text2 = "time_night" };
        var ed1 = new ActionEditor { DataContext = fresh, Width = 700 };
        var ed2 = new ActionEditor { DataContext = saved, Width = 700 };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(ed1);
        panel.Children.Add(new Border { Height = 12 });
        panel.Children.Add(ed2);
        var win = new Window
        {
            Width = 760, Height = 1100, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Background = (Brush)Application.Current.FindResource("BgBrush"), Content = panel,
        };
        win.Show();
        await Idle();

        T Find<T>(ActionEditor ed, string name) => (T)ed.FindName(name);
        var games = Find<ComboBox>(ed1, "GameCombo");
        check(games.Items.Count >= 1 && ((Choice)games.SelectedItem).Label == "GTA V (Legacy)", "editor: game dropdown shows GTA V (Legacy)");
        var list = Find<ListBox>(ed1, "CommandList");
        check(list.Items.Count == 608, $"editor: command list has all 608 commands ({list.Items.Count})");
        int containers = CountContainers(list);
        check(containers > 0 && containers < 80, $"editor: list is virtualized ({containers} rows built for 608 commands)");
        check(Find<ComboBox>(ed1, "CategoryCombo").Items.Count > 5, "editor: category filter filled");
        Shot(win, Path.Combine(shotsDir, "1-list.png"));

        Find<TextBox>(ed1, "CommandSearch").Text = "spawn veh";
        await Idle();
        check(list.Items.Count == 1, $"editor: searching 'spawn veh' leaves 1 (the live command replaced the file's) = {list.Items.Count}");
        var item = list.Items.Cast<GameCatalogItem>().First(i => i.TargetId == "gta5:testmod");
        list.SelectedItem = item;
        await Idle();
        check(fresh.Text == "gta5:testmod" && fresh.Text2 == "spawn_vehicle", $"editor: picking stores target and command ({fresh.Text} / {fresh.Text2})");
        check(fresh.Args == """{"model":"adder","plate":"{user}"}""", "editor: defaults stored as JSON: " + fresh.Args);
        var args = Find<WrapPanel>(ed1, "GameArgsPanel");
        check(args.Children.Count == 2, "editor: two argument fields built");
        var modelCombo = ((StackPanel)args.Children[0]).Children.OfType<ComboBox>().Single();
        modelCombo.SelectedItem = "rhino";
        var plateBox = ((StackPanel)args.Children[1]).Children.OfType<TextBox>().Single();
        plateBox.Text = "{user} {count}";
        await Idle();
        check(fresh.Args == """{"model":"rhino","plate":"{user} {count}"}""", "editor: edited fields stored: " + fresh.Args);
        check(fresh.Summary() == "Game: Spawn Vehicle", "summary: " + fresh.Summary());

        Find<Button>(ed1, "GameTestButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var resultText = Find<TextBlock>(ed1, "GameTestResult");
        for (int i = 0; i < 60 && (resultText.Text == "" || resultText.Text.StartsWith("Running")); i++) await Task.Delay(100);
        check(resultText.Text == "Done: did spawn_vehicle", "editor: Test button ran it: " + resultText.Text);
        await Idle();
        Shot(win, Path.Combine(shotsDir, "2-picked.png"));

        // Number field
        Find<TextBox>(ed1, "CommandSearch").Text = "money";
        await Idle();
        list.SelectedItem = list.Items.Cast<GameCatalogItem>().Single();
        await Idle();
        check(fresh.Args == """{"amount":1000}""", "editor: number default stored as a number: " + fresh.Args);
        ((StackPanel)args.Children[0]).Children.OfType<TextBox>().Single().Text = "{count}";
        await Idle();
        check(fresh.Args == """{"amount":"{count}"}""", "editor: a template in a number field is kept as text: " + fresh.Args);

        // Saved event for a mod that isn't running
        check(Find<TextBlock>(ed2, "ChosenCommand").Text == "Night time  ·  Chaos Mod V", "editor: saved offline command shows its name: " + Find<TextBlock>(ed2, "ChosenCommand").Text);
        check(saved.Text == "gta5:chaosmod" && saved.Text2 == "time_night", "editor: opening a saved event doesn't change it");
        var testBtn2 = Find<Button>(ed2, "GameTestButton");
        testBtn2.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var r2 = Find<TextBlock>(ed2, "GameTestResult");
        for (int i = 0; i < 30 && (r2.Text == "" || r2.Text.StartsWith("Running")); i++) await Task.Delay(100);
        check(r2.Text == "Didn't work: Chaos Mod V isn't connected", "editor: Test on a mod that isn't running: " + r2.Text);
        await Idle();
        Shot(win, Path.Combine(shotsDir, "3-final.png"));
        win.Close();
    }

    static int CountContainers(DependencyObject d)
    {
        int n = d is ListBoxItem ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) n += CountContainers(VisualTreeHelper.GetChild(d, i));
        return n;
    }

    static async Task Idle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    static void Shot(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(w.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bmp.Render(bg);
        bmp.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
