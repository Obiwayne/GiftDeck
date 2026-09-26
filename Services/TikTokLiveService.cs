using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GiftDeck.Services;

// Opens and closes a TikTok LIVE through the Streamlabs API, using the user's Streamlabs TikTok LIVE access token.
// This is the same API the Streamlabs desktop app and the open-source stream key generator use.
public class TikTokLiveState
{
    public string Token { get; set; } = "";
    public string Title { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public bool Mature { get; set; }
    public bool AutoObs { get; set; } = true;
    public bool ResetTotalsOnLive { get; set; } = true;
    public string GeneratorConfigPath { get; set; } = "";
    // Send the Aitum Vertical canvas (through the local relay) instead of OBS's main canvas.
    public bool SendVertical { get; set; } = true;
    public string AitumOutput { get; set; } = "Vertical Stream";
    public int RelayPid { get; set; }

    // The stream that is currently open, if any.
    public string StreamId { get; set; }
    public string Server { get; set; }
    public string Key { get; set; }
    public DateTime? StartedAt { get; set; }
}

public class TikTokAccount
{
    public string Username { get; set; }
    public bool CanBeLive { get; set; }
    public string Status { get; set; }
}

public class TikTokCategory
{
    public string Name { get; set; }
    public string Id { get; set; }
    public override string ToString() => Name;
}

public class TikTokLiveService
{
    public event Action StatusChanged;
    public void NotifyChanged() => StatusChanged?.Invoke();

    public TikTokLiveState State { get; private set; } = new TikTokLiveState();
    public bool Live => !string.IsNullOrEmpty(State.StreamId);

    // Asks TikTok's own web API whether the account is actually showing as LIVE (status 2).
    public static async Task<bool> IsShowingLiveAsync(string username)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        var json = await http.GetStringAsync("https://www.tiktok.com/api-live/user/room/?aid=1988&sourceType=54&uniqueId=" + Uri.EscapeDataString(username));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("data", out var d)
               && d.TryGetProperty("liveRoom", out var room)
               && room.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() == 2;
    }

    readonly HttpClient _http;
    const string Api = "https://streamlabs.com/api/v5/slobs/tiktok/";

    public TikTokLiveService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) StreamlabsDesktop/1.17.0 Chrome/122.0.6261.156 Electron/29.3.1 Safari/537.36");
    }

    public void Load()
    {
        State = Storage.Load<TikTokLiveState>("tiktok.json") ?? new TikTokLiveState();
        if (string.IsNullOrEmpty(State.GeneratorConfigPath))
        {
            var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "StreamLabsTikTokStreamKeyGeneratorRelease-win-2.0.9", "config.json");
            if (File.Exists(guess)) State.GeneratorConfigPath = guess;
        }
    }

    public void Save() => Storage.Save("tiktok.json", State);

    // Reads the token the Stream Key Generator saved, so it does not have to be pasted by hand.
    public string ImportTokenFromGenerator(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new Exception("config.json was not found at " + path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(token)) throw new Exception("That config.json has no token in it. Log in with the generator first.");
        State.Token = token.Trim();
        if (string.IsNullOrEmpty(State.Title) && root.TryGetProperty("title", out var ti)) State.Title = ti.GetString() ?? "";
        if (string.IsNullOrEmpty(State.CategoryName) && root.TryGetProperty("game", out var g)) State.CategoryName = g.GetString() ?? "";
        if (root.TryGetProperty("audience_type", out var a)) State.Mature = a.ToString() == "1";
        Save();
        StatusChanged?.Invoke();
        return State.Token;
    }

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(State.Token)) throw new Exception("No TikTok token. Import it from the Stream Key Generator or paste it first.");
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", State.Token);
        return req;
    }

    async Task<JsonElement> SendAsync(HttpRequestMessage req)
    {
        var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new Exception("Streamlabs rejected the token. Log in again with the Stream Key Generator and import the new token.");
        if (!res.IsSuccessStatusCode) throw new Exception($"Streamlabs answered {(int)res.StatusCode}: {Short(text)}");
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch { throw new Exception("Streamlabs sent an unexpected reply: " + Short(text)); }
    }

    public async Task<TikTokAccount> InfoAsync()
    {
        var r = await SendAsync(Request(HttpMethod.Get, "info"));
        var acc = new TikTokAccount { CanBeLive = r.TryGetProperty("can_be_live", out var c) && c.ValueKind == JsonValueKind.True };
        if (r.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("username", out var n)) acc.Username = n.GetString();
        if (r.TryGetProperty("application_status", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("status", out var st)) acc.Status = st.GetString();
        return acc;
    }

    public async Task<List<TikTokCategory>> SearchCategoriesAsync(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return new List<TikTokCategory>();
        if (query.Length > 25) query = query.Substring(0, 25); // longer names make the API fail
        var r = await SendAsync(Request(HttpMethod.Get, "info?category=" + Uri.EscapeDataString(query)));
        var list = new List<TikTokCategory>();
        if (r.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            foreach (var c in cats.EnumerateArray())
                list.Add(new TikTokCategory
                {
                    Name = c.TryGetProperty("full_name", out var fn) ? fn.GetString() : "",
                    Id = c.TryGetProperty("game_mask_id", out var id) ? id.GetString() : "",
                });
        list.Add(new TikTokCategory { Name = "Other", Id = "" });
        return list;
    }

    public async Task<(string server, string key)> StartAsync()
    {
        if (Live) throw new Exception("A LIVE is already open. End it first.");
        var form = new MultipartFormDataContent
        {
            { new StringContent(string.IsNullOrWhiteSpace(State.Title) ? "LIVE" : State.Title), "title" },
            { new StringContent("win32"), "device_platform" },
            { new StringContent(State.CategoryId ?? ""), "category" },
            { new StringContent(State.Mature ? "1" : "0"), "audience_type" },
        };
        var req = Request(HttpMethod.Post, "stream/start");
        req.Content = form;
        var r = await SendAsync(req);
        if (!r.TryGetProperty("id", out var id) || !r.TryGetProperty("rtmp", out var rtmp) || !r.TryGetProperty("key", out var key))
        {
            var msg = r.TryGetProperty("message", out var m) ? m.GetString() : r.GetRawText();
            throw new Exception("TikTok did not open the LIVE: " + Short(msg));
        }
        State.StreamId = id.ToString();
        State.Server = rtmp.GetString();
        State.Key = key.GetString();
        State.StartedAt = DateTime.Now;
        Save();
        Log.Write("TikTok LIVE opened (stream " + State.StreamId + ")");
        StatusChanged?.Invoke();
        return (State.Server, State.Key);
    }

    public async Task EndAsync()
    {
        if (!Live) return;
        var streamId = State.StreamId;
        try
        {
            var r = await SendAsync(Request(HttpMethod.Post, "stream/" + streamId + "/end"));
            bool ok = r.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            Log.Write(ok ? "TikTok LIVE ended" : "TikTok answered without success when ending the LIVE: " + Short(r.GetRawText()));
        }
        finally
        {
            State.StreamId = null;
            State.Server = null;
            State.Key = null;
            State.StartedAt = null;
            Save();
            StatusChanged?.Invoke();
        }
    }

    // Forget a stream we think is open (for example after TikTok closed it on its own).
    public void Forget()
    {
        State.StreamId = null;
        State.Server = null;
        State.Key = null;
        State.StartedAt = null;
        Save();
        StatusChanged?.Invoke();
    }

    static string Short(string s) => s == null ? "" : s.Length > 240 ? s.Substring(0, 240) : s;
}
