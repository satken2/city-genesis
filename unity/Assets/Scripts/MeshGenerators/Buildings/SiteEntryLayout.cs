using UnityEngine;

/// <summary>
/// Pure data for a site's entry: where the approach from the building front crosses the parcel perimeter.
/// The approach runs along the outward normal from the midpoint of the building's front edge (vertex 0→1), where the entrance part sits,
/// so fence openings, gates, trails, and the vacant area kept clear of scatter parts all follow one line.
/// All positions use local XZ relative to center_point. Specification: city-genesis/docs/ja/geometries/F_建物メッシュの生成.md.
/// </summary>
public static class SiteEntryLayout
{
    /// <summary>Opening width (m) when the building has no gate part, wide enough for one car.</summary>
    public const float OPENING_WIDTH = 3f;

    /// <summary>Distance (m) the ray starts behind the front edge, so the exit from the occupied outline is found even when the edge lies on it.</summary>
    private const float RAY_BACKOFF = 0.05f;

    public struct Entry
    {
        /// <summary>Opening center on the parcel perimeter.</summary>
        public Vector2 Gate;
        /// <summary>Parcel edge (CCW) carrying the opening. Gates face along it like fence walls.</summary>
        public Vector2 EdgeStart, EdgeEnd;
        /// <summary>Where the approach leaves the occupied outline (the entrance's front face when the body is set back for it).</summary>
        public Vector2 Front;
        /// <summary>Opening width (m).</summary>
        public float Width;

        /// <summary>Corridor from the building front to the opening, kept clear of scatter parts (CCW).</summary>
        public Vector2[] Corridor()
        {
            Vector2 along = (Gate - Front).normalized;
            Vector2 side = new Vector2(along.y, -along.x) * (Width * 0.5f);
            return new[] { Front + side, Gate + side, Gate - side, Front - side };
        }
    }

    /// <summary>
    /// Resolve the entry. site=parcel, front=body outline whose edge 0→1 is the front, occupied=outline including entrance/stair setbacks
    /// (all CCW). Return false when no outline or the ray never reaches the parcel perimeter.
    /// </summary>
    public static bool TryResolve(Vector2[] site, Vector2[] front, Vector2[] occupied, float width, out Entry entry)
    {
        entry = default;
        if (site == null || site.Length < 3 || front == null || front.Length < 3)
        {
            return false;
        }

        Vector2 edge = front[1] - front[0];
        if (edge.sqrMagnitude < 1e-6f)
        {
            return false;
        }
        Vector2 direction = new Vector2(edge.y, -edge.x).normalized;
        Vector2 origin = (front[0] + front[1]) * 0.5f - direction * RAY_BACKOFF;

        float exit = FirstHit(occupied ?? front, origin, direction, 0f, out _);
        if (float.IsInfinity(exit))
        {
            exit = RAY_BACKOFF;
        }
        float reach = FirstHit(site, origin, direction, exit, out int siteEdge);
        if (float.IsInfinity(reach))
        {
            return false;
        }

        entry = new Entry
        {
            Gate = origin + direction * reach,
            EdgeStart = site[siteEdge],
            EdgeEnd = site[(siteEdge + 1) % site.Length],
            Front = origin + direction * exit,
            Width = width,
        };
        return true;
    }

    /// <summary>Nearest ray distance at or beyond minDistance where the ray crosses ring, and that edge's index. Infinity if none.</summary>
    private static float FirstHit(Vector2[] ring, Vector2 origin, Vector2 direction, float minDistance, out int edgeIndex)
    {
        float nearest = float.PositiveInfinity;
        edgeIndex = -1;
        for (int i = 0; i < ring.Length; i++)
        {
            if (PolylineUtils.IntersectRaySegment(origin, direction, ring[i], ring[(i + 1) % ring.Length], out float distance, out _)
                && distance >= minDistance && distance < nearest)
            {
                nearest = distance;
                edgeIndex = i;
            }
        }
        return nearest;
    }
}
