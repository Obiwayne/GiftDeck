using System.Collections.Concurrent;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Cover art for the Games page. A pack's own cover (Packs\<id>\cover.png) when it has one, else Steam's header
// image (460x215), downloaded once and kept in %APPDATA%\GiftDeck\covers\<steamAppId>.jpg. Everything happens off the
// UI thread; images come back frozen. A game without a picture (or offline on first run) gets a generated tile.
static class CoverCache
{
    public const double Aspect = 215.0 / 460.0; // Steam header art: height / width

    static readonly HttpClient Http = MakeClient();
    static readonly SemaphoreSlim Downloads = new SemaphoreSlim(4); // a few at a time: 60 covers on a first visit
    static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource>>> Loads = new ConcurrentDictionary<string, Lazy<Task<ImageSource>>>();

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GiftDeck/1.0");
        return c;
    }

    // The same task for the same game for the whole session, so tiles and the game page never load twice.
    // Completes with null when there's no picture (the caller keeps the generated tile).
    public static Task<ImageSource> GetAsync(GamePack p)
    {
        if (p == null) return Task.FromResult<ImageSource>(null);
        var key = p.Id.ToLowerInvariant() + "|" + p.CoverPath + "|" + p.SteamAppId;
        string local = p.CoverPath;
        int appId = p.SteamAppId;
        string name = p.Name;
        return Loads.GetOrAdd(key, _ => new Lazy<Task<ImageSource>>(() => Task.Run(async () =>
        {
            var img = await LoadAsync(local, appId, name);
            if (img == null && appId > 0) Loads.TryRemove(key, out Lazy<Task<ImageSource>> _); // offline? try again next time the page is built
            return img;
        }))).Value;
    }

    // Sets an Image's picture once it's ready (right away when it already is), fading it in.
    public static void Apply(Image target, GamePack p, Func<bool> stillWanted = null, Image alsoTarget = null)
    {
        var task = GetAsync(p);
        if (task.IsCompleted)
        {
            var now = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            Show(target, now, false);
            Show(alsoTarget, now, false);
            return;
        }
        Show(target, null, false);
        Show(alsoTarget, null, false);
        // Back on the image's own thread, whatever SynchronizationContext the caller had.
        task.ContinueWith(t => target.Dispatcher.BeginInvoke(() =>
        {
            var src = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (stillWanted != null && !stillWanted()) return;
            Show(target, src, true);
            Show(alsoTarget, src, true);
        }), TaskScheduler.Default);
    }

    static void Show(Image img, ImageSource src, bool fade)
    {
        if (img == null) return;
        img.Source = src;
        img.Visibility = src == null ? Visibility.Collapsed : Visibility.Visible;
        if (src == null) return;
        double to = img.Tag is double d ? d : 1; // Tag: the image's resting opacity (the page backdrop is faint)
        if (fade) img.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, to, TimeSpan.FromMilliseconds(220)));
        else { img.BeginAnimation(UIElement.OpacityProperty, null); img.Opacity = to; }
    }

    static async Task<ImageSource> LoadAsync(string local, int appId, string name)
    {
        try
        {
            if (!string.IsNullOrEmpty(local) && File.Exists(local))
            {
                var img = Decode(File.ReadAllBytes(local), 560);
                if (img != null) return img;
            }
            if (appId <= 0) return null;

            var file = Storage.PathFor($"covers\\{appId}.jpg");
            if (File.Exists(file))
            {
                var img = Decode(File.ReadAllBytes(file), 0);
                if (img != null) return img;
                try { File.Delete(file); } catch { } // a broken download: fetch it again
            }

            byte[] bytes = null;
            await Downloads.WaitAsync();
            try
            {
                foreach (var url in new[]
                {
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                })
                {
                    try
                    {
                        using var resp = await Http.GetAsync(url);
                        if (!resp.IsSuccessStatusCode) continue;
                        bytes = await resp.Content.ReadAsByteArrayAsync();
                        if (bytes.Length > 0) break;
                    }
                    catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException) { bytes = null; }
                }
            }
            finally { Downloads.Release(); }
            if (bytes == null || bytes.Length == 0) return null;

            var decoded = Decode(bytes, 0);
            if (decoded == null) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var tmp = file + ".part";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, file, true);
            }
            catch (Exception e) { Log.Write($"Couldn't keep the cover for {name}: {e.Message}"); }
            return decoded;
        }
        catch (Exception e)
        {
            Log.Write($"No cover for {name}: {e.Message}");
            return null;
        }
    }

    static ImageSource Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // The tile a game gets while its picture loads, or for good when it has none: a gradient in the game's own
    // colour (from its id) with its initials.
    public static FrameworkElement Fallback(GamePack p, double fontSize = 40)
    {
        double hue = GameCatalog.Hue(p?.Id ?? p?.Name);
        var bg = new LinearGradientBrush(GameCatalog.FromHsl(hue, 0.55, 0.40), GameCatalog.FromHsl(hue + 38, 0.60, 0.18), new Point(0, 0), new Point(1, 1));
        bg.Freeze();
        var glow = new RadialGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
        {
            Center = new Point(0.2, 0.1), GradientOrigin = new Point(0.2, 0.1), RadiusX = 0.9, RadiusY = 1.1,
        };
        glow.Freeze();
        var grid = new Grid { Background = bg };
        grid.Children.Add(new Border { Background = glow });
        grid.Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16),
            Child = new TextBlock
            {
                Text = GameCatalog.Initials(p?.Name), FontSize = fontSize, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        return grid;
    }
}
