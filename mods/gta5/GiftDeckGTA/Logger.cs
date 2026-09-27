using System;
using System.IO;

namespace GiftDeckGTA
{
    // scripts\GiftDeckGTA\log.txt: what happened, for when something doesn't work on stream.
    internal static class Logger
    {
        static string _path;
        static readonly object _lock = new object();

        public static void Init(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "log.txt");
                // Keep the log from growing forever
                if (File.Exists(_path) && new FileInfo(_path).Length > 2 * 1024 * 1024)
                    File.Delete(_path);
            }
            catch { }
        }

        public static void Write(string message)
        {
            if (_path == null)
                return;
            lock (_lock)
            {
                try { File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); }
                catch { }
            }
        }
    }
}
