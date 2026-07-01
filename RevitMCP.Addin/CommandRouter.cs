using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Maps a command name to a Revit API action. Runs on the UI thread (called
    /// from <see cref="RequestHandler.Execute"/>), so transactions are legal here.
    ///
    /// Conventions:
    ///  - read commands need no transaction; commands that change the model wrap
    ///    the change in one.
    ///  - commands that modify the document call <see cref="EnsureWritable"/> first
    ///    so a global read-only mode is honoured (UI-only actions like selection
    ///    and temporary isolate stay available).
    ///  - errors throw <see cref="McpException"/> with a stable code so the LLM can
    ///    reason about them.
    ///  - all lengths/coordinates are Revit internal units (feet).
    /// </summary>
    public static class CommandRouter
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 5000;

        public static Dictionary<string, object> Route(UIApplication app, string command, Dictionary<string, object> p)
        {
            p = p ?? new Dictionary<string, object>();
            var uidoc = app.ActiveUIDocument;

            switch (command)
            {
                // ---- meta / read ------------------------------------------------
                case "ping":             return Ping(app);
                case "get_model_info":   return GetModelInfo(app);
                case "list_categories":  return ListCategories(RequireDoc(uidoc));
                case "query_elements":   return QueryElements(RequireDoc(uidoc), p);
                case "get_element_info": return GetElementInfo(RequireDoc(uidoc), GetLong(p, "id"));
                case "get_parameter":    return GetParameter(RequireDoc(uidoc), GetLong(p, "id"), GetString(p, "name"));
                case "get_selection":    return GetSelection(uidoc);
                case "list_views":       return ListViews(RequireDoc(uidoc));
                case "get_active_view":  return GetActiveView(uidoc);
                case "list_levels":      return ListLevels(RequireDoc(uidoc));

                // ---- write ------------------------------------------------------
                case "set_parameter":    return SetParameter(uidoc, p);
                case "set_selection":    return SetSelection(uidoc, p);
                case "color_elements":   return ColorElements(uidoc, p);
                case "isolate_elements": return IsolateElements(uidoc, p);
                case "reset_view":       return ResetView(uidoc);
                case "delete_elements":  return DeleteElements(uidoc, p);

                default:
                    throw new McpException(McpException.UnknownCommand, $"Unknown command: {command}");
            }
        }

        // ==================== read commands ==================================

        private static Dictionary<string, object> Ping(UIApplication app)
        {
            return new Dictionary<string, object>
            {
                ["service"] = "RevitMCP",
                ["version"] = app.Application.VersionNumber,
                ["versionName"] = app.Application.VersionName,
                ["document"] = app.ActiveUIDocument?.Document?.Title ?? "(none)",
                ["readOnly"] = Config.ReadOnly
            };
        }

        private static Dictionary<string, object> GetModelInfo(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc?.Document == null)
                throw new McpException(McpException.NoDocument, "No active document. Open a model in Revit first.");
            var doc = uidoc.Document;

            int instanceCount = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
            int viewCount = new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>().Count(v => !v.IsTemplate);
            int levelCount = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();

            return new Dictionary<string, object>
            {
                ["title"] = doc.Title,
                ["path"] = string.IsNullOrEmpty(doc.PathName) ? "(unsaved)" : doc.PathName,
                ["isWorkshared"] = doc.IsWorkshared,
                ["isFamilyDocument"] = doc.IsFamilyDocument,
                ["revitVersion"] = app.Application.VersionNumber,
                ["activeView"] = uidoc.ActiveView?.Name,
                ["elementInstanceCount"] = instanceCount,
                ["viewCount"] = viewCount,
                ["levelCount"] = levelCount,
                ["readOnly"] = Config.ReadOnly
            };
        }

        private static Dictionary<string, object> ListCategories(Document doc)
        {
            var counts = new Dictionary<string, int>();
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var name = e.Category?.Name;
                if (string.IsNullOrEmpty(name)) continue;
                counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
            }
            var list = counts.OrderByDescending(kv => kv.Value)
                .Select(kv => new Dictionary<string, object> { ["name"] = kv.Key, ["count"] = kv.Value })
                .ToList();
            return new Dictionary<string, object> { ["count"] = list.Count, ["categories"] = list };
        }

        private static Dictionary<string, object> QueryElements(Document doc, Dictionary<string, object> p)
        {
            int limit = ClampLimit(GetIntOr(p, "limit", DefaultLimit));
            string category = p.ContainsKey("category") ? Convert.ToString(p["category"]) : null;
            string nameLike = p.ContainsKey("name_contains") ? Convert.ToString(p["name_contains"]) : null;

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            IEnumerable<Element> query = collector;

            var bic = TryParseCategory(category);
            if (bic.HasValue)
                query = collector.OfCategory(bic.Value);
            else if (!string.IsNullOrWhiteSpace(category))
            {
                var target = category.Trim();
                query = collector.Where(e => e.Category != null &&
                    string.Equals(e.Category.Name, target, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(nameLike))
            {
                var needle = nameLike.Trim();
                query = query.Where(e => (e.Name ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // Take limit+1 so we can flag truncation without a second full pass.
            var page = query.Take(limit + 1).ToList();
            bool truncated = page.Count > limit;
            var items = page.Take(limit).Select(ElementSummary).ToList();

            return new Dictionary<string, object>
            {
                ["count"] = items.Count,
                ["truncated"] = truncated,
                ["limit"] = limit,
                ["elements"] = items
            };
        }

        private static Dictionary<string, object> GetElementInfo(Document doc, long id)
        {
            var el = doc.GetElement(new ElementId(id))
                     ?? throw new McpException(McpException.NotFound, $"No element with id {id}.");

            var result = ElementSummary(el);

            // Type
            var typeId = el.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                var type = doc.GetElement(typeId);
                if (type != null)
                {
                    result["typeId"] = typeId.Value;
                    result["typeName"] = type.Name;
                }
            }

            // Level
            if (el.LevelId != null && el.LevelId != ElementId.InvalidElementId)
            {
                var level = doc.GetElement(el.LevelId);
                if (level != null) result["level"] = level.Name;
            }

            // Location
            var loc = DescribeLocation(el.Location);
            if (loc != null) result["location"] = loc;

            // Bounding box (in the model, view = null)
            try
            {
                var bb = el.get_BoundingBox(null);
                if (bb != null)
                    result["boundingBox"] = new Dictionary<string, object>
                    {
                        ["min"] = Xyz(bb.Min),
                        ["max"] = Xyz(bb.Max)
                    };
            }
            catch { /* some elements have no bbox */ }

            // Parameters
            var parameters = new Dictionary<string, object>();
            foreach (Parameter param in el.Parameters)
            {
                try
                {
                    var name = param.Definition?.Name;
                    if (!string.IsNullOrEmpty(name) && !parameters.ContainsKey(name))
                        parameters[name] = ParamValue(param);
                }
                catch { /* skip a misbehaving parameter rather than fail the call */ }
            }
            result["parameters"] = parameters;
            return result;
        }

        private static Dictionary<string, object> GetParameter(Document doc, long id, string name)
        {
            var el = doc.GetElement(new ElementId(id))
                     ?? throw new McpException(McpException.NotFound, $"No element with id {id}.");
            var param = el.LookupParameter(name)
                        ?? throw new McpException(McpException.NotFound, $"Element {id} has no parameter '{name}'.");
            return new Dictionary<string, object>
            {
                ["id"] = id,
                ["name"] = name,
                ["value"] = ParamValue(param),
                ["displayValue"] = SafeValueString(param),
                ["storageType"] = param.StorageType.ToString(),
                ["isReadOnly"] = param.IsReadOnly
            };
        }

        private static Dictionary<string, object> GetSelection(UIDocument uidoc)
        {
            var doc = RequireDoc(uidoc);
            var items = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .Select(ElementSummary)
                .ToList();
            return new Dictionary<string, object> { ["count"] = items.Count, ["elements"] = items };
        }

        private static Dictionary<string, object> ListViews(Document doc)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Select(v => new Dictionary<string, object>
                {
                    ["id"] = v.Id.Value,
                    ["name"] = v.Name,
                    ["viewType"] = v.ViewType.ToString()
                })
                .ToList();
            return new Dictionary<string, object> { ["count"] = views.Count, ["views"] = views };
        }

        private static Dictionary<string, object> GetActiveView(UIDocument uidoc)
        {
            RequireDoc(uidoc);
            var v = uidoc.ActiveView
                    ?? throw new McpException(McpException.NotFound, "No active view.");
            return new Dictionary<string, object>
            {
                ["id"] = v.Id.Value,
                ["name"] = v.Name,
                ["viewType"] = v.ViewType.ToString()
            };
        }

        private static Dictionary<string, object> ListLevels(Document doc)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new Dictionary<string, object>
                {
                    ["id"] = l.Id.Value,
                    ["name"] = l.Name,
                    ["elevation"] = l.Elevation
                })
                .ToList();
            return new Dictionary<string, object> { ["count"] = levels.Count, ["levels"] = levels };
        }

        // ==================== write commands =================================

        private static Dictionary<string, object> SetParameter(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var name = GetString(p, "name");
            var value = p.ContainsKey("value") ? p["value"] : null;
            var ids = GetIdList(p, allowSingle: true);

            var updated = new List<Dictionary<string, object>>();
            using (var t = new Transaction(doc, $"MCP: set {name}"))
            {
                t.Start();
                foreach (var id in ids)
                {
                    var el = doc.GetElement(new ElementId(id));
                    if (el == null)
                        throw new McpException(McpException.NotFound, $"No element with id {id}.");
                    var param = el.LookupParameter(name);
                    if (param == null)
                        throw new McpException(McpException.NotFound, $"Element {id} has no parameter '{name}'.");
                    if (param.IsReadOnly)
                        throw new McpException(McpException.ReadOnlyParam, $"Parameter '{name}' on element {id} is read-only.");

                    SetParamValue(param, value);
                    updated.Add(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["value"] = ParamValue(param)
                    });
                }
                t.Commit();
            }

            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["updatedCount"] = updated.Count,
                ["updated"] = updated
            };
        }

        private static Dictionary<string, object> SetSelection(UIDocument uidoc, Dictionary<string, object> p)
        {
            // Selection is UI-only, not a document change, so it is allowed even in
            // read-only mode.
            var doc = RequireDoc(uidoc);
            var ids = GetIdList(p, allowSingle: true)
                .Select(id => new ElementId(id))
                .Where(eid => doc.GetElement(eid) != null)
                .ToList();
            uidoc.Selection.SetElementIds(ids);
            return new Dictionary<string, object> { ["selectedCount"] = ids.Count };
        }

        private static Dictionary<string, object> ColorElements(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var view = uidoc.ActiveView
                       ?? throw new McpException(McpException.NotFound, "No active view to apply overrides to.");

            byte r = (byte)GetIntOr(p, "r", 255);
            byte g = (byte)GetIntOr(p, "g", 0);
            byte b = (byte)GetIntOr(p, "b", 0);
            var color = new Color(r, g, b);
            var ids = GetIdList(p, allowSingle: true).Select(id => new ElementId(id)).ToList();

            var solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);

            int applied = 0;
            using (var t = new Transaction(doc, "MCP: color elements"))
            {
                t.Start();
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(color);
                if (solidFill != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                    ogs.SetSurfaceForegroundPatternColor(color);
                }
                foreach (var id in ids)
                {
                    if (doc.GetElement(id) == null) continue;
                    view.SetElementOverrides(id, ogs);
                    applied++;
                }
                t.Commit();
            }
            return new Dictionary<string, object>
            {
                ["view"] = view.Name,
                ["coloredCount"] = applied,
                ["color"] = new Dictionary<string, object> { ["r"] = r, ["g"] = g, ["b"] = b }
            };
        }

        private static Dictionary<string, object> IsolateElements(UIDocument uidoc, Dictionary<string, object> p)
        {
            // Temporary isolate is a transient view state (no transaction), so it is
            // allowed in read-only mode.
            var doc = RequireDoc(uidoc);
            var view = uidoc.ActiveView
                       ?? throw new McpException(McpException.NotFound, "No active view.");

            var ids = GetIdList(p, allowSingle: true)
                .Select(id => new ElementId(id))
                .Where(eid => doc.GetElement(eid) != null)
                .ToList();

            // Temporary isolate is a view state, not a document change — no transaction.
            view.IsolateElementsTemporarily(ids);
            return new Dictionary<string, object>
            {
                ["view"] = view.Name,
                ["isolatedCount"] = ids.Count,
                ["note"] = "Temporary isolate; call reset_view to clear."
            };
        }

        private static Dictionary<string, object> ResetView(UIDocument uidoc)
        {
            RequireDoc(uidoc);
            var view = uidoc.ActiveView
                       ?? throw new McpException(McpException.NotFound, "No active view.");
            view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            return new Dictionary<string, object> { ["view"] = view.Name, ["reset"] = true };
        }

        private static Dictionary<string, object> DeleteElements(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var ids = GetIdList(p, allowSingle: true)
                .Select(id => new ElementId(id))
                .Where(eid => doc.GetElement(eid) != null)
                .ToList();

            if (ids.Count == 0)
                throw new McpException(McpException.NotFound, "None of the given ids exist in the model.");

            if (Config.ConfirmDestructive)
            {
                var td = new TaskDialog("RevitMCP")
                {
                    MainInstruction = $"Delete {ids.Count} element(s)?",
                    MainContent = "An AI tool requested this deletion via RevitMCP.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                if (td.Show() != TaskDialogResult.Yes)
                    throw new McpException(McpException.Cancelled, "Deletion was declined in Revit.");
            }

            ICollection<ElementId> deleted;
            using (var t = new Transaction(doc, "MCP: delete elements"))
            {
                t.Start();
                deleted = doc.Delete(ids); // includes dependent elements
                t.Commit();
            }
            return new Dictionary<string, object>
            {
                ["requestedCount"] = ids.Count,
                ["deletedCount"] = deleted?.Count ?? 0
            };
        }

        // ==================== helpers ========================================

        private static void EnsureWritable()
        {
            if (Config.ReadOnly)
                throw new McpException(McpException.ReadOnly,
                    "RevitMCP is in read-only mode (REVIT_MCP_READONLY). Write commands are disabled.");
        }

        private static Document RequireDoc(UIDocument uidoc)
        {
            if (uidoc?.Document == null)
                throw new McpException(McpException.NoDocument, "No active document. Open a model in Revit first.");
            return uidoc.Document;
        }

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

        private static Dictionary<string, object> DescribeLocation(Location location)
        {
            if (location is LocationPoint lp)
                return new Dictionary<string, object> { ["type"] = "point", ["point"] = Xyz(lp.Point) };
            if (location is LocationCurve lc && lc.Curve != null)
                return new Dictionary<string, object>
                {
                    ["type"] = "curve",
                    ["start"] = Xyz(lc.Curve.GetEndPoint(0)),
                    ["end"] = Xyz(lc.Curve.GetEndPoint(1))
                };
            return null;
        }

        private static Dictionary<string, object> Xyz(XYZ p)
            => new Dictionary<string, object> { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };

        private static object ParamValue(Parameter p)
        {
            if (!p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.Double:    return p.AsDouble();
                case StorageType.Integer:   return p.AsInteger();
                case StorageType.String:    return p.AsString();
                case StorageType.ElementId: return p.AsElementId().Value;
                default:                    return SafeValueString(p);
            }
        }

        private static string SafeValueString(Parameter p)
        {
            try { return p.AsValueString(); }
            catch { return null; }
        }

        private static void SetParamValue(Parameter p, object value)
        {
            switch (p.StorageType)
            {
                case StorageType.Double:    p.Set(Convert.ToDouble(value)); break;
                case StorageType.Integer:   p.Set(Convert.ToInt32(value)); break;
                case StorageType.String:    p.Set(Convert.ToString(value)); break;
                case StorageType.ElementId: p.Set(new ElementId(Convert.ToInt64(value))); break;
                default:
                    throw new McpException(McpException.Unsupported, "Unsupported parameter storage type.");
            }
        }

        private static BuiltInCategory? TryParseCategory(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var candidate = s.Trim();
            var withPrefix = candidate.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                ? candidate : "OST_" + candidate;
            if (Enum.TryParse(withPrefix, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                return bic;
            if (Enum.TryParse(candidate, true, out bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                return bic;
            return null;
        }

        // ---- parameter parsing ---------------------------------------------

        private static long GetLong(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null)
                throw new McpException(McpException.BadRequest, $"Missing parameter '{key}'.");
            try { return Convert.ToInt64(p[key]); }
            catch { throw new McpException(McpException.BadRequest, $"Parameter '{key}' must be an integer id."); }
        }

        private static string GetString(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null)
                throw new McpException(McpException.BadRequest, $"Missing parameter '{key}'.");
            return Convert.ToString(p[key]);
        }

        private static int GetIntOr(Dictionary<string, object> p, string key, int fallback)
        {
            if (!p.ContainsKey(key) || p[key] == null) return fallback;
            try { return Convert.ToInt32(p[key]); }
            catch { return fallback; }
        }

        private static int ClampLimit(int limit)
            => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

        /// <summary>
        /// Reads element ids from either "ids" (array) or "id" (single). Accepts the
        /// various shapes JavaScriptSerializer produces for a JSON array.
        /// </summary>
        private static List<long> GetIdList(Dictionary<string, object> p, bool allowSingle)
        {
            var result = new List<long>();

            if (p.ContainsKey("ids") && p["ids"] is IEnumerable en && !(p["ids"] is string))
            {
                foreach (var item in en)
                {
                    if (item == null) continue;
                    try { result.Add(Convert.ToInt64(item)); }
                    catch { throw new McpException(McpException.BadRequest, "Every entry in 'ids' must be an integer."); }
                }
            }

            if (allowSingle && result.Count == 0 && p.ContainsKey("id") && p["id"] != null)
                result.Add(GetLong(p, "id"));

            if (result.Count == 0)
                throw new McpException(McpException.BadRequest, "Provide an element id via 'id' or 'ids'.");

            return result;
        }
    }
}
