using System.Windows;
using System.Windows.Controls;

namespace GiftDeck.Views;

// The Games page's grid: as many equal columns as fit (each at least MinTileWidth), tiles stretched to fill the
// row so the right edge lines up, rows as tall as their tallest tile.
public class GameCatalogGrid : Panel
{
    public double MinTileWidth { get; set; } = 236;
    public double Gap { get; set; } = 18;
    public double RowGap { get; set; } = 22;

    int Columns(double width) => Math.Max(1, (int)Math.Floor((width + Gap) / (MinTileWidth + Gap)));

    double TileWidth(double width, int cols) => Math.Max(0, (width - Gap * (cols - 1)) / cols);

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 1256 : available.Width;
        int cols = Columns(width);
        double tile = TileWidth(width, cols);
        double height = 0, row = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(tile, double.PositiveInfinity));
            row = Math.Max(row, child.DesiredSize.Height);
            if (++i % cols == 0) { height += row + RowGap; row = 0; }
        }
        if (i % cols != 0) height += row;
        else if (i > 0) height -= RowGap;
        return new Size(i == 0 ? 0 : width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double tile = TileWidth(final.Width, cols);
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double y = 0;
        for (int start = 0; start < visible.Count; start += cols)
        {
            var rowItems = visible.Skip(start).Take(cols).ToList();
            double row = rowItems.Max(c => c.DesiredSize.Height);
            for (int c = 0; c < rowItems.Count; c++)
                rowItems[c].Arrange(new Rect(c * (tile + Gap), y, tile, row));
            y += row + RowGap;
        }
        return final;
    }
}

// Keeps its child at a fixed height/width ratio of whatever width it's given (the covers' 460x215).
public class AspectBox : Decorator
{
    public double Ratio { get; set; } = CoverCache.Aspect;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 460 : available.Width;
        var size = new Size(w, w * Ratio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size final)
    {
        var size = new Size(final.Width, final.Width * Ratio);
        Child?.Arrange(new Rect(size));
        return size;
    }
}
