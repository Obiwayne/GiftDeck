using System.Text.Json;
using System.Text.Json.Serialization;

namespace GiftDeck.Services;

public static class Storage
{
    // GIFTDECK_DATA points a development build at its own folder, so it never touches the data
    // the installed (stable) GiftDeck uses. run-dev.ps1 sets it.
    public static readonly string Dir = Environment.GetEnvironmentVariable("GIFTDECK_DATA") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GiftDeck");

    public static bool IsDevData => Environment.GetEnvironmentVariable("GIFTDECK_DATA") is { Length: > 0 };

    static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string name) => Path.Combine(Dir, name);

    // Files that were there but couldn't be read this session, each already copied aside (shown to the user once).
    public static readonly List<string> Unreadable = new List<string>();

    public static T Load<T>(string name) where T : class
    {
        try
        {
            var p = PathFor(name);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Opts);
        }
        catch (Exception e)
        {
            Log.Write($"Could not read {name}: {e.Message}");
            // The caller carries on with defaults and will save over this file, so keep the original first.
            try
            {
                var p = PathFor(name);
                if (File.Exists(p))
                {
                    var copy = p + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                    File.Copy(p, copy, true);
                    Log.Write($"Kept a copy of {name} as {Path.GetFileName(copy)}");
                    lock (Unreadable) Unreadable.Add(copy);
                }
            }
            catch { }
            return null;
        }
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor(name)));
            var tmp = PathFor(name + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
            File.Move(tmp, PathFor(name), true);
        }
        catch (Exception e)
        {
            Log.Write($"Could not save {name}: {e.Message}");
        }
    }

    public static void Delete(string name)
    {
        try { File.Delete(PathFor(name)); } catch { }
    }
}

public static class Log
{
    public static event Action<string> Written;
    static readonly object Lock = new object();
    static int _writes;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Storage.Dir);
                var path = Storage.PathFor("log.txt");
                if (++_writes % 500 == 1 && File.Exists(path) && new FileInfo(path).Length > 5_000_000)
                    File.Move(path, Storage.PathFor("log.old.txt"), true); // keep the log from growing forever
                File.AppendAllText(Storage.PathFor("log.txt"), line + Environment.NewLine);
            }
            catch { }
        }
        Written?.Invoke(line);
    }
}
