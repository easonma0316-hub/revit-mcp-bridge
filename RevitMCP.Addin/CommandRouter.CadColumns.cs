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
    /// create_columns_from_cad: closed rectangles / circles on a CAD column layer ->
    /// column instances (structural columns by default).
    ///
    /// Rectangle: a closed 4-corner polyline with perpendicular sides; the longer
    /// side is Width, the shorter Depth, the instance is rotated to the longer
    /// side. Circle: an unbound arc, or bounded arcs sharing centre + radius that
    /// add up to a full turn. Types are reused within tolerance or duplicated from
    /// the nearest type of the chosen family, named by its convention
    /// ("1000x1000mm" -> "600x500mm_AI", "1000mm" (= radius) -> "400mm_AI").
    /// </summary>
    public static partial class CommandRouter
    {
        private sealed class ColumnPlan
        {
            public string Kind;      // rect | round
            public double Cx, Cy;    // centre, mm
            public double W, D;      // rect: width (long) / depth (short), mm; round: W = D = diameter
            public double AngleRad;  // rotation of the width axis about +Z
        }

        private static Dictionary<string, object> CreateColumnsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", false);
            if (!dryRun) EnsureWritable();

            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0)
                throw new McpException(McpException.BadRequest, "Provide the column layer(s) via 'layer' or 'layers'.");
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            var topLevelId = GetOptLong(p, "top_level_id");
            var topLevel = topLevelId.HasValue ? doc.GetElement(new ElementId(topLevelId.Value)) as Level : null;
            double heightMm = GetDoubleOr(p, "height_mm", 0);
            if (topLevel == null && heightMm <= 0)
                throw new McpException(McpException.BadRequest, "Give 'height_mm' or 'top_level_id'.");
            var bbox = GetOptBboxMm(p);
            double tol = GetDoubleOr(p, "tolerance_mm", 10.0);
            double sizeStep = GetDoubleOr(p, "size_step_mm", 50.0);
            double sizeTol = GetDoubleOr(p, "size_tolerance_mm", 25.0);
            double minSize = GetDoubleOr(p, "min_size_mm", 200.0);
            double maxSize = GetDoubleOr(p, "max_size_mm", 3000.0);
            bool structural = GetBoolOr(p, "structural", true);
            bool createTypes = GetBoolOr(p, "create_missing_types", true);
            bool skipExisting = GetBoolOr(p, "skip_existing", true);
            string aiTag = p.ContainsKey("ai_tag") && p["ai_tag"] != null ? Convert.ToString(p["ai_tag"]) : "_AI";
            var warnings = new List<string>();

            // ---- 1. rectangles and circles ------------------------------------
            var plans = new List<ColumnPlan>();
            var arcGroups = new Dictionary<string, List<Arc>>(); // bounded arcs by (centre, radius)
            // Loose edges (lines, polyline edges of non-rectangles, small fillet arcs)
            // are chained into components afterwards: 4-line rectangles, rectangles
            // with rounded corners, and steel I/H symbols (2 flanges + web).
            var edges = new List<double[]>(); // x1, y1, x2, y2, length
            int looseLines = 0;
            void AddEdge(double ax, double ay, double bx, double by)
            {
                var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (len < 5 || len > 1.5 * maxSize) return;
                if (bbox != null && !InBbox(bbox, (ax + bx) / 2, (ay + by) / 2)) return;
                edges.Add(new[] { ax, ay, bx, by, len });
            }
            int polys = 0, notRect = 0, circles = 0, tooSmall = 0;
            foreach (var prim in EnumerateCad(doc, li))
            {
                if (!layers.Contains(prim.Layer)) continue;
                switch (prim.Geo)
                {
                    case Line ln:
                    {
                        looseLines++;
                        var a = ln.GetEndPoint(0); var b = ln.GetEndPoint(1);
                        AddEdge(FtToMm(a.X), FtToMm(a.Y), FtToMm(b.X), FtToMm(b.Y));
                        break;
                    }
                    case PolyLine pl:
                    {
                        polys++;
                        var pts = pl.GetCoordinates().Select(q => new[] { FtToMm(q.X), FtToMm(q.Y) }).ToList();
                        bool closed = pts.Count > 1 && Math.Abs(pts[0][0] - pts[pts.Count - 1][0]) < 1 && Math.Abs(pts[0][1] - pts[pts.Count - 1][1]) < 1;
                        if (closed) pts.RemoveAt(pts.Count - 1);
                        var plan = RectFromCorners(pts, tol);
                        if (plan == null)
                        {
                            notRect++;
                            for (int i = 1; i < pts.Count; i++) AddEdge(pts[i - 1][0], pts[i - 1][1], pts[i][0], pts[i][1]);
                            if (closed && pts.Count > 2) AddEdge(pts[pts.Count - 1][0], pts[pts.Count - 1][1], pts[0][0], pts[0][1]);
                            break;
                        }
                        if (plan.W < minSize || plan.W > maxSize || plan.D < minSize) { tooSmall++; break; }
                        if (!InBbox(bbox, plan.Cx, plan.Cy)) break;
                        plans.Add(plan);
                        break;
                    }
                    case Arc arc when !arc.IsBound:
                    {
                        circles++;
                        var d = 2 * FtToMm(arc.Radius);
                        double cx = FtToMm(arc.Center.X), cy = FtToMm(arc.Center.Y);
                        if (d < minSize || d > maxSize) { tooSmall++; break; }
                        if (!InBbox(bbox, cx, cy)) break;
                        plans.Add(new ColumnPlan { Kind = "round", Cx = cx, Cy = cy, W = d, D = d });
                        break;
                    }
                    case Arc arc:
                    {
                        var key = Math.Round(FtToMm(arc.Center.X) / tol) + ":" + Math.Round(FtToMm(arc.Center.Y) / tol) + ":" + Math.Round(FtToMm(arc.Radius) / tol);
                        if (!arcGroups.TryGetValue(key, out var lst)) arcGroups[key] = lst = new List<Arc>();
                        lst.Add(arc);
                        // a small fillet arc also acts as an edge (its chord) joining two sides
                        if (FtToMm(arc.Radius) <= 0.5 * minSize)
                        {
                            var a = arc.GetEndPoint(0); var b = arc.GetEndPoint(1);
                            AddEdge(FtToMm(a.X), FtToMm(a.Y), FtToMm(b.X), FtToMm(b.Y));
                        }
                        break;
                    }
                }
            }
            // bounded arcs that close into a full circle
            foreach (var grp in arcGroups.Values)
            {
                var sweep = grp.Sum(a => (a.GetEndParameter(1) - a.GetEndParameter(0)) * 180.0 / Math.PI);
                if (sweep < 350) continue;
                var a0 = grp[0];
                var d = 2 * FtToMm(a0.Radius);
                double cx = FtToMm(a0.Center.X), cy = FtToMm(a0.Center.Y);
                circles++;
                if (d < minSize || d > maxSize) { tooSmall++; continue; }
                if (!InBbox(bbox, cx, cy)) continue;
                plans.Add(new ColumnPlan { Kind = "round", Cx = cx, Cy = cy, W = d, D = d });
            }
            // ---- 1b. chain loose edges into components -----------------------------
            int comps = 0, compRects = 0, compSteel = 0, compOther = 0;
            if (edges.Count > 1)
            {
                // union-find over endpoints within tol
                var parent = Enumerable.Range(0, edges.Count).ToArray();
                int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
                void Union(int i, int j) { i = Find(i); j = Find(j); if (i != j) parent[i] = j; }
                bool Near(double ax, double ay, double bx, double by) => Math.Abs(ax - bx) <= tol && Math.Abs(ay - by) <= tol;
                // Connect edges whose endpoint touches another edge (at its end OR along
                // it: the web of an I-symbol meets the flange midpoints). Bbox prefilter,
                // then point-to-segment distance.
                double PtSeg(double px, double py, double[] e)
                {
                    double ex = e[2] - e[0], ey = e[3] - e[1];
                    double t = ((px - e[0]) * ex + (py - e[1]) * ey) / (e[4] * e[4]);
                    t = Math.Max(0, Math.Min(1, t));
                    double qx = e[0] + t * ex - px, qy = e[1] + t * ey - py;
                    return Math.Sqrt(qx * qx + qy * qy);
                }
                var order = Enumerable.Range(0, edges.Count).OrderBy(i => Math.Min(edges[i][0], edges[i][2])).ToList();
                var minX = order.Select(i => Math.Min(edges[i][0], edges[i][2])).ToList();
                for (int a = 0; a < order.Count; a++)
                {
                    int i = order[a];
                    double iMaxX = Math.Max(edges[i][0], edges[i][2]) + tol;
                    double iMinY = Math.Min(edges[i][1], edges[i][3]) - tol, iMaxY = Math.Max(edges[i][1], edges[i][3]) + tol;
                    for (int b = a + 1; b < order.Count && minX[b] <= iMaxX; b++)
                    {
                        int j = order[b];
                        if (Math.Max(edges[j][1], edges[j][3]) < iMinY || Math.Min(edges[j][1], edges[j][3]) > iMaxY) continue;
                        if (PtSeg(edges[i][0], edges[i][1], edges[j]) <= tol || PtSeg(edges[i][2], edges[i][3], edges[j]) <= tol ||
                            PtSeg(edges[j][0], edges[j][1], edges[i]) <= tol || PtSeg(edges[j][2], edges[j][3], edges[i]) <= tol)
                            Union(i, j);
                    }
                }
                var groups = new Dictionary<int, List<double[]>>();
                for (int i = 0; i < edges.Count; i++)
                {
                    var r = Find(i);
                    if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<double[]>();
                    g.Add(edges[i]);
                }
                foreach (var g in groups.Values)
                {
                    comps++;
                    if (g.Count < 3) { compOther++; continue; }
                    // frame = direction of the longest edge; all edges must be along or across it
                    var longest = g.OrderByDescending(e => e[4]).First();
                    double dx = (longest[2] - longest[0]) / longest[4], dy = (longest[3] - longest[1]) / longest[4];
                    double nx = -dy, ny = dx;
                    bool axisAligned = true;
                    foreach (var e in g)
                    {
                        if (e[4] < 3 * tol) continue; // fillet chords may sit at 45 deg
                        double ex = (e[2] - e[0]) / e[4], ey = (e[3] - e[1]) / e[4];
                        var c0 = Math.Abs(ex * dx + ey * dy);
                        if (c0 > 0.03 && c0 < 0.997) { axisAligned = false; break; }
                    }
                    if (!axisAligned) { compOther++; continue; }
                    double u1 = double.MaxValue, u2 = double.MinValue, v1 = double.MaxValue, v2 = double.MinValue;
                    foreach (var e in g)
                        foreach (var end in new[] { 0, 2 })
                        {
                            var u = dx * e[end] + dy * e[end + 1]; var v = nx * e[end] + ny * e[end + 1];
                            u1 = Math.Min(u1, u); u2 = Math.Max(u2, u); v1 = Math.Min(v1, v); v2 = Math.Max(v2, v);
                        }
                    double su = u2 - u1, sv = v2 - v1;
                    if (Math.Max(su, sv) > maxSize || Math.Max(su, sv) < minSize || Math.Min(su, sv) < 0.4 * minSize) { tooSmall++; continue; }
                    double cx = dx * (u1 + u2) / 2 + nx * (v1 + v2) / 2, cy = dy * (u1 + u2) / 2 + ny * (v1 + v2) / 2;
                    if (!InBbox(bbox, cx, cy)) continue;
                    var alongEdges = g.Where(e => Math.Abs(((e[2] - e[0]) * dx + (e[3] - e[1]) * dy) / e[4]) > 0.997).ToList();
                    var acrossEdges = g.Where(e => Math.Abs(((e[2] - e[0]) * dx + (e[3] - e[1]) * dy) / e[4]) < 0.03).ToList();
                    double total = g.Sum(e => e[4]);
                    double perimeter = 2 * (su + sv);
                    // I / H symbol: two equal parallel flanges at the extremes + one web
                    // between their midpoints (3 lines, nothing else of note)
                    bool steel = false;
                    if (g.Count == 3 && alongEdges.Count + acrossEdges.Count == 3)
                    {
                        var two = alongEdges.Count == 2 ? alongEdges : acrossEdges;
                        var web = alongEdges.Count == 2 ? acrossEdges : alongEdges;
                        if (two.Count == 2 && web.Count == 1 && Math.Abs(two[0][4] - two[1][4]) <= tol && web[0][4] >= 0.5 * two[0][4])
                        {
                            // web ends near the flange midpoints
                            double m0x = (two[0][0] + two[0][2]) / 2, m0y = (two[0][1] + two[0][3]) / 2;
                            double m1x = (two[1][0] + two[1][2]) / 2, m1y = (two[1][1] + two[1][3]) / 2;
                            var w0 = web[0];
                            bool touches = (Near(w0[0], w0[1], m0x, m0y) && Near(w0[2], w0[3], m1x, m1y)) ||
                                           (Near(w0[0], w0[1], m1x, m1y) && Near(w0[2], w0[3], m0x, m0y));
                            if (touches) steel = true;
                        }
                    }
                    if (steel)
                    {
                        // flange length = section breadth (b), flange spacing = depth (d);
                        // the plan's "width" axis is the depth (web) direction
                        var two = alongEdges.Count == 2 ? alongEdges : acrossEdges;
                        var flangeAlong = alongEdges.Count == 2;      // flanges run along (dx,dy)
                        double b = two[0][4];
                        double d = flangeAlong ? sv : su;
                        double ang = flangeAlong ? Math.Atan2(ny, nx) : Math.Atan2(dy, dx); // depth axis
                        while (ang > Math.PI / 2) ang -= Math.PI;
                        while (ang <= -Math.PI / 2) ang += Math.PI;
                        plans.Add(new ColumnPlan { Kind = "steel", Cx = cx, Cy = cy, W = d, D = b, AngleRad = ang });
                        compSteel++;
                        continue;
                    }
                    // rectangle (4 sides, possibly with rounded corners): the outline must be
                    // essentially complete, otherwise it is an L / U of something else
                    if (total < 0.75 * perimeter || alongEdges.Count < 2 || acrossEdges.Count < 2) { compOther++; continue; }
                    double w = Math.Max(su, sv), dd = Math.Min(su, sv);
                    double angR = su >= sv ? Math.Atan2(dy, dx) : Math.Atan2(ny, nx);
                    while (angR > Math.PI / 2) angR -= Math.PI;
                    while (angR <= -Math.PI / 2) angR += Math.PI;
                    plans.Add(new ColumnPlan { Kind = "rect", Cx = cx, Cy = cy, W = w, D = dd, AngleRad = angR });
                    compRects++;
                }
            }
            // duplicates in the drawing (same footprint drawn twice)
            int dupPlans = 0;
            {
                var unique = new List<ColumnPlan>();
                foreach (var c in plans)
                {
                    if (unique.Any(u => Math.Abs(u.Cx - c.Cx) <= 50 && Math.Abs(u.Cy - c.Cy) <= 50)) { dupPlans++; continue; }
                    unique.Add(c);
                }
                plans = unique;
            }
            if (plans.Count == 0)
                throw new McpException(McpException.NotFound,
                    $"No column rectangles/circles found on layer(s) {string.Join(", ", layers)}" + (bbox != null ? " inside bbox_mm." : "."));

            // ---- 2. skip existing columns nearby ------------------------------
            int skipped = 0;
            if (skipExisting)
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(f => (f.Category?.Id.Value == (long)BuiltInCategory.OST_Columns || f.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns)
                                && f.Location is LocationPoint && f.LevelId == level.Id) // same (base) level only
                    .Select(f => { var lp = (LocationPoint)f.Location; return new[] { FtToMm(lp.Point.X), FtToMm(lp.Point.Y) }; }).ToList();
                if (existing.Count > 0)
                {
                    var keep = new List<ColumnPlan>();
                    foreach (var c in plans)
                        if (existing.Any(e => Math.Abs(e[0] - c.Cx) <= 100 && Math.Abs(e[1] - c.Cy) <= 100)) skipped++; else keep.Add(c);
                    plans = keep;
                }
            }

            // ---- 3. types -------------------------------------------------------
            // Families of the requested category first; the other column category is
            // a fallback (e.g. steel sections only exist as structural columns, plain
            // rectangles only as architectural ones).
            var bic = structural ? BuiltInCategory.OST_StructuralColumns : BuiltInCategory.OST_Columns;
            var bicOther = structural ? BuiltInCategory.OST_Columns : BuiltInCategory.OST_StructuralColumns;
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(bic).Cast<FamilySymbol>().ToList();
            var symbolsOther = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(bicOther).Cast<FamilySymbol>().ToList();
            if (symbols.Count == 0 && symbolsOther.Count == 0)
                throw new McpException(McpException.NotFound, "No column families loaded.");
            bool IsStructuralSym(FamilySymbol s0) => s0.Category != null && s0.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
            string[] Hints(string kind)
            {
                var custom = GetOptString(p, kind + "_family");
                if (!string.IsNullOrEmpty(custom)) return new[] { custom };
                if (kind == "steel") return new[] { "W Shapes", "W-Wide", "UC", "UB", "HW", "HM", "HN", "H型钢", "I-Shape", "Wide Flange", "Steel" };
                return kind == "rect" ? new[] { "Rectangular", "Regtangular", "矩形", "方" } : new[] { "Circular", "Round", "圆" };
            }
            Parameter Prm(FamilySymbol s, params string[] names)
            {
                foreach (var n in names)
                {
                    var q = s.LookupParameter(n);
                    if (q != null && q.StorageType == StorageType.Double) return q;
                }
                return null;
            }
            double PrmMm(FamilySymbol s, params string[] names) { var q = Prm(s, names); return q != null && q.HasValue ? FtToMm(q.AsDouble()) : -1; }
            var widthNames = new[] { "Width", "b", "宽度", "B" };
            var depthNames = new[] { "Depth", "h", "深度", "H", "D" };
            var steelDepthNames = new[] { "d", "Depth", "Height", "h", "H", "D", "截面高度" };
            var steelBreadthNames = new[] { "bf", "b", "B", "Width", "Flange Width" };
            var radiusNames = new[] { "Radius", "R", "半径" };
            var diamNames = new[] { "Diameter", "D", "直径", "d" };

            var createdTypes = new List<string>();
            var typeCache = new Dictionary<string, FamilySymbol>();
            FamilySymbol ResolveType(ColumnPlan c)
            {
                double w = Math.Round(c.W / sizeStep) * sizeStep, d = Math.Round(c.D / sizeStep) * sizeStep;
                var key = c.Kind + ":" + w + "x" + d;
                if (typeCache.TryGetValue(key, out var cached)) return cached;
                var hints = Hints(c.Kind);
                List<FamilySymbol> Pool(List<FamilySymbol> src)
                {
                    var q0 = src.Where(s => hints.Any(h => (s.FamilyName ?? "").IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                    if (c.Kind == "rect") q0 = q0.Where(s => Prm(s, widthNames) != null && Prm(s, depthNames) != null).ToList();
                    else if (c.Kind == "steel") q0 = q0.Where(s => Prm(s, steelDepthNames) != null && Prm(s, steelBreadthNames) != null).ToList();
                    else q0 = q0.Where(s => Prm(s, radiusNames) != null || Prm(s, diamNames) != null).ToList();
                    return q0;
                }
                var pool = Pool(symbols);
                if (pool.Count == 0)
                {
                    pool = Pool(symbolsOther);
                    if (pool.Count > 0 && !warnings.Any(w0 => w0.StartsWith("Using " + (structural ? "architectural" : "structural"))))
                        warnings.Add($"Using {(structural ? "architectural" : "structural")} column families for {c.Kind} columns (none of the requested category has size parameters).");
                }
                if (pool.Count == 0 && c.Kind == "steel")
                {
                    // no steel section family: fall back to a rectangular column of the section's envelope
                    warnings.Add($"steel {w}x{d}: no I/H-section column family loaded (hints: {string.Join("/", hints)}; pass steel_family) - using a rectangular column of the same envelope.");
                    var rc = new ColumnPlan { Kind = "rect", Cx = c.Cx, Cy = c.Cy, W = Math.Max(w, d), D = Math.Min(w, d), AngleRad = c.AngleRad + (w >= d ? 0 : Math.PI / 2) };
                    var sym0 = ResolveType(rc);
                    typeCache[key] = sym0;
                    return sym0;
                }
                if (pool.Count == 0)
                    throw new McpException(McpException.NotFound, $"No {c.Kind} column family with size parameters found (hints: {string.Join("/", hints)}); pass {c.Kind}_family.");
                FamilySymbol chosen = null; FamilySymbol nearest; double diff;
                if (c.Kind == "steel")
                {
                    nearest = pool.OrderBy(s => Math.Abs(PrmMm(s, steelDepthNames) - w) + Math.Abs(PrmMm(s, steelBreadthNames) - d)).First();
                    diff = Math.Max(Math.Abs(PrmMm(nearest, steelDepthNames) - w), Math.Abs(PrmMm(nearest, steelBreadthNames) - d));
                }
                else if (c.Kind == "rect")
                {
                    nearest = pool.OrderBy(s => Math.Abs(PrmMm(s, widthNames) - w) + Math.Abs(PrmMm(s, depthNames) - d)).First();
                    diff = Math.Max(Math.Abs(PrmMm(nearest, widthNames) - w), Math.Abs(PrmMm(nearest, depthNames) - d));
                }
                else
                {
                    double Dia(FamilySymbol s) { var r = PrmMm(s, radiusNames); return r >= 0 ? 2 * r : PrmMm(s, diamNames); }
                    nearest = pool.OrderBy(s => Math.Abs(Dia(s) - w)).First();
                    diff = Math.Abs(Dia(nearest) - w);
                }
                if (diff <= sizeTol) chosen = nearest;
                else if (createTypes && !dryRun)
                {
                    string name;
                    if (c.Kind == "rect" || c.Kind == "steel")
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(nearest.Name, @"(\d+)\s*[xX×]\s*(\d+)");
                        name = m.Success ? nearest.Name.Substring(0, m.Index) + $"{w}x{d}" + nearest.Name.Substring(m.Index + m.Length) : $"{w}x{d}mm";
                    }
                    else
                    {
                        var isRadius = Prm(nearest, radiusNames) != null;
                        var num = isRadius ? w / 2 : w;
                        var m = System.Text.RegularExpressions.Regex.Match(nearest.Name, @"\d+");
                        name = m.Success ? nearest.Name.Substring(0, m.Index) + $"{num}" + nearest.Name.Substring(m.Index + m.Length) : $"{num}mm";
                    }
                    if (!string.IsNullOrEmpty(aiTag) && name.EndsWith(aiTag)) name = name.Substring(0, name.Length - aiTag.Length);
                    name += aiTag;
                    var byName = symbols.Concat(symbolsOther).FirstOrDefault(s => s.FamilyName == nearest.FamilyName && s.Name == name);
                    if (byName != null) chosen = byName;
                    else
                    {
                        try
                        {
                            var dup = nearest.Duplicate(name) as FamilySymbol;
                            if (c.Kind == "rect")
                            {
                                Prm(dup, widthNames)?.Set(MmToFt(w));
                                Prm(dup, depthNames)?.Set(MmToFt(d));
                            }
                            else if (c.Kind == "steel")
                            {
                                var dp = Prm(dup, steelDepthNames); var bp = Prm(dup, steelBreadthNames);
                                if (dp == null || dp.IsReadOnly || bp == null || bp.IsReadOnly)
                                    throw new InvalidOperationException("section size parameters are read-only (catalog family) - load the matching catalog type instead");
                                dp.Set(MmToFt(w)); bp.Set(MmToFt(d));
                            }
                            else
                            {
                                var rp = Prm(dup, radiusNames);
                                if (rp != null) rp.Set(MmToFt(w / 2)); else Prm(dup, diamNames)?.Set(MmToFt(w));
                            }
                            MarkAiType(dup, "create_columns_from_cad", nearest.FamilyName + ": " + nearest.Name);
                            symbols.Add(dup);
                            chosen = dup;
                            createdTypes.Add($"{dup.FamilyName}: {name} (from '{nearest.Name}')");
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not create type {name} from '{nearest.FamilyName}: {nearest.Name}' ({ex.Message}); using it as is.");
                            chosen = nearest;
                        }
                    }
                }
                else
                {
                    chosen = nearest;
                    warnings.Add(dryRun && createTypes
                        ? $"{c.Kind} {w}x{d}: no type within {sizeTol} mm; a type will be duplicated from '{nearest.FamilyName}: {nearest.Name}'."
                        : $"{c.Kind} {w}x{d}: nearest type is '{nearest.FamilyName}: {nearest.Name}'.");
                }
                typeCache[key] = chosen;
                return chosen;
            }

            // ---- 4. place -------------------------------------------------------
            var placed = new List<Dictionary<string, object>>();
            var failures = new List<string>();
            double z = level.Elevation;
            Dictionary<string, object> Describe(ColumnPlan c, FamilySymbol sym, FamilyInstance inst)
            {
                var d = new Dictionary<string, object>
                {
                    ["kind"] = c.Kind,
                    ["center"] = new[] { Math.Round(c.Cx, 1), Math.Round(c.Cy, 1) },
                    ["size_mm"] = c.Kind != "round" ? new[] { Math.Round(c.W, 1), Math.Round(c.D, 1) } : new[] { Math.Round(c.W, 1) },
                    ["rotation_deg"] = Math.Round(c.AngleRad * 180 / Math.PI, 2),
                    ["type"] = sym != null ? sym.FamilyName + ": " + sym.Name : null,
                    ["type_id"] = sym?.Id.Value
                };
                if (inst != null) d["id"] = inst.Id.Value;
                return d;
            }

            var ownTx = !dryRun && !doc.IsModifiable;
            var t = ownTx ? new Transaction(doc, "MCP: columns from CAD") : null;
            try
            {
                if (ownTx)
                {
                    t.Start();
                    var fh = t.GetFailureHandlingOptions();
                    fh.SetFailuresPreprocessor(new SilentFailures());
                    t.SetFailureHandlingOptions(fh);
                }
                foreach (var c in plans)
                {
                    FamilySymbol sym;
                    try { sym = ResolveType(c); } catch (McpException ex) { failures.Add(ex.Message); continue; }
                    if (dryRun) { placed.Add(Describe(c, sym, null)); continue; }
                    try
                    {
                        if (!sym.IsActive) sym.Activate();
                        var pt = new XYZ(MmToFt(c.Cx), MmToFt(c.Cy), z);
                        var inst = doc.Create.NewFamilyInstance(pt, sym, level, IsStructuralSym(sym) ? StructuralType.Column : StructuralType.NonStructural);
                        if (Math.Abs(c.AngleRad) > 1e-4)
                            ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(pt, pt + XYZ.BasisZ), c.AngleRad);
                        // extent: base = level; top = top level or base + height
                        var topP = inst.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                        var topOff = inst.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                        if (topLevel != null) { topP?.Set(topLevel.Id); topOff?.Set(0); }
                        else { topP?.Set(level.Id); topOff?.Set(MmToFt(heightMm)); }
                        placed.Add(Describe(c, sym, inst));
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{c.Kind} at ({Math.Round(c.Cx)}, {Math.Round(c.Cy)}): {ex.Message}");
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
                ["input"] = new Dictionary<string, object>
                {
                    ["polylines"] = polys, ["not_rectangles"] = notRect, ["circles"] = circles, ["out_of_size_range"] = tooSmall,
                    ["loose_lines"] = looseLines, ["edge_components"] = comps, ["component_rectangles"] = compRects,
                    ["component_steel_sections"] = compSteel, ["components_ignored"] = compOther
                },
                ["duplicate_plans_dropped"] = dupPlans,
                ["skipped_existing"] = skipped,
                ["columns_planned"] = plans.Count,
                ["columns_created"] = dryRun ? 0 : placed.Count,
                ["columns"] = placed,
                ["types_created"] = createdTypes,
                ["failures"] = failures,
                ["warnings"] = warnings
            };
        }

        /// <summary>4 corners -> rectangle plan (centre, long side = width, rotation), or null.</summary>
        private static ColumnPlan RectFromCorners(List<double[]> pts, double tol)
        {
            if (pts.Count != 4) return null;
            var e = new double[4][];
            for (int i = 0; i < 4; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % 4];
                e[i] = new[] { b[0] - a[0], b[1] - a[1] };
            }
            double L(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1]);
            for (int i = 0; i < 4; i++)
            {
                var l1 = L(e[i]); var l2 = L(e[(i + 1) % 4]);
                if (l1 < 1 || l2 < 1) return null;
                var dot = (e[i][0] * e[(i + 1) % 4][0] + e[i][1] * e[(i + 1) % 4][1]) / (l1 * l2);
                if (Math.Abs(dot) > 0.03) return null; // not perpendicular (~1.7 deg)
            }
            if (Math.Abs(L(e[0]) - L(e[2])) > tol || Math.Abs(L(e[1]) - L(e[3])) > tol) return null;
            var w = L(e[0]); var d = L(e[1]); var wv = e[0];
            if (d > w) { var tmp = w; w = d; d = tmp; wv = e[1]; }
            var ang = Math.Atan2(wv[1], wv[0]);
            // normalise to (-90, 90]: a rectangle rotated by 180 is the same rectangle
            while (ang > Math.PI / 2) ang -= Math.PI;
            while (ang <= -Math.PI / 2) ang += Math.PI;
            return new ColumnPlan
            {
                Kind = "rect",
                Cx = pts.Average(q => q[0]), Cy = pts.Average(q => q[1]),
                W = w, D = d, AngleRad = ang
            };
        }
    }
}
