using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Maps a command name to a Revit API action. Runs on the UI thread (called
    /// from <see cref="RequestHandler.Execute"/>), so transactions are legal here.
    /// Add new tools by adding a case and a matching @mcp.tool in the Python server.
    /// </summary>
    public static class CommandRouter
    {
        public static Dictionary<string, object> Route(UIApplication app, string command, Dictionary<string, object> p)
        {
            var uidoc = app.ActiveUIDocument;
            switch (command)
            {
                case "ping":             return Ping(app);
                case "get_selection":    return GetSelection(uidoc);
                case "get_element_info": return GetElementInfo(uidoc, GetLong(p, "id"));
                case "set_parameter":    return SetParameter(uidoc, GetLong(p, "id"), GetString(p, "name"),
                                                              p.ContainsKey("value") ? p["value"] : null);
                default:
                    throw new ArgumentException($"Unknown command: {command}");
            }
        }

        // ---- read-only commands --------------------------------------------

        private static Dictionary<string, object> Ping(UIApplication app)
        {
            return new Dictionary<string, object>
            {
                ["version"] = app.Application.VersionNumber,
                ["versionName"] = app.Application.VersionName,
                ["document"] = app.ActiveUIDocument?.Document?.Title ?? "(none)"
            };
        }

        private static Dictionary<string, object> GetSelection(UIDocument uidoc)
        {
            if (uidoc == null) throw new InvalidOperationException("No active document.");
            var doc = uidoc.Document;
            var items = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .Select(ElementSummary)
                .ToList();
            return new Dictionary<string, object> { ["count"] = items.Count, ["elements"] = items };
        }

        private static Dictionary<string, object> GetElementInfo(UIDocument uidoc, long id)
        {
            if (uidoc == null) throw new InvalidOperationException("No active document.");
            var el = uidoc.Document.GetElement(new ElementId(id));
            if (el == null) throw new ArgumentException($"No element with id {id}.");

            var result = ElementSummary(el);
            var parameters = new Dictionary<string, object>();
            foreach (Parameter param in el.Parameters)
            {
                var name = param.Definition?.Name;
                if (!string.IsNullOrEmpty(name) && !parameters.ContainsKey(name))
                    parameters[name] = ParamValue(param);
            }
            result["parameters"] = parameters;
            return result;
        }

        // ---- write command (demonstrates the Transaction pattern) -----------

        private static Dictionary<string, object> SetParameter(UIDocument uidoc, long id, string name, object value)
        {
            if (uidoc == null) throw new InvalidOperationException("No active document.");
            var doc = uidoc.Document;
            var el = doc.GetElement(new ElementId(id));
            if (el == null) throw new ArgumentException($"No element with id {id}.");

            var param = el.LookupParameter(name);
            if (param == null) throw new ArgumentException($"Element {id} has no parameter '{name}'.");
            if (param.IsReadOnly) throw new InvalidOperationException($"Parameter '{name}' is read-only.");

            using (var t = new Transaction(doc, $"MCP: set {name}"))
            {
                t.Start();
                SetParamValue(param, value);
                t.Commit();
            }

            return new Dictionary<string, object> { ["id"] = id, ["name"] = name, ["value"] = ParamValue(param) };
        }

        // ---- helpers --------------------------------------------------------

        private static Dictionary<string, object> ElementSummary(Element e)
        {
            return new Dictionary<string, object>
            {
                ["id"] = e.Id.Value,           // Revit 2024: ElementId.Value is a long
                ["name"] = e.Name,
                ["category"] = e.Category?.Name,
                ["class"] = e.GetType().Name
            };
        }

        private static object ParamValue(Parameter p)
        {
            if (!p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.Double:    return p.AsDouble();
                case StorageType.Integer:   return p.AsInteger();
                case StorageType.String:    return p.AsString();
                case StorageType.ElementId: return p.AsElementId().Value;
                default:                    return p.AsValueString();
            }
        }

        private static void SetParamValue(Parameter p, object value)
        {
            switch (p.StorageType)
            {
                case StorageType.Double:    p.Set(Convert.ToDouble(value)); break;
                case StorageType.Integer:   p.Set(Convert.ToInt32(value)); break;
                case StorageType.String:    p.Set(Convert.ToString(value)); break;
                case StorageType.ElementId: p.Set(new ElementId(Convert.ToInt64(value))); break;
                default: throw new InvalidOperationException("Unsupported parameter storage type.");
            }
        }

        private static long GetLong(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null) throw new ArgumentException($"Missing parameter '{key}'.");
            return Convert.ToInt64(p[key]);
        }

        private static string GetString(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null) throw new ArgumentException($"Missing parameter '{key}'.");
            return Convert.ToString(p[key]);
        }
    }
}
