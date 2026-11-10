using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// Static store for coastline (land boundary polygons).
///
/// The backend <c>GET /api/v1/terrain/coastline</c> response (GeoJSON-like) is fetched by
/// <see cref="TerrainManager.FetchCoastline"/> and stored here.
/// Coordinates lie in the backend world X-Z plane (map center at the origin),
/// stored as vertex sequences for rings without distinguishing outer boundaries from holes.
///
/// Used by port (MODEL_PORT) quay mesh generation (<see cref="PortBuildingMeshBuilder"/>)
/// to determine which edges of the base polygon touch the coastline.
/// Coastlines are extracted from terrain when creating a city, then expanded with reclaimed land as reclamation completes.
/// FetchCoastline therefore refetches on every session:reload, replacing the rings only when the response differs
/// (<see cref="Version"/> advances only then).
/// </summary>
public static class CoastlineStore
{
    // Each ring is a closed-loop vertex sequence (the first and last vertices may or may not coincide).
    // Vector2 x=world X, y=world Z.
    private static readonly List<Vector2[]> rings = new List<Vector2[]>();

    // Rebuilt together with rings and replaced as a whole, so a reader always sees one consistent index.
    private static LandIndex landIndex;

    /// <summary>Whether coastline fetching has been attempted (regardless of success or empty results).</summary>
    public static bool IsLoaded { get; private set; }

    /// <summary>Whether at least one land boundary ring exists.</summary>
    public static bool HasCoastline => rings.Count > 0;

    /// <summary>
    /// Coastline data generation number, incremented by every SetFromGeoJson / Clear.
    /// Used to invalidate cached geometry derived from the coastline (such as land regions for quay clipping).
    /// </summary>
    public static int Version { get; private set; }

    /// <summary>
    /// Raw response text the stored rings were built from; null when none is applied.
    /// FetchCoastline skips a response identical to this, keeping the rings, the land index, and <see cref="Version"/>.
    /// </summary>
    public static string SourceJson { get; private set; }

    /// <summary>Read-only view of stored rings. Vector2 x=world X, y=world Z.</summary>
    public static IReadOnlyList<Vector2[]> Rings => rings;

    /// <summary>Discard stored data. Called on session switches, for example.</summary>
    public static void Clear()
    {
        rings.Clear();
        landIndex = null;
        IsLoaded = false;
        SourceJson = null;
        Version++;
    }

    /// <summary>
    /// Build rings from GeoJSON-like coordinates (Polygon or MultiPolygon).
    /// Polygon uses <c>[ring, hole, ...]</c> and MultiPolygon uses <c>[[ring, hole, ...], ...]</c>,
    /// but both are handled by recursively collecting arrays of points (<c>[x, z]</c>)
    /// as rings, independent of nesting depth.
    /// Empty coordinates (no coastline) count as successfully loaded with zero rings.
    /// sourceJson is the response text the coordinates came from, kept as <see cref="SourceJson"/>.
    /// </summary>
    public static void SetFromGeoJson(JToken coordinates, string sourceJson = null)
    {
        rings.Clear();
        if (coordinates != null)
        {
            CollectRings(coordinates, rings);
        }
        landIndex = new LandIndex(rings);
        IsLoaded = true;
        SourceJson = sourceJson;
        Version++;
    }

    private static void CollectRings(JToken token, List<Vector2[]> output)
    {
        if (!(token is JArray array) || array.Count == 0)
        {
            return;
        }

        // If the first element is a point (an array of at least two numbers), this array itself is a ring.
        if (IsPoint(array[0]))
        {
            Vector2[] ring = new Vector2[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                JToken p = array[i];
                ring[i] = new Vector2((float)p[0], (float)p[1]);
            }
            output.Add(ring);
            return;
        }

        // Otherwise descend one level.
        foreach (JToken child in array)
        {
            CollectRings(child, output);
        }
    }

    private static bool IsPoint(JToken token)
    {
        return token is JArray arr && arr.Count >= 2 &&
               (arr[0].Type == JTokenType.Float || arr[0].Type == JTokenType.Integer);
    }

