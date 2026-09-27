using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GiftDeck.Services;

public class KickChannelInfo
{
    public long ChannelId { get; set; }
    public long ChatroomId { get; set; }
    public string Slug { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Live { get; set; }
}

// Kick's public (no login) web endpoints: channel name -> channel and chatroom ids, and the Pusher app to listen on.
// kick.com sits behind Cloudflare, which sometimes turns .NET's HttpClient away (403) while curl.exe gets through,
// so each call falls back to Windows' own curl.exe.
public static class KickApi
{
    // Kick's public Pusher app (what kick.com's own chat uses for signed-out viewers). Asked for fresh on each
    // connect when possible (web.kick.com tells the web player which app to use); these are the fallback.
    public const string DefaultAppKey = "32cbd69e4b950bf97679";
    public const string DefaultCluster = "us2";

    static readonly HttpClient Http = CreateClient();

    // Which way the last successful call got through ("HttpClient" or "curl.exe"), for the log and the tests.
    public static string LastRoute { get; private set; } = "";
    // Tests only: go straight to curl.exe, as if Cloudflare had turned HttpClient away.
    public static bool SkipHttpClient { get; set; }

    static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return h;
    }

    // Accepts "name", "@name", "kick.com/name" or a full link.
    public static string CleanName(string input)
    {
        var s = (input ?? "").Trim().TrimStart('@');
        int i = s.IndexOf("kick.com/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) s = s.Substring(i + "kick.com/".Length);
        s = s.Split('/', '?', '#')[0];
        return s.Trim().ToLowerInvariant();
    }

    public static async Task<KickChannelInfo> GetChannelAsync(string name, CancellationToken ct = default)
    {
        var slug = CleanName(name);
        if (slug.Length == 0) throw new Exception("No Kick channel name set.");
        var url = "https://kick.com/api/v2/channels/" + Uri.EscapeDataString(slug);
        var (status, body) = await GetAsync(url, null, ct);
        if (status == 404) throw new Exception($"There's no Kick channel called \"{slug}\".");
        if (status != 200 || string.IsNullOrWhiteSpace(body)) throw new Exception($"Kick didn't answer the channel lookup (HTTP {status}).");

        using var doc = JsonDocument.Parse(body);
        var d = doc.RootElement;
        var chatroom = J.Prop(d, "chatroom");
        var user = J.Prop(d, "user");
        var live = J.Prop(d, "livestream");
        var info = new KickChannelInfo
        {
            ChannelId = J.Num(d, 0, "id"),
            ChatroomId = chatroom != null ? J.Num(chatroom.Value, 0, "id") : 0,
            Slug = J.Str(d, "slug") ?? slug,
            DisplayName = (user != null ? J.Str(user.Value, "username") : null) ?? slug,
            Live = live != null && live.Value.ValueKind == JsonValueKind.Object && (!J.Has(live.Value, "is_live") || J.Bool(live.Value, "is_live")),
        };
        if (info.ChannelId == 0 || info.ChatroomId == 0) throw new Exception("Kick's answer had no chatroom for " + slug + ".");
        return info;
    }

    // The Pusher app Kick's web player is told to use for this channel's chat; the known one if that fails.
    public static async Task<(string key, string cluster)> GetPusherAppAsync(long channelId, CancellationToken ct = default)
    {
        try
        {
            var body = "{\"client\":{\"id\":\"giftdeck-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "\",\"type\":\"web\"},\"capabilities\":{\"accepted_providers\":[{\"provider\":\"pusher\"}]}}";
            var (status, text) = await GetAsync($"https://web.kick.com/api/v1/realtime/channels/{channelId}/chat/connection", body, ct);
            if (status == 200 && !string.IsNullOrEmpty(text))
            {
                using var doc = JsonDocument.Parse(text);
                var data = J.Prop(doc.RootElement, "data");
                var conns = data != null ? J.Prop(data.Value, "connections") : null;
                if (conns != null && conns.Value.ValueKind == JsonValueKind.Array)
                    foreach (var c in conns.Value.EnumerateArray())
                    {
                        if (J.Str(c, "provider") != "pusher") continue;
                        var cred = J.Prop(c, "credentials");
                        var key = cred != null ? J.Str(cred.Value, "app_key") : null;
                        var cluster = cred != null ? J.Str(cred.Value, "cluster") : null;
                        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(cluster)) return (key, cluster);
                    }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return (DefaultAppKey, DefaultCluster);
    }

    // GET (or POST a JSON body) with HttpClient, then curl.exe if Cloudflare says no.
    static async Task<(int status, string body)> GetAsync(string url, string postJson, CancellationToken ct)
    {
        int status = 0;
        if (!SkipHttpClient) try
        {
            using var req = new HttpRequestMessage(postJson == null ? HttpMethod.Get : HttpMethod.Post, url);
            if (postJson != null) req.Content = new StringContent(postJson, Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct);
            status = (int)res.StatusCode;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (status == 200 && LooksLikeJson(text)) { LastRoute = "HttpClient"; return (200, text); }
            if (status == 404) { LastRoute = "HttpClient"; return (404, text); }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }

        var viaCurl = await CurlAsync(url, postJson, ct);
        if (viaCurl.status != 0) { LastRoute = "curl.exe"; return viaCurl; }
        return (status, null);
    }

    static bool LooksLikeJson(string s) => s != null && s.TrimStart().StartsWith("{");

    static async Task<(int status, string body)> CurlAsync(string url, string postJson, CancellationToken ct)
    {
        var curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        if (!File.Exists(curl)) curl = "curl.exe";
        var psi = new ProcessStartInfo(curl)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = postJson != null,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-s", "--max-time", "15", "-H", "Accept: application/json", "-w", "\n%{http_code}" }) psi.ArgumentList.Add(a);
        if (postJson != null)
            foreach (var a in new[] { "-X", "POST", "-H", "Content-Type: application/json", "--data-binary", "@-" }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(url);
        try
        {
            using var p = Process.Start(psi);
            if (postJson != null) { await p.StandardInput.WriteAsync(postJson); p.StandardInput.Close(); }
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            int nl = output.LastIndexOf('\n');
            if (nl < 0 || !int.TryParse(output.Substring(nl + 1).Trim(), out int code) || code == 0) return (0, null);
            var body = output.Substring(0, nl);
            if (code == 200 && !LooksLikeJson(body)) return (0, null);
            return (code, body);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return (0, null); }
    }
}
