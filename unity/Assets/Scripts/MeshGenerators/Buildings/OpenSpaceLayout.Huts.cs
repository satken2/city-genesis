using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using UnityEngine;

/// <summary>
/// Placement plan for huts (MODEL_HUTS): huts in rows filling the parcel, which keeps its bare ground.
/// One rule fills a length with huts along either axis. Of the hut counts, take the one whose huts stretch the least; give each hut
/// an equal slot; let the gap between pads take up to HutGap of the slot, and stretch the hut for the rest. A length too short for a
/// hut shrunk to HutShrink stays empty.
/// Rows lie along the road-facing edge and are counted over the parcel's depth. Along a row, huts fill each stretch where the row has
/// room for a shrunk hut, aiming at the row's stretch so a hut keeps its proportions where it can. Each hut then takes the depth its
/// slot has inside the parcel, so an edge slanting across a row shrinks the huts along it and leaves a strip where they no longer fit.
/// Huts face the road. Lanes run down the middle of the gaps between neighboring huts: those between rows run through, those between
/// the huts of a row stop half a lane width short of them.
/// The huts are the numbered parts site_hut1… (as many as are numbered consecutively from 1), drawn per slot by the seed.
/// Built in local coordinates relative to the road-facing edge (s along the edge, t inward), mirrored left/right by the seed.
/// </summary>
public static partial class OpenSpaceLayout
{
    const double HutGap = 4;      // Widest gap between neighboring huts' pads (m). A hut at the parcel edge keeps half of it there.
    const double HutShrink = .8;  // Smallest stretch of a hut.
    const double HutProbe = .25;  // Step (m) along a row when finding where it has room for a hut.
    const double HutEdge = .01;   // Inset (m) of a slot's sides when testing it against the parcel, so a side lying on the parcel edge does not count as outside.

    struct HutSlot { public double S0, S1, T0, T1; }

    static void Huts(Plan p, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        p.Bare = true;
        var huts = new List<string>();
        Vector2 hut = Vector2.zero;
        for (int n = 1; sizes != null && sizes.TryGetValue("site_hut" + n, out var size); n++) { huts.Add("site_hut" + n); hut = Vector2.Max(hut, size); }
        if (huts.Count == 0) return;
        Frame f = FacilityFrame(p, seed);
        Geometry site = LocalSite(p, f);
        if (site == null) return;
        var env = site.EnvelopeInternal;
        var (rows, rowPitch, rowStretch) = HutFill(env.Height, hut.y, 1);
        var placed = new List<List<HutSlot>>();
        int drawn = 0;
        for (int r = 0; r < rows; r++)
        {
            double t0 = env.MinY + r * rowPitch, t1 = t0 + rowPitch;
            var row = new List<HutSlot>();
            foreach (var (s0, s1) in HutRuns(site, env, t0, t1, hut.y * HutShrink))
            {
                var (count, pitch, stretch) = HutFill(s1 - s0, hut.x, rowStretch);
                for (int i = 0; i < count; i++)
                {
                    var slot = new HutSlot { S0 = s0 + i * pitch, S1 = s0 + (i + 1) * pitch };
                    if (!HutDepth(site, slot.S0, slot.S1, t0, t1, out slot.T0, out slot.T1)) continue;
                    double depth = slot.T1 - slot.T0;
                    if (depth < hut.y * HutShrink) continue;
                    double across = Math.Max(Math.Min(stretch, depth / hut.y), (depth - HutGap) / hut.y);
                    string part = huts[Math.Min(huts.Count - 1, (int)(Rand(seed, 100 + drawn++) * huts.Count))];
                    Vector2 own = sizes[part];
                    p.Props.Add(new Prop { Part = part, Position = f.World((slot.S0 + slot.S1) * .5, (slot.T0 + slot.T1) * .5), Forward = f.Dir(0, -1),
                        Scale = new Vector3((float)(stretch * hut.x / own.x), (float)Math.Sqrt(stretch * across), (float)(across * hut.y / own.y)),
                        Fitted = true, Unit = true });
                    row.Add(slot);
                }
            }
            placed.Add(row);
        }
        if (sizes.TryGetValue("lane", out var lane)) HutLanes(p, f, placed, env.MinY, rowPitch, lane.y * .5);
    }

    /// <summary>
    /// Fill a length with huts of the given size: the count, the slot pitch, and the huts' stretch, which comes as near target as the
    /// gap allows. The count whose stretch is nearest target wins, then the one nearest the authored size. No huts when even one
    /// would shrink below HutShrink.
    /// </summary>
    static (int count, double pitch, double stretch) HutFill(double length, double size, double target)
    {
        (int count, double pitch, double stretch) best = (0, 0, 0);
        double bestOff = double.MaxValue, bestSize = double.MaxValue;
        for (int n = 1; length / n >= size * HutShrink; n++)
        {
            double pitch = length / n, stretch = Math.Max(Math.Min(target, pitch / size), (pitch - HutGap) / size);
            double off = Math.Round(Math.Abs(Math.Log(stretch / target)), 6), fromSize = Math.Abs(Math.Log(stretch));
            if (off > bestOff || (off == bestOff && fromSize >= bestSize)) continue;
            best = (n, pitch, stretch); bestOff = off; bestSize = fromSize;
        }
        return best;
    }

