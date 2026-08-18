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
            var topLevelId = GetOptLong(p, "top_level_id");
            var topLevel = topLevelId.HasValue ? doc.GetElement(new ElementId(topLevelId.Value)) as Level : null;
            double heightMm = GetDoubleOr(p, "height_mm", 0);
            if (topLevel == null && heightMm <= 0)
            {
                // default: the next level up
                topLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => l.Elevation > level.Elevation + 1e-6).OrderBy(l => l.Elevation).FirstOrDefault();
                if (topLevel == null) throw new McpException(McpException.BadRequest, "Give 'top_level_id' or 'height_mm' (no level above the base level).");
            }
            double totalHeight = topLevel != null ? FtToMm(topLevel.Elevation - level.Elevation) : heightMm;
            var bbox = GetOptBboxMm(p);
            double treadMin = GetDoubleOr(p, "tread_min_mm", 220), treadMax = GetDoubleOr(p, "tread_max_mm", 420);
            double widthMin = GetDoubleOr(p, "width_min_mm", 600), widthMax = GetDoubleOr(p, "width_max_mm", 3500);
            int minTreads = GetIntOr(p, "min_treads", 3);
            double landingMax = GetDoubleOr(p, "landing_max_mm", 3000);
            double angleTol = GetDoubleOr(p, "angle_tolerance_deg", 1.0);
            bool skipExisting = GetBoolOr(p, "skip_existing", true);
            string aiTag = GetOptString(p, "ai_tag") ?? "_AI";
            var arrowLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (p.ContainsKey("arrow_layers") && p["arrow_layers"] is IEnumerable al && !(p["arrow_layers"] is string))
                foreach (var o in al) if (o != null) arrowLayers.Add(Convert.ToString(o));
            var warnings = new List<string>();

            // ---- 1. segments + arrowheads -------------------------------------
            var segs = new List<double[]>();      // x1,y1,x2,y2,len,angle(0..180)
            var arrowHeads = new List<double[]>();  // x,y of small closed triangles
            void AddSeg(double ax, double ay, double bx, double by)
            {
                var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (len < widthMin || len > widthMax) return;
                if (bbox != null && !InBbox(bbox, (ax + bx) / 2, (ay + by) / 2)) return;
                var ang = Math.Atan2(by - ay, bx - ax) * 180 / Math.PI; if (ang < 0) ang += 180; if (ang >= 180 - 1e-9) ang -= 180;
                segs.Add(new[] { ax, ay, bx, by, len, ang });
            }
            foreach (var prim in EnumerateCad(doc, li))
            {
                bool onStair = layers.Contains(prim.Layer), onArrow = arrowLayers.Contains(prim.Layer);
                if (!onStair && !onArrow) continue;
                switch (prim.Geo)
                {
                    case Line ln when onStair:
                        AddSeg(FtToMm(ln.GetEndPoint(0).X), FtToMm(ln.GetEndPoint(0).Y), FtToMm(ln.GetEndPoint(1).X), FtToMm(ln.GetEndPoint(1).Y));
                        break;
                    case PolyLine pl:
                    {
                        var pts = pl.GetCoordinates().Select(q => new[] { FtToMm(q.X), FtToMm(q.Y) }).ToList();
                        bool closed = pts.Count > 1 && Math.Abs(pts[0][0] - pts[pts.Count - 1][0]) < 1 && Math.Abs(pts[0][1] - pts[pts.Count - 1][1]) < 1;
                        var core = closed ? pts.Take(pts.Count - 1).ToList() : pts;
                        // arrowhead: a small closed triangle
                        if (core.Count == 3 && (closed || pts.Count == 3))
                        {
                            double ex = core.Max(q => q[0]) - core.Min(q => q[0]), ey = core.Max(q => q[1]) - core.Min(q => q[1]);
                            if (Math.Max(ex, ey) <= 600 && Math.Max(ex, ey) >= 40)
                            {
                                var hx = core.Average(q => q[0]); var hy = core.Average(q => q[1]);
                                if (bbox == null || InBbox(bbox, hx, hy)) arrowHeads.Add(new[] { hx, hy });
                                break;
                            }
                        }
                        if (onStair)
                            for (int i = 1; i < pts.Count; i++) AddSeg(pts[i - 1][0], pts[i - 1][1], pts[i][0], pts[i][1]);
                        break;
                    }
                }
            }
            if (segs.Count < minTreads)
                throw new McpException(McpException.NotFound, $"No stair tread lines on layer(s) {string.Join(", ", layers)}" + (bbox != null ? " inside bbox_mm." : "."));

            // ---- 2. combs -> runs ---------------------------------------------
            var runs = new List<RunPlan>();
            var used = new bool[segs.Count];
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
                    // path spans one tread beyond the last nosing (the top riser) - the
                    // riser count equals the number of lines
                    double o1 = offs.First(), o2 = offs.Last() + depth;
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
                    runs.Add(run);
                }
            }
            if (runs.Count == 0)
                throw new McpException(McpException.NotFound, "No tread combs (>= min_treads parallel, equally spaced lines) found on the stair layer(s).");

            // ---- 3. chain runs into stairs (ends close together = landing) --------
            double EndDist(RunPlan a, RunPlan b)
            {
                double D(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
                return Math.Min(Math.Min(D(a.Cx1, a.Cy1, b.Cx1, b.Cy1), D(a.Cx1, a.Cy1, b.Cx2, b.Cy2)),
                                Math.Min(D(a.Cx2, a.Cy2, b.Cx1, b.Cy1), D(a.Cx2, a.Cy2, b.Cx2, b.Cy2)));
            }
            bool Overlap(RunPlan a, RunPlan b) =>
                Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX) > 200 && Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY) > 200;
            int chains = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].Chain >= 0) continue;
                runs[i].Chain = chains;
                var stack = new Stack<int>(); stack.Push(i);
                while (stack.Count > 0)
                {
                    var a = stack.Pop();
                    for (int j = 0; j < runs.Count; j++)
                    {
                        if (runs[j].Chain >= 0 || Overlap(runs[a], runs[j])) continue;
                        if (EndDist(runs[a], runs[j]) <= landingMax) { runs[j].Chain = chains; stack.Push(j); }
                    }
                }
                chains++;
            }

            // ---- 4. order each chain bottom -> top -----------------------------------
            var stairsPlans = new List<List<RunPlan>>();
            var directionSource = new List<string>();
            for (int c = 0; c < chains; c++)
            {
                var members = runs.Where(r => r.Chain == c).ToList();
                if (members.Count > 4) { warnings.Add($"Chain of {members.Count} runs near ({Math.Round(members[0].Cx1)}, {Math.Round(members[0].Cy1)}) skipped (too many runs - overlapping stairs?)."); continue; }
                // find the free end (an end not close to any other run's end): candidates for bottom/top
                var ends = new List<(RunPlan r, bool second, double x, double y)>();
                foreach (var r in members) { ends.Add((r, false, r.Cx1, r.Cy1)); ends.Add((r, true, r.Cx2, r.Cy2)); }
                bool Free((RunPlan r, bool second, double x, double y) e) =>
                    !ends.Any(o => o.r != e.r && Math.Sqrt((o.x - e.x) * (o.x - e.x) + (o.y - e.y) * (o.y - e.y)) <= landingMax);
                var freeEnds = ends.Where(Free).ToList();
                (RunPlan r, bool second, double x, double y) top;
                string src;
                double minX = members.Min(m => m.MinX) - 1500, maxX = members.Max(m => m.MaxX) + 1500, minY = members.Min(m => m.MinY) - 1500, maxY = members.Max(m => m.MaxY) + 1500;
                var heads = arrowHeads.Where(h => h[0] >= minX && h[0] <= maxX && h[1] >= minY && h[1] <= maxY).ToList();
                var pool = freeEnds.Count > 0 ? freeEnds : ends;
                if (heads.Count > 0)
                {
                    top = pool.OrderBy(e => heads.Min(h => Math.Sqrt((h[0] - e.x) * (h[0] - e.x) + (h[1] - e.y) * (h[1] - e.y)))).First();
                    src = "arrowhead";
                }
                else { top = pool.First(); src = "guess (no arrowhead found - check the direction)"; }
                // walk from the top run backwards to order the chain
                var ordered = new List<RunPlan>();
                var remaining = new List<RunPlan>(members);
                var curRun = top.r; bool curTopIsSecond = top.second;
                while (curRun != null)
                {
                    remaining.Remove(curRun);
                    curRun.Reversed = !curTopIsSecond;      // ensure E = top end
                    ordered.Insert(0, curRun);
                    // previous run: the one whose end is nearest to this run's START
                    double sx = curRun.SX, sy = curRun.SY;
                    RunPlan prev = null; bool prevTopIsSecond = false; double best = landingMax;
                    foreach (var r in remaining)
                    {
                        double d1 = Math.Sqrt((r.Cx1 - sx) * (r.Cx1 - sx) + (r.Cy1 - sy) * (r.Cy1 - sy));
                        double d2 = Math.Sqrt((r.Cx2 - sx) * (r.Cx2 - sx) + (r.Cy2 - sy) * (r.Cy2 - sy));
                        if (d1 < best) { best = d1; prev = r; prevTopIsSecond = false; }
                        if (d2 < best) { best = d2; prev = r; prevTopIsSecond = true; }
                    }
                    curRun = prev; curTopIsSecond = prevTopIsSecond;
                }
                if (remaining.Count > 0) { warnings.Add($"Chain near ({Math.Round(members[0].Cx1)}, {Math.Round(members[0].Cy1)}): {remaining.Count} run(s) could not be ordered and were dropped."); }
                stairsPlans.Add(ordered);
                directionSource.Add(src);
            }

            // ---- 5. skip existing stairs on this level ------------------------------
            int skipped = 0;
            if (skipExisting)
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(Stairs)).Cast<Stairs>()
                    .Where(s => s.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() == level.Id)
                    .Select(s => s.get_BoundingBox(null)).Where(b => b != null).ToList();
                var keep = new List<int>();
                for (int i = 0; i < stairsPlans.Count; i++)
                {
                    var m = stairsPlans[i];
                    double minX = m.Min(r => r.MinX), maxX = m.Max(r => r.MaxX), minY = m.Min(r => r.MinY), maxY = m.Max(r => r.MaxY);
                    bool hit = existing.Any(b => Math.Min(FtToMm(b.Max.X), maxX) - Math.Max(FtToMm(b.Min.X), minX) > 300 &&
                                                 Math.Min(FtToMm(b.Max.Y), maxY) - Math.Max(FtToMm(b.Min.Y), minY) > 300);
                    if (hit) skipped++; else keep.Add(i);
                }
                stairsPlans = keep.Select(i => stairsPlans[i]).ToList();
                directionSource = keep.Select(i => directionSource[i]).ToList();
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
                double riserH = totalHeight / Math.Max(1, totalRisers);
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
                        stairsId = scope.Start(level.Id, topLevel?.Id ?? level.Id);
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
                                    th?.Set(level.Id); off?.Set(MmToFt(heightMm));
                                }
                            }
                            double z = level.Elevation;
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
                                z = run.TopElevation;
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
                ["arrowheads"] = arrowHeads.Count,
                ["runs_found"] = runs.Count,
                ["stairs_planned"] = stairsPlans.Count,
                ["stairs_created"] = dryRun ? 0 : created.Count(d => d.ContainsKey("id")),
                ["skipped_existing"] = skipped,
                ["base_level"] = level.Name,
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