    /// <summary>
    /// Check whether the specified world position (x, z; backend coordinates) lies within
    /// <paramref name="threshold"/> meters of a coastline boundary segment.
    /// Return early upon finding a segment within the threshold.
    /// Return false if coastline data is unloaded or there is no land.
    /// </summary>
    public static bool IsNearCoastline(float x, float z, float threshold)
    {
        if (rings.Count == 0)
        {
            return false;
        }

        float thresholdSq = threshold * threshold;
        for (int r = 0; r < rings.Count; r++)
        {
            Vector2[] ring = rings[r];
            int count = ring.Length;
            if (count < 2)
            {
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % count];
                if (PolylineUtils.DistanceToSegmentSq(new Vector2(x, z), a, b) <= thresholdSq)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Check whether the specified world position (x, z; backend coordinates) lies inside land.
    ///
    /// Apply the even-odd crossing rule across every ring, without distinguishing outer boundaries and holes.
    /// The backend coastline stores outer rings together with hole rings such as lakes;
    /// counting crossings across all rings naturally handles holes: inside only the outer ring is odd (land),
    /// while inside both outer and hole rings is even (sea/lake).
    /// Only edges in the query's Z band are tested, so the cost does not grow with the total coastline length.
    /// Return false if coastline data is unloaded or there is no land (caller provides the fallback).
    /// </summary>
    public static bool IsLand(float x, float z)
    {
        LandIndex index = landIndex;
        return index != null && index.Contains(x, z);
    }

    /// <summary>
    /// Edges of every ring with at least three vertices, bucketed by horizontal Z band.
    /// Ring parities combine by XOR, so the total crossing count over all edges gives the same answer as testing each ring.
    /// Edges keep the orientation of <see cref="PolygonUtils.Contains(IReadOnlyList{Vector2}, Vector2)"/> and use its
    /// <see cref="PolygonUtils.CrossesRay"/>, so results agree with the per-ring test.
    /// </summary>
    private sealed class LandIndex
    {
        private const float BandHeight = 10f; // m. A band holds only the edges near the shoreline crossings of its strip.
        private readonly float originZ;
        private readonly int bandCount;
        private readonly int[] bandStart;
        private readonly float[] edgeXi, edgeZi, edgeXj, edgeZj;

        public LandIndex(List<Vector2[]> rings)
        {
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            foreach (Vector2[] ring in rings)
            {
                if (ring.Length < 3) continue;
                foreach (Vector2 p in ring)
                {
                    if (p.y < minZ) minZ = p.y;
                    if (p.y > maxZ) maxZ = p.y;
                }
            }
            if (!(minZ <= maxZ))
            {
                bandStart = new int[1];
                edgeXi = edgeZi = edgeXj = edgeZj = new float[0];
                return;
            }
            originZ = minZ;
            bandCount = Band(maxZ) + 1;

            // Count entries per band, convert to offsets, then fill. Horizontal edges never cross the ray and are skipped.
            bandStart = new int[bandCount + 1];
            foreach (Vector2[] ring in rings)
            {
                if (ring.Length < 3) continue;
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    if (ring[i].y == ring[j].y) continue;
                    int last = Band(Mathf.Max(ring[i].y, ring[j].y));
                    for (int band = Band(Mathf.Min(ring[i].y, ring[j].y)); band <= last; band++) bandStart[band + 1]++;
                }
            }
            for (int band = 0; band < bandCount; band++) bandStart[band + 1] += bandStart[band];

            int total = bandStart[bandCount];
            edgeXi = new float[total];
            edgeZi = new float[total];
            edgeXj = new float[total];
            edgeZj = new float[total];
            var next = (int[])bandStart.Clone();
            foreach (Vector2[] ring in rings)
            {
                if (ring.Length < 3) continue;
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    if (ring[i].y == ring[j].y) continue;
                    int last = Band(Mathf.Max(ring[i].y, ring[j].y));
                    for (int band = Band(Mathf.Min(ring[i].y, ring[j].y)); band <= last; band++)
                    {
                        int slot = next[band]++;
                        edgeXi[slot] = ring[i].x;
                        edgeZi[slot] = ring[i].y;
                        edgeXj[slot] = ring[j].x;
                        edgeZj[slot] = ring[j].y;
                    }
                }
            }
        }

        // The same function places edges and queries, so a z inside an edge's Z range always lands in one of its bands.
        private int Band(float z) => (int)System.Math.Floor(((double)z - originZ) / BandHeight);

        public bool Contains(float x, float z)
        {
            if (!(z >= originZ)) return false;
            int band = Band(z);
            if (band >= bandCount) return false;
            bool inside = false;
            for (int k = bandStart[band], end = bandStart[band + 1]; k < end; k++)
            {
                if (PolygonUtils.CrossesRay(edgeXi[k], edgeZi[k], edgeXj[k], edgeZj[k], x, z)) inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>
    /// Return signed distance from the specified world position (x, z; backend coordinates) to the coastline.
    /// Positive inside land, negative in the sea (outside land). Magnitude is the shortest distance to any edge of any ring.
    /// Each point scans all edges; use <see cref="SampleSignedDistanceGrid"/> when sampling an entire grid.
    /// Return negative infinity if coastline data is unloaded or there is no land (treat the entire area as sea).
    /// </summary>
    public static float SignedDistance(float x, float z)
    {
        if (rings.Count == 0)
        {
            return float.NegativeInfinity;
        }

        float minDistSq = float.PositiveInfinity;
        for (int r = 0; r < rings.Count; r++)
        {
            Vector2[] ring = rings[r];
            int count = ring.Length;
            if (count < 2)
            {
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % count];
                float dSq = PolylineUtils.DistanceToSegmentSq(new Vector2(x, z), a, b);
                if (dSq < minDistSq)
                {
                    minDistSq = dSq;
                }
            }
        }

        float dist = Mathf.Sqrt(minDistSq);
        return IsLand(x, z) ? dist : -dist;
    }

    /// <summary>
    /// Return signed distances at grid points (xs[i], zs[j]) in [i, j]. Pass xs and zs in ascending order.
    /// Signs match <see cref="IsLand"/> and magnitudes match <see cref="SignedDistance"/>, except points farther than maxDistance
    /// are clamped to ±maxDistance. Calling SignedDistance per grid point scales with grid points times coastline vertices,
    /// so determine inside/outside using ray intersections per row, and distances using only nearby edges.
    /// </summary>
    public static float[,] SampleSignedDistanceGrid(float[] xs, float[] zs, float maxDistance)
    {
        var result = new float[xs.Length, zs.Length];
        // Inside/outside and distance require two scans; retain the same ring set even if it is replaced midway.
        Vector2[][] snapshot = rings.ToArray();

        var crossings = new List<float>();
        for (int j = 0; j < zs.Length; j++)
        {
            crossings.Clear();
            foreach (Vector2[] ring in snapshot)
            {
                if (ring.Length >= 3) PolygonUtils.AppendRayCrossings(ring, zs[j], crossings);
            }
            crossings.Sort();
            int passed = 0;
            for (int i = 0; i < xs.Length; i++)
            {
                while (passed < crossings.Count && crossings[passed] <= xs[i]) passed++;
                result[i, j] = ((crossings.Count - passed) & 1) == 1 ? maxDistance : -maxDistance;
            }
        }

        // Register edges at sample spacing no greater than maxDistance. Every edge point is within maxDistance/2 of a registered point,
        // so candidates within radius reach include every edge within maxDistance.
        float reach = maxDistance * 1.5f;
        var segments = new List<(Vector2 start, Vector2 end)>();
        var hash = new SpatialHash<int>(reach);
        foreach (Vector2[] ring in snapshot)
        {
            int count = ring.Length;
            if (count < 2) continue;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % count];
                int samples = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / maxDistance));
                for (int k = 0; k < samples; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, (k + 0.5f) / samples);
                    hash.Add(segments.Count, new Vector3(p.x, 0f, p.y));
                }
                segments.Add((a, b));
            }
        }

        float maxDistanceSq = maxDistance * maxDistance;
        for (int i = 0; i < xs.Length; i++)
        {
            for (int j = 0; j < zs.Length; j++)
            {
                var point = new Vector2(xs[i], zs[j]);
                float minDistanceSq = maxDistanceSq;
                foreach (int index in hash.Query(new Vector3(point.x, 0f, point.y), reach))
                {
                    float distanceSq = PolylineUtils.DistanceToSegmentSq(point, segments[index].start, segments[index].end);
                    if (distanceSq < minDistanceSq) minDistanceSq = distanceSq;
                }
                float distance = Mathf.Sqrt(minDistanceSq);
                result[i, j] = result[i, j] > 0f ? distance : -distance;
            }
        }
        return result;
    }

    /// <summary>
    /// Return the world-coordinate (backend) AABB of all ring vertices.
    /// Used to restrict the land mesh generation grid scan to areas containing land.
    /// False if there are no rings.
    /// </summary>
    public static bool TryGetBounds(out float minX, out float minZ, out float maxX, out float maxZ)
    {
        minX = float.PositiveInfinity;
        minZ = float.PositiveInfinity;
        maxX = float.NegativeInfinity;
        maxZ = float.NegativeInfinity;
        bool any = false;
        for (int r = 0; r < rings.Count; r++)
        {
            Vector2[] ring = rings[r];
            for (int i = 0; i < ring.Length; i++)
            {
                Vector2 p = ring[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
                any = true;
            }
        }
        return any;
    }
}
