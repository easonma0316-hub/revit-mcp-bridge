using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// create_windows_from_cad: window symbols on a CAD layer -> hosted window
    /// instances.
    ///
    /// In a plan DWG a window is a bundle of straight lines drawn ALONG the wall
    /// across the opening (the two wall-face cut lines, glass / frame lines) plus
    /// short jamb lines across the wall. Every along-wall segment that sits inside
    /// a wall's corridor is assigned to that wall; overlapping / touching segments
    /// on one wall are merged into one opening whose extent is the window width
    /// and whose middle is the window centre. Requires the wall to run through
    /// the opening (create_walls_from_cad with `window_layers`).
    /// </summary>
    public static partial class CommandRouter
    {
        private sealed class WindowPlan
        {
            public WallLine2D Host;
            public double TLo, THi;      // extent along the host (params)
            public double Cx, Cy, Width;
            public int Segments;
            public double FaceFit;       // mean | |offset| - half thickness | of its segments: 0 = lines sit on the wall faces
        }

        private static Dictionary<string, object> CreateWindowsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0)
                throw new McpException(McpException.BadRequest, "Provide the window layer(s) via 'layer' or 'layers'.");
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            var bbox = GetOptBboxMm(p);
            bool dryRun = GetBoolOr(p, "dry_run", true);
            double hostTol = GetDoubleOr(p, "host_tolerance_mm", 60.0);
            double sillMm = GetDoubleOr(p, "sill_height_mm", 900.0);
            double minWidth = GetDoubleOr(p, "min_width_mm", 300.0);
            double maxWidth = GetDoubleOr(p, "max_width_mm", 6000.0);
            double joinGap = GetDoubleOr(p, "join_gap_mm", 100.0);
            double widthStep = GetDoubleOr(p, "width_step_mm", 100.0);
            double widthTol = GetDoubleOr(p, "width_tolerance_mm", 50.0);
            bool createTypes = GetBoolOr(p, "create_missing_types", true);
            bool skipExisting = GetBoolOr(p, "skip_existing", true);
            string aiTag = GetOptString(p, "ai_tag") ?? "_AI";
            string familyHint = GetOptString(p, "family");
            var warnings = new List<string>();
            if (!dryRun) EnsureWritable();

            // ---- 1. window segments (lines + polyline edges) on the layers ----------
            var segs = new List<double[]>(); // x1,y1,x2,y2
            int rawCount = 0;
            void Add(XYZ a, XYZ b)
            {
                double ax = FtToMm(a.X), ay = FtToMm(a.Y), bx = FtToMm(b.X), by = FtToMm(b.Y);
                if (bbox != null && !InBbox(bbox, (ax + bx) / 2, (ay + by) / 2)) return;
                var l2 = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
                if (l2 < 100 * 100) return;
                segs.Add(new[] { ax, ay, bx, by });
            }
            foreach (var prim in EnumerateCad(doc, li))
            {
                if (!layers.Contains(prim.Layer)) continue;
                rawCount++;
                if (prim.Geo is Line ln) Add(ln.GetEndPoint(0), ln.GetEndPoint(1));
                else if (prim.Geo is PolyLine pl)
                {
                    var pts = pl.GetCoordinates();
                    for (int i = 1; i < pts.Count; i++) Add(pts[i - 1], pts[i]);
                }
            }
            if (segs.Count == 0)
                throw new McpException(McpException.NotFound,
                    $"No straight window geometry on layer(s) {string.Join(", ", layers)}" + (bbox != null ? " inside bbox_mm." : "."));

            // ---- 2. assign along-wall segments to walls -----------------------------
            var walls = CollectWallLines(doc, level, bbox != null ? new[] { bbox[0] - 3000, bbox[1] - 3000, bbox[2] + 3000, bbox[3] + 3000 } : null);
            if (walls.Count == 0)
                throw new McpException(McpException.NotFound, "No straight walls on that level - run create_walls_from_cad (with window_layers) first.");
            var perWall = new Dictionary<WallLine2D, List<double[]>>(); // (tlo, thi)
            int alongCount = 0, unhostedCount = 0, acrossCount = 0;
            var unhosted = new List<Dictionary<string, object>>();
            foreach (var sg in segs)
            {
                var dx = sg[2] - sg[0]; var dy = sg[3] - sg[1]; var len = Math.Sqrt(dx * dx + dy * dy);
                WallLine2D best = null; double bestOff = double.MaxValue, bestScore = double.MaxValue; bool anyAlong = false;
                foreach (var w in walls)
                {
                    var cos = Math.Abs((dx * w.Dx + dy * w.Dy) / len);
                    if (cos < 0.97) continue;
                    anyAlong = true;
                    var half = w.HalfThick + hostTol;
                    var o1 = Math.Abs(w.Nx * sg[0] + w.Ny * sg[1] - w.Offset);
                    var o2 = Math.Abs(w.Nx * sg[2] + w.Ny * sg[3] - w.Offset);
                    if (o1 > half || o2 > half) continue;
                    var t1 = w.Dx * sg[0] + w.Dy * sg[1]; var t2 = w.Dx * sg[2] + w.Dy * sg[3];
                    var lo = Math.Min(t1, t2); var hi = Math.Max(t1, t2);
                    // inside the wall's extent (a bit of slack: the wall may stop at the jamb)
                    if (hi < w.T1 - hostTol || lo > w.T2 + hostTol) continue;
                    // prefer the wall on whose FACE the line sits (window cut lines are
                    // drawn at the wall faces); a centre-line symbol then lands on the
                    // thinnest wall around it
                    var off = Math.Max(o1, o2);
                    var score = Math.Abs(off - w.HalfThick);
                    if (score < bestScore) { bestScore = score; bestOff = off; best = w; }
                }
                if (best == null)
                {
                    if (anyAlong) { unhostedCount++; if (unhosted.Count < 50) unhosted.Add(new Dictionary<string, object> { ["start"] = new[] { Math.Round(sg[0]), Math.Round(sg[1]) }, ["end"] = new[] { Math.Round(sg[2]), Math.Round(sg[3]) }, ["length_mm"] = Math.Round(len) }); }
                    else acrossCount++;
                    continue;
                }
                alongCount++;
                var ta = best.Dx * sg[0] + best.Dy * sg[1]; var tb = best.Dx * sg[2] + best.Dy * sg[3];
                if (!perWall.TryGetValue(best, out var list)) perWall[best] = list = new List<double[]>();
                list.Add(new[] { Math.Min(ta, tb), Math.Max(ta, tb), Math.Abs(bestOff - best.HalfThick) });
            }

            // ---- 3. merge into openings ---------------------------------------------
            var plans = new List<WindowPlan>();
            foreach (var kv in perWall)
            {
                var w = kv.Key;
                var ranges = kv.Value.OrderBy(r => r[0]).ToList();
                var cur = new List<double[]>();
                void Flush()
                {
                    if (cur.Count == 0) return;
                    var lo = cur.Min(r => r[0]); var hi = cur.Max(r => r[1]);
                    var width = hi - lo;
                    // The opening extent is best given by the LONGEST segments (face /
                    // glass lines); tiny frame ticks may poke a little beyond them.
                    var longest = cur.Max(r => r[1] - r[0]);
                    var mainLo = cur.Where(r => r[1] - r[0] >= 0.8 * longest).Min(r => r[0]);
                    var mainHi = cur.Where(r => r[1] - r[0] >= 0.8 * longest).Max(r => r[1]);
                    if (width - (mainHi - mainLo) <= 2 * hostTol) { lo = mainLo; hi = mainHi; width = hi - lo; }
                    if (width >= minWidth && width <= maxWidth)
                    {
                        var tc = (lo + hi) / 2;
                        // centre on the wall's centreline
                        var cx = w.Dx * tc + w.Nx * w.Offset; var cy = w.Dy * tc + w.Ny * w.Offset;
                        plans.Add(new WindowPlan { Host = w, TLo = lo, THi = hi, Cx = cx, Cy = cy, Width = width, Segments = cur.Count, FaceFit = cur.Average(r => r[2]) });
                    }
                    cur.Clear();
                }
                double curHi = double.MinValue;
                foreach (var r in ranges)
                {
                    if (cur.Count > 0 && r[0] > curHi + joinGap) Flush();
                    cur.Add(r);
                    curHi = Math.Max(curHi, r[1]);
                }
                Flush();
            }
            // A window's two face lines can land on two different (parallel, close)
            // walls when the wall pass split a thick wall into two thin ones or a
            // double wall sits there: keep one window per opening - the one whose lines
            // sit on the wall FACES (cut lines of a real window), then the one drawn with
            // more segments, then the thinner wall (a symbolic opening line sits on the
            // centre of the wall it belongs to).
            var dedup = new List<WindowPlan>();
            int dupPlans = 0;
            foreach (var pl in plans.OrderBy(x => Math.Round(x.FaceFit / 20)).ThenByDescending(x => x.Segments).ThenBy(x => x.Host.HalfThick))
            {
                bool dup = dedup.Any(o => Math.Abs(o.Host.Dx * pl.Host.Dx + o.Host.Dy * pl.Host.Dy) > 0.97 &&
                                          Math.Abs(o.Cx - pl.Cx) <= 300 && Math.Abs(o.Cy - pl.Cy) <= 300 &&
                                          Math.Abs(o.Width - pl.Width) <= 200);
                if (dup) { dupPlans++; continue; }
                dedup.Add(pl);
            }
            plans = dedup.OrderBy(pl => pl.Cx).ThenBy(pl => pl.Cy).ToList();

            // ---- 4. type resolution ---------------------------------------------------
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Windows)
                .Cast<FamilySymbol>().ToList();
            double SymWidth(FamilySymbol s)
            {
                foreach (var prm in new[] { s.get_Parameter(BuiltInParameter.WINDOW_WIDTH), s.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM),
                                            s.LookupParameter("Width"), s.LookupParameter("宽度") })
                    if (prm != null && prm.StorageType == StorageType.Double && prm.HasValue) return FtToMm(prm.AsDouble());
                return 0;
            }
            Parameter SymWidthParam(FamilySymbol s)
            {
                foreach (var prm in new[] { s.get_Parameter(BuiltInParameter.WINDOW_WIDTH), s.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM),
                                            s.LookupParameter("Width"), s.LookupParameter("宽度") })
                    if (prm != null && prm.StorageType == StorageType.Double && !prm.IsReadOnly) return prm;
                return null;
            }
            var explicitMap = new List<KeyValuePair<double, ElementId>>();
            if (p.ContainsKey("type_map") && p["type_map"] is IEnumerable tm && !(p["type_map"] is string))
                foreach (var item in tm)
                    if (item is Dictionary<string, object> d && d.ContainsKey("width_mm") && d.ContainsKey("type_id"))
                        explicitMap.Add(new KeyValuePair<double, ElementId>(Convert.ToDouble(d["width_mm"]), new ElementId(Convert.ToInt64(d["type_id"]))));
            var hints = !string.IsNullOrEmpty(familyHint) ? new[] { familyHint } : new[] { "fixed", "固定", "casement", "平开", "sliding", "推拉" };
            var typeCache = new Dictionary<double, FamilySymbol>();
            var createdTypes = new List<string>();
            FamilySymbol ResolveType(double width)
            {
                var nominal = widthStep > 0 ? Math.Round(width / widthStep) * widthStep : Math.Round(width);
                if (typeCache.TryGetValue(nominal, out var cached)) return cached;
                FamilySymbol chosen = null;
                foreach (var kv in explicitMap)
                    if (Math.Abs(kv.Key - width) <= widthTol) chosen = doc.GetElement(kv.Value) as FamilySymbol;
                if (chosen == null)
                {
                    var pool = symbols.Where(s => SymWidth(s) > 0).ToList();
                    if (pool.Count == 0) throw new McpException(McpException.NotFound, "No window families with a Width parameter are loaded in the document.");
                    // prefer the hinted family (first hint that matches anything)
                    foreach (var h in hints)
                    {
                        var sub = pool.Where(s => (s.FamilyName ?? "").IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  (s.Name ?? "").IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (sub.Count > 0) { pool = sub; break; }
                    }
                    var nearest = pool.OrderBy(s => Math.Abs(SymWidth(s) - width)).First();
                    var nearestNominal = pool.OrderBy(s => Math.Abs(SymWidth(s) - nominal)).First();
                    if (Math.Abs(SymWidth(nearest) - width) <= widthTol) chosen = nearest;
                    else if (Math.Abs(SymWidth(nearestNominal) - nominal) <= widthTol) chosen = nearestNominal;
                    else if (createTypes && !dryRun)
                    {
                        var name = DeriveTypeName(nearest.Name, SymWidth(nearest), nominal, aiTag);
                        var byName = symbols.FirstOrDefault(s => s.FamilyName == nearest.FamilyName && s.Name == name);
                        var wp = SymWidthParam(nearest);
                        if (byName != null) chosen = byName;
                        else if (wp != null)
                        {
                            try
                            {
                                var dup = nearest.Duplicate(name) as FamilySymbol;
                                dup.get_Parameter(wp.Definition)?.Set(MmToFt(nominal));
                                MarkAiType(dup, "create_windows_from_cad", nearest.FamilyName + ": " + nearest.Name);
                                symbols.Add(dup);
                                chosen = dup;
                                createdTypes.Add($"{dup.FamilyName}: {name} ({nominal} mm, from '{nearest.Name}')");
                            }
                            catch (Exception ex)
                            {
                                warnings.Add($"Could not create a {nominal} mm type from '{nearest.FamilyName}: {nearest.Name}' ({ex.Message}); using it as is.");
                                chosen = nearest;
                            }
                        }
                        else
                        {
                            warnings.Add($"'{nearest.FamilyName}' has no writable type Width; using '{nearest.Name}' ({Math.Round(SymWidth(nearest))} mm) for a {Math.Round(width)} mm window.");
                            chosen = nearest;
                        }
                    }
                    else
                    {
                        if (dryRun && createTypes)
                            warnings.Add($"{Math.Round(width)} mm: no window type within {widthTol} mm; '{DeriveTypeName(nearest.Name, SymWidth(nearest), nominal, aiTag)}' will be created from '{nearest.FamilyName}: {nearest.Name}'.");
                        else
                            warnings.Add($"{Math.Round(width)} mm: nearest window type is '{nearest.FamilyName}: {nearest.Name}' ({Math.Round(SymWidth(nearest))} mm).");
                        chosen = nearest;
                    }
                }
                typeCache[nominal] = chosen;
                return chosen;
            }

            // ---- 5. place --------------------------------------------------------------
            var existing = skipExisting
                ? new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilyInstance>().Where(f => f.Location is LocationPoint && f.LevelId == level.Id) // same level only
                    .Select(f => { var lp = (LocationPoint)f.Location; return new[] { FtToMm(lp.Point.X), FtToMm(lp.Point.Y) }; }).ToList()
                : new List<double[]>();
            var windows = new List<Dictionary<string, object>>();
            var failures = new List<string>();
            int skipped = 0;
            double z = level.Elevation + MmToFt(sillMm);

            var ownTx = !dryRun && !doc.IsModifiable;
            var t = ownTx ? new Transaction(doc, "MCP: windows from CAD") : null;
            try
            {
                if (ownTx)
                {
                    t.Start();
                    var fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(new SilentFailures());
                    t.SetFailureHandlingOptions(fho);
                }
                foreach (var pl in plans)
                {
                    if (existing.Any(e => Math.Abs(e[0] - pl.Cx) <= 200 && Math.Abs(e[1] - pl.Cy) <= 200)) { skipped++; continue; }
                    FamilySymbol sym;
                    try { sym = ResolveType(pl.Width); }
                    catch (McpException ex) { failures.Add(ex.Message); continue; }
                    var desc = new Dictionary<string, object>
                    {
                        ["width_mm"] = Math.Round(pl.Width, 1),
                        ["center"] = new[] { Math.Round(pl.Cx, 1), Math.Round(pl.Cy, 1) },
                        ["host_wall_id"] = pl.Host.Wall.Id.Value,
                        ["segments"] = pl.Segments,
                        ["type"] = sym != null ? sym.FamilyName + ": " + sym.Name : null,
                        ["type_id"] = sym?.Id.Value
                    };
                    if (dryRun) { windows.Add(desc); continue; }
                    try
                    {
                        if (!sym.IsActive) sym.Activate();
                        var pt = new XYZ(MmToFt(pl.Cx), MmToFt(pl.Cy), z);
                        var inst = doc.Create.NewFamilyInstance(pt, sym, pl.Host.Wall, level, StructuralType.NonStructural);
                        var sill = inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sill != null && !sill.IsReadOnly) sill.Set(MmToFt(sillMm));
                        desc["id"] = inst.Id.Value;
                        windows.Add(desc);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Math.Round(pl.Width)} mm at ({Math.Round(pl.Cx)}, {Math.Round(pl.Cy)}): {ex.Message}");
                    }
                }
                if (ownTx) t.Commit();
            }
            finally { t?.Dispose(); }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun,
                ["link"] = ImportName(doc, li),
                ["layers"] = layers.ToList(),
                ["entities_on_layers"] = rawCount,
                ["segments"] = segs.Count,
                ["segments_along_walls"] = alongCount,
                ["segments_across_or_other"] = acrossCount,
                ["segments_unhosted"] = unhostedCount,
                ["unhosted_examples"] = unhosted,
                ["duplicate_plans_dropped"] = dupPlans,
                ["windows_planned"] = plans.Count,
                ["windows_created"] = dryRun ? 0 : windows.Count(w => w.ContainsKey("id")),
                ["skipped_existing"] = skipped,
                ["windows"] = windows,
                ["types_created"] = createdTypes,
                ["failures"] = failures,
                ["warnings"] = warnings
            };
        }
    }
}
