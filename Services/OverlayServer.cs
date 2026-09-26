using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace GiftDeck.Services;

// A tiny local web server. OBS loads the overlay pages from it as Browser Sources,
// and the pages get live updates over a server-sent events stream at /events.
public class OverlayServer
{
    readonly OverlayService _svc;
    HttpListener _listener;
    CancellationTokenSource _cts;
    readonly ConcurrentDictionary<Guid, HttpListenerResponse> _clients = new ConcurrentDictionary<Guid, HttpListenerResponse>();

    public bool Running { get; private set; }
    public string LastError { get; private set; }
    public string BaseUrl => $"http://localhost:{_svc.Config.Port}";
    public int ClientCount => _clients.Count;

    public OverlayServer(OverlayService svc)
    {
        _svc = svc;
        svc.Broadcast += Send;
    }

    public void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_svc.Config.Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_svc.Config.Port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            Running = true;
            LastError = null;
            _ = Task.Run(Loop);
            _ = Task.Run(KeepAlive);
            Log.Write("Overlay pages available at " + BaseUrl);
        }
        catch (Exception e)
        {
            Running = false;
            LastError = e.Message;
            Log.Write("Overlay server could not start: " + e.Message);
        }
    }

    public void Stop()
    {
        Running = false;
        try { _cts?.Cancel(); } catch { }
        foreach (var c in _clients.Values) { try { c.Close(); } catch { } }
        _clients.Clear();
        try { _listener?.Stop(); } catch { }
    }

    async Task Loop()
    {
        while (Running && !_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { if (!Running || _cts.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    async Task KeepAlive()
    {
        while (Running && !_cts.IsCancellationRequested)
        {
            try { await Task.Delay(15000, _cts.Token); } catch { break; }
            SendRaw(": ping\n\n");
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/events") { OpenStream(ctx); return; }
            if (path == "/state") { Write(ctx, _svc.StateJson(), "application/json"); return; }
            if (path.StartsWith("/tile-image/")) { ServeTileImage(ctx, path.Substring("/tile-image/".Length)); return; }

            string page = null;
            if (path.StartsWith("/overlay/goal")) page = "goal.html";
            else if (path.StartsWith("/overlay/menu")) page = "menu.html";
            else if (path.StartsWith("/overlay/counter")) page = "counter.html";
            else if (path.StartsWith("/overlay/countdown")) page = "countdown.html";
            else if (path.StartsWith("/overlay/alerts")) page = "alerts.html";
            else if (path == "/") page = "index.html";

            if (page == null)
            {
                ctx.Response.StatusCode = 404;
                Write(ctx, "Not found", "text/plain");
                return;
            }
            Write(ctx, Page(page), "text/html; charset=utf-8");
        }
        catch (Exception e)
        {
            Log.Write("Overlay request failed: " + e.Message);
            try { ctx.Response.Abort(); } catch { }
        }
    }

    // A tile's picture: a file on this PC is sent directly, a web address is redirected to.
    void ServeTileImage(HttpListenerContext ctx, string idText)
    {
        var tile = Guid.TryParse(idText, out var id) ? _svc.FindTile(id) : null;
        var src = tile == null ? null : _svc.ResolveTileImage(tile);
        if (string.IsNullOrEmpty(src))
        {
            ctx.Response.StatusCode = 404;
            Write(ctx, "No picture", "text/plain");
            return;
        }
        if (!File.Exists(src) && src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // Web pictures are fetched once into GiftDeck's cache and served from there,
            // because TikTok's image server refuses requests that come from a web page.
            var cached = GiftImages.CachedFileAsync(src).GetAwaiter().GetResult();
            if (cached != null) src = cached;
        }
        if (File.Exists(src))
        {
            var ext = Path.GetExtension(src).ToLowerInvariant();
            var type = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            var bytes = File.ReadAllBytes(src);
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
            return;
        }
        ctx.Response.StatusCode = 302;
        ctx.Response.Headers["Location"] = src;
        ctx.Response.Close();
    }

    static void Write(HttpListenerContext ctx, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    void OpenStream(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        res.ContentType = "text/event-stream";
        res.Headers["Cache-Control"] = "no-cache";
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.SendChunked = true;
        var id = Guid.NewGuid();
        _clients[id] = res;
        if (!WriteTo(res, "data: " + _svc.StateJson() + "\n\n"))
        {
            _clients.TryRemove(id, out _);
        }
    }

    void Send(string json) => SendRaw("data: " + json + "\n\n");

    void SendRaw(string text)
    {
        foreach (var pair in _clients.ToArray())
        {
            if (WriteTo(pair.Value, text)) continue;
            _clients.TryRemove(pair.Key, out _);
            try { pair.Value.Close(); } catch { }
        }
    }

    static bool WriteTo(HttpListenerResponse res, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            lock (res)
            {
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Flush();
            }
            return true;
        }
        catch { return false; }
    }

    static readonly ConcurrentDictionary<string, string> PageCache = new ConcurrentDictionary<string, string>();

    static string Page(string name) => PageCache.GetOrAdd(name, n =>
    {
        using var s = typeof(OverlayServer).Assembly.GetManifestResourceStream("GiftDeck.Overlays." + n);
        if (s == null) return "<html><body>Missing page " + n + "</body></html>";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    });
}
