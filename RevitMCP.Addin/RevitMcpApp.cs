using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Add-in entry point. On startup it creates the UI-thread dispatcher and
    /// launches the HTTP listener that the external MCP server talks to.
    /// </summary>
    public class RevitMcpApp : IExternalApplication
    {
        // Change the port here if 8765 is taken (also update REVIT_MCP_URL on the Python side).
        private const string Prefix = "http://127.0.0.1:8765/";

        private HttpServer _server;
        private RevitDispatcher _dispatcher;

        public Result OnStartup(UIControlledApplication application)
        {
            // ExternalEvent.Create must run on the Revit UI thread — OnStartup is a valid context.
            _dispatcher = new RevitDispatcher();
            _server = new HttpServer(Prefix, _dispatcher);
            _server.Start();
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _server?.Stop();
            return Result.Succeeded;
        }
    }
}
