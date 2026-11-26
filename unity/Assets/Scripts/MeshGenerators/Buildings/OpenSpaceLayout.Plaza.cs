using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Buffer;
using UnityEngine;

/// <summary>
/// Placement plan for civic squares (MODEL_PLAZA), in local coordinates of the road-facing edge (s along it, t inward) so the
/// paving stays square to that edge however the site is turned.
/// The whole site is stone slabs, their joints laid as surfaces because a ground texture follows the world axes, not the site's.
/// The flagpole stands at the point farthest from the site edge, where two bands of accent stone
/// cross: one from the road-facing edge to the back, one across, each lined with lamps. A third band frames the site. Just inside
/// the frame, planters (each with a bench before it) and lamps alternate around the site; the bands and the gaps between planters
/// stay open to walk through.
/// Parts are placed at their actual sizes, and a part missing from the model folder is left out.
/// </summary>
public static partial class OpenSpaceLayout
{
    const double PlazaSlab = 3.0;        // Spacing of the joints between slabs (m).
    const int PlazaMaxJoints = 60;       // Most joints in one direction; larger sites get larger slabs instead of more surfaces.
    const double PlazaJoint = .08;       // Joint width (m).
    const double PlazaFrame = 1.2;       // Width of the band along the site edge (m).
    const double PlazaBay = 12.0;        // Spacing of the planters around the site (m); a lamp stands midway between two planters.
    const double PlazaGap = .35;         // Clear ground between the frame and a planter, a planter and its bench, a band and its lamps (m).

