using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Minimal JSON-over-HTTP listener on a background thread.
    ///
    /// POST /  body: { "command": "...", "params": { ... } }
    ///         -> { "ok": true, "result": {...} }  or
    ///            { "ok": false, "error": "...", "code": "..." }
    ///
    /// GET  /  -> lightweight health probe (no Revit UI thread needed), so a
    ///            browser or curl can confirm the listener is alive.
    /// </summary>
    public class HttpServer
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly RevitDispatcher _dispatcher;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private Thread _thread;
        private volatile bool _running;

        /// <summary>The prefix the listener actually bound to (may differ from the preferred port).</summary>
        public string BoundPrefix { get; private set; }

        public HttpServer(RevitDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _json.MaxJsonLength = 64 * 1024 * 1024; // large models can produce big element lists
        }

        /// <summary>
        /// Bind to the preferred port, falling back to the next few ports if it's
        /// taken. Returns the bound prefix, or throws if none could be claimed.
        /// </summary>
        public string Start(int preferredPort, int probeCount)
        {
            HttpListenerException last = null;
            for (int i = 0; i < probeCount; i++)
            {
                int port = preferredPort + i;
                var prefix = $"http://127.0.0.1:{port}/";
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add(prefix);
                    _listener.Start();
                    BoundPrefix = prefix;
                    _running = true;
                    _thread = new Thread(Loop) { IsBackground = true, Name = "RevitMCP.HttpServer" };
                    _thread.Start();
                    Log.Info($"HTTP listener started on {prefix}");
                    return prefix;
                }
                catch (HttpListenerException ex)
                {
                    last = ex;
                    Log.Warn($"Port {port} unavailable ({ex.Message}); trying next.");
                }
            }
            throw new InvalidOperationException(
                $"Could not bind any port in {preferredPort}..{preferredPort + probeCount - 1}.", last);
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
            Log.Info("HTTP listener stopped.");
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; } // listener was stopped
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                if (ctx.Request.HttpMethod == "GET")
                {
                    WriteJson(ctx, 200, Health());
                    return;
                }
                if (ctx.Request.HttpMethod != "POST")
                {
                    WriteJson(ctx, 405, Fail(McpException.BadRequest, "Use POST with a JSON body, or GET for health."));
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream,
                                                     ctx.Request.ContentEncoding ?? Encoding.UTF8))
                    body = reader.ReadToEnd();

                Dictionary<string, object> request;
                try
                {
                    request = _json.Deserialize<Dictionary<string, object>>(body)
                              ?? new Dictionary<string, object>();
                }
                catch (Exception ex)
                {
                    WriteJson(ctx, 400, Fail(McpException.BadRequest, "Body is not valid JSON: " + ex.Message));
                    return;
                }

                var command = request.ContainsKey("command") ? Convert.ToString(request["command"]) : null;
                var parameters = request.ContainsKey("params") ? request["params"] as Dictionary<string, object> : null;

                if (string.IsNullOrEmpty(command))
                {
                    WriteJson(ctx, 400, Fail(McpException.BadRequest, "Request must include a 'command' field."));
                    return;
                }

                Log.Info($"-> {command}");
                var result = _dispatcher.Execute(command, parameters, Config.CommandTimeoutMs);
                WriteJson(ctx, 200, new Dictionary<string, object> { ["ok"] = true, ["result"] = result });
            }
            catch (McpException ex)
            {
                WriteJson(ctx, 200, Fail(ex.Code, ex.Message));
            }
            catch (Exception ex)
            {
                Log.Error("Unhandled request error", ex);
                WriteJson(ctx, 200, Fail(McpException.Internal, ex.Message));
            }
        }

        private Dictionary<string, object> Health()
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["result"] = new Dictionary<string, object>
                {
                    ["service"] = "RevitMCP",
                    ["status"] = "alive",
                    ["readOnly"] = Config.ReadOnly,
                    ["port"] = BoundPrefix
                }
            };
        }

        private static Dictionary<string, object> Fail(string code, string message)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = message,
                ["code"] = code
            };
        }

        private void WriteJson(HttpListenerContext ctx, int status, Dictionary<string, object> payload)
        {
            var buffer = Encoding.UTF8.GetBytes(_json.Serialize(payload));
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch { /* client gone */ }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { /* ignore */ }
            }
        }
    }
}
