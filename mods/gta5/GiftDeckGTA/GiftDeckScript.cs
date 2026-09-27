using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.UI;
using Font = GTA.UI.Font;

namespace GiftDeckGTA
{
    // GiftDeck GTA: runs the commands GiftDeck sends when viewers send gifts (gta5:giftdeck over GameLink).
    // The connection lives on its own thread; everything that touches the game happens here, on the tick.
    public partial class GiftDeckScript : Script
    {
        const int MaxTriggersPerTick = 3;
        const int GreetingMs = 12000;

        static readonly Color Green = Color.FromArgb(255, 90, 230, 110);
        static readonly Color Red = Color.FromArgb(255, 240, 80, 80);
        static readonly Color Accent = Color.FromArgb(255, 124, 92, 255); // GiftDeck purple
        static readonly Color Grey = Color.FromArgb(255, 180, 180, 180);

        readonly GameLinkClient _link;
        readonly Random _rng = new Random();

        // Settings (GiftDeckGTA.ini)
        readonly string _host;
        readonly int _port;
        Keys _menuKey;
        bool _showGifts, _showConnection, _showGreeting;

        bool _paused;
        bool _wasConnected;
        int _greetingStart, _greetingUntil;
        bool _started;
        int _handled;

        static int Now => Environment.TickCount;

        public GiftDeckScript()
        {
            Logger.Init(Path.Combine(BaseDirectory, "GiftDeckGTA"));
            Logger.Write("GiftDeck GTA " + Catalog.Version + " starting");

            // Host and port are separate keys because SHVDN's ini reader mangles values with "://" in them.
            _host = Settings.GetValue("GameLink", "Host", "127.0.0.1");
            _port = Settings.GetValue("GameLink", "Port", 21216);
            if (!Enum.TryParse(Settings.GetValue("Menu", "Key", "F10"), true, out _menuKey)) _menuKey = Keys.F10;
            _showGifts = Settings.GetValue("Notifications", "ShowGifts", true);
            _showConnection = Settings.GetValue("Notifications", "ShowConnection", true);
            _showGreeting = Settings.GetValue("Notifications", "ShowGreeting", true);

            InitCommands();
            _link = new GameLinkClient(_host, _port, Catalog.Hello, Logger.Write);
            _link.Start();

            Interval = 0;
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;
        }

        void OnAborted(object sender, EventArgs e)
        {
            _link.Stop();
            CleanUpEffects();
            Logger.Write("GiftDeck GTA stopped");
        }

        void OnTick(object sender, EventArgs e)
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists()) return;
            int now = Now;

            if (!_started)
            {
                _started = true;
                _greetingStart = now;
                _greetingUntil = _showGreeting ? now + GreetingMs : now;
                Logger.Write("In Story Mode");
            }

            bool connected = _link.Connected;
            if (connected != _wasConnected)
            {
                _wasConnected = connected;
                if (connected) SendStatus();
                if (_showConnection && now > _greetingUntil)
                    Notify(connected ? "~g~GiftDeck connected~s~ - gifts will play in game" : "~r~GiftDeck disconnected~s~ - waiting for it...");
            }

            for (int i = 0; i < MaxTriggersPerTick && _link.Triggers.TryDequeue(out var t); i++)
                Handle(t);

            UpdateEffects(ped, now);

