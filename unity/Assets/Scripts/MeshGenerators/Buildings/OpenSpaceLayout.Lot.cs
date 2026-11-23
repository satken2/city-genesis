using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Buffer;
using UnityEngine;

/// <summary>
/// Parking lots laid around what stands on a site, as on the forecourt of a gas station. The whole site is paved. A planted strip with a curb runs around its edge and opens
/// for driveways on the road-facing edge. Parking stalls go nose-in to the building front behind a raised walk, in double rows
/// across open ground, and along the free edges; a stall is two lines, a wheel stop, and now and then a parked car. A stall needs a
/// drive aisle behind it, which on a shallow lot may be a driveway and the road beyond it. In the planted strip stand the sign at
/// a front corner, yard lights, shrubs, and trees.
/// Built in local coordinates relative to the road-facing edge (s along the edge, t inward), mirrored left/right by the seed.
/// Parts missing from the model folder are skipped.
/// </summary>
public static partial class OpenSpaceLayout
{
    const double LotAisle = 6.0;          // Clear run for cars: behind a parked car, inside a driveway, between the road and what stands on the lot (m).
    const double LotDriveway = 9.0;       // Width of a driveway on the road-facing edge (m).
    const double LotPlanter = 4.0;        // Least planted strip left between two driveways (m); a narrower frontage opens as one driveway.
    const double LotWalk = 1.6;           // Depth of the raised walk along a building front (m).
    const double LotStallWidth = 2.6, LotStallDepth = 5.2;
    const double LotDoor = 2.4;           // Half the run of building front kept clear at the door (m). The door is at the center of the front.
    const double LotDoorReach = 8.0;      // How far either side of the door the things a shop keeps outside stand (m).
    const int LotRowStalls = 4;           // Fewest stalls side by side that make a parking row on open ground.
    const double LotSignDepth = 3.0;      // How far in from the road-facing edge the sign stands (m).
    const float LotCarAtFront = .55f, LotCarAtEdge = .25f;  // Chance that a car stands in a stall at the building front, elsewhere.

    /// <summary>
    /// The parcel as a plan's site: its vertices starting with the edge the building's front (footprint vertex 0→1) looks onto.
    /// Falls back to the footprint without a parcel or when the front does not look onto the parcel's edge.
    /// </summary>
    public static Vector2[] LotFacingRoad(Vector2[] parcel, Vector2[] footprint)
    {
        if (parcel == null || parcel.Length < 3 || footprint == null || footprint.Length < 3) return footprint;
        Vector2 edge = footprint[1] - footprint[0], outward = new Vector2(edge.y, -edge.x).normalized, origin = (footprint[0] + footprint[1]) * .5f;
        int first = -1;
        float nearest = float.MaxValue;
        for (int i = 0; i < parcel.Length; i++)
        {
            Vector2 a = parcel[i], side = parcel[(i + 1) % parcel.Length] - a;
            float cross = Cross(outward, side);
            if (Mathf.Abs(cross) < 1e-6f) continue;
            float along = Cross(a - origin, side) / cross, across = Cross(a - origin, outward) / cross;
            if (along < -.05f || across < 0f || across > 1f || along >= nearest) continue;
            nearest = along; first = i;
        }
        if (first < 0) return footprint;
        var lot = new Vector2[parcel.Length];
        for (int i = 0; i < lot.Length; i++) lot[i] = parcel[(first + i) % parcel.Length];
        return lot;
    }

    /// <summary>
    /// The planted strip around a lot, with its curb: the site outside paved, which is the site inside the edge strip joined by the
    /// driveways through the strip on the road-facing edge (one at each end of a frontage wide enough, else one over the frontage).
    /// paved is null when the site is too small to have an inside.
    /// </summary>
    static Geometry LotEdge(Plan p, Frame f, Geometry site, double edge, double halfFront, out Polygon paved, out List<Geometry> driveways)
    {
        driveways = new List<Geometry>();
        paved = Largest(site.Buffer(-edge, new BufferParameters { JoinStyle = JoinStyle.Mitre }));
        if (paved == null) return null;
        double lo = -halfFront + edge, hi = halfFront - edge;
        if (hi - lo >= 2 * LotDriveway + LotPlanter)
        {
            driveways.Add(Rect(lo, -1, lo + LotDriveway, edge + .01));
            driveways.Add(Rect(hi - LotDriveway, -1, hi, edge + .01));
        }
        else if (hi > lo) driveways.Add(Rect(lo, -1, hi, edge + .01));
        Geometry open = paved;
        foreach (var driveway in driveways) open = Union(open, And(site, driveway));
        Geometry planter = Minus(site, open);
        Emit(p, f, planter, "site_grass", Curb, "site_curb");
        Emit(p, f, And(planter, open.Buffer(.2, Round())), "site_curb", Curb + .003f);
        return planter;
    }