    static void Plaza(Plan p, IReadOnlyDictionary<string, Vector2> sizes)
    {
        Vector2 u = (p.Polygon[1] - p.Polygon[0]).normalized;
        if (u.sqrMagnitude < .5f) u = Vector2.right;
        var f = new Frame { O = (p.Polygon[0] + p.Polygon[1]) * .5f, U = u, V = new Vector2(-u.y, u.x) };
        Geometry site = LocalSite(p, f);
        if (site == null || site.Area < 12) return;
        var env = site.EnvelopeInternal;
        double slab = Math.Max(PlazaSlab, Math.Max(env.Width, env.Height) / PlazaMaxJoints);
        Coordinate pole = PlazaCenter(site);

        // Bands: their edges lie on joints, so the slabs between them are whole on both sides of each band.
        var mitre = new BufferParameters { JoinStyle = JoinStyle.Mitre };
        Geometry cross = Union(Rect(pole.X - slab * .5, env.MinY - 1, pole.X + slab * .5, env.MaxY + 1),
            Rect(env.MinX - 1, pole.Y - slab * .5, env.MaxX + 1, pole.Y + slab * .5));
        Geometry bands = And(Union(cross, Minus(site, site.Buffer(-PlazaFrame, mitre))), site);
        Geometry fields = Minus(site, bands);
        Emit(p, f, bands, "site_paving", .014f);
        for (int side = -1; side <= 1; side += 2)
        {
            for (double s = pole.X + side * slab * 1.5; s > env.MinX && s < env.MaxX; s += side * slab)
                Emit(p, f, And(Rect(s - PlazaJoint * .5, env.MinY - 1, s + PlazaJoint * .5, env.MaxY + 1), fields), "site_joint", .016f);
            for (double t = pole.Y + side * slab * 1.5; t > env.MinY && t < env.MaxY; t += side * slab)
                Emit(p, f, And(Rect(env.MinX - 1, t - PlazaJoint * .5, env.MaxX + 1, t + PlazaJoint * .5), fields), "site_joint", .016f);
        }
        if (sizes == null || sizes.Count == 0) return;

        // Things stand clear of the two crossing bands; the frame is only a pattern on the ground.
        var placer = new ParkPlacer { Plan = p, F = f, Sizes = sizes, Site = PreparedGeometryFactory.Prepare(site.Buffer(-.15)),
            Hard = PreparedGeometryFactory.Prepare(cross), Objects = new ParkDiscs(4) };
        // The flagpole is combined into the building's mesh: the flag's material is the city's, resolved with the building's materials.
        if (placer.Put("site_flagpole", pole.X, pole.Y, 0, -1, null, 0)) p.Props[p.Props.Count - 1].Unit = true;

        sizes.TryGetValue("site_planter", out Vector2 planter);
        sizes.TryGetValue("site_bench", out Vector2 bench);
        var inner = Largest(site.Buffer(-(PlazaFrame + PlazaGap + planter.y * .5), mitre));
        if (inner != null)
        {
            Coordinate[] ring = Oriented(inner.ExteriorRing.Coordinates, false);
            var along = new double[ring.Length];
            for (int i = 1; i < ring.Length; i++) along[i] = along[i - 1] + ring[i].Distance(ring[i - 1]);
            double length = along[ring.Length - 1];
            int bays = Math.Max(1, (int)Math.Round(length / PlazaBay));
            // Counted from where the band from the road-facing edge enters, so both sides of that band mirror each other.
            double start = PlazaNearest(ring, along, pole.X, env.MinY), step = length / bays;
            for (int k = 0; k < bays; k++)
            {
                var (ls, lt, lds, ldt) = PlazaAt(ring, along, start + k * step);
                placer.Put("site_light", ls, lt, -ldt, lds, false);
                var (s, t, ds, dt) = PlazaAt(ring, along, start + (k + .5) * step);
                // The ring runs counterclockwise, so (-dt, ds) points into the square.
                if (!placer.Put("site_planter", s, t, -dt, ds, false)) continue;
                double reach = planter.y * .5 + PlazaGap + bench.y * .5;
                placer.Put("site_bench", s - dt * reach, t + ds * reach, -dt, ds, false);
            }
        }

        // Lamps line both sides of the two bands, every second bay out from the flagpole, where the things around the site leave room.
        sizes.TryGetValue("site_light", out Vector2 lamp);
        foreach (var (ds, dt) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
            for (double d = PlazaBay * 2; d < Math.Max(env.Width, env.Height); d += PlazaBay * 2)
                for (int side = -1; side <= 1; side += 2)
                {
                    double off = side * (slab * .5 + PlazaGap + lamp.x * .5);
                    placer.Put("site_light", pole.X + ds * d - dt * off, pole.Y + dt * d + ds * off, dt * side, -ds * side, false);
                }
    }

    /// <summary>
    /// The point farthest from the site edge: the centroid when it is about as far in as any point (regular sites, where the
    /// farthest points form a line or a patch), otherwise the best of a grid of samples.
    /// </summary>
    static Coordinate PlazaCenter(Geometry site)
    {
        var env = site.EnvelopeInternal;
        double scale = Math.Sqrt(site.Area), h = Math.Max(.5, scale / 60), reach = scale + 4;
        var edges = new ParkEdgeIndex(site, Math.Max(8, scale));
        var area = new NetTopologySuite.Algorithm.Locate.IndexedPointInAreaLocator(site);
        var probe = new Coordinate();
        Coordinate best = site.Centroid.Coordinate;
        double bestClear = -1;
        for (double y = env.MinY + h * .5; y < env.MaxY; y += h)
            for (double x = env.MinX + h * .5; x < env.MaxX; x += h)
            {
                probe.X = x; probe.Y = y;
                if (area.Locate(probe) != Location.Interior) continue;
                double clear = edges.Distance(x, y, reach);
                if (clear > bestClear) { bestClear = clear; best = new Coordinate(x, y); }
            }
        Coordinate centroid = site.Centroid.Coordinate;
        if (area.Locate(centroid) == Location.Interior && edges.Distance(centroid.X, centroid.Y, reach) >= bestClear - h) return centroid;
        return best;
    }

    /// <summary>Arc length along a closed ring of its point nearest to (s, t).</summary>
    static double PlazaNearest(Coordinate[] ring, double[] along, double s, double t)
    {
        double best = double.MaxValue, at = 0;
        for (int i = 0; i + 1 < ring.Length; i++)
        {
            double ds = ring[i + 1].X - ring[i].X, dt = ring[i + 1].Y - ring[i].Y, len = ds * ds + dt * dt;
            double k = len < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((s - ring[i].X) * ds + (t - ring[i].Y) * dt) / len));
            double px = ring[i].X + ds * k - s, py = ring[i].Y + dt * k - t, d = px * px + py * py;
            if (d < best) { best = d; at = along[i] + Math.Sqrt(len) * k; }
        }
        return at;
    }

    /// <summary>Point and unit tangent of a closed ring at an arc length, wrapping past its end.</summary>
    static (double s, double t, double ds, double dt) PlazaAt(Coordinate[] ring, double[] along, double at)
    {
        double length = along[ring.Length - 1];
        at -= Math.Floor(at / length) * length;
        int i = Array.BinarySearch(along, at);
        if (i < 0) i = ~i - 1;
        i = Math.Max(0, Math.Min(ring.Length - 2, i));
        while (i < ring.Length - 2 && along[i + 1] - along[i] < 1e-9) i++;
        double len = Math.Max(1e-9, along[i + 1] - along[i]), k = (at - along[i]) / len;
        double ds = (ring[i + 1].X - ring[i].X) / len, dt = (ring[i + 1].Y - ring[i].Y) / len;
        return (ring[i].X + ds * len * k, ring[i].Y + dt * len * k, ds, dt);
    }
}
