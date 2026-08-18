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
    /// Project set-up and benchmarking commands used by the CAD-to-BIM workflow:
    /// open a document, create floor-plan views, link DWGs onto them, export
    /// views to DWG, and dump the model's architectural elements to JSON so two
    /// models (ground truth vs. rebuilt) can be compared offline.
    /// </summary>
    public static partial class CommandRouter
    {
        // ==================== open_document ==================================

        /// <summary>Active document, or - when `title` is given - any open (non-link)
        /// document whose title or path matches it.</summary>
        private static Document ResolveDocument(UIApplication app, string title)
        {
            if (string.IsNullOrEmpty(title)) return RequireDoc(app.ActiveUIDocument);
            foreach (Document d in app.Application.Documents)
                if (!d.IsLinked && (string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(d.PathName, title, StringComparison.OrdinalIgnoreCase)))
                    return d;
            throw new McpException(McpException.NotFound, $"No open document titled '{title}'.");
        }

        private static Dictionary<string, object> OpenDocument(UIApplication app, Dictionary<string, object> p)
        {
            var path = GetString(p, "path");
            if (!File.Exists(path))
                throw new McpException(McpException.NotFound, $"File not found: {path}");
            var uidoc = app.OpenAndActivateDocument(path);
            var doc = uidoc.Document;
            return new Dictionary<string, object>
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["activeView"] = uidoc.ActiveView?.Name
            };
        }

        // ==================== create_floor_plan_view =========================

        private static Dictionary<string, object> CreateFloorPlanView(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "level_id is not a Level.");
            var name = GetOptString(p, "name");
            var ceiling = GetBoolOr(p, "ceiling", false);
            var activate = GetBoolOr(p, "activate", false);
            var scale = GetIntOr(p, "scale", 0);
            var detail = GetOptString(p, "detail_level"); // coarse|medium|fine

            var wantedFamily = ceiling ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == wantedFamily)
                ?? throw new McpException(McpException.NotFound, $"No {wantedFamily} view family type in this model.");

            ViewPlan view;
            var ownTx = !doc.IsModifiable;
            var t = ownTx ? new Transaction(doc, "MCP: create plan view") : null;
            try
            {
                if (ownTx) t.Start();
                view = ViewPlan.Create(doc, vft.Id, level.Id);
                if (!string.IsNullOrWhiteSpace(name)) view.Name = name;
                if (scale > 0) view.Scale = scale;
                if (!string.IsNullOrEmpty(detail))
                {
                    switch (detail.Trim().ToLowerInvariant())
                    {
                        case "coarse": view.DetailLevel = ViewDetailLevel.Coarse; break;
                        case "medium": view.DetailLevel = ViewDetailLevel.Medium; break;
                        case "fine": view.DetailLevel = ViewDetailLevel.Fine; break;
                    }
                }
                if (ownTx) t.Commit();
            }
            finally { t?.Dispose(); }

            if (activate && !doc.IsModifiable) uidoc.ActiveView = view;
            return new Dictionary<string, object>
            {
                ["id"] = view.Id.Value,
                ["name"] = view.Name,
                ["viewType"] = view.ViewType.ToString(),
                ["level_id"] = level.Id.Value,
                ["level"] = level.Name
            };
        }

        // ==================== link_cad =======================================

        private static Dictionary<string, object> LinkCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            EnsureWritable();
            var doc = RequireDoc(uidoc);
            var path = GetString(p, "path");
            if (!File.Exists(path))
                throw new McpException(McpException.NotFound, $"File not found: {path}");

            View view;
            var optView = GetOptLong(p, "view_id");
            if (optView.HasValue)
                view = doc.GetElement(new ElementId(optView.Value)) as View
                       ?? throw new McpException(McpException.NotFound, "view_id is not a view.");
            else
                view = uidoc.ActiveView ?? throw new McpException(McpException.NotFound, "No active view.");
            if (view.ViewType != ViewType.FloorPlan && view.ViewType != ViewType.CeilingPlan &&
                view.ViewType != ViewType.EngineeringPlan && view.ViewType != ViewType.AreaPlan &&
                view.ViewType != ViewType.Detail && view.ViewType != ViewType.DraftingView &&
                view.ViewType != ViewType.Section && view.ViewType != ViewType.Elevation &&
                view.ViewType != ViewType.ThreeD)
                throw new McpException(McpException.BadRequest, $"Cannot link a DWG into a {view.ViewType} view.");

            var thisViewOnly = GetBoolOr(p, "this_view_only", true);
            var placement = (GetOptString(p, "placement") ?? "origin").Trim().ToLowerInvariant();
            var unit = (GetOptString(p, "unit") ?? "mm").Trim().ToLowerInvariant();
            var colors = (GetOptString(p, "colors") ?? "preserve").Trim().ToLowerInvariant();
            var link = GetBoolOr(p, "link", true);
            var pin = GetBoolOr(p, "pin", true);

            var opt = new DWGImportOptions
            {
                ThisViewOnly = thisViewOnly,
                OrientToView = GetBoolOr(p, "orient_to_view", false),
                VisibleLayersOnly = GetBoolOr(p, "visible_layers_only", false)
            };
            switch (placement)
            {
                case "shared": opt.Placement = ImportPlacement.Shared; break;
                case "center": case "centre": opt.Placement = ImportPlacement.Centered; break;
                case "site": opt.Placement = ImportPlacement.Site; break;
                default: opt.Placement = ImportPlacement.Origin; break;
            }
            switch (unit)
            {
                case "auto": case "default": opt.Unit = ImportUnit.Default; break;
                case "m": case "meter": case "metre": opt.Unit = ImportUnit.Meter; break;
                case "cm": opt.Unit = ImportUnit.Centimeter; break;
                case "ft": case "foot": case "feet": opt.Unit = ImportUnit.Foot; break;
                case "in": case "inch": opt.Unit = ImportUnit.Inch; break;
                default: opt.Unit = ImportUnit.Millimeter; break;
            }
            switch (colors)
            {
                case "bw": case "blackwhite": case "black_and_white": opt.ColorMode = ImportColorMode.BlackAndWhite; break;
                case "invert": case "inverted": opt.ColorMode = ImportColorMode.Inverted; break;
                default: opt.ColorMode = ImportColorMode.Preserved; break;
            }

            ElementId id = ElementId.InvalidElementId;
            var ownTx = !doc.IsModifiable;
            var t = ownTx ? new Transaction(doc, "MCP: link CAD") : null;
            try
            {
                if (ownTx)
                {
                    t.Start();
                    var fh = t.GetFailureHandlingOptions();
                    fh.SetFailuresPreprocessor(new SilentFailures());
                    t.SetFailureHandlingOptions(fh);
                }
                bool ok = link ? doc.Link(path, opt, view, out id) : doc.Import(path, opt, view, out id);
                if (!ok || id == ElementId.InvalidElementId)
                    throw new McpException(McpException.Internal, "Revit refused to link/import the file (unsupported format or empty drawing?).");
                if (pin && doc.GetElement(id) is Element e) e.Pinned = true;
                if (ownTx) t.Commit();
            }
            finally { t?.Dispose(); }

            var li = doc.GetElement(id) as ImportInstance;
            var result = new Dictionary<string, object>
            {
                ["id"] = id.Value,
                ["name"] = li != null ? ImportName(doc, li) : Path.GetFileName(path),
                ["view_id"] = view.Id.Value,
                ["view"] = view.Name,
                ["viewSpecific"] = li?.ViewSpecific ?? thisViewOnly,
                ["placement"] = placement,
                ["unit"] = unit
            };
            try
            {
                var bb = li?.get_BoundingBox(li.ViewSpecific ? view : null);
                if (bb != null)
                    result["bbox_mm"] = new[]
                    {
                        Math.Round(FtToMm(bb.Min.X)), Math.Round(FtToMm(bb.Min.Y)),
                        Math.Round(FtToMm(bb.Max.X)), Math.Round(FtToMm(bb.Max.Y))
                    };
            }
            catch { /* no bbox */ }
            return result;
        }

        // ==================== export_dwg =====================================

        private static Dictionary<string, object> ExportDwg(UIApplication app, Dictionary<string, object> p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = ResolveDocument(app, GetOptString(p, "document"));
            var folder = GetString(p, "folder");
            Directory.CreateDirectory(folder);
            var prefix = GetOptString(p, "prefix") ?? "";
            var sharedCoords = GetBoolOr(p, "shared_coords", false);
            var mergedViews = GetBoolOr(p, "merged_views", true);

            var ids = new List<ElementId>();
            if (p.ContainsKey("view_ids") && p["view_ids"] is IEnumerable seq && !(p["view_ids"] is string))
                foreach (var o in seq) ids.Add(new ElementId(Convert.ToInt64(o)));
            if (p.ContainsKey("view_names") && p["view_names"] is IEnumerable nseq && !(p["view_names"] is string))
            {
                var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
                foreach (var o in nseq)
                {
                    var name = Convert.ToString(o);
                    var v = all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new McpException(McpException.NotFound, $"View '{name}' not found.");
                    ids.Add(v.Id);
                }
            }
            if (ids.Count == 0)
            {
                if (uidoc == null || uidoc.Document.Title != doc.Title)
                    throw new McpException(McpException.BadRequest, "Give view_ids or view_names when exporting a non-active document.");
                var v = uidoc.ActiveView ?? throw new McpException(McpException.NotFound, "No active view.");
                ids.Add(v.Id);
            }

            var opt = new DWGExportOptions
            {
                SharedCoords = sharedCoords,
                MergedViews = mergedViews,
                FileVersion = ACADVersion.R2018,
                ExportOfSolids = SolidGeometry.Polymesh,
                TargetUnit = ExportUnit.Millimeter
            };
            var setup = GetOptString(p, "export_setup");
            if (!string.IsNullOrEmpty(setup))
            {
                var predefined = BaseExportOptions.GetPredefinedSetupNames(doc);
                if (predefined.Contains(setup))
                    opt = DWGExportOptions.GetPredefinedOptions(doc, setup) ?? opt;
                else
                    throw new McpException(McpException.NotFound,
                        $"Export setup '{setup}' not found. Available: {string.Join(", ", predefined)}");
            }

            var files = new List<Dictionary<string, object>>();
            foreach (var vid in ids)
            {
                var v = doc.GetElement(vid) as View
                        ?? throw new McpException(McpException.NotFound, $"view {vid.Value} not found.");
                var safe = string.Concat((prefix + v.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                bool ok = doc.Export(folder, safe, new List<ElementId> { vid }, opt);
                files.Add(new Dictionary<string, object>
                {
                    ["view_id"] = vid.Value,
                    ["view"] = v.Name,
                    ["ok"] = ok,
                    ["file"] = Path.Combine(folder, safe + ".dwg")
                });
            }
            return new Dictionary<string, object> { ["count"] = files.Count, ["files"] = files };
        }

        // ==================== dump_model =====================================

        private static readonly string[] DumpCategories = { "walls", "doors", "windows", "columns", "stairs", "floors", "grids" };

        private static Dictionary<string, object> DumpModel(UIApplication app, Dictionary<string, object> p)
        {
            // Optional `document`: title (or path) of any open document - lets a
            // ground-truth model be dumped without activating it.
            var hostDoc = ResolveDocument(app, GetOptString(p, "document"));
            var levelId = GetOptLong(p, "level_id");
            var levelName = GetOptString(p, "level_name");
            var path = GetOptString(p, "path");
            var includeLinks = GetBoolOr(p, "include_links", true);
            var wanted = new HashSet<string>(DumpCategories);
            if (p.ContainsKey("categories") && p["categories"] is IEnumerable seq && !(p["categories"] is string))
            {
                wanted = new HashSet<string>();
                foreach (var o in seq) wanted.Add(Convert.ToString(o).Trim().ToLowerInvariant());
            }
            var bbox = GetOptBboxMm(p);
            if (levelId.HasValue && levelName == null)
                levelName = (hostDoc.GetElement(new ElementId(levelId.Value)) as Level)?.Name
                            ?? throw new McpException(McpException.NotFound, "level_id is not a Level.");

            var result = new Dictionary<string, object> { ["document"] = hostDoc.Title };
            var lists = new Dictionary<string, List<Dictionary<string, object>>>();
            foreach (var k in DumpCategories) if (wanted.Contains(k)) lists[k] = new List<Dictionary<string, object>>();
            var levelsOut = new List<Dictionary<string, object>>();
            var sources = new List<Dictionary<string, object>>();

            // ---- host document, then every loaded Revit link (transformed into host coords)
            DumpDoc(hostDoc, Transform.Identity, "host", levelName, bbox, lists, levelsOut, sources);
            if (includeLinks)
            {
                foreach (var rli in new FilteredElementCollector(hostDoc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document ld = null;
                    try { ld = rli.GetLinkDocument(); } catch { }
                    if (ld == null) continue;
                    DumpDoc(ld, rli.GetTotalTransform(), rli.Name, levelName, bbox, lists, null, sources);
                }
            }

            result["levels"] = levelsOut;
            result["sources"] = sources;
            var counts = new Dictionary<string, object>();
            foreach (var kv in lists) { result[kv.Key] = kv.Value; counts[kv.Key] = kv.Value.Count; }
            result["counts"] = counts;

            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, Json.Serialize(result), new System.Text.UTF8Encoding(false));
                return new Dictionary<string, object>
                {
                    ["document"] = hostDoc.Title,
                    ["path"] = Path.GetFullPath(path),
                    ["counts"] = counts,
                    ["levels"] = levelsOut,
                    ["sources"] = sources
                };
            }
            return result;
        }

        private static void DumpDoc(Document doc, Transform xf, string source, string levelName, double[] bbox,
            Dictionary<string, List<Dictionary<string, object>>> lists,
            List<Dictionary<string, object>> levelsOut, List<Dictionary<string, object>> sources)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            var levelById = levels.ToDictionary(l => l.Id, l => l);
            if (levelsOut != null)
                foreach (var l in levels)
                    levelsOut.Add(new Dictionary<string, object>
                    {
                        ["id"] = l.Id.Value, ["name"] = l.Name, ["elevation_mm"] = Math.Round(FtToMm(l.Elevation), 1)
                    });

            string LevelName(ElementId id) => id != null && levelById.TryGetValue(id, out var l) ? l.Name : null;
            string ParamLevel(Element e, BuiltInParameter bp) => LevelName(e.get_Parameter(bp)?.AsElementId());
            string ElemLevel(Element e)
            {
                var n = LevelName(e.LevelId);
                if (n != null) return n;
                return ParamLevel(e, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)
                       ?? ParamLevel(e, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)
                       ?? ParamLevel(e, BuiltInParameter.LEVEL_PARAM)
                       ?? ParamLevel(e, BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            }
            bool LevelOk(Element e) => levelName == null || string.Equals(ElemLevel(e), levelName, StringComparison.OrdinalIgnoreCase);
            BoundingBoxXYZ Bb(Element e)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { }
                if (bb == null || xf.IsIdentity) return bb;
                var pts = new[]
                {
                    xf.OfPoint(bb.Min), xf.OfPoint(bb.Max),
                    xf.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)), xf.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z)),
                    xf.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z)), xf.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z)),
                    xf.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z)), xf.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z))
                };
                return new BoundingBoxXYZ
                {
                    Min = new XYZ(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z)),
                    Max = new XYZ(pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z))
                };
            }
            bool BboxOk(Element e)
            {
                if (bbox == null) return true;
                var bb = Bb(e);
                if (bb == null) return true;
                return FtToMm(bb.Max.X) >= bbox[0] && FtToMm(bb.Min.X) <= bbox[2] &&
                       FtToMm(bb.Max.Y) >= bbox[1] && FtToMm(bb.Min.Y) <= bbox[3];
            }
            IEnumerable<Element> Collect(BuiltInCategory bic)
                => new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                    .Where(e => LevelOk(e) && BboxOk(e));
            double? ParamMm(Element e, BuiltInParameter bp)
            {
                var pr = e.get_Parameter(bp);
                return pr != null && pr.HasValue && pr.StorageType == StorageType.Double ? Math.Round(FtToMm(pr.AsDouble()), 1) : (double?)null;
            }
            double? TypeOrInstMm(FamilyInstance fi, BuiltInParameter bp)
                => ParamMm(fi, bp) ?? (fi.Symbol != null ? ParamMm(fi.Symbol, bp) : (double?)null);
            double? NamedMm(Element e, params string[] names)
            {
                if (e == null) return null;
                foreach (var n in names)
                {
                    var pr = e.LookupParameter(n);
                    if (pr != null && pr.HasValue && pr.StorageType == StorageType.Double)
                        return Math.Round(FtToMm(pr.AsDouble()), 1);
                }
                return null;
            }
            double[] P3(XYZ q) { q = xf.OfPoint(q); return new[] { Math.Round(FtToMm(q.X), 1), Math.Round(FtToMm(q.Y), 1), Math.Round(FtToMm(q.Z), 1) }; }
            double[] P2(XYZ q) { q = xf.OfPoint(q); return new[] { Math.Round(FtToMm(q.X), 1), Math.Round(FtToMm(q.Y), 1) }; }
            double[] Dir2(XYZ v) { v = xf.OfVector(v); return new[] { Math.Round(v.X, 4), Math.Round(v.Y, 4) }; }
            Dictionary<string, object> CurveMm(Curve c)
            {
                if (c is Line ln)
                    return new Dictionary<string, object> { ["kind"] = "line", ["start"] = P2(ln.GetEndPoint(0)), ["end"] = P2(ln.GetEndPoint(1)) };
                if (c is Arc arc)
                    return new Dictionary<string, object>
                    {
                        ["kind"] = "arc",
                        ["center"] = P2(arc.Center),
                        ["radius_mm"] = Math.Round(FtToMm(arc.Radius), 1),
                        ["start"] = P2(arc.GetEndPoint(0)),
                        ["end"] = P2(arc.GetEndPoint(1)),
                        ["mid"] = P2(arc.Evaluate(0.5, true))
                    };
                return new Dictionary<string, object>
                {
                    ["kind"] = c.GetType().Name.ToLowerInvariant(),
                    ["start"] = P2(c.GetEndPoint(0)),
                    ["end"] = P2(c.GetEndPoint(1)),
                    ["mid"] = P2(c.Evaluate(0.5, true))
                };
            }
            double[] Bbox2(Element e)
            {
                var bb = Bb(e);
                if (bb == null) return null;
                return new[]
                {
                    Math.Round(FtToMm(bb.Min.X)), Math.Round(FtToMm(bb.Min.Y)),
                    Math.Round(FtToMm(bb.Max.X)), Math.Round(FtToMm(bb.Max.Y)),
                    Math.Round(FtToMm(bb.Min.Z)), Math.Round(FtToMm(bb.Max.Z))
                };
            }
            var srcCounts = new Dictionary<string, object>();

            if (lists.TryGetValue("walls", out var walls))
            {
                int n = 0;
                foreach (var w in Collect(BuiltInCategory.OST_Walls).OfType<Wall>())
                {
                    var lc = w.Location as LocationCurve;
                    var d = new Dictionary<string, object>
                    {
                        ["id"] = w.Id.Value,
                        ["source"] = source,
                        ["type"] = w.WallType?.Name,
                        ["kind"] = w.WallType?.Kind.ToString(),
                        ["curtain"] = w.WallType != null && w.WallType.Kind == WallKind.Curtain,
                        ["level"] = LevelName(w.LevelId),
                        ["thickness_mm"] = w.WallType != null && w.WallType.Kind != WallKind.Curtain ? Math.Round(FtToMm(w.WallType.Width), 1) : (double?)null,
                        ["height_mm"] = ParamMm(w, BuiltInParameter.WALL_USER_HEIGHT_PARAM),
                        ["base_offset_mm"] = ParamMm(w, BuiltInParameter.WALL_BASE_OFFSET),
                        ["top_level"] = ParamLevel(w, BuiltInParameter.WALL_HEIGHT_TYPE),
                        ["length_mm"] = lc != null ? Math.Round(FtToMm(lc.Curve.Length), 1) : (double?)null,
                        ["structural"] = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1,
                        ["stacked_parent"] = w.IsStackedWallMember ? w.StackedWallOwnerId.Value : (long?)null,
                        ["bbox"] = Bbox2(w)
                    };
                    if (lc != null) d["curve"] = CurveMm(lc.Curve);
                    walls.Add(d); n++;
                }
                srcCounts["walls"] = n;
            }

            foreach (var cat in new[] { ("doors", BuiltInCategory.OST_Doors), ("windows", BuiltInCategory.OST_Windows) })
            {
                if (!lists.TryGetValue(cat.Item1, out var list)) continue;
                int n = 0;
                var isDoor = cat.Item2 == BuiltInCategory.OST_Doors;
                foreach (var fi in Collect(cat.Item2).OfType<FamilyInstance>())
                {
                    var lp = fi.Location as LocationPoint;
                    var host = fi.Host;
                    var d = new Dictionary<string, object>
                    {
                        ["id"] = fi.Id.Value,
                        ["source"] = source,
                        ["family"] = fi.Symbol?.Family?.Name,
                        ["type"] = fi.Symbol?.Name,
                        ["level"] = ElemLevel(fi),
                        ["host_id"] = host?.Id.Value,
                        ["host_class"] = host?.GetType().Name,
                        ["in_curtain_wall"] = host is Wall hw && hw.WallType?.Kind == WallKind.Curtain,
                        ["width_mm"] = TypeOrInstMm(fi, isDoor ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH)
                                       ?? TypeOrInstMm(fi, BuiltInParameter.FAMILY_WIDTH_PARAM)
                                       ?? NamedMm(fi.Symbol, "Width", "Rough Width", "宽度"),
                        ["height_mm"] = TypeOrInstMm(fi, isDoor ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT)
                                        ?? TypeOrInstMm(fi, BuiltInParameter.FAMILY_HEIGHT_PARAM)
                                        ?? NamedMm(fi.Symbol, "Height", "Rough Height", "高度"),
                        ["sill_mm"] = ParamMm(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM),
                        ["mirrored"] = fi.Mirrored,
                        ["hand_flipped"] = fi.HandFlipped,
                        ["facing_flipped"] = fi.FacingFlipped,
                        ["bbox"] = Bbox2(fi)
                    };
                    if (lp != null) d["location"] = P3(lp.Point);
                    else if (Bb(fi) is BoundingBoxXYZ bb)
                        d["location"] = new[] { Math.Round(FtToMm((bb.Min.X + bb.Max.X) / 2), 1), Math.Round(FtToMm((bb.Min.Y + bb.Max.Y) / 2), 1), Math.Round(FtToMm(bb.Min.Z), 1) };
                    try { d["facing"] = Dir2(fi.FacingOrientation); d["hand"] = Dir2(fi.HandOrientation); } catch { }
                    list.Add(d); n++;
                }
                srcCounts[cat.Item1] = n;
            }

            if (lists.TryGetValue("columns", out var cols))
            {
                int n = 0;
                foreach (var cat in new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns })
                {
                    foreach (var fi in Collect(cat).OfType<FamilyInstance>())
                    {
                        var lp = fi.Location as LocationPoint;
                        var sym = fi.Symbol;
                        var d = new Dictionary<string, object>
                        {
                            ["id"] = fi.Id.Value,
                            ["source"] = source,
                            ["structural"] = cat == BuiltInCategory.OST_StructuralColumns,
                            ["family"] = sym?.Family?.Name,
                            ["type"] = sym?.Name,
                            ["base_level"] = ParamLevel(fi, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM) ?? LevelName(fi.LevelId),
                            ["top_level"] = ParamLevel(fi, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM),
                            ["base_offset_mm"] = ParamMm(fi, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM),
                            ["top_offset_mm"] = ParamMm(fi, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM),
                            ["slanted"] = fi.IsSlantedColumn,
                            ["width_mm"] = NamedMm(sym, "b", "Width", "宽度"),
                            ["depth_mm"] = NamedMm(sym, "h", "Depth", "深度"),
                            ["radius_mm"] = NamedMm(sym, "Radius", "半径") ?? (NamedMm(sym, "Diameter", "直径") is double dia ? dia / 2 : (double?)null),
                            ["bbox"] = Bbox2(fi)
                        };
                        if (lp != null)
                        {
                            d["location"] = P3(lp.Point);
                            try { d["rotation_deg"] = Math.Round(lp.Rotation * 180 / Math.PI, 2); } catch { }
                        }
                        else if (fi.Location is LocationCurve lc)
                        {
                            d["curve"] = CurveMm(lc.Curve);
                            d["location"] = P3(lc.Curve.Evaluate(0.5, true));
                        }
                        cols.Add(d); n++;
                    }
                }
                srcCounts["columns"] = n;
            }

            if (lists.TryGetValue("stairs", out var stairs))
            {
                int n = 0;
                foreach (var st in Collect(BuiltInCategory.OST_Stairs).OfType<Stairs>())
                {
                    var runs = new List<Dictionary<string, object>>();
                    foreach (var rid in st.GetStairsRuns())
                    {
                        if (!(doc.GetElement(rid) is StairsRun run)) continue;
                        var rd = new Dictionary<string, object>
                        {
                            ["id"] = rid.Value,
                            ["shape"] = run.StairsRunStyle.ToString(),
                            ["risers"] = run.ActualRisersNumber,
                            ["treads"] = run.ActualTreadsNumber,
                            ["width_mm"] = Math.Round(FtToMm(run.ActualRunWidth), 1),
                            ["base_elev_mm"] = Math.Round(FtToMm(run.BaseElevation), 1),
                            ["top_elev_mm"] = Math.Round(FtToMm(run.TopElevation), 1),
                            ["bbox"] = Bbox2(run)
                        };
                        try { rd["path"] = run.GetStairsPath().Select(CurveMm).ToList(); } catch { }
                        try { rd["footprint"] = run.GetFootprintBoundary().Select(CurveMm).ToList(); } catch { }
                        runs.Add(rd);
                    }
                    var landings = new List<Dictionary<string, object>>();
                    foreach (var lid in st.GetStairsLandings())
                    {
                        if (!(doc.GetElement(lid) is StairsLanding ld)) continue;
                        var ldd = new Dictionary<string, object>
                        {
                            ["id"] = lid.Value,
                            ["elev_mm"] = Math.Round(FtToMm(ld.BaseElevation), 1),
                            ["bbox"] = Bbox2(ld)
                        };
                        try { ldd["footprint"] = ld.GetFootprintBoundary().Select(CurveMm).ToList(); } catch { }
                        landings.Add(ldd);
                    }
                    stairs.Add(new Dictionary<string, object>
                    {
                        ["id"] = st.Id.Value,
                        ["source"] = source,
                        ["type"] = doc.GetElement(st.GetTypeId())?.Name,
                        ["base_level"] = ParamLevel(st, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM),
                        ["top_level"] = ParamLevel(st, BuiltInParameter.STAIRS_TOP_LEVEL_PARAM),
                        ["height_mm"] = Math.Round(FtToMm(st.Height), 1),
                        ["risers"] = st.ActualRisersNumber,
                        ["treads"] = st.ActualTreadsNumber,
                        ["riser_height_mm"] = Math.Round(FtToMm(st.ActualRiserHeight), 1),
                        ["tread_depth_mm"] = Math.Round(FtToMm(st.ActualTreadDepth), 1),
                        ["runs"] = runs,
                        ["landings"] = landings,
                        ["bbox"] = Bbox2(st)
                    });
                    n++;
                }
                srcCounts["stairs"] = n;
            }

            if (lists.TryGetValue("floors", out var floors))
            {
                int n = 0;
                foreach (var f in Collect(BuiltInCategory.OST_Floors).OfType<Floor>())
                {
                    floors.Add(new Dictionary<string, object>
                    {
                        ["id"] = f.Id.Value,
                        ["source"] = source,
                        ["type"] = f.FloorType?.Name,
                        ["level"] = LevelName(f.LevelId),
                        ["thickness_mm"] = ParamMm(f, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM),
                        ["area_m2"] = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED) is Parameter ap && ap.HasValue
                            ? Math.Round(UnitUtils.ConvertFromInternalUnits(ap.AsDouble(), UnitTypeId.SquareMeters), 2) : (double?)null,
                        ["bbox"] = Bbox2(f)
                    });
                    n++;
                }
                srcCounts["floors"] = n;
            }

            if (lists.TryGetValue("grids", out var grids))
            {
                int n = 0;
                foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                {
                    grids.Add(new Dictionary<string, object> { ["id"] = g.Id.Value, ["source"] = source, ["name"] = g.Name, ["curve"] = CurveMm(g.Curve) });
                    n++;
                }
                srcCounts["grids"] = n;
            }

            sources.Add(new Dictionary<string, object>
            {
                ["source"] = source,
                ["document"] = doc.Title,
                ["identity_transform"] = xf.IsIdentity,
                ["origin_mm"] = new[] { Math.Round(FtToMm(xf.Origin.X), 1), Math.Round(FtToMm(xf.Origin.Y), 1), Math.Round(FtToMm(xf.Origin.Z), 1) },
                ["counts"] = srcCounts
            });
        }
    }
}
