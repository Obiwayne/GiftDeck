using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The setup checklist at the top of Stream Setup: what a new user still has to do, each row
// ticking itself off. TikFinity is only required for 18+ LIVEs (TikTok sends their chat and
// gifts only to logged-in viewers, and TikFinity is the logged-in reader).
public partial class StreamSetupView
{
    bool _installing;
    string _installText;
    string _installError;

    record Step(bool Done, string Title, string Detail, bool Optional = false, params (string label, Action click, bool primary)[] Buttons);

    void UpdateChecklist()
    {
        if (ChecklistPanel == null) return;
        var s = Hub.Settings;
        var t = Tt.State;
        bool mature = t.Mature;
        bool useTf = Reader == "tikfinity";
        var steps = new List<Step>();

        bool hasName = !string.IsNullOrWhiteSpace(s.BridgeUsername);
        steps.Add(new Step(hasName, "TikTok username",
            hasName ? "MayhemDeck reads @" + s.BridgeUsername.Trim().TrimStart('@') + "'s LIVE." : "Type the username you stream from, or log in to Streamlabs below and MayhemDeck fills it in.",
            false, hasName ? Array.Empty<(string, Action, bool)>() : new[] { ("Enter username", (Action)(() => { AccountCard.BringIntoView(); UsernameBox.Focus(); }), false) }));

        bool hasToken = !string.IsNullOrWhiteSpace(t.Token);
        steps.Add(new Step(hasToken, "Streamlabs login (for Go LIVE)",
            hasToken ? (string.IsNullOrWhiteSpace(t.AccountUsername) ? "Logged in." : "Logged in as @" + t.AccountUsername + ".")
                     : "Lets MayhemDeck open your LIVE and get its stream key. Nothing to install: you log in to Streamlabs in your browser with your TikTok account.",
            false, hasToken ? Array.Empty<(string, Action, bool)>() : new[] { ("Log in with TikTok", (Action)(() => { TokenCard.BringIntoView(); StartLogin(); }), true) }));

        bool obsReady = s.ObsManaged && ObsHost.PortraitExists();
        steps.Add(new Step(obsReady, "OBS set up",
            obsReady ? "MayhemDeck runs OBS hidden on your portrait canvas." : "Install OBS Studio (obsproject.com), then let MayhemDeck make its portrait 1080x1920 setup.",
            false, obsReady ? Array.Empty<(string, Action, bool)>() : new[] { ("Set up portrait OBS", (Action)(() => { EngineCard.BringIntoView(); SetUpPortrait_Click(SetUpPortraitButton, null); }), true) }));

        if (!mature && !useTf)
        {
            steps.Add(new Step(false, "TikFinity (only for 18+ LIVEs)",
                "Not needed: your LIVE isn't set to 18+, so MayhemDeck reads chat and gifts by itself. If you turn on 18+, TikTok only sends chat and gifts to logged-in viewers, and MayhemDeck then needs TikFinity.",
                Optional: true));
        }
        else
        {
            string why = mature ? "Your LIVE is 18+: " : "";
            bool installed = TikFinityInstaller.Installed;
            string installDetail = _installing ? _installText ?? "Starting…"
                : installed ? "Installed."
                : why + "TikTok only sends 18+ chat and gifts to logged-in viewers, so MayhemDeck reads them through TikFinity (free). MayhemDeck downloads it from TikFinity's own site."
                  + (_installError != null ? "\nLast try: " + _installError : "");
            steps.Add(new Step(installed, "Install TikFinity", installDetail, false,
                installed || _installing ? Array.Empty<(string, Action, bool)>() : new[] { ("Install TikFinity (about 95 MB)", (Action)InstallTikFinity, true) }));

            steps.Add(new Step(useTf, "Read your LIVE through TikFinity",
                useTf ? "MayhemDeck starts TikFinity for you and listens to it." : "Switch \"Reading your LIVE\" to TikFinity.",
                false, useTf || !installed ? Array.Empty<(string, Action, bool)>() : new[] { ("Use TikFinity", (Action)UseTikFinity, true) }));

            bool running = TikFinityService.IsProcessRunning();
            bool connected = useTf && Hub.TikFinity.Connected;
            steps.Add(new Step(connected, "TikFinity running",
                connected ? "Connected to TikFinity." : !useTf ? "Waits for the step above." : running ? "TikFinity is starting; connecting…" : "MayhemDeck starts it within a few seconds.",
                false));

            bool confirmed = s.TikFinityConfirmed;
            var confirmButtons = new List<(string, Action, bool)>();
            if (!confirmed && running) confirmButtons.Add(("Show TikFinity", (Action)(() => { Hub.TikFinity.ShowWindow(); UpdateReader(); }), false));
            if (!confirmed) confirmButtons.Add(("Done", (Action)(() => { s.TikFinityConfirmed = true; Hub.SaveSettings(); UpdateChecklist(); }), true));
            steps.Add(new Step(confirmed, "Log in to TikFinity and turn off its Events",
                confirmed ? "Done. (TikFinity keeps you logged in.)"
                          : "In TikFinity, log in with the TikTok account you stream from. If Google sign-in is blocked there, pick another way TikTok offers. Then switch off everything on TikFinity's Events tab: MayhemDeck runs your events, and leaving TikFinity's on makes gifts fire twice.",
                false, confirmButtons.ToArray()));
        }

        var required = steps.Where(x => !x.Optional).ToList();
        int done = required.Count(x => x.Done);
        ChecklistCount.Text = $"{done} of {required.Count} done";
        ChecklistIntro.Text = done == required.Count
            ? "Everything's set up. ✓"
            : "What MayhemDeck still needs before your first LIVE. Each step ticks itself off.";

        ChecklistPanel.Children.Clear();
        foreach (var st in steps) ChecklistPanel.Children.Add(Row(st));
    }

    UIElement Row(Step st)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mark = new TextBlock
        {
            Text = st.Done ? "✓" : st.Optional ? "–" : "○",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource(st.Done ? "SuccessBrush" : st.Optional ? "MutedBrush" : "WarnBrush"),
            Margin = new Thickness(0, 1, 0, 0),
        };
        grid.Children.Add(mark);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = st.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource(st.Optional ? "MutedBrush" : "TextBrush") });
        if (!string.IsNullOrEmpty(st.Detail))
            text.Children.Add(new TextBlock { Text = st.Detail, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (st.Buttons.Length > 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            foreach (var (label, click, primary) in st.Buttons)
            {
                var b = new Button { Content = label, Style = (Style)FindResource(primary ? "Primary" : "Ghost"), Margin = new Thickness(6, 0, 0, 0) };
                b.Click += (_, _) => click();
                buttons.Children.Add(b);
            }
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
        }
        return grid;
    }

    async void InstallTikFinity()
    {
        if (_installing) return;
        _installing = true;
        _installError = null;
        _installText = "Starting…";
        UpdateChecklist();
        try
        {
            await TikFinityInstaller.InstallAsync(new Progress<(double part, string text)>(p => { _installText = p.text; UpdateChecklist(); }));
        }
        catch (Exception ex)
        {
            _installError = ex.Message;
            Log.Write("TikFinity install failed: " + ex.Message);
        }
        finally
        {
            _installing = false;
            _installText = null;
            UpdateChecklist();
            UpdateReader();
        }
    }

    void UseTikFinity()
    {
        foreach (ComboBoxItem item in ReaderBox.Items)
            if ((string)item.Tag == "tikfinity") ReaderBox.SelectedItem = item; // Reader_Changed switches the feed and starts TikFinity
        ReaderCard.BringIntoView();
    }
}
