using System;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Add-in entry point. On startup it creates the UI-thread dispatcher and
    /// launches the HTTP listener the external MCP server talks to.
    ///
    /// Startup is deliberately fail-soft: if the listener can't bind (port taken,
    /// missing URL ACL, ...) we log it and show a dialog, but still return
    /// <see cref="Result.Succeeded"/> so Revit doesn't disable the add-in.
    /// </summary>
    public class RevitMcpApp : IExternalApplication
    {
        private HttpServer _server;
        private RevitDispatcher _dispatcher;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // ExternalEvent.Create must run on the Revit UI thread — OnStartup qualifies.
                _dispatcher = new RevitDispatcher();
                _server = new HttpServer(_dispatcher);
                var prefix = _server.Start(Config.PreferredPort, Config.PortProbeCount);

                Log.Info($"RevitMCP ready. readOnly={Config.ReadOnly}, confirmDestructive={Config.ConfirmDestructive}");

                if (Config.PreferredPort != PortOf(prefix))
                {
                    // Bound to a fallback port — tell the user so they can point the
                    // Python side at it via REVIT_MCP_URL.
                    TaskDialog.Show("RevitMCP",
                        $"Port {Config.PreferredPort} was busy; RevitMCP is listening on {prefix} instead.\n\n" +
                        $"Set REVIT_MCP_URL={prefix} for the MCP server so they match.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("RevitMCP failed to start", ex);
                TaskDialog.Show("RevitMCP",
                    "RevitMCP could not start its listener, so AI tools will be unavailable.\n\n" +
                    ex.Message + "\n\nSee the log at:\n" + Log.Path);
                // Swallow so Revit keeps the add-in enabled for the next session.
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _server?.Stop();
            return Result.Succeeded;
        }

        private static int PortOf(string prefix)
        {
            try { return new Uri(prefix).Port; }
            catch { return -1; }
        }
    }
}
