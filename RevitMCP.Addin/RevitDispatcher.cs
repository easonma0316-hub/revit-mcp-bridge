using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Marshals command execution from the background HTTP thread onto Revit's UI
    /// thread via an ExternalEvent, then blocks until the result is ready.
    /// </summary>
    public class RevitDispatcher
    {
        private readonly ExternalEvent _externalEvent;
        private readonly RequestHandler _handler;

        // Revit allows only one ExternalEvent round-trip at a time, so serialize requests.
        private readonly object _gate = new object();

        public RevitDispatcher()
        {
            _handler = new RequestHandler();
            _externalEvent = ExternalEvent.Create(_handler);
        }

        public Dictionary<string, object> Execute(string command, Dictionary<string, object> parameters, int timeoutMs = 30000)
        {
            lock (_gate)
            {
                _handler.Prepare(command, parameters);
                _externalEvent.Raise();

                if (!_handler.Done.Wait(timeoutMs))
                    throw new TimeoutException(
                        $"Revit did not process '{command}' within {timeoutMs} ms " +
                        "(is a modal dialog open, or is Revit busy?).");

                if (_handler.Error != null)
                    throw new Exception(_handler.Error.Message, _handler.Error);

                return _handler.Result;
            }
        }
    }
}
