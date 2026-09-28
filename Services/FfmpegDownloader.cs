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
        try
        {
            // Same download as the setup installers: gives up if no data arrives for 30 seconds, deletes a partial file.
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("GiftDeck");
                await SetupSteps.DownloadFileAsync(http, Url, zip, (done, total) =>
                {
                    if (total > 0) progress?.Report((double)done / total);
                }, cancel);
            }

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zip);
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new Exception("The ffmpeg download didn't contain ffmpeg.exe");
                entry.ExtractToFile(InstalledExe, overwrite: true);
            }, cancel);
        }
        finally { try { File.Delete(zip); } catch { } }
        Log.Write("ffmpeg installed to " + InstallDir);
    }
}
