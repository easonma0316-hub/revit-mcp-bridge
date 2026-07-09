using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// One pending command plus its own completion state. Because every request
    /// owns its slot, a request that times out on the caller side can never leak
    /// its result into the next request — the old shared-field design had exactly
    /// that race.
    /// </summary>
    internal sealed class PendingRequest
    {
        public string Command;
        public Dictionary<string, object> Params;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public Dictionary<string, object> Result;
        public Exception Error;

        // Set true once the caller has given up waiting; the handler still runs the
        // command (it's already on the UI thread) but no one is listening.
        public volatile bool Abandoned;
    }

    /// <summary>
    /// The <see cref="IExternalEventHandler"/> that runs on Revit's UI thread. It
    /// drains a queue of <see cref="PendingRequest"/> items, so a single raised
    /// event can process everything that piled up while Revit was busy.
    /// </summary>
    public class RequestHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();

        internal void Enqueue(PendingRequest request) => _queue.Enqueue(request);

        public void Execute(UIApplication app)
        {
            // Drain everything currently queued. New arrivals will re-raise the event.
            while (_queue.TryDequeue(out var request))
            {
                try
                {
                    request.Result = CommandRouter.Route(app, request.Command, request.Params);
                }
                catch (Exception ex)
                {
                    request.Error = ex;
                    Log.Error($"Command '{request.Command}' failed", ex);
                }
                finally
                {
                    request.Done.Set();
                }
            }
        }

        public string GetName() => "RevitMCP.RequestHandler";
    }
}
