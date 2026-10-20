using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Section-classification utilities for underground paths (tunnels).
///
/// Path Y coordinates are relative to the ground; negative values indicate underground placement.
/// For rendering, compare depth at each sample (current terrain elevation minus road world Y)
/// with the master-data clearance height (height) to classify sections.
///
/// - Depth ≤ height: open-cut section. The backend excavates terrain to road elevation
///   when construction starts, so render the mesh normally.
/// - Depth > height: fully buried tunnel section. Terrain is not deformed,
///   so skip mesh generation in normal view and use the same generator in underground view.
///   Place portal parts at the boundaries.
///
/// Classification depends only on the depth function at each sample, allowing repeated
/// open-cut/tunnel transitions within one path, with portals at every threshold crossing.
/// The backend's matching classification is in genesis-core geo_path_depth.py;
/// excavated open-cut sections having depth approximately 0 keeps both classifications consistent.
/// </summary>
public static class TunnelSectionUtil
{
    public readonly struct DrawSection
    {
        public readonly List<Vector3> Points;
        public readonly bool IsUnderground;
        public DrawSection(List<Vector3> points, bool underground) { Points = points; IsUnderground = underground; }
    }

    /// <summary>
    /// Visible sections shared by normal, construction, and distant rendering. Underground view also passes buried sections to the same generator.
    /// points is the exact rendered centerline (PathManager.CalculateSectionCenterline).
    /// </summary>
    public static List<DrawSection> GetDrawSections(List<Vector3> points, float clearance,
        bool includeUnderground, List<PortalInfo> portals = null)
    {
        var sections = new List<DrawSection>();
        foreach (var run in SplitDrawableRuns(points, clearance, portals))
            sections.Add(new DrawSection(run, false));
        if (includeUnderground)
            foreach (var run in SplitRuns(points, clearance, keepTunnel: true, portals: null, pathFollowsTerrain: false))
                sections.Add(new DrawSection(run, true));
        return sections;
    }

    public static bool IsTunnelPoint(Vector3 point, float clearance) => GetDepth(point) > clearance;

    /// <summary>A path is underground if any anchor's relative Y is below this negative-side threshold.</summary>
    public const float UNDERGROUND_REL_Y_EPS = 0.01f;

    /// <summary>
    /// Allowed Y difference for considering a point attached to the ground (m).
    /// Terrain-following points sit at terrain +0.1m, so allow enough margin to classify them as ground-level.
    /// </summary>
    public const float GROUND_SNAP_EPSILON = 0.5f;

    /// <summary>Depth-test subsampling interval (m), matching CalculatePathPoints sampling.</summary>
    public const float SAMPLE_INTERVAL = 5f;

    /// <summary>Tunnel-portal placement data.</summary>
    public struct PortalInfo
    {
        /// <summary>Portal position on the road centerline where depth crosses clearance.</summary>
        public Vector3 position;

        /// <summary>Normalized horizontal direction toward the tunnel interior.</summary>
        public Vector3 direction;
    }

