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
    /// More CAD-driven categories (all generic: layer names, types, heights are parameters):
    ///  - create_roofs_from_cad         : roof edge lines / closed outlines -> flat footprint roofs
    ///  - create_railings_from_cad      : railing path lines -> railings (plan railings only)
    ///  - create_curtain_walls_from_cad : glazing lines (+ mullion section symbols) -> curtain walls
    ///                                    with the drawn vertical grid
    /// Every tool dry-runs by default and reports what it would build (so heights / types can be
    /// confirmed by a human first).
    /// </summary>
    public static partial class CommandRouter
    {
        // ---------------------------------------------------------------- shared helpers
        private sealed class CurveChain { public List<Curve> Curves = new List<Curve>(); public bool Closed; }

        /// <summary>Greedy end-to-end chaining of curves (ends within tol); returns open and closed chains.</summary>
        private static List<CurveChain> ChainCurves(List<Curve> open, double tol)
        {
            var chains = new List<CurveChain>();
            var used = new bool[open.Count];
            for (int i = 0; i < open.Count; i++)
            {
                if (used[i]) continue;
                var chain = new List<Curve> { open[i] }; used[i] = true;
                // grow forward
                for (int pass = 0; pass < 2; pass++)
                {
                    while (true)
                    {
                        var cur = chain[chain.Count - 1].GetEndPoint(1);
                        var start = chain[0].GetEndPoint(0);
                        if (chain.Count >= 3 && cur.DistanceTo(start) < tol) break;
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
                        chain.Add(flip ? open[next].CreateReversed() : open[next]);
                    }
                    // second pass: grow backwards by reversing the chain
                    if (pass == 0)
                    {
                        chain.Reverse();
                        for (int k = 0; k < chain.Count; k++) chain[k] = chain[k].CreateReversed();
                    }
                }
                bool closed = chain.Count >= 3 && chain[chain.Count - 1].GetEndPoint(1).DistanceTo(chain[0].GetEndPoint(0)) < tol;
                chains.Add(new CurveChain { Curves = chain, Closed = closed });
            }
            return chains;
        }

        /// <summary>CurveLoop from a chain whose ends only meet within a tolerance: snap line ends / insert tiny connectors.</summary>
        private static CurveLoop LoopFromChain(List<Curve> chain, bool close)
        {
            var cl = new CurveLoop();
            var cs = new List<Curve>(chain);
            for (int i = 0; i < cs.Count; i++)
            {
                var c = cs[i];
                if (i > 0)
                {
                    var prevEnd = cs[i - 1].GetEndPoint(1);
                    if (prevEnd.DistanceTo(c.GetEndPoint(0)) > 1e-6)
                    {
                        if (c is Line ln && ln.GetEndPoint(1).DistanceTo(prevEnd) > MmToFt(1)) cs[i] = c = Line.CreateBound(prevEnd, ln.GetEndPoint(1));
                        else { cl.Append(Line.CreateBound(prevEnd, c.GetEndPoint(0))); }
                    }
                }
                cl.Append(c);
            }
            if (close)
            {
                var gap = cs[cs.Count - 1].GetEndPoint(1).DistanceTo(cs[0].GetEndPoint(0));
                if (gap > 1e-6) cl.Append(Line.CreateBound(cs[cs.Count - 1].GetEndPoint(1), cs[0].GetEndPoint(0)));
            }
            return cl;
        }

        private static double PolyAreaM2(IEnumerable<Curve> cs)
        {
            var pts = new List<XYZ>();
            foreach (var c in cs) { var t = c.Tessellate(); for (int i = 0; i + 1 < t.Count; i++) pts.Add(t[i]); }
            double a = 0;
            for (int i = 0; i < pts.Count; i++) { var q = pts[i]; var r = pts[(i + 1) % pts.Count]; a += q.X * r.Y - r.X * q.Y; }
            return Math.Abs(a) / 2 * 0.09290304;
        }

        private static Curve Flatten(Curve c, double z) => c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, z - c.GetEndPoint(0).Z)));

        private static List<Curve> CadCurvesOnLayers(Document doc, ImportInstance li, HashSet<string> layers, double[] bbox, double minLenFt, List<List<XYZ>> closedPolys = null)
        {
            var result = new List<Curve>();
            foreach (var prim in EnumerateCad(doc, li))
            {
                if (!layers.Contains(prim.Layer)) continue;
                switch (prim.Geo)
                {
                    case Line ln: if (ln.Length >= minLenFt) result.Add(ln); break;
                    case Arc arc when arc.IsBound: if (arc.Length >= minLenFt) result.Add(arc); break;
                    case PolyLine pl:
                    {
                        var pts = pl.GetCoordinates().ToList();
                        bool closed = pts.Count > 3 && pts[0].DistanceTo(pts[pts.Count - 1]) < MmToFt(1);
                        if (closed && closedPolys != null) { closedPolys.Add(pts.Take(pts.Count - 1).ToList()); break; }
                        for (int i = 1; i < pts.Count; i++)
                            if (pts[i].DistanceTo(pts[i - 1]) >= Math.Max(minLenFt, MmToFt(1))) result.Add(Line.CreateBound(pts[i - 1], pts[i]));
                        break;
                    }
                }
            }
            if (bbox != null)
                result = result.Where(c => { var m = c.Evaluate(0.5, true); return InBbox(bbox, FtToMm(m.X), FtToMm(m.Y)); }).ToList();
            return result;
        }

        private static Transaction BeginOwnTx(Document doc, string name, out bool ownTx)
        {
            ownTx = !doc.IsModifiable;
            if (!ownTx) return null;
            var t = new Transaction(doc, name);
            t.Start();
            var fh = t.GetFailureHandlingOptions(); fh.SetFailuresPreprocessor(new SilentFailures()); t.SetFailureHandlingOptions(fh);
            return t;
        }

        // ================================================================ roofs
        private static Dictionary<string, object> CreateRoofsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", true);
            if (!dryRun) EnsureWritable();
            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0) throw new McpException(McpException.BadRequest, "Provide the roof outline layer(s) via 'layer' or 'layers'.");
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            double minArea = GetDoubleOr(p, "min_area_m2", 5);
            double joinTol = MmToFt(GetDoubleOr(p, "join_tolerance_mm", 25));
            double offsetMm = GetDoubleOr(p, "offset_mm", 0);
            double? thick = p.ContainsKey("thickness_mm") ? GetDoubleOr(p, "thickness_mm", 0) : (double?)null;
            bool innerAsOpenings = GetBoolOr(p, "inner_loops_as_openings", true);
            string aiTag = GetOptString(p, "ai_tag") ?? "_AI";
            var bbox = GetOptBboxMm(p);
            var warnings = new List<string>();

            var closedPolys = new List<List<XYZ>>();
            var curves = CadCurvesOnLayers(doc, li, layers, bbox, MmToFt(1), closedPolys);
            var chains = ChainCurves(curves, joinTol);
            foreach (var poly in closedPolys)
            {
                var ch = new CurveChain { Closed = true };
                for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; if (a.DistanceTo(b) > MmToFt(1)) ch.Curves.Add(Line.CreateBound(a, b)); }
                if (ch.Curves.Count >= 3) chains.Add(ch);
            }
            double z0 = level.Elevation;
            var loops = new List<(List<Curve> cs, double area, double[] bb, List<XYZ> poly)>();
            int openChains = 0;
            foreach (var ch in chains)
            {
                if (!ch.Closed) { openChains++; continue; }
                var cs = ch.Curves.Select(c => Flatten(c, z0)).ToList();
                double area = PolyAreaM2(cs);
                if (area < minArea) continue;
                CurveLoop cl;
                try { cl = LoopFromChain(cs, true); cs = cl.ToList(); } catch { continue; }
                var poly = new List<XYZ>(); foreach (var c in cs) poly.Add(c.GetEndPoint(0));
                var bb = LoopBboxMm(cl);
                if (bbox != null && !InBbox(bbox, (bb[0] + bb[2]) / 2, (bb[1] + bb[3]) / 2)) continue;
                loops.Add((cs, area, bb, poly));
            }
            if (loops.Count == 0)
                throw new McpException(McpException.NotFound, $"No closed roof outline >= {minArea} m2 on layer(s) {string.Join(", ", layers)} ({chains.Count} chains, {openChains} open - raise join_tolerance_mm or check the layer).");
            // nesting: inner loops become openings of the smallest containing loop
            bool PointIn(List<XYZ> poly, XYZ q)
            {
                bool inside = false;
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                    if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
                return inside;
            }
            var parent = Enumerable.Repeat(-1, loops.Count).ToArray();
            if (innerAsOpenings)
                for (int i = 0; i < loops.Count; i++)
                {
                    double bestA = double.MaxValue;
                    for (int j = 0; j < loops.Count; j++)
                        if (i != j && loops[j].area > loops[i].area && loops[j].area < bestA && PointIn(loops[j].poly, loops[i].poly[0])) { bestA = loops[j].area; parent[i] = j; }
                }
            int Depth(int i) { int d = 0; while (parent[i] >= 0) { d++; i = parent[i]; } return d; }

            // type
            var roofTypes = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().ToList();
            var optType = GetOptLong(p, "type_id"); string typeName = GetOptString(p, "type_name");
            RoofType rt = null;
            if (optType.HasValue) rt = doc.GetElement(new ElementId(optType.Value)) as RoofType;
            else if (!string.IsNullOrEmpty(typeName)) rt = roofTypes.FirstOrDefault(t => t.Name == typeName) ?? roofTypes.FirstOrDefault(t => t.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (rt == null) { rt = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.RoofType)) as RoofType ?? roofTypes.FirstOrDefault(); }
            if (rt == null) throw new McpException(McpException.NotFound, "No roof type in the document.");

            var created = new List<Dictionary<string, object>>(); var failures = new List<string>(); string typeCreated = null;
            var t = dryRun ? null : BeginOwnTx(doc, "MCP: roofs from CAD", out var ownTx);
            try
            {
                if (!dryRun && thick.HasValue && thick.Value > 0)
                {
                    var cs0 = rt.GetCompoundStructure(); double cur = cs0 != null ? FtToMm(cs0.GetWidth()) : 0;
                    if (Math.Abs(cur - thick.Value) > 0.5)
                    {
                        var name = $"{rt.Name} {Math.Round(thick.Value)}mm{aiTag}";
                        var existing = roofTypes.FirstOrDefault(x => x.Name == name);
                        if (existing != null) rt = existing;
                        else
                        {
                            var nt = rt.Duplicate(name) as RoofType; var ncs = nt.GetCompoundStructure();
                            if (ncs != null)
                            {
                                int core = ncs.GetFirstCoreLayerIndex(); if (core < 0) core = 0;
                                double others = 0; for (int i = 0; i < ncs.LayerCount; i++) if (i != core) others += ncs.GetLayerWidth(i);
                                ncs.SetLayerWidth(core, Math.Max(MmToFt(1), MmToFt(thick.Value) - others)); nt.SetCompoundStructure(ncs);
                            }
                            MarkAiType(nt, "create_roofs_from_cad", rt.Name); rt = nt; typeCreated = name;
                        }
                    }
                }
                for (int i = 0; i < loops.Count; i++)
                {
                    if (Depth(i) % 2 == 1) continue;
                    var holes = Enumerable.Range(0, loops.Count).Where(j => parent[j] == i).ToList();
                    var desc = new Dictionary<string, object> { ["area_m2"] = Math.Round(loops[i].area, 1), ["openings"] = holes.Count, ["vertices"] = loops[i].cs.Count, ["bbox_mm"] = loops[i].bb };
                    if (dryRun) { desc["outline_mm"] = loops[i].poly.Select(q => new[] { Math.Round(FtToMm(q.X)), Math.Round(FtToMm(q.Y)) }).ToList(); created.Add(desc); continue; }
                    try
                    {
                        var ca = new CurveArray();
                        foreach (var c in loops[i].cs) ca.Append(c);
                        foreach (var j in holes) foreach (var c in loops[j].cs) ca.Append(c);
                        var mca = new ModelCurveArray();
                        var roof = doc.Create.NewFootPrintRoof(ca, level, rt, out mca);
                        // flat roof: no slope-defining edges
                        foreach (ModelCurve mc in mca) { try { roof.set_DefinesSlope(mc, false); } catch { } }
                        if (Math.Abs(offsetMm) > 1e-6) roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(MmToFt(offsetMm));
                        desc["id"] = roof.Id.Value; desc["type"] = rt.Name; created.Add(desc);
                    }
                    catch (Exception ex) { failures.Add($"roof ({Math.Round(loops[i].area)} m2 at {loops[i].bb[0]},{loops[i].bb[1]}): {ex.Message}"); }
                }
                if (t != null) CommitOrThrow(t, "roofs from CAD");
            }
            finally { if (t != null && t.HasStarted() && !t.HasEnded()) t.RollBack(); t?.Dispose(); }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun, ["link"] = ImportName(doc, li), ["layers"] = layers.ToList(), ["level"] = level.Name,
                ["chains"] = chains.Count, ["open_chains"] = openChains, ["closed_loops"] = loops.Count,
                ["roofs_planned"] = created.Count, ["roofs_created"] = dryRun ? 0 : created.Count(d => d.ContainsKey("id")),
                ["roof_type"] = rt.Name, ["type_created"] = typeCreated,
                ["roof_types_available"] = roofTypes.Select(x => x.Name).ToList(),
                ["roofs"] = created, ["failures"] = failures, ["warnings"] = warnings,
            };
        }

        // ================================================================ railings
        private static Dictionary<string, object> CreateRailingsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", true);
            if (!dryRun) EnsureWritable();
            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0) throw new McpException(McpException.BadRequest, "Provide the railing layer(s) via 'layer' or 'layers'.");
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            double minSeg = MmToFt(GetDoubleOr(p, "min_segment_mm", 150));    // drops baluster / post symbols
            double minPath = GetDoubleOr(p, "min_path_mm", 600);              // drops leftovers
            double joinTol = MmToFt(GetDoubleOr(p, "join_tolerance_mm", 30));
            double doubleLine = GetDoubleOr(p, "double_line_mm", 120);        // railings drawn as thin outlines / parallel lines -> one path on the midline
            double baseOffsetMm = GetDoubleOr(p, "base_offset_mm", 0);
            bool skipExisting = GetBoolOr(p, "skip_existing", true);
            var bbox = GetOptBboxMm(p);
            var warnings = new List<string>();

            var curves = CadCurvesOnLayers(doc, li, layers, bbox, minSeg);
            int raw = curves.Count;
            // Revit (and most CAD) draws a railing as a thin outline: two long parallel sides a few cm
            // apart (stair railings: handrail + guard = 2-3 nested outlines). Collapse parallel lines
            // closer than doubleLine into their midline, repeatedly, until nothing merges; exact
            // duplicates are dropped.
            var lines = curves.OfType<Line>().ToList();
            var others = curves.Where(c => !(c is Line)).ToList();
            int mergeRounds = 0;
            while (mergeRounds++ < 4)
            {
                var dropped = new bool[lines.Count];
                var merged = new List<Line>();
                bool any = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (dropped[i]) continue;
                    var a0 = lines[i]; var d = a0.Direction; var n = new XYZ(-d.Y, d.X, 0);
                    Line partner = null; int pj = -1; double pOff = 0;
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        if (dropped[j]) continue;
                        var b0 = lines[j];
                        if (Math.Abs(b0.Direction.DotProduct(d)) < 0.999) continue;
                        double off = n.DotProduct(b0.Evaluate(0.5, true) - a0.GetEndPoint(0));
                        if (Math.Abs(off) > MmToFt(doubleLine)) continue;
                        double t1 = d.DotProduct(b0.GetEndPoint(0) - a0.GetEndPoint(0)), t2 = d.DotProduct(b0.GetEndPoint(1) - a0.GetEndPoint(0));
                        double ov = Math.Min(a0.Length, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2));
                        if (ov < 0.5 * Math.Min(a0.Length, b0.Length)) continue;
                        partner = b0; pj = j; pOff = off; break;
                    }
                    if (partner != null)
                    {
                        dropped[pj] = true; any = true;
                        var shift = n * (pOff / 2);
                        double t1 = d.DotProduct(partner.GetEndPoint(0) - a0.GetEndPoint(0)), t2 = d.DotProduct(partner.GetEndPoint(1) - a0.GetEndPoint(0));
                        double lo = Math.Min(0, Math.Min(t1, t2)), hi = Math.Max(a0.Length, Math.Max(t1, t2));
                        merged.Add(Line.CreateBound(a0.GetEndPoint(0) + d * lo + shift, a0.GetEndPoint(0) + d * hi + shift));
                    }
                    else merged.Add(a0);
                }
                lines = merged;
                if (!any) break;
            }
            // Revit railings accept lines and arcs only: tessellate splines / ellipses into short lines
            var othersOk = new List<Curve>();
            foreach (var c in others)
            {
                if (c is Arc) { othersOk.Add(c); continue; }
                var pts = c.Tessellate();
                for (int i = 1; i < pts.Count; i++) if (pts[i].DistanceTo(pts[i - 1]) > MmToFt(1)) othersOk.Add(Line.CreateBound(pts[i - 1], pts[i]));
            }
            others = othersOk;
            var mergedAll = new List<Curve>(lines); mergedAll.AddRange(others);
            var merged2 = mergedAll;
            double z0 = level.Elevation + MmToFt(baseOffsetMm);
            var chains = ChainCurves(merged2.Select(c => Flatten(c, z0)).ToList(), joinTol);
            var paths0 = chains.Select(ch => (ch, len: ch.Curves.Sum(c => c.Length))).Where(x => FtToMm(x.len) >= minPath).OrderByDescending(x => x.len).ToList();
            // drop paths that run parallel to a longer path within pathDupTol (leftover outline sides)
            double pathDupTol = MmToFt(GetDoubleOr(p, "path_duplicate_mm", 150));
            // paths on stair footprints are the stair railings (sloped, belong to the stair) - skip
            // them unless asked otherwise; stairs get railings via their own tool / Revit defaults
            bool excludeOnStairs = GetBoolOr(p, "exclude_on_stairs", true);
            var stairBoxes = new List<double[]>();
            if (excludeOnStairs)
                foreach (var st in new FilteredElementCollector(doc).OfClass(typeof(Stairs)).Cast<Stairs>())
                {
                    var bl = st.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId(); var tl = st.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.AsElementId();
                    if (bl != level.Id && tl != level.Id) continue;
                    var bb = st.get_BoundingBox(null); if (bb == null) continue;
                    stairBoxes.Add(new[] { bb.Min.X - MmToFt(300), bb.Min.Y - MmToFt(300), bb.Max.X + MmToFt(300), bb.Max.Y + MmToFt(300) });
                }
            var excludeBoxes = new List<double[]>();
            if (p.ContainsKey("exclude_bboxes_mm") && p["exclude_bboxes_mm"] is IEnumerable ebs && !(p["exclude_bboxes_mm"] is string))
                foreach (var o in ebs)
                    if (o is IEnumerable bbo && !(o is string))
                    {
                        var pts = bbo.Cast<object>().Select(q => (q as IEnumerable)?.Cast<object>().Select(Convert.ToDouble).ToArray()).Where(q => q != null && q.Length >= 2).ToList();
                        if (pts.Count >= 2) excludeBoxes.Add(new[] { MmToFt(Math.Min(pts[0][0], pts[1][0])), MmToFt(Math.Min(pts[0][1], pts[1][1])), MmToFt(Math.Max(pts[0][0], pts[1][0])), MmToFt(Math.Max(pts[0][1], pts[1][1])) });
                    }
            int onStairs = 0, excluded = 0;
            var paths = new List<(CurveChain ch, double len)>();
            foreach (var cand in paths0)
            {
                if (excludeBoxes.Count > 0)
                {
                    var mids0 = cand.ch.Curves.Select(c => c.Evaluate(0.5, true)).ToList();
                    if (mids0.Count(m => excludeBoxes.Any(sb => m.X >= sb[0] && m.X <= sb[2] && m.Y >= sb[1] && m.Y <= sb[3])) >= Math.Max(1, mids0.Count * 0.6)) { excluded++; continue; }
                }
                if (stairBoxes.Count > 0)
                {
                    var mids = cand.ch.Curves.Select(c => c.Evaluate(0.5, true)).ToList();
                    int inside = mids.Count(m => stairBoxes.Any(sb => m.X >= sb[0] && m.X <= sb[2] && m.Y >= sb[1] && m.Y <= sb[3]));
                    if (inside >= Math.Max(1, mids.Count * 0.6)) { onStairs++; continue; }
                }
                var mid = cand.ch.Curves.Select(c => c.Evaluate(0.5, true)).ToList();
                bool dup = paths.Any(kept =>
                {
                    int near = 0;
                    foreach (var m in mid)
                        foreach (var kc in kept.ch.Curves)
                        {
                            try { if (kc.Distance(m) <= pathDupTol) { near++; break; } } catch { }
                        }
                    return near >= Math.Max(1, mid.Count * 0.6);
                });
                if (!dup) paths.Add(cand);
            }

            // type
            var railTypes = new FilteredElementCollector(doc).OfClass(typeof(RailingType)).Cast<RailingType>().ToList();
            double? TypeHeightMm(RailingType rt0) { try { var prm = rt0.get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT) ?? rt0.LookupParameter("Railing Height"); return prm != null ? FtToMm(prm.AsDouble()) : (double?)null; } catch { return null; } }
            var optType = GetOptLong(p, "type_id"); string typeName = GetOptString(p, "type_name"); double? wantH = p.ContainsKey("height_mm") ? GetDoubleOr(p, "height_mm", 0) : (double?)null;
            RailingType rt = null;
            if (optType.HasValue) rt = doc.GetElement(new ElementId(optType.Value)) as RailingType;
            else if (!string.IsNullOrEmpty(typeName)) rt = railTypes.FirstOrDefault(x => x.Name == typeName) ?? railTypes.FirstOrDefault(x => x.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
            else if (wantH.HasValue) rt = railTypes.Where(x => TypeHeightMm(x).HasValue).OrderBy(x => Math.Abs(TypeHeightMm(x).Value - wantH.Value)).FirstOrDefault();
            if (rt == null) rt = railTypes.FirstOrDefault();
            if (rt == null) throw new McpException(McpException.NotFound, "No railing type in the document.");
            if (wantH.HasValue && TypeHeightMm(rt).HasValue && Math.Abs(TypeHeightMm(rt).Value - wantH.Value) > 1)
                warnings.Add($"No railing type is exactly {wantH} mm high; using '{rt.Name}' ({Math.Round(TypeHeightMm(rt).Value)} mm). Pass type_id/type_name to override.");

            // existing railings (dedupe)
            var existing = skipExisting ? new FilteredElementCollector(doc).OfClass(typeof(Railing)).Cast<Railing>().Where(r => r.LevelId == level.Id)
                .Select(r => r.get_BoundingBox(null)).Where(b => b != null).ToList() : new List<BoundingBoxXYZ>();

            var created = new List<Dictionary<string, object>>(); var failures = new List<string>(); int skipped = 0;
            var t = dryRun ? null : BeginOwnTx(doc, "MCP: railings from CAD", out var ownTx);
            try
            {
                foreach (var (ch, len) in paths)
                {
                    CurveLoop cl;
                    try { cl = LoopFromChain(ch.Curves, ch.Closed); }
                    catch (Exception ex) { failures.Add($"path {Math.Round(FtToMm(len))} mm: {ex.Message}"); continue; }
                    var bb = LoopBboxMm(cl);
                    if (existing.Any(b => Math.Min(FtToMm(b.Max.X), bb[2]) - Math.Max(FtToMm(b.Min.X), bb[0]) > -100 && Math.Min(FtToMm(b.Max.Y), bb[3]) - Math.Max(FtToMm(b.Min.Y), bb[1]) > -100
                                         && Math.Abs(FtToMm(b.Min.X) - bb[0]) < 150 && Math.Abs(FtToMm(b.Min.Y) - bb[1]) < 150)) { skipped++; continue; }
                    var desc = new Dictionary<string, object>
                    {
                        ["length_mm"] = Math.Round(FtToMm(len)), ["segments"] = ch.Curves.Count, ["closed"] = ch.Closed, ["bbox_mm"] = bb,
                        ["start"] = new[] { Math.Round(FtToMm(ch.Curves[0].GetEndPoint(0).X)), Math.Round(FtToMm(ch.Curves[0].GetEndPoint(0).Y)) },
                        ["end"] = new[] { Math.Round(FtToMm(ch.Curves[ch.Curves.Count - 1].GetEndPoint(1).X)), Math.Round(FtToMm(ch.Curves[ch.Curves.Count - 1].GetEndPoint(1).Y)) },
                    };
                    if (dryRun) { created.Add(desc); continue; }
                    try
                    {
                        var r = Railing.Create(doc, cl, rt.Id, level.Id);
                        if (Math.Abs(baseOffsetMm) > 1e-6) r.get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT_OFFSET)?.Set(MmToFt(baseOffsetMm));
                        desc["id"] = r.Id.Value; desc["type"] = rt.Name; created.Add(desc);
                    }
                    catch (Exception ex) { failures.Add($"railing {Math.Round(FtToMm(len))} mm at {bb[0]},{bb[1]}: {ex.Message}"); }
                }
                if (t != null) CommitOrThrow(t, "railings from CAD");
            }
            finally { if (t != null && t.HasStarted() && !t.HasEnded()) t.RollBack(); t?.Dispose(); }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun, ["link"] = ImportName(doc, li), ["layers"] = layers.ToList(), ["level"] = level.Name,
                ["segments_raw"] = raw, ["segments_after_double_line_merge"] = merged2.Count, ["paths"] = paths.Count, ["paths_dropped_as_duplicates"] = paths0.Count - paths.Count - onStairs - excluded, ["paths_on_stairs_skipped"] = onStairs, ["paths_in_exclude_bboxes"] = excluded,
                ["railings_planned"] = created.Count, ["railings_created"] = dryRun ? 0 : created.Count(d => d.ContainsKey("id")), ["skipped_existing"] = skipped,
                ["railing_type"] = rt.Name, ["railing_type_height_mm"] = TypeHeightMm(rt).HasValue ? Math.Round(TypeHeightMm(rt).Value) : (object)null,
                ["railing_types_available"] = railTypes.Select(x => new Dictionary<string, object> { ["id"] = x.Id.Value, ["name"] = x.Name, ["height_mm"] = TypeHeightMm(x).HasValue ? Math.Round(TypeHeightMm(x).Value) : (object)null }).ToList(),
                ["railings"] = created, ["failures"] = failures, ["warnings"] = warnings,
            };
        }

        // ================================================================ curtain walls
        private static Dictionary<string, object> CreateCurtainWallsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", true);
            if (!dryRun) EnsureWritable();
            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0) throw new McpException(McpException.BadRequest, "Provide the glazing line layer(s) via 'layer' or 'layers'.");
            var mullionLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (p.ContainsKey("mullion_layers") && p["mullion_layers"] is IEnumerable ml && !(p["mullion_layers"] is string))
                foreach (var o in ml) if (o != null) mullionLayers.Add(Convert.ToString(o));
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            var topLevelIdOpt = GetOptLong(p, "top_level_id");
            var topLevel = topLevelIdOpt.HasValue ? doc.GetElement(new ElementId(topLevelIdOpt.Value)) as Level : null;
            double topOffsetMm = GetDoubleOr(p, "top_offset_mm", 0), baseOffsetMm = GetDoubleOr(p, "base_offset_mm", 0);
            double heightMm = topLevel != null ? FtToMm(topLevel.Elevation - level.Elevation) + topOffsetMm - baseOffsetMm : GetDoubleOr(p, "height_mm", 0);
            if (!dryRun && heightMm <= 0) throw new McpException(McpException.BadRequest, "Give 'height_mm' or 'top_level_id' (confirm the curtain wall height first - dry_run lists the runs).");
            double minSeg = MmToFt(GetDoubleOr(p, "min_segment_mm", 100));
            double joinGap = GetDoubleOr(p, "join_gap_mm", 250);           // gap between glazing lines across a mullion
            double collinearTol = GetDoubleOr(p, "collinear_tolerance_mm", 25);
            double minRun = GetDoubleOr(p, "min_run_mm", 600);
            double doubleLine = GetDoubleOr(p, "double_line_mm", 200);     // glass drawn as two lines (both faces of the mullion) -> one
            bool gridFromMullions = GetBoolOr(p, "grid_from_mullions", true);
            double defaultGrid = GetDoubleOr(p, "default_grid_mm", 0);     // 0 = no vertical grid when no mullions found
            double mullionMaxArea = GetDoubleOr(p, "mullion_max_area_m2", 0.05);
            bool skipExisting = GetBoolOr(p, "skip_existing", true);
            string aiTag = GetOptString(p, "ai_tag") ?? "_AI";
            var bbox = GetOptBboxMm(p);
            var warnings = new List<string>();

            // ---- 1. glazing segments (lines only) + mullion section symbols (small closed polylines)
            var closedPolys = new List<List<XYZ>>();
            var segs = CadCurvesOnLayers(doc, li, layers, bbox, minSeg, closedPolys).OfType<Line>().ToList();
            var mullionPts = new List<XYZ>();
            var mullionSrc = new List<List<XYZ>>(closedPolys);
            if (mullionLayers.Count > 0) CadCurvesOnLayers(doc, li, mullionLayers, bbox, MmToFt(1), mullionSrc);
            foreach (var poly in mullionSrc)
            {
                var cs = new List<Curve>();
                for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; if (a.DistanceTo(b) > MmToFt(0.5)) cs.Add(Line.CreateBound(a, b)); }
                if (cs.Count < 3) continue;
                double area = PolyAreaM2(cs);
                if (area > mullionMaxArea || area <= 0) continue;
                mullionPts.Add(new XYZ(poly.Average(q => q.X), poly.Average(q => q.Y), 0));
            }
            // ---- 2. group collinear segments -> runs (gaps <= joinGap bridged: mullions sit there)
            var used = new bool[segs.Count];
            var runs = new List<(XYZ a, XYZ b, int segCount, double glassLen)>();
            for (int i = 0; i < segs.Count; i++)
            {
                if (used[i]) continue;
                var baseLn = segs[i]; var d = baseLn.Direction; var n = new XYZ(-d.Y, d.X, 0); var p0 = baseLn.GetEndPoint(0);
                var members = new List<int>();
                for (int j = 0; j < segs.Count; j++)
                {
                    if (used[j]) continue;
                    var s = segs[j];
                    if (Math.Abs(s.Direction.DotProduct(d)) < 0.9995) continue;
                    double off = Math.Abs(n.DotProduct(s.Evaluate(0.5, true) - p0));
                    if (off > MmToFt(collinearTol) && off > MmToFt(doubleLine)) continue;
                    members.Add(j);
                }
                // sort by position along d and split at gaps > joinGap
                var spans = members.Select(j => { var s = segs[j]; double t1 = d.DotProduct(s.GetEndPoint(0) - p0), t2 = d.DotProduct(s.GetEndPoint(1) - p0); return (j, lo: Math.Min(t1, t2), hi: Math.Max(t1, t2), off: n.DotProduct(s.Evaluate(0.5, true) - p0)); }).OrderBy(x => x.lo).ToList();
                var cur = new List<(int j, double lo, double hi, double off)>();
                void Flush()
                {
                    if (cur.Count == 0) return;
                    double lo = cur.Min(x => x.lo), hi = cur.Max(x => x.hi);
                    double glass = 0; double covLo = double.NaN;
                    foreach (var x in cur.OrderBy(x => x.lo)) { glass += x.hi - x.lo; }
                    double offAvg = cur.Average(x => x.off);
                    foreach (var x in cur) used[x.j] = true;
                    if (FtToMm(hi - lo) >= minRun) runs.Add((p0 + d * lo + n * offAvg, p0 + d * hi + n * offAvg, cur.Count, glass));
                    cur.Clear();
                }
                foreach (var sp in spans)
                {
                    if (cur.Count > 0 && sp.lo - cur.Max(x => x.hi) > MmToFt(joinGap)) Flush();
                    cur.Add(sp);
                }
                Flush();
            }
            // dedupe runs that are double lines of each other (parallel within doubleLine, overlapping)
            var finalRuns = new List<(XYZ a, XYZ b, int segCount, double glassLen)>();
            foreach (var r in runs.OrderByDescending(r => r.a.DistanceTo(r.b)))
            {
                var d = (r.b - r.a).Normalize(); var n = new XYZ(-d.Y, d.X, 0);
                bool dup = finalRuns.Any(f =>
                {
                    var fd = (f.b - f.a).Normalize(); if (Math.Abs(fd.DotProduct(d)) < 0.9995) return false;
                    double off = Math.Abs(n.DotProduct((f.a + f.b) / 2 - r.a)); if (off > MmToFt(doubleLine)) return false;
                    double t1 = d.DotProduct(f.a - r.a), t2 = d.DotProduct(f.b - r.a); double L = r.a.DistanceTo(r.b);
                    double ov = Math.Min(L, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2));
                    return ov > 0.5 * L;
                });
                if (!dup) finalRuns.Add(r);
            }

            // ---- 3. types: curtain wall type (manual vertical grid, mullions from the type), mullion type
            var cwTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(w => w.Kind == WallKind.Curtain).ToList();
            if (cwTypes.Count == 0) throw new McpException(McpException.NotFound, "No curtain wall type in the document (load a curtain wall / storefront type first).");
            var optType = GetOptLong(p, "type_id"); string typeName = GetOptString(p, "type_name");
            WallType baseType = null;
            if (optType.HasValue) baseType = doc.GetElement(new ElementId(optType.Value)) as WallType;
            else if (!string.IsNullOrEmpty(typeName)) baseType = cwTypes.FirstOrDefault(x => x.Name == typeName) ?? cwTypes.FirstOrDefault(x => x.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (baseType == null) baseType = cwTypes.FirstOrDefault(x => x.Name.IndexOf("Curtain Wall", StringComparison.OrdinalIgnoreCase) >= 0 && !x.Name.EndsWith(aiTag)) ?? cwTypes.First(x => !x.Name.EndsWith(aiTag) || cwTypes.Count == 1);
            var mullionTypes = new FilteredElementCollector(doc).OfClass(typeof(MullionType)).Cast<MullionType>().ToList();
            var optMullion = GetOptLong(p, "mullion_type_id"); string mullionName = GetOptString(p, "mullion_type_name");
            MullionType mullionType = null;
            if (optMullion.HasValue) mullionType = doc.GetElement(new ElementId(optMullion.Value)) as MullionType;
            else if (!string.IsNullOrEmpty(mullionName)) mullionType = mullionTypes.FirstOrDefault(x => x.Name.IndexOf(mullionName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (mullionType == null) mullionType = mullionTypes.FirstOrDefault(x => x.Name.IndexOf("Rect", StringComparison.OrdinalIgnoreCase) >= 0) ?? mullionTypes.FirstOrDefault();
            bool wantMullions = GetBoolOr(p, "mullions", true) && mullionType != null;

            // existing curtain walls on the level (dedupe)
            // existing curtain walls that pass through this level (multi-storey glazing built once from a lower level counts)
            var existing = new List<Curve>();
            if (skipExisting)
                foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.WallType.Kind == WallKind.Curtain))
                {
                    var lc = (w.Location as LocationCurve)?.Curve; if (lc == null) continue;
                    var bb = w.get_BoundingBox(null); if (bb == null) continue;
                    double zLvl = level.Elevation + MmToFt(100);
                    if (bb.Min.Z <= zLvl && bb.Max.Z >= level.Elevation + MmToFt(500)) existing.Add(lc);
                }

            var created = new List<Dictionary<string, object>>(); var failures = new List<string>(); int skipped = 0; string typeCreated = null;
            double z0 = level.Elevation;
            var t = dryRun ? null : BeginOwnTx(doc, "MCP: curtain walls from CAD", out var ownTx);
            try
            {
                WallType cwType = baseType;
                if (!dryRun)
                {
                    // derived type: no automatic vertical grid (we add the drawn grid), mullions on grid lines + borders
                    var name = $"{(baseType.Name.EndsWith(aiTag) ? baseType.Name.Substring(0, baseType.Name.Length - aiTag.Length) : baseType.Name)} CAD grid{aiTag}";
                    cwType = cwTypes.FirstOrDefault(x => x.Name == name);
                    if (cwType == null)
                    {
                        cwType = baseType.Duplicate(name) as WallType;
                        try { cwType.get_Parameter(BuiltInParameter.SPACING_LAYOUT_VERT)?.Set(0); } catch { }
                        try { cwType.get_Parameter(BuiltInParameter.SPACING_LAYOUT_HORIZ)?.Set(0); } catch { }
                        if (wantMullions)
                        {
                            foreach (var bip in new[] { BuiltInParameter.AUTO_MULLION_INTERIOR_VERT, BuiltInParameter.AUTO_MULLION_BORDER1_VERT, BuiltInParameter.AUTO_MULLION_BORDER2_VERT, BuiltInParameter.AUTO_MULLION_BORDER1_HORIZ, BuiltInParameter.AUTO_MULLION_BORDER2_HORIZ })
                                try { cwType.get_Parameter(bip)?.Set(mullionType.Id); } catch { }
                        }
                        MarkAiType(cwType, "create_curtain_walls_from_cad", baseType.Name);
                        typeCreated = name;
                    }
                }
                foreach (var r in finalRuns)
                {
                    var a = new XYZ(r.a.X, r.a.Y, z0); var b = new XYZ(r.b.X, r.b.Y, z0);
                    double L = a.DistanceTo(b);
                    var d = (b - a).Normalize(); var n = new XYZ(-d.Y, d.X, 0);
                    // mullion positions on this run
                    var gridT = new List<double>();
                    foreach (var m in mullionPts)
                    {
                        double off = Math.Abs(n.DotProduct(new XYZ(m.X, m.Y, 0) - new XYZ(a.X, a.Y, 0)));
                        if (off > MmToFt(150)) continue;
                        double tt = d.DotProduct(new XYZ(m.X, m.Y, 0) - new XYZ(a.X, a.Y, 0));
                        if (tt < MmToFt(50) || tt > L - MmToFt(50)) continue;
                        gridT.Add(tt);
                    }
                    gridT.Sort();
                    var spacing = new List<double>(); for (int i = 1; i < gridT.Count; i++) spacing.Add(FtToMm(gridT[i] - gridT[i - 1]));
                    if (gridT.Count == 0 && defaultGrid > 0) for (double tt = MmToFt(defaultGrid); tt < L - MmToFt(50); tt += MmToFt(defaultGrid)) gridT.Add(tt);
                    bool exists = existing.Any(c =>
                    {
                        if (!(c is Line el) || Math.Abs(el.Direction.DotProduct(d)) < 0.999) return false;
                        var e0 = new XYZ(el.GetEndPoint(0).X, el.GetEndPoint(0).Y, 0); var e1 = new XYZ(el.GetEndPoint(1).X, el.GetEndPoint(1).Y, 0);
                        var a0 = new XYZ(a.X, a.Y, 0);
                        double off = Math.Abs(n.DotProduct((e0 + e1) / 2 - a0)); if (off > MmToFt(300)) return false;
                        double t1 = d.DotProduct(e0 - a0), t2 = d.DotProduct(e1 - a0);
                        double ov = Math.Min(L, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2));
                        return ov >= 0.7 * Math.Min(L, el.Length);
                    });
                    if (exists) { skipped++; continue; }
                    var desc = new Dictionary<string, object>
                    {
                        ["start"] = new[] { Math.Round(FtToMm(a.X)), Math.Round(FtToMm(a.Y)) }, ["end"] = new[] { Math.Round(FtToMm(b.X)), Math.Round(FtToMm(b.Y)) },
                        ["length_mm"] = Math.Round(FtToMm(L)), ["glazing_segments"] = r.segCount, ["grid_lines"] = gridT.Count,
                        ["grid_spacing_mm"] = spacing.Count > 0 ? Math.Round(spacing.Average()) : (object)null,
                        ["height_mm"] = heightMm > 0 ? Math.Round(heightMm) : (object)null,
                    };
                    if (dryRun) { created.Add(desc); continue; }
                    try
                    {
                        var wall = Wall.Create(doc, Line.CreateBound(a, b), cwType.Id, level.Id, MmToFt(heightMm), MmToFt(baseOffsetMm), false, false);
                        if (topLevel != null)
                        {
                            wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(topLevel.Id);
                            if (Math.Abs(topOffsetMm) > 1e-6) wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(MmToFt(topOffsetMm));
                        }
                        doc.Regenerate();
                        int added = 0;
                        var grid = wall.CurtainGrid;
                        if (grid != null)
                            foreach (var tt in gridT)
                            {
                                try { grid.AddGridLine(false, a + d * tt + new XYZ(0, 0, MmToFt(heightMm) / 2), false); added++; }
                                catch (Exception ex) { if (added == 0) warnings.Add($"grid line failed: {ex.Message}"); }
                            }
                        desc["id"] = wall.Id.Value; desc["type"] = cwType.Name; desc["grid_lines_added"] = added;
                        created.Add(desc);
                    }
                    catch (Exception ex) { failures.Add($"curtain wall {Math.Round(FtToMm(L))} mm at ({Math.Round(FtToMm(a.X))}, {Math.Round(FtToMm(a.Y))}): {ex.Message}"); }
                }
                if (t != null) CommitOrThrow(t, "curtain walls from CAD");
            }
            finally { if (t != null && t.HasStarted() && !t.HasEnded()) t.RollBack(); t?.Dispose(); }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun, ["link"] = ImportName(doc, li), ["layers"] = layers.ToList(), ["level"] = level.Name,
                ["glazing_segments"] = segs.Count, ["mullion_symbols"] = mullionPts.Count, ["runs"] = finalRuns.Count,
                ["height_mm"] = heightMm > 0 ? Math.Round(heightMm) : (object)null, ["top_level"] = topLevel?.Name,
                ["curtain_walls_planned"] = created.Count, ["curtain_walls_created"] = dryRun ? 0 : created.Count(d => d.ContainsKey("id")), ["skipped_existing"] = skipped,
                ["base_type"] = baseType.Name, ["type_created"] = typeCreated, ["mullion_type"] = mullionType?.Name,
                ["curtain_types_available"] = cwTypes.Select(x => x.Name).ToList(),
                ["mullion_types_available"] = mullionTypes.Select(x => x.Name).Take(30).ToList(),
                ["curtain_walls"] = created, ["failures"] = failures, ["warnings"] = warnings,
            };
        }
    }
}
