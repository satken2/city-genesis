using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>Geometric tests omitting piers/roadside equipment interfering with other roads/tracks, independent of drawing order.</summary>
public static class PillarClearance
{
    /// <summary>
    /// Passage space. Points may share PathManager's cached list; treat as read-only.
    /// Represent height lift with YOffset (added to each Y during tests).
    /// Pier feet may occupy medians within MedianHalfWidth of centerline or sidewalks within SidewalkWidth of edges (default zero makes full width roadway).
    /// </summary>
    public sealed class Corridor
    {
        public List<Vector3> Points;
        public float HalfWidth;
        public float Height;
        public float YOffset;
        public float MedianHalfWidth;
        public float SidewalkWidth;

        /// <summary>Forbidden-band count: index 0 is full-width deck; remaining bands are roadways (two sides if a median exists).</summary>
        internal int FootBandCount
        {
            get
            {
                float inner = Mathf.Max(0f, MedianHalfWidth), outer = HalfWidth - Mathf.Max(0f, SidewalkWidth);
                return outer <= inner ? 1 : inner > 0f ? 3 : 2;
            }
        }

        /// <summary>
        /// Band lateral offset/half-width and obstructing height above road surface.
        /// Deck height is -FootTolerance, obstructing only if surface is at least FootTolerance above the foot (pier passes through it).
        /// </summary>
        internal void GetFootBand(int index, out float offset, out float halfWidth, out float height)
        {
            offset = 0f;
            if (index == 0) { halfWidth = HalfWidth; height = -FootTolerance; return; }
            float inner = Mathf.Max(0f, MedianHalfWidth), outer = HalfWidth - Mathf.Max(0f, SidewalkWidth);
            height = Height;
            if (inner <= 0f) { halfWidth = outer; return; }
            halfWidth = (outer - inner) * 0.5f;
            offset = (index == 1 ? 1f : -1f) * (inner + halfWidth);
        }

        // Tests replace Points/HalfWidth externally; validate XZ bounds by Points reference/count, and use current HalfWidth for every test.
        private List<Vector3> boundsSource;
        private int boundsCount = -1;
        private float minX, maxX, minZ, maxZ;

        /// <summary>True if center lies more than radius outside Points' XZ bounds (cannot intersect any segment).</summary>
        internal bool IsOutsideXZ(Vector3 center, float radius)
        {
            if (!ReferenceEquals(boundsSource, Points) || boundsCount != Points.Count)
            {
                boundsSource = Points;
                boundsCount = Points.Count;
                minX = minZ = float.PositiveInfinity;
                maxX = maxZ = float.NegativeInfinity;
                for (int i = 0; i < Points.Count; i++)
                {
                    Vector3 p = Points[i];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
            }
            // Expand rejection bounds slightly so float/double distance rounding cannot miss intersections.
            double r = radius + 1e-3;
            return center.x < minX - r || center.x > maxX + r || center.z < minZ - r || center.z > maxZ + r;
        }
    }

    public static Func<PathSummary, List<Corridor>> NeighborProvider;
    public const float Margin = 0.25f;
    // Height treating surface roads (terrain+0.2m) and sidewalk steps as ground supporting posts.
    public const float FootTolerance = 0.5f;

    /// <summary>
    /// Project forbidden bands (roadways/tracks/decks) across pier direction; move each side's post outward only as far as needed.
    /// Stop when the post fits a sidewalk/median.
    /// Conservative approximation: expand bands by post circumcircle, clip segments by height/longitudinal range, then project laterally.
    /// </summary>
    public static void FindPortalOffsets(Vector3 origin, Vector3 right, float minY, float maxY,
        float legRadius, float minimumHalfSpan, List<Corridor> corridors, out float left, out float rightOffset)
    {
        left = -minimumHalfSpan;
        rightOffset = minimumHalfSpan;
        if (corridors == null) return;
        var intervals = new List<Vector2>();
        Vector3 forward = Vector3.Cross(right, Vector3.up);
        foreach (var corridor in corridors)
        {
            if (corridor.Points == null) continue;
            for (int band = 0; band < corridor.FootBandCount; band++)
            {
                corridor.GetFootBand(band, out float offset, out float halfWidth, out float height);
                float radius = legRadius + halfWidth + Margin;
                for (int i = 1; i < corridor.Points.Count; i++)
                {
                    Vector3 a = corridor.Points[i - 1] + Vector3.up * corridor.YOffset;
                    Vector3 b = corridor.Points[i] + Vector3.up * corridor.YOffset;
                    if (offset != 0f)
                    {
                        Vector3 shift = RightOf(a, b) * offset;
                        a += shift;
                        b += shift;
                    }
                    float lo = 0f, hi = 1f;
                    if (!ClipRange(a.y, b.y, minY - height, maxY, ref lo, ref hi)) continue;
                    if (!ClipRange(Vector3.Dot(a - origin, forward), Vector3.Dot(b - origin, forward),
                            -radius, radius, ref lo, ref hi)) continue;
                    float x0 = Vector3.Dot(Vector3.Lerp(a, b, lo) - origin, right);
                    float x1 = Vector3.Dot(Vector3.Lerp(a, b, hi) - origin, right);
                    intervals.Add(new Vector2(Mathf.Min(x0, x1) - radius, Mathf.Max(x0, x1) + radius));
                }
            }
        }
        // Exit overlapping bands in order; no need to span separate distant roads together.
        intervals.Sort((a, b) => a.x.CompareTo(b.x));
        for (int i = 0; i < intervals.Count; i++)
            if (rightOffset >= intervals[i].x && rightOffset <= intervals[i].y)
                rightOffset = intervals[i].y + 0.01f;
        intervals.Sort((a, b) => b.y.CompareTo(a.y));
        for (int i = 0; i < intervals.Count; i++)
            if (left >= intervals[i].x && left <= intervals[i].y)
                left = intervals[i].x - 0.01f;
    }

