using System;
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
                case "list_levels":       return ListLevels(uidoc);
                case "list_views":        return ListViews(uidoc);
                case "list_categories":   return ListCategories(uidoc);
                case "get_elements":      return GetElements(uidoc, GetString(p, "category"), GetOptInt(p, "limit", 100));
                case "list_family_types": return ListFamilyTypes(uidoc, GetOptString(p, "category"),
                                                                  GetOptString(p, "contains"), GetOptInt(p, "limit", 200));
                case "export_view_image": return ExportViewImage(uidoc, GetOptLong(p, "view_id"), GetOptInt(p, "pixels", 1600));
                case "select_elements":   return SelectElements(uidoc, GetLongList(p, "ids"));
                case "delete_elements":   return DeleteElements(uidoc, GetLongList(p, "ids"));
                case "create_wall":       return CreateWall(uidoc, p);
                case "place_family_instance": return PlaceFamilyInstance(uidoc, p);
                case "get_project_info":  return GetProjectInfo(uidoc);
                case "get_view_elements": return GetViewElements(uidoc, GetOptLong(p, "view_id"), GetOptInt(p, "limit", 100));
                case "filter_elements":   return FilterElements(uidoc, GetOptString(p, "category"),
                                                                 GetOptString(p, "name_contains"),
                                                                 GetOptLong(p, "level_id"), GetOptInt(p, "limit", 100));
                case "get_location":      return GetLocation(uidoc, GetLong(p, "id"));
                case "move_elements":     return MoveElements(uidoc, GetLongList(p, "ids"), GetPointMm(p, "vector", 0));
                case "copy_elements":     return CopyElements(uidoc, GetLongList(p, "ids"), GetPointMm(p, "vector", 0));
                case "create_level":      return CreateLevel(uidoc, GetString(p, "name"), GetDouble(p, "elevation_mm"));
                case "create_grid":       return CreateGrid(uidoc, p);
                case "create_floor":      return CreateFloor(uidoc, p);
                case "create_room":       return CreateRoom(uidoc, p);
                case "set_active_view":   return SetActiveView(uidoc, GetLong(p, "view_id"));
                case "color_elements":    return ColorElements(uidoc, p);
                case "execute_code":      return ExecuteCode(app, GetString(p, "code"));
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

        private static Dictionary<string, object> ListLevels(UIDocument uidoc)
        {
            var doc = Doc(uidoc);
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new Dictionary<string, object>
                {
                    ["id"] = l.Id.Value,
                    ["name"] = l.Name,
                    ["elevation_mm"] = FtToMm(l.Elevation)
                })
                .ToList();
            return new Dictionary<string, object> { ["count"] = levels.Count, ["levels"] = levels };
        }

        private static Dictionary<string, object> ListViews(UIDocument uidoc)
        {
            var doc = Doc(uidoc);
            var activeId = uidoc.ActiveView?.Id ?? ElementId.InvalidElementId;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate
                            && v.ViewType != ViewType.Undefined
                            && v.ViewType != ViewType.Internal
                            && v.ViewType != ViewType.ProjectBrowser
                            && v.ViewType != ViewType.SystemBrowser)
                .Select(v => new Dictionary<string, object>
                {
                    ["id"] = v.Id.Value,
                    ["name"] = v.Name,
                    ["type"] = v.ViewType.ToString(),
                    ["isActive"] = v.Id == activeId
                })
                .ToList();
            return new Dictionary<string, object> { ["count"] = views.Count, ["views"] = views };
        }

        private static Dictionary<string, object> ListCategories(UIDocument uidoc)
        {
            var doc = Doc(uidoc);
            var counts = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null)
                .GroupBy(e => e.Category.Name)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => (object)g.Count());
            return new Dictionary<string, object> { ["count"] = counts.Count, ["categories"] = counts };
        }

        private static Dictionary<string, object> GetElements(UIDocument uidoc, string category, int limit)
        {
            var doc = Doc(uidoc);
            var all = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => string.Equals(e.Category?.Name, category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var items = all.Take(limit).Select(e =>
            {
                var s = ElementSummary(e);
                s["level"] = (doc.GetElement(e.LevelId) as Level)?.Name;
                return s;
            }).ToList();
            return new Dictionary<string, object>
            {
                ["total"] = all.Count,
                ["returned"] = items.Count,
                ["elements"] = items
            };
        }

        private static Dictionary<string, object> ListFamilyTypes(UIDocument uidoc, string category, string contains, int limit)
        {
            var doc = Doc(uidoc);
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>();
            if (!string.IsNullOrEmpty(category))
                symbols = symbols.Where(s => string.Equals(s.Category?.Name, category, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(contains))
                symbols = symbols.Where(s => (s.Family.Name + " " + s.Name).IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);

            var all = symbols
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
                ["total"] = all.Count,
                ["returned"] = Math.Min(all.Count, limit),
                ["familyTypes"] = all.Take(limit).ToList()
            };
        }

        private static Dictionary<string, object> GetProjectInfo(UIDocument uidoc)
        {
            var doc = Doc(uidoc);
            var pi = doc.ProjectInformation;
            string lengthUnit = null;
            try
            {
                var unitId = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                lengthUnit = LabelUtils.GetLabelForUnit(unitId);
            }
            catch { }
            return new Dictionary<string, object>
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["isWorkshared"] = doc.IsWorkshared,
                ["projectName"] = pi?.Name,
                ["projectNumber"] = pi?.Number,
                ["client"] = pi?.ClientName,
                ["address"] = pi?.Address,
                ["building"] = pi?.BuildingName,
                ["status"] = pi?.Status,
                ["lengthUnit"] = lengthUnit
            };
        }

        private static Dictionary<string, object> GetViewElements(UIDocument uidoc, long? viewId, int limit)
        {
            var doc = Doc(uidoc);
            var view = GetView(uidoc, viewId);
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
                ["returned"] = items.Count,
                ["elements"] = items
            };
        }

        private static Dictionary<string, object> FilterElements(UIDocument uidoc, string category,
                                                                 string nameContains, long? levelId, int limit)
        {
            var doc = Doc(uidoc);
            if (category == null && nameContains == null && !levelId.HasValue)
                throw new ArgumentException("Provide at least one filter: 'category', 'name_contains' or 'level_id'.");

            IEnumerable<Element> query = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null);
            if (!string.IsNullOrEmpty(category))
                query = query.Where(e => string.Equals(e.Category.Name, category, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(nameContains))
                query = query.Where(e => (e.Name ?? "").IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
            if (levelId.HasValue)
                query = query.Where(e => e.LevelId.Value == levelId.Value);

            var all = query.ToList();
            var items = all.Take(limit).Select(e =>
            {
                var s = ElementSummary(e);
                s["level"] = (doc.GetElement(e.LevelId) as Level)?.Name;
                return s;
            }).ToList();
            return new Dictionary<string, object>
            {
                ["total"] = all.Count,
                ["returned"] = items.Count,
                ["elements"] = items
            };
        }

        private static Dictionary<string, object> GetLocation(UIDocument uidoc, long id)
        {
            var doc = Doc(uidoc);
            var el = doc.GetElement(new ElementId(id));
            if (el == null) throw new ArgumentException($"No element with id {id}.");

            var result = ElementSummary(el);
            if (el.Location is LocationPoint lp)
            {
                result["point_mm"] = PtMm(lp.Point);
                try { result["rotation_deg"] = Math.Round(lp.Rotation * 180.0 / Math.PI, 3); }
                catch { } // rotation is undefined for some point-located elements
            }
            else if (el.Location is LocationCurve lc)
            {
                result["start_mm"] = PtMm(lc.Curve.GetEndPoint(0));
                result["end_mm"] = PtMm(lc.Curve.GetEndPoint(1));
                result["length_mm"] = Math.Round(FtToMm(lc.Curve.Length), 2);
            }
            var bb = el.get_BoundingBox(null);
            if (bb != null)
            {
                result["bbox_min_mm"] = PtMm(bb.Min);
                result["bbox_max_mm"] = PtMm(bb.Max);
            }
            return result;
        }

        private static Dictionary<string, object> ExportViewImage(UIDocument uidoc, long? viewId, int pixels)
        {
            var doc = Doc(uidoc);
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
                throw new InvalidOperationException($"Export of view '{view.Name}' produced no image (not a graphical view?).");
            return new Dictionary<string, object> { ["view"] = view.Name, ["path"] = file };
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

        private static Dictionary<string, object> SelectElements(UIDocument uidoc, List<long> ids)
        {
            var doc = Doc(uidoc);
            var valid = ids.Select(i => new ElementId(i)).Where(id => doc.GetElement(id) != null).ToList();
            uidoc.Selection.SetElementIds(valid);
            return new Dictionary<string, object> { ["requested"] = ids.Count, ["selected"] = valid.Count };
        }

        private static Dictionary<string, object> DeleteElements(UIDocument uidoc, List<long> ids)
        {
            var doc = Doc(uidoc);
            ICollection<ElementId> deleted;
            using (var t = new Transaction(doc, "MCP: delete elements"))
            {
                t.Start();
                deleted = doc.Delete(ids.Select(i => new ElementId(i)).ToList());
                t.Commit();
            }
            // doc.Delete also removes dependents (tags, joins, ...), so deleted >= requested.
            return new Dictionary<string, object> { ["requested"] = ids.Count, ["deleted"] = deleted.Count };
        }

        private static Dictionary<string, object> CreateWall(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = Doc(uidoc);
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level;
            if (level == null) throw new ArgumentException("'level_id' is not a Level.");

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

        private static Dictionary<string, object> PlaceFamilyInstance(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = Doc(uidoc);
            var symbol = doc.GetElement(new ElementId(GetLong(p, "type_id"))) as FamilySymbol;
            if (symbol == null) throw new ArgumentException("'type_id' is not a family type (use list_family_types).");

            var point = GetPointMm(p, "point", 0);
            var optLevel = GetOptLong(p, "level_id");
            var level = optLevel.HasValue ? doc.GetElement(new ElementId(optLevel.Value)) as Level : null;
            if (optLevel.HasValue && level == null) throw new ArgumentException("'level_id' is not a Level.");

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

        private static Dictionary<string, object> MoveElements(UIDocument uidoc, List<long> ids, XYZ vector)
        {
            var doc = Doc(uidoc);
            var elementIds = ToExistingIds(doc, ids);
            using (var t = new Transaction(doc, "MCP: move elements"))
            {
                t.Start();
                ElementTransformUtils.MoveElements(doc, elementIds, vector);
                t.Commit();
            }
            return new Dictionary<string, object> { ["moved"] = elementIds.Count };
        }

        private static Dictionary<string, object> CopyElements(UIDocument uidoc, List<long> ids, XYZ vector)
        {
            var doc = Doc(uidoc);
            var elementIds = ToExistingIds(doc, ids);
            ICollection<ElementId> copies;
            using (var t = new Transaction(doc, "MCP: copy elements"))
            {
                t.Start();
                copies = ElementTransformUtils.CopyElements(doc, elementIds, vector);
                t.Commit();
            }
            return new Dictionary<string, object>
            {
                ["copied"] = copies.Count,
                ["newIds"] = copies.Select(id => id.Value).ToList()
            };
        }

        private static Dictionary<string, object> CreateLevel(UIDocument uidoc, string name, double elevationMm)
        {
            var doc = Doc(uidoc);
            Level level;
            using (var t = new Transaction(doc, "MCP: create level"))
            {
                t.Start();
                level = Level.Create(doc, MmToFt(elevationMm));
                level.Name = name; // throws if the name is already taken
                t.Commit();
            }
            var result = ElementSummary(level);
            result["elevation_mm"] = FtToMm(level.Elevation);
            return result;
        }

        private static Dictionary<string, object> CreateGrid(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = Doc(uidoc);
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

        private static Dictionary<string, object> CreateFloor(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = Doc(uidoc);
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level;
            if (level == null) throw new ArgumentException("'level_id' is not a Level.");

            var pts = GetPointListMm(p, "boundary", level.Elevation);
            // Callers may repeat the first point to close the loop; we close it ourselves.
            if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3)
                throw new ArgumentException("'boundary' needs at least 3 distinct [x, y] points in mm.");

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

        private static Dictionary<string, object> CreateRoom(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = Doc(uidoc);
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level;
            if (level == null) throw new ArgumentException("'level_id' is not a Level.");
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

        private static Dictionary<string, object> SetActiveView(UIDocument uidoc, long viewId)
        {
            var view = GetView(uidoc, viewId);
            if (view.IsTemplate) throw new ArgumentException($"View '{view.Name}' is a template and cannot be opened.");
            // Direct assignment of ActiveView is not allowed from an API event handler;
            // RequestViewChange applies as soon as Revit regains control (right after this reply).
            uidoc.RequestViewChange(view);
            return new Dictionary<string, object> { ["id"] = view.Id.Value, ["view"] = view.Name, ["status"] = "requested" };
        }

        private static Dictionary<string, object> ColorElements(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = Doc(uidoc);
            var view = GetView(uidoc, GetOptLong(p, "view_id"));
            var ids = GetLongList(p, "ids");
            var clear = p.ContainsKey("clear") && p["clear"] != null && Convert.ToBoolean(p["clear"]);

            var ogs = new OverrideGraphicSettings(); // empty settings = reset to defaults
            if (!clear)
            {
                var rgb = GetLongList(p, "rgb");
                if (rgb.Count != 3) throw new ArgumentException("'rgb' must be [r, g, b] (0-255 each).");
                var color = new Color((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);
                var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                ogs.SetProjectionLineColor(color);
                if (solid != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solid.Id);
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetCutForegroundPatternId(solid.Id);
                    ogs.SetCutForegroundPatternColor(color);
                }
            }

            var elementIds = ToExistingIds(doc, ids);
            using (var t = new Transaction(doc, clear ? "MCP: clear element colors" : "MCP: color elements"))
            {
                t.Start();
                foreach (var id in elementIds)
                    view.SetElementOverrides(id, ogs);
                t.Commit();
            }
            return new Dictionary<string, object>
            {
                ["view"] = view.Name,
                [clear ? "cleared" : "colored"] = elementIds.Count
            };
        }

        // ---- execute_code (dual-use: only exposed when the MCP server sets ---
        // ---- REVIT_MCP_ENABLE_CODE — see the README's warning section) -------

        private static Dictionary<string, object> ExecuteCode(UIApplication app, string code)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = Doc(uidoc);

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
                throw new ArgumentException("C# compilation failed:\n" + string.Join("\n", errors));
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
            if (v is XYZ xyz) return PtMm(xyz);
            if (v is System.Collections.IDictionary dict)
            {
                var result = new Dictionary<string, object>();
                foreach (System.Collections.DictionaryEntry entry in dict)
                    result[Convert.ToString(entry.Key)] = Jsonable(entry.Value, depth + 1);
                return result;
            }
            if (v is System.Collections.IEnumerable seq)
                return seq.Cast<object>().Select(item => Jsonable(item, depth + 1)).ToList();
            return v.ToString();
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
                case StorageType.Double:
                    // Revit stores doubles in *internal* units (feet, radians, ...).
                    // Return the value converted to the parameter's display units so
                    // the number matches what the user sees in the Revit UI, and keep
                    // the raw internal value + a unit-aware display string alongside.
                    return new Dictionary<string, object>
                    {
                        ["value"] = DisplayValue(p),
                        ["display"] = p.AsValueString(),   // e.g. "3000 mm"
                        ["internal"] = p.AsDouble()        // raw feet/radians
                    };
                case StorageType.Integer:   return p.AsInteger();
                case StorageType.String:    return p.AsString();
                case StorageType.ElementId: return p.AsElementId().Value;
                default:                    return p.AsValueString();
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
                default: throw new InvalidOperationException("Unsupported parameter storage type.");
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

        private static Document Doc(UIDocument uidoc)
        {
            if (uidoc == null) throw new InvalidOperationException("No active document.");
            return uidoc.Document;
        }

        private static double MmToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        private static double FtToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        // XYZ in internal units (feet) -> [x, y, z] in millimeters for JSON output.
        private static List<double> PtMm(XYZ pt) => new List<double>
        {
            Math.Round(FtToMm(pt.X), 2), Math.Round(FtToMm(pt.Y), 2), Math.Round(FtToMm(pt.Z), 2)
        };

        // Resolve a view id, or fall back to the active view when no id is given.
        private static View GetView(UIDocument uidoc, long? viewId)
        {
            var doc = Doc(uidoc);
            var view = viewId.HasValue ? doc.GetElement(new ElementId(viewId.Value)) as View : uidoc.ActiveView;
            if (view == null)
                throw new ArgumentException(viewId.HasValue ? $"No view with id {viewId}." : "No active view.");
            return view;
        }

        private static List<ElementId> ToExistingIds(Document doc, List<long> ids)
        {
            var missing = ids.Where(i => doc.GetElement(new ElementId(i)) == null).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"No element(s) with id(s): {string.Join(", ", missing)}.");
            return ids.Select(i => new ElementId(i)).ToList();
        }

        // [x, y] or [x, y, z] in millimeters -> XYZ in internal units (feet).
        // If z is omitted, defaultZFeet (already internal units) is used.
        private static XYZ GetPointMm(Dictionary<string, object> p, string key, double defaultZFeet)
        {
            if (!p.ContainsKey(key) || !(p[key] is System.Collections.IEnumerable seq) || p[key] is string)
                throw new ArgumentException($"Missing list parameter '{key}' ([x, y] or [x, y, z] in mm).");
            var nums = seq.Cast<object>().Select(Convert.ToDouble).ToArray();
            if (nums.Length < 2)
                throw new ArgumentException($"Parameter '{key}' needs at least [x, y] in mm.");
            var z = nums.Length >= 3 ? MmToFt(nums[2]) : defaultZFeet;
            return new XYZ(MmToFt(nums[0]), MmToFt(nums[1]), z);
        }

        // List of [x, y] or [x, y, z] points in millimeters -> XYZ list in internal units.
        private static List<XYZ> GetPointListMm(Dictionary<string, object> p, string key, double defaultZFeet)
        {
            if (!p.ContainsKey(key) || !(p[key] is System.Collections.IEnumerable seq) || p[key] is string)
                throw new ArgumentException($"Missing list parameter '{key}' (list of [x, y] points in mm).");
            var pts = new List<XYZ>();
            foreach (var item in seq)
            {
                if (!(item is System.Collections.IEnumerable pair) || item is string)
                    throw new ArgumentException($"'{key}' must be a list of [x, y] points in mm.");
                var nums = pair.Cast<object>().Select(Convert.ToDouble).ToArray();
                if (nums.Length < 2)
                    throw new ArgumentException($"Each point in '{key}' needs at least [x, y].");
                var z = nums.Length >= 3 ? MmToFt(nums[2]) : defaultZFeet;
                pts.Add(new XYZ(MmToFt(nums[0]), MmToFt(nums[1]), z));
            }
            return pts;
        }

        private static List<long> GetLongList(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || !(p[key] is System.Collections.IEnumerable seq) || p[key] is string)
                throw new ArgumentException($"Missing list parameter '{key}'.");
            var ids = seq.Cast<object>().Select(Convert.ToInt64).ToList();
            if (ids.Count == 0) throw new ArgumentException($"Parameter '{key}' must not be empty.");
            return ids;
        }

        private static int GetOptInt(Dictionary<string, object> p, string key, int fallback)
        {
            return p.ContainsKey(key) && p[key] != null ? Convert.ToInt32(p[key]) : fallback;
        }

        private static long? GetOptLong(Dictionary<string, object> p, string key)
        {
            return p.ContainsKey(key) && p[key] != null ? Convert.ToInt64(p[key]) : (long?)null;
        }

        private static string GetOptString(Dictionary<string, object> p, string key)
        {
            return p.ContainsKey(key) && p[key] != null ? Convert.ToString(p[key]) : null;
        }

        private static long GetLong(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null) throw new ArgumentException($"Missing parameter '{key}'.");
            return Convert.ToInt64(p[key]);
        }

        private static double GetDouble(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null) throw new ArgumentException($"Missing parameter '{key}'.");
            return Convert.ToDouble(p[key]);
        }

        private static string GetString(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key) || p[key] == null) throw new ArgumentException($"Missing parameter '{key}'.");
            return Convert.ToString(p[key]);
        }
    }
}
