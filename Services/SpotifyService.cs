using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GiftDeck.Services;

// Spotify Web API with the PKCE login flow (no client secret needed).
public class SpotifyService
{
    public event Action StatusChanged;

    public bool Linked => _tokens?.RefreshToken != null;
    public string AccountName { get; private set; }
    public string LastError { get; private set; }
    public bool Linking { get; private set; }

    class Tokens
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    Tokens _tokens;
    readonly HttpClient _http = new HttpClient();
    const string Scopes = "user-modify-playback-state user-read-playback-state user-read-currently-playing";

    public string RedirectUri => $"http://127.0.0.1:{Hub.Settings.SpotifyCallbackPort}/callback";

    public void Load()
    {
        _tokens = Storage.Load<Tokens>("spotify.json");
        if (Linked) _ = RefreshProfileAsync();
    }

    public void Unlink()
    {
        _tokens = null;
        AccountName = null;
        Storage.Delete("spotify.json");
        StatusChanged?.Invoke();
    }

    public async Task LinkAsync()
    {
        var clientId = (Hub.Settings.SpotifyClientId ?? "").Trim();
        if (clientId.Length == 0) throw new Exception("Enter your Spotify Client ID first");
        if (Linking) throw new Exception("A Spotify login is already in progress");
        Linking = true;
        StatusChanged?.Invoke();
        try
        {
            var verifier = RandomString(64);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = RandomString(16);

            var listener = new TcpListener(IPAddress.Loopback, Hub.Settings.SpotifyCallbackPort);
            listener.Start();
            try
            {
                var url = "https://accounts.spotify.com/authorize?" +
                          "client_id=" + Uri.EscapeDataString(clientId) +
                          "&response_type=code" +
                          "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                          "&scope=" + Uri.EscapeDataString(Scopes) +
                          "&code_challenge_method=S256&code_challenge=" + challenge +
                          "&state=" + state;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                string code = null;
                var deadline = DateTime.Now.AddMinutes(3);
                while (code == null && DateTime.Now < deadline)
                {
                    var acceptTask = listener.AcceptTcpClientAsync();
                    var done = await Task.WhenAny(acceptTask, Task.Delay(1000));
                    if (done != acceptTask) continue;
                    using var client = await acceptTask;
                    using var stream = client.GetStream();
                    stream.ReadTimeout = 3000;
                    var buf = new byte[8192];
                    int n = await stream.ReadAsync(buf, 0, buf.Length);
                    var request = Encoding.ASCII.GetString(buf, 0, n);
                    var line = request.Split('\n')[0];
                    var parts = line.Split(' ');
                    string body;
                    if (parts.Length >= 2 && parts[1].StartsWith("/callback"))
                    {
                        var q = ParseQuery(parts[1]);
                        if (q.TryGetValue("error", out var err))
                            body = "Spotify said: " + err + ". You can close this window.";
                        else if (q.TryGetValue("state", out var st) && st == state && q.TryGetValue("code", out var c))
                        {
                            code = c;
                            body = "MayhemDeck is now linked to Spotify. You can close this window.";
                        }
                        else body = "Unexpected reply from Spotify. Please try again from MayhemDeck.";
                    }
                    else body = "MayhemDeck is waiting for Spotify.";
                    var html = "<html><body style='font-family:Segoe UI;background:#0F1117;color:#E8EAF0;display:flex;align-items:center;justify-content:center;height:100vh'><h2>" + body + "</h2></body></html>";
                    var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + Encoding.UTF8.GetByteCount(html) + "\r\nConnection: close\r\n\r\n" + html;
                    var rb = Encoding.UTF8.GetBytes(resp);
                    await stream.WriteAsync(rb, 0, rb.Length);
                }
                if (code == null) throw new Exception("Timed out waiting for the Spotify login");

                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = clientId,
                    ["code_verifier"] = verifier,
                };
                await ExchangeAsync(form);
                await RefreshProfileAsync();
                Log.Write("Spotify linked" + (AccountName != null ? " as " + AccountName : ""));
            }
            finally
            {
                listener.Stop();
            }
        }
        catch (Exception e)
        {
            LastError = e.Message;
            throw;
        }
        finally
        {
            Linking = false;
            StatusChanged?.Invoke();
        }
    }

    async Task ExchangeAsync(Dictionary<string, string> form)
    {
        var res = await _http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception("Spotify token request failed: " + Short(text));
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        var t = _tokens ?? new Tokens();
        t.AccessToken = r.GetProperty("access_token").GetString();
        if (r.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String) t.RefreshToken = rt.GetString();
        t.ExpiresAt = DateTime.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32());
        _tokens = t;
        Storage.Save("spotify.json", _tokens);
    }

    async Task<string> TokenAsync()
    {
        if (_tokens == null || _tokens.RefreshToken == null) throw new Exception("Spotify is not linked");
        if (DateTime.UtcNow > _tokens.ExpiresAt.AddMinutes(-1))
        {
            await ExchangeAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _tokens.RefreshToken,
                ["client_id"] = (Hub.Settings.SpotifyClientId ?? "").Trim(),
            });
        }
        return _tokens.AccessToken;
    }

    async Task<JsonElement?> ApiAsync(HttpMethod method, string path, bool retry = true)
    {
        var token = await TokenAsync();
        using var req = new HttpRequestMessage(method, "https://api.spotify.com" + path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Put || method == HttpMethod.Post) req.Content = new StringContent("", Encoding.UTF8, "application/json");
        var res = await _http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.Unauthorized && retry)
        {
            _tokens.ExpiresAt = DateTime.MinValue;
            return await ApiAsync(method, path, false);
        }
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            string msg = Short(text);
            try
            {
                using var ed = JsonDocument.Parse(text);
                if (ed.RootElement.TryGetProperty("error", out var err))
                {
                    var reason = err.TryGetProperty("reason", out var rs) ? rs.GetString() : null;
                    msg = err.TryGetProperty("message", out var m) ? m.GetString() : msg;
                    if (reason == "NO_ACTIVE_DEVICE") msg = "No active Spotify device. Start playing something in Spotify first.";
                    else if (reason == "PREMIUM_REQUIRED") msg = "Spotify Premium is required to control playback.";
                }
            }
            catch { }
            throw new Exception(msg);
        }
        if (res.StatusCode == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public async Task RefreshProfileAsync()
    {
        try
        {
            var me = await ApiAsync(HttpMethod.Get, "/v1/me");
            AccountName = me?.GetProperty("display_name").GetString() ?? me?.GetProperty("id").GetString();
            LastError = null;
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
        StatusChanged?.Invoke();
    }

    public Task PlayAsync() => ApiAsync(HttpMethod.Put, "/v1/me/player/play");
    public Task PauseAsync() => ApiAsync(HttpMethod.Put, "/v1/me/player/pause");
    public Task NextAsync() => ApiAsync(HttpMethod.Post, "/v1/me/player/next");
    public Task PreviousAsync() => ApiAsync(HttpMethod.Post, "/v1/me/player/previous");
    public Task SetVolumeAsync(int percent) => ApiAsync(HttpMethod.Put, "/v1/me/player/volume?volume_percent=" + Math.Clamp(percent, 0, 100));

    public async Task<int?> GetVolumeAsync()
    {
        var p = await ApiAsync(HttpMethod.Get, "/v1/me/player");
        if (p == null) return null;
        if (p.Value.TryGetProperty("device", out var d) && d.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        return null;
    }

    public async Task ChangeVolumeAsync(int delta)
    {
        var cur = await GetVolumeAsync() ?? 50;
        await SetVolumeAsync(cur + delta);
    }

    public async Task<string> NowPlayingAsync()
    {
        var p = await ApiAsync(HttpMethod.Get, "/v1/me/player/currently-playing");
        if (p == null || !p.Value.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        var name = item.GetProperty("name").GetString();
        var artists = item.TryGetProperty("artists", out var ar) ? string.Join(", ", ar.EnumerateArray().Select(a => a.GetProperty("name").GetString())) : "";
        bool playing = p.Value.TryGetProperty("is_playing", out var ip) && ip.GetBoolean();
        return (playing ? "" : "(paused) ") + name + (artists.Length > 0 ? " by " + artists : "");
    }

    // Searches for a track and adds it to the queue. Returns "Track by Artist".
    public async Task<string> RequestAsync(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) throw new Exception("Empty song request");
        string uri, label;
        if (query.Contains("open.spotify.com/track/") || query.StartsWith("spotify:track:"))
        {
            var id = query.Contains("spotify:track:") ? query.Substring(query.IndexOf("spotify:track:") + 14) : query.Substring(query.IndexOf("/track/") + 7);
            id = new string(id.TakeWhile(ch => char.IsLetterOrDigit(ch)).ToArray());
            var track = await ApiAsync(HttpMethod.Get, "/v1/tracks/" + id);
            CheckExplicit(track.Value);
            uri = track.Value.GetProperty("uri").GetString();
            label = TrackLabel(track.Value);
        }
        else
        {
            var r = await ApiAsync(HttpMethod.Get, "/v1/search?q=" + Uri.EscapeDataString(query) + "&type=track&limit=1");
            var items = r.Value.GetProperty("tracks").GetProperty("items");
            if (items.GetArrayLength() == 0) throw new Exception("No Spotify track matched \"" + query + "\"");
            var t = items[0];
            CheckExplicit(t);
            uri = t.GetProperty("uri").GetString();
            label = TrackLabel(t);
        }
        await ApiAsync(HttpMethod.Post, "/v1/me/player/queue?uri=" + Uri.EscapeDataString(uri));
        return label;
    }

    static void CheckExplicit(JsonElement t)
    {
        if (Hub.Settings.SpotifyBlockExplicit && t.TryGetProperty("explicit", out var x) && x.ValueKind == JsonValueKind.True)
            throw new Exception(TrackLabel(t) + " is marked explicit, and explicit songs are switched off");
    }

    static string TrackLabel(JsonElement t)
    {
        var name = t.GetProperty("name").GetString();
        var artist = t.TryGetProperty("artists", out var ar) && ar.GetArrayLength() > 0 ? ar[0].GetProperty("name").GetString() : null;
        return artist != null ? name + " by " + artist : name;
    }

    static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var d = new Dictionary<string, string>();
        int q = pathAndQuery.IndexOf('?');
        if (q < 0) return d;
        foreach (var pair in pathAndQuery.Substring(q + 1).Split('&'))
        {
            var kv = pair.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    static string RandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Short(string s) => s.Length > 200 ? s.Substring(0, 200) : s;
}