    /// <summary>
    /// Return whether a path may contain underground sections, meaning any anchor has negative relative Y.
    /// Same definition as backend is_underground_path.
    /// </summary>
    public static bool IsUndergroundPath(GeneratedModels.PathSummary path)
    {
        if (path == null) return false;

        var startList = path.GetStartPointList();
        if (startList != null && startList.Count >= 3 && startList[1] < -UNDERGROUND_REL_Y_EPS)
            return true;

        var endList = path.GetEndPointList();
        if (endList != null && endList.Count >= 3 && endList[1] < -UNDERGROUND_REL_Y_EPS)
            return true;

        var waypoints = path.GetWaypointList();
        if (waypoints != null)
        {
            foreach (var wp in waypoints)
            {
                if (wp.position != null && wp.position.Length >= 3 && wp.position[1] < -UNDERGROUND_REL_Y_EPS)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Return the variant's clearance height (m), used directly without a buffer as the open-cut/tunnel threshold.
    /// Return 0 before master data is available, treating everything below the surface as tunnel.
    /// </summary>
    public static float GetClearanceHeight(int pathVariant)
    {
        var store = MasterDataStore.Instance;
        if (store == null || !store.IsInitialized) return 0f;

        var master = store.GetPathMaster(pathVariant);
        if (master != null && master.height.HasValue) return master.height.Value;
        return 0f;
    }

    /// <summary>
    /// Split an underground path polyline into continuous visible sections: open cut, surface, or elevated.
    /// Exclude tunnel points with depth > clearance and insert interpolated boundary points
    /// at exactly clearance depth. Store portal data in portals.
    ///
    /// Classify only by point depth. Terrain following depends on path settings and anchor relative Y;
    /// the centerline builder already places following spans above ground, so do not infer it here.
    /// </summary>
    /// <param name="points">The exact rendered centerline (PathManager.CalculateSectionCenterline).</param>
    /// <param name="clearance">Clearance height (m).</param>
    /// <param name="portals">Output for portal positions and directions; may be null.</param>
    /// <returns>Point lists for visible sections; empty when fully buried.</returns>
    public static List<List<Vector3>> SplitDrawableRuns(
        List<Vector3> points, float clearance, List<PortalInfo> portals)
    {
        return SplitRuns(points, clearance, keepTunnel: false, portals, pathFollowsTerrain: false);
    }

    /// <summary>
    /// Cheaply test whether a polyline contains any tunnel section with depth &gt; clearance.
    /// Detects non-following paths entering hills even when relative Y=0.
    /// With pathFollowsTerrain=false, disable the IsOnGround shortcut
    /// and always evaluate depth, avoiding misclassifying near-surface non-following decks as terrain-following.
    /// A coarser sampleInterval provides a lightweight check for the Far tier.
    /// </summary>
    public static bool HasTunnelSection(
        List<Vector3> points, float clearance, bool pathFollowsTerrain,
        float sampleInterval = SAMPLE_INTERVAL)
    {
        if (points == null || points.Count < 2) return false;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];
            bool followsTerrain = pathFollowsTerrain && IsOnGround(a) && IsOnGround(b);
            if (followsTerrain) continue;

            float horizontal = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
            int subdivisions = Mathf.Max(1, Mathf.CeilToInt(horizontal / sampleInterval));
            for (int s = 0; s < subdivisions; s++)
            {
                Vector3 p = Vector3.Lerp(a, b, (float)s / subdivisions);
                if (GetDepth(p) > clearance) return true;
            }
        }
        Vector3 last = points[points.Count - 1];
        bool lastOnGround = pathFollowsTerrain && IsOnGround(last);
        return !lastOnGround && GetDepth(last) > clearance;
    }

    /// <summary>
    /// Extract only continuous tunnel sections with depth > clearance, including interpolated boundary points at each end.
    /// Used for polylines without span metadata, such as drawn-spline samples, edited points, or snapping segments.
    /// When pathFollowsTerrain is true, classify sections with both endpoints within GROUND_SNAP_EPSILON of the surface as ground-level.
    /// </summary>
    public static List<List<Vector3>> CollectTunnelRuns(
        List<Vector3> points, float clearance, bool pathFollowsTerrain = true)
    {
        return SplitRuns(points, clearance, keepTunnel: true, portals: null, pathFollowsTerrain);
    }

    /// <summary>
    /// Subsample the polyline for tunnel classification and return continuous visible, non-tunnel sections
    /// when keepTunnel=false, or tunnel sections when true.
    /// Insert interpolated points at section boundaries where depth equals clearance;
    /// also store portal data if portals is provided.
    /// </summary>
    private static List<List<Vector3>> SplitRuns(
        List<Vector3> points, float clearance, bool keepTunnel, List<PortalInfo> portals,
        bool pathFollowsTerrain = true)
    {
        var runs = new List<List<Vector3>>();
        if (points == null || points.Count < 2) return runs;

        // Subsample the polyline and classify each point as inside or outside a tunnel.
        var samples = new List<Vector3>();
        var isTunnel = new List<bool>();

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];

            // For pathFollowsTerrain=false, disable the ground-attached shortcut
            // so near-surface decks are not mistaken for terrain-following paths, missing hills that rise above them.
            bool followsTerrain = pathFollowsTerrain && IsOnGround(a) && IsOnGround(b);

            float horizontal = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
            int subdivisions = Mathf.Max(1, Mathf.CeilToInt(horizontal / SAMPLE_INTERVAL));

            for (int s = 0; s < subdivisions; s++)
            {
                float t = (float)s / subdivisions;
                Vector3 p = Vector3.Lerp(a, b, t);
                samples.Add(p);
                isTunnel.Add(!followsTerrain && GetDepth(p) > clearance);
            }
        }
        // Final point.
        Vector3 last = points[points.Count - 1];
        samples.Add(last);
        bool lastOnGround = pathFollowsTerrain && IsOnGround(last);
        isTunnel.Add(!lastOnGround && GetDepth(last) > clearance);

