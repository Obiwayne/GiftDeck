using System.Windows;
using System.Windows.Controls;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class ObsView : UserControl
{
    bool _switching;

    public ObsView()
    {
        InitializeComponent();
        HostBox.Text = Hub.Settings.ObsHost;
        PortBox.Text = Hub.Settings.ObsPort.ToString();
        PasswordBox.Text = Hub.Settings.ObsPassword;
        AutoConnect.IsChecked = Hub.Settings.ObsAutoConnect;
        Hub.Obs.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        UpdateStatus();
    }

    void SaveFields()
    {
        Hub.Settings.ObsHost = HostBox.Text.Trim().Length > 0 ? HostBox.Text.Trim() : "127.0.0.1";
        if (int.TryParse(PortBox.Text.Trim(), out int port) && port > 0) Hub.Settings.ObsPort = port;
        Hub.Settings.ObsPassword = PasswordBox.Text.Trim();
        Hub.SaveSettings();
    }

    void UpdateStatus()
    {
        Status.Text = Hub.Obs.Connected ? $"Connected. {Hub.Obs.Scenes.Count} scenes found." : "Not connected.";
        Error.Text = !Hub.Obs.Connected && Hub.Obs.LastError != null ? Hub.Obs.LastError : "";
        _switching = true;
        SceneList.ItemsSource = Hub.Obs.Scenes.ToList();
        var canvases = Hub.Obs.Canvases;
        if (canvases.Count == 0)
        {
            CanvasHeading.Text = "Vertical canvas scenes";
            CanvasHint.Text = Hub.Obs.Connected
                ? "No vertical canvas found. This needs the Aitum Stream Suite plugin in OBS."
                : "Connect to OBS to see the vertical canvas scenes.";
            CanvasSceneList.ItemsSource = null;
        }
        else
        {
            CanvasHeading.Text = string.Join(" / ", canvases) + " scenes";
            CanvasHint.Text = "Click a scene to switch the vertical canvas to it right now. Events use the action 'OBS: switch scene (vertical canvas)'.";
            var rows = new List<CanvasRow>();
            foreach (var c in canvases)
                foreach (var s in Hub.Obs.CanvasScenes.TryGetValue(c, out var list) ? list : new List<string>())
                    rows.Add(new CanvasRow { Canvas = c, Scene = s });
            CanvasSceneList.ItemsSource = rows;
        }
        _switching = false;
    }

    class CanvasRow
    {
        public string Canvas;
        public string Scene;
        public override string ToString() => Scene;
    }

    async void CanvasScene_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || !(CanvasSceneList.SelectedItem is CanvasRow row)) return;
        try { await Hub.Obs.SetCanvasSceneAsync(row.Canvas, row.Scene); Error.Text = ""; }
        catch (Exception ex) { Error.Text = ex.Message; }
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        SaveFields();
        Error.Text = "";
        try { await Hub.Obs.ConnectAsync(); }
        catch (Exception ex) { Error.Text = ex.Message; }
        UpdateStatus();
    }

    async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await Hub.Obs.DisconnectAsync();
        UpdateStatus();
    }

    void Detect_Click(object sender, RoutedEventArgs e)
    {
        var cfg = ObsService.ReadObsConfig();
        if (!cfg.found)
        {
            Error.Text = "Could not find OBS's WebSocket settings file. Is OBS installed for this user?";
            return;
        }
        PortBox.Text = cfg.port.ToString();
        PasswordBox.Text = cfg.authRequired ? cfg.password : "";
        SaveFields();
        Error.Text = cfg.enabled ? "" : "OBS has its WebSocket server switched off. Enable it in OBS under Tools, WebSocket Server Settings, then press Connect.";
    }

    void AutoConnect_Click(object sender, RoutedEventArgs e)
    {
        Hub.Settings.ObsAutoConnect = AutoConnect.IsChecked == true;
        Hub.SaveSettings();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Hub.Obs.Connected) await Hub.Obs.ConnectAsync();
            else await Hub.Obs.ConnectAsync();
        }
        catch (Exception ex) { Error.Text = ex.Message; }
        UpdateStatus();
    }

    async void Scene_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || !(SceneList.SelectedItem is string scene)) return;
        try { await Hub.Obs.SetSceneAsync(scene); Error.Text = ""; }
        catch (Exception ex) { Error.Text = ex.Message; }
    }
}
