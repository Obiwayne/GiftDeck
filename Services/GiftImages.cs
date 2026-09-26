using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GiftDeck.Services;

// Downloads gift pictures once (as PNG, which TikTok's CDN serves when asked) and keeps them on disk.
public static class GiftImages
{
    static readonly string Dir = Path.Combine(Storage.Dir, "gifts");
    static readonly HttpClient Http = MakeClient();
    static readonly ConcurrentDictionary<string, Task<ImageSource>> Pending = new ConcurrentDictionary<string, Task<ImageSource>>();
    static readonly ConcurrentDictionary<string, ImageSource> Loaded = new ConcurrentDictionary<string, ImageSource>();

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 GiftDeck");
        return c;
    }

    public static Task<ImageSource> LoadAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource>(null);
        if (Loaded.TryGetValue(url, out var done)) return Task.FromResult(done);
        return Pending.GetOrAdd(url, u => Task.Run(() => Fetch(u)));
    }

    // The PNG on disk for a picture address, downloading it first if needed. Null if it cannot be fetched.
    public static async Task<string> CachedFileAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, Hash(url) + ".png");
            if (!File.Exists(file))
            {
                var bytes = await Http.GetByteArrayAsync(PngUrl(url));
                await File.WriteAllBytesAsync(file, bytes);
            }
            return file;
        }
        catch (Exception e)
        {
            Log.Write("Gift picture failed: " + e.Message);
            return null;
        }
    }

    static async Task<ImageSource> Fetch(string url)
    {
        try
        {
            var file = await CachedFileAsync(url);
            if (file == null) return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 96;
            img.UriSource = new Uri(file, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            Loaded[url] = img;
            return img;
        }
        catch (Exception e)
        {
            Log.Write("Gift picture failed: " + e.Message);
            return null;
        }
        finally
        {
            Pending.TryRemove(url, out _);
        }
    }

    // TikTok's image CDN picks the format from the suffix: "...png~tplv-obj.webp" becomes "...png~tplv-obj.png".
    static string PngUrl(string url) => url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? url.Substring(0, url.Length - 5) + ".png" : url;

    static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
