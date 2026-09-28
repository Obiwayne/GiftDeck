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
            try
            {
                // One pending accept per listener, renewed only when it's used, so no connection is left unanswered.
                var a4 = v4.AcceptTcpClientAsync(cts.Token).AsTask();
                var a6 = v6?.AcceptTcpClientAsync(cts.Token).AsTask();
                while (code == null)
                {
                    var next = a6 == null ? a4 : await Task.WhenAny(a4, a6);
                    var client = await next;
                    if (next == a4) a4 = v4.AcceptTcpClientAsync(cts.Token).AsTask();
                    else a6 = v6.AcceptTcpClientAsync(cts.Token).AsTask();
                    using (client)
                        code = await HandleCallbackAsync(client, cts.Token);
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new Exception("The login wasn't finished in time. Press Log in again.");
            }
            return await ExchangeAsync(code, verifier, cancel);
        }
        finally
        {
            try { v4.Stop(); } catch { }
            try { v6?.Stop(); } catch { }
        }
    }

    // How long one connection may sit idle before GiftDeck stops waiting for its request (browsers open spare ones).
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    // Reads the browser's request, answers with a short page, and returns the code.
    // Returns null for anything that isn't Streamlabs' answer (a favicon, an empty spare connection, an idle one).
    static async Task<string> HandleCallbackAsync(TcpClient client, CancellationToken cancel)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        int n = 0;
        using (var read = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            read.CancelAfter(ReadTimeout);
            try
            {
                // Only the first line matters; it can arrive in more than one piece.
                while (n < buffer.Length)
                {
                    int got = await stream.ReadAsync(buffer.AsMemory(n), read.Token);
                    if (got == 0) break;
                    n += got;
                    if (Encoding.ASCII.GetString(buffer, 0, n).Contains("\r\n")) break;
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return null; }
            catch (IOException) { return null; }
        }
        if (n == 0) return null;
        var request = Encoding.ASCII.GetString(buffer, 0, n);
        var firstLine = request.Split("\r\n")[0];                // GET /?success=true&code=... HTTP/1.1
        var target = firstLine.Split(' ').ElementAtOrDefault(1) ?? "";
        var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
        var values = new Dictionary<string, string>();
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=', 2)))
            values[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";

        bool ok = values.TryGetValue("success", out var s) && s == "true" && values.ContainsKey("code");
        // Neither a code nor a success flag: not Streamlabs' answer (a favicon, a stray request). Ignore it and keep waiting.
        bool unrelated = !values.ContainsKey("code") && !values.ContainsKey("success");
        var body = ok ? "<h2 style='font-family:sans-serif'>GiftDeck is logged in to Streamlabs. You can close this tab.</h2>"
                 : unrelated ? ""
                 : "<h2 style='font-family:sans-serif'>Login didn't complete. Go back to GiftDeck and try again.</h2>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var status = ok ? "200 OK" : unrelated ? "404 Not Found" : "400 Bad Request";
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancel);
            await stream.WriteAsync(bytes, cancel);
        }
        catch (IOException) { } // the browser closed the tab; the answer still counts

        if (unrelated) return null;
        if (!ok) throw new Exception("Streamlabs reported that the login didn't complete.");
        return values["code"];
    }

    static async Task<string> ExchangeAsync(string code, string verifier, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) StreamlabsDesktop/1.20.4 Chrome/122.0.6261.156 Electron/29.3.1 Safari/537.36");
        var url = "https://streamlabs.com/api/v5/slobs/auth/data?code_verifier=" + Uri.EscapeDataString(verifier) + "&code=" + Uri.EscapeDataString(code);
        var res = await http.GetAsync(url, cancel);
        var text = await res.Content.ReadAsStringAsync(cancel);
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
