using System.Text.Json;
using System.Text.Json.Serialization;

namespace GiftDeck.Services;

public static class Storage
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GiftDeck");

    static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string name) => Path.Combine(Dir, name);

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

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Storage.Dir);
                File.AppendAllText(Storage.PathFor("log.txt"), line + Environment.NewLine);
            }
            catch { }
        }
        Written?.Invoke(line);
    }
}