    /// <summary>The n-th draw's pick among the numbered parts of a series present in the model folder. Null when there are none.</summary>
    static string LotPick(FacilityYard y, string series, uint seed, int n)
    {
        int count = 0;
        while (y.Has(series + (count + 1))) count++;
        return count == 0 ? null : series + (1 + Math.Min(count - 1, (int)(Rand(seed, n) * count)));
    }

    /// <summary>
    /// Before a building front of the given width centered at s, at depth front: a raised walk with the things a shop keeps outside
    /// against the wall near the door, bollards along its edge, and parking stalls nose-in to it where a drive aisle stays
    /// clear behind them.
    /// </summary>
    static void LotFront(FacilityYard y, uint seed, double s, double front, double width)
    {
        if (!y.Claim(s, front - LotWalk * .5, 0, 1, width, LotWalk)) return;
        Emit(y.Plan, y.F, Rect(s - width * .5, front - LotWalk, s + width * .5, front), "site_paving", Curb, "site_curb");
        int slot = 0;
        for (double at = s - width * .5 + 1.2; at <= s + width * .5 - 1.2; at += 2.1, slot += 2)
        {
            string part = LotPick(y, "site_clutter", seed, 300 + slot);
            if (part == null || Math.Abs(at - s) < LotDoor || Math.Abs(at - s) > LotDoorReach || Rand(seed, 301 + slot) > .8f) continue;
            y.Add(part, at, front - y.Sizes[part].y * .5 - .08, 0, -1, false);
        }
        int stalls = (int)Math.Floor(width / LotStallWidth);
        for (int i = 0; i <= stalls; i++)
            y.Add("site_bollard", s + (i - stalls * .5) * LotStallWidth, front - LotWalk + .3, 0, -1, false);
        for (int i = 0; i < stalls; i++)
            LotStall(y, seed, s + (i - (stalls - 1) * .5) * LotStallWidth, front - LotWalk - LotStallDepth * .5, 0, 1, LotCarAtFront, 400 + i * 4);
    }

    /// <summary>
    /// One parking stall centered at (s,t), nose toward (fs,ft), if it and a drive aisle behind it are free: its two lines,
    /// a wheel stop, and now and then a parked car.
    /// </summary>
    static bool LotStall(FacilityYard y, uint seed, double s, double t, double fs, double ft, float occupied, int draw)
    {
        if (!y.Claim(s, t, fs, ft, LotStallWidth, LotStallDepth, LotAisle)) return false;
        double xs = ft, xt = -fs, half = LotStallWidth * .5, back = LotStallDepth * .5;
        foreach (int side in new[] { -1, 1 })
            Emit(y.Plan, y.F, Strip(s + side * xs * half - fs * back, t + side * xt * half - ft * back, fs, ft, LotStallDepth, .1), "site_marking", .022f);
        y.Add("site_stop", s + fs * (back - .7), t + ft * (back - .7), -fs, -ft, false);
        if (Rand(seed, draw) < occupied)
        {
            int nose = Rand(seed, draw + 1) < .7f ? 1 : -1;
            y.Add(LotPick(y, "site_car", seed, draw + 2), s - fs * .25, t - ft * .25, nose * fs, nose * ft, false);
        }
        return true;
    }

