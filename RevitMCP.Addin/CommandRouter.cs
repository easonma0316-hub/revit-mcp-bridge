using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
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
    ///  - lengths/coordinates cross the API in millimeters (converted to Revit's
    ///    internal feet here); parameter values use the model's display units.
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
                case "ping":              return Ping(app);
                case "get_model_info":    return GetModelInfo(app);
                case "list_categories":   return ListCategories(RequireDoc(uidoc));
                case "query_elements":    return QueryElements(RequireDoc(uidoc), p);
                case "get_element_info":  return GetElementInfo(RequireDoc(uidoc), GetLong(p, "id"));
                case "get_parameter":     return GetParameter(RequireDoc(uidoc), GetLong(p, "id"), GetString(p, "name"));
                case "get_selection":     return GetSelection(uidoc);
                case "list_views":        return ListViews(uidoc);
                case "get_active_view":   return GetActiveView(uidoc);
                case "list_levels":       return ListLevels(RequireDoc(uidoc));
                case "list_family_types": return ListFamilyTypes(RequireDoc(uidoc), GetOptString(p, "category"),
                                                                 GetOptString(p, "contains"), GetIntOr(p, "limit", DefaultLimit));
                case "get_view_elements": return GetViewElements(uidoc, GetOptLong(p, "view_id"), GetIntOr(p, "limit", 100));
                case "export_view_image": return ExportViewImage(uidoc, GetOptLong(p, "view_id"), GetIntOr(p, "pixels", 1600));

                // ---- write ------------------------------------------------------
                case "set_parameter":    return SetParameter(uidoc, p);
                case "set_selection":    return SetSelection(uidoc, p);
                case "color_elements":   return ColorElements(uidoc, p);
                case "isolate_elements": return IsolateElements(uidoc, p);
                case "reset_view":       return ResetView(uidoc);
                case "set_active_view":  return SetActiveView(uidoc, GetLong(p, "view_id"));
                case "delete_elements":  return DeleteElements(uidoc, p);
                case "move_elements":    return MoveElements(uidoc, p);
                case "copy_elements":    return CopyElements(uidoc, p);
                case "create_wall":      return CreateWall(uidoc, p);
                case "create_floor":     return CreateFloor(uidoc, p);
                case "create_level":     return CreateLevel(uidoc, GetString(p, "name"), GetDouble(p, "elevation_mm"));
                case "create_grid":      return CreateGrid(uidoc, p);
                case "create_room":      return CreateRoom(uidoc, p);
                case "place_family_instance": return PlaceFamilyInstance(uidoc, p);
                case "execute_code":     return ExecuteCode(app, GetString(p, "code"));

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

            var pi = doc.ProjectInformation;
            string lengthUnit = null;
            try
            {
                var unitId = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                lengthUnit = LabelUtils.GetLabelForUnit(unitId);
            }
            catch { /* leave null if the units API misbehaves */ }

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
                ["projectName"] = pi?.Name,
                ["projectNumber"] = pi?.Number,
                ["client"] = pi?.ClientName,
                ["address"] = pi?.Address,
                ["lengthUnit"] = lengthUnit,
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
            long? levelId = GetOptLong(p, "level_id");

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

            if (levelId.HasValue)
                query = query.Where(e => e.LevelId != null && e.LevelId.Value == levelId.Value);

            // Take limit+1 so we can flag truncation without a second full pass.
            var page = query.Take(limit + 1).ToList();
            bool truncated = page.Count > limit;
            var items = page.Take(limit).Select(e =>
            {
                var s = ElementSummary(e);
                if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
                    s["level"] = doc.GetElement(e.LevelId)?.Name;
                return s;
            }).ToList();

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

        private static Dictionary<string, object> ListViews(UIDocument uidoc)
        {
            var doc = RequireDoc(uidoc);
            var activeId = uidoc.ActiveView?.Id ?? ElementId.InvalidElementId;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Select(v => new Dictionary<string, object>
                {
                    ["id"] = v.Id.Value,
                    ["name"] = v.Name,
                    ["viewType"] = v.ViewType.ToString(),
                    ["isActive"] = v.Id == activeId
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
                    ["elevation_mm"] = Math.Round(FtToMm(l.Elevation), 2)
                })
                .ToList();
            return new Dictionary<string, object> { ["count"] = levels.Count, ["levels"] = levels };
        }

        private static Dictionary<string, object> ListFamilyTypes(Document doc, string category, string contains, int limit)
        {
            limit = ClampLimit(limit);
            IEnumerable<FamilySymbol> symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>();
            if (!string.IsNullOrEmpty(category))
                symbols = symbols.Where(s => string.Equals(s.Category?.Name, category, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(contains))
                symbols = symbols.Where(s => (s.Family.Name + " " + s.Name).IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);

            var page = symbols.Take(limit + 1).ToList();
            bool truncated = page.Count > limit;
            var items = page.Take(limit)
                .Select(s => new Dictionary<string, object>
                {
                    ["id"] = s.Id.Value,
                    ["category"] = s.Category?.Name,
                    ["family"] = s.Family.Name,
                    ["type"] = s.Name
                })
                .ToList();
            return new Dictionary<string, object>
            {
                ["count"] = items.Count,
                ["truncated"] = truncated,
                ["familyTypes"] = items
            };
        }

        private static Dictionary<string, object> GetViewElements(UIDocument uidoc, long? viewId, int limit)
        {
            var doc = RequireDoc(uidoc);
            var view = GetView(uidoc, viewId);
            limit = ClampLimit(limit);
            var all = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                .Where(e => e.Category != null)
                .ToList();
            var counts = all.GroupBy(e => e.Category.Name)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => (object)g.Count());
            var items = all.Take(limit).Select(ElementSummary).ToList();
            return new Dictionary<string, object>
            {
                ["view"] = view.Name,
                ["total"] = all.Count,
                ["categories"] = counts,
                ["count"] = items.Count,
                ["elements"] = items
            };
        }

        private static Dictionary<string, object> ExportViewImage(UIDocument uidoc, long? viewId, int pixels)
        {
            var doc = RequireDoc(uidoc);
            var view = GetView(uidoc, viewId);

            // Revit appends "- <view type> - <view name>" to FilePath, so export into a
            // fresh directory and return whatever single file shows up there.
            var dir = Path.Combine(Path.GetTempPath(), "RevitMCP", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var opts = new ImageExportOptions
            {
                FilePath = Path.Combine(dir, "view"),
                ExportRange = ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixels,
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG
            };
            opts.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(opts);

            var file = Directory.GetFiles(dir).FirstOrDefault();
            if (file == null)
                throw new McpException(McpException.Unsupported,
                    $"Export of view '{view.Name}' produced no image (not a graphical view?).");
            return new Dictionary<string, object> { ["view"] = view.Name, ["path"] = file };
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
            var view = GetView(uidoc, GetOptLong(p, "view_id"));
            bool clear = p.ContainsKey("clear") && p["clear"] != null && Convert.ToBoolean(p["clear"]);

            var ogs = new OverrideGraphicSettings(); // empty settings = reset to defaults
            byte r = 0, g = 0, b = 0;
            if (!clear)
            {
                r = (byte)GetIntOr(p, "r", 255);
                g = (byte)GetIntOr(p, "g", 0);
                b = (byte)GetIntOr(p, "b", 0);
                var color = new Color(r, g, b);
                var solidFill = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                ogs.SetProjectionLineColor(color);
                if (solidFill != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetCutForegroundPatternId(solidFill.Id);
                    ogs.SetCutForegroundPatternColor(color);
                }
            }

            var ids = GetIdList(p, allowSingle: true).Select(id => new ElementId(id)).ToList();
            int applied = 0;
            using (var t = new Transaction(doc, clear ? "MCP: clear element colors" : "MCP: color elements"))
            {
                t.Start();
                foreach (var id in ids)
                {
                    if (doc.GetElement(id) == null) continue;
                    view.SetElementOverrides(id, ogs);
                    applied++;
                }
                t.Commit();
            }
            var result = new Dictionary<string, object>
            {
                ["view"] = view.Name,
                [clear ? "clearedCount" : "coloredCount"] = applied
            };
            if (!clear)
                result["color"] = new Dictionary<string, object> { ["r"] = r, ["g"] = g, ["b"] = b };
            return result;
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
            view.IsolateElementsTemporary(ids);
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

        private static Dictionary<string, object> SetActiveView(UIDocument uidoc, long viewId)
        {
            // UI-only (no document change), so allowed in read-only mode.
            var view = GetView(uidoc, viewId);
            if (view.IsTemplate)
                throw new McpException(McpException.Unsupported, $"View '{view.Name}' is a template and cannot be opened.");
            // Direct assignment of ActiveView is not allowed from an API event handler;
            // RequestViewChange applies as soon as Revit regains control (right after this reply).
            uidoc.RequestViewChange(view);
            return new Dictionary<string, object> { ["id"] = view.Id.Value, ["view"] = view.Name, ["status"] = "requested" };
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

        private static Dictionary<string, object> MoveElements(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var ids = ToExistingIds(doc, GetIdList(p, allowSingle: true));
            var vector = GetPointMm(p, "vector", 0);
            using (var t = new Transaction(doc, "MCP: move elements"))
            {
                t.Start();
                ElementTransformUtils.MoveElements(doc, ids, vector);
                t.Commit();
            }
            return new Dictionary<string, object> { ["movedCount"] = ids.Count };
        }

        private static Dictionary<string, object> CopyElements(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var ids = ToExistingIds(doc, GetIdList(p, allowSingle: true));
            var vector = GetPointMm(p, "vector", 0);
            ICollection<ElementId> copies;
            using (var t = new Transaction(doc, "MCP: copy elements"))
            {
                t.Start();
                copies = ElementTransformUtils.CopyElements(doc, ids, vector);
                t.Commit();
            }
            return new Dictionary<string, object>
            {
                ["copiedCount"] = copies.Count,
                ["newIds"] = copies.Select(id => id.Value).ToList()
            };
        }

        private static Dictionary<string, object> CreateWall(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");

            var start = GetPointMm(p, "start", level.Elevation);
            var end = GetPointMm(p, "end", level.Elevation);
            var height = MmToFt(GetDouble(p, "height_mm"));

            var optType = GetOptLong(p, "type_id");
            var typeId = optType.HasValue ? new ElementId(optType.Value)
                                          : doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);

            Wall wall;
            using (var t = new Transaction(doc, "MCP: create wall"))
            {
                t.Start();
                wall = Wall.Create(doc, Line.CreateBound(start, end), typeId, level.Id,
                                   height, 0, false, false);
                t.Commit();
            }
            return ElementSummary(wall);
        }

        private static Dictionary<string, object> CreateFloor(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");

            var pts = GetPointListMm(p, "boundary", level.Elevation);
            // Callers may repeat the first point to close the loop; we close it ourselves.
            if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3)
                throw new McpException(McpException.BadRequest,
                    "'boundary' needs at least 3 distinct [x, y] points in mm.");

            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
                loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));

            var optType = GetOptLong(p, "type_id");
            var typeId = optType.HasValue ? new ElementId(optType.Value)
                                          : doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType);

            Floor floor;
            using (var t = new Transaction(doc, "MCP: create floor"))
            {
                t.Start();
                floor = Floor.Create(doc, new List<CurveLoop> { loop }, typeId, level.Id);
                t.Commit();
            }
            return ElementSummary(floor);
        }

        private static Dictionary<string, object> CreateLevel(UIDocument uidoc, string name, double elevationMm)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            Level level;
            using (var t = new Transaction(doc, "MCP: create level"))
            {
                t.Start();
                level = Level.Create(doc, MmToFt(elevationMm));
                level.Name = name; // throws if the name is already taken
                t.Commit();
            }
            var result = ElementSummary(level);
            result["elevation_mm"] = Math.Round(FtToMm(level.Elevation), 2);
            return result;
        }

        private static Dictionary<string, object> CreateGrid(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var start = GetPointMm(p, "start", 0);
            var end = GetPointMm(p, "end", 0);
            var name = GetOptString(p, "name");

            Grid grid;
            using (var t = new Transaction(doc, "MCP: create grid"))
            {
                t.Start();
                grid = Grid.Create(doc, Line.CreateBound(start, end));
                if (!string.IsNullOrEmpty(name)) grid.Name = name;
                t.Commit();
            }
            return ElementSummary(grid);
        }

        private static Dictionary<string, object> CreateRoom(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            var point = GetPointMm(p, "point", level.Elevation);

            Room room;
            using (var t = new Transaction(doc, "MCP: create room"))
            {
                t.Start();
                room = doc.Create.NewRoom(level, new UV(point.X, point.Y));
                t.Commit();
            }
            var result = ElementSummary(room);
            result["number"] = room.Number;
            // 0 m2 means the point is not inside a closed loop of room-bounding elements.
            result["area_m2"] = Math.Round(UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters), 3);
            return result;
        }

        private static Dictionary<string, object> PlaceFamilyInstance(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var symbol = doc.GetElement(new ElementId(GetLong(p, "type_id"))) as FamilySymbol
                         ?? throw new McpException(McpException.NotFound,
                             "'type_id' is not a family type (use list_family_types).");

            var point = GetPointMm(p, "point", 0);
            var optLevel = GetOptLong(p, "level_id");
            var level = optLevel.HasValue ? doc.GetElement(new ElementId(optLevel.Value)) as Level : null;
            if (optLevel.HasValue && level == null)
                throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");

            FamilyInstance inst;
            using (var t = new Transaction(doc, $"MCP: place {symbol.Name}"))
            {
                t.Start();
                if (!symbol.IsActive) symbol.Activate();
                inst = level != null
                    ? doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
                t.Commit();
            }
            return ElementSummary(inst);
        }

        // ---- execute_code (dual-use: only exposed when the MCP server sets ---
        // ---- REVIT_MCP_ENABLE_CODE — see the README's warning section) -------

        private static Dictionary<string, object> ExecuteCode(UIApplication app, string code)
        {
            EnsureWritable();
            var uidoc = app.ActiveUIDocument;
            var doc = RequireDoc(uidoc);

            const string prefix = @"using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

public static class McpDynamicCode
{
    public static object Run(UIApplication app, UIDocument uidoc, Document doc)
    {
";
            var source = prefix + code + "\n    }\n}\n";
            var prefixLines = prefix.Count(c => c == '\n');

            System.CodeDom.Compiler.CompilerResults results;
            using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
            {
                var pars = new System.CodeDom.Compiler.CompilerParameters { GenerateInMemory = true };
                pars.ReferencedAssemblies.Add("System.dll");
                pars.ReferencedAssemblies.Add("System.Core.dll");
                pars.ReferencedAssemblies.Add(typeof(Document).Assembly.Location);      // RevitAPI
                pars.ReferencedAssemblies.Add(typeof(UIApplication).Assembly.Location); // RevitAPIUI
                results = provider.CompileAssemblyFromSource(pars, source);
            }
            if (results.Errors.HasErrors)
            {
                var errors = results.Errors.Cast<System.CodeDom.Compiler.CompilerError>()
                    .Where(e => !e.IsWarning)
                    .Select(e => $"line {e.Line - prefixLines}: {e.ErrorText}");
                throw new McpException(McpException.BadRequest,
                    "C# compilation failed:\n" + string.Join("\n", errors));
            }

            var run = results.CompiledAssembly.GetType("McpDynamicCode").GetMethod("Run");
            object value;
            using (var t = new Transaction(doc, "MCP: execute code"))
            {
                t.Start();
                try
                {
                    value = run.Invoke(null, new object[] { app, uidoc, doc });
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    // Transaction is disposed without commit -> model changes roll back.
                    throw ex.InnerException ?? ex;
                }
                t.Commit();
            }
            return new Dictionary<string, object> { ["result"] = Jsonable(value, 0) };
        }

        // Best-effort conversion of an arbitrary returned object into something the
        // JSON serializer can emit (Revit types get their useful projection).
        private static object Jsonable(object v, int depth)
        {
            if (v == null) return null;
            if (v is string || v is bool || v is int || v is long || v is double || v is float || v is decimal)
                return v;
            if (depth >= 4) return v.ToString();
            if (v is ElementId id) return id.Value;
            if (v is Element el) return ElementSummary(el);
            if (v is XYZ xyz) return Xyz(xyz);
            if (v is IDictionary dict)
            {
                var result = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dict)
                    result[Convert.ToString(entry.Key)] = Jsonable(entry.Value, depth + 1);
                return result;
            }
            if (v is IEnumerable seq)
                return seq.Cast<object>().Select(item => Jsonable(item, depth + 1)).ToList();
            return v.ToString();
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
            {
                var result = new Dictionary<string, object> { ["type"] = "point", ["point"] = Xyz(lp.Point) };
                try { result["rotation_deg"] = Math.Round(lp.Rotation * 180.0 / Math.PI, 3); }
                catch { /* rotation is undefined for some point-located elements */ }
                return result;
            }
            if (location is LocationCurve lc && lc.Curve != null)
                return new Dictionary<string, object>
                {
                    ["type"] = "curve",
                    ["start"] = Xyz(lc.Curve.GetEndPoint(0)),
                    ["end"] = Xyz(lc.Curve.GetEndPoint(1)),
                    ["length_mm"] = Math.Round(FtToMm(lc.Curve.Length), 2)
                };
            return null;
        }

        // XYZ in internal units (feet) -> {x, y, z} in millimeters for JSON output.
        private static Dictionary<string, object> Xyz(XYZ p)
            => new Dictionary<string, object>
            {
                ["x"] = Math.Round(FtToMm(p.X), 2),
                ["y"] = Math.Round(FtToMm(p.Y), 2),
                ["z"] = Math.Round(FtToMm(p.Z), 2)
            };

        private static double MmToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        private static double FtToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        // Resolve a view id, or fall back to the active view when no id is given.
        private static View GetView(UIDocument uidoc, long? viewId)
        {
            var doc = RequireDoc(uidoc);
            var view = viewId.HasValue ? doc.GetElement(new ElementId(viewId.Value)) as View : uidoc.ActiveView;
            if (view == null)
                throw new McpException(McpException.NotFound,
                    viewId.HasValue ? $"No view with id {viewId}." : "No active view.");
            return view;
        }

        private static List<ElementId> ToExistingIds(Document doc, List<long> ids)
        {
            var missing = ids.Where(i => doc.GetElement(new ElementId(i)) == null).ToList();
            if (missing.Count > 0)
                throw new McpException(McpException.NotFound,
                    $"No element(s) with id(s): {string.Join(", ", missing)}.");
            return ids.Select(i => new ElementId(i)).ToList();
        }

        private static object ParamValue(Parameter p)
        {
            if (!p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.Double:
                    // Revit stores doubles in *internal* units (feet, radians, ...).
                    // Return the value converted to the parameter's display units so
                    // the number matches what the user sees in the Revit UI, and keep
                    // the raw internal value + a unit-aware display string alongside.
                    return new Dictionary<string, object>
                    {
                        ["value"] = DisplayValue(p),
                        ["display"] = SafeValueString(p),  // e.g. "3000 mm"
                        ["internal"] = p.AsDouble()        // raw feet/radians
                    };
                case StorageType.Integer:   return p.AsInteger();
                case StorageType.String:    return p.AsString();
                case StorageType.ElementId: return p.AsElementId().Value;
                default:                    return SafeValueString(p);
            }
        }

        // Value in the parameter's display units (falls back to internal if unitless).
        private static double DisplayValue(Parameter p)
        {
            var unitId = TryGetUnitTypeId(p);
            return unitId != null
                ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unitId)
                : p.AsDouble();
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
                case StorageType.Double:
                    // Interpret the incoming number in the parameter's display units and
                    // convert to internal units before storing (inverse of DisplayValue).
                    var raw = Convert.ToDouble(value);
                    var unitId = TryGetUnitTypeId(p);
                    p.Set(unitId != null ? UnitUtils.ConvertToInternalUnits(raw, unitId) : raw);
                    break;
                case StorageType.Integer:   p.Set(Convert.ToInt32(value)); break;
                case StorageType.String:    p.Set(Convert.ToString(value)); break;
                case StorageType.ElementId: p.Set(new ElementId(Convert.ToInt64(value))); break;
                default:
                    throw new McpException(McpException.Unsupported, "Unsupported parameter storage type.");
            }
        }

        // Not every Double parameter has a unit (e.g. "Number"); those throw, so probe safely.
        private static ForgeTypeId TryGetUnitTypeId(Parameter p)
        {
            try
            {
                var unitId = p.GetUnitTypeId();
                return (unitId != null && !unitId.Empty()) ? unitId : null;
            }
            catch
            {
                return null;
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

        private static double GetDouble(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null)
                throw new McpException(McpException.BadRequest, $"Missing parameter '{key}'.");
            try { return Convert.ToDouble(p[key]); }
            catch { throw new McpException(McpException.BadRequest, $"Parameter '{key}' must be a number."); }
        }

        private static int GetIntOr(Dictionary<string, object> p, string key, int fallback)
        {
            if (!p.ContainsKey(key) || p[key] == null) return fallback;
            try { return Convert.ToInt32(p[key]); }
            catch { return fallback; }
        }

        private static long? GetOptLong(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null) return null;
            return GetLong(p, key);
        }

        private static string GetOptString(Dictionary<string, object> p, string key)
        {
            return p.ContainsKey(key) && p[key] != null ? Convert.ToString(p[key]) : null;
        }

        private static int ClampLimit(int limit)
            => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

        // [x, y] or [x, y, z] in millimeters -> XYZ in internal units (feet).
        // If z is omitted, defaultZFeet (already internal units) is used.
        private static XYZ GetPointMm(Dictionary<string, object> p, string key, double defaultZFeet)
        {
            if (!p.ContainsKey(key) || !(p[key] is IEnumerable seq) || p[key] is string)
                throw new McpException(McpException.BadRequest,
                    $"Missing list parameter '{key}' ([x, y] or [x, y, z] in mm).");
            var nums = seq.Cast<object>().Select(Convert.ToDouble).ToArray();
            if (nums.Length < 2)
                throw new McpException(McpException.BadRequest, $"Parameter '{key}' needs at least [x, y] in mm.");
            var z = nums.Length >= 3 ? MmToFt(nums[2]) : defaultZFeet;
            return new XYZ(MmToFt(nums[0]), MmToFt(nums[1]), z);
        }

        // List of [x, y] or [x, y, z] points in millimeters -> XYZ list in internal units.
        private static List<XYZ> GetPointListMm(Dictionary<string, object> p, string key, double defaultZFeet)
        {
            if (!p.ContainsKey(key) || !(p[key] is IEnumerable seq) || p[key] is string)
                throw new McpException(McpException.BadRequest,
                    $"Missing list parameter '{key}' (list of [x, y] points in mm).");
            var pts = new List<XYZ>();
            foreach (var item in seq)
            {
                if (!(item is IEnumerable pair) || item is string)
                    throw new McpException(McpException.BadRequest, $"'{key}' must be a list of [x, y] points in mm.");
                var nums = pair.Cast<object>().Select(Convert.ToDouble).ToArray();
                if (nums.Length < 2)
                    throw new McpException(McpException.BadRequest, $"Each point in '{key}' needs at least [x, y].");
                var z = nums.Length >= 3 ? MmToFt(nums[2]) : defaultZFeet;
                pts.Add(new XYZ(MmToFt(nums[0]), MmToFt(nums[1]), z));
            }
            return pts;
        }

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
