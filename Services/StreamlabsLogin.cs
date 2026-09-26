using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GiftDeck.Services;

// Gets a Streamlabs TikTok LIVE token by logging in through Streamlabs in the user's own browser
// (so "Continue with Google" works), the same way Streamlabs Desktop does: a PKCE code flow whose
// result comes back to a one-off listener on this PC. No Streamlabs Desktop or key generator needed.
public static class StreamlabsLogin
{
    public static async Task<string> LoginAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(64)).ToLowerInvariant();
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Listen on both loopback addresses: the browser may resolve "localhost" to either.
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        int port = ((IPEndPoint)v4.LocalEndpoint).Port;
        TcpListener v6 = null;
        try { v6 = new TcpListener(IPAddress.IPv6Loopback, port); v6.Start(); } catch { v6 = null; }

        try
        {
            var url = "https://streamlabs.com/slobs/login?skip_splash=true&external=electron&tiktok&force_verify&origin=slobs"
                      + $"&port={port}&code_challenge={challenge}&code_flow=true";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Log.Write("Streamlabs login opened in the browser");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(timeout);
            string code = null;
            while (code == null)
            {
                var accepts = new List<Task<TcpClient>> { v4.AcceptTcpClientAsync(cts.Token).AsTask() };
                if (v6 != null) accepts.Add(v6.AcceptTcpClientAsync(cts.Token).AsTask());
                TcpClient client;
                try { client = await await Task.WhenAny(accepts); }
                catch (OperationCanceledException) { throw new Exception("The login wasn't finished in time. Press Log in again."); }
                using (client)
                    code = await HandleCallbackAsync(client);
            }
            return await ExchangeAsync(code, verifier);
        }
        finally
        {
            try { v4.Stop(); } catch { }
            try { v6?.Stop(); } catch { }
        }
    }

    // Reads the browser's request, answers with a short page, and returns the code (null for unrelated requests).
    static async Task<string> HandleCallbackAsync(TcpClient client)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        int n = await stream.ReadAsync(buffer);
        var request = Encoding.ASCII.GetString(buffer, 0, n);
        var firstLine = request.Split("\r\n")[0];                // GET /?success=true&code=... HTTP/1.1
        var target = firstLine.Split(' ').ElementAtOrDefault(1) ?? "";
        var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
        var values = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2))
            .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "");

        bool ok = values.TryGetValue("success", out var s) && s == "true" && values.ContainsKey("code");
        bool favicon = target.StartsWith("/favicon");
        var body = ok ? "<h2 style='font-family:sans-serif'>GiftDeck is logged in to Streamlabs. You can close this tab.</h2>"
                      : "<h2 style='font-family:sans-serif'>Login didn't complete. Go back to GiftDeck and try again.</h2>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {(ok || favicon ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bytes);

        if (favicon) return null;
        if (!ok) throw new Exception("Streamlabs reported that the login didn't complete.");
        return values["code"];
    }

    static async Task<string> ExchangeAsync(string code, string verifier)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) StreamlabsDesktop/1.20.4 Chrome/122.0.6261.156 Electron/29.3.1 Safari/537.36");
        var url = "https://streamlabs.com/api/v5/slobs/auth/data?code_verifier=" + Uri.EscapeDataString(verifier) + "&code=" + Uri.EscapeDataString(code);
        var res = await http.GetAsync(url);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"Streamlabs refused the login (HTTP {(int)res.StatusCode}).");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("data", out var data) || !data.TryGetProperty("oauth_token", out var tok))
            throw new Exception("Streamlabs didn't return a token.");
        Log.Write("Streamlabs login finished; token saved");
        return tok.GetString();
    }
}