    /// <summary>
    /// Double rows of stalls, nose to nose, across the lot wherever a run of them with a drive aisle on both sides is free. Rows keep
    /// clear of the lot's sides and rear by a stall and an aisle, which the edge stalls take.
    /// </summary>
    static void LotRows(FacilityYard y, uint seed)
    {
        var env = y.Env;
        double keep = LotStallDepth + LotAisle;
        int draw = 0;
        // t is the line where the two rows' noses meet.
        for (double t = env.MinY + keep; t + keep <= env.MaxY - LotStallDepth + 1e-6;)
        {
            var free = new List<double>();
            for (double s = env.MinX + keep + LotStallWidth * .5; s + LotStallWidth * .5 <= env.MaxX - keep + 1e-6; s += LotStallWidth)
                if (y.Free(s, t, LotStallWidth, 2 * keep)) free.Add(s);
            if (free.Count < LotRowStalls) { t += 2; continue; }
            foreach (double s in free)
                foreach (int half in new[] { -1, 1 })
                {
                    draw += 4;
                    LotStall(y, seed, s, t + half * LotStallDepth * .5, 0, -half, LotCarAtEdge, 700 + draw);
                }
            t += 2 * LotStallDepth + LotAisle;
        }
    }

    /// <summary>Parking stalls nose-in to the lot's edge wherever the edge and a drive aisle behind the stall are free, except along the road.</summary>
    static void LotEdgeStalls(FacilityYard y, uint seed, Polygon paved)
    {
        int draw = 0;
        foreach (var d in Walk(Oriented(paved.ExteriorRing.Coordinates, false), LotStallWidth * .5, LotStallWidth))
        {
            double ns = -d.dt, nt = d.ds;
            draw += 4;
            if (nt > .7 && d.t < y.Env.MinY + LotAisle) continue;
            LotStall(y, seed, d.s + ns * (LotStallDepth * .5 + .05), d.t + nt * (LotStallDepth * .5 + .05), -ns, -nt, LotCarAtEdge, 4000 + draw);
        }
    }

    /// <summary>
    /// In the planted strip: the sign near a front corner, broadside to the road's traffic; yard lights; shrubs, and trees where
    /// the strip is wide enough for a trunk.
    /// </summary>
    static void LotPlanting(FacilityYard y, uint seed, Geometry planter, double halfFront)
    {
        var middle = Largest(y.Site.Buffer(-y.Edge * .5, new BufferParameters { JoinStyle = JoinStyle.Mitre }));
        if (middle == null || planter.IsEmpty) return;
        var planted = PreparedGeometryFactory.Prepare(planter);
        var ring = Oriented(middle.ExteriorRing.Coordinates, false);
        var standing = new List<Coordinate>();
        bool Clear(double s, double t, double gap)
        {
            if (!planted.Contains(Nts.CreatePoint(new Coordinate(s, t)))) return false;
            foreach (var other in standing) if (other.Distance(new Coordinate(s, t)) < gap) return false;
            return true;
        }
        (double s, double t, double ds, double dt)? sign = null;
        double nearest = double.MaxValue;
        foreach (var d in Walk(ring, 0, 1))
        {
            double distance = (d.s + halfFront) * (d.s + halfFront) + (d.t - LotSignDepth) * (d.t - LotSignDepth);
            if (distance >= nearest || !Clear(d.s, d.t, 0)) continue;
            nearest = distance; sign = d;
        }
        if (sign.HasValue && y.Has("site_sign"))
        {
            y.Add("site_sign", sign.Value.s, sign.Value.t, -sign.Value.dt, sign.Value.ds, true);
            standing.Add(new Coordinate(sign.Value.s, sign.Value.t));
        }
        foreach (var d in Walk(ring, 7, 19))
        {
            if (!Clear(d.s, d.t, 3)) continue;
            y.Add("site_light", d.s, d.t, -d.dt, d.ds, false);
            standing.Add(new Coordinate(d.s, d.t));
        }
        int draw = 0;
        foreach (var d in Walk(ring, 1.2, 2.4))
        {
            draw += 3;
            float lot = Rand(seed, 800 + draw);
            string part = lot < .1f && y.Edge >= 1.2 ? LotPick(y, "site_tree", seed, 801 + draw) : lot < .55f ? LotPick(y, "site_shrub", seed, 801 + draw) : null;
            if (part == null || !Clear(d.s, d.t, 1.6)) continue;
            // A shrub wider than the strip would spill over the curb; a tree's crown may.
            if (lot >= .1f && y.Sizes[part].x > y.Edge + .7) continue;
            double turn = Rand(seed, 802 + draw) * Math.PI * 2;
            y.Add(part, d.s, d.t, Math.Cos(turn), Math.Sin(turn), false);
        }
    }
}
