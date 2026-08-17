using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// CAD-link driven modelling: read the geometry of a linked/imported DWG
    /// (per layer) and turn it into Revit elements.
    ///
    ///  - list_cad_links         : ImportInstances + per-layer entity statistics
    ///  - get_cad_geometry       : raw curves of chosen layers, in mm (LLM-readable)
    ///  - create_walls_from_cad  : pair parallel wall lines -> centerlines -> Wall.Create
    ///
    /// All 2D maths here is done in millimetres in the model's XY plane; the DWG
    /// geometry is taken from GeometryInstance.GetInstanceGeometry(), which is
    /// already in model coordinates (link placement applied).
    /// </summary>
    public static partial class CommandRouter
    {
        // ==================== CAD geometry access ============================

        private sealed class CadPrimitive
        {
            public string Layer;
            public GeometryObject Geo;
        }

        private static ImportInstance RequireImport(Document doc, long id)
        {
            var li = doc.GetElement(new ElementId(id)) as ImportInstance;
            if (li == null)
                throw new McpException(McpException.NotFound,
                    $"Element {id} is not a CAD link/import (see list_cad_links).");
            return li;
        }

        private static string ImportName(Document doc, ImportInstance li)
        {
            var type = doc.GetElement(li.GetTypeId());
            return type?.Name ?? li.Name;
        }

        /// <summary>All primitives of the import with their layer name (model coordinates).</summary>
        private static List<CadPrimitive> EnumerateCad(Document doc, ImportInstance li)
        {
            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
            if (li.ViewSpecific)
            {
                var v = doc.GetElement(li.OwnerViewId) as View;
                if (v != null) opt.View = v;
            }

            var result = new List<CadPrimitive>();
            var layerCache = new Dictionary<ElementId, string>();
            var geo = li.get_Geometry(opt);
            if (geo == null) return result;

            void Walk(IEnumerable<GeometryObject> objs, Transform xf)
            {
                foreach (var g in objs)
                {
                    if (g is GeometryInstance inst)
                    {
                        // GetInstanceGeometry already applies the instance transform.
                        Walk(inst.GetInstanceGeometry(), xf);
                        continue;
                    }
                    if (!layerCache.TryGetValue(g.GraphicsStyleId, out var layer))
                    {
                        var gs = doc.GetElement(g.GraphicsStyleId) as GraphicsStyle;
                        layer = gs?.GraphicsStyleCategory?.Name ?? "?";
                        layerCache[g.GraphicsStyleId] = layer;
                    }
                    result.Add(new CadPrimitive { Layer = layer, Geo = g });
                }
            }
            Walk(geo, Transform.Identity);
            return result;
        }

        private static string Kind(GeometryObject g)
        {
            switch (g)
            {
                case Line _:     return "line";
                case Arc a:      return a.IsBound ? "arc" : "circle";
                case PolyLine _: return "polyline";
                case Ellipse _:  return "ellipse";
                case Curve _:    return "curve";
                case Solid _:    return "solid";
                case Point _:    return "point";
                case Mesh _:     return "mesh";
                default:         return g.GetType().Name.ToLowerInvariant();
            }
        }

        // 2D bbox in mm: [[xmin, ymin], [xmax, ymax]] (optional).
        private static double[] GetOptBboxMm(Dictionary<string, object> p, string key = "bbox_mm")
        {
            if (!p.ContainsKey(key) || p[key] == null) return null;
            if (!(p[key] is IEnumerable seq) || p[key] is string)
                throw new McpException(McpException.BadRequest, $"'{key}' must be [[xmin, ymin], [xmax, ymax]] in mm.");
            var pts = new List<double[]>();
            foreach (var item in seq)
            {
                if (!(item is IEnumerable pair) || item is string) continue;
                var nums = pair.Cast<object>().Select(Convert.ToDouble).ToArray();
                if (nums.Length >= 2) pts.Add(nums);
            }
            if (pts.Count != 2)
                throw new McpException(McpException.BadRequest, $"'{key}' must be [[xmin, ymin], [xmax, ymax]] in mm.");
            return new[]
            {
                Math.Min(pts[0][0], pts[1][0]), Math.Min(pts[0][1], pts[1][1]),
                Math.Max(pts[0][0], pts[1][0]), Math.Max(pts[0][1], pts[1][1])
            };
        }

        private static bool InBbox(double[] b, double xMm, double yMm)
            => b == null || (xMm >= b[0] && xMm <= b[2] && yMm >= b[1] && yMm <= b[3]);

        private static HashSet<string> GetLayerSet(Dictionary<string, object> p)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (p.ContainsKey("layers") && p["layers"] is IEnumerable seq && !(p["layers"] is string))
                foreach (var item in seq) if (item != null) set.Add(Convert.ToString(item));
            if (p.ContainsKey("layer") && p["layer"] != null)
                set.Add(Convert.ToString(p["layer"]));
            return set;
        }

        private static double[] Pt2(XYZ p) => new[] { Math.Round(FtToMm(p.X), 1), Math.Round(FtToMm(p.Y), 1) };

        // ==================== list_cad_links =================================

        private static Dictionary<string, object> ListCadLinks(Document doc, bool includeLayers, int layerLimit)
        {
            var links = new List<Dictionary<string, object>>();
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                var entry = new Dictionary<string, object>
                {
                    ["id"] = li.Id.Value,
                    ["name"] = ImportName(doc, li),
                    ["isLinked"] = li.IsLinked,
                    ["viewSpecific"] = li.ViewSpecific,
                    ["ownerView"] = li.ViewSpecific ? doc.GetElement(li.OwnerViewId)?.Name : null,
                    ["pinned"] = li.Pinned
                };
                if (includeLayers)
                {
                    var stats = new Dictionary<string, int[]>(); // lines, arcs, polylines, other
                    foreach (var prim in EnumerateCad(doc, li))
                    {
                        if (!stats.TryGetValue(prim.Layer, out var s)) stats[prim.Layer] = s = new int[4];
                        switch (Kind(prim.Geo))
                        {
                            case "line": s[0]++; break;
                            case "arc": case "circle": s[1]++; break;
                            case "polyline": s[2]++; break;
                            default: s[3]++; break;
                        }
                    }
                    var layers = stats
                        .OrderByDescending(kv => kv.Value.Sum())
                        .Take(layerLimit)
                        .Select(kv => new Dictionary<string, object>
                        {
                            ["name"] = kv.Key,
                            ["lines"] = kv.Value[0],
                            ["arcs"] = kv.Value[1],
                            ["polylines"] = kv.Value[2],
                            ["other"] = kv.Value[3]
                        }).ToList();
                    entry["layerCount"] = stats.Count;
                    entry["layers"] = layers;
                }
                links.Add(entry);
            }
            return new Dictionary<string, object> { ["count"] = links.Count, ["links"] = links };
        }

        // ==================== get_cad_geometry ===============================

        private static Dictionary<string, object> GetCadGeometry(Document doc, Dictionary<string, object> p)
        {
            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            var bbox = GetOptBboxMm(p);
            int limit = Math.Min(Math.Max(GetIntOr(p, "limit", 500), 1), 5000);

            var curves = new List<Dictionary<string, object>>();
            var skipped = new Dictionary<string, int>();
            int total = 0;

            foreach (var prim in EnumerateCad(doc, li))
            {
                if (layers.Count > 0 && !layers.Contains(prim.Layer)) continue;

                var kind = Kind(prim.Geo);
                Dictionary<string, object> item = null;
                List<XYZ> probe = null; // points used for the bbox test

                switch (prim.Geo)
                {
                    case Line ln:
                        probe = new List<XYZ> { ln.GetEndPoint(0), ln.GetEndPoint(1) };
                        item = new Dictionary<string, object>
                        {
                            ["kind"] = "line", ["layer"] = prim.Layer,
                            ["start"] = Pt2(ln.GetEndPoint(0)), ["end"] = Pt2(ln.GetEndPoint(1)),
                            ["length_mm"] = Math.Round(FtToMm(ln.Length), 1)
                        };
                        break;
                    case Arc arc:
                        if (arc.IsBound)
                        {
                            probe = new List<XYZ> { arc.GetEndPoint(0), arc.GetEndPoint(1), arc.Evaluate(0.5, true) };
                            item = new Dictionary<string, object>
                            {
                                ["kind"] = "arc", ["layer"] = prim.Layer,
                                ["center"] = Pt2(arc.Center), ["radius_mm"] = Math.Round(FtToMm(arc.Radius), 1),
                                ["start"] = Pt2(arc.GetEndPoint(0)), ["end"] = Pt2(arc.GetEndPoint(1)),
                                ["mid"] = Pt2(arc.Evaluate(0.5, true)),
                                ["sweep_deg"] = Math.Round((arc.GetEndParameter(1) - arc.GetEndParameter(0)) * 180.0 / Math.PI, 1)
                            };
                        }
                        else
                        {
                            probe = new List<XYZ> { arc.Center };
                            item = new Dictionary<string, object>
                            {
                                ["kind"] = "circle", ["layer"] = prim.Layer,
                                ["center"] = Pt2(arc.Center), ["radius_mm"] = Math.Round(FtToMm(arc.Radius), 1)
                            };
                        }
                        break;
                    case PolyLine pl:
                        var pts = pl.GetCoordinates();
                        probe = pts.ToList();
                        bool closed = pts.Count > 2 && pts[0].DistanceTo(pts[pts.Count - 1]) < MmToFt(0.5);
                        item = new Dictionary<string, object>
                        {
                            ["kind"] = "polyline", ["layer"] = prim.Layer,
                            ["points"] = pts.Select(Pt2).ToList(), ["closed"] = closed
                        };
                        break;
                    case Curve c when c.IsBound:
                        probe = new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) };
                        item = new Dictionary<string, object>
                        {
                            ["kind"] = kind, ["layer"] = prim.Layer,
                            ["start"] = Pt2(c.GetEndPoint(0)), ["end"] = Pt2(c.GetEndPoint(1))
                        };
                        break;
                    default:
                        skipped[kind] = skipped.ContainsKey(kind) ? skipped[kind] + 1 : 1;
                        continue;
                }

                if (bbox != null && !probe.Any(pt => InBbox(bbox, FtToMm(pt.X), FtToMm(pt.Y)))) continue;

                total++;
                if (curves.Count < limit) curves.Add(item);
            }

            return new Dictionary<string, object>
            {
                ["link"] = ImportName(doc, li),
                ["count"] = total,
                ["returned"] = curves.Count,
                ["truncated"] = total > curves.Count,
                ["skipped"] = skipped,
                ["curves"] = curves
            };
        }

        // ==================== create_walls_from_cad ==========================

        // A straight segment in mm (model XY), plus its line-equation form.
        private sealed class Seg
        {
            public double X1, Y1, X2, Y2;
            public double Angle;   // direction angle in degrees, normalised to [0, 180)
            public double Dx, Dy;  // unit direction
            public double Nx, Ny;  // unit normal
            public double Offset;  // n . p  (perpendicular distance of the line from origin)
            public double T1, T2;  // d . p  (parameter range along the direction), T1 < T2
            public int Source;     // which CAD entity it came from (one id per polyline / line)
            public bool Paired;
            public List<double[]> Claims = new List<double[]>(); // param ranges already used by same-source walls
        }

        private sealed class PairCandidate
        {
            public Seg A, B; public double Lo, Hi, Thickness;
            public bool SameSource => A.Source == B.Source;
        }

        private sealed class WallPiece
        {
            public double Dx, Dy, Nx, Ny; // shared direction of the pair
            public double Offset;         // centerline offset
            public double T1, T2;         // extent along the direction
            public double Thickness;
        }

        private static readonly double[] DefaultThicknessesMm =
            { 100, 120, 150, 180, 200, 240, 250, 300, 350, 370, 400 };

        private static Seg MakeSeg(double x1, double y1, double x2, double y2)
        {
            var dx = x2 - x1; var dy = y2 - y1;
            var len = Math.Sqrt(dx * dx + dy * dy);
            var ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (ang < 0) ang += 180.0;
            if (ang >= 180.0 - 1e-9) ang -= 180.0;
            var rad = ang * Math.PI / 180.0;
            var s = new Seg
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Angle = ang,
                Dx = Math.Cos(rad), Dy = Math.Sin(rad),
                Nx = -Math.Sin(rad), Ny = Math.Cos(rad)
            };
            s.Offset = s.Nx * x1 + s.Ny * y1;
            var t1 = s.Dx * x1 + s.Dy * y1;
            var t2 = s.Dx * x2 + s.Dy * y2;
            s.T1 = Math.Min(t1, t2); s.T2 = Math.Max(t1, t2);
            return s;
        }

        private static double GetDoubleOr(Dictionary<string, object> p, string key, double fallback)
        {
            if (!p.ContainsKey(key) || p[key] == null) return fallback;
            try { return Convert.ToDouble(p[key]); }
            catch { return fallback; }
        }

        private static List<double> GetDoubleListOr(Dictionary<string, object> p, string key, double[] fallback)
        {
            if (!p.ContainsKey(key) || p[key] == null || !(p[key] is IEnumerable seq) || p[key] is string)
                return fallback.ToList();
            var list = seq.Cast<object>().Where(o => o != null).Select(Convert.ToDouble).ToList();
            return list.Count > 0 ? list : fallback.ToList();
        }

        private static Dictionary<string, object> CreateWallsFromCad(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", false);
            if (!dryRun) EnsureWritable();

            var li = RequireImport(doc, GetLong(p, "link_id"));
            var layers = GetLayerSet(p);
            if (layers.Count == 0)
                throw new McpException(McpException.BadRequest, "Provide the wall layer(s) via 'layer' or 'layers'.");
            var level = doc.GetElement(new ElementId(GetLong(p, "level_id"))) as Level
                        ?? throw new McpException(McpException.NotFound, "'level_id' is not a Level (see list_levels).");
            var heightMm = GetDouble(p, "height_mm");
            var bbox = GetOptBboxMm(p);
            var thicknesses = GetDoubleListOr(p, "thicknesses_mm", DefaultThicknessesMm);
            double tol = GetDoubleOr(p, "tolerance_mm", 5.0);
            double angleTol = GetDoubleOr(p, "angle_tolerance_deg", 0.5);
            double minLen = GetDoubleOr(p, "min_length_mm", 300.0);
            double mergeGap = GetDoubleOr(p, "merge_gap_mm", 300.0);   // small: windows must stay open
            double laneTol = GetDoubleOr(p, "merge_offset_tolerance_mm", 25.0); // collinear pieces may be this far apart sideways
            int maxWalls = GetIntOr(p, "max_walls", 1000);
            var warnings = new List<string>();

            // ---- 1. collect straight segments from the wall layers -----------
            var segs = new List<Seg>();
            var cadArcs = new List<CadArc>();
            int arcCount = 0, otherCount = 0, polylineCount = 0, lineCount = 0, source = 0;
            foreach (var prim in EnumerateCad(doc, li))
            {
                if (!layers.Contains(prim.Layer)) continue;
                source++;
                switch (prim.Geo)
                {
                    case Line ln:
                        lineCount++;
                        AddSeg(segs, ln.GetEndPoint(0), ln.GetEndPoint(1), bbox, source);
                        break;
                    case PolyLine pl:
                        polylineCount++;
                        var pts = pl.GetCoordinates();
                        for (int i = 0; i + 1 < pts.Count; i++) AddSeg(segs, pts[i], pts[i + 1], bbox, source);
                        break;
                    case Arc arc when arc.IsBound:
                        arcCount++;
                        AddArc(cadArcs, arc, bbox, source);
                        break;
                    default:
                        otherCount++;
                        break;
                }
            }

            // Door layers: a door SWING ARC inside a gap is evidence that the gap is a
            // door opening, so the wall may run through it (the door cuts it later).
            // Lines/polylines on those layers (windows, leaf rectangles) are NOT
            // evidence: window openings stay as gaps in the wall.
            var doorLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "door_layers", "opening_layers" })
                if (p.ContainsKey(key) && p[key] is IEnumerable ol && !(p[key] is string))
                    foreach (var item in ol) if (item != null) doorLayers.Add(Convert.ToString(item));
            double maxOpening = GetDoubleOr(p, "max_opening_mm", 3000.0);
            // Each swing arc: hinge (centre), the two end points, leaf width.
            var doorArcs = new List<double[]>(); // hx, hy, e1x, e1y, e2x, e2y, r
            if (doorLayers.Count > 0)
            {
                foreach (var prim in EnumerateCad(doc, li))
                {
                    if (!doorLayers.Contains(prim.Layer) || !(prim.Geo is Arc darc) || !darc.IsBound) continue;
                    var sweep = (darc.GetEndParameter(1) - darc.GetEndParameter(0)) * 180.0 / Math.PI;
                    if (sweep < 45 || sweep > 200) continue; // door swings are ~90 (or 180) degrees
                    var r = FtToMm(darc.Radius);
                    if (r < 250 || r > 3000) continue;
                    doorArcs.Add(new[]
                    {
                        FtToMm(darc.Center.X), FtToMm(darc.Center.Y),
                        FtToMm(darc.GetEndPoint(0).X), FtToMm(darc.GetEndPoint(0).Y),
                        FtToMm(darc.GetEndPoint(1).X), FtToMm(darc.GetEndPoint(1).Y), r
                    });
                }
            }
            // Does a door that BELONGS TO wall w sit in the gap ta..tb? Belonging means:
            // hinge and latch (the arc end on the wall) both lie in w's corridor, the
            // closed leaf (hinge -> latch) runs along w, and both are inside the gap.
            // A door of a crossing/nearby wall must not bridge this wall's gap.
            bool OpeningBetween(WallPiece w, double ta, double tb)
            {
                if (doorArcs.Count == 0) return false;
                var lo = Math.Min(ta, tb) - 150; var hi = Math.Max(ta, tb) + 150;
                var half = w.Thickness / 2 + 60; // hinge/latch may sit on a face or the centreline
                foreach (var a in doorArcs)
                {
                    var offH = Math.Abs(w.Nx * a[0] + w.Ny * a[1] - w.Offset);
                    if (offH > half) continue;
                    var tH = w.Dx * a[0] + w.Dy * a[1];
                    if (tH < lo || tH > hi) continue;
                    // latch = the arc end nearer the wall line
                    var off1 = Math.Abs(w.Nx * a[2] + w.Ny * a[3] - w.Offset);
                    var off2 = Math.Abs(w.Nx * a[4] + w.Ny * a[5] - w.Offset);
                    double lx, ly, offL;
                    if (off1 <= off2) { lx = a[2]; ly = a[3]; offL = off1; } else { lx = a[4]; ly = a[5]; offL = off2; }
                    if (offL > half) continue;
                    var tL = w.Dx * lx + w.Dy * ly;
                    if (tL < lo || tL > hi) continue;
                    // closed leaf must run along the wall (not across it)
                    var cx = lx - a[0]; var cy = ly - a[1]; var clen = Math.Sqrt(cx * cx + cy * cy);
                    if (clen < 1) continue;
                    if (Math.Abs((cx * w.Dx + cy * w.Dy) / clen) < 0.97) continue;
                    return true;
                }
                return false;
            }
            if (segs.Count == 0)
                throw new McpException(McpException.NotFound,
                    $"No straight wall geometry found on layer(s) {string.Join(", ", layers)}" +
                    (bbox != null ? " inside bbox_mm." : "."));

            // ---- 2. pair parallel segments at a wall thickness apart ----------
            double maxT = thicknesses.Max() + tol;
            var pieces = new List<WallPiece>();
            var byAngle = segs.OrderBy(s => s.Angle).ToList();

            // Group by direction. Angles near 180 wrap to near 0: treat those as one group
            // by testing both plain and wrapped differences.
            // Split into direction groups wherever consecutive angles differ by more
            // than angleTol (gap-based, so results don't depend on which segment
            // happens to start a group).
            var groups = new List<List<Seg>>();
            foreach (var s in byAngle)
            {
                var g = groups.LastOrDefault();
                if (g != null && Math.Abs(s.Angle - g[g.Count - 1].Angle) <= angleTol) g.Add(s);
                else groups.Add(new List<Seg> { s });
            }
            if (groups.Count > 1)
            {
                var first = groups[0]; var last = groups[groups.Count - 1];
                if (Math.Abs(last[last.Count - 1].Angle - 180.0 - first[0].Angle) <= angleTol)
                {
                    // Same direction (a line at 179.8 deg is a line at -0.2 deg): merge into the
                    // first group; every group is re-framed to a common direction below.
                    first.AddRange(last);
                    groups.RemoveAt(groups.Count - 1);
                }
            }

            foreach (var g in groups)
            {
                if (g.Count < 2) continue;
                // Use one common frame per group (its median direction) so offsets are comparable.
                var refSeg = g.OrderBy(x => x.Angle).ElementAt(g.Count / 2);
                foreach (var s in g)
                {
                    s.Dx = refSeg.Dx; s.Dy = refSeg.Dy; s.Nx = refSeg.Nx; s.Ny = refSeg.Ny;
                    s.Offset = s.Nx * s.X1 + s.Ny * s.Y1;
                    var t1 = s.Dx * s.X1 + s.Dy * s.Y1; var t2 = s.Dx * s.X2 + s.Dy * s.Y2;
                    s.T1 = Math.Min(t1, t2); s.T2 = Math.Max(t1, t2);
                }
                var sorted = g.OrderBy(s => s.Offset).ToList();
                var candidates = new List<PairCandidate>();
                for (int i = 0; i < sorted.Count; i++)
                {
                    var a = sorted[i];
                    for (int j = i + 1; j < sorted.Count; j++)
                    {
                        var b = sorted[j];
                        var dc = b.Offset - a.Offset;
                        if (dc > maxT) break;
                        if (dc < thicknesses.Min() - tol) continue;
                        double thick = -1;
                        foreach (var t in thicknesses)
                            if (Math.Abs(dc - t) <= tol) { thick = t; break; }
                        if (thick < 0) continue;
                        var lo = Math.Max(a.T1, b.T1);
                        var hi = Math.Min(a.T2, b.T2);
                        if (hi - lo < minLen) continue;

                        // Two wall faces have nothing between them: reject the pair if a
                        // third parallel segment runs between a and b over this range AND
                        // is far enough from both faces to be a wall face itself. Finish /
                        // plaster lines hugging a face (closer than the thinnest wall) are
                        // ignored so a 250 outline with 44 mm inner lines still pairs.
                        double minFace = thicknesses.Min() - tol;
                        double mid = (a.Offset + b.Offset) / 2;
                        bool blocked = false;
                        for (int k = i + 1; k < j && !blocked; k++)
                        {
                            var c = sorted[k];
                            if (c.Offset - a.Offset < minFace || b.Offset - c.Offset < minFace) continue;
                            // A line exactly half-way is the wall's own drawn centreline
                            // (faces + centreline convention), not another face.
                            if (Math.Abs(c.Offset - mid) <= tol) continue;
                            var ov = Math.Min(hi, c.T2) - Math.Max(lo, c.T1);
                            if (ov > 0.3 * (hi - lo)) blocked = true;
                        }
                        if (blocked) continue;

                        candidates.Add(new PairCandidate { A = a, B = b, Lo = lo, Hi = hi, Thickness = thick });
                    }
                }

                // Faces of one polyline outline belong together: accept those first, then
                // cross-entity pairs only where at least one face is still unclaimed
                // (kills the phantom "wall" between two back-to-back outlines).
                foreach (var c in candidates.OrderByDescending(c => c.SameSource).ThenByDescending(c => c.Hi - c.Lo))
                {
                    if (!c.SameSource && Claimed(c.A, c.Lo, c.Hi) && Claimed(c.B, c.Lo, c.Hi)) continue;
                    if (c.SameSource)
                    {
                        c.A.Claims.Add(new[] { c.Lo, c.Hi });
                        c.B.Claims.Add(new[] { c.Lo, c.Hi });
                    }
                    c.A.Paired = c.B.Paired = true;
                    pieces.Add(new WallPiece
                    {
                        Dx = refSeg.Dx, Dy = refSeg.Dy, Nx = refSeg.Nx, Ny = refSeg.Ny,
                        Offset = (c.A.Offset + c.B.Offset) / 2.0, T1 = c.Lo, T2 = c.Hi, Thickness = c.Thickness
                    });
                }
            }
            var unpairedSegs = segs.Where(s => !s.Paired && Math.Sqrt((s.X2 - s.X1) * (s.X2 - s.X1) + (s.Y2 - s.Y1) * (s.Y2 - s.Y1)) >= minLen).ToList();
            int unpaired = unpairedSegs.Count;

            // ---- 3. merge collinear pieces (same direction, thickness, offset) --
            var merged = new List<WallPiece>();
            // Cluster pieces by direction (angle gaps <= angleTol) and put every piece of a
            // cluster into one common frame, so near-collinear pieces from different
            // segment groups still land in the same lane.
            double PieceAngle(WallPiece w) { var a = Math.Atan2(w.Dy, w.Dx) * 180.0 / Math.PI; if (a < 0) a += 180; if (a >= 180 - 1e-9) a -= 180; return a; }
            var clusters = new List<List<WallPiece>>();
            foreach (var w in pieces.OrderBy(PieceAngle))
            {
                var c = clusters.LastOrDefault();
                if (c != null && Math.Abs(PieceAngle(w) - PieceAngle(c[c.Count - 1])) <= angleTol) c.Add(w);
                else clusters.Add(new List<WallPiece> { w });
            }
            if (clusters.Count > 1)
            {
                var first = clusters[0]; var last = clusters[clusters.Count - 1];
                if (Math.Abs(PieceAngle(last[last.Count - 1]) - 180.0 - PieceAngle(first[0])) <= angleTol)
                { first.AddRange(last); clusters.RemoveAt(clusters.Count - 1); }
            }
            foreach (var cl in clusters)
            {
                var refP = cl.OrderBy(PieceAngle).ElementAt(cl.Count / 2);
                foreach (var w in cl)
                {
                    // endpoints in world coords, then re-frame
                    var sx = w.Dx * w.T1 + w.Nx * w.Offset; var sy = w.Dy * w.T1 + w.Ny * w.Offset;
                    var ex = w.Dx * w.T2 + w.Nx * w.Offset; var ey = w.Dy * w.T2 + w.Ny * w.Offset;
                    w.Dx = refP.Dx; w.Dy = refP.Dy; w.Nx = refP.Nx; w.Ny = refP.Ny;
                    w.Offset = (w.Nx * sx + w.Ny * sy + w.Nx * ex + w.Ny * ey) / 2;
                    var t1 = w.Dx * sx + w.Dy * sy; var t2 = w.Dx * ex + w.Dy * ey;
                    w.T1 = Math.Min(t1, t2); w.T2 = Math.Max(t1, t2);
                }
            }
            foreach (var grp in pieces.GroupBy(w => new { Dx = Math.Round(w.Dx, 6), Dy = Math.Round(w.Dy, 6), w.Thickness }))
            {
                var byOffset = grp.OrderBy(w => w.Offset).ThenBy(w => w.T1).ToList();
                var lane = new List<WallPiece>();
                void FlushLane()
                {
                    if (lane.Count == 0) return;
                    // Pieces in a lane may be a few mm apart sideways (CAD sloppiness);
                    // put them all on the longest piece's centerline.
                    var refOff = lane.OrderByDescending(x => x.T2 - x.T1).First().Offset;
                    WallPiece cur = null;
                    foreach (var w in lane.OrderBy(x => x.T1))
                    {
                        if (cur != null && (w.T1 <= cur.T2 + mergeGap ||
                                            (w.T1 <= cur.T2 + maxOpening && OpeningBetween(cur, cur.T2, w.T1))))
                        {
                            cur.T2 = Math.Max(cur.T2, w.T2);
                            continue;
                        }
                        cur = new WallPiece { Dx = w.Dx, Dy = w.Dy, Nx = w.Nx, Ny = w.Ny, Offset = refOff, T1 = w.T1, T2 = w.T2, Thickness = w.Thickness };
                        merged.Add(cur);
                    }
                    lane.Clear();
                }
                foreach (var w in byOffset)
                {
                    if (lane.Count > 0 && Math.Abs(w.Offset - lane[0].Offset) > laneTol) FlushLane();
                    lane.Add(w);
                }
                FlushLane();
            }
            merged = merged.Where(w => w.T2 - w.T1 >= minLen).ToList();

            // ---- 3a. drop near-duplicates: a piece running (almost) on top of a longer
            // one - same direction, centerlines closer than half a thickness, >= 80 %
            // covered - is CAD clutter (finish lines pairing with faces), not a wall.
            int dedup = 0;
            {
                var kept = new List<WallPiece>();
                foreach (var w in merged.OrderByDescending(w => (w.T2 - w.T1) * 1000 + w.Thickness))
                {
                    bool dup = false;
                    foreach (var k in kept)
                    {
                        if (Math.Abs(k.Dx * w.Dx + k.Dy * w.Dy) < 0.9999) continue; // different direction
                        // express w's centerline offset in k's frame (frames may be flipped)
                        var wx = w.Nx * w.Offset; var wy = w.Ny * w.Offset;
                        var off = k.Nx * wx + k.Ny * wy;
                        if (Math.Abs(off - k.Offset) > Math.Max(k.Thickness, w.Thickness) / 2) continue;
                        // param range of w in k's frame
                        var sx = w.Dx * w.T1 + wx; var sy = w.Dy * w.T1 + wy;
                        var ex = w.Dx * w.T2 + wx; var ey = w.Dy * w.T2 + wy;
                        var t1 = k.Dx * sx + k.Dy * sy; var t2 = k.Dx * ex + k.Dy * ey;
                        var lo = Math.Min(t1, t2); var hi = Math.Max(t1, t2);
                        var cover = Math.Min(hi, k.T2) - Math.Max(lo, k.T1);
                        if (cover >= 0.8 * (hi - lo)) { dup = true; break; }
                    }
                    if (dup) dedup++; else kept.Add(w);
                }
                merged = kept;
            }

            // ---- 3a2. close door gaps between collinear pieces of DIFFERENT thickness --
            // (200 wall, door, 250 wall): they can't share a lane, so extend the earlier
            // piece to the later piece's start when a door sits in the gap. Both keep
            // their thickness; Revit joins them end to end.
            int crossThickBridged = 0;
            {
                var byDir = merged.GroupBy(w => new { Dx = Math.Round(w.Dx, 6), Dy = Math.Round(w.Dy, 6) });
                foreach (var grp in byDir)
                {
                    var list = grp.OrderBy(w => w.Offset).ThenBy(w => w.T1).ToList();
                    for (int i = 0; i < list.Count; i++)
                        for (int k = i + 1; k < list.Count; k++)
                        {
                            var a = list[i]; var b = list[k];
                            if (b.Offset - a.Offset > laneTol) break;
                            if (a.Thickness == b.Thickness) continue;
                            WallPiece lo = a.T2 <= b.T1 ? a : b, hi = lo == a ? b : a;
                            var gap = hi.T1 - lo.T2;
                            if (gap <= 0 || gap > maxOpening) continue;
                            if (!OpeningBetween(lo, lo.T2, hi.T1)) continue;
                            lo.T2 = hi.T1; crossThickBridged++;
                        }
                }
            }

            // ---- 3b. snap ends to the centerline of the wall they run into ------
            // CAD faces stop at the *face* of the crossing wall, so a centerline ends
            // half a thickness short of (or past) the other centerline. Extend/trim
            // to the intersection so Revit joins the walls (L and T junctions).
            int snapped = 0, extended = 0;
            if (GetBoolOr(p, "snap_ends", true))
            {
                var updates = new List<Tuple<WallPiece, int, double>>();
                foreach (var w in merged)
                {
                    foreach (var o in merged)
                    {
                        if (ReferenceEquals(w, o)) continue;
                        var cross = w.Dx * o.Dy - w.Dy * o.Dx;
                        if (Math.Abs(cross) < 0.05) continue; // parallel-ish: never snap
                        // Intersection of the two centerlines, expressed as params on w and o.
                        // Point on w: d_w * t + n_w * off_w ; on o: d_o * u + n_o * off_o.
                        var px = w.Nx * w.Offset; var py = w.Ny * w.Offset;
                        var qx = o.Nx * o.Offset; var qy = o.Ny * o.Offset;
                        var rx = qx - px; var ry = qy - py;
                        var t = (rx * o.Dy - ry * o.Dx) / cross;
                        var u = (rx * w.Dy - ry * w.Dx) / cross;
                        if (u < o.T1 - o.Thickness / 2 - tol || u > o.T2 + o.Thickness / 2 + tol) continue;
                        var snap = o.Thickness / 2 + tol;
                        if (Math.Abs(t - w.T1) <= snap) updates.Add(Tuple.Create(w, 0, t));
                        else if (t < w.T1 && w.T1 - t <= maxOpening && OpeningBetween(w, t, w.T1))
                        { updates.Add(Tuple.Create(w, 0, t)); extended++; }
                        if (Math.Abs(t - w.T2) <= snap) updates.Add(Tuple.Create(w, 1, t));
                        else if (t > w.T2 && t - w.T2 <= maxOpening && OpeningBetween(w, w.T2, t))
                        { updates.Add(Tuple.Create(w, 1, t)); extended++; }
                    }
                }
                foreach (var up in updates)
                {
                    if (up.Item2 == 0) { if (Math.Abs(up.Item1.T1 - up.Item3) > 1e-6) { up.Item1.T1 = up.Item3; snapped++; } }
                    else               { if (Math.Abs(up.Item1.T2 - up.Item3) > 1e-6) { up.Item1.T2 = up.Item3; snapped++; } }
                }
                merged = merged.Where(w => w.T2 - w.T1 >= minLen / 2).ToList();
            }

            // ---- 3c. curved walls: concentric arcs a thickness apart --------------
            var curved = new List<CurvedPiece>();
            int unpairedArcs = 0;
            {
                var usedArc = new HashSet<CadArc>();
                for (int i = 0; i < cadArcs.Count; i++)
                {
                    var a = cadArcs[i];
                    CadArc bestB = null; double bestThick = 0, bestLo = 0, bestHi = 0, bestScore = double.MaxValue;
                    for (int j = 0; j < cadArcs.Count; j++)
                    {
                        if (i == j) continue;
                        var b = cadArcs[j];
                        if (usedArc.Contains(b)) continue;
                        if (Math.Abs(a.Cx - b.Cx) > tol * 2 || Math.Abs(a.Cy - b.Cy) > tol * 2) continue;
                        var dr = Math.Abs(a.R - b.R);
                        double thick = -1;
                        foreach (var tk in thicknesses) if (Math.Abs(dr - tk) <= tol) { thick = tk; break; }
                        if (thick < 0) continue;
                        // angular overlap (intervals are CCW [A0, A1] with A1 > A0; try +-2pi shifts)
                        double lo = 0, hi = 0, best = -1;
                        foreach (var shift in new[] { -2 * Math.PI, 0.0, 2 * Math.PI })
                        {
                            var l = Math.Max(a.A0, b.A0 + shift); var h = Math.Min(a.A1, b.A1 + shift);
                            if (h - l > best) { best = h - l; lo = l; hi = h; }
                        }
                        var midR = (a.R + b.R) / 2;
                        if (best <= 0 || best * midR < minLen) continue;
                        // prefer the closest radius partner (adjacent faces)
                        var score = dr;
                        if (score < bestScore) { bestScore = score; bestB = b; bestThick = thick; bestLo = lo; bestHi = hi; }
                    }
                    if (bestB == null) { if (!usedArc.Contains(a)) unpairedArcs++; continue; }
                    usedArc.Add(a); usedArc.Add(bestB);
                    curved.Add(new CurvedPiece
                    {
                        Cx = (a.Cx + bestB.Cx) / 2, Cy = (a.Cy + bestB.Cy) / 2,
                        R = (a.R + bestB.R) / 2, A0 = bestLo, A1 = bestHi, Thickness = bestThick
                    });
                }
            }

            // ---- 3d. skip walls that already exist (re-runs / overlapping regions) ---
            int skippedExisting = 0;
            if (GetBoolOr(p, "skip_existing", true))
            {
                double snapD = GetDoubleOr(p, "existing_tolerance_mm", 100.0);
                var existing = new List<double[]>();   // x1,y1,x2,y2 (mm) of straight walls on this level
                var existingArcs = new List<double[]>(); // cx,cy,r,a0,a1
                foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    if (w.LevelId != level.Id || !(w.Location is LocationCurve lc)) continue;
                    if (lc.Curve is Line ln)
                        existing.Add(new[] { FtToMm(ln.GetEndPoint(0).X), FtToMm(ln.GetEndPoint(0).Y), FtToMm(ln.GetEndPoint(1).X), FtToMm(ln.GetEndPoint(1).Y) });
                    else if (lc.Curve is Arc ea)
                        existingArcs.Add(new[] { FtToMm(ea.Center.X), FtToMm(ea.Center.Y), FtToMm(ea.Radius),
                                                 FtToMm(ea.GetEndPoint(0).X), FtToMm(ea.GetEndPoint(0).Y), FtToMm(ea.GetEndPoint(1).X), FtToMm(ea.GetEndPoint(1).Y) });
                }
                bool Near(double ax, double ay, double bx, double by) => Math.Abs(ax - bx) <= snapD && Math.Abs(ay - by) <= snapD;
                if (existing.Count > 0)
                {
                    var keep = new List<WallPiece>();
                    foreach (var w in merged)
                    {
                        var sx = w.Dx * w.T1 + w.Nx * w.Offset; var sy = w.Dy * w.T1 + w.Ny * w.Offset;
                        var ex = w.Dx * w.T2 + w.Nx * w.Offset; var ey = w.Dy * w.T2 + w.Ny * w.Offset;
                        bool dup = existing.Any(e => (Near(sx, sy, e[0], e[1]) && Near(ex, ey, e[2], e[3])) ||
                                                     (Near(sx, sy, e[2], e[3]) && Near(ex, ey, e[0], e[1])));
                        if (dup) skippedExisting++; else keep.Add(w);
                    }
                    merged = keep;
                }
                if (existingArcs.Count > 0)
                {
                    var keep = new List<CurvedPiece>();
                    foreach (var c in curved)
                    {
                        double sx = c.Cx + c.R * Math.Cos(c.A0), sy = c.Cy + c.R * Math.Sin(c.A0);
                        double ex = c.Cx + c.R * Math.Cos(c.A1), ey = c.Cy + c.R * Math.Sin(c.A1);
                        bool dup = existingArcs.Any(e => Math.Abs(e[2] - c.R) <= snapD &&
                                                         ((Near(sx, sy, e[3], e[4]) && Near(ex, ey, e[5], e[6])) ||
                                                          (Near(sx, sy, e[5], e[6]) && Near(ex, ey, e[3], e[4]))));
                        if (dup) skippedExisting++; else keep.Add(c);
                    }
                    curved = keep;
                }
            }

            if (!dryRun && merged.Count + curved.Count > maxWalls)
                throw new McpException(McpException.BadRequest,
                    $"{merged.Count + curved.Count} walls would be created (max_walls={maxWalls}). Narrow bbox_mm or raise max_walls.");

            // ---- 4. resolve wall types by thickness --------------------------
            var basicTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Where(t => t.Kind == WallKind.Basic).ToList();
            var explicitMap = new Dictionary<double, ElementId>();
            if (p.ContainsKey("type_map") && p["type_map"] is IEnumerable tm && !(p["type_map"] is string))
                foreach (var item in tm)
                    if (item is Dictionary<string, object> d && d.ContainsKey("thickness_mm") && d.ContainsKey("type_id"))
                        explicitMap[Convert.ToDouble(d["thickness_mm"])] = new ElementId(Convert.ToInt64(d["type_id"]));
            bool createTypes = GetBoolOr(p, "create_missing_types", true);
            string aiTag = p.ContainsKey("ai_tag") && p["ai_tag"] != null ? Convert.ToString(p["ai_tag"]) : "_AI";
            var baseTypeId = GetOptLong(p, "base_type_id");
            double typeTol = GetDoubleOr(p, "type_tolerance_mm", 10.0);       // reuse an existing type this close
            int minPerType = GetIntOr(p, "min_walls_per_new_type", 3);        // fewer walls -> probably CAD noise, no new type
            var thicknessCounts = merged.GroupBy(w => w.Thickness).ToDictionary(g => g.Key, g => g.Count());
            foreach (var c in curved) thicknessCounts[c.Thickness] = (thicknessCounts.TryGetValue(c.Thickness, out var n0) ? n0 : 0) + 1;
            var createdTypes = new List<string>();
            var typeCache = new Dictionary<double, WallType>();
            WallType ResolveType(double thick)
            {
                if (typeCache.TryGetValue(thick, out var cached)) return cached;
                WallType chosen = null;
                foreach (var kv in explicitMap)
                    if (Math.Abs(kv.Key - thick) <= tol) chosen = doc.GetElement(kv.Value) as WallType;
                if (chosen == null && basicTypes.Count > 0)
                {
                    // Reuse anything within tolerance (AI-made types from earlier runs included).
                    var nearest = basicTypes.OrderBy(t => Math.Abs(FtToMm(t.Width) - thick)).First();
                    var diff = Math.Abs(FtToMm(nearest.Width) - thick);
                    int uses = thicknessCounts.TryGetValue(thick, out var n1) ? n1 : 0;
                    // Reuse within type_tolerance_mm, unless the existing type's width is itself a
                    // *different* nominal thickness (240 must not borrow the 250 type; a 95 mm
                    // partition may serve 100 mm walls because 95 is not a nominal value).
                    var nearestW = FtToMm(nearest.Width);
                    bool otherNominal = thicknesses.Any(tk => Math.Abs(tk - nearestW) <= tol && Math.Abs(tk - thick) > tol);
                    if (diff <= typeTol && !otherNominal) chosen = nearest;
                    else if (createTypes && uses < minPerType)
                    {
                        chosen = nearest;
                        warnings.Add($"{thick} mm: only {uses} wall(s) (min_walls_per_new_type={minPerType}) - treated as CAD noise, using '{nearest.Name}' ({Math.Round(FtToMm(nearest.Width), 1)} mm). Pass type_map to force a type.");
                    }
                    else if (createTypes && !dryRun)
                    {
                        var src = baseTypeId.HasValue ? doc.GetElement(new ElementId(baseTypeId.Value)) as WallType ?? nearest : nearest;
                        var name = DeriveTypeName(src.Name, FtToMm(src.Width), thick, aiTag);
                        var existing = basicTypes.FirstOrDefault(t => t.Name == name);
                        if (existing != null) chosen = existing;
                        else
                        {
                            try
                            {
                                var dup = src.Duplicate(name) as WallType;
                                var cs = dup.GetCompoundStructure();
                                int idx = cs.LayerCount == 1 ? 0 : cs.GetFirstCoreLayerIndex();
                                if (idx < 0)
                                {
                                    idx = 0;
                                    for (int k = 1; k < cs.LayerCount; k++) if (cs.GetLayerWidth(k) > cs.GetLayerWidth(idx)) idx = k;
                                }
                                var newLayer = FtToMm(cs.GetLayerWidth(idx)) + (thick - FtToMm(cs.GetWidth()));
                                if (newLayer <= 0) throw new InvalidOperationException("core layer would become <= 0 mm");
                                cs.SetLayerWidth(idx, MmToFt(newLayer));
                                dup.SetCompoundStructure(cs);
                                MarkAiType(dup, "create_walls_from_cad", src.Name);
                                basicTypes.Add(dup);
                                createdTypes.Add($"{name} ({thick} mm, from '{src.Name}')");
                                chosen = dup;
                            }
                            catch (Exception ex)
                            {
                                warnings.Add($"Could not create a {thick} mm wall type from '{src.Name}' ({ex.Message}); using '{nearest.Name}' ({Math.Round(FtToMm(nearest.Width), 1)} mm).");
                                chosen = nearest;
                            }
                        }
                    }
                    else
                    {
                        chosen = nearest;
                        warnings.Add(dryRun && createTypes
                            ? $"{thick} mm: no wall type of that thickness; '{DeriveTypeName(nearest.Name, FtToMm(nearest.Width), thick, aiTag)}' will be created from '{nearest.Name}'."
                            : $"No wall type is {thick} mm thick; using nearest '{nearest.Name}' ({Math.Round(FtToMm(nearest.Width), 1)} mm). Pass type_map to override.");
                    }
                }
                if (chosen == null)
                    throw new McpException(McpException.NotFound, "No basic wall types in the document.");
                typeCache[thick] = chosen;
                return chosen;
            }

            // ---- 5. create -------------------------------------------------------
            var walls = new List<Dictionary<string, object>>();
            var failures = new List<string>();
            double z = level.Elevation;

            Dictionary<string, object> Describe(WallPiece w, WallType wt, Wall created)
            {
                var sx = w.Dx * w.T1 + w.Nx * w.Offset; var sy = w.Dy * w.T1 + w.Ny * w.Offset;
                var ex = w.Dx * w.T2 + w.Nx * w.Offset; var ey = w.Dy * w.T2 + w.Ny * w.Offset;
                var d = new Dictionary<string, object>
                {
                    ["start"] = new[] { Math.Round(sx, 1), Math.Round(sy, 1) },
                    ["end"] = new[] { Math.Round(ex, 1), Math.Round(ey, 1) },
                    ["length_mm"] = Math.Round(w.T2 - w.T1, 1),
                    ["thickness_mm"] = w.Thickness,
                    ["type"] = wt.Name,
                    ["type_id"] = wt.Id.Value
                };
                if (created != null) d["id"] = created.Id.Value;
                return d;
            }

            Dictionary<string, object> DescribeCurved(CurvedPiece c, WallType wt, Wall created)
            {
                var d = new Dictionary<string, object>
                {
                    ["curved"] = true,
                    ["center"] = new[] { Math.Round(c.Cx, 1), Math.Round(c.Cy, 1) },
                    ["radius_mm"] = Math.Round(c.R, 1),
                    ["start"] = new[] { Math.Round(c.Cx + c.R * Math.Cos(c.A0), 1), Math.Round(c.Cy + c.R * Math.Sin(c.A0), 1) },
                    ["end"] = new[] { Math.Round(c.Cx + c.R * Math.Cos(c.A1), 1), Math.Round(c.Cy + c.R * Math.Sin(c.A1), 1) },
                    ["length_mm"] = Math.Round((c.A1 - c.A0) * c.R, 1),
                    ["thickness_mm"] = c.Thickness,
                    ["type"] = wt.Name,
                    ["type_id"] = wt.Id.Value
                };
                if (created != null) d["id"] = created.Id.Value;
                return d;
            }

            if (dryRun)
            {
                int cap = GetIntOr(p, "report_limit", 500);
                foreach (var w in merged.Take(cap)) walls.Add(Describe(w, ResolveType(w.Thickness), null));
                foreach (var c in curved.Take(cap)) walls.Add(DescribeCurved(c, ResolveType(c.Thickness), null));
            }
            else
            {
                // Normally we own the transaction; when called from inside an already
                // open one (e.g. via execute_code) just work within it.
                var ownTx = !doc.IsModifiable;
                var t = ownTx ? new Transaction(doc, "MCP: walls from CAD") : null;
                try
                {
                    if (ownTx)
                    {
                        t.Start();
                        var fh = t.GetFailureHandlingOptions();
                        fh.SetFailuresPreprocessor(new SilentFailures());
                        t.SetFailureHandlingOptions(fh);
                    }
                    foreach (var w in merged)
                    {
                        var wt = ResolveType(w.Thickness);
                        try
                        {
                            var s = new XYZ(MmToFt(w.Dx * w.T1 + w.Nx * w.Offset), MmToFt(w.Dy * w.T1 + w.Ny * w.Offset), z);
                            var e = new XYZ(MmToFt(w.Dx * w.T2 + w.Nx * w.Offset), MmToFt(w.Dy * w.T2 + w.Ny * w.Offset), z);
                            var wall = Wall.Create(doc, Line.CreateBound(s, e), wt.Id, level.Id, MmToFt(heightMm), 0, false, false);
                            walls.Add(Describe(w, wt, wall));
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{Math.Round(w.T2 - w.T1)} mm wall @ offset {Math.Round(w.Offset)}: {ex.Message}");
                        }
                    }
                    foreach (var c in curved)
                    {
                        var wt = ResolveType(c.Thickness);
                        try
                        {
                            var centre = new XYZ(MmToFt(c.Cx), MmToFt(c.Cy), z);
                            var arc = Arc.Create(centre, MmToFt(c.R), c.A0, c.A1, XYZ.BasisX, XYZ.BasisY);
                            var wall = Wall.Create(doc, arc, wt.Id, level.Id, MmToFt(heightMm), 0, false, false);
                            walls.Add(DescribeCurved(c, wt, wall));
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"curved wall r={Math.Round(c.R)} @ ({Math.Round(c.Cx)}, {Math.Round(c.Cy)}): {ex.Message}");
                        }
                    }
                    if (ownTx) t.Commit();
                }
                finally
                {
                    t?.Dispose();
                }
            }

            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun,
                ["link"] = ImportName(doc, li),
                ["layers"] = layers.ToList(),
                ["input"] = new Dictionary<string, object>
                {
                    ["lines"] = lineCount, ["polylines"] = polylineCount, ["arcs"] = arcCount,
                    ["other_skipped"] = otherCount, ["segments"] = segs.Count
                },
                ["pairs"] = pieces.Count,
                ["unpaired_segments"] = unpaired,
                ["unpaired_examples"] = GetBoolOr(p, "report_unpaired", false)
                    ? unpairedSegs.OrderByDescending(s => (s.X2 - s.X1) * (s.X2 - s.X1) + (s.Y2 - s.Y1) * (s.Y2 - s.Y1)).Take(GetIntOr(p, "report_limit", 100))
                        .Select(s => new Dictionary<string, object>
                        {
                            ["start"] = new[] { Math.Round(s.X1, 1), Math.Round(s.Y1, 1) },
                            ["end"] = new[] { Math.Round(s.X2, 1), Math.Round(s.Y2, 1) },
                            ["length_mm"] = Math.Round(Math.Sqrt((s.X2 - s.X1) * (s.X2 - s.X1) + (s.Y2 - s.Y1) * (s.Y2 - s.Y1)), 1)
                        }).ToList()
                    : null,
                ["curved_walls_planned"] = curved.Count,
                ["unpaired_arcs"] = unpairedArcs,
                ["types_created"] = createdTypes,
                ["ends_snapped"] = snapped,
                ["ends_extended_over_openings"] = extended,
                ["door_gaps_bridged_across_thickness"] = crossThickBridged,
                ["walls_planned"] = merged.Count + curved.Count,
                ["skipped_existing"] = skippedExisting,
                ["dropped_duplicates"] = dedup,
                ["walls_created"] = dryRun ? 0 : walls.Count,
                ["walls"] = walls,
                ["failures"] = failures,
                ["warnings"] = warnings
            };
        }

        // A bounded arc from the DWG in mm; angles are CCW about +Z, A1 > A0.
        private sealed class CadArc
        {
            public double Cx, Cy, R, A0, A1; public int Source;
        }

        private sealed class CurvedPiece
        {
            public double Cx, Cy, R, A0, A1, Thickness;
        }

        private static void AddArc(List<CadArc> arcs, Arc arc, double[] bbox, int source)
        {
            double cx = FtToMm(arc.Center.X), cy = FtToMm(arc.Center.Y), r = FtToMm(arc.Radius);
            var s = arc.GetEndPoint(0); var e = arc.GetEndPoint(1); var m = arc.Evaluate(0.5, true);
            if (bbox != null && !InBbox(bbox, FtToMm(m.X), FtToMm(m.Y))) return;
            double a0 = Math.Atan2(FtToMm(s.Y) - cy, FtToMm(s.X) - cx);
            double a1 = Math.Atan2(FtToMm(e.Y) - cy, FtToMm(e.X) - cx);
            double am = Math.Atan2(FtToMm(m.Y) - cy, FtToMm(m.X) - cx);
            // Order CCW: from a0 to a1 passing through am.
            double Norm(double x) { while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
            var ccwSpan = Norm(a1 - a0); var toMid = Norm(am - a0);
            if (toMid > ccwSpan) { var t = a0; a0 = a1; a1 = t; ccwSpan = Norm(a1 - a0); }
            a0 = Norm(a0); a1 = a0 + ccwSpan;
            arcs.Add(new CadArc { Cx = cx, Cy = cy, R = r, A0 = a0, A1 = a1, Source = source });
        }

        /// <summary>
        /// Name for an AI-created type that follows the source type's convention:
        /// the source's size number is replaced by the new one (e.g.
        /// "SYB_WA_Generic_200mm" -> "SYB_WA_Generic_250mm", "W900 x H2100" ->
        /// "W1150 x H2100"), then the AI tag is appended.
        /// </summary>
        private static string DeriveTypeName(string sourceName, double sourceValue, double newValue, string aiTag)
        {
            var oldNum = Math.Round(sourceValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var newNum = Math.Round(newValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string name;
            var m = System.Text.RegularExpressions.Regex.Match(sourceName ?? "", @"(?<!\d)" + oldNum + @"(?!\d)");
            if (m.Success) name = sourceName.Substring(0, m.Index) + newNum + sourceName.Substring(m.Index + m.Length);
            else name = (sourceName ?? "Type") + " " + newNum + "mm";
            // strip a tag the source may already carry (AI type derived from an AI type)
            if (!string.IsNullOrEmpty(aiTag) && name.EndsWith(aiTag)) name = name.Substring(0, name.Length - aiTag.Length);
            return name + (aiTag ?? "");
        }

        /// <summary>Record provenance on an AI-created type (Type Comments + Description if writable).</summary>
        private static void MarkAiType(ElementType type, string tool, string sourceName)
        {
            var note = $"AI (RevitMCP {tool}) {DateTime.Now:yyyy-MM-dd}, duplicated from '{sourceName}'";
            foreach (var bip in new[] { BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, BuiltInParameter.ALL_MODEL_DESCRIPTION })
            {
                try
                {
                    var prm = type.get_Parameter(bip);
                    if (prm != null && !prm.IsReadOnly && prm.StorageType == StorageType.String) prm.Set(note);
                }
                catch { /* best effort */ }
            }
        }

        // True if more than half of [lo, hi] on this segment is already used by a same-source wall.
        private static bool Claimed(Seg s, double lo, double hi)
        {
            double covered = 0;
            foreach (var c in s.Claims)
                covered += Math.Max(0, Math.Min(hi, c[1]) - Math.Max(lo, c[0]));
            return covered > 0.5 * (hi - lo);
        }

        private static void AddSeg(List<Seg> segs, XYZ a, XYZ b, double[] bbox, int source)
        {
            double x1 = FtToMm(a.X), y1 = FtToMm(a.Y), x2 = FtToMm(b.X), y2 = FtToMm(b.Y);
            if (Math.Abs(x2 - x1) < 1 && Math.Abs(y2 - y1) < 1) return;
            if (bbox != null && !InBbox(bbox, (x1 + x2) / 2, (y1 + y2) / 2)) return;
            var seg = MakeSeg(x1, y1, x2, y2);
            seg.Source = source;
            segs.Add(seg);
        }

        /// <summary>Swallow warnings (e.g. "walls overlap") so batch creation doesn't pop dialogs.</summary>
        private sealed class SilentFailures : IFailuresPreprocessor
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
