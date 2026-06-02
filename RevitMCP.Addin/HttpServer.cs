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
    /// Minimal JSON-over-HTTP listener on a background thread. Each POST body is
    /// { "command": "...", "params": { ... } }; the command is run on the Revit
    /// UI thread via the dispatcher and the result returned as JSON.
    /// </summary>
    public class HttpServer
    {
        private readonly HttpListener _listener;
        private readonly RevitDispatcher _dispatcher;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private Thread _thread;
        private volatile bool _running;

        public HttpServer(string prefix, RevitDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "RevitMCP.HttpServer" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
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
            string responseJson;
            try
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream,
                                                     ctx.Request.ContentEncoding ?? Encoding.UTF8))
                    body = reader.ReadToEnd();

                var request = _json.Deserialize<Dictionary<string, object>>(body)
                              ?? new Dictionary<string, object>();

                var command = request.ContainsKey("command") ? Convert.ToString(request["command"]) : null;
                var parameters = request.ContainsKey("params") ? request["params"] as Dictionary<string, object> : null;

                if (string.IsNullOrEmpty(command))
                    throw new ArgumentException("Request must include a 'command' field.");

                var result = _dispatcher.Execute(command, parameters);
                responseJson = _json.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["result"] = result
                });
            }
            catch (Exception ex)
            {
                responseJson = _json.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = ex.Message
                });
            }

            var buffer = Encoding.UTF8.GetBytes(responseJson);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buffer.Length;
            try { ctx.Response.OutputStream.Write(buffer, 0, buffer.Length); }
            catch { /* client gone */ }
            finally { ctx.Response.OutputStream.Close(); }
        }
    }
}