        // Extract continuous sections of the target class: tunnel or visible, depending on keepTunnel.
        List<Vector3> current = null;
        for (int i = 0; i < samples.Count; i++)
        {
            bool isTarget = isTunnel[i] == keepTunnel;
            if (isTarget)
            {
                if (current == null)
                {
                    current = new List<Vector3>();
                    // Class transition: add the interpolated boundary point (portal) at the section start.
                    if (i > 0)
                    {
                        Vector3 tunnelSide = keepTunnel ? samples[i] : samples[i - 1];
                        Vector3 openSide = keepTunnel ? samples[i - 1] : samples[i];
                        Vector3 boundary = InterpolateBoundary(tunnelSide, openSide, clearance);
                        current.Add(boundary);
                        if (!keepTunnel)
                        {
                            AddPortal(portals, boundary, samples[i - 1] - samples[i]);
                        }
                    }
                }
                current.Add(samples[i]);
            }
            else
            {
                if (current != null)
                {
                    // Class transition: add the interpolated boundary point (portal) at the section end.
                    Vector3 tunnelSide = keepTunnel ? samples[i - 1] : samples[i];
                    Vector3 openSide = keepTunnel ? samples[i] : samples[i - 1];
                    Vector3 boundary = InterpolateBoundary(tunnelSide, openSide, clearance);
                    current.Add(boundary);
                    if (!keepTunnel)
                    {
                        AddPortal(portals, boundary, samples[i] - samples[i - 1]);
                    }

                    if (current.Count >= 2) runs.Add(current);
                    current = null;
                }
            }
        }
        if (current != null && current.Count >= 2) runs.Add(current);

        return runs;
    }

    /// <summary>Check whether a point is attached to the surface, as a terrain-following point.</summary>
    private static bool IsOnGround(Vector3 p)
    {
        float ground = Utils.GetGroundHeight(new Vector3(p.x, 1000f, p.z));
        return Mathf.Abs(ground - p.y) <= GROUND_SNAP_EPSILON;
    }

    /// <summary>Return point depth: terrain elevation minus road Y, positive underground.</summary>
    private static float GetDepth(Vector3 p)
    {
        float ground = Utils.GetGroundHeight(new Vector3(p.x, 1000f, p.z));
        return ground - p.y;
    }

    /// <summary>
    /// Linearly interpolate between tunnelSide and openSide
    /// to find the boundary point at exactly clearance depth.
    /// </summary>
    private static Vector3 InterpolateBoundary(Vector3 tunnelSide, Vector3 openSide, float clearance)
    {
        float depthTunnel = GetDepth(tunnelSide);
        float depthOpen = GetDepth(openSide);

        float denom = depthTunnel - depthOpen;
        if (Mathf.Abs(denom) < 1e-4f) return openSide;

        // Position from openSide toward tunnelSide where depth equals clearance.
        float t = (clearance - depthOpen) / denom;
        t = Mathf.Clamp01(t);
        return Vector3.Lerp(openSide, tunnelSide, t);
    }

    private static void AddPortal(List<PortalInfo> portals, Vector3 position, Vector3 towardTunnel)
    {
        if (portals == null) return;

        Vector3 dir = new Vector3(towardTunnel.x, 0f, towardTunnel.z);
        if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
        portals.Add(new PortalInfo
        {
            position = position,
            direction = dir.normalized,
        });
    }
}
