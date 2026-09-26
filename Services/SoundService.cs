using System.Windows;
using System.Windows.Media;

namespace GiftDeck.Services;

// Plays mp3/wav files through the default output device. Several sounds can overlap.
public class SoundService
{
    readonly List<MediaPlayer> _active = new List<MediaPlayer>();

    public void Play(string path, int volumePercent)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new Exception("No sound file chosen");
        if (!File.Exists(path)) throw new Exception("Sound file not found: " + path);
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            var p = new MediaPlayer();
            p.MediaEnded += (s, e) => { p.Close(); lock (_active) _active.Remove(p); };
            p.MediaFailed += (s, e) => { Log.Write("Sound failed: " + e.ErrorException.Message); lock (_active) _active.Remove(p); };
            p.Open(new Uri(path, UriKind.Absolute));
            double master = Math.Clamp(Hub.Settings.SoundVolume, 0, 100) / 100.0;
            p.Volume = Math.Clamp(volumePercent, 0, 100) / 100.0 * master;
            p.Play();
            lock (_active) _active.Add(p);
        });
    }

    public void StopAll()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            lock (_active)
            {
                foreach (var p in _active) { try { p.Stop(); p.Close(); } catch { } }
                _active.Clear();
            }
        });
    }
}
