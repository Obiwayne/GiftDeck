using System.Collections.ObjectModel;
using System.Windows;
using GiftDeck.Models;

namespace GiftDeck.Services;

// Known TikTok gifts: a built-in list, plus whatever TikFinity reports (its config on connect, and live gifts).
public class GiftCatalog
{
    public ObservableCollection<GiftInfo> Gifts { get; } = new ObservableCollection<GiftInfo>();

    public void Load()
    {
        foreach (var g in BuiltIn()) MergeInternal(g, false);
        var saved = Storage.Load<List<GiftInfo>>("gifts.json") ?? new List<GiftInfo>();
        foreach (var g in saved) MergeInternal(g, true);
        Sort();
    }

    public void Save()
    {
        Storage.Save("gifts.json", Gifts.ToList());
    }

    // Called from any thread. Gifts reported by TikFinity are trusted over the built-in list.
    public void Learn(GiftInfo g)
    {
        if (g == null || string.IsNullOrWhiteSpace(g.Name)) return;
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            if (MergeInternal(g, true))
            {
                Sort();
                Save();
            }
        });
    }

    public GiftInfo Find(long id, string name)
    {
        GiftInfo byId = id != 0 ? Gifts.FirstOrDefault(x => x.Id == id) : null;
        if (byId != null) return byId;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Gifts.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    bool MergeInternal(GiftInfo g, bool trusted)
    {
        var existing = Find(g.Id, g.Name);
        if (existing == null)
        {
            Gifts.Add(new GiftInfo { Id = g.Id, Name = g.Name.Trim(), Coins = g.Coins, ImageUrl = g.ImageUrl });
            return true;
        }
        bool changed = false;
        if (existing.Id == 0 && g.Id != 0) { existing.Id = g.Id; changed = true; }
        if (trusted && g.Coins > 0 && existing.Coins != g.Coins) { existing.Coins = g.Coins; changed = true; }
        if (trusted && !string.IsNullOrEmpty(g.Name) && existing.Name != g.Name.Trim()) { existing.Name = g.Name.Trim(); changed = true; }
        if (string.IsNullOrEmpty(existing.ImageUrl) && !string.IsNullOrEmpty(g.ImageUrl)) { existing.ImageUrl = g.ImageUrl; changed = true; }
        return changed;
    }

    void Sort()
    {
        var sorted = Gifts.OrderBy(g => g.Coins).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Gifts.IndexOf(sorted[i]);
            if (cur != i) Gifts.Move(cur, i);
        }
    }

    static IEnumerable<GiftInfo> BuiltIn()
    {
        GiftInfo G(string name, int coins, long id = 0) => new GiftInfo { Name = name, Coins = coins, Id = id };
        return new[]
        {
            G("Rose", 1, 5655), G("TikTok", 1, 5269), G("GG", 1, 6064), G("Ice Cream Cone", 1, 5827), G("Heart Me", 1, 7934),
            G("Coffee", 1), G("Love You", 1), G("Team Bracelet", 2), G("Finger Heart", 5, 5487), G("Mic", 5),
            G("Rosa", 10), G("Friendship Necklace", 10), G("Perfume", 20, 5658), G("Doughnut", 30, 5879),
            G("Paper Crane", 99), G("Little Crown", 99), G("Cap", 99), G("Hat and Mustache", 99), G("Hand Hearts", 100),
            G("Confetti", 100), G("Sunglasses", 199, 5509), G("Corgi", 299), G("Boxing Gloves", 299), G("Money Gun", 500),
            G("Swan", 699), G("Train", 899), G("Galaxy", 1000, 11046), G("Glowing Jellyfish", 1000, 8972), G("Gold Mine", 1000),
            G("Fireworks", 1088), G("Chasing the Dream", 1500), G("Whale Diving", 2150), G("Motorcycle", 2988), G("Ferris Wheel", 3000),
            G("Private Jet", 4888), G("Leon the Kitten", 4888), G("Sports Car", 7000), G("Star Throne", 7999), G("Yacht", 9888),
            G("Interstellar", 10000), G("Diamond Flight", 10000), G("Falcon", 10999), G("Planet", 15000), G("Rocket", 20000),
            G("Phoenix", 25999), G("Adam's Dream", 25999), G("Dragon Flame", 26999), G("Lion", 29999), G("TikTok Stars", 39999),
            G("TikTok Universe", 44999),
        };
    }
}
