using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// create_stairs_from_cad: tread lines on a CAD stair layer -> Revit stairs
    /// (straight runs + automatic landings, StairsEditScope).
    ///
    /// A run in a plan DWG is a comb of parallel, equal-length, equally spaced
    /// lines (the tread nosings / risers); the spacing is the tread depth, the
    /// line length the run width, the count the risers. Runs whose ends lie close
    /// together (a landing between them, U- or L-shaped stairs) are chained into
    /// one stair. Direction: the run end closest to a path arrowhead (a small
    /// closed triangle on `arrow_layers`, or on the stair layers) is the top;
    /// without an arrowhead the chain order is a guess (reported).
    /// </summary>
    public static partial class CommandRouter
    {
        private sealed class RunPlan
        {
            public double Dx, Dy;          // unit run direction (across the treads), bottom -> top once ordered
            public double Cx1, Cy1, Cx2, Cy2; // path start / end on the run centre line (before ordering: arbitrary)
            public double Width, TreadDepth;
            public int Risers;
            public double MinX, MinY, MaxX, MaxY;
            public int Chain = -1;
            public bool Reversed;          // set when ordering flips start/end
            public bool Cut;               // ends at a break line (the stair going up, cut at the view plane)
            public double Tx, Ty, Tlo, Thi, O1, O2; // frame: tread direction, across range, along range (mm)
            public double SX => Reversed ? Cx2 : Cx1; public double SY => Reversed ? Cy2 : Cy1;
            public double EX => Reversed ? Cx1 : Cx2; public double EY => Reversed ? Cy1 : Cy2;
        }

        private static Dictionary<string, object> CreateStairsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", true);
            if (!dryRun) EnsureWritable();
            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0)
                throw new McpException(McpException.BadRequest, "Provide the stair layer(s) via 'layer' or 'layers'.");
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            // from_below: the plan of `level_id` shows the stairs ARRIVING at that level complete
            // (Revit draws a stair in the plan of its top level too), while the stairs going up are
            // cut at the view plane. In that mode the stairs are built from `base_level_id`
            // (default: the next level down) up to `level_id`.
            bool fromBelow = GetBoolOr(p, "from_below", false);
            var topLevelId = GetOptLong(p, "top_level_id");
            var topLevel = topLevelId.HasValue ? doc.GetElement(new ElementId(topLevelId.Value)) as Level : null;
            double heightMm = GetDoubleOr(p, "height_mm", 0);
            Level baseLevel = level;
            if (fromBelow)
            {
                topLevel = level;
                var baseId = GetOptLong(p, "base_level_id");
                baseLevel = baseId.HasValue ? doc.GetElement(new ElementId(baseId.Value)) as Level : null;
                if (baseLevel == null)
                    baseLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .Where(l => l.Elevation < level.Elevation - 1e-6).OrderByDescending(l => l.Elevation).FirstOrDefault();
                if (baseLevel == null) throw new McpException(McpException.BadRequest, "from_below: give 'base_level_id' (no level below this one).");
            }
            else if (topLevel == null && heightMm <= 0)
            {
                // default: the next level up
                topLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => l.Elevation > level.Elevation + 1e-6).OrderBy(l => l.Elevation).FirstOrDefault();
                if (topLevel == null) throw new McpException(McpException.BadRequest, "Give 'top_level_id' or 'height_mm' (no level above the base level).");
            }
            double totalHeight = topLevel != null ? FtToMm(topLevel.Elevation - baseLevel.Elevation) : heightMm;
            var bbox = GetOptBboxMm(p);
            double treadMin = GetDoubleOr(p, "tread_min_mm", 220), treadMax = GetDoubleOr(p, "tread_max_mm", 420);
            double widthMin = GetDoubleOr(p, "width_min_mm", 600), widthMax = GetDoubleOr(p, "width_max_mm", 3500);
            int minTreads = GetIntOr(p, "min_treads", 3);
            int minRisers = GetIntOr(p, "min_risers", 5);
            double landingMax = GetDoubleOr(p, "landing_max_mm", 2200);
            double angleTol = GetDoubleOr(p, "angle_tolerance_deg", 1.0);
            bool skipExisting = GetBoolOr(p, "skip_existing", true);
            string aiTag = GetOptString(p, "ai_tag") ?? "_AI";
            var arrowLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (p.ContainsKey("arrow_layers") && p["arrow_layers"] is IEnumerable al && !(p["arrow_layers"] is string))
                foreach (var o in al) if (o != null) arrowLayers.Add(Convert.ToString(o));
            var warnings = new List<string>();

            // ---- 1. segments, arrows, break lines -------------------------------
            var segs = new List<double[]>();        // x1,y1,x2,y2,len,angle(0..180)
            var arrows = new List<double[]>();      // apex x, apex y, dir x, dir y
            var arrowSegs = new List<double[]>();   // short segments on arrow layers: x1,y1,x2,y2
            var pathPolys = new List<List<double[]>>(); // polylines on arrow layers (stair path annotation)
            var paths = new List<double[]>();       // px, py (path start), ax, ay (apex), length, vertices
            double widthAdd = GetDoubleOr(p, "width_add_mm", 0);
            double riserTarget = GetDoubleOr(p, "riser_target_mm", 170);
            var zigzags = new List<List<double[]>>(); // break-line vertices
            var breakLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (p.ContainsKey("break_layers") && p["break_layers"] is IEnumerable bl && !(p["break_layers"] is string))
                foreach (var o in bl) if (o != null) breakLayers.Add(Convert.ToString(o));
            double nosingMax = GetDoubleOr(p, "nosing_max_mm", 100);
            double riserMin = GetDoubleOr(p, "riser_min_mm", 130), riserMax = GetDoubleOr(p, "riser_max_mm", 220);
            void AddSeg(double ax, double ay, double bx, double by)
            {
                var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (len < widthMin || len > widthMax) return;
                if (bbox != null && !InBbox(bbox, (ax + bx) / 2, (ay + by) / 2)) return;
                var ang = Math.Atan2(by - ay, bx - ax) * 180 / Math.PI; if (ang < 0) ang += 180; if (ang >= 180 - 1e-9) ang -= 180;
                segs.Add(new[] { ax, ay, bx, by, len, ang });
            }
            void AddArrowSeg(double ax, double ay, double bx, double by)
            {
                var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (len < 100 || len > 700) return;
                if (bbox != null && !InBbox(bbox, (ax + bx) / 2, (ay + by) / 2)) return;
                arrowSegs.Add(new[] { ax, ay, bx, by });
            }
            bool IsZigzag(List<double[]> pts)
            {
                // a break-line symbol: >= 4 short segments whose turn direction alternates at least twice
                if (pts.Count < 5) return false;
                int flips = 0; double prevCross = 0;
                for (int i = 1; i < pts.Count; i++)
                {
                    double sx = pts[i][0] - pts[i - 1][0], sy = pts[i][1] - pts[i - 1][1];
                    if (Math.Sqrt(sx * sx + sy * sy) > 1000) return false;
                    if (i >= 2)
                    {
                        double px = pts[i - 1][0] - pts[i - 2][0], py = pts[i - 1][1] - pts[i - 2][1];
                        double lp = Math.Sqrt(px * px + py * py), ls = Math.Sqrt(sx * sx + sy * sy);
                        double cr = lp > 1e-9 && ls > 1e-9 ? (px * sy - py * sx) / (lp * ls) : 0; // sin(turn angle)
                        if (Math.Abs(cr) < 0.05) continue;                                       // collinear (float noise)
                        if (prevCross != 0 && Math.Sign(cr) != Math.Sign(prevCross)) flips++;
                        prevCross = cr;
                    }
                }
                return flips >= 2;
            }
            foreach (var prim in EnumerateCad(doc, li))
            {
                bool onStair = layers.Contains(prim.Layer), onArrow = arrowLayers.Contains(prim.Layer), onBreak = breakLayers.Contains(prim.Layer);
                if (!onStair && !onArrow && !onBreak) continue;
                switch (prim.Geo)
                {
                    case Line ln:
                    {
                        double ax = FtToMm(ln.GetEndPoint(0).X), ay = FtToMm(ln.GetEndPoint(0).Y), bx = FtToMm(ln.GetEndPoint(1).X), by = FtToMm(ln.GetEndPoint(1).Y);
                        if (onStair) AddSeg(ax, ay, bx, by);
                        if (onArrow || (onStair && arrowLayers.Count == 0)) AddArrowSeg(ax, ay, bx, by);
                        break;
                    }
                    case PolyLine pl:
                    {
                        var pts = pl.GetCoordinates().Select(q => new[] { FtToMm(q.X), FtToMm(q.Y) }).ToList();
                        bool closed = pts.Count > 1 && Math.Abs(pts[0][0] - pts[pts.Count - 1][0]) < 1 && Math.Abs(pts[0][1] - pts[pts.Count - 1][1]) < 1;
                        var core = closed ? pts.Take(pts.Count - 1).ToList() : pts;
                        // arrowhead drawn as a small closed triangle
                        if (core.Count == 3 && (closed || pts.Count == 3))
                        {
                            double ex = core.Max(q => q[0]) - core.Min(q => q[0]), ey = core.Max(q => q[1]) - core.Min(q => q[1]);
                            if (Math.Max(ex, ey) <= 600 && Math.Max(ex, ey) >= 40)
                            {
                                // apex = vertex opposite the shortest side; direction = from that side's midpoint to the apex
                                int best = 0; double bestLen = double.MaxValue;
                                for (int k = 0; k < 3; k++)
                                {
                                    var a = core[(k + 1) % 3]; var b = core[(k + 2) % 3];
                                    var l = Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
                                    if (l < bestLen) { bestLen = l; best = k; }
                                }
                                var apex = core[best]; var m1 = core[(best + 1) % 3]; var m2 = core[(best + 2) % 3];
                                double mx = (m1[0] + m2[0]) / 2, my = (m1[1] + m2[1]) / 2;
                                double ddx = apex[0] - mx, ddy = apex[1] - my, dl = Math.Sqrt(ddx * ddx + ddy * ddy);
                                if (dl > 1 && (bbox == null || InBbox(bbox, apex[0], apex[1])))
                                    arrows.Add(new[] { apex[0], apex[1], ddx / dl, ddy / dl });
                                break;
                            }
                        }
                        if (IsZigzag(pts)) { if (bbox == null || pts.Any(q => InBbox(bbox, q[0], q[1]))) zigzags.Add(pts); break; }
                        if (onArrow && pts.Count >= 3 && (bbox == null || pts.Any(q => InBbox(bbox, q[0], q[1])))) pathPolys.Add(pts);
                        // treads: merge collinear consecutive polyline segments (a tread line is split where
                        // the path annotation crosses it)
                        if (onStair)
                        {
                            int i0 = 0;
                            for (int i = 1; i < pts.Count; i++)
                            {
                                bool last = i == pts.Count - 1;
                                bool straight = false;
                                if (!last)
                                {
                                    double ux = pts[i][0] - pts[i0][0], uy = pts[i][1] - pts[i0][1];
                                    double vx = pts[i + 1][0] - pts[i][0], vy = pts[i + 1][1] - pts[i][1];
                                    double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                                    straight = lu > 1e-6 && lv > 1e-6 && (ux * vx + uy * vy) / (lu * lv) > 0.9999;
                                }
                                if (straight) continue;
                                AddSeg(pts[i0][0], pts[i0][1], pts[i][0], pts[i][1]);
                                i0 = i;
                            }
                        }
                        for (int i = 1; i < pts.Count; i++)
                            if (onArrow || (onStair && arrowLayers.Count == 0)) AddArrowSeg(pts[i - 1][0], pts[i - 1][1], pts[i][0], pts[i][1]);
                        break;
                    }
                }
            }
            // open "V" arrowheads: two short segments sharing an endpoint, 50..130 deg apart
            {
                var usedArrow = new bool[arrowSegs.Count];
                for (int i = 0; i < arrowSegs.Count; i++)
                {
                    if (usedArrow[i]) continue;
                    for (int j = i + 1; j < arrowSegs.Count; j++)
                    {
                        if (usedArrow[j]) continue;
                        var a = arrowSegs[i]; var b = arrowSegs[j];
                        double[] apexPt = null, fa = null, fb = null;
                        foreach (var ea in new[] { 0, 2 })
                            foreach (var eb in new[] { 0, 2 })
                                if (Math.Abs(a[ea] - b[eb]) < 5 && Math.Abs(a[ea + 1] - b[eb + 1]) < 5)
                                { apexPt = new[] { a[ea], a[ea + 1] }; fa = new[] { a[2 - ea], a[3 - ea] }; fb = new[] { b[2 - eb], b[3 - eb] }; }
                        if (apexPt == null) continue;
                        double ux = fa[0] - apexPt[0], uy = fa[1] - apexPt[1], vx = fb[0] - apexPt[0], vy = fb[1] - apexPt[1];
                        double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                        if (lu < 1 || lv < 1 || Math.Max(lu, lv) / Math.Min(lu, lv) > 1.15) continue;
                        double cosang = (ux * vx + uy * vy) / (lu * lv);
                        double angDeg = Math.Acos(Math.Max(-1, Math.Min(1, cosang))) * 180 / Math.PI;
                        if (angDeg < 50 || angDeg > 130) continue;
                        double mx = (fa[0] + fb[0]) / 2, my = (fa[1] + fb[1]) / 2;
                        double ddx = apexPt[0] - mx, ddy = apexPt[1] - my, dl = Math.Sqrt(ddx * ddx + ddy * ddy);
                        if (dl < 1) continue;
                        arrows.Add(new[] { apexPt[0], apexPt[1], ddx / dl, ddy / dl });
                        usedArrow[i] = usedArrow[j] = true;
                        break;
                    }
                }
            }
            // stair paths: an arrow-layer polyline that starts or ends at an arrowhead apex (or whose
            // second / second-to-last vertex is the apex when one wing belongs to the polyline).
            // Path start P = the far end, A = apex.
            foreach (var pl in pathPolys)
            {
                int n = pl.Count;
                bool Near(double[] q, double[] a) => Math.Abs(q[0] - a[0]) < 5 && Math.Abs(q[1] - a[1]) < 5;
                double[] apex = null; int pIdx = -1; int aIdx = -1;
                foreach (var a in arrows)
                {
                    if (Near(pl[0], a)) { apex = a; aIdx = 0; pIdx = n - 1; break; }
                    if (Near(pl[n - 1], a)) { apex = a; aIdx = n - 1; pIdx = 0; break; }
                    if (n >= 3 && Near(pl[1], a) && Math.Sqrt(Math.Pow(pl[0][0] - pl[1][0], 2) + Math.Pow(pl[0][1] - pl[1][1], 2)) <= 700) { apex = a; aIdx = 1; pIdx = n - 1; break; }
                    if (n >= 3 && Near(pl[n - 2], a) && Math.Sqrt(Math.Pow(pl[n - 1][0] - pl[n - 2][0], 2) + Math.Pow(pl[n - 1][1] - pl[n - 2][1], 2)) <= 700) { apex = a; aIdx = n - 2; pIdx = 0; break; }
                }
                if (apex == null) continue;
                double len = 0; int lo = Math.Min(aIdx, pIdx), hi = Math.Max(aIdx, pIdx);
                for (int i = lo + 1; i <= hi; i++) len += Math.Sqrt(Math.Pow(pl[i][0] - pl[i - 1][0], 2) + Math.Pow(pl[i][1] - pl[i - 1][1], 2));
                paths.Add(new[] { pl[pIdx][0], pl[pIdx][1], apex[0], apex[1], len, hi - lo });
            }

            // ---- 1b. dedupe: superimposed lines (stairs stacked over each other) and nosing doubles ----
            int rawSegs = segs.Count;
            {
                var keep = new List<double[]>();
                var ordered = segs.OrderByDescending(s => s[4]).ToList();
                foreach (var s in ordered)
                {
                    double dx = (s[2] - s[0]) / s[4], dy = (s[3] - s[1]) / s[4], nx = -dy, ny = dx;
                    bool dup = false;
                    foreach (var k in keep)
                    {
                        double da = Math.Abs(k[5] - s[5]); if (da > 90) da = 180 - da;
                        if (da > angleTol) continue;
                        double off1 = nx * (k[0] - s[0]) + ny * (k[1] - s[1]), off2 = nx * (k[2] - s[0]) + ny * (k[3] - s[1]);
                        if (Math.Abs(off1) > nosingMax || Math.Abs(off2) > nosingMax) continue;
                        double t1 = dx * (k[0] - s[0]) + dy * (k[1] - s[1]), t2 = dx * (k[2] - s[0]) + dy * (k[3] - s[1]);
                        double lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
                        // s is contained in k (k is the longer one) -> duplicate / nosing double
                        if (lo <= 0.1 * s[4] + 5 && hi >= 0.9 * s[4] - 5) { dup = true; break; }
                    }
                    if (!dup) keep.Add(s);
                }
                segs = keep;
            }
            if (segs.Count < minTreads)
                throw new McpException(McpException.NotFound, $"No stair tread lines on layer(s) {string.Join(", ", layers)}" + (bbox != null ? " inside bbox_mm." : "."));

            // ---- 2. combs -> runs ---------------------------------------------
            var runs = new List<RunPlan>();
            var byAngle = Enumerable.Range(0, segs.Count).OrderBy(i => segs[i][5]).ToList();
            var groups = new List<List<int>>();
            foreach (var i in byAngle)
            {
                var g = groups.LastOrDefault();
                if (g != null && Math.Abs(segs[i][5] - segs[g[g.Count - 1]][5]) <= angleTol) g.Add(i);
                else groups.Add(new List<int> { i });
            }
            if (groups.Count > 1)
            {
                var f = groups[0]; var l = groups[groups.Count - 1];
                if (Math.Abs(segs[l[l.Count - 1]][5] - 180 - segs[f[0]][5]) <= angleTol) { f.AddRange(l); groups.RemoveAt(groups.Count - 1); }
            }
            foreach (var g in groups)
            {
                if (g.Count < minTreads) continue;
                var refIdx = g[g.Count / 2];
                var a0 = segs[refIdx][5] * Math.PI / 180;
                double dx = Math.Cos(a0), dy = Math.Sin(a0), nx = -dy, ny = dx; // tread direction d, run direction n
                var framed = g.Select(i =>
                {
                    var s = segs[i];
                    double t1 = dx * s[0] + dy * s[1], t2 = dx * s[2] + dy * s[3];
                    double off = nx * (s[0] + s[2]) / 2 + ny * (s[1] + s[3]) / 2;
                    return new { i, off, tlo = Math.Min(t1, t2), thi = Math.Max(t1, t2), len = s[4] };
                }).OrderBy(x => x.off).ToList();
                var taken = new HashSet<int>();
                for (int s0 = 0; s0 < framed.Count; s0++)
                {
                    if (taken.Contains(framed[s0].i)) continue;
                    // grow a comb from s0: next line = similar t-range, spacing in [treadMin, treadMax], consistent
                    var comb = new List<int> { s0 };
                    double spacing = -1;
                    int cur = s0;
                    while (true)
                    {
                        int next = -1; double bestD = double.MaxValue;
                        for (int k = cur + 1; k < framed.Count; k++)
                        {
                            var d = framed[k].off - framed[cur].off;
                            if (d > treadMax + 15) break;
                            if (d < treadMin - 15) continue;
                            if (taken.Contains(framed[k].i)) continue;
                            var ov = Math.Min(framed[cur].thi, framed[k].thi) - Math.Max(framed[cur].tlo, framed[k].tlo);
                            var shorter = Math.Min(framed[cur].len, framed[k].len);
                            if (ov < 0.8 * shorter) continue;
                            if (Math.Abs(framed[k].len - framed[cur].len) > 0.15 * Math.Max(framed[k].len, framed[cur].len)) continue;
                            if (spacing > 0 && Math.Abs(d - spacing) > 20) continue;
                            if (d < bestD) { bestD = d; next = k; }
                        }
                        if (next < 0) break;
                        if (spacing < 0) spacing = bestD;
                        comb.Add(next); cur = next;
                    }
                    if (comb.Count < minTreads) continue;
                    foreach (var k in comb) taken.Add(framed[k].i);
                    var offs = comb.Select(k => framed[k].off).ToList();
                    var tlo = comb.Average(k => framed[k].tlo); var thi = comb.Average(k => framed[k].thi);
                    var tc = (tlo + thi) / 2;
                    double depth = (offs.Last() - offs.First()) / (comb.Count - 1);
                    // Revit draws one line per riser; the run path spans first riser -> last riser,
                    // i.e. (risers - 1) treads (the top riser sits at the landing / floor edge)
                    double o1 = offs.First(), o2 = offs.Last();
                    var run = new RunPlan
                    {
                        Dx = nx, Dy = ny,
                        Cx1 = dx * tc + nx * o1, Cy1 = dy * tc + ny * o1,
                        Cx2 = dx * tc + nx * o2, Cy2 = dy * tc + ny * o2,
                        Width = thi - tlo, TreadDepth = depth, Risers = comb.Count
                    };
                    var xs = comb.SelectMany(k => new[] { segs[framed[k].i][0], segs[framed[k].i][2] }).ToList();
                    var ys = comb.SelectMany(k => new[] { segs[framed[k].i][1], segs[framed[k].i][3] }).ToList();
                    run.MinX = xs.Min(); run.MaxX = xs.Max(); run.MinY = ys.Min(); run.MaxY = ys.Max();
                    run.Tx = dx; run.Ty = dy; run.Tlo = tlo; run.Thi = thi; run.O1 = o1; run.O2 = o2;
                    run.Width += widthAdd;
                    runs.Add(run);
                }
            }
            if (runs.Count == 0)
                throw new McpException(McpException.NotFound, "No tread combs (>= min_treads parallel, equally spaced lines) found on the stair layer(s).");
            // Break lines: a zigzag just beyond (or at) the last tread line of a run marks the stair
            // going up, cut at the view plane. A zigzag crossing the middle of a run means a cut
            // stair is stacked on a complete one (same lines) - that run is kept. When several runs
            // share the band (cut run + the next run of the stair below), only the nearest is cut.
            foreach (var z in zigzags)
            {
                RunPlan bestRun = null; double bestD = double.MaxValue;
                foreach (var run in runs)
                {
                    var ts = new List<double>();
                    foreach (var q in z)
                    {
                        double t = run.Dx * q[0] + run.Dy * q[1], u = run.Tx * q[0] + run.Ty * q[1];
                        if (u < run.Tlo - 50 || u > run.Thi + 50) continue;
                        ts.Add(t);
                    }
                    if (ts.Count == 0) continue;
                    double tz = ts.Average(), d = run.TreadDepth;
                    double dist = double.MaxValue;
                    if (tz > run.O2 - 0.5 * d && tz < run.O2 + 2.5 * d) dist = Math.Abs(tz - run.O2);
                    else if (tz < run.O1 + 0.5 * d && tz > run.O1 - 2.5 * d) dist = Math.Abs(tz - run.O1);
                    if (dist < bestD) { bestD = dist; bestRun = run; }
                }
                if (bestRun != null) bestRun.Cut = true;
            }

            // ---- 3. chain runs into stairs (ends close together = landing) --------
            double EndDist(RunPlan a, RunPlan b)
            {
                double D(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
                return Math.Min(Math.Min(D(a.Cx1, a.Cy1, b.Cx1, b.Cy1), D(a.Cx1, a.Cy1, b.Cx2, b.Cy2)),
                                Math.Min(D(a.Cx2, a.Cy2, b.Cx1, b.Cy1), D(a.Cx2, a.Cy2, b.Cx2, b.Cy2)));
            }
            bool Overlap(RunPlan a, RunPlan b) =>
                Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX) > 200 && Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY) > 200;
            // two runs join through a landing when their ends are close AND the geometry is a U
            // (parallel, side by side: along-ranges overlap), a straight continuation (parallel, same
            // centre line) or an L (perpendicular); parallel runs merely staggered next to each other
            // (adjacent stair cores) do not.
            bool Connects(RunPlan a, RunPlan b)
            {
                if (EndDist(a, b) > landingMax) return false;
                double dot = Math.Abs(a.Dx * b.Dx + a.Dy * b.Dy);
                if (dot < 0.9) return true; // L-shape or skewed: accept
                // parallel: project b onto a's frame
                double tb1 = a.Dx * b.Cx1 + a.Dy * b.Cy1, tb2 = a.Dx * b.Cx2 + a.Dy * b.Cy2;
                double ta1 = a.Dx * a.Cx1 + a.Dy * a.Cy1, ta2 = a.Dx * a.Cx2 + a.Dy * a.Cy2;
                double lo = Math.Max(Math.Min(ta1, ta2), Math.Min(tb1, tb2)), hi = Math.Min(Math.Max(ta1, ta2), Math.Max(tb1, tb2));
                double minLen = Math.Min(Math.Abs(ta2 - ta1), Math.Abs(tb2 - tb1));
                if (hi - lo >= 0.5 * minLen) return true; // U: side by side
                double across = Math.Abs(a.Tx * ((b.Cx1 + b.Cx2) / 2 - (a.Cx1 + a.Cx2) / 2) + a.Ty * ((b.Cy1 + b.Cy2) / 2 - (a.Cy1 + a.Cy2) / 2));
                return across <= Math.Max(a.Width, b.Width) / 2; // straight continuation
            }
            List<List<RunPlan>> BuildChains(List<RunPlan> pool)
            {
                foreach (var r in pool) r.Chain = -1;
                var result = new List<List<RunPlan>>();
                for (int i = 0; i < pool.Count; i++)
                {
                    if (pool[i].Chain >= 0) continue;
                    pool[i].Chain = result.Count;
                    var members = new List<RunPlan> { pool[i] };
                    var stack = new Stack<RunPlan>(); stack.Push(pool[i]);
                    while (stack.Count > 0)
                    {
                        var a = stack.Pop();
                        foreach (var b in pool)
                        {
                            if (b.Chain >= 0 || Overlap(a, b)) continue;
                            if (Connects(a, b)) { b.Chain = a.Chain; members.Add(b); stack.Push(b); }
                        }
                    }
                    result.Add(members);
                }
                return result;
            }
            // Cut runs (the stair going up, truncated at the view plane) cannot be modelled from this
            // plan - drop them; the rest are complete stairs. Their base level: the given one, or
            // (from_below without base_level_id) the level below whose height gives the most
            // plausible riser (closest to riser_target_mm within riser_min/max).
            var baseCandidates = new List<Level>();
            if (fromBelow && !GetOptLong(p, "base_level_id").HasValue)
                baseCandidates = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => l.Elevation < level.Elevation - 1e-6).OrderByDescending(l => l.Elevation).ToList();
            else baseCandidates.Add(baseLevel);
            int droppedCut = runs.Count(r => r.Cut);
            int droppedImplausible = 0;
            var chainList = new List<List<RunPlan>>();
            var chainBase = new List<Level>();
            var chainHeight = new List<double>();
            foreach (var members in BuildChains(runs.Where(r => !r.Cut).ToList()))
            {
                int n = members.Sum(r => r.Risers);
                if (n < minRisers)
                {
                    droppedImplausible += members.Count;
                    warnings.Add($"Chain of {members.Count} run(s) / {n} risers near ({Math.Round(members[0].Cx1)}, {Math.Round(members[0].Cy1)}) skipped: fewer than min_risers={minRisers} (a fragment hidden by a landing / floor?).");
                    continue;
                }
                Level bestLevel = null; double bestH = 0, bestScore = double.MaxValue;
                foreach (var cand in baseCandidates)
                {
                    double h = topLevel != null ? FtToMm(topLevel.Elevation - cand.Elevation) : heightMm;
                    double rh = h / Math.Max(1, n);
                    if (rh < riserMin || rh > riserMax) continue;
                    double score = Math.Abs(rh - riserTarget);
                    if (score < bestScore) { bestScore = score; bestLevel = cand; bestH = h; }
                }
                if (bestLevel == null)
                {
                    droppedImplausible += members.Count;
                    double h0 = topLevel != null ? FtToMm(topLevel.Elevation - baseCandidates[0].Elevation) : heightMm;
                    warnings.Add($"Chain of {members.Count} run(s) / {n} risers near ({Math.Round(members[0].Cx1)}, {Math.Round(members[0].Cy1)}) skipped: riser height {Math.Round(h0 / Math.Max(1, n))} mm for {Math.Round(h0)} mm level height" +
                                 (fromBelow ? " (no level below gives a plausible riser)." : " (a stair arriving from another level? use from_below=true)."));
                    continue;
                }
                chainList.Add(members); chainBase.Add(bestLevel); chainHeight.Add(bestH);
            }

            // ---- 4. order each chain bottom -> top -----------------------------------
            var stairsPlans = new List<List<RunPlan>>();
            var directionSource = new List<string>();
            var planBase = new List<Level>();
            var planHeight = new List<double>();
            for (int ci = 0; ci < chainList.Count; ci++)
            {
                var members = chainList[ci];
                if (members.Count > 4) { warnings.Add($"Chain of {members.Count} runs near ({Math.Round(members[0].Cx1)}, {Math.Round(members[0].Cy1)}) skipped (too many runs - overlapping stairs?)."); continue; }
                double tread0 = members.Average(r => r.TreadDepth);
                var ends = new List<(RunPlan r, bool second, double x, double y)>();
                foreach (var r in members) { ends.Add((r, false, r.Cx1, r.Cy1)); ends.Add((r, true, r.Cx2, r.Cy2)); }
                double Dist(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
                // free ends: connect run pairs by their closest end pair (landing); the rest are free
                var connected = new HashSet<int>();
                for (int i1 = 0; i1 < members.Count; i1++)
                    for (int i2 = i1 + 1; i2 < members.Count; i2++)
                    {
                        int bi = -1, bj = -1; double bd = landingMax;
                        for (int e1 = 0; e1 < 2; e1++)
                            for (int e2 = 0; e2 < 2; e2++)
                            {
                                var a = ends[2 * i1 + e1]; var b = ends[2 * i2 + e2];
                                double d = Dist(a.x, a.y, b.x, b.y);
                                if (d < bd) { bd = d; bi = 2 * i1 + e1; bj = 2 * i2 + e2; }
                            }
                        if (bi >= 0) { connected.Add(bi); connected.Add(bj); }
                    }
                var freeEnds = ends.Where((e, idx) => !connected.Contains(idx)).ToList();
                if (freeEnds.Count == 0) freeEnds = ends;
                bool OnChain(double x, double y) => members.Any(r => x >= r.MinX - 150 && x <= r.MaxX + 150 && y >= r.MinY - 150 && y <= r.MaxY + 150);
                // Stair path annotation: start P -> apex A. Apex at a chain end = the arrow points to
                // the top (UP style: Revit draws it for the stair going up / full-length paths). Apex
                // mid-run = the path starts at the TOP and points down (DN style: Revit draws it for
                // the stair arriving from below, ending a few treads above the bottom). Several paths
                // on one chain (a cut stair stacked on a complete one): the longest wins.
                (RunPlan r, bool second, double x, double y) top = default; bool haveTop = false; string src = null;
                var votes = new List<(double len, (RunPlan r, bool second, double x, double y) e, string kind)>();
                double endTol = 1.5 * tread0 + 150;
                foreach (var pt in paths)
                {
                    double px = pt[0], py = pt[1], ax = pt[2], ay = pt[3];
                    if (!OnChain(ax, ay) && !OnChain(px, py)) continue;
                    // the path starts at an end of the stair by construction - match against all run ends
                    var nearP = ends.OrderBy(e => Dist(e.x, e.y, px, py)).First();
                    if (Dist(nearP.x, nearP.y, px, py) > endTol) continue; // path does not start at this chain
                    var nearA = ends.Where(e => !(e.r == nearP.r && e.second == nearP.second)).OrderBy(e => Dist(e.x, e.y, ax, ay)).First();
                    bool apexAtEnd = Dist(nearA.x, nearA.y, ax, ay) <= endTol && !(nearA.r == nearP.r && nearA.second == nearP.second);
                    if (apexAtEnd) votes.Add((pt[4], nearA, "UP-style path (arrow at the top)"));
                    else votes.Add((pt[4], nearP, "DN-style path (starts at the top)"));
                }
                if (votes.Count > 0)
                {
                    var best = votes.OrderByDescending(v => v.len).First();
                    top = best.e; haveTop = true; src = best.kind;
                    if (votes.Any(v => v.e.r != best.e.r || v.e.second != best.e.second)) src += " (conflicting paths - check the direction)";
                }
                else if (arrows.Count > 0)
                {
                    // no path polyline: the free end nearest an arrowhead is the top (closed-triangle convention)
                    var near = arrows.Where(a => OnChain(a[0], a[1])).ToList();
                    if (near.Count > 0)
                    {
                        top = freeEnds.OrderBy(e => near.Min(a => Dist(a[0], a[1], e.x, e.y))).First(); haveTop = true;
                        src = "arrowhead";
                    }
                }
                if (!haveTop) { top = freeEnds[0]; src = "guess (no stair path found - check the direction)"; }
                // order: walk down from the top run
                var seed = top.r;
                seed.Reversed = !top.second;
                var ordered = new List<RunPlan> { seed };
                var remaining = members.Where(r => r != seed).ToList();
                while (remaining.Count > 0)
                {
                    var first = ordered[0];
                    RunPlan prv = null; bool prvTopIsSecond = false; double best = landingMax;
                    foreach (var r in remaining)
                    {
                        double d1 = Dist(r.Cx1, r.Cy1, first.SX, first.SY), d2 = Dist(r.Cx2, r.Cy2, first.SX, first.SY);
                        if (d1 < best) { best = d1; prv = r; prvTopIsSecond = false; }
                        if (d2 < best) { best = d2; prv = r; prvTopIsSecond = true; }
                    }
                    if (prv == null) break;
                    prv.Reversed = !prvTopIsSecond; ordered.Insert(0, prv); remaining.Remove(prv);
                }
                if (remaining.Count > 0) { warnings.Add($"Chain near ({Math.Round(members[0].Cx1)}, {Math.Round(members[0].Cy1)}): {remaining.Count} run(s) could not be ordered and were dropped."); }
                stairsPlans.Add(ordered);
                directionSource.Add(src);
                planBase.Add(chainBase[ci]);
                planHeight.Add(chainHeight[ci]);
            }

            // ---- 5. skip existing stairs on this level ------------------------------
            int skipped = 0;
            if (skipExisting)
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(Stairs)).Cast<Stairs>()
                    .Select(s => (lvl: s.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId(), bb: s.get_BoundingBox(null)))
                    .Where(x => x.bb != null).ToList();
                var keep = new List<int>();
                for (int i = 0; i < stairsPlans.Count; i++)
                {
                    var m = stairsPlans[i];
                    double minX = m.Min(r => r.MinX), maxX = m.Max(r => r.MaxX), minY = m.Min(r => r.MinY), maxY = m.Max(r => r.MaxY);
                    bool hit = existing.Any(x => x.lvl == planBase[i].Id &&
                                                 Math.Min(FtToMm(x.bb.Max.X), maxX) - Math.Max(FtToMm(x.bb.Min.X), minX) > 300 &&
                                                 Math.Min(FtToMm(x.bb.Max.Y), maxY) - Math.Max(FtToMm(x.bb.Min.Y), minY) > 300);
                    if (hit) skipped++; else keep.Add(i);
                }
                stairsPlans = keep.Select(i => stairsPlans[i]).ToList();
                directionSource = keep.Select(i => directionSource[i]).ToList();
                planBase = keep.Select(i => planBase[i]).ToList();
                planHeight = keep.Select(i => planHeight[i]).ToList();
            }

            // ---- 6. type ---------------------------------------------------------------
            var stairTypes = new FilteredElementCollector(doc).OfClass(typeof(StairsType)).Cast<StairsType>().ToList();
            if (stairTypes.Count == 0) throw new McpException(McpException.NotFound, "No stair types in the document.");
            var optType = GetOptLong(p, "type_id");
            var baseType = optType.HasValue ? doc.GetElement(new ElementId(optType.Value)) as StairsType ?? stairTypes[0]
                                            : stairTypes.OrderBy(t => (t.Name ?? "").IndexOf("AI", StringComparison.Ordinal) >= 0 ? 1 : 0).First();

            // ---- 7. create --------------------------------------------------------------
            var created = new List<Dictionary<string, object>>();
            var failures = new List<string>();
            var typesCreated = new List<string>();
            var typeCache = new Dictionary<string, StairsType>();
            for (int i = 0; i < stairsPlans.Count; i++)
            {
                var m = stairsPlans[i];
                int totalRisers = m.Sum(r => r.Risers);
                var stairBase = planBase[i];
                double stairHeight = planHeight[i];
                double riserH = stairHeight / Math.Max(1, totalRisers);
                double tread = m.Average(r => r.TreadDepth);
                var desc = new Dictionary<string, object>
                {
                    ["runs"] = m.Select(r => new Dictionary<string, object>
                    {
                        ["start"] = new[] { Math.Round(r.SX), Math.Round(r.SY) }, ["end"] = new[] { Math.Round(r.EX), Math.Round(r.EY) },
                        ["risers"] = r.Risers, ["width_mm"] = Math.Round(r.Width), ["tread_mm"] = Math.Round(r.TreadDepth, 1)
                    }).ToList(),
                    ["total_risers"] = totalRisers,
                    ["riser_height_mm"] = Math.Round(riserH, 1),
                    ["direction"] = directionSource[i],
                    ["base_level"] = stairBase.Name,
                    ["height_mm"] = Math.Round(stairHeight, 1),
                    ["bbox_mm"] = new[] { Math.Round(m.Min(r => r.MinX)), Math.Round(m.Min(r => r.MinY)), Math.Round(m.Max(r => r.MaxX)), Math.Round(m.Max(r => r.MaxY)) }
                };
                if (dryRun) { created.Add(desc); continue; }
                if (doc.IsModifiable)
                    throw new McpException(McpException.Unsupported, "Stairs need their own StairsEditScope and cannot be created inside an open transaction (call natively / execute_code with transaction=false).");
                try
                {
                    // one type per (tread depth, riser height) - min tread / max riser drive the run layout
                    var key = Math.Round(tread) + ":" + Math.Round(riserH);
                    if (!typeCache.TryGetValue(key, out var st))
                    {
                        var name = $"{baseType.Name} T{Math.Round(tread)} R{Math.Round(riserH)}{aiTag}";
                        st = stairTypes.FirstOrDefault(t => t.Name == name);
                        if (st == null)
                        {
                            using (var tt = new Transaction(doc, "MCP: stair type"))
                            {
                                tt.Start();
                                st = baseType.Duplicate(name) as StairsType;
                                st.MinTreadDepth = MmToFt(tread);
                                st.MaxRiserHeight = MmToFt(riserH + 1);
                                MarkAiType(st, "create_stairs_from_cad", baseType.Name);
                                tt.Commit();
                            }
                            stairTypes.Add(st);
                            typesCreated.Add(name);
                        }
                        typeCache[key] = st;
                    }
                    ElementId stairsId;
                    using (var scope = new StairsEditScope(doc, "MCP: stairs from CAD"))
                    {
                        stairsId = scope.Start(stairBase.Id, topLevel?.Id ?? stairBase.Id);
                        using (var t = new Transaction(doc, "MCP: stair runs"))
                        {
                            t.Start();
                            var stairs = doc.GetElement(stairsId) as Stairs;
                            if (stairs != null)
                            {
                                try { stairs.ChangeTypeId(st.Id); } catch { }
                                try { stairs.DesiredRisersNumber = totalRisers; } catch { }
                                if (topLevel == null)
                                {
                                    var th = stairs.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM);
                                    var off = stairs.get_Parameter(BuiltInParameter.STAIRS_TOP_OFFSET);
                                    th?.Set(stairBase.Id); off?.Set(MmToFt(heightMm));
                                }
                            }
                            double z = stairBase.Elevation;
                            StairsRun prevRun = null;
                            foreach (var r in m)
                            {
                                var line = Line.CreateBound(new XYZ(MmToFt(r.SX), MmToFt(r.SY), z), new XYZ(MmToFt(r.EX), MmToFt(r.EY), z));
                                var run = StairsRun.CreateStraightRun(doc, stairsId, line, StairsRunJustification.Center);
                                try { run.ActualRunWidth = MmToFt(r.Width); } catch { }
                                if (prevRun != null)
                                {
                                    try { StairsLanding.CreateAutomaticLanding(doc, prevRun.Id, run.Id); }
                                    catch (Exception ex) { warnings.Add($"Landing between runs failed ({ex.Message}); runs left unconnected."); }
                                }
                                doc.Regenerate();
                                z = stairBase.Elevation + run.TopElevation; // run elevations are relative to the stairs base
                                prevRun = run;
                            }
                            t.Commit();
                        }
                        scope.Commit(new SilentStairsFailures());
                    }
                    var el = doc.GetElement(stairsId) as Stairs;
                    desc["id"] = stairsId.Value;
                    if (el != null) { desc["actual_risers"] = el.ActualRisersNumber; desc["actual_riser_height_mm"] = Math.Round(FtToMm(el.ActualRiserHeight), 1); }
                    created.Add(desc);
                }
                catch (Exception ex)
                {
                    failures.Add($"stair near ({Math.Round(m[0].SX)}, {Math.Round(m[0].SY)}): {ex.Message}");
                }
            }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun,
                ["link"] = ImportName(doc, li),
                ["layers"] = layers.ToList(),
                ["segments"] = segs.Count,
                ["arrowheads"] = arrows.Count,
                ["paths"] = paths.Count,
                ["arrows_detail"] = dryRun ? arrows.Select(q => new[] { Math.Round(q[0]), Math.Round(q[1]), Math.Round(q[2], 2), Math.Round(q[3], 2) }).ToList() : null,
                ["path_polys"] = dryRun ? pathPolys.Select(q => new[] { q.Count, Math.Round(q[0][0]), Math.Round(q[0][1]), Math.Round(q[1][0]), Math.Round(q[1][1]), Math.Round(q[q.Count - 1][0]), Math.Round(q[q.Count - 1][1]) }).ToList() : null,
                ["paths_detail"] = dryRun ? paths.Select(q => new[] { Math.Round(q[0]), Math.Round(q[1]), Math.Round(q[2]), Math.Round(q[3]), Math.Round(q[4]) }).ToList() : null,
                ["runs_detail"] = dryRun ? runs.Select(r => new Dictionary<string, object> { ["c1"] = new[] { Math.Round(r.Cx1), Math.Round(r.Cy1) }, ["c2"] = new[] { Math.Round(r.Cx2), Math.Round(r.Cy2) }, ["risers"] = r.Risers, ["width"] = Math.Round(r.Width), ["cut"] = r.Cut }).ToList() : null,
                ["break_lines"] = zigzags.Count,
                ["segments_raw"] = rawSegs,
                ["runs_dropped_cut"] = droppedCut,
                ["runs_dropped_implausible"] = droppedImplausible,
                ["runs_found"] = runs.Count,
                ["stairs_planned"] = stairsPlans.Count,
                ["stairs_created"] = dryRun ? 0 : created.Count(d => d.ContainsKey("id")),
                ["skipped_existing"] = skipped,
                ["base_level"] = fromBelow && baseCandidates.Count > 1 ? "auto (per stair)" : baseLevel.Name,
                ["from_below"] = fromBelow,
                ["top_level"] = topLevel?.Name,
                ["height_mm"] = Math.Round(totalHeight, 1),
                ["stairs"] = created,
                ["types_created"] = typesCreated,
                ["failures"] = failures,
                ["warnings"] = warnings
            };
        }

        private sealed class SilentStairsFailures : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }
    }
}