    /// <summary>
    /// Stretches of the band t0–t1 where some line across it holds at least need of the parcel: probed every HutProbe and
    /// refined at the ends.
    /// </summary>
    static List<(double s0, double s1)> HutRuns(Geometry site, Envelope env, double t0, double t1, double need)
    {
        var runs = new List<(double, double)>();
        bool Open(double s) => HutCross(site, s, t0, t1) >= need;
        double Edge(double open, double shut)
        {
            for (int k = 0; k < 20; k++) { double mid = (open + shut) * .5; if (Open(mid)) open = mid; else shut = mid; }
            return open;
        }
        int steps = Math.Max(1, (int)Math.Ceiling(env.Width / HutProbe));
        double step = env.Width / steps, start = double.NaN;
        for (int i = 0; i <= steps; i++)
        {
            double s = env.MinX + i * step;
            bool open = Open(s);
            if (open && double.IsNaN(start)) start = i == 0 ? s : Edge(s, s - step);
            if (!open && !double.IsNaN(start)) { runs.Add((start, Edge(s - step, s))); start = double.NaN; }
        }
        if (!double.IsNaN(start)) runs.Add((start, env.MaxX));
        return runs;
    }

    /// <summary>Longest piece of the parcel on the line across the band t0–t1 at s.</summary>
    static double HutCross(Geometry site, double s, double t0, double t1)
    {
        var crossings = new List<double>();
        void Ring(LineString ring)
        {
            var c = ring.Coordinates;
            for (int i = 0; i + 1 < c.Length; i++)
                if ((c[i].X <= s) != (c[i + 1].X <= s)) crossings.Add(c[i].Y + (c[i + 1].Y - c[i].Y) * (s - c[i].X) / (c[i + 1].X - c[i].X));
        }
        foreach (var polygon in Polygons(site))
        {
            Ring(polygon.ExteriorRing);
            for (int h = 0; h < polygon.NumInteriorRings; h++) Ring(polygon.GetInteriorRingN(h));
        }
        crossings.Sort();
        double longest = 0;
        for (int i = 0; i + 1 < crossings.Count; i += 2)
            longest = Math.Max(longest, Math.Min(crossings[i + 1], t1) - Math.Max(crossings[i], t0));
        return longest;
    }

    /// <summary>The longest depth in the band t0–t1 over which the slot's whole width s0–s1 lies in the parcel. False when there is none.</summary>
    static bool HutDepth(Geometry site, double s0, double s1, double t0, double t1, out double from, out double to)
    {
        var blocked = new List<(double lo, double hi)>();
        foreach (var piece in Polygons(Minus(Rect(s0 + HutEdge, t0, s1 - HutEdge, t1), site)))
            if (piece.Area > 1e-6) blocked.Add((piece.EnvelopeInternal.MinY, piece.EnvelopeInternal.MaxY));
        blocked.Sort((a, b) => a.lo.CompareTo(b.lo));
        from = to = t0;
        double cursor = t0;
        foreach (var (lo, hi) in blocked)
        {
            if (lo - cursor > to - from) { from = cursor; to = lo; }
            cursor = Math.Max(cursor, hi);
        }
        if (t1 - cursor > to - from) { from = cursor; to = t1; }
        return to > from;
    }

    /// <summary>
    /// Lanes down the gaps between neighboring huts. Between rows, along the row boundary where huts on both sides reach it.
    /// Between neighboring huts of a row, along their shared slot side over the depth both have, stopping half a lane width short of a lane between rows.
    /// </summary>
    static void HutLanes(Plan p, Frame f, List<List<HutSlot>> rows, double front, double rowPitch, double halfWidth)
    {
        const double eps = 1e-6;
        var across = new List<(double t, double s0, double s1)>();
        for (int r = 0; r + 1 < rows.Count; r++)
        {
            double t = front + (r + 1) * rowPitch;
            var pieces = new List<(double s0, double s1)>();
            foreach (var lower in rows[r])
                foreach (var upper in rows[r + 1])
                {
                    double s0 = Math.Max(lower.S0, upper.S0), s1 = Math.Min(lower.S1, upper.S1);
                    if (s1 - s0 > eps && Math.Abs(lower.T1 - t) < eps && Math.Abs(upper.T0 - t) < eps) pieces.Add((s0, s1));
                }
            pieces.Sort((a, b) => a.s0.CompareTo(b.s0));
            for (int i = 0; i < pieces.Count; i++)
            {
                double s0 = pieces[i].s0, s1 = pieces[i].s1;
                while (i + 1 < pieces.Count && pieces[i + 1].s0 <= s1 + eps) s1 = Math.Max(s1, pieces[++i].s1);
                across.Add((t, s0, s1));
                p.Lanes.Add((f.World(s0, t), f.World(s1, t)));
            }
        }
        bool Crossed(double s, double t)
        {
            foreach (var lane in across) if (Math.Abs(lane.t - t) < eps && s >= lane.s0 - eps && s <= lane.s1 + eps) return true;
            return false;
        }
        foreach (var row in rows)
            for (int i = 0; i + 1 < row.Count; i++)
            {
                HutSlot a = row[i], b = row[i + 1];
                if (Math.Abs(a.S1 - b.S0) > eps) continue;
                double s = a.S1, t0 = Math.Max(a.T0, b.T0), t1 = Math.Min(a.T1, b.T1);
                if (Crossed(s, t0)) t0 += halfWidth;
                if (Crossed(s, t1)) t1 -= halfWidth;
                if (t1 - t0 > eps) p.Lanes.Add((f.World(s, t0), f.World(s, t1)));
            }
    }
}
