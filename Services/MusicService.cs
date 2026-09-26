using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace GiftDeck.Services;

public class MusicTrack
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AudioUrl { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string LicenseUrl { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public int Seconds { get; set; }
}

// Endless background music from Jamendo (free Creative Commons tracks), shuffled, one after another.
public class MusicService
{
    public static readonly (string Label, string Tag)[] Genres =
    {
        ("Anything", ""), ("Chill / lounge", "chillout+lounge"), ("Lo-fi", "lofi"), ("Electronic", "electronic"),
        ("Hip hop", "hiphop"), ("Rock", "rock"), ("Pop", "pop"), ("Ambient", "ambient"), ("Jazz", "jazz"),
        ("Funk", "funk"), ("Cinematic", "soundtrack+cinematic"), ("Dance / EDM", "dance+edm"), ("Metal", "metal"),
    };

    const int PageSize = 50;

    public event Action Changed;

    public MusicTrack Current { get; private set; }
    public bool Playing { get; private set; }
    public bool Loading { get; private set; }
    public string LastError { get; private set; }

    static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    readonly Queue<MusicTrack> _queue = new Queue<MusicTrack>();
    readonly HashSet<string> _played = new HashSet<string>();
    readonly Dictionary<string, int> _totals = new Dictionary<string, int>();
    readonly Random _rng = new Random();
    MediaPlayer _player;
    int _failuresInARow;
    string _queueFilter = "";

    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;
    public TimeSpan Length => _player != null && _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan
        : TimeSpan.FromSeconds(Current?.Seconds ?? 0);

    MediaPlayer Player
    {
        get
        {
            if (_player != null) return _player;
            _player = new MediaPlayer();
            _player.MediaEnded += async (s, e) => await NextAsync();
            _player.MediaFailed += async (s, e) =>
            {
                Log.Write($"Music: couldn't play \"{Current?.Title}\": {e.ErrorException?.Message}");
                if (++_failuresInARow >= 5) { Stop("Several tracks in a row failed to play. Check your internet connection."); return; }
                await NextAsync();
            };
            _player.MediaOpened += (s, e) => { _failuresInARow = 0; Changed?.Invoke(); };
            ApplyVolume();
            return _player;
        }
    }

    // Play/Pause button: resumes the current track, or starts the music if nothing is loaded yet.
    public async Task PlayAsync()
    {
        if (Current != null && !Playing)
        {
            Player.Play();
            Playing = true;
            Changed?.Invoke();
            return;
        }
        if (Current == null) await NextAsync();
    }

    public void Pause()
    {
        if (_player == null || !Playing) return;
        _player.Pause();
        Playing = false;
        Changed?.Invoke();
    }

    public async Task NextAsync()
    {
        if (Loading) return;
        Loading = true;
        LastError = null;
        Changed?.Invoke();
        try
        {
            var track = await TakeNextAsync();
            if (track == null) { Stop(LastError ?? "Jamendo sent no tracks for this genre. Try another one."); return; }
            Current = track;
            _played.Add(track.Id);
            Player.Open(new Uri(track.AudioUrl));
            Player.Play();
            Playing = true;
            Log.Write($"Music: {track.Title} by {track.Artist}");
        }
        catch (Exception e)
        {
            Stop(e.Message);
        }
        finally
        {
            Loading = false;
            Changed?.Invoke();
        }
    }

    // Genre or instrumental setting changed: forget the queued tracks so the next one follows the new choice.
    public void FilterChanged()
    {
        lock (_queue) _queue.Clear();
    }

    public void ApplyVolume()
    {
        if (_player != null) _player.Volume = Math.Clamp(Hub.Settings.MusicVolume, 0, 100) / 100.0;
    }

    public void Shutdown()
    {
        try { _player?.Stop(); _player?.Close(); } catch { }
    }

    void Stop(string error)
    {
        LastError = error;
        Playing = false;
        try { _player?.Stop(); } catch { }
        if (error != null) Log.Write("Music: " + error);
        Changed?.Invoke();
    }

    string FilterKey => Hub.Settings.MusicGenre + "|" + Hub.Settings.MusicInstrumental;

    async Task<MusicTrack> TakeNextAsync()
    {
        if (_queueFilter != FilterKey) { FilterChanged(); _queueFilter = FilterKey; }
        lock (_queue) if (_queue.Count > 0) return _queue.Dequeue();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var tracks = await FetchPageAsync();
            if (tracks == null) return null;
            var fresh = tracks.Where(t => !_played.Contains(t.Id)).ToList();
            if (fresh.Count == 0 && tracks.Count > 0) { _played.Clear(); fresh = tracks; } // heard everything nearby: allow repeats
            foreach (var t in fresh.OrderBy(_ => _rng.Next())) lock (_queue) _queue.Enqueue(t);
            lock (_queue) if (_queue.Count > 0) return _queue.Dequeue();
        }
        return null;
    }

    // One page of tracks from a random spot in the chosen genre, so each session sounds different.
    async Task<List<MusicTrack>> FetchPageAsync()
    {
        var id = (Hub.Settings.JamendoClientId ?? "").Trim();
        if (id.Length == 0) { LastError = "Add your Jamendo Client ID first (see the setup steps above)."; return null; }

        var key = FilterKey;
        int offset = 0;
        if (_totals.TryGetValue(key, out int total) && total > PageSize)
            offset = _rng.Next(0, Math.Min(total - PageSize, 5000));

        var url = Hub.Settings.JamendoApiBase.TrimEnd('/') + "/tracks/?format=json&fullcount=true&audioformat=mp32&order=popularity_total"
                  + $"&limit={PageSize}&offset={offset}&client_id={Uri.EscapeDataString(id)}";
        var tag = Hub.Settings.MusicGenre ?? "";
        if (tag.Length > 0) url += "&fuzzytags=" + Uri.EscapeDataString(tag).Replace("%2B", "+");
        if (Hub.Settings.MusicInstrumental) url += "&vocalinstrumental=instrumental";

        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = doc.RootElement;
        var headers = root.GetProperty("headers");
        if (headers.GetProperty("status").GetString() != "success")
        {
            var msg = headers.TryGetProperty("error_message", out var m) ? m.GetString() : "unknown error";
            LastError = msg != null && msg.Contains("Client Id") ? "Jamendo didn't accept that Client ID. Check it was copied fully." : "Jamendo: " + msg;
            return null;
        }
        if (headers.TryGetProperty("warnings", out var w) && (w.GetString() ?? "").Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Jamendo says this Client ID has hit its usage limit: " + w.GetString();
            return null;
        }
        if (headers.TryGetProperty("results_fullcount", out var fc) && fc.TryGetInt32(out int full)) _totals[key] = full;

        var list = new List<MusicTrack>();
        foreach (var r in root.GetProperty("results").EnumerateArray())
        {
            var audio = Str(r, "audio");
            if (string.IsNullOrEmpty(audio)) continue;
            list.Add(new MusicTrack
            {
                Id = Str(r, "id"),
                Title = Str(r, "name"),
                Artist = Str(r, "artist_name"),
                AudioUrl = audio,
                ImageUrl = Str(r, "image") is { Length: > 0 } img ? img : Str(r, "album_image"),
                LicenseUrl = Str(r, "license_ccurl"),
                PageUrl = Str(r, "shareurl"),
                Seconds = r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0,
            });
        }
        return list;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
}
