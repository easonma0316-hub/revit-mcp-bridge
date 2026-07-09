using System;
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

            try
            {
                _server.Start();
            }
            catch (System.Net.HttpListenerException ex)
            {
                // Most common cause on Windows: no URL ACL reservation and Revit isn't
                // elevated, so HttpListener.Start() fails with "Access is denied" (5).
                // Surface it instead of failing silently — the add-in would otherwise
                // report success while the MCP bridge never listens.
                TaskDialog.Show(
                    "RevitMCP",
                    "The MCP HTTP listener could not start on\n" +
                    Prefix + "\n\n" +
                    "Reason: " + ex.Message + " (code " + ex.ErrorCode + ")\n\n" +
                    "If this is an 'Access is denied' error, reserve the URL once from an\n" +
                    "elevated PowerShell:\n\n" +
                    "    netsh http add urlacl url=" + Prefix + " user=Everyone\n\n" +
                    "then restart Revit.");
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("RevitMCP", "The MCP HTTP listener failed to start:\n\n" + ex);
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _server?.Stop();
            return Result.Succeeded;
        }
    }
}
