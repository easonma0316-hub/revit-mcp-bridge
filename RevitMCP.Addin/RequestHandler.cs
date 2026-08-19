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

        /// <summary>True while an MCP request runs on the UI thread.</summary>
        internal static bool InRequest { get; private set; }

        public void Execute(UIApplication app)
        {
            // Nobody sits at the screen during an MCP request: a warning dialog would
            // freeze the bridge until someone clicks it away. Swallow warnings raised by
            // any transaction we run (errors still surface); the user's own edits are
            // untouched because the handler only acts while InRequest is set.
            EnsureFailureHook(app);
            InRequest = true;
            try
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
            finally { InRequest = false; }
        }

        internal sealed class SwallowedWarning
        {
            public string Description;
            public int Count;
            public DateTime Last;
            public HashSet<long> ElementIds = new HashSet<long>();
        }
        /// <summary>Warnings deleted by the hook during MCP requests, by description (since start-up).</summary>
        internal static readonly Dictionary<string, SwallowedWarning> SwallowedWarnings = new Dictionary<string, SwallowedWarning>();

        private static bool _hooked;
        private static void EnsureFailureHook(UIApplication app)
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                app.Application.FailuresProcessing += (s, e) =>
                {
                    if (!InRequest) return;
                    var fa = e.GetFailuresAccessor();
                    bool any = false;
                    foreach (var f in fa.GetFailureMessages())
                    {
                        if (f.GetSeverity() != Autodesk.Revit.DB.FailureSeverity.Warning) continue;
                        // remember what was swallowed (list_warnings reports it)
                        try
                        {
                            var desc = f.GetDescriptionText();
                            lock (SwallowedWarnings)
                            {
                                if (!SwallowedWarnings.TryGetValue(desc, out var rec)) SwallowedWarnings[desc] = rec = new SwallowedWarning { Description = desc };
                                rec.Count++;
                                rec.Last = DateTime.Now;
                                foreach (var id in f.GetFailingElementIds()) if (rec.ElementIds.Count < 200) rec.ElementIds.Add(id.Value);
                            }
                        }
                        catch { }
                        fa.DeleteWarning(f); any = true;
                    }
                    if (any) e.SetProcessingResult(Autodesk.Revit.DB.FailureProcessingResult.Continue);
                };
            }
            catch (Exception ex) { Log.Error("FailuresProcessing hook failed", ex); }
        }

        public string GetName() => "RevitMCP.RequestHandler";
    }
}
