using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.UI;
using Font = GTA.UI.Font;

namespace GiftDeckGTA
{
    // The F10 menu, like StreamToEarn's: Up/Down to move, Left/Right to change a value, Enter to pick,
    // Backspace to go back (or close). Shows the connection, pauses triggers, and tests any command.
    public partial class GiftDeckScript
    {
        const int VisibleRows = 12;

        class MenuItem
        {
            public string Label;
            public Func<string> Value;
            public Action<int> Adjust; // left/right
            public Action Select;      // enter
            public Func<Color?> ValueColour;
        }

        class MenuPage
        {
            public string Title;
            public Func<List<MenuItem>> Build;
            public List<MenuItem> Items;
            public int Index, Scroll;
        }

        readonly Stack<MenuPage> _pages = new Stack<MenuPage>();
        bool _menuOpen;

        // Argument values picked in the test menu, per command (starts at the catalog defaults)
        readonly Dictionary<string, Dictionary<string, string>> _testArgs = new Dictionary<string, Dictionary<string, string>>();

        void ToggleMenu()
        {
            if (_menuOpen)
            {
                CloseMenu();
                return;
            }
            _pages.Clear();
            Push("GIFTDECK", MainItems);
            _menuOpen = true;
            PlayFrontend("SELECT");
        }

        void CloseMenu()
        {
            _menuOpen = false;
            _pages.Clear();
            PlayFrontend("BACK");
        }

        void Push(string title, Func<List<MenuItem>> build)
        {
            var page = new MenuPage { Title = title, Build = build };
            page.Items = build();
            _pages.Push(page);
        }

        List<MenuItem> MainItems() => new List<MenuItem>
        {
            new MenuItem
            {
                Label = "GiftDeck",
                Value = () => _link.Connected ? "Connected" : "Waiting...",
                ValueColour = () => _link.Connected ? Green : Red,
                Select = () => Notify(_link.Connected
                    ? $"Connected to GiftDeck at {_link.Url}~n~{_handled} command{(_handled == 1 ? "" : "s")} run this session"
                    : $"Waiting for GiftDeck at {_link.Url}~n~" + (_link.LastError.Length > 0 ? _link.LastError : "Is GiftDeck open?")),
            },
            new MenuItem
            {
                Label = "Gift triggers",
                Value = () => _paused ? "Paused" : "Running",
                ValueColour = () => _paused ? Red : Green,
                Select = TogglePause,
                Adjust = d => TogglePause(),
            },
            new MenuItem { Label = "Test a command  >", Select = () => Push("TEST A COMMAND", CategoryItems) },
            new MenuItem { Label = "Settings  >", Select = () => Push("SETTINGS", SettingsItems) },
            new MenuItem { Label = "Reconnect now", Select = () => { _link.Reconnect(); Notify("Reconnecting to GiftDeck..."); } },
            new MenuItem { Label = "Remove spawned attackers", Select = () => Notify(RemoveAttackers()) },
            new MenuItem { Label = "Close", Select = CloseMenu },
        };

        void TogglePause()
        {
            _paused = !_paused;
            Notify(_paused ? "~r~Gift triggers paused~s~ - GiftDeck is told they didn't run" : "~g~Gift triggers running");
            Logger.Write(_paused ? "Triggers paused" : "Triggers resumed");
            SendStatus();
        }

        List<MenuItem> CategoryItems() => Catalog.Categories.Select(category => new MenuItem
        {
            Label = category + "  >",
            Value = () => Catalog.Commands.Count(c => c.Category == category).ToString(),
            Select = () => Push(category.ToUpperInvariant(), () => CommandItems(category)),
        }).ToList();

        List<MenuItem> CommandItems(string category) => Catalog.Commands.Where(c => c.Category == category).Select(c =>
        {
            var arg = c.Args.FirstOrDefault();
            return new MenuItem
            {
                Label = c.Name,
                Value = arg == null ? (Func<string>)null : () => TestArg(c, arg),
                Adjust = arg == null ? (Action<int>)null : d => ChangeTestArg(c, arg, d),
                Select = () => RunTest(c),
            };
        }).ToList();

        List<MenuItem> SettingsItems() => new List<MenuItem>
        {
            Toggle("Gift notifications", () => _showGifts, v => { _showGifts = v; SaveSetting("Notifications", "ShowGifts", v); }),
            Toggle("Connection notifications", () => _showConnection, v => { _showConnection = v; SaveSetting("Notifications", "ShowConnection", v); }),
            Toggle("Greeting on load", () => _showGreeting, v => { _showGreeting = v; SaveSetting("Notifications", "ShowGreeting", v); }),
            new MenuItem { Label = "GiftDeck address", Value = () => $"{_host}:{_port}", Select = () => Notify("Change Host and Port in scripts\\GiftDeckGTA.ini") },
            new MenuItem { Label = "Menu key", Value = () => _menuKey.ToString(), Select = () => Notify("Change [Menu] Key in scripts\\GiftDeckGTA.ini") },
        };

        MenuItem Toggle(string label, Func<bool> get, Action<bool> set) => new MenuItem
        {
            Label = label,
            Value = () => get() ? "On" : "Off",
            ValueColour = () => get() ? Green : Red,
            Select = () => set(!get()),
            Adjust = d => set(!get()),
        };

        // ---------------------------------------------------------------- testing commands

        Dictionary<string, string> TestArgs(CommandInfo c)
        {
            if (!_testArgs.TryGetValue(c.Id, out var args))
                _testArgs[c.Id] = args = c.Args.ToDictionary(a => a.Name, a => a.Default);
            return args;
        }

