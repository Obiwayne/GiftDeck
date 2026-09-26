using System.IO.Compression;
using System.Net.Http;

namespace GiftDeck.Services;

// One-click ffmpeg for the Go LIVE relay: downloads the official "essentials" Windows build
// from gyan.dev and keeps only ffmpeg.exe in %LOCALAPPDATA%\GiftDeck\ffmpeg.
public static class FfmpegDownloader
{
    const string Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GiftDeck", "ffmpeg");
    public static string InstalledExe => Path.Combine(InstallDir, "ffmpeg.exe");

    // progress: 0..1 while downloading
    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(InstallDir);
        var zip = Path.Combine(Path.GetTempPath(), "giftdeck-ffmpeg.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GiftDeck");
            using var res = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancel);
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync(cancel);
            await using var dst = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, cancel)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), cancel);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception("The ffmpeg download didn't contain ffmpeg.exe");
            entry.ExtractToFile(InstalledExe, overwrite: true);
        }, cancel);
        try { File.Delete(zip); } catch { }
        Log.Write("ffmpeg installed to " + InstallDir);
    }
}
