using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin
{
    /// <summary>
    /// Warnings:
    ///  - list_warnings : the document's persistent warnings (what Revit's "Review Warnings"
    ///    shows - walls overlap, joined but not intersecting, duplicate marks, off-axis ...)
    ///    grouped by description with the involved element ids, plus the transient warnings
    ///    the add-in swallowed during MCP requests.
    ///  - fix_warnings  : deterministic fixes for the classes that are safe to automate
    ///    (dry_run first). Everything else is reported for a human decision.
    /// </summary>
    public static partial class CommandRouter
    {
        private enum FixKind { None, UnjoinDisjoint, DeleteDuplicateWall, ClearDuplicateMark, DeleteZeroLengthWall }

        private sealed class WarningGroup
        {
            public string Description;
            public FailureSeverity Severity;
            public int Count;
            public List<List<long>> ElementSets = new List<List<long>>();
            public HashSet<long> Elements = new HashSet<long>();
            public FixKind Fix;
        }

        private static FixKind ClassifyWarning(string desc)
        {
            var d = desc ?? "";
            if (d.IndexOf("joined but do not intersect", StringComparison.OrdinalIgnoreCase) >= 0) return FixKind.UnjoinDisjoint;
            if (d.IndexOf("walls overlap", StringComparison.OrdinalIgnoreCase) >= 0) return FixKind.DeleteDuplicateWall;
            if (d.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0 && d.IndexOf("Mark", StringComparison.OrdinalIgnoreCase) >= 0) return FixKind.ClearDuplicateMark;
            return FixKind.None;
        }

        private static string FixLabel(FixKind k)
        {
            switch (k)
            {
                case FixKind.UnjoinDisjoint: return "unjoin the elements (JoinGeometryUtils.UnjoinGeometry)";
                case FixKind.DeleteDuplicateWall: return "touching walls (overlap <= touch_tolerance_mm): nudge the thinner one apart; a wall embedded in a thicker/longer collinear one: delete it; partial overlaps are left for you";
                case FixKind.ClearDuplicateMark: return "clear the Mark of all but the first element";
                case FixKind.DeleteZeroLengthWall: return "delete walls shorter than 10 mm";
                default: return "no automatic fix - review manually";
            }
        }

        private static List<WarningGroup> CollectWarnings(Document doc)
        {
            var groups = new Dictionary<string, WarningGroup>();
            foreach (var fm in doc.GetWarnings())
            {
                string desc;
                try { desc = fm.GetDescriptionText(); } catch { desc = "(unreadable warning)"; }
                if (!groups.TryGetValue(desc, out var g))
                    groups[desc] = g = new WarningGroup { Description = desc, Severity = fm.GetSeverity(), Fix = ClassifyWarning(desc) };
                g.Count++;
                var ids = new List<long>();
                try { foreach (var id in fm.GetFailingElements()) ids.Add(id.Value); } catch { }
                try { foreach (var id in fm.GetAdditionalElements()) ids.Add(id.Value); } catch { }
                g.ElementSets.Add(ids);
                foreach (var id in ids) g.Elements.Add(id);
            }
            return groups.Values.OrderByDescending(g => g.Count).ToList();
        }

        private static Dictionary<string, object> ListWarnings(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            int maxIds = GetIntOr(p, "max_ids", 20);
            bool includeSwallowed = GetBoolOr(p, "include_swallowed", true);
            var groups = CollectWarnings(doc);
            string Cat(long id) { var e = doc.GetElement(new ElementId(id)); return e?.Category?.Name ?? e?.GetType().Name ?? "?"; }
            var outGroups = groups.Select(g => new Dictionary<string, object>
            {
                ["description"] = g.Description,
                ["severity"] = g.Severity.ToString(),
                ["count"] = g.Count,
                ["auto_fix"] = g.Fix != FixKind.None,
                ["fix"] = FixLabel(g.Fix),
                ["categories"] = g.Elements.Select(Cat).GroupBy(c => c).ToDictionary(x => x.Key, x => (object)x.Count()),
                ["element_ids"] = g.Elements.Take(maxIds).ToList(),
                ["elements_total"] = g.Elements.Count,
                ["example_sets"] = g.ElementSets.Take(5).ToList(),
            }).ToList();
            var result = new Dictionary<string, object>
            {
                ["document"] = doc.Title,
                ["warnings_total"] = groups.Sum(g => g.Count),
                ["groups"] = outGroups,
                ["auto_fixable"] = groups.Where(g => g.Fix != FixKind.None).Sum(g => g.Count),
            };
            if (includeSwallowed)
            {
                List<Dictionary<string, object>> sw;
                lock (RequestHandler.SwallowedWarnings)
                    sw = RequestHandler.SwallowedWarnings.Values.OrderByDescending(w => w.Count).Select(w => new Dictionary<string, object>
                    {
                        ["description"] = w.Description, ["count"] = w.Count, ["last"] = w.Last.ToString("HH:mm:ss"),
                        ["element_ids"] = w.ElementIds.Take(maxIds).ToList(),
                    }).ToList();
                result["swallowed_during_requests"] = sw;
                result["swallowed_note"] = "Transient warnings the add-in dismissed while running MCP commands (since Revit started). Persistent ones also appear in 'groups'.";
            }
            return result;
        }

        private static Dictionary<string, object> FixWarnings(UIDocument uidoc, Dictionary<string, object> p)
        {
            var doc = RequireDoc(uidoc);
            bool dryRun = GetBoolOr(p, "dry_run", true);
            if (!dryRun) EnsureWritable();
            var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (p.ContainsKey("kinds") && p["kinds"] is IEnumerable ks && !(p["kinds"] is string))
                foreach (var o in ks) if (o != null) kinds.Add(Convert.ToString(o));
            bool Want(FixKind k) => kinds.Count == 0 || kinds.Contains(k.ToString());
            double dupTol = GetDoubleOr(p, "duplicate_tolerance_mm", 50);
            int maxFixes = GetIntOr(p, "max_fixes", 500);

            var groups = CollectWarnings(doc);
            var plan = new List<Dictionary<string, object>>();   // per action
            var skipped = new List<Dictionary<string, object>>();
            var toDelete = new HashSet<long>();
            var toUnjoin = new List<(long a, long b)>();
            var toClearMark = new List<long>();
            var toMove = new Dictionary<long, XYZ>();
            double touchTol = GetDoubleOr(p, "touch_tolerance_mm", 5);

            foreach (var g in groups)
            {
                if (g.Fix == FixKind.None || !Want(g.Fix))
                {
                    skipped.Add(new Dictionary<string, object> { ["description"] = g.Description, ["count"] = g.Count, ["reason"] = g.Fix == FixKind.None ? "no automatic fix" : "kind not requested" });
                    continue;
                }
                foreach (var set in g.ElementSets)
                {
                    if (plan.Count >= maxFixes) break;
                    switch (g.Fix)
                    {
                        case FixKind.UnjoinDisjoint:
                        {
                            var els = set.Select(id => doc.GetElement(new ElementId(id))).Where(e => e != null).ToList();
                            for (int i = 0; i < els.Count; i++)
                                for (int j = i + 1; j < els.Count; j++)
                                {
                                    bool joined = false;
                                    try { joined = JoinGeometryUtils.AreElementsJoined(doc, els[i], els[j]); } catch { }
                                    if (!joined) continue;
                                    toUnjoin.Add((els[i].Id.Value, els[j].Id.Value));
                                    plan.Add(new Dictionary<string, object> { ["action"] = "unjoin", ["elements"] = new[] { els[i].Id.Value, els[j].Id.Value }, ["warning"] = g.Description });
                                }
                            break;
                        }
                        case FixKind.DeleteDuplicateWall:
                        {
                            var walls = set.Select(id => doc.GetElement(new ElementId(id)) as Wall).Where(w => w != null).ToList();
                            if (walls.Count != 2) { skipped.Add(new Dictionary<string, object> { ["description"] = g.Description, ["elements"] = set, ["reason"] = "not a pair of walls" }); break; }
                            var a = walls[0]; var b = walls[1];
                            if (toDelete.Contains(a.Id.Value) || toDelete.Contains(b.Id.Value) || toMove.ContainsKey(a.Id.Value) || toMove.ContainsKey(b.Id.Value)) break;
                            var la = (a.Location as LocationCurve)?.Curve as Line; var lb = (b.Location as LocationCurve)?.Curve as Line;
                            if (la == null || lb == null) { skipped.Add(new Dictionary<string, object> { ["description"] = g.Description, ["elements"] = set, ["reason"] = "curved wall" }); break; }
                            double dot = Math.Abs(la.Direction.DotProduct(lb.Direction));
                            if (dot < 0.9998) { skipped.Add(new Dictionary<string, object> { ["description"] = g.Description, ["elements"] = set, ["reason"] = "not parallel (crossing / T-junction overlap)" }); break; }
                            // frame on a: along = direction, normal = left normal; footprints across
                            var along = la.Direction; var normal = new XYZ(-along.Y, along.X, 0);
                            var pa = la.GetEndPoint(0);
                            double offB = normal.DotProduct(lb.Evaluate(0.5, true) - pa);           // b's centre line offset from a's (ft)
                            double wa = a.Width, wb = b.Width;
                            double aLo = -wa / 2, aHi = wa / 2, bLo = offB - wb / 2, bHi = offB + wb / 2;
                            double depth = Math.Min(aHi, bHi) - Math.Max(aLo, bLo);               // lateral overlap (ft)
                            double ta1 = 0, ta2 = la.Length;
                            double tb1 = along.DotProduct(lb.GetEndPoint(0) - pa), tb2 = along.DotProduct(lb.GetEndPoint(1) - pa);
                            double blo = Math.Min(tb1, tb2), bhi = Math.Max(tb1, tb2);
                            double tol = MmToFt(dupTol), touch = MmToFt(touchTol), lateralTol = MmToFt(20);
                            double depthMm = FtToMm(depth);
                            if (depth <= touch)
                            {
                                // touching / grazing: push the thinner wall outward by the overlap (+1 mm)
                                var mover = wb <= wa ? b : a;
                                double sign = mover == b ? Math.Sign(offB) : -Math.Sign(offB);
                                if (sign == 0) sign = 1;
                                var vec = normal * (depth + MmToFt(1)) * sign;
                                toMove[mover.Id.Value] = vec;
                                plan.Add(new Dictionary<string, object> { ["action"] = "move touching wall", ["element"] = mover.Id.Value, ["other"] = (mover == a ? b : a).Id.Value, ["by_mm"] = Math.Round(depthMm + 1, 1), ["warning"] = g.Description });
                                break;
                            }
                            bool bInA = bLo >= aLo - lateralTol && bHi <= aHi + lateralTol && blo >= ta1 - tol && bhi <= ta2 + tol;
                            bool aInB = aLo >= bLo - lateralTol && aHi <= bHi + lateralTol && ta1 >= blo - tol && ta2 <= bhi + tol;
                            Wall victim = bInA && aInB ? (lb.Length <= la.Length ? b : a) : bInA ? b : aInB ? a : null;
                            if (victim == null)
                            {
                                skipped.Add(new Dictionary<string, object> { ["description"] = g.Description, ["elements"] = set, ["reason"] = $"partial overlap ({Math.Round(depthMm)} mm deep) - trim / split by hand" });
                                break;
                            }
                            var survivor = victim == a ? b : a;
                            int insV = victim.FindInserts(true, false, false, false).Count, insS = survivor.FindInserts(true, false, false, false).Count;
                            if (insV > 0 && insS == 0 && bInA && aInB) { var t = victim; victim = survivor; survivor = t; }
                            else if (insV > 0) { skipped.Add(new Dictionary<string, object> { ["description"] = g.Description, ["elements"] = set, ["reason"] = "the embedded wall hosts doors/windows" }); break; }
                            toDelete.Add(victim.Id.Value);
                            plan.Add(new Dictionary<string, object> { ["action"] = "delete embedded wall", ["delete"] = victim.Id.Value, ["keep"] = survivor.Id.Value, ["length_mm"] = Math.Round(FtToMm(((victim.Location as LocationCurve).Curve).Length)), ["thickness_mm"] = Math.Round(FtToMm(victim.Width)), ["inside_thickness_mm"] = Math.Round(FtToMm(survivor.Width)), ["warning"] = g.Description });
                            break;
                        }
                        case FixKind.ClearDuplicateMark:
                        {
                            var els = set.Select(id => doc.GetElement(new ElementId(id))).Where(e => e != null).ToList();
                            foreach (var e in els.Skip(1))
                            {
                                var prm = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                                if (prm == null || prm.IsReadOnly) continue;
                                toClearMark.Add(e.Id.Value);
                                plan.Add(new Dictionary<string, object> { ["action"] = "clear Mark", ["element"] = e.Id.Value, ["mark"] = prm.AsString(), ["warning"] = g.Description });
                            }
                            break;
                        }
                    }
                }
            }

            int done = 0; var failures = new List<string>();
            if (!dryRun && plan.Count > 0)
            {
                var ownTx = !doc.IsModifiable;
                var t = ownTx ? new Transaction(doc, "MCP: fix warnings") : null;
                try
                {
                    if (ownTx) { t.Start(); var fh = t.GetFailureHandlingOptions(); fh.SetFailuresPreprocessor(new SilentFailures()); t.SetFailureHandlingOptions(fh); }
                    foreach (var (a, b) in toUnjoin)
                    {
                        try { var ea = doc.GetElement(new ElementId(a)); var eb = doc.GetElement(new ElementId(b)); if (ea != null && eb != null && JoinGeometryUtils.AreElementsJoined(doc, ea, eb)) { JoinGeometryUtils.UnjoinGeometry(doc, ea, eb); done++; } }
                        catch (Exception ex) { failures.Add($"unjoin {a}/{b}: {ex.Message}"); }
                    }
                    foreach (var kv in toMove)
                    {
                        try { if (doc.GetElement(new ElementId(kv.Key)) != null) { ElementTransformUtils.MoveElement(doc, new ElementId(kv.Key), kv.Value); done++; } }
                        catch (Exception ex) { failures.Add($"move {kv.Key}: {ex.Message}"); }
                    }
                    foreach (var id in toClearMark)
                    {
                        try { doc.GetElement(new ElementId(id))?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(""); done++; }
                        catch (Exception ex) { failures.Add($"mark {id}: {ex.Message}"); }
                    }
                    if (toDelete.Count > 0)
                    {
                        try { done += doc.Delete(toDelete.Select(id => new ElementId(id)).Where(id => doc.GetElement(id) != null).ToList()).Count(id => toDelete.Contains(id.Value)); }
                        catch (Exception ex) { failures.Add($"delete: {ex.Message}"); }
                    }
                    if (ownTx) CommitOrThrow(t, "fix warnings");
                }
                finally { if (ownTx && t.HasStarted() && !t.HasEnded()) t.RollBack(); t?.Dispose(); }
            }
            int remaining = -1;
            if (!dryRun) { try { doc.Regenerate(); } catch { } remaining = doc.GetWarnings().Count; }
            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun,
                ["warnings_before"] = groups.Sum(g => g.Count),
                ["planned"] = plan.Count,
                ["applied"] = dryRun ? 0 : done,
                ["warnings_after"] = dryRun ? null : (object)remaining,
                ["actions"] = plan,
                ["skipped"] = skipped,
                ["failures"] = failures,
                ["kinds"] = new[] { "UnjoinDisjoint", "DeleteDuplicateWall", "ClearDuplicateMark" },
            };
        }
    }
}