            if (now < _greetingUntil) DrawGreeting(now);
            if (_menuOpen) DrawMenu();
        }

        void SendStatus() => _link.SendStatus(_paused ? "Paused in the GTA menu (" + _menuKey + ")" : "In Story Mode");

        // ---------------------------------------------------------------- triggers

        void Handle(Trigger t)
        {
            var info = Catalog.Find(t.Command);
            if (info == null || !_commands.TryGetValue(t.Command, out var run))
            {
                Logger.Write($"Unknown command '{t.Command}' from {t.User}");
                Reply(t, false, "GiftDeck GTA doesn't know the command '" + t.Command + "'");
                return;
            }
            if (_paused && !t.FromMenu)
            {
                Reply(t, false, "Triggers are paused in GTA (" + _menuKey + " menu)");
                return;
            }

            if (_showGifts) Notify(Describe(t, info));
            try
            {
                string message = run(t) ?? "";
                _handled++;
                Logger.Write($"{Describe(t, info)} -> ok {message}");
                Reply(t, true, message);
            }
            catch (CommandFailed f)
            {
                Logger.Write($"{Describe(t, info)} -> failed: {f.Message}");
                if (t.FromMenu) Notify("~r~" + f.Message);
                Reply(t, false, f.Message);
            }
            catch (Exception ex)
            {
                Logger.Write($"{Describe(t, info)} -> error: {ex}");
                if (t.FromMenu) Notify("~r~Error: " + ex.Message);
                Reply(t, false, "Error in GTA: " + ex.Message);
            }
        }

        void Reply(Trigger t, bool ok, string message)
        {
            if (!t.FromMenu) _link.SendResult(t.Id, ok, message);
        }

        // "{user} sent {gift}: {command}"
        static string Describe(Trigger t, CommandInfo info)
        {
            string user = Clean(t.User, "Someone");
            string gift = Clean(t.Gift, "");
            if (t.FromMenu) return "~b~Test~s~: " + info.Name;
            if (gift.Length == 0) return $"~b~{user}~s~: {info.Name}";
            return $"~b~{user}~s~ sent {gift}{(t.Count > 1 ? " x" + t.Count : "")}: ~y~{info.Name}~s~";
        }

        // GTA's fonts can't draw emoji, and ~ starts a colour code, so strip both.
        static string Clean(string text, string fallback)
        {
            var sb = new StringBuilder();
            foreach (char c in text ?? "")
                if (c != '~' && !char.IsSurrogate(c) && !char.IsControl(c) && c < 0x2000)
                    sb.Append(c);
            string s = sb.ToString().Trim();
            if (s.Length == 0) s = fallback;
            return s.Length > 24 ? s.Substring(0, 23) + "." : s;
        }

        // ---------------------------------------------------------------- screen

        // Feed notification through natives (Notification.Show was renamed in the 3.7 nightlies).
        static void Notify(string message)
        {
            Function.Call(N.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(N.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);
            Function.Call(N.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
        }

        static void PlayFrontend(string sound, string set = "HUD_FRONTEND_DEFAULT_SOUNDSET") =>
            Function.Call(N.PLAY_SOUND_FRONTEND, -1, sound, set, true);

        static void Text(string text, float x, float y, float scale, Color colour, Font font, Alignment align) =>
            new TextElement(text, new PointF(x, y), scale, colour, font, align, true, true).Draw();

        void DrawGreeting(int now)
        {
            // Fade out over the last second
            int left = _greetingUntil - now;
            int alpha = left < 1000 ? Math.Max(0, left * 255 / 1000) : 255;
            bool connected = _link.Connected;
            float x = 640f, y = 40f;
            Text("GiftDeck GTA ready", x, y, 0.75f, Color.FromArgb(alpha, Green), Font.ChaletComprimeCologne, Alignment.Center);
            string line = connected
                ? "Connected to GiftDeck - gifts will play in game"
                : "Waiting for GiftDeck... (open GiftDeck on this PC)";
            Text(line, x, y + 38f, 0.42f, Color.FromArgb(alpha, connected ? Green : Color.White), Font.ChaletLondon, Alignment.Center);
            Text("Press " + _menuKey + " for the GiftDeck menu", x, y + 66f, 0.35f, Color.FromArgb(alpha, Grey), Font.ChaletLondon, Alignment.Center);
        }

        // ---------------------------------------------------------------- keys

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == _menuKey && !e.Control && !e.Shift && !e.Alt)
            {
                ToggleMenu();
                return;
            }
            if (_menuOpen) HandleMenuKey(e.KeyCode);
        }

        void SaveSetting(string section, string key, bool value)
        {
            Settings.SetValue(section, key, value);
            try { Settings.Save(); }
            catch (Exception ex) { Logger.Write("Couldn't save GiftDeckGTA.ini: " + ex.Message); }
        }
    }

    // A command that can't run right now, with a reason GiftDeck shows in its log.
    public class CommandFailed : Exception
    {
        public CommandFailed(string message) : base(message) { }
    }
}
