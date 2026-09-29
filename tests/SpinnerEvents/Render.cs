using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GiftDeck.Models;
using GiftDeck.Services;

// "render <folder>": draws the event editor's Gift Spinner section, the Events list and the Overlays page's
// spinner editor to PNGs, offscreen (no window is shown, nothing is clicked).
static class Render
{
    public static int Run(string outDir)
    {
        int result = 1;
        var t = new Thread(() => result = RunSta(Path.GetFullPath(outDir)));
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    static void SetHub(string property, object value) =>
        typeof(Hub).GetProperty(property, BindingFlags.Public | BindingFlags.Static).GetSetMethod(true).Invoke(null, new[] { value });

    static int RunSta(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MayhemDeck;component/Theme.xaml") });

        var gifts = new GiftCatalog();
        gifts.Load();
        SetHub("Gifts", gifts);
        var overlays = new OverlayService();
        SetHub("Overlays", overlays);
        var rules = new RulesEngine();
        SetHub("Rules", rules);

        var main = new Spinner { Name = "Gift Spinner" };
        var second = new Spinner { Name = "Boss wheel" };
        main.Entries.Add(new SpinnerEntry { Label = "Old prize: confetti", Rarity = Rarity.Uncommon, Actions = { new RuleAction { Type = ActionType.Delay, Number = 5 } } });
        overlays.Config.Spinners.Add(main);
        overlays.Config.Spinners.Add(second);

        Rule Ev(string name, string gift, Rarity? r, string spinner = "", bool on = true) => new Rule
        {
            Name = name, SpinRarity = r, SpinnerId = spinner, Enabled = on,
            Trigger = new RuleTrigger { Type = TriggerType.Gift, GiftName = gift },
            Actions = { new RuleAction { Type = ActionType.KeyPress, Text = "F5" } },
        };
        var spin = new Rule { Name = "Rose spins the wheel", Trigger = new RuleTrigger { Type = TriggerType.Gift, GiftName = "Rose" }, Actions = { new RuleAction { Type = ActionType.SpinWheel } } };
        rules.Rules.Add(spin);
        rules.Rules.Add(Ev("Kickflip", "TikTok", Rarity.Common));
        rules.Rules.Add(Ev("Spawn a tank", "Finger Heart", Rarity.Rare));
        rules.Rules.Add(Ev("Meteor shower", "Galaxy", Rarity.Legendary));
        rules.Rules.Add(Ev("Switched off", "Donut", Rarity.Epic, on: false));
        var boss = Ev("Killer clowns", "Lion", Rarity.Epic, second.Id.ToString());
        rules.Rules.Add(boss);
        rules.Rules.Add(Ev("Not on a wheel", "GG", null));

        // 1. The event editor (its content, laid out at the window's width).
        // (Rendered without actions: the action editors need OBS and GameLink services this harness does not start.)
        var bossView = boss.Clone(); bossView.Id = boss.Id; bossView.Actions.Clear();
        var editor = new GiftDeck.Views.RuleEditorWindow(bossView);
        var content = (FrameworkElement)editor.Content;
        editor.Content = null;
        Save(content, 780, 1500, Path.Combine(outDir, "event-editor.png"));

        var plainView = rules.Rules[6].Clone(); plainView.Actions.Clear();
        var editor2 = new GiftDeck.Views.RuleEditorWindow(plainView);
        var content2 = (FrameworkElement)editor2.Content;
        editor2.Content = null;
        Save(content2, 780, 1500, Path.Combine(outDir, "event-editor-none.png"));

        // 2. The Events list with rarity badges.
        Save(new GiftDeck.Views.EventsView(), 1000, 760, Path.Combine(outDir, "events.png"));

        // 3. The Overlays page's stream tools (spinner part).
        Save(new GiftDeck.Views.StreamToolsPanel(), 1100, 1500, Path.Combine(outDir, "stream-tools.png"));

        Console.WriteLine("Rendered to " + outDir);
        return 0;
    }

    static void Save(FrameworkElement element, int w, int h, string file)
    {
        var host = new Border { Background = (Brush)Application.Current.TryFindResource("BgBrush") ?? Brushes.Black, Child = element, Width = w };
        host.Measure(new Size(w, double.PositiveInfinity));
        double height = Math.Min(h, Math.Max(200, host.DesiredSize.Height));
        host.Height = height;
        host.Measure(new Size(w, height));
        host.Arrange(new Rect(0, 0, w, height));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(w, (int)height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        png.Save(fs);
        host.Child = null;
        Console.WriteLine("  wrote " + file);
    }
}