        string TestArg(CommandInfo c, ArgInfo arg) => TestArgs(c)[arg.Name];

        void ChangeTestArg(CommandInfo c, ArgInfo arg, int direction)
        {
            var args = TestArgs(c);
            string value = args[arg.Name];
            if (arg.Choices.Length > 0)
            {
                int i = Array.IndexOf(arg.Choices, value);
                args[arg.Name] = arg.Choices[((i < 0 ? 0 : i + direction) % arg.Choices.Length + arg.Choices.Length) % arg.Choices.Length];
            }
            else if (arg.Type == "number")
            {
                int.TryParse(arg.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out int def);
                int step = def >= 1000 ? 500 : def >= 100 ? 50 : def >= 20 ? 5 : 1;
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
                args[arg.Name] = (n + direction * step).ToString(CultureInfo.InvariantCulture);
            }
            PlayFrontend("NAV_LEFT_RIGHT");
        }

        void RunTest(CommandInfo c)
        {
            var t = new Trigger
            {
                Id = "menu-" + Now,
                Command = c.Id,
                User = "Menu test",
                FromMenu = true,
                Args = TestArgs(c).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            };
            // Teleports and spawns are easier to see with the menu out of the way
            if (c.Category == "Teleport" || c.Id == "skydive" || c.Id == "launch_up") CloseMenu();
            Handle(t);
        }

        // ---------------------------------------------------------------- keys and drawing

        void HandleMenuKey(Keys key)
        {
            var page = _pages.Peek();
            var items = page.Items;
            if (items.Count == 0 && key != Keys.Back) return;
            switch (key)
            {
                case Keys.Up:
                    page.Index = (page.Index + items.Count - 1) % items.Count;
                    PlayFrontend("NAV_UP_DOWN");
                    break;
                case Keys.Down:
                    page.Index = (page.Index + 1) % items.Count;
                    PlayFrontend("NAV_UP_DOWN");
                    break;
                case Keys.Left:
                    items[page.Index].Adjust?.Invoke(-1);
                    break;
                case Keys.Right:
                    items[page.Index].Adjust?.Invoke(1);
                    break;
                case Keys.Enter:
                    PlayFrontend("SELECT");
                    items[page.Index].Select?.Invoke();
                    break;
                case Keys.Back:
                    if (_pages.Count > 1)
                    {
                        _pages.Pop();
                        PlayFrontend("BACK");
                    }
                    else CloseMenu();
                    break;
            }
        }

        void DrawMenu()
        {
            if (_pages.Count == 0) return;
            // Stop the arrow keys and Enter from also driving the phone, weapon wheel etc.
            Function.Call(N.DISABLE_ALL_CONTROL_ACTIONS, 0);

            var page = _pages.Peek();
            var items = page.Items;
            if (page.Index < page.Scroll) page.Scroll = page.Index;
            if (page.Index >= page.Scroll + VisibleRows) page.Scroll = page.Index - VisibleRows + 1;

            const float x = 50f, w = 440f, rowH = 36f;
            float y = 110f;

            new ContainerElement(new PointF(x, y), new SizeF(w, 88f), Color.FromArgb(235, 12, 12, 22)).Draw();
            new ContainerElement(new PointF(x, y), new SizeF(w, 5f), Accent).Draw();
            Text(page.Title, x + w / 2f, y + 12f, 0.8f, Color.White, Font.Pricedown, Alignment.Center);
            string sub = _pages.Count > 1 ? "GIFTDECK GTA" : "GIFTDECK GTA  " + Catalog.Version;
            Text(sub, x + w / 2f, y + 58f, 0.33f, Accent, Font.ChaletLondon, Alignment.Center);
            y += 88f;

            int end = Math.Min(items.Count, page.Scroll + VisibleRows);
            for (int i = page.Scroll; i < end; i++)
            {
                var item = items[i];
                bool selected = i == page.Index;
                Color bg = selected ? Color.FromArgb(245, 240, 240, 245) : Color.FromArgb(215, 20, 20, 32);
                Color fg = selected ? Color.FromArgb(255, 15, 15, 15) : Color.White;
                new ContainerElement(new PointF(x, y), new SizeF(w, rowH), bg).Draw();
                Text(item.Label, x + 16f, y + 6f, 0.4f, fg, Font.ChaletLondon, Alignment.Left);
                if (item.Value != null)
                {
                    string value = item.Value();
                    if (value.Length > 22) value = value.Substring(0, 21) + ".";
                    if (selected && item.Adjust != null) value = "<  " + value + "  >";
                    Color vc = item.ValueColour?.Invoke() ?? fg;
                    if (selected && vc != fg) vc = Color.FromArgb(255, vc.R * 3 / 5, vc.G * 3 / 5, vc.B * 3 / 5); // readable on the light bar
                    Text(value, x + w - 16f, y + 4f, 0.46f, vc, Font.ChaletComprimeCologne, Alignment.Right);
                }
                y += rowH;
            }

            string footer = items.Count > VisibleRows ? $"{page.Index + 1} / {items.Count}     " : "";
            footer += _pages.Count > 1 ? "Enter pick   Left/Right change   Backspace back" : "Enter pick   Left/Right change   Backspace close";
            new ContainerElement(new PointF(x, y), new SizeF(w, 32f), Color.FromArgb(235, 12, 12, 22)).Draw();
            Text(footer, x + w / 2f, y + 8f, 0.28f, Grey, Font.ChaletLondon, Alignment.Center);
        }
    }
}
