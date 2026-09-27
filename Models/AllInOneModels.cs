namespace GiftDeck.Models;

// ---- All-in-one overlay: one Browser Source / TikTok LIVE Studio link that shows every part ----
// See docs/v2.1/all-in-one-overlay.md.

// Where a part sits on the all-in-one page.
public enum OverlaySpot { TopLeft, Top, TopRight, Left, Middle, Right, BottomLeft, Bottom, BottomRight }

public class AllInOnePart
{
    public bool On { get; set; }
    public OverlaySpot Spot { get; set; } = OverlaySpot.Top;

    // How wide the part is, in % of the page width.
    int _width = 100;
    public int Width { get => _width; set => _width = Math.Clamp(value, 10, 100); }
}

public class AllInOneConfig
{
    public AllInOnePart Alerts { get; set; } = new AllInOnePart { On = true, Spot = OverlaySpot.Middle, Width = 100 };
    public AllInOnePart Spinner { get; set; } = new AllInOnePart { On = true, Spot = OverlaySpot.Middle, Width = 70 };
    public AllInOnePart Goals { get; set; } = new AllInOnePart { On = true, Spot = OverlaySpot.Top, Width = 100 };
    public AllInOnePart Strip { get; set; } = new AllInOnePart { On = true, Spot = OverlaySpot.Top, Width = 100 };
    public AllInOnePart Board { get; set; } = new AllInOnePart { On = false, Spot = OverlaySpot.Bottom, Width = 100 };
    public AllInOnePart GiftList { get; set; } = new AllInOnePart { On = false, Spot = OverlaySpot.Right, Width = 40 };

    // The wheel only shows while it spins, so it doesn't cover the game the rest of the time.
    public bool SpinnerOnlyWhileSpinning { get; set; } = true;

    // Text and bars are drawn this much bigger (a 1080 x 1920 page is shrunk a lot on a phone).
    int _scale = 130;
    public int Scale { get => _scale; set => _scale = Math.Clamp(value, 50, 300); }

    // Keep the top and bottom edges clear, where the TikTok app draws its own buttons and chat.
    public bool LeaveRoomForTikTok { get; set; } = true;

    // The size used by Add to OBS and the preview: portrait 1080 x 1920, or landscape 1920 x 1080.
    public bool Landscape { get; set; }
}
