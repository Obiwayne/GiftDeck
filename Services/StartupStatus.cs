namespace GiftDeck.Services;

public enum StatusKind { Off, Loading, Ok, Error }

// One place that says, in plain words, whether OBS and the LIVE reader (TikFinity or the bridge)
// are starting, ready or broken. Used by the sidebar and the status strip on the right-hand panel.
// Anything still "loading" after a while turns into an error, so a hang never looks like progress.
public static class StartupStatus
{
    static readonly DateTime AppStart = DateTime.Now;
    const int GiveUpSeconds = 60;

    static DateTime? _obsWaitingSince, _readerWaitingSince;

    static bool TooLong(ref DateTime? since, bool waiting)
    {
        if (!waiting) { since = null; return false; }
        since ??= DateTime.Now;
        return (DateTime.Now - since.Value).TotalSeconds > GiveUpSeconds;
    }

    public static (StatusKind kind, string text) Obs()
    {
        var engine = Hub.Engine;
        bool connected = Hub.Obs.Connected;
        if (connected) { _obsWaitingSince = null; return (StatusKind.Ok, "OBS ready"); }

        if (!Hub.Settings.ObsManaged)
        {
            _obsWaitingSince = null;
            return (StatusKind.Off, "OBS not connected");
        }
        if (engine.Restarting)
        {
            _obsWaitingSince = null;
            return (StatusKind.Loading, "OBS was closed; starting it again in the background…");
        }
        if (!string.IsNullOrEmpty(engine.LastError))
        {
            _obsWaitingSince = null;
            return (StatusKind.Error, "OBS error: " + engine.LastError);
        }
        if (!ObsHost.IsRunning && !engine.Starting && (DateTime.Now - AppStart).TotalSeconds > 15)
        {
            _obsWaitingSince = null;
            return ObsHost.PortraitExists()
                ? (StatusKind.Error, "OBS isn't running. It starts again when you Go LIVE, or restart GiftDeck.")
                : (StatusKind.Error, "OBS isn't set up yet: Stream Setup, Set up portrait OBS.");
        }
        if (TooLong(ref _obsWaitingSince, true))
            return (StatusKind.Error, "Something's wrong: OBS is taking too long to answer. Check its WebSocket server is on.");
        return (StatusKind.Loading, engine.Starting || !ObsHost.IsRunning ? "Starting OBS…" : "Connecting to OBS…");
    }

    public static (StatusKind kind, string text) Reader()
    {
        var t = Hub.TikFinity;
        if (BridgeService.NeedsUsername)
        {
            _readerWaitingSince = null;
            return (StatusKind.Error, "Set your TikTok username (Stream Setup)");
        }

        if (BridgeService.InUse)
        {
            // GiftDeck's own bridge; being offline is normal, not an error.
            if (t.Connected) { _readerWaitingSince = null; return t.TikTokLive == true ? (StatusKind.Ok, "Connected to your LIVE") : (StatusKind.Ok, "Waiting for your LIVE"); }
            if (TooLong(ref _readerWaitingSince, true))
                return (StatusKind.Error, "Something's wrong: the TikTok bridge didn't start" + Reason(t));
            return (StatusKind.Loading, "Starting the TikTok bridge…");
        }

        // TikFinity
        if (t.Connected)
        {
            _readerWaitingSince = null;
            // TikFinity says "not live" whenever you're offline; that's only a problem once GiftDeck has put you live.
            if (t.TikTokLive == false)
                return Hub.TikTok.Live
                    ? (StatusKind.Error, "Something's wrong: TikFinity isn't on your LIVE, so chat and gifts won't come through")
                    : (StatusKind.Ok, "TikFinity connected, waiting for your LIVE");
            return (StatusKind.Ok, t.TikTokLive == true ? "TikFinity connected to your LIVE" : "TikFinity connected");
        }
        bool wanted = Hub.Settings.AutoLaunchTikFinity || Hub.Settings.LiveReader == "tikfinity";
        if (!TikFinityInstaller.Installed)
        {
            _readerWaitingSince = null;
            return (StatusKind.Error, "TikFinity isn't installed (Stream Setup, Install TikFinity)");
        }
        if (!t.ProcessRunning && !wanted)
        {
            _readerWaitingSince = null;
            return (StatusKind.Error, "TikFinity isn't running. Open TikFinity.");
        }
        if (TooLong(ref _readerWaitingSince, true))
            return (StatusKind.Error, "Something's wrong: GiftDeck can't connect to TikFinity" + Reason(t));
        return (StatusKind.Loading, t.ProcessRunning ? "Connecting to TikFinity…" : "Starting TikFinity…");
    }

    static string Reason(TikFinityService t) => string.IsNullOrWhiteSpace(t.LastError) ? "." : " (" + t.LastError.TrimEnd('.') + ").";
}