    private static bool ClipRange(float a, float b, float min, float max, ref float lo, ref float hi)
    {
        float delta = b - a;
        if (Mathf.Abs(delta) < 1e-6f) return a >= min && a <= max;
        float t0 = (min - a) / delta, t1 = (max - a) / delta;
        lo = Mathf.Max(lo, Mathf.Min(t0, t1));
        hi = Mathf.Min(hi, Mathf.Max(t0, t1));
        return lo <= hi;
    }

    /// <summary>Planar right unit vector of segment a→b. Zero for zero-length segments.</summary>
    private static Vector3 RightOf(Vector3 a, Vector3 b) => new Vector3(b.z - a.z, 0f, a.x - b.x).normalized;

    /// <summary>Bounds in prefab-root coordinates including child-mesh transforms. Apply root scale during placement.</summary>
    public static Bounds? GetPrefabBounds(GameObject prefab)
    {
        if (prefab == null) return null;
        Bounds? result = null;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;
            var matrix = prefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var bounds = Utils.TransformBounds(filter.sharedMesh.bounds, matrix);
            if (result.HasValue)
            {
                var combined = result.Value;
                combined.Encapsulate(bounds);
                result = combined;
            }
            else result = bounds;
        }
        return result;
    }

    /// <summary>
    /// Approximate ground-contact members (bottom=foot) by circumcircle and height interval; test forbidden bands.
    /// Bands cover roadway/track passage and full-width decks above foot, excluding surface sidewalks/medians.
    /// Intersect horizontal-overlap and height-overlap intervals along road segments to handle ramps.
    /// </summary>
    public static bool Intersects(Bounds part, List<Corridor> corridors)
    {
        if (corridors == null) return false;
        float partRadius = new Vector2(part.extents.x, part.extents.z).magnitude;
        foreach (var corridor in corridors)
            for (int band = 0; band < corridor.FootBandCount; band++)
            {
                corridor.GetFootBand(band, out float offset, out float halfWidth, out float height);
                if (Reaches(corridor, part.center, partRadius, part.min.y - height, part.max.y, offset, halfWidth)) return true;
            }
        return false;
    }

    /// <summary>
    /// Whether a surface at least rise above foot passes within radius (equipment horizontal radius)+half-width of foot in plan.
    /// Used to omit equipment under overhead paths regardless of their height.
    /// </summary>
    public static bool PassesAbove(Vector3 foot, float radius, float rise, List<Corridor> corridors)
    {
        if (corridors == null) return false;
        foreach (var corridor in corridors)
            if (Reaches(corridor, foot, radius, foot.y + rise, double.PositiveInfinity, 0f, corridor.HalfWidth)) return true;
        return false;
    }

    /// <summary>Whether any segment passes within radius+half-width of any points in plan, ignoring height. points must be densely sampled, like uncut centerlines.</summary>
    public static bool NearXZ(IReadOnlyList<Vector3> points, float radius, Corridor corridor)
    {
        if (points == null) return false;
        for (int i = 0; i < points.Count; i++)
            if (Reaches(corridor, points[i], radius, double.NegativeInfinity, double.PositiveInfinity, 0f, corridor.HalfWidth)) return true;
        return false;
    }

    /// <summary>Whether the portion of a right-offset centerline within [minY, maxY] passes within radius+halfWidth of center in plan.</summary>
    private static bool Reaches(Corridor corridor, Vector3 center, float radius, double minY, double maxY, float offset, float halfWidth)
    {
        if (corridor.Points == null || corridor.Points.Count < 2) return false;
        float reach = radius + halfWidth + Margin;
        if (corridor.IsOutsideXZ(center, reach + Mathf.Abs(offset))) return false;
        Vector3 lift = new Vector3(0f, corridor.YOffset, 0f);
        for (int i = 1; i < corridor.Points.Count; i++)
        {
            Vector3 a = corridor.Points[i - 1] + lift, b = corridor.Points[i] + lift;
            if (offset != 0f)
            {
                Vector3 shift = RightOf(a, b) * offset;
                a += shift;
                b += shift;
            }
            double lo = 0, hi = 1;
            double dy = b.y - a.y;
            if (Math.Abs(dy) < 1e-8)
            {
                if (a.y < minY || a.y > maxY) continue;
            }
            else
            {
                double t0 = (minY - a.y) / dy, t1 = (maxY - a.y) / dy;
                lo = Math.Max(lo, Math.Min(t0, t1));
                hi = Math.Min(hi, Math.Max(t0, t1));
                if (lo > hi) continue;
            }
            double dx = b.x - a.x, dz = b.z - a.z;
            double px = a.x - center.x, pz = a.z - center.z;
            double lengthSquared = dx * dx + dz * dz;
            double t = lengthSquared > 1e-8 ? -(px * dx + pz * dz) / lengthSquared : lo;
            t = Math.Max(lo, Math.Min(hi, t));
            double x = px + dx * t, z = pz + dz * t;
            if (x * x + z * z <= reach * reach) return true;
        }
        return false;
    }
}
