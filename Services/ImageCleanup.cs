using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GiftDeck.Services;

// Pictures cut out of a screenshot often come with a solid black background instead of a see-through one,
// which shows as a black square on a coloured tile. When all four corners of a picture are solid black,
// the black joined to its edges is made see-through (black inside the picture, like sunglasses, stays).
// The result is kept in a cache next to GiftDeck's other data, so each picture is only done once.
public static class ImageCleanup
{
    const int Black = 16;      // this dark or darker, touching the background: see-through (screenshot black is 0-10)
    const int Soft = 60;       // edge pixels up to this dark get part see-through, so the outline isn't jagged

    static readonly object Lock = new object();

    // The picture to use: a cleaned copy when it has a black background, otherwise the file itself.
    public static string WithoutBlackBackground(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp")) return path; // gifs animate; leave them alone
            var info = new FileInfo(path);
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks)))[..20];
            var dir = Storage.PathFor("picture-cache");
            var done = Path.Combine(dir, key + ".png");
            var plain = Path.Combine(dir, key + ".none");   // checked before: no black background
            if (File.Exists(done)) return done;
            if (File.Exists(plain)) return path;
            lock (Lock)
            {
                Directory.CreateDirectory(dir);
                if (!Clean(path, done)) { File.WriteAllText(plain, ""); return path; }
                return done;
            }
        }
        catch (Exception e)
        {
            Log.Write("Couldn't check a picture's background: " + e.Message);
            return path;
        }
    }

    static bool Clean(string path, string output)
    {
        BitmapSource src;
        using (var fs = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            src = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        }
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        if (w < 8 || h < 8 || w * h > 4_000_000) return false;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);

        int Dark(int x, int y) { int i = y * stride + x * 4; return Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])); }
        bool Solid(int x, int y) => px[y * stride + x * 4 + 3] >= 250 && Dark(x, y) <= Black;
        // Only pictures whose four corners are solid black: anything else already has its own background.
        if (!(Solid(1, 1) && Solid(w - 2, 1) && Solid(1, h - 2) && Solid(w - 2, h - 2))) return false;

        // Flood from the edges through dark pixels: that's the background.
        var bg = new bool[w * h];
        var queue = new Queue<int>();
        void Seed(int x, int y) { int k = y * w + x; if (!bg[k] && Dark(x, y) <= Black) { bg[k] = true; queue.Enqueue(k); } }
        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
        while (queue.Count > 0)
        {
            int k = queue.Dequeue(), x = k % w, y = k / w;
            if (x > 0) Seed(x - 1, y);
            if (x < w - 1) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y < h - 1) Seed(x, y + 1);
        }

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int k = y * w + x, i = y * stride + x * 4;
                if (bg[k]) { px[i + 3] = 0; continue; }
                // A dark pixel right next to the background: part see-through, for a smooth edge.
                bool edge = (x > 0 && bg[k - 1]) || (x < w - 1 && bg[k + 1]) || (y > 0 && bg[k - w]) || (y < h - 1 && bg[k + w]);
                int d = Dark(x, y);
                if (edge && d < Soft) px[i + 3] = (byte)Math.Min(px[i + 3], (d - Black) * 255 / (Soft - Black));
            }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(result));
        var tmp = output + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, output, true);
        return true;
    }
}
