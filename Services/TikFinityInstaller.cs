namespace GiftDeck.Services;

// One-click TikFinity for 18+ LIVEs: the installer TikFinity's own updater uses, from TikFinity's server,
// SHA-512 checked, installed silently (/S; it's a one-click per-user installer). Nothing of TikFinity ships with GiftDeck.
public static class TikFinityInstaller
{
    const string Feed = "https://tikfinity-electron-updates.b-cdn.net/win/";

    public static bool Installed => File.Exists(Hub.Settings.TikFinityExe);

    public static Task InstallAsync(IProgress<(double part, string text)> progress) =>
        SetupSteps.InstallFromElectronFeedAsync(Feed, "TikFinity", "/S", progress, () => Installed);
}
