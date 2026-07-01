using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Marshals command execution from a background HTTP thread onto Revit's UI
    /// thread. Each call gets its own <see cref="PendingRequest"/>, so requests can
    /// be queued concurrently and a timed-out request can't corrupt a later one.
    /// </summary>
    public class RevitDispatcher
    {
        private readonly ExternalEvent _externalEvent;
        private readonly RequestHandler _handler;

        public RevitDispatcher()
        {
            _handler = new RequestHandler();
            _externalEvent = ExternalEvent.Create(_handler);
        }

        public Dictionary<string, object> Execute(string command, Dictionary<string, object> parameters, int timeoutMs)
        {
            var request = new PendingRequest
            {
                Command = command,
                Params = parameters ?? new Dictionary<string, object>()
            };

            _handler.Enqueue(request);
            _externalEvent.Raise();

            if (!request.Done.Wait(timeoutMs))
            {
                request.Abandoned = true;
                throw new McpException(McpException.Internal,
                    $"Revit did not process '{command}' within {timeoutMs} ms. " +
                    "A modal dialog may be open, or Revit may be busy with a long operation.");
            }

            if (request.Error != null)
            {
                if (request.Error is McpException) throw request.Error;
                throw new McpException(McpException.Internal, request.Error.Message, request.Error);
            }

            return request.Result;
        }
    }
}
