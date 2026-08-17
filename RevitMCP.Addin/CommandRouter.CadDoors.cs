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
    /// create_doors_from_cad: door swing arcs on a CAD layer -> hosted door instances.
    ///
    /// A door in a plan DWG is (almost always) a quarter-circle swing arc whose
    /// centre is the hinge and whose radius is the leaf width, plus a small leaf
    /// rectangle. Of the arc's two end points, the one on the wall is the latch
    /// position (closed leaf), the other is the open leaf tip - together they give
    /// hinge side (hand) and swing side (facing). Two arcs whose latch points meet
    /// form a double (equal radii) or asymmetric door.
    /// </summary>
    public static partial class CommandRouter
    {
        private sealed class WallLine2D
        {
            public Wall Wall;
            public double X1, Y1, X2, Y2, Dx, Dy, Nx, Ny, Offset, T1, T2, HalfThick;
        }

        private sealed class DoorArc
        {
            public double Hx, Hy, R;             // hinge (arc centre) + leaf width, mm
            public double E1x, E1y, E2x, E2y;    // arc end points
            public WallLine2D Host;
            public double Lx, Ly, Ox, Oy;        // latch point (on wall) / open tip
            public double TL, TH;                // params of latch and hinge along the host
        }

        private sealed class DoorPlan
        {
            public string Kind;                  // single | double | asymmetric
            public double Width;                 // total opening width, mm
            public double Cx, Cy;                // opening centre on the host centerline
            public double HandX, HandY;          // latch -> hinge along the wall (fallback heuristic only)
            public double HingeT;                // desired hinge position along the host (param), single / small leaf
            public double HingeLeaf;             // width of the leaf that hangs at HingeT
            public double FaceX, FaceY;          // swing side (unit, perpendicular to wall)
            public WallLine2D Host;
            public List<DoorArc> Arcs = new List<DoorArc>();
        }

        private static List<WallLine2D> CollectWallLines(Document doc, Level level, double[] bbox)
        {
            var result = new List<WallLine2D>();
            foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (level != null && w.LevelId != level.Id) continue;
                if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line ln)) continue;
                var a = ln.GetEndPoint(0); var b = ln.GetEndPoint(1);
                var wl = new WallLine2D
                {
                    Wall = w,
                    X1 = FtToMm(a.X), Y1 = FtToMm(a.Y), X2 = FtToMm(b.X), Y2 = FtToMm(b.Y),
                    HalfThick = FtToMm(w.Width) / 2
                };
                var dx = wl.X2 - wl.X1; var dy = wl.Y2 - wl.Y1; var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) continue;
                wl.Dx = dx / len; wl.Dy = dy / len; wl.Nx = -wl.Dy; wl.Ny = wl.Dx;
                wl.Offset = wl.Nx * wl.X1 + wl.Ny * wl.Y1;
                wl.T1 = wl.Dx * wl.X1 + wl.Dy * wl.Y1; wl.T2 = wl.Dx * wl.X2 + wl.Dy * wl.Y2;
                if (wl.T1 > wl.T2) { var t = wl.T1; wl.T1 = wl.T2; wl.T2 = t; }
                if (bbox != null && !InBbox(bbox, (wl.X1 + wl.X2) / 2, (wl.Y1 + wl.Y2) / 2) &&
                    !InBbox(bbox, wl.X1, wl.Y1) && !InBbox(bbox, wl.X2, wl.Y2)) continue;
                result.Add(wl);
            }
            return result;
        }

        /// <summary>
        /// Locate a hinge of a placed door from its plan symbolic geometry. Door
        /// families draw the swing as an arc centred on the hinge (radius = leaf
        /// width) and/or the open leaf as a line from the hinge; both give the hinge
        /// as a point on the host centerline. <paramref name="smallest"/> picks the
        /// small leaf of an asymmetric door, otherwise the biggest leaf. Returns the
        /// hinge as a param along the host, or null if nothing recognisable is drawn.
        /// </summary>
        private static double? FindHingeParam(FamilyInstance inst, View planView, WallLine2D host, bool smallest)
        {
            if (planView == null) return null;
            try
            {
                var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = true, View = planView };
                var geo = inst.get_Geometry(opt);
                if (geo == null) return null;
                var hinges = new List<KeyValuePair<double, double>>();     // from swing arcs: (leaf size, param)
                var lineHinges = new List<KeyValuePair<double, double>>(); // from open-leaf lines (fallback only)
                double onWall = host.HalfThick + 60;
                void Scan(IEnumerable<GeometryObject> objs)
                {
                    foreach (var g in objs)
                    {
                        if (g is GeometryInstance gi) { Scan(gi.GetInstanceGeometry()); continue; }
                        if (g is Arc arc && arc.IsBound)
                        {
                            double cx = FtToMm(arc.Center.X), cy = FtToMm(arc.Center.Y), r = FtToMm(arc.Radius);
                            if (r < 200 || Math.Abs(host.Nx * cx + host.Ny * cy - host.Offset) > onWall) continue;
                            hinges.Add(new KeyValuePair<double, double>(r, host.Dx * cx + host.Dy * cy));
                        }
                        else if (g is Line ln)
                        {
                            var len = FtToMm(ln.Length);
                            if (len < 400) continue; // frame/jamb lines are short; leaves are not
                            var a = ln.GetEndPoint(0); var b = ln.GetEndPoint(1);
                            double ax = FtToMm(a.X), ay = FtToMm(a.Y), bx = FtToMm(b.X), by = FtToMm(b.Y);
                            var offA = Math.Abs(host.Nx * ax + host.Ny * ay - host.Offset);
                            var offB = Math.Abs(host.Nx * bx + host.Ny * by - host.Offset);
                            // open leaf: one end on the wall, the other a leaf-length off it
                            if (offA <= onWall && Math.Abs(offB - len) <= onWall)
                                lineHinges.Add(new KeyValuePair<double, double>(len, host.Dx * ax + host.Dy * ay));
                            else if (offB <= onWall && Math.Abs(offA - len) <= onWall)
                                lineHinges.Add(new KeyValuePair<double, double>(len, host.Dx * bx + host.Dy * by));
                        }
                    }
                }
                Scan(geo);
                if (hinges.Count == 0) hinges = lineHinges;
                if (hinges.Count == 0) return null;
                var pick = smallest ? hinges.OrderBy(h => h.Key).First() : hinges.OrderByDescending(h => h.Key).First();
                return pick.Value;
            }
            catch { return null; }
        }

        private static Dictionary<string, object> CreateDoorsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", false);
            if (!dryRun) EnsureWritable();

            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0)
                throw new McpException(McpException.BadRequest, "Provide the door layer(s) via 'layer' or 'layers'.");
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            var bbox = GetOptBboxMm(p);
            double tol = GetDoubleOr(p, "tolerance_mm", 5.0);
            double hostTol = GetDoubleOr(p, "host_tolerance_mm", 60.0);   // hinge may sit on the face or the centreline
            double widthTol = GetDoubleOr(p, "width_tolerance_mm", 30.0);
            double minSweep = GetDoubleOr(p, "min_sweep_deg", 60.0);
            double maxSweep = GetDoubleOr(p, "max_sweep_deg", 120.0);
            double minWidth = GetDoubleOr(p, "min_width_mm", 250.0);          // smallest leaf (asymmetric doors have ~300 mm leaves)
            double minSingle = GetDoubleOr(p, "min_single_width_mm", 600.0);  // a lone arc narrower than this is not a door
            double maxWidth = GetDoubleOr(p, "max_width_mm", 2500.0);
            bool createTypes = GetBoolOr(p, "create_missing_types", true);
            bool skipExisting = GetBoolOr(p, "skip_existing", true);
            bool invertHand = GetBoolOr(p, "invert_hand", false);
            var warnings = new List<string>();

            // ---- 1. swing arcs -------------------------------------------------
            var arcs = new List<DoorArc>();
            int arcTotal = 0;
            foreach (var prim in EnumerateCad(doc, li))
            {
                if (!layers.Contains(prim.Layer) || !(prim.Geo is Arc arc) || !arc.IsBound) continue;
                arcTotal++;
                var sweep = (arc.GetEndParameter(1) - arc.GetEndParameter(0)) * 180.0 / Math.PI;
                var r = FtToMm(arc.Radius);
                if (sweep < minSweep || sweep > maxSweep || r < minWidth || r > maxWidth) continue;
                var c = arc.Center;
                if (!InBbox(bbox, FtToMm(c.X), FtToMm(c.Y))) continue;
                var e1 = arc.GetEndPoint(0); var e2 = arc.GetEndPoint(1);
                arcs.Add(new DoorArc
                {
                    Hx = FtToMm(c.X), Hy = FtToMm(c.Y), R = r,
                    E1x = FtToMm(e1.X), E1y = FtToMm(e1.Y), E2x = FtToMm(e2.X), E2y = FtToMm(e2.Y)
                });
            }
            if (arcs.Count == 0)
                throw new McpException(McpException.NotFound,
                    $"No door swing arcs ({minSweep}-{maxSweep} deg, {minWidth}-{maxWidth} mm) on layer(s) {string.Join(", ", layers)}" +
                    (bbox != null ? " inside bbox_mm." : "."));

            // ---- 2. host wall per arc ------------------------------------------
            var walls = CollectWallLines(doc, level, null);
            if (walls.Count == 0)
                throw new McpException(McpException.NotFound, $"No straight walls on level '{level.Name}' to host doors (run create_walls_from_cad first).");
            var unhosted = new List<Dictionary<string, object>>();
            foreach (var a in arcs)
            {
                WallLine2D best = null; double bestScore = double.MaxValue;
                foreach (var w in walls)
                {
                    // Hinge must sit on the wall (within half thickness + tolerance) and
                    // within the wall's extent (allow the width as slack for wall ends).
                    var offH = Math.Abs(w.Nx * a.Hx + w.Ny * a.Hy - w.Offset);
                    if (offH > w.HalfThick + hostTol) continue;
                    var tH = w.Dx * a.Hx + w.Dy * a.Hy;
                    if (tH < w.T1 - a.R - tol || tH > w.T2 + a.R + tol) continue;
                    // One arc end must be on the wall too (the closed leaf), the other off it.
                    var off1 = Math.Abs(w.Nx * a.E1x + w.Ny * a.E1y - w.Offset);
                    var off2 = Math.Abs(w.Nx * a.E2x + w.Ny * a.E2y - w.Offset);
                    var onWall = Math.Min(off1, off2);
                    if (onWall > w.HalfThick + hostTol) continue;
                    var score = offH + onWall;
                    if (score < bestScore) { bestScore = score; best = w; }
                }
                if (best == null)
                {
                    unhosted.Add(new Dictionary<string, object>
                    {
                        ["hinge"] = new[] { Math.Round(a.Hx, 1), Math.Round(a.Hy, 1) },
                        ["width_mm"] = Math.Round(a.R, 1)
                    });
                    continue;
                }
                a.Host = best;
                var o1 = Math.Abs(best.Nx * a.E1x + best.Ny * a.E1y - best.Offset);
                var o2 = Math.Abs(best.Nx * a.E2x + best.Ny * a.E2y - best.Offset);
                if (o1 <= o2) { a.Lx = a.E1x; a.Ly = a.E1y; a.Ox = a.E2x; a.Oy = a.E2y; }
                else          { a.Lx = a.E2x; a.Ly = a.E2y; a.Ox = a.E1x; a.Oy = a.E1y; }
                a.TL = best.Dx * a.Lx + best.Dy * a.Ly;
                a.TH = best.Dx * a.Hx + best.Dy * a.Hy;
            }
            var hosted = arcs.Where(a => a.Host != null).ToList();

            // ---- 3. group arcs into doors ---------------------------------------
            var plans = new List<DoorPlan>();
            var used = new HashSet<DoorArc>();
            int ignoredSmall = 0;
            foreach (var a in hosted)
            {
                if (used.Contains(a)) continue;
                // A partner arc: same host, latch points coincide, hinges on opposite sides.
                var partner = hosted.FirstOrDefault(b => b != a && !used.Contains(b) && b.Host == a.Host &&
                    Math.Abs(b.TL - a.TL) <= 30 && Math.Sign(b.TH - b.TL) != Math.Sign(a.TH - a.TL) &&
                    Math.Abs(Math.Abs(b.TH - a.TH) - (a.R + b.R)) <= 60);
                var plan = new DoorPlan { Host = a.Host };
                plan.Arcs.Add(a); used.Add(a);
                var w = a.Host;
                double faceSign = Math.Sign(w.Nx * (a.Ox - a.Hx) + w.Ny * (a.Oy - a.Hy));
                if (partner != null)
                {
                    plan.Arcs.Add(partner); used.Add(partner);
                    plan.Width = a.R + partner.R;
                    plan.Kind = Math.Abs(a.R - partner.R) <= 30 ? "double" : "asymmetric";
                    var tc = (a.TH + partner.TH) / 2;
                    plan.Cx = w.Dx * tc + w.Nx * w.Offset; plan.Cy = w.Dy * tc + w.Ny * w.Offset;
                    // hand: judged by where the small leaf hangs (irrelevant for equal leaves)
                    var small = a.R <= partner.R ? a : partner; var big = small == a ? partner : a;
                    var hs = Math.Sign(small.TH - big.TH);
                    plan.HandX = w.Dx * hs; plan.HandY = w.Dy * hs;
                    plan.HingeT = small.TH; plan.HingeLeaf = small.R;
                }
                else
                {
                    if (a.R < minSingle) { ignoredSmall++; continue; }
                    plan.Width = a.R;
                    plan.Kind = "single";
                    var tc = (a.TH + a.TL) / 2;
                    plan.Cx = w.Dx * tc + w.Nx * w.Offset; plan.Cy = w.Dy * tc + w.Ny * w.Offset;
                    var hs = Math.Sign(a.TH - a.TL);   // latch -> hinge along the wall
                    plan.HandX = w.Dx * hs; plan.HandY = w.Dy * hs;
                    plan.HingeT = a.TH; plan.HingeLeaf = a.R;
                }
                plan.FaceX = w.Nx * faceSign; plan.FaceY = w.Ny * faceSign;
                plans.Add(plan);
            }

            // ---- 4. door types by width ------------------------------------------
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Doors).Cast<FamilySymbol>().ToList();
            double SymWidth(FamilySymbol s)
            {
                foreach (var prm in new[] { s.get_Parameter(BuiltInParameter.DOOR_WIDTH), s.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM),
                                            s.LookupParameter("Width"), s.LookupParameter("宽度") })
                    if (prm != null && prm.StorageType == StorageType.Double && prm.HasValue) return FtToMm(prm.AsDouble());
                return -1;
            }
            Parameter SymWidthParam(FamilySymbol s)
            {
                foreach (var prm in new[] { s.get_Parameter(BuiltInParameter.DOOR_WIDTH), s.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM),
                                            s.LookupParameter("Width"), s.LookupParameter("宽度") })
                    if (prm != null && prm.StorageType == StorageType.Double && !prm.IsReadOnly) return prm;
                return null;
            }
            var explicitMap = new List<KeyValuePair<double, ElementId>>();
            if (p.ContainsKey("type_map") && p["type_map"] is IEnumerable tm && !(p["type_map"] is string))
                foreach (var item in tm)
                    if (item is Dictionary<string, object> d && d.ContainsKey("width_mm") && d.ContainsKey("type_id"))
                        explicitMap.Add(new KeyValuePair<double, ElementId>(Convert.ToDouble(d["width_mm"]), new ElementId(Convert.ToInt64(d["type_id"]))));
            string[] KindHints(string kind)
            {
                var custom = GetOptString(p, kind + "_family");
                if (!string.IsNullOrEmpty(custom)) return new[] { custom };
                switch (kind)
                {
                    case "single": return new[] { "single", "单开", "单扇", "平开" };
                    case "double": return new[] { "double", "双开", "双扇" };
                    default:       return new[] { "asym", "子母" };
                }
            }
            var typeCache = new Dictionary<string, FamilySymbol>();
            var createdTypes = new List<string>();
            FamilySymbol ResolveDoorType(string kind, double width)
            {
                var key = kind + ":" + Math.Round(width);
                if (typeCache.TryGetValue(key, out var cached)) return cached;
                FamilySymbol chosen = null;
                foreach (var kv in explicitMap)
                    if (Math.Abs(kv.Key - width) <= widthTol) chosen = doc.GetElement(kv.Value) as FamilySymbol;
                if (chosen == null)
                {
                    var hints = KindHints(kind);
                    var pool = symbols.Where(s => hints.Any(h => (s.FamilyName ?? "").IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                 (s.Name ?? "").IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                    if (kind == "double") pool = pool.Where(s => !(s.FamilyName ?? "").ToLowerInvariant().Contains("asym") && !(s.FamilyName ?? "").Contains("子母")).ToList();
                    if (pool.Count == 0) pool = symbols;
                    if (pool.Count == 0) throw new McpException(McpException.NotFound, "No door families loaded in the document.");
                    var nearest = pool.OrderBy(s => Math.Abs(SymWidth(s) - width)).First();
                    var diff = Math.Abs(SymWidth(nearest) - width);
                    if (diff <= widthTol) chosen = nearest;
                    else if (createTypes && kind != "asymmetric" && !dryRun)
                    {
                        var wp = SymWidthParam(nearest);
                        if (wp != null)
                        {
                            try
                            {
                                var newName = $"W{Math.Round(width)} (MCP from {nearest.Name})";
                                var dup = nearest.Duplicate(newName) as FamilySymbol;
                                dup.get_Parameter(wp.Definition)?.Set(MmToFt(width));
                                chosen = dup;
                                createdTypes.Add($"{dup.FamilyName}: {newName}");
                            }
                            catch (Exception ex)
                            {
                                warnings.Add($"Could not create a {Math.Round(width)} mm type from '{nearest.FamilyName}: {nearest.Name}' ({ex.Message}); using it as is.");
                                chosen = nearest;
                            }
                        }
                        else
                        {
                            warnings.Add($"'{nearest.FamilyName}' has no writable type Width; using '{nearest.Name}' ({Math.Round(SymWidth(nearest))} mm) for a {Math.Round(width)} mm door.");
                            chosen = nearest;
                        }
                    }
                    else
                    {
                        if (dryRun && createTypes && kind != "asymmetric")
                            warnings.Add($"{kind} {Math.Round(width)} mm: no matching type; a new type will be duplicated from '{nearest.FamilyName}: {nearest.Name}'.");
                        else
                            warnings.Add($"{kind} {Math.Round(width)} mm: nearest type is '{nearest.FamilyName}: {nearest.Name}' ({Math.Round(SymWidth(nearest))} mm).");
                        chosen = nearest;
                    }
                }
                typeCache[key] = chosen;
                return chosen;
            }

            // ---- 5. place ---------------------------------------------------------
            var existing = skipExisting
                ? new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).OfCategory(BuiltInCategory.OST_Doors)
                    .Cast<FamilyInstance>().Where(f => f.Location is LocationPoint)
                    .Select(f => { var lp = (LocationPoint)f.Location; return new[] { FtToMm(lp.Point.X), FtToMm(lp.Point.Y) }; }).ToList()
                : new List<double[]>();

            var doors = new List<Dictionary<string, object>>();
            var failures = new List<string>();
            int skipped = 0;
            double z = level.Elevation;

            Dictionary<string, object> Describe(DoorPlan d, FamilySymbol sym, FamilyInstance inst, bool fh, bool ff)
            {
                var r = new Dictionary<string, object>
                {
                    ["kind"] = d.Kind,
                    ["width_mm"] = Math.Round(d.Width, 1),
                    ["center"] = new[] { Math.Round(d.Cx, 1), Math.Round(d.Cy, 1) },
                    ["host_wall_id"] = d.Host.Wall.Id.Value,
                    ["type"] = sym != null ? sym.FamilyName + ": " + sym.Name : null,
                    ["type_id"] = sym?.Id.Value,
                    ["leaves_mm"] = d.Arcs.Select(a => Math.Round(a.R, 1)).ToList()
                };
                if (inst != null) { r["id"] = inst.Id.Value; r["flipped_hand"] = fh; r["flipped_facing"] = ff; }
                return r;
            }

            // A plan view is needed to read the doors' symbolic swing lines.
            View planView = uidoc.ActiveView is ViewPlan ? uidoc.ActiveView : null;
            if (planView == null)
                planView = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id);

            var ownTx = !dryRun && !doc.IsModifiable;
            var t = ownTx ? new Transaction(doc, "MCP: doors from CAD") : null;
            try
            {
                if (ownTx)
                {
                    t.Start();
                    var fho = t.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(new SilentFailures());
                    t.SetFailureHandlingOptions(fho);
                }
                foreach (var d in plans)
                {
                    if (existing.Any(e => Math.Abs(e[0] - d.Cx) <= 200 && Math.Abs(e[1] - d.Cy) <= 200)) { skipped++; continue; }
                    FamilySymbol sym;
                    try { sym = ResolveDoorType(d.Kind, d.Width); }
                    catch (McpException ex) { failures.Add(ex.Message); continue; }
                    if (dryRun) { doors.Add(Describe(d, sym, null, false, false)); continue; }
                    try
                    {
                        if (!sym.IsActive) sym.Activate();
                        var pt = new XYZ(MmToFt(d.Cx), MmToFt(d.Cy), z);
                        var inst = doc.Create.NewFamilyInstance(pt, sym, d.Host.Wall, level, StructuralType.NonStructural);
                        doc.Regenerate();
                        bool fh = false, ff = false;
                        double? dbgHinge = null;
                        var face = inst.FacingOrientation;
                        if (face.X * d.FaceX + face.Y * d.FaceY < 0 && inst.CanFlipFacing) { inst.flipFacing(); ff = true; doc.Regenerate(); }
                        if (d.Kind != "double" && inst.CanFlipHand)
                        {
                            // Where does this family actually hang its leaf? Read the open-leaf
                            // symbolic line in plan; fall back to the hand vector if not found.
                            var hingeT = FindHingeParam(inst, planView, d.Host, d.Kind == "asymmetric");
                            dbgHinge = hingeT;
                            bool wrong;
                            if (hingeT.HasValue)
                                wrong = Math.Abs(hingeT.Value - d.HingeT) > d.Width / 2;
                            else
                            {
                                var hand = inst.HandOrientation;
                                wrong = (hand.X * d.HandX + hand.Y * d.HandY) * (invertHand ? -1 : 1) < 0;
                            }
                            if (wrong) { inst.flipHand(); fh = true; doc.Regenerate(); }
                            if (hingeT.HasValue && GetBoolOr(p, "verify_hinge", true))
                            {
                                var after = FindHingeParam(inst, planView, d.Host, d.Kind == "asymmetric");
                                if (after.HasValue && Math.Abs(after.Value - d.HingeT) > d.Width / 2)
                                    warnings.Add($"Door {inst.Id.Value}: hinge still {Math.Round(Math.Abs(after.Value - d.HingeT))} mm from the CAD hinge after flipping - check it.");
                            }
                        }
                        var desc = Describe(d, sym, inst, fh, ff);
                        desc["hinge_check"] = new Dictionary<string, object>
                        {
                            ["detected_t"] = dbgHinge.HasValue ? Math.Round(dbgHinge.Value, 1) : (object)null,
                            ["desired_t"] = Math.Round(d.HingeT, 1),
                            ["plan_view"] = planView?.Name
                        };
                        doors.Add(desc);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{d.Kind} {Math.Round(d.Width)} mm at ({Math.Round(d.Cx)}, {Math.Round(d.Cy)}): {ex.Message}");
                    }
                }
                if (ownTx) t.Commit();
            }
            finally
            {
                t?.Dispose();
            }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun,
                ["link"] = ImportName(doc, li),
                ["layers"] = layers.ToList(),
                ["arcs_on_layers"] = arcTotal,
                ["swing_arcs"] = arcs.Count,
                ["hosted_arcs"] = hosted.Count,
                ["ignored_small_arcs"] = ignoredSmall,
                ["doors_planned"] = plans.Count,
                ["doors_created"] = dryRun ? 0 : doors.Count,
                ["skipped_existing"] = skipped,
                ["doors"] = doors,
                ["unhosted"] = unhosted,
                ["types_created"] = createdTypes,
                ["failures"] = failures,
                ["warnings"] = warnings
            };
        }
    }
}
