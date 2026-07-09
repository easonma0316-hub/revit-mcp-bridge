using System;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Runtime configuration, read once from environment variables at startup.
    /// Everything has a safe default so a plain install "just works".
    ///
    ///   REVIT_MCP_PORT       preferred listener port (default 8765). If taken,
    ///                        the server probes the next few ports automatically.
    ///   REVIT_MCP_READONLY   "1"/"true" blocks every model-changing command.
    ///   REVIT_MCP_CONFIRM    "0"/"false" skips the Revit confirmation dialog for
    ///                        destructive commands (delete). Confirmation is ON by
    ///                        default so nothing is destroyed silently.
    ///   REVIT_MCP_TIMEOUT_MS how long a single command may run on the UI thread
    ///                        (default 60000).
    /// </summary>
    public static class Config
    {
        public static int PreferredPort { get; } = ParseInt("REVIT_MCP_PORT", 8765);
        public static bool ReadOnly { get; private set; } = ParseBool("REVIT_MCP_READONLY", false);
        public static bool ConfirmDestructive { get; } = ParseBool("REVIT_MCP_CONFIRM", true);
        public static int CommandTimeoutMs { get; } = ParseInt("REVIT_MCP_TIMEOUT_MS", 60000);

        /// <summary>How many consecutive ports to try if the preferred one is busy.</summary>
        public const int PortProbeCount = 10;

        private static bool ParseBool(string key, bool fallback)
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrEmpty(v)) return fallback;
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        private static int ParseInt(string key, int fallback)
        {
            var v = Environment.GetEnvironmentVariable(key);
            return int.TryParse(v, out var n) && n > 0 ? n : fallback;
        }
    }
}
