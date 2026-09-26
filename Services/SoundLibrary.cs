using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace GiftDeck.Services;

// Searches MyInstants (the same library TikFinity uses) and saves chosen clips to the GiftDeck sounds folder.
public class SoundResult
{
    public string Title { get; set; }
    public string Url { get; set; }
    public override string ToString() => Title;
}

public static class SoundLibrary
{
    public static readonly string Dir = Path.Combine(Storage.Dir, "sounds");
    static readonly string CacheDir = Path.Combine(Storage.Dir, "sounds", "preview");
    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
        return c;
    }

    // Each result on the page has a play('/media/sounds/x.mp3' ...) button followed by its title link.
    static readonly Regex ResultRx = new Regex(
        @"play\('(?<url>/media/sounds/[^']+)'[\s\S]*?<a href=""/[a-z]{2}/instant/[^""]+"" class=""instant-link[^""]*"">(?<title>[^<]*)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
    static readonly string CurlExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");

    // MyInstants rejects .NET's web requests (it fingerprints the connection), so fetch through the
    // curl that ships with Windows, and fall back to HttpClient if that is missing.
    static async Task<byte[]> FetchAsync(string url)
    {
        if (File.Exists(CurlExe))
        {
            var tmp = Path.Combine(Path.GetTempPath(), "giftdeck_" + Guid.NewGuid().ToString("N"));
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(CurlExe)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                };
                foreach (var a in new[] { "-s", "-L", "--max-time", "25", "-A", Agent, "-H", "Accept: text/html,application/xhtml+xml,*/*", "-H", "Accept-Language: en-GB,en;q=0.9", "-o", tmp, url })
                    psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi);
                await p.WaitForExitAsync();
                if (p.ExitCode == 0 && File.Exists(tmp)) return await File.ReadAllBytesAsync(tmp);
                throw new Exception("curl exit code " + p.ExitCode);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        return await Http.GetByteArrayAsync(url);
    }

    public static async Task<List<SoundResult>> SearchAsync(string query)
    {
        query = (query ?? "").Trim();
        var list = new List<SoundResult>();
        if (query.Length == 0) return list;
        var html = System.Text.Encoding.UTF8.GetString(await FetchAsync("https://www.myinstants.com/en/search/?name=" + Uri.EscapeDataString(query)));
        if (html.Length < 2000 || !html.Contains("instant-link")) throw new Exception("the site did not return search results (it may be blocking automated requests right now)");
        foreach (Match m in ResultRx.Matches(html))
        {
            var title = WebUtility.HtmlDecode(m.Groups["title"].Value).Trim();
            var url = "https://www.myinstants.com" + m.Groups["url"].Value;
            if (title.Length == 0 || list.Any(x => x.Url == url)) continue;
            list.Add(new SoundResult { Title = title, Url = url });
        }
        return list;
    }

    // Fetches the clip for a preview; kept in a cache folder so replaying is instant.
    public static async Task<string> PreviewFileAsync(SoundResult r)
    {
        Directory.CreateDirectory(CacheDir);
        var file = Path.Combine(CacheDir, SafeName(Path.GetFileNameWithoutExtension(r.Url)) + ".mp3");
        if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await FetchAsync(r.Url));
        return file;
    }

    // Saves the clip permanently under a readable name and returns the path to use in the action.
    public static async Task<string> SaveAsync(SoundResult r)
    {
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, SafeName(r.Title) + ".mp3");
        if (!File.Exists(file))
        {
            var preview = await PreviewFileAsync(r);
            File.Copy(preview, file);
        }
        return file;
    }

    static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length > 60) cleaned = cleaned.Substring(0, 60).Trim();
        return cleaned.Length == 0 ? "sound" : cleaned;
    }
}
