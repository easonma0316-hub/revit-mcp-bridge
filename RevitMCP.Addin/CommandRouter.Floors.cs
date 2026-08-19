using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Floors:
    ///  - create_floor_from_walls: the building footprint of a level, derived from the walls
    ///    already modelled on it (wall footprints inflated by `gap_mm`, unioned as solids; the
    ///    outer loop of each connected part, deflated again, becomes a floor). Architectural
    ///    plans rarely draw slab outlines - the outer wall faces are the slab edge.
    ///  - create_floors_from_cad: closed polylines (or closed chains of lines) on CAD layer(s)
    ///    become floors; a closed loop lying inside another one becomes a hole in it.
    /// </summary>
    public static partial class CommandRouter
    {
        private static Dictionary<string, object> CreateFloorFromWalls(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", true);
            if (!dryRun) EnsureWritable();
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            double gap = GetDoubleOr(p, "gap_mm", 150);            // closes gaps between walls up to 2*gap (unmodelled doors/curtain walls)
            double minArea = GetDoubleOr(p, "min_area_m2", 10);    // parts smaller than this (a lone wall stub) are skipped
            double minHole = GetDoubleOr(p, "min_hole_area_m2", 0); // 0 = no holes; else enclosed regions >= this become openings (shafts)
            double maxHole = GetDoubleOr(p, "max_hole_area_m2", 40);
            bool exteriorOnly = GetBoolOr(p, "exterior_only", false);
            string aiTag = GetOptString(p, "ai_tag") ?? "_AI";
            var bbox = GetOptBboxMm(p);
            var warnings = new List<string>();

            // ---- 1. walls on the level --------------------------------------------------
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.LevelId == level.Id)
                .ToList();
            if (p.ContainsKey("wall_ids") && p["wall_ids"] is IEnumerable wi && !(p["wall_ids"] is string))
            {
                var set = new HashSet<long>();
                foreach (var o in wi) if (o != null) set.Add(Convert.ToInt64(o));
                walls = walls.Where(w => set.Contains(w.Id.Value)).ToList();
            }
            if (exteriorOnly)
                walls = walls.Where(w => w.WallType.Function == WallFunction.Exterior).ToList();
            if (bbox != null)
                walls = walls.Where(w =>
                {
                    var lc = (w.Location as LocationCurve)?.Curve; if (lc == null) return false;
                    var m = lc.Evaluate(0.5, true); return InBbox(bbox, FtToMm(m.X), FtToMm(m.Y));
                }).ToList();
            if (walls.Count == 0) throw new McpException(McpException.NotFound, "No walls on this level (create walls first).");

            // ---- 2. inflated wall footprints -> union solid --------------------------------
            double z0 = level.Elevation, h = MmToFt(100);
            Solid acc = null; int unioned = 0, failed = 0;
            foreach (var w in walls)
            {
                var lc = (w.Location as LocationCurve)?.Curve;
                if (lc == null) continue;
                try
                {
                    Curve c = lc;
                    if (c is Line ln && gap > 0)
                    {
                        // extend the ends by gap so walls meeting at corners / across small gaps fuse
                        var d = ln.Direction; var e = MmToFt(gap);
                        c = Line.CreateBound(ln.GetEndPoint(0) - d * e, ln.GetEndPoint(1) + d * e);
                    }
                    // flatten to the level plane
                    c = c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, z0 - c.GetEndPoint(0).Z)));
                    var loop = CurveLoop.CreateViaThicken(c, w.Width + 2 * MmToFt(gap), XYZ.BasisZ);
                    var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h);
                    if (acc == null) { acc = solid; unioned++; continue; }
                    try
                    {
                        acc = BooleanOperationsUtils.ExecuteBooleanOperation(acc, solid, BooleanOperationsType.Union);
                        unioned++;
                    }
                    catch
                    {
                        // retry with a slightly different inflation (coincident faces upset the boolean)
                        var loop2 = CurveLoop.CreateViaThicken(c, w.Width + 2 * MmToFt(gap) + MmToFt(3), XYZ.BasisZ);
                        var solid2 = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop2 }, XYZ.BasisZ, h * 1.01);
                        try { acc = BooleanOperationsUtils.ExecuteBooleanOperation(acc, solid2, BooleanOperationsType.Union); unioned++; }
                        catch (Exception ex2) { failed++; if (failed <= 5) warnings.Add($"wall {w.Id.Value} could not be unioned: {ex2.Message}"); }
                    }
                }
                catch (Exception ex) { failed++; if (failed <= 5) warnings.Add($"wall {w.Id.Value}: {ex.Message}"); }
            }
            // optional CAD lines (curtain walls, shopfronts, storefront glazing - anything that closes
            // the outline but was not modelled as walls) join the union as thin strips
            int cadSegs = 0;
            var optLink = GetOptLong(p, "link_id");
            var cadLayers = GetLayerSet(p);
            if (optLink.HasValue && cadLayers.Count > 0)
            {
                var li = RequireImport(doc, optLink.Value);
                double cadTh = MmToFt(GetDoubleOr(p, "cad_thickness_mm", 100)) + 2 * MmToFt(gap);
                void AddStrip(Curve c)
                {
                    try
                    {
                        if (c.Length < MmToFt(50)) return;
                        if (bbox != null) { var m = c.Evaluate(0.5, true); if (!InBbox(bbox, FtToMm(m.X), FtToMm(m.Y))) return; }
                        if (c is Line ln && gap > 0) c = Line.CreateBound(ln.GetEndPoint(0) - ln.Direction * MmToFt(gap), ln.GetEndPoint(1) + ln.Direction * MmToFt(gap));
                        c = c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, z0 - c.GetEndPoint(0).Z)));
                        var loop = CurveLoop.CreateViaThicken(c, cadTh, XYZ.BasisZ);
                        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h);
                        if (acc == null) acc = solid;
                        else acc = BooleanOperationsUtils.ExecuteBooleanOperation(acc, solid, BooleanOperationsType.Union);
                        cadSegs++;
                    }
                    catch (Exception ex) { failed++; if (failed <= 5) warnings.Add($"CAD segment could not be unioned: {ex.Message}"); }
                }
                foreach (var prim in EnumerateCad(doc, li))
                {
                    if (!cadLayers.Contains(prim.Layer)) continue;
                    switch (prim.Geo)
                    {
                        case Line ln: AddStrip(ln); break;
                        case Arc arc when arc.IsBound: AddStrip(arc); break;
                        case PolyLine pl:
                        {
                            var pts = pl.GetCoordinates();
                            for (int i = 1; i < pts.Count; i++) if (pts[i].DistanceTo(pts[i - 1]) > MmToFt(1)) AddStrip(Line.CreateBound(pts[i - 1], pts[i]));
                            break;
                        }
                    }
                }
            }
            if (acc == null) throw new McpException(McpException.Unsupported, "No wall footprint could be built.");

            // ---- 3. bottom faces -> outer loops (deflated by gap) ----------------------------
            double LoopArea(CurveLoop cl)
            {
                double a = 0;
                foreach (var c in cl)
                {
                    var pts = c.Tessellate();
                    for (int i = 0; i + 1 < pts.Count; i++) a += pts[i].X * pts[i + 1].Y - pts[i + 1].X * pts[i].Y;
                }
                return Math.Abs(a) / 2;
            }
            CurveLoop Offset(CurveLoop cl, double dist)
            {
                // CreateViaOffset sign depends on loop orientation: take the result whose area shrinks
                // (and only accept a result that is actually smaller than the input)
                cl = SimplifyLoop(cl, MmToFt(20), z0);
                double a0 = LoopArea(cl);
                CurveLoop best = null; double bestA = a0;
                foreach (var sgn in new[] { 1.0, -1.0 })
                {
                    try
                    {
                        var o = CurveLoop.CreateViaOffset(cl, sgn * dist, XYZ.BasisZ);
                        var a = LoopArea(o);
                        if (a < bestA) { bestA = a; best = o; }
                    }
                    catch { }
                }
                if (best != null) return best;
                // Revit's offset gives up on long noisy loops: mitred inward offset of the polygon
                var m = MiterOffsetInward(cl, dist, z0);
                if (m != null && LoopArea(m) < a0) return m;
                return null;
            }
            var floorsPlanned = new List<(CurveLoop outer, List<CurveLoop> holes, double areaM2)>();
            var rawOuter = new List<CurveLoop>(); // un-deflated outlines (fallback when the deflated loop is invalid)
            foreach (Face f in acc.Faces)
            {
                var pf = f as PlanarFace;
                if (pf == null || pf.FaceNormal.Z > -0.9) continue; // bottom faces only
                var loops = pf.GetEdgesAsCurveLoops().ToList();
                if (loops.Count == 0) continue;
                var outer = loops.OrderByDescending(LoopArea).First();
                double areaFt = LoopArea(outer);
                double areaM2 = areaFt * 0.09290304;
                if (areaM2 < minArea) continue;
                var deflated = gap > 0 ? Offset(outer, MmToFt(gap)) ?? outer : outer;
                deflated = SimplifyLoop(deflated, MmToFt(20), z0);
                var holes = new List<CurveLoop>();
                if (minHole > 0)
                    foreach (var il in loops)
                    {
                        if (il == outer) continue;
                        double am2 = LoopArea(il) * 0.09290304;
                        if (am2 < minHole || am2 > maxHole) continue;
                        // holes grow back by gap (Offset() picks the smaller area; for a hole we want the larger)
                        CurveLoop grown = null; double ga = -1;
                        foreach (var sgn in new[] { 1.0, -1.0 })
                        {
                            try { var o = CurveLoop.CreateViaOffset(il, sgn * MmToFt(gap), XYZ.BasisZ); var a = LoopArea(o); if (a > ga) { ga = a; grown = o; } } catch { }
                        }
                        holes.Add(SimplifyLoop(grown ?? il, MmToFt(20), z0));
                    }
                if (deflated == outer && gap > 0) warnings.Add($"footprint part ({Math.Round(areaM2)} m2): could not deflate the outline by gap_mm - it is {gap} mm too big all round.");
                floorsPlanned.Add((deflated, holes, LoopArea(deflated) * 0.09290304));
                rawOuter.Add(SimplifyLoop(outer, MmToFt(20), z0));
            }
            if (floorsPlanned.Count == 0) throw new McpException(McpException.NotFound, $"No footprint part >= min_area_m2 ({minArea}).");

            // ---- 4. type ------------------------------------------------------------------
            var optType = GetOptLong(p, "type_id");
            string typeName = GetOptString(p, "type_name");
            var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
            FloorType ft = null;
            if (optType.HasValue) ft = doc.GetElement(new ElementId(optType.Value)) as FloorType;
            else if (!string.IsNullOrEmpty(typeName)) ft = floorTypes.FirstOrDefault(t => t.Name == typeName) ?? floorTypes.FirstOrDefault(t => t.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (ft == null) { var def = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType)) as FloorType; ft = def ?? floorTypes.FirstOrDefault(); }
            if (ft == null) throw new McpException(McpException.NotFound, "No floor type in the document.");
            double? thick = p.ContainsKey("thickness_mm") ? GetDoubleOr(p, "thickness_mm", 0) : (double?)null;

            // ---- 5. create --------------------------------------------------------------------
            var created = new List<Dictionary<string, object>>();
            var failures = new List<string>();
            string typeCreated = null;
            var ownTx = !dryRun && !doc.IsModifiable;
            var t = ownTx ? new Transaction(doc, "MCP: floor from walls") : null;
            try
            {
                if (ownTx) { t.Start(); var fh = t.GetFailureHandlingOptions(); fh.SetFailuresPreprocessor(new SilentFailures()); t.SetFailureHandlingOptions(fh); }
                if (!dryRun && thick.HasValue && thick.Value > 0)
                {
                    var cs = ft.GetCompoundStructure();
                    double cur = cs != null ? FtToMm(cs.GetWidth()) : 0;
                    if (Math.Abs(cur - thick.Value) > 0.5)
                    {
                        var name = $"{ft.Name} {Math.Round(thick.Value)}mm{aiTag}";
                        var existing = floorTypes.FirstOrDefault(x => x.Name == name);
                        if (existing != null) ft = existing;
                        else
                        {
                            var nt = ft.Duplicate(name) as FloorType;
                            var ncs = nt.GetCompoundStructure();
                            if (ncs != null)
                            {
                                int core = ncs.GetFirstCoreLayerIndex(); if (core < 0) core = 0;
                                double others = 0; for (int i = 0; i < ncs.LayerCount; i++) if (i != core) others += ncs.GetLayerWidth(i);
                                ncs.SetLayerWidth(core, Math.Max(MmToFt(1), MmToFt(thick.Value) - others));
                                nt.SetCompoundStructure(ncs);
                            }
                            MarkAiType(nt, "create_floor_from_walls", ft.Name);
                            ft = nt; typeCreated = name;
                        }
                    }
                }
                for (int fi = 0; fi < floorsPlanned.Count; fi++)
                {
                    var fp = floorsPlanned[fi];
                    var desc = new Dictionary<string, object>
                    {
                        ["area_m2"] = Math.Round(fp.areaM2, 1),
                        ["holes"] = fp.holes.Count,
                        ["vertices"] = fp.outer.Count(),
                        ["bbox_mm"] = LoopBboxMm(fp.outer),
                    };
                    if (dryRun) { desc["outline_mm"] = LoopPointsMm(fp.outer); created.Add(desc); continue; }
                    try
                    {
                        var loops = new List<CurveLoop> { fp.outer }; loops.AddRange(fp.holes);
                        var fl = Floor.Create(doc, loops, ft.Id, level.Id);
                        desc["id"] = fl.Id.Value; desc["type"] = ft.Name;
                        created.Add(desc);
                    }
                    catch (Exception ex)
                    {
                        // holes may self-intersect with the outer loop: retry without them; a deflated
                        // outline can self-intersect too: retry with the raw (gap_mm too big) outline
                        Floor fl = null; string note = null;
                        try { fl = Floor.Create(doc, new List<CurveLoop> { fp.outer }, ft.Id, level.Id); note = "holes dropped: " + ex.Message; }
                        catch
                        {
                            try { fl = Floor.Create(doc, new List<CurveLoop> { rawOuter[fi] }, ft.Id, level.Id); note = $"deflated outline invalid - used the raw outline ({gap} mm too big all round): " + ex.Message; }
                            catch (Exception ex3) { failures.Add($"floor ({Math.Round(fp.areaM2)} m2): {ex3.Message}"); }
                        }
                        if (fl != null) { desc["id"] = fl.Id.Value; desc["type"] = ft.Name; desc["holes"] = 0; desc["note"] = note; created.Add(desc); }
                    }
                }
                if (ownTx) CommitOrThrow(t, "floors");
            }
            finally { if (ownTx && t.HasStarted() && !t.HasEnded()) t.RollBack(); t?.Dispose(); }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun,
                ["level"] = level.Name,
                ["walls"] = walls.Count,
                ["walls_unioned"] = unioned,
                ["walls_failed"] = failed,
                ["cad_segments"] = cadSegs,
                ["gap_mm"] = gap,
                ["floors_planned"] = floorsPlanned.Count,
                ["floors_created"] = dryRun ? 0 : created.Count(d => d.ContainsKey("id")),
                ["floor_type"] = ft.Name,
                ["type_created"] = typeCreated,
                ["floors"] = created,
                ["failures"] = failures,
                ["warnings"] = warnings,
            };
        }

        /// <summary>Polyline loop -> drop tiny edges, merge collinear neighbours (Floor.Create chokes on noisy loops).</summary>
        private static CurveLoop SimplifyLoop(CurveLoop cl, double minEdgeFt, double z)
        {
            var pts = new List<XYZ>();
            bool allLines = cl.All(c => c is Line);
            if (!allLines) return cl;
            foreach (var c in cl) pts.Add(c.GetEndPoint(0));
            // drop short edges
            bool changed = true;
            while (changed && pts.Count > 3)
            {
                changed = false;
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    if (a.DistanceTo(b) < minEdgeFt) { pts.RemoveAt((i + 1) % pts.Count); changed = true; break; }
                }
                for (int i = 0; i < pts.Count && !changed; i++)
                {
                    var a = pts[(i + pts.Count - 1) % pts.Count]; var b = pts[i]; var c = pts[(i + 1) % pts.Count];
                    var u = (b - a); var v = (c - b);
                    if (u.GetLength() < 1e-9 || v.GetLength() < 1e-9) { pts.RemoveAt(i); changed = true; break; }
                    var cr = u.Normalize().CrossProduct(v.Normalize()).GetLength();
                    var dot = u.Normalize().DotProduct(v.Normalize());
                    if (cr < 0.002 && dot > 0) { pts.RemoveAt(i); changed = true; break; } // collinear
                }
            }
            if (pts.Count < 3) return cl;
            var o = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
                o.Append(Line.CreateBound(new XYZ(pts[i].X, pts[i].Y, z), new XYZ(pts[(i + 1) % pts.Count].X, pts[(i + 1) % pts.Count].Y, z)));
            return o;
        }

        /// <summary>Inward mitred offset of a polygonal loop (all lines); null if not a polygon.</summary>
        private static CurveLoop MiterOffsetInward(CurveLoop cl, double dist, double z)
        {
            if (!cl.All(c => c is Line)) return null;
            var pts = cl.Select(c => c.GetEndPoint(0)).ToList();
            int n = pts.Count; if (n < 3) return null;
            double signed = 0;
            for (int i = 0; i < n; i++) { var a = pts[i]; var b = pts[(i + 1) % n]; signed += a.X * b.Y - b.X * a.Y; }
            double side = signed > 0 ? 1 : -1; // CCW: inward = left normal
            var outPts = new List<XYZ>();
            for (int i = 0; i < n; i++)
            {
                var p0 = pts[(i + n - 1) % n]; var p1 = pts[i]; var p2 = pts[(i + 1) % n];
                var d1 = (p1 - p0); var d2 = (p2 - p1);
                if (d1.GetLength() < 1e-9 || d2.GetLength() < 1e-9) continue;
                d1 = d1.Normalize(); d2 = d2.Normalize();
                var n1 = new XYZ(-d1.Y * side, d1.X * side, 0); var n2 = new XYZ(-d2.Y * side, d2.X * side, 0);
                // offset lines: (p0 + n1*dist) + t*d1  and (p1 + n2*dist) + u*d2 ; intersect
                var a = p1 + n1 * dist; var b = p1 + n2 * dist;
                double cross = d1.X * d2.Y - d1.Y * d2.X;
                XYZ q;
                if (Math.Abs(cross) < 1e-6) q = a;
                else
                {
                    // solve a + t*d1 = b + u*d2
                    double t = ((b.X - a.X) * d2.Y - (b.Y - a.Y) * d2.X) / cross;
                    q = a + d1 * t;
                    // cap the miter length for very sharp corners
                    if (q.DistanceTo(p1) > 4 * dist) q = p1 + (n1 + n2).Normalize() * dist;
                }
                outPts.Add(new XYZ(q.X, q.Y, z));
            }
            if (outPts.Count < 3) return null;
            var o = new CurveLoop();
            for (int i = 0; i < outPts.Count; i++)
            {
                var a = outPts[i]; var b = outPts[(i + 1) % outPts.Count];
                if (a.DistanceTo(b) < 1e-6) continue;
                o.Append(Line.CreateBound(a, b));
            }
            return o;
        }

        private static List<double[]> LoopPointsMm(CurveLoop cl)
        {
            var r = new List<double[]>();
            foreach (var c in cl) r.Add(new[] { Math.Round(FtToMm(c.GetEndPoint(0).X)), Math.Round(FtToMm(c.GetEndPoint(0).Y)) });
            return r;
        }

        private static double[] LoopBboxMm(CurveLoop cl)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var c in cl)
                foreach (var q in c.Tessellate())
                {
                    minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
                }
            return new[] { Math.Round(FtToMm(minX)), Math.Round(FtToMm(minY)), Math.Round(FtToMm(maxX)), Math.Round(FtToMm(maxY)) };
        }

        // ------------------------------------------------------------------------------------
        private static Dictionary<string, object> CreateFloorsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", true);
            if (!dryRun) EnsureWritable();
            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0) throw new McpException(McpException.BadRequest, "Provide the floor outline layer(s) via 'layer' or 'layers'.");
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            double minArea = GetDoubleOr(p, "min_area_m2", 1);
            double joinTol = GetDoubleOr(p, "join_tolerance_mm", 10);
            bool chainLines = GetBoolOr(p, "chain_lines", true);
            bool holesFromInner = GetBoolOr(p, "inner_loops_as_holes", true);
            var bbox = GetOptBboxMm(p);
            var warnings = new List<string>();

            // ---- 1. closed loops: closed polylines + closed chains of lines/arcs/open polylines ----
            var loops = new List<List<Curve>>();
            var open = new List<Curve>();
            foreach (var prim in EnumerateCad(doc, li))
            {
                if (!layers.Contains(prim.Layer)) continue;
                switch (prim.Geo)
                {
                    case PolyLine pl:
                    {
                        var pts = pl.GetCoordinates().ToList();
                        if (pts.Count < 2) break;
                        bool closed = pts[0].DistanceTo(pts[pts.Count - 1]) < MmToFt(joinTol);
                        var cs = new List<Curve>();
                        for (int i = 1; i < pts.Count; i++) if (pts[i].DistanceTo(pts[i - 1]) > MmToFt(1)) cs.Add(Line.CreateBound(pts[i - 1], pts[i]));
                        if (closed && cs.Count >= 3) loops.Add(cs);
                        else if (closed && cs.Count < 3) break;
                        else open.AddRange(cs);
                        break;
                    }
                    case Line ln: open.Add(ln); break;
                    case Arc arc:
                        if (arc.IsBound) open.Add(arc);
                        else if (arc.IsCyclic && !arc.IsBound) loops.Add(new List<Curve> { Arc.Create(arc.Center, arc.Radius, 0, Math.PI, arc.XDirection, arc.YDirection), Arc.Create(arc.Center, arc.Radius, Math.PI, 2 * Math.PI, arc.XDirection, arc.YDirection) });
                        break;
                }
            }
            if (chainLines && open.Count > 1)
            {
                // greedy chaining of open curves into closed loops
                var used = new bool[open.Count];
                double tol = MmToFt(joinTol);
                for (int i = 0; i < open.Count; i++)
                {
                    if (used[i]) continue;
                    var chain = new List<Curve> { open[i] }; used[i] = true;
                    var start = open[i].GetEndPoint(0); var cur = open[i].GetEndPoint(1);
                    bool closed = false;
                    for (int guard = 0; guard < open.Count; guard++)
                    {
                        if (cur.DistanceTo(start) < tol && chain.Count >= 3) { closed = true; break; }
                        int next = -1; bool flip = false; double best = tol;
                        for (int j = 0; j < open.Count; j++)
                        {
                            if (used[j]) continue;
                            double d0 = open[j].GetEndPoint(0).DistanceTo(cur), d1 = open[j].GetEndPoint(1).DistanceTo(cur);
                            if (d0 < best) { best = d0; next = j; flip = false; }
                            if (d1 < best) { best = d1; next = j; flip = true; }
                        }
                        if (next < 0) break;
                        used[next] = true;
                        var c = open[next];
                        if (flip) c = c.CreateReversed();
                        chain.Add(c); cur = c.GetEndPoint(1);
                    }
                    if (closed) loops.Add(chain);
                }
            }
            // flatten to the level, compute area / bbox, bbox filter
            double z0 = level.Elevation;
            var plans = new List<(CurveLoop loop, double areaM2, double[] bb, List<XYZ> poly)>();
            foreach (var cs in loops)
            {
                try
                {
                    var cl = new CurveLoop();
                    var poly = new List<XYZ>();
                    foreach (var c in cs)
                    {
                        var cc = c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, z0 - c.GetEndPoint(0).Z)));
                        cl.Append(cc);
                        poly.AddRange(cc.Tessellate().Take(cc.Tessellate().Count - 1));
                    }
                    double a = 0;
                    for (int i = 0; i < poly.Count; i++) { var q = poly[i]; var r = poly[(i + 1) % poly.Count]; a += q.X * r.Y - r.X * q.Y; }
                    double areaM2 = Math.Abs(a) / 2 * 0.09290304;
                    if (areaM2 < minArea) continue;
                    var bb = LoopBboxMm(cl);
                    if (bbox != null && !InBbox(bbox, (bb[0] + bb[2]) / 2, (bb[1] + bb[3]) / 2)) continue;
                    plans.Add((cl, areaM2, bb, poly));
                }
                catch (Exception ex) { warnings.Add("loop skipped: " + ex.Message); }
            }
            if (plans.Count == 0) throw new McpException(McpException.NotFound, $"No closed outline >= min_area_m2 on layer(s) {string.Join(", ", layers)}.");

            // ---- 2. nesting: a loop inside another becomes a hole of the smallest container ----
            bool PointIn(List<XYZ> poly, XYZ q)
            {
                bool inside = false;
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                {
                    if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) &&
                        q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
                }
                return inside;
            }
            var parent = new int[plans.Count];
            for (int i = 0; i < plans.Count; i++)
            {
                parent[i] = -1;
                if (!holesFromInner) continue;
                double bestA = double.MaxValue;
                for (int j = 0; j < plans.Count; j++)
                {
                    if (i == j || plans[j].areaM2 <= plans[i].areaM2) continue;
                    if (plans[j].areaM2 < bestA && PointIn(plans[j].poly, plans[i].poly[0])) { bestA = plans[j].areaM2; parent[i] = j; }
                }
            }
            // holes of holes are floors again (islands): depth parity
            int Depth(int i) { int d = 0; while (parent[i] >= 0) { d++; i = parent[i]; } return d; }

            // ---- 3. type + create ------------------------------------------------------------
            var optType = GetOptLong(p, "type_id");
            string typeName = GetOptString(p, "type_name");
            var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
            FloorType ft = null;
            if (optType.HasValue) ft = doc.GetElement(new ElementId(optType.Value)) as FloorType;
            else if (!string.IsNullOrEmpty(typeName)) ft = floorTypes.FirstOrDefault(t => t.Name == typeName) ?? floorTypes.FirstOrDefault(t => t.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (ft == null) { var def = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType)) as FloorType; ft = def ?? floorTypes.FirstOrDefault(); }
            if (ft == null) throw new McpException(McpException.NotFound, "No floor type in the document.");

            var created = new List<Dictionary<string, object>>();
            var failures = new List<string>();
            var ownTx = !dryRun && !doc.IsModifiable;
            var t = ownTx ? new Transaction(doc, "MCP: floors from CAD") : null;
            try
            {
                if (ownTx) { t.Start(); var fh = t.GetFailureHandlingOptions(); fh.SetFailuresPreprocessor(new SilentFailures()); t.SetFailureHandlingOptions(fh); }
                for (int i = 0; i < plans.Count; i++)
                {
                    if (Depth(i) % 2 == 1) continue; // a hole
                    var holes = Enumerable.Range(0, plans.Count).Where(j => parent[j] == i).Select(j => plans[j].loop).ToList();
                    var desc = new Dictionary<string, object> { ["area_m2"] = Math.Round(plans[i].areaM2, 1), ["holes"] = holes.Count, ["bbox_mm"] = plans[i].bb };
                    if (dryRun) { created.Add(desc); continue; }
                    try
                    {
                        var all = new List<CurveLoop> { plans[i].loop }; all.AddRange(holes);
                        var fl = Floor.Create(doc, all, ft.Id, level.Id);
                        desc["id"] = fl.Id.Value; desc["type"] = ft.Name; created.Add(desc);
                    }
                    catch (Exception ex) { failures.Add($"floor ({Math.Round(plans[i].areaM2)} m2 at {plans[i].bb[0]},{plans[i].bb[1]}): {ex.Message}"); }
                }
                if (ownTx) CommitOrThrow(t, "floors");
            }
            finally { if (ownTx && t.HasStarted() && !t.HasEnded()) t.RollBack(); t?.Dispose(); }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun,
                ["link"] = ImportName(doc, li),
                ["layers"] = layers.ToList(),
                ["level"] = level.Name,
                ["closed_loops"] = plans.Count,
                ["open_curves"] = open.Count,
                ["floors_planned"] = created.Count,
                ["floors_created"] = dryRun ? 0 : created.Count(d => d.ContainsKey("id")),
                ["floor_type"] = ft.Name,
                ["floors"] = created,
                ["failures"] = failures,
                ["warnings"] = warnings,
            };
        }
    }
}
