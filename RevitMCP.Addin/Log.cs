using System;
using System.IO;
using System.Text;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Tiny append-only file logger. Debugging an add-in that lives inside Revit
    /// is painful without a trail, so every request, error, and lifecycle event is
    /// written here. Failures to log are swallowed on purpose — logging must never
    /// take Revit down.
    /// </summary>
    internal static class Log
    {
        private static readonly object _gate = new object();

        /// <summary>Log file path, e.g. %LOCALAPPDATA%\RevitMCP\RevitMCP.log.</summary>
        public static string Path { get; } = BuildPath();

        private static string BuildPath()
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevitMCP");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "RevitMCP.log");
            }
            catch
            {
                // Fall back to the temp folder if LocalAppData is unavailable.
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevitMCP.log");
            }
        }

        public static void Info(string message)  => Write("INFO", message);
        public static void Warn(string message)  => Write("WARN", message);
        public static void Error(string message, Exception ex = null)
            => Write("ERROR", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

        private static void Write(string level, string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                lock (_gate)
                {
                    // Keep the log from growing without bound (~2 MB cap, best effort).
                    try
                    {
                        var fi = new FileInfo(Path);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                            File.WriteAllText(Path, string.Empty);
                    }
                    catch { /* ignore rotation failure */ }

                    File.AppendAllText(Path, line, Encoding.UTF8);
                }
            }
            catch { /* logging must never throw */ }
        }
    }
}
