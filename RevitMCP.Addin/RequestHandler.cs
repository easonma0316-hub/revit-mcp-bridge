using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// The ExternalEventHandler that actually runs on the Revit UI thread.
    /// The dispatcher fills in the pending request, raises the event, and waits
    /// on <see cref="Done"/>; Execute() runs the command and signals completion.
    /// </summary>
    public class RequestHandler : IExternalEventHandler
    {
        private string _command;
        private Dictionary<string, object> _params;

        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public Dictionary<string, object> Result { get; private set; }
        public Exception Error { get; private set; }

        public void Prepare(string command, Dictionary<string, object> parameters)
        {
            _command = command;
            _params = parameters ?? new Dictionary<string, object>();
            Result = null;
            Error = null;
            Done.Reset();
        }

        public void Execute(UIApplication app)
        {
            try
            {
                Result = CommandRouter.Route(app, _command, _params);
            }
            catch (Exception ex)
            {
                Error = ex;
            }
            finally
            {
                Done.Set();
            }
        }

        public string GetName() => "RevitMCP.RequestHandler";
    }
}
