#!/usr/bin/env python3
"""Compare two dump_model JSON dumps (ground truth vs. a CAD-rebuilt model) and
report per-category precision / recall / F1, plus lists of misses (truth
elements with no match in test) and extras (test elements with no match in
truth), and diagnostics on matched-but-wrong attributes.

Both inputs must follow the dump_model schema (see mcp_server/server.py /
RevitMCP.Addin/CommandRouter.Project.cs): top-level "walls", "doors",
"windows", "columns", "stairs", "floors", "grids" lists, all lengths in mm.

Usage:
    python compare_models.py TRUTH.json TEST.json [--level NAME]
        [--bbox xmin ymin xmax ymax] [--tol-pos 150] [--tol-width 100]
        [--tol-thick 30] [--ignore-level] [--all] [--json out.json] [--csv out.csv]

Matching (see category functions below for the exact rule per category):
    doors/windows/columns/floors: nearest within --tol-pos (mm), greedy
        one-to-one, restricted to the same level name (case-insensitive) only
        when both sides have a level; --ignore-level drops that restriction.
    walls: strict 1:1 match on endpoints (+ center/radius for arcs) within
        --tol-pos, AND a separate length-coverage recall (a truth wall counts
        as "covered" once nearby, near-parallel test walls overlap >= 80% of
        its length).
    stairs: bbox XY IoU >= 0.3 or bbox-center distance <= 1500 mm.
    grids: matched by name (case-insensitive).

Standalone, stdlib only.
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import sys

CATEGORIES = ["walls", "doors", "windows", "columns", "stairs", "floors", "grids"]
LEVEL_FIELD = {
    "walls": "level", "doors": "level", "windows": "level", "floors": "level",
    "columns": "base_level", "stairs": "base_level",
}


# --------------------------------------------------------------------------- #
# generic helpers
# --------------------------------------------------------------------------- #

def load(path: str) -> dict:
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def dist(a, b) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def bbox_xy(elem: dict):
    bb = elem.get("bbox")
    if not bb or len(bb) < 4:
        return None
    return bb[0], bb[1], bb[2], bb[3]


def bbox_center(elem: dict):
    bb = bbox_xy(elem)
    if bb is None:
        return None
    return (bb[0] + bb[2]) / 2.0, (bb[1] + bb[3]) / 2.0


def bbox_overlap(a, b) -> bool:
    return not (a[2] < b[0] or a[0] > b[2] or a[3] < b[1] or a[1] > b[3])


def loc2(elem: dict):
    loc = elem.get("location")
    if not loc or len(loc) < 2:
        return None
    return loc[0], loc[1]


def level_ok(t_lvl, s_lvl, ignore_level: bool) -> bool:
    """Level name matches case-insensitively when both sides have one;
    otherwise (either side missing, or --ignore-level) the restriction is
    dropped and the pair is a candidate regardless of level."""
    if ignore_level:
        return True
    if t_lvl and s_lvl:
        return str(t_lvl).strip().lower() == str(s_lvl).strip().lower()
    return True


def prf(truth_n: int, test_n: int, matched: int):
    p = matched / test_n if test_n else 1.0
    r = matched / truth_n if truth_n else 1.0
    f1 = 2 * p * r / (p + r) if (p + r) else 0.0
    return p, r, f1


def greedy_assign(candidates):
    """candidates: list of (score, truth_idx, test_idx), lower score better.
    Returns (matches, matched_truth_idx, matched_test_idx)."""
    candidates = sorted(candidates, key=lambda c: c[0])
    ut, us = set(), set()
    matches = []
    for score, ti, si in candidates:
        if ti in ut or si in us:
            continue
        ut.add(ti)
        us.add(si)
        matches.append((ti, si, score))
    return matches, ut, us


def filter_level(elems: list, field: str, level_name: str) -> list:
    if not level_name:
        return elems
    ln = level_name.strip().lower()
    return [e for e in elems if e.get(field) and str(e[field]).strip().lower() == ln]


def filter_bbox(elems: list, bbox, get_bbox):
    if not bbox:
        return elems
    out = []
    for e in elems:
        eb = get_bbox(e)
        if eb is None or bbox_overlap(eb, bbox):
            out.append(e)
    return out


def grid_bbox(elem: dict):
    c = elem.get("curve")
    if not c:
        return None
    pts = [c.get("start"), c.get("end"), c.get("center"), c.get("mid")]
    pts = [p for p in pts if p]
    if not pts:
        return None
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


# --------------------------------------------------------------------------- #
# point-based categories: doors, windows, columns, floors
# --------------------------------------------------------------------------- #

def match_points(truth, test, tol_pos, level_field, ignore_level, loc_fn):
    candidates = []
    for ti, t in enumerate(truth):
        tp = loc_fn(t)
        if tp is None:
            continue
        for si, s in enumerate(test):
            if not level_ok(t.get(level_field), s.get(level_field), ignore_level):
                continue
            sp = loc_fn(s)
            if sp is None:
                continue
            d = dist(tp, sp)
            if d <= tol_pos:
                candidates.append((d, ti, si))
    matches, ut, us = greedy_assign(candidates)
    misses = [truth[i] for i in range(len(truth)) if i not in ut]
    extras = [test[i] for i in range(len(test)) if i not in us]
    return matches, misses, extras


def width_agree(t_w, s_w, tol_width):
    """Returns True/False, or None if unknown on either side (0/null = unknown,
    not penalised)."""
    if not t_w or not s_w:
        return None
    return abs(t_w - s_w) <= tol_width


def report_doors_windows(cat, truth, test, tol_pos, tol_width, ignore_level, cap):
    field = LEVEL_FIELD[cat]
    matches, misses, extras = match_points(truth, test, tol_pos, field, ignore_level, loc2)
    p, r, f1 = prf(len(truth), len(test), len(matches))

    width_known = width_agreed = 0
    unhosted = 0
    for ti, si, _ in matches:
        t, s = truth[ti], test[si]
        wa = width_agree(t.get("width_mm"), s.get("width_mm"), tol_width)
        if wa is not None:
            width_known += 1
            if wa:
                width_agreed += 1
        if s.get("host_id") is None:
            unhosted += 1

    return {
        "truth_n": len(truth), "test_n": len(test), "matched": len(matches),
        "precision": p, "recall": r, "f1": f1,
        "width_known": width_known, "width_agreed": width_agreed,
        "unhosted_matched": unhosted,
        "misses": [fmt_door(e) for e in misses[: None if cap else 40]],
        "extras": [fmt_door(e) for e in extras[: None if cap else 40]],
        "misses_n": len(misses), "extras_n": len(extras),
    }


def size_agree(t, s, tol_width):
    """Column size agreement: width/depth within tolerance (swap allowed), or
    radius within tolerance for round columns. None if unknown either side."""
    tw, td, tr = t.get("width_mm"), t.get("depth_mm"), t.get("radius_mm")
    sw, sd, sr = s.get("width_mm"), s.get("depth_mm"), s.get("radius_mm")
    if tr and sr:
        return abs(tr - sr) <= tol_width
    if tw and td and sw and sd:
        direct = abs(tw - sw) <= tol_width and abs(td - sd) <= tol_width
        swapped = abs(tw - sd) <= tol_width and abs(td - sw) <= tol_width
        return direct or swapped
    return None


def report_columns(truth, test, tol_pos, tol_width, ignore_level, cap):
    field = LEVEL_FIELD["columns"]
    matches, misses, extras = match_points(truth, test, tol_pos, field, ignore_level, loc2)
    p, r, f1 = prf(len(truth), len(test), len(matches))

    size_known = size_agreed = 0
    for ti, si, _ in matches:
        sa = size_agree(truth[ti], test[si], tol_width)
        if sa is not None:
            size_known += 1
            if sa:
                size_agreed += 1

    return {
        "truth_n": len(truth), "test_n": len(test), "matched": len(matches),
        "precision": p, "recall": r, "f1": f1,
        "size_known": size_known, "size_agreed": size_agreed,
        "misses": [fmt_column(e) for e in misses[: None if cap else 40]],
        "extras": [fmt_column(e) for e in extras[: None if cap else 40]],
        "misses_n": len(misses), "extras_n": len(extras),
    }


def report_floors(truth, test, tol_pos, ignore_level, cap):
    """Floors are compared by footprint, not one-to-one: a rebuild typically has one slab per
    level where the truth has many finish floors / landings. A truth floor counts as covered when
    >= 70 % of its bbox lies inside the union of the test floors' bboxes (approximated by summing
    pairwise bbox intersections, capped at the truth area); a test floor is 'supported' when
    >= 50 % of its bbox is covered by truth floors. Also reports total area per side.
    Note: bbox-based, so L-shaped slabs over-count."""
    def area(b):
        return max(0.0, b[2] - b[0]) * max(0.0, b[3] - b[1])

    def inter(a, b):
        ix0, iy0 = max(a[0], b[0]), max(a[1], b[1])
        ix1, iy1 = min(a[2], b[2]), min(a[3], b[3])
        return max(0.0, ix1 - ix0) * max(0.0, iy1 - iy0)

    tb = [(t, bbox_xy(t)) for t in truth]
    sb = [(s, bbox_xy(s)) for s in test]
    covered, misses = [], []
    cov_area_t = 0.0
    for t, b in tb:
        if b is None or area(b) <= 0:
            continue
        c = min(area(b), sum(inter(b, b2) for _, b2 in sb if b2 is not None))
        cov_area_t += c
        (covered if c / area(b) >= 0.7 else misses).append(t)
    supported, extras = [], []
    for s, b in sb:
        if b is None or area(b) <= 0:
            continue
        c = min(area(b), sum(inter(b, b2) for _, b2 in tb if b2 is not None))
        (supported if c / area(b) >= 0.5 else extras).append(s)
    truth_area = sum(t.get("area_m2") or 0 for t in truth)
    test_area = sum(s.get("area_m2") or 0 for s in test)
    n_t = len([1 for _, b in tb if b is not None])
    n_s = len([1 for _, b in sb if b is not None])
    r = len(covered) / n_t if n_t else 1.0
    p = len(supported) / n_s if n_s else 1.0
    f1 = 2 * p * r / (p + r) if (p + r) else 0.0
    return {
        "truth_n": len(truth), "test_n": len(test), "matched": len(covered),
        "precision": p, "recall": r, "f1": f1,
        "supported": len(supported),
        "truth_area_m2": truth_area, "test_area_m2": test_area,
        "truth_bbox_area_covered": (cov_area_t / sum(area(b) for _, b in tb if b is not None)) if tb else 0.0,
        "misses": [fmt_floor(e) for e in misses[: None if cap else 40]],
        "extras": [fmt_floor(e) for e in extras[: None if cap else 40]],
        "misses_n": len(misses), "extras_n": len(extras),
    }


# --------------------------------------------------------------------------- #
# walls
# --------------------------------------------------------------------------- #

def wall_endpoints(w):
    c = w.get("curve")
    if not c or "start" not in c or "end" not in c:
        return None
    return tuple(c["start"]), tuple(c["end"])


def wall_strict_score(t, s, tol_pos):
    ct, cs = t.get("curve"), s.get("curve")
    if not ct or not cs or ct.get("kind") != cs.get("kind"):
        return None
    te, se = wall_endpoints(t), wall_endpoints(s)
    if te is None or se is None:
        return None
    d1 = max(dist(te[0], se[0]), dist(te[1], se[1]))
    d2 = max(dist(te[0], se[1]), dist(te[1], se[0]))
    d = min(d1, d2)
    if ct["kind"] == "arc":
        d = max(d, abs(ct.get("radius_mm", 0) - cs.get("radius_mm", 0)),
                dist(tuple(ct["center"]), tuple(cs["center"])))
    return d if d <= tol_pos else None


def match_walls_strict(truth, test, tol_pos, ignore_level):
    candidates = []
    for ti, t in enumerate(truth):
        for si, s in enumerate(test):
            if not level_ok(t.get("level"), s.get("level"), ignore_level):
                continue
            sc = wall_strict_score(t, s, tol_pos)
            if sc is not None:
                candidates.append((sc, ti, si))
    matches, ut, us = greedy_assign(candidates)
    misses = [truth[i] for i in range(len(truth)) if i not in ut]
    extras = [test[i] for i in range(len(test)) if i not in us]
    return matches, misses, extras


def angle_between_lines_deg(a1, a2):
    d = abs(a1 - a2) % math.pi
    if d > math.pi / 2:
        d = math.pi - d
    return math.degrees(d)


def along_perp(s, e, p):
    dx, dy = e[0] - s[0], e[1] - s[1]
    length = math.hypot(dx, dy)
    if length == 0:
        return 0.0, 0.0
    ux, uy = dx / length, dy / length
    vx, vy = p[0] - s[0], p[1] - s[1]
    return vx * ux + vy * uy, -vx * uy + vy * ux


def wall_coverage(truth_wall, candidate_test_walls, tol_pos, angle_max_deg=5.0):
    """Fraction (0..1) of a straight truth wall's length covered by nearby,
    near-parallel straight test walls (lateral distance <= tol_pos)."""
    c = truth_wall.get("curve")
    if not c or c.get("kind") != "line":
        return None
    s, e = tuple(c["start"]), tuple(c["end"])
    length = math.hypot(e[0] - s[0], e[1] - s[1])
    if length <= 0:
        return None
    t_ang = math.atan2(e[1] - s[1], e[0] - s[0])

    intervals = []
    for w in candidate_test_walls:
        cw = w.get("curve")
        if not cw or cw.get("kind") != "line":
            continue
        ws, we = tuple(cw["start"]), tuple(cw["end"])
        w_ang = math.atan2(we[1] - ws[1], we[0] - ws[0])
        if angle_between_lines_deg(t_ang, w_ang) > angle_max_deg:
            continue
        a0, p0 = along_perp(s, e, ws)
        a1, p1 = along_perp(s, e, we)
        if abs(p0) > tol_pos and abs(p1) > tol_pos:
            continue
        lo, hi = max(0.0, min(a0, a1)), min(length, max(a0, a1))
        if hi > lo:
            intervals.append((lo, hi))
    if not intervals:
        return 0.0
    intervals.sort()
    merged = [intervals[0]]
    for lo, hi in intervals[1:]:
        plo, phi = merged[-1]
        if lo <= phi:
            merged[-1] = (plo, max(phi, hi))
        else:
            merged.append((lo, hi))
    covered = sum(hi - lo for lo, hi in merged)
    return covered / length


def report_walls(truth_all, test_all, tol_pos, tol_thick, ignore_level, cap):
    # Skip stacked-wall parents (kind == "Stacked"); keep their members.
    truth = [w for w in truth_all if w.get("kind") != "Stacked"]
    test = [w for w in test_all if w.get("kind") != "Stacked"]

    matches, misses, extras = match_walls_strict(truth, test, tol_pos, ignore_level)
    p, r, f1 = prf(len(truth), len(test), len(matches))

    thick_known = thick_agreed = 0
    curtain_mismatch = 0
    for ti, si, _ in matches:
        t, s = truth[ti], test[si]
        tt, st = t.get("thickness_mm"), s.get("thickness_mm")
        if tt is not None and st is not None:
            thick_known += 1
            if abs(tt - st) <= tol_thick:
                thick_agreed += 1
        if bool(t.get("curtain")) != bool(s.get("curtain")):
            curtain_mismatch += 1

    # Length-coverage recall: for each truth wall, do nearby/parallel test
    # walls (any level restriction honoured) cover >= 80% of its length?
    covered = 0
    coverages = []
    for t in truth:
        cand = [s for s in test if level_ok(t.get("level"), s.get("level"), ignore_level)]
        cov = wall_coverage(t, cand, tol_pos)
        if cov is not None:
            coverages.append(cov)
            if cov >= 0.8:
                covered += 1
    coverage_n = len(coverages)

    return {
        "truth_n": len(truth), "test_n": len(test), "matched": len(matches),
        "precision": p, "recall": r, "f1": f1,
        "thickness_known": thick_known, "thickness_agreed": thick_agreed,
        "curtain_mismatch": curtain_mismatch,
        "coverage_covered": covered, "coverage_n": coverage_n,
        "misses": [fmt_wall(e) for e in misses[: None if cap else 40]],
        "extras": [fmt_wall(e) for e in extras[: None if cap else 40]],
        "misses_n": len(misses), "extras_n": len(extras),
    }


# --------------------------------------------------------------------------- #
# stairs
# --------------------------------------------------------------------------- #

def iou(a, b):
    ix0, iy0 = max(a[0], b[0]), max(a[1], b[1])
    ix1, iy1 = min(a[2], b[2]), min(a[3], b[3])
    if ix1 <= ix0 or iy1 <= iy0:
        return 0.0
    inter = (ix1 - ix0) * (iy1 - iy0)
    area_a = (a[2] - a[0]) * (a[3] - a[1])
    area_b = (b[2] - b[0]) * (b[3] - b[1])
    union = area_a + area_b - inter
    return inter / union if union > 0 else 0.0


def report_stairs(truth, test, cap):
    candidates = []
    for ti, t in enumerate(truth):
        tb = bbox_xy(t)
        if tb is None:
            continue
        for si, s in enumerate(test):
            sb = bbox_xy(s)
            if sb is None:
                continue
            i = iou(tb, sb)
            tc, sc = bbox_center(t), bbox_center(s)
            d = dist(tc, sc) if tc and sc else float("inf")
            if i >= 0.3 or d <= 1500:
                score = -i * 100000 + d  # higher IoU wins, distance breaks ties
                candidates.append((score, ti, si))
    matches, ut, us = greedy_assign(candidates)
    misses = [truth[i] for i in range(len(truth)) if i not in ut]
    extras = [test[i] for i in range(len(test)) if i not in us]
    p, r, f1 = prf(len(truth), len(test), len(matches))

    riser_agree = run_agree = 0
    for ti, si, _ in matches:
        t, s = truth[ti], test[si]
        if t.get("risers") == s.get("risers"):
            riser_agree += 1
        if len(t.get("runs") or []) == len(s.get("runs") or []):
            run_agree += 1

    return {
        "truth_n": len(truth), "test_n": len(test), "matched": len(matches),
        "precision": p, "recall": r, "f1": f1,
        "riser_agree": riser_agree, "run_agree": run_agree,
        "misses": [fmt_stair(e) for e in misses[: None if cap else 40]],
        "extras": [fmt_stair(e) for e in extras[: None if cap else 40]],
        "misses_n": len(misses), "extras_n": len(extras),
    }


# --------------------------------------------------------------------------- #
# grids
# --------------------------------------------------------------------------- #

def report_grids(truth, test, tol_pos, cap):
    used_s = set()
    matches, misses, extras = [], [], []
    for ti, t in enumerate(truth):
        tn = (t.get("name") or "").strip().lower()
        found = None
        for si, s in enumerate(test):
            if si in used_s:
                continue
            if tn and (s.get("name") or "").strip().lower() == tn:
                found = si
                break
        if found is not None:
            used_s.add(found)
            matches.append((ti, found))
        else:
            misses.append(t)
    for si, s in enumerate(test):
        if si not in used_s:
            extras.append(s)
    p, r, f1 = prf(len(truth), len(test), len(matches))

    endpoint_known = endpoint_agreed = 0
    for ti, si in matches:
        tc, sc = truth[ti].get("curve"), test[si].get("curve")
        if tc and sc and "start" in tc and "end" in tc and "start" in sc and "end" in sc:
            endpoint_known += 1
            d1 = max(dist(tc["start"], sc["start"]), dist(tc["end"], sc["end"]))
            d2 = max(dist(tc["start"], sc["end"]), dist(tc["end"], sc["start"]))
            if min(d1, d2) <= tol_pos:
                endpoint_agreed += 1

    return {
        "truth_n": len(truth), "test_n": len(test), "matched": len(matches),
        "precision": p, "recall": r, "f1": f1,
        "endpoint_known": endpoint_known, "endpoint_agreed": endpoint_agreed,
        "misses": [fmt_grid(e) for e in misses[: None if cap else 40]],
        "extras": [fmt_grid(e) for e in extras[: None if cap else 40]],
        "misses_n": len(misses), "extras_n": len(extras),
    }


# --------------------------------------------------------------------------- #
# formatting of misses/extras rows
# --------------------------------------------------------------------------- #

def fmt_wall(w):
    c = w.get("curve") or {}
    if c.get("kind") == "line":
        curve = f"line ({c['start'][0]:.0f},{c['start'][1]:.0f})-({c['end'][0]:.0f},{c['end'][1]:.0f})"
    elif c.get("kind") == "arc":
        curve = f"arc center=({c['center'][0]:.0f},{c['center'][1]:.0f}) r={c.get('radius_mm')}"
    else:
        curve = "?"
    return (f"id={w.get('id')} level={w.get('level')} type={w.get('type')} "
            f"kind={w.get('kind')} thickness={w.get('thickness_mm')}mm "
            f"length={w.get('length_mm')}mm {curve}")


def fmt_door(d):
    loc = d.get("location") or [None, None, None]
    return (f"id={d.get('id')} level={d.get('level')} family={d.get('family')} "
            f"type={d.get('type')} width={d.get('width_mm')}mm "
            f"host={d.get('host_id')} loc=({loc[0]},{loc[1]})")


fmt_window = fmt_door


def fmt_column(c):
    loc = c.get("location") or [None, None, None]
    return (f"id={c.get('id')} level={c.get('base_level')} family={c.get('family')} "
            f"type={c.get('type')} w={c.get('width_mm')} d={c.get('depth_mm')} "
            f"r={c.get('radius_mm')} loc=({loc[0]},{loc[1]})")


def fmt_stair(s):
    return (f"id={s.get('id')} level={s.get('base_level')} type={s.get('type')} "
            f"risers={s.get('risers')} runs={len(s.get('runs') or [])} bbox={s.get('bbox')}")


def fmt_floor(f):
    return f"id={f.get('id')} level={f.get('level')} type={f.get('type')} area={f.get('area_m2')}m2"


def fmt_grid(g):
    c = g.get("curve") or {}
    ends = f"({c['start'][0]:.0f},{c['start'][1]:.0f})-({c['end'][0]:.0f},{c['end'][1]:.0f})" if "start" in c and "end" in c else "?"
    return f"id={g.get('id')} name={g.get('name')} {ends}"


# --------------------------------------------------------------------------- #
# printing
# --------------------------------------------------------------------------- #

def pct(x):
    return f"{x * 100:.1f}%"


def print_report(cat, rep, cap_all):
    print(f"\n=== {cat.upper()} ===")
    print(f"truth={rep['truth_n']}  test={rep['test_n']}  matched={rep['matched']}  "
          f"precision={pct(rep['precision'])}  recall={pct(rep['recall'])}  f1={pct(rep['f1'])}")

    if cat == "walls":
        if rep["coverage_n"]:
            cov_pct = rep["coverage_covered"] / rep["coverage_n"] * 100
        else:
            cov_pct = 0.0
        print(f"length-coverage recall (straight walls, >=80% covered): "
              f"{rep['coverage_covered']}/{rep['coverage_n']} ({cov_pct:.1f}%)")
        if rep["thickness_known"]:
            print(f"thickness agreement (matched, known both sides): "
                  f"{rep['thickness_agreed']}/{rep['thickness_known']} "
                  f"({rep['thickness_agreed'] / rep['thickness_known'] * 100:.1f}%)")
        print(f"curtain/basic mismatches (matched): {rep['curtain_mismatch']}")
    elif cat in ("doors", "windows"):
        if rep["width_known"]:
            print(f"width agreement (matched, known both sides): "
                  f"{rep['width_agreed']}/{rep['width_known']} "
                  f"({rep['width_agreed'] / rep['width_known'] * 100:.1f}%)")
        print(f"unhosted in test (matched, host_id null): {rep['unhosted_matched']}")
    elif cat == "columns":
        if rep["size_known"]:
            print(f"size agreement (matched, known both sides, swap/rotation allowed): "
                  f"{rep['size_agreed']}/{rep['size_known']} "
                  f"({rep['size_agreed'] / rep['size_known'] * 100:.1f}%)")
    elif cat == "stairs":
        if rep["matched"]:
            print(f"riser-count exact agreement (matched): {rep['riser_agree']}/{rep['matched']}")
            print(f"run-count exact agreement (matched): {rep['run_agree']}/{rep['matched']}")
    elif cat == "floors":
        print(f"footprint: truth floors >=70% covered by test slabs = {rep['matched']}/{rep['truth_n']} (recall above); "
              f"test slabs >=50% over truth floors = {rep['supported']}/{rep['test_n']} (precision above); "
              f"truth bbox area covered {rep['truth_bbox_area_covered'] * 100:.1f}%; "
              f"area truth {rep['truth_area_m2']:.0f} m2 vs test {rep['test_area_m2']:.0f} m2")
    elif cat == "grids":
        if rep["endpoint_known"]:
            print(f"endpoint agreement (name-matched, known both sides): "
                  f"{rep['endpoint_agreed']}/{rep['endpoint_known']}")

    if rep["misses_n"]:
        shown = len(rep["misses"])
        suffix = "" if cap_all or shown == rep["misses_n"] else f" (showing {shown}/{rep['misses_n']}, use --all)"
        print(f"-- misses (truth unmatched){suffix}:")
        for line in rep["misses"]:
            print(f"   {line}")
    if rep["extras_n"]:
        shown = len(rep["extras"])
        suffix = "" if cap_all or shown == rep["extras_n"] else f" (showing {shown}/{rep['extras_n']}, use --all)"
        print(f"-- extras (test unmatched){suffix}:")
        for line in rep["extras"]:
            print(f"   {line}")


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("truth", help="ground-truth dump_model JSON")
    ap.add_argument("test", help="test/rebuilt dump_model JSON")
    ap.add_argument("--level", help="only compare elements on this level (case-insensitive); grids are not level-filtered")
    ap.add_argument("--bbox", nargs=4, type=float, metavar=("XMIN", "YMIN", "XMAX", "YMAX"),
                     help="only compare elements whose bbox touches this rectangle (mm)")
    ap.add_argument("--tol-pos", type=float, default=150.0, help="position tolerance, mm (default 150)")
    ap.add_argument("--tol-width", type=float, default=100.0, help="width tolerance, mm (default 100)")
    ap.add_argument("--tol-thick", type=float, default=30.0, help="wall-thickness tolerance, mm (default 30)")
    ap.add_argument("--ignore-level", action="store_true", help="never restrict matching by level name")
    ap.add_argument("--truth-cut", action="store_true",
                     help="with --level: select TRUTH walls/columns by the plan cut plane (bbox z-range spans the "
                          "level elevation + --cut-offset) instead of by level name, i.e. everything a floor plan of "
                          "that level shows - what a level-by-level CAD rebuild can be expected to produce; "
                          "doors/windows/stairs still filter by level name. Implies --ignore-level for walls/columns.")
    ap.add_argument("--cut-offset", type=float, default=1200.0, help="plan cut plane above the level, mm (default 1200)")
    ap.add_argument("--all", action="store_true", help="don't cap miss/extra lists at 40 rows")
    ap.add_argument("--json", metavar="FILE", help="write the full result as JSON")
    ap.add_argument("--csv", metavar="FILE", help="write a per-category summary as CSV")
    args = ap.parse_args()

    truth_doc = load(args.truth)
    test_doc = load(args.test)

    def level_elev(doc, name):
        for l in doc.get("levels") or []:
            if str(l.get("name", "")).strip().lower() == name.strip().lower():
                return l.get("elevation_mm")
        return None

    def prep(doc, cat, is_truth=False):
        elems = doc.get(cat) or []
        field = LEVEL_FIELD.get(cat)
        if field and args.level:
            if is_truth and args.truth_cut and cat in ("walls", "columns"):
                elev = level_elev(truth_doc, args.level)
                if elev is None:
                    elev = level_elev(test_doc, args.level)
                if elev is None:
                    raise SystemExit(f"--truth-cut: level '{args.level}' not found in either dump's levels list")
                zcut = elev + args.cut_offset
                elems = [e for e in elems if e.get("bbox") and len(e["bbox"]) >= 6
                         and e["bbox"][4] <= zcut <= e["bbox"][5]]
            else:
                elems = filter_level(elems, field, args.level)
        if args.bbox:
            get_bb = grid_bbox if cat == "grids" else bbox_xy
            elems = filter_bbox(elems, tuple(args.bbox), get_bb)
        return elems

    results = {}
    for cat in CATEGORIES:
        t_elems = prep(truth_doc, cat, is_truth=True)
        s_elems = prep(test_doc, cat)
        ignore_lvl = args.ignore_level or (args.truth_cut and cat in ("walls", "columns"))
        if cat == "walls":
            rep = report_walls(t_elems, s_elems, args.tol_pos, args.tol_thick, ignore_lvl, args.all)
        elif cat in ("doors", "windows"):
            rep = report_doors_windows(cat, t_elems, s_elems, args.tol_pos, args.tol_width, args.ignore_level, args.all)
        elif cat == "columns":
            rep = report_columns(t_elems, s_elems, args.tol_pos, args.tol_width, ignore_lvl, args.all)
        elif cat == "stairs":
            rep = report_stairs(t_elems, s_elems, args.all)
        elif cat == "floors":
            rep = report_floors(t_elems, s_elems, args.tol_pos, args.ignore_level, args.all)
        elif cat == "grids":
            rep = report_grids(t_elems, s_elems, args.tol_pos, args.all)
        else:
            continue
        results[cat] = rep

    print(f"Truth: {args.truth}  ({truth_doc.get('document')})")
    print(f"Test:  {args.test}  ({test_doc.get('document')})")
    if args.level:
        print(f"Level filter: {args.level}" + (f"  (truth walls/columns by cut plane @ +{args.cut_offset:g} mm)" if args.truth_cut else ""))
    if args.bbox:
        print(f"Bbox filter: {args.bbox}")

    for cat in CATEGORIES:
        print_report(cat, results[cat], args.all)

    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(results, f, indent=1)
        print(f"\nWrote {args.json}")

    if args.csv:
        with open(args.csv, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["category", "truth_n", "test_n", "matched", "precision", "recall", "f1"])
            for cat in CATEGORIES:
                rep = results[cat]
                w.writerow([cat, rep["truth_n"], rep["test_n"], rep["matched"],
                            f"{rep['precision']:.4f}", f"{rep['recall']:.4f}", f"{rep['f1']:.4f}"])
        print(f"Wrote {args.csv}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
