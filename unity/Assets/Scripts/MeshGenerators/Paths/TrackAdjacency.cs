using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// One neighboring candidate for nearby-track joined rendering (TrackPath).
/// Supplied by PathManager through TrackAdjacency.NeighborProvider.
/// </summary>
public class TrackNeighborInfo
{
    /// <summary>Neighboring candidate path ID.</summary>
    public int PathId;

    /// <summary>Rendering points (world coordinates, terrain-conformed, sampled about every 5m).</summary>
    public List<Vector3> Points;

    /// <summary>Master full width (track-bed width, m).</summary>
    public float Width;

    /// <summary>
    /// Left/right platform extension widths (m), resolved by TrackPath.ResolvePlatformExtents in this track's
    /// geometric frame (point-list start→end).
    /// Both zero for nonstations. Used to check whether neighboring platforms face this track for island joins.
    /// </summary>
    public float PlatformLeft;

    public float PlatformRight;

    /// <summary>
    /// Centerline-to-platform-edge (deck track-side edge) distance (TrackPath.PlatformEdgeDistance).
    /// Used to extend island decks to neighboring edges. Two-track stations measure from outer tracks, farther than single track.
    /// </summary>
    public float PlatformEdge = TrackPath.PLATFORM_EDGE_OFFSET;

    /// <summary>
    /// LRT-stop platform intervals (3D cumulative distance from Points start; LrtPlatformParts.ZoneRanges). Null if absent.
    /// Main corridor track places poles beyond neighbor platform edge (LrtPlatformOuter) in these intervals.
    /// </summary>
    public List<Vector2> LrtPlatformZones;

    /// <summary>Centerline-to-LRT-stop platform outer-edge distance (LrtRoadParts.PlatformOuterOffset, both sides).</summary>
    public float LrtPlatformOuter;

    /// <summary>
    /// Per-point bed outer-edge shifts (same indices as Points; positive outward, symmetric), representing S-curve joins (TrackEndTransition)
    /// where tracks/platforms/beds move outward. Null means zero throughout.
    /// </summary>
    public float[] OuterShifts;

    /// <summary>
    /// Whether this is provisional (planned/deferred/construction/preview). Completed tracks do not yield their parts
    /// (island decks/poles) to provisional tracks; provisional tracks draw infill (bed gaps/remaining island decks) in their own appearance.
    /// Completed/provisional tracks do not share poles.
    /// </summary>
    public bool Provisional;
}

/// <summary>
/// Joining-corridor information along this track's rendering points.
///
/// Double track can be one two-track path or two parallel one-way single tracks.
/// The latter otherwise appears as independent single tracks;
/// treat tracks running close and parallel in XZ/Y (including combined double track) as one corridor,
/// joining body/walls/poles/beams for rendering.
///
/// All indices match this track's points (spline control points).
/// Sides follow spline progression (right = horizontal Cross(up, forward)).
/// Oppositely laid single tracks (up/down lines) still join because tests use
/// absolute tangent parallelism.
/// </summary>
public class TrackCorridorInfo
{
    /// <summary>Spline-percent interval (0–1).</summary>
    public struct PercentRange
    {
        public double From;
        public double To;
    }

    /// <summary>Own rendering points (same as spline control points).</summary>
    public List<Vector3> Points;

    /// <summary>Horizontal right unit vectors per point.</summary>
    public Vector3[] RightDir;

    /// <summary>Cumulative arc length (m). Spline SampleMode.Uniform makes percent approximately arc-length fraction.</summary>
    public float[] CumulativeLength;

    public float TotalLength;

    /// <summary>Own half-width (half master width).</summary>
    public float OwnHalfWidth;

    /// <summary>Own per-point bed outer shifts (S curves, positive outward, symmetric). Null means zero throughout.</summary>
    public float[] OwnOuterShift;

    /// <summary>Own half-width at point i (including S-curve shift).</summary>
    public float OwnHalfAt(int i) => OwnHalfWidth + (OwnOuterShift != null ? OwnOuterShift[i] : 0f);

    /// <summary>Whether an immediately adjacent corridor track exists on the left.</summary>
    public bool[] LeftCovered;

    public bool[] RightCovered;

    /// <summary>
    /// Omit poles/beams here because this track is not the corridor's main track (minimum path_id);
    /// main track places them across full corridor width.
    /// </summary>
    public bool[] SuppressPoleSet;

    /// <summary>
    /// Points where another bed physically overlaps the left wall position, forbidding walls.
    /// Independent of parallelism (angle/gap), ignoring angle; prevents walls across converging/crossing tracks
    /// at merge throats or scissors-like at-grade crossings.
    /// </summary>
    public bool[] LeftWallBlocked;

    public bool[] RightWallBlocked;

    /// <summary>Distance to corridor left edge (positive from own centerline; OwnHalfWidth on isolated sections).</summary>
    public float[] LeftExtent;

    public float[] RightExtent;

    /// <summary>
    /// Positive distance to left neighbor's near bed edge.
    /// Lower path_id fills bed gaps (avoids duplicates);
    /// NaN when not responsible or no neighbor.
    /// </summary>
    public float[] LeftFillTo;

    public float[] RightFillTo;

    /// <summary>
    /// Positive centerline distance to left platform-deck outer edge.
    /// NaN means omit deck here (no platform on that side, or
    /// neighbor fills the island-join deck).
    /// Normally own half-width + platform_width. Island-fill owners extend to the neighbor's
    /// edge line (TrackNeighborInfo.PlatformEdge from its centerline); where other beds encroach,
    /// clamp before them.
    /// Track-side edge remains own edge line (TrackPath.PlatformEdgeDistance,
    /// held by TrackPath); this array describes only outer edges.
    /// </summary>
    public float[] LeftDeckFar;

    public float[] RightDeckFar;

    /// <summary>
    /// Points where left deck outer edge joins a neighbor's platform edge.
    /// Omit outer vertical skirt there (neighbor edge parts provide the face).
    /// </summary>
    public bool[] LeftDeckJoined;

    public bool[] RightDeckJoined;

    /// <summary>
    /// Points omitting left platform_wall (outer walls/fences/benches):
    /// island joins or other beds overlapping platform outer edges.
    /// </summary>
    public bool[] LeftPlatformWallOmit;

    public bool[] RightPlatformWallOmit;

    /// <summary>
    /// Points where another bed overlaps the left LRT-platform band (own bed edge→platform outer edge+margin).
    /// Stop positions fix platforms independently of track layout; omit that side here
    /// (avoid decks/ramps over beds at branches or parallel tracks). Always false for non-LRT tracks.
    /// </summary>
    public bool[] LeftLrtPlatformBlocked;

    public bool[] RightLrtPlatformBlocked;

    /// <summary>Whether any joined interval exists. Corridors with false are not generated.</summary>
    public bool AnyCoverage;

    /// <summary>Return nearest point index to cumulative arc length length (m), using binary search.</summary>
    public int SampleIndexAtLength(float length)
    {
        float[] cum = CumulativeLength;
        int hi = cum.Length - 1;
        if (length <= 0f || hi <= 0) return 0;
        if (length >= TotalLength) return hi;

        int lo = 0;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (cum[mid] < length) lo = mid + 1;
            else hi = mid;
        }
        // lo is the first point at/above length. Return the nearer neighboring point.
        if (lo > 0 && length - cum[lo - 1] < cum[lo] - length) return lo - 1;
        return lo;
    }

    /// <summary>
    /// Return spline-percent intervals allowing walls on the specified side (no neighbor and no bed
    /// overlapping wall positions). Boundaries lie midway between samples.
    /// Omit runs shorter than minRunLength (m) to avoid slivers.
    /// </summary>
    public List<PercentRange> GetWallPercentIntervals(bool leftSide, float minRunLength)
    {
        bool[] covered = leftSide ? LeftCovered : RightCovered;
        bool[] blocked = leftSide ? LeftWallBlocked : RightWallBlocked;
        return GetClearPercentIntervals(i => covered[i] || blocked[i], minRunLength);
    }

    /// <summary>
    /// Return spline-percent intervals permitting platform_wall (outer edges),
    /// excluding island joins/other-bed outer-edge encroachments.
    /// </summary>
    public List<PercentRange> GetPlatformWallPercentIntervals(bool leftSide, float minRunLength)
    {
        bool[] omit = leftSide ? LeftPlatformWallOmit : RightPlatformWallOmit;
        return GetClearPercentIntervals(i => omit[i], minRunLength);
    }

    /// <summary>
    /// Return LRT-platform-blocked intervals on the given side in meters from spline start (ascending x).
    /// Boundaries lie midway between samples; blocked endpoints extend to infinity
    /// so ramps beyond cut ends also avoid other tracks.
    /// </summary>
    public List<Vector2> GetLrtPlatformBlockedRanges(bool leftSide)
    {
        bool[] blocked = leftSide ? LeftLrtPlatformBlocked : RightLrtPlatformBlocked;
        var result = new List<Vector2>();
        int n = Points.Count;
        int i = 0;
        while (i < n)
        {
            if (!blocked[i]) { i++; continue; }
            int s = i;
            while (i < n && blocked[i]) i++;
            int e = i - 1;
            float from = s == 0 ? float.NegativeInfinity : (CumulativeLength[s - 1] + CumulativeLength[s]) * 0.5f;
            float to = e == n - 1 ? float.PositiveInfinity : (CumulativeLength[e] + CumulativeLength[e + 1]) * 0.5f;
            result.Add(new Vector2(from, to));
        }
        return result;
    }

    private List<PercentRange> GetClearPercentIntervals(System.Func<int, bool> isOmitted, float minRunLength)
        => ClearPercentIntervals(CumulativeLength, TotalLength, isOmitted, minRunLength);

    /// <summary>
    /// Shared return of spline-percent runs where isOmitted=false (also used by road joins, RoadCorridorInfo).
    /// Boundaries lie midway between samples. Omit fragments shorter than minRunLength (m)
    /// to prevent slivers.
    /// </summary>
    public static List<PercentRange> ClearPercentIntervals(float[] cumulativeLength, float totalLength, System.Func<int, bool> isOmitted, float minRunLength)
    {
        var result = new List<PercentRange>();
        int n = cumulativeLength.Length;
        if (totalLength <= 0f)
        {
            result.Add(new PercentRange { From = 0.0, To = 1.0 });
            return result;
        }

        int i = 0;
        while (i < n)
        {
            if (isOmitted(i)) { i++; continue; }
            int s = i;
            while (i < n && !isOmitted(i)) i++;
            int e = i - 1;

            float fromLen = (s == 0) ? 0f : (cumulativeLength[s - 1] + cumulativeLength[s]) * 0.5f;
            float toLen = (e == n - 1) ? totalLength : (cumulativeLength[e] + cumulativeLength[e + 1]) * 0.5f;
            if (toLen - fromLen < minRunLength) continue;

            result.Add(new PercentRange { From = fromLen / totalLength, To = toLen / totalLength });
        }
        return result;
    }
}

/// <summary>
/// Adjacency analysis for nearby parallel tracks, using LocalConstants TRACK_MERGE_* thresholds.
/// All tests affect Unity rendering only, never backend data/pathfinding.
/// </summary>
public static class TrackAdjacency
{
    /// <summary>
    /// Whether track is provisional (planned/scheduled/deferred/construction; preview temporary paths use status 0=planned).
    /// Completed tracks do not yield parts or share poles with provisional tracks (TrackNeighborInfo.Provisional).
    /// </summary>
    public static bool IsProvisional(PathSummary path) => path != null
        && (path.status == Constants.STATUS_TEMPORARY || path.status == Constants.STATUS_SCHEDULED_CONSTRUCTION
            || path.status == Constants.STATUS_PENDING || path.status == Constants.STATUS_CONSTRUCTING);

    /// <summary>
    /// Neighbor lookup delegate, set by PathManager.Awake (requires cached path data
    /// and cell spatial index). Null disables joined rendering
    /// (e.g. mesh test scenes).
    /// </summary>
    public static System.Func<PathSummary, List<TrackNeighborInfo>> NeighborProvider;

    /// <summary>Maximum distance from own centerline scanned for corridor connections (m). Ignore farther candidates.</summary>
    private const float MAX_SCAN_HALF_WIDTH = 40f;

    /// <summary>
    /// Allowed nearest-point forward component (m), preventing false parallelism
    /// beyond neighboring endpoints where projection clamps.
    /// </summary>
    private const float LONGITUDINAL_TOLERANCE = 2.5f;

    /// <summary>
    /// Extra margin outside wall zones (m) for physical bed-over-wall blocking,
    /// avoiding surfaces merely touching walls visually.
    /// </summary>
    private const float WALL_BLOCK_CLEARANCE = 0.3f;

    /// <summary>
    /// Margin (m) for bed/platform encroachment without island joins.
    /// Omit platform_wall and clamp decks before beds approaching this closely.
    /// </summary>
    private const float PLATFORM_ENCROACH_CLEARANCE = 0.3f;

    // Allow only errors permitting one shared top height; distinct from bed-join height tolerance.
    private const float PLATFORM_JOIN_MAX_DY = 0.02f;

    /// <summary>
    /// One corridor member (self/neighbor). O is signed lateral position from own centerline.
    /// Reach is outer-edge half-width, normally bed half-width. In LRT-platform sections, expand it so poles
    /// at outer edge+POLE_MARGIN lie at platform outer edge+PLATFORM_POLE_CLEARANCE (joining still uses Half).
    /// </summary>
    private struct Member
    {
        public float O;
        public float Half;
        public float Reach;
        public int Id;
        public bool Provisional;
    }

    /// <summary>
    /// Whether this track draws infill between adjacent tracks (bed gaps/island decks). For completed/provisional pairs, provisional tracks
    /// draw in their own appearance; same-kind pairs use lower path_id.
    /// </summary>
    private static bool OwnsFill(int ownId, bool ownProvisional, int otherId, bool otherProvisional)
        => ownProvisional != otherProvisional ? ownProvisional : ownId < otherId;

    /// <summary>
    /// Compute neighbor joining corridors along own points.
    /// Return null if no joined/wall-blocked/platform-joined/encroached sample exists
    /// (callers fall back to standalone rendering).
    ///
    /// At each point, sort self and nearby candidates laterally; choose the connected component
    /// containing self whose successive bed-edge gaps are ≤ TRACK_MERGE_MAX_EDGE_GAP.
    /// Chaining allows three or more parallel tracks (quadruple-track equivalents) in one corridor.
    ///
    /// ownWallWidth is own sound-barrier width (wall_width, null-equivalent zero), used
    /// for physical wall-blocking zones (LeftWallBlocked / RightWallBlocked).
    ///
    /// ownPlatformLeft / ownPlatformRight are own platform extension widths
    /// (resolved by TrackPath.ResolvePlatformExtents; zero means absent). On platform sides:
    ///   - Do not chain bed joins (ballast infill, wall omission, pole corridors),
    ///     preventing gap fillers beneath platforms.
    ///   - Instead test island joins: nearly parallel station paths with facing platforms
    ///     join when bed-edge gap ≤ sum of platform extensions;
    ///     connect deck tops into one island platform.
    ///     Lower path_id fills to neighbor bed edge (=neighbor platform edge);
    ///     omit both platform_wall sides, and minimum-path_id track places poles outside full island width
    ///     (same main-track approach as bed-join pole corridors).
    ///   - For encroachment without an island (gap exceeds combined extensions but bed overlaps platform, etc.),
    ///     omit platform_wall and clamp deck before neighbor bed.
    ///
    /// ownLrtPlatformOuter is centerline-to-LRT-stop outer-edge distance for 431 (zero for non-LRT).
    /// Platforms follow stop positions; record only whether other beds overlap that band per point
    /// (Left/RightLrtPlatformBlocked; bed joins occur regardless of platforms).
    /// </summary>
    public static TrackCorridorInfo Compute(int ownPathId, List<Vector3> ownPoints, float ownWidth, float ownWallWidth, float ownPlatformLeft, float ownPlatformRight, List<TrackNeighborInfo> neighbors, float ownLrtPlatformOuter = 0f)
        => ComputeWithShifts(ownPathId, ownPoints, ownWidth, ownWallWidth, ownPlatformLeft, ownPlatformRight, neighbors, ownLrtPlatformOuter, null, false);

    /// <summary>
    /// Compute with per-point own bed outer shifts (ownOuterShift, S curves; null means zero).
    /// Check own/neighbor half-widths and platform edges at their S-shifted positions.
    /// Output extensions, deck outer edges, and fill endpoints remain distances from own centerline including shifts.
    /// ownProvisional marks unfinished tracks (planned/construction/preview). For completed/provisional pairs,
    /// provisional tracks draw infill and poles remain separate (TrackNeighborInfo.Provisional).
    /// </summary>
    public static TrackCorridorInfo ComputeWithShifts(int ownPathId, List<Vector3> ownPoints, float ownWidth, float ownWallWidth, float ownPlatformLeft, float ownPlatformRight, List<TrackNeighborInfo> neighbors, float ownLrtPlatformOuter, float[] ownOuterShift, bool ownProvisional)
    {
        if (ownPoints == null || ownPoints.Count < 2 || neighbors == null || neighbors.Count == 0) return null;
        if (ownOuterShift != null && ownOuterShift.Length != ownPoints.Count) ownOuterShift = null;

        int n = ownPoints.Count;
        float ownHalf = ownWidth * 0.5f;

        var info = new TrackCorridorInfo
        {
            Points = ownPoints,
            RightDir = new Vector3[n],
            CumulativeLength = new float[n],
            OwnHalfWidth = ownHalf,
            OwnOuterShift = ownOuterShift,
            LeftCovered = new bool[n],
            RightCovered = new bool[n],
            SuppressPoleSet = new bool[n],
            LeftWallBlocked = new bool[n],
            RightWallBlocked = new bool[n],
            LeftExtent = new float[n],
            RightExtent = new float[n],
            LeftFillTo = new float[n],
            RightFillTo = new float[n],
            LeftDeckFar = new float[n],
            RightDeckFar = new float[n],
            LeftDeckJoined = new bool[n],
            RightDeckJoined = new bool[n],
            LeftPlatformWallOmit = new bool[n],
            RightPlatformWallOmit = new bool[n],
            LeftLrtPlatformBlocked = new bool[n],
            RightLrtPlatformBlocked = new bool[n],
        };

        float cum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) cum += Vector3.Distance(ownPoints[i - 1], ownPoints[i]);
            info.CumulativeLength[i] = cum;
            float half = info.OwnHalfAt(i);
            info.LeftExtent[i] = half;
            info.RightExtent[i] = half;
            info.LeftFillTo[i] = float.NaN;
            info.RightFillTo[i] = float.NaN;
            info.LeftDeckFar[i] = ownPlatformLeft > 0f ? half + ownPlatformLeft : float.NaN;
            info.RightDeckFar[i] = ownPlatformRight > 0f ? half + ownPlatformRight : float.NaN;
        }
        info.TotalLength = cum;

        // Per-candidate XZ AABB expanded by scan distance. Coarse prefilter avoids
        // full polyline scans at every sample for obviously distant candidates.
        var nbBounds = new Rect[neighbors.Count];
        var nbCumulative = new float[neighbors.Count][];
        for (int j = 0; j < neighbors.Count; j++)
        {
            nbBounds[j] = ComputeExpandedBoundsXZ(neighbors[j]?.Points, MAX_SCAN_HALF_WIDTH);
            nbCumulative[j] = CumulativeLengths(neighbors[j]);
        }

        float cosMaxAngle = Mathf.Cos(LocalConstants.TRACK_MERGE_MAX_ANGLE_DEG * Mathf.Deg2Rad);
        float cosMaxConvergeAngle = Mathf.Cos(LocalConstants.TRACK_MERGE_MAX_CONVERGE_ANGLE_DEG * Mathf.Deg2Rad);
        bool anyWallBlock = false;
        bool anyPlatform = false;
        bool anyLrtPlatformBlock = false;
        var members = new List<Member>(neighbors.Count + 1);

        for (int i = 0; i < n; i++)
        {
            Vector3 ownPos = ownPoints[i];
            Vector3 fwd = ownPoints[Mathf.Min(i + 1, n - 1)] - ownPoints[Mathf.Max(i - 1, 0)];
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f)
            {
                info.RightDir[i] = i > 0 ? info.RightDir[i - 1] : Vector3.right;
                continue;
            }
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            info.RightDir[i] = right;

            float ownHalfAt = info.OwnHalfAt(i);
            members.Clear();
            members.Add(new Member { O = 0f, Half = ownHalfAt, Reach = ownHalfAt, Id = ownPathId, Provisional = ownProvisional });

            // Per-sample platform joining/encroachment aggregates (each side).
            // island*   ... island neighbor existence, nearest bed edge, minimum path_id, full-island outer edge.
            // encroach* ... minimum deck clamp position for encroachment without island joins.
            bool islandLeft = false, islandRight = false;
            float islandJoinLeft = float.MaxValue, islandJoinRight = float.MaxValue;
            int islandMinIdLeft = int.MaxValue, islandMinIdRight = int.MaxValue;
            float islandOuterLeft = 0f, islandOuterRight = 0f;
            // Whether nearest island neighbor is provisional (changes ownership for completed pairs) and its standalone deck outer edge (distance from own centerline).
            bool islandProvisionalLeft = false, islandProvisionalRight = false;
            float islandStandaloneLeft = float.MaxValue, islandStandaloneRight = float.MaxValue;
            float encroachLeft = float.MaxValue, encroachRight = float.MaxValue;

            for (int j = 0; j < neighbors.Count; j++)
            {
                TrackNeighborInfo nb = neighbors[j];
                if (nb?.Points == null || nb.Points.Count < 2) continue;
                if (!nbBounds[j].Contains(new Vector2(ownPos.x, ownPos.z))) continue;
                if (!TryClosestPointOnPolylineXZ(nb.Points, ownPos, out Vector3 cp, out Vector3 nbTan, out bool clampedAtPolylineEnd, out float nbAlong)) continue;

                // Require Y proximity (same vertical layer).
                // Do not join separate layers, such as elevated/surface or surface/underground tracks.
                if (Mathf.Abs(cp.y - ownPos.y) > LocalConstants.TRACK_MERGE_MAX_DY) continue;

                Vector3 delta = cp - ownPos;
                delta.y = 0f;
                // Exclude false endpoint clamps (neighbor ends before this point).
                if (Mathf.Abs(Vector3.Dot(fwd, delta)) > LONGITUDINAL_TOLERANCE) continue;

                float o = Vector3.Dot(right, delta);
                if (Mathf.Abs(o) > MAX_SCAN_HALF_WIDTH) continue;

                float nbShift = NeighborShift(nb, nbCumulative[j], nbAlong);
                float nbHalf = nb.Width * 0.5f + nbShift;
                float cosAngle = Mathf.Abs(Vector3.Dot(fwd, nbTan));
                float centerDist = delta.magnitude;

                // Endpoint-clamp artifact: nearest point clamps to neighbor endpoint while beds
                // are close enough to overlap; intersection-hull-clipped endpoints project near own centerline,
                // possibly on the opposite/outside side. Exclude these geometric artifacts from corridor
                // membership/island tests (otherwise walls/platforms become irregular for several samples before merge nodes).
                bool endClampVirtual = clampedAtPolylineEnd && centerDist < ownHalfAt + nbHalf;

                // Physical wall blocking, regardless of angle. Walls must not stand on other track beds.
                // Independently of parallelism, record where neighbor bed cross-section [o-nbHalf, o+nbHalf]
                // overlaps own wall zone [ownHalf, ownHalf+wall width+margin] (mirrored on left).
                // Prevent walls crossing neighbor bodies at merge throats (converging sections before tangents align)
                // or shallow-angle at-grade crossings.
                if (ownWallWidth > 0f)
                {
                    float zoneMin = ownHalfAt;
                    float zoneMax = ownHalfAt + ownWallWidth + WALL_BLOCK_CLEARANCE;
                    if (o + nbHalf > zoneMin && o - nbHalf < zoneMax)
                    {
                        info.RightWallBlocked[i] = true;
                        anyWallBlock = true;
                    }
                    if (-o + nbHalf > zoneMin && -o - nbHalf < zoneMax)
                    {
                        info.LeftWallBlocked[i] = true;
                        anyWallBlock = true;
                    }
                }

                // Whether another bed overlaps the LRT-platform band (own bed edge→platform outer edge+margin), regardless of angle.
                // Start the band at own bed edge to exclude endpoint artifacts from continuing tracks.
                if (ownLrtPlatformOuter > 0f && !endClampVirtual)
                {
                    float zoneMax = ownLrtPlatformOuter + PLATFORM_ENCROACH_CLEARANCE;
                    if (o + nbHalf > ownHalfAt && o - nbHalf < zoneMax)
                    {
                        info.RightLrtPlatformBlocked[i] = true;
                        anyLrtPlatformBlock = true;
                    }
                    if (-o + nbHalf > ownHalfAt && -o - nbHalf < zoneMax)
                    {
                        info.LeftLrtPlatformBlocked[i] = true;
                        anyLrtPlatformBlock = true;
                    }
                }

                // Platform joining/encroachment tests (only sides with own platforms).
                float myPlatform = o > 0f ? ownPlatformRight : ownPlatformLeft;
                if (myPlatform > 0f)
                {
                    float absO = Mathf.Abs(o);
                    float edgeGap = absO - ownHalfAt - nbHalf;

                    // Neighbor platform extensions facing self and facing away
                    // (island outer edge). Map neighbor geometric left/right into own frame
                    // based on which side self occupies (-delta=horizontal vector from neighbor's nearest point to self).
                    Vector3 nbRight = Vector3.Cross(Vector3.up, nbTan);
                    bool ownIsOnNbRight = Vector3.Dot(nbRight, -delta) > 0f;
                    float nbFacingPlatform = ownIsOnNbRight ? nb.PlatformRight : nb.PlatformLeft;
                    float nbOuterPlatform = ownIsOnNbRight ? nb.PlatformLeft : nb.PlatformRight;

                    // Island joins: nearly parallel station paths with facing platforms and bed-edge gap
                    // no greater than combined platform extensions (=decks can reach).
                    bool island = !endClampVirtual && cosAngle >= cosMaxAngle
                        && Mathf.Abs(cp.y - ownPos.y) <= PLATFORM_JOIN_MAX_DY
                        && nbFacingPlatform > 0f
                        && edgeGap > 0f && edgeGap <= myPlatform + nbFacingPlatform;
                    if (island)
                    {
                        // Fill to neighbor platform edge line (matches its edge-part geometry, which covers the seam).
                        float joinTo = absO - (nb.PlatformEdge + nbShift);
                        float outerTo = absO + nbHalf + nbOuterPlatform;
                        float standalone = absO - nbHalf - nbFacingPlatform;
                        if (o > 0f)
                        {
                            islandRight = true;
                            if (joinTo < islandJoinRight) { islandJoinRight = joinTo; islandProvisionalRight = nb.Provisional; islandStandaloneRight = standalone; }
                            if (nb.Provisional == ownProvisional && nb.PathId < islandMinIdRight) islandMinIdRight = nb.PathId;
                            if (nb.Provisional == ownProvisional && outerTo > islandOuterRight) islandOuterRight = outerTo;
                        }
                        else
                        {
                            islandLeft = true;
                            if (joinTo < islandJoinLeft) { islandJoinLeft = joinTo; islandProvisionalLeft = nb.Provisional; islandStandaloneLeft = standalone; }
                            if (nb.Provisional == ownProvisional && nb.PathId < islandMinIdLeft) islandMinIdLeft = nb.PathId;
                            if (nb.Provisional == ownProvisional && outerTo > islandOuterLeft) islandOuterLeft = outerTo;
                        }
                    }
                    else if (!endClampVirtual && edgeGap < myPlatform + PLATFORM_ENCROACH_CLEARANCE)
                    {
                        // Encroachment without island joins where other beds overlap platform including outer margin.
                        // Artifacts clamped to connected endpoints are not beds crossing the platform.
                        // Record deck clamping positions before beds (any angle).
                        float limit = absO - nbHalf - PLATFORM_ENCROACH_CLEARANCE;
                        if (o > 0f) { if (limit < encroachRight) encroachRight = limit; }
                        else { if (limit < encroachLeft) encroachLeft = limit; }
                    }

                    // Exclude tracks on own platform sides from bed-join chains
                    // (prevent ballast fill/pole corridors beneath platforms; island tests above handle
                    // platform-side joins and full-island pole spans).
                    continue;
                }

                // Corridor membership: nearly parallel tracks (absolute alignment allows opposite up/down directions)
                // and converging merge/branch tracks (angle <=45°, same as turn constraint)
                // join once bed-edge gaps meet the threshold.
                // Waiting for full tangent alignment on double-track merges would delay joining
                // until beds overlap, leaving inter-track walls across neighboring bodies.
                bool parallel = cosAngle >= cosMaxAngle;
                bool converging = !parallel && cosAngle >= cosMaxConvergeAngle
                    && (centerDist - nbHalf - ownHalfAt) <= LocalConstants.TRACK_MERGE_MAX_EDGE_GAP;
                if (!parallel && !converging) continue;

                // Endpoint-clamp artifact guard (endClampVirtual above): prevent false outside-neighbor
                // detection before merges from removing outside sound barriers for a few samples
                // and creating wall gaps.
                if (endClampVirtual) continue;

                if (Mathf.Abs(o) < 0.01f) continue;

                // In neighbor LRT-platform intervals, move main-track poles beyond its platform outer edge.
                float reach = nbHalf;
                if (nb.LrtPlatformOuter > 0f && LrtPlatformParts.Contains(nb.LrtPlatformZones, nbAlong))
                    reach = Mathf.Max(reach, nb.LrtPlatformOuter + LrtRoadParts.PLATFORM_POLE_CLEARANCE - TrackPath.POLE_MARGIN);
                members.Add(new Member { O = o, Half = nbHalf, Reach = reach, Id = nb.PathId, Provisional = nb.Provisional });
            }

            // Finalize platform joining/encroachment regardless of bed-join membership.
            if (ownPlatformRight > 0f)
            {
                if (islandRight)
                {
                    anyPlatform = true;
                    info.RightPlatformWallOmit[i] = true;
                    ApplyIslandDeck(info.RightDeckFar, info.RightDeckJoined, i, ownPathId, ownProvisional, islandProvisionalRight,
                        islandMinIdRight, islandJoinRight, islandStandaloneRight, encroachRight);
                    // Minimum-path_id track of the same kind (completed/completed or provisional/provisional) places poles outside full island width (including neighbor outer platform),
                    //using the bed-corridor main-track approach. Completed/provisional tracks do not share.
                    if (islandMinIdRight != int.MaxValue)
                    {
                        info.RightExtent[i] = Mathf.Max(info.RightExtent[i], islandOuterRight);
                        if (islandMinIdRight < ownPathId) info.SuppressPoleSet[i] = true;
                    }
                }
                else if (encroachRight < float.MaxValue)
                {
                    anyPlatform = true;
                    info.RightPlatformWallOmit[i] = true;
                    info.RightDeckFar[i] = Mathf.Min(info.RightDeckFar[i], encroachRight);
                }
            }
            if (ownPlatformLeft > 0f)
            {
                if (islandLeft)
                {
                    anyPlatform = true;
                    info.LeftPlatformWallOmit[i] = true;
                    ApplyIslandDeck(info.LeftDeckFar, info.LeftDeckJoined, i, ownPathId, ownProvisional, islandProvisionalLeft,
                        islandMinIdLeft, islandJoinLeft, islandStandaloneLeft, encroachLeft);
                    if (islandMinIdLeft != int.MaxValue)
                    {
                        info.LeftExtent[i] = Mathf.Max(info.LeftExtent[i], islandOuterLeft);
                        if (islandMinIdLeft < ownPathId) info.SuppressPoleSet[i] = true;
                    }
                }
                else if (encroachLeft < float.MaxValue)
                {
                    anyPlatform = true;
                    info.LeftPlatformWallOmit[i] = true;
                    info.LeftDeckFar[i] = Mathf.Min(info.LeftDeckFar[i], encroachLeft);
                }
            }

            if (members.Count <= 1) continue;

            members.Sort((a, b) => a.O.CompareTo(b.O));
            int ownIdx = -1;
            for (int m = 0; m < members.Count; m++)
            {
                if (members[m].Id == ownPathId) { ownIdx = m; break; }
            }
            if (ownIdx < 0) continue;

            // Find the component containing self, chained by bed-edge gaps within threshold.
            int lo = ownIdx;
            while (lo - 1 >= 0 && (members[lo].O - members[lo].Half) - (members[lo - 1].O + members[lo - 1].Half) <= LocalConstants.TRACK_MERGE_MAX_EDGE_GAP)
            {
                lo--;
            }
            int hi = ownIdx;
            while (hi + 1 < members.Count && (members[hi + 1].O - members[hi + 1].Half) - (members[hi].O + members[hi].Half) <= LocalConstants.TRACK_MERGE_MAX_EDGE_GAP)
            {
                hi++;
            }
            if (lo == ownIdx && hi == ownIdx) continue;

            info.AnyCoverage = true;
            // Share poles only among same-kind members (completed/completed or provisional/provisional). Completed/provisional tracks
            // each use their own width, keeping completed poles intact if provisional neighbors hide/cancel.
            // Island pole corridors finalized above may have already expanded the opposite side,
            // so combine with maximum rather than overwrite.
            int minId = ownPathId;
            for (int m = lo; m <= hi; m++)
            {
                if (members[m].Provisional != ownProvisional) continue;
                info.LeftExtent[i] = Mathf.Max(info.LeftExtent[i], -(members[m].O - members[m].Reach));
                info.RightExtent[i] = Mathf.Max(info.RightExtent[i], members[m].O + members[m].Reach);
                if (members[m].Id < minId) minId = members[m].Id;
            }
            if (minId < ownPathId) info.SuppressPoleSet[i] = true;

            if (lo < ownIdx)
            {
                Member lm = members[ownIdx - 1];
                info.LeftCovered[i] = true;
                if (OwnsFill(ownPathId, ownProvisional, lm.Id, lm.Provisional)) info.LeftFillTo[i] = -(lm.O + lm.Half);
            }
            if (hi > ownIdx)
            {
                Member rm = members[ownIdx + 1];
                info.RightCovered[i] = true;
                if (OwnsFill(ownPathId, ownProvisional, rm.Id, rm.Provisional)) info.RightFillTo[i] = rm.O - rm.Half;
            }
        }

        return (info.AnyCoverage || anyWallBlock || anyPlatform || anyLrtPlatformBlock) ? info : null;
    }

    /// <summary>
    /// Determine one island deck's outer edge. For same-kind pairs, lower path_id extends to neighbor edge;
    /// the other omits its deck. For completed/provisional pairs, completed keeps its standalone outer edge
    /// (remaining intact if provisional disappears); provisional fills to it to connect the island. Stop at intervening third tracks.
    /// </summary>
    private static void ApplyIslandDeck(float[] deckFar, bool[] deckJoined, int i, int ownPathId, bool ownProvisional, bool partnerProvisional,
        int minSameKindId, float joinTo, float partnerStandalone, float encroach)
    {
        if (partnerProvisional != ownProvisional)
        {
            if (ownProvisional)
            {
                deckFar[i] = Mathf.Min(partnerStandalone, encroach);
                deckJoined[i] = encroach >= partnerStandalone;
            }
            else
            {
                deckFar[i] = Mathf.Min(deckFar[i], encroach);
            }
            return;
        }
        if (ownPathId < minSameKindId)
        {
            deckFar[i] = Mathf.Min(joinTo, encroach);
            deckJoined[i] = encroach >= joinTo;
        }
        else
        {
            deckFar[i] = float.NaN;
        }
    }

    /// <summary>Neighbor point-list cumulative 3D distances for S shifts (null without shifts).</summary>
    private static float[] CumulativeLengths(TrackNeighborInfo nb)
    {
        if (nb?.OuterShifts == null || nb.Points == null || nb.OuterShifts.Length != nb.Points.Count) return null;
        var cumulative = new float[nb.Points.Count];
        for (int i = 1; i < cumulative.Length; i++) cumulative[i] = cumulative[i - 1] + Vector3.Distance(nb.Points[i - 1], nb.Points[i]);
        return cumulative;
    }

    /// <summary>Neighbor bed outer shift at point-list distance along (linear interpolation between points).</summary>
    private static float NeighborShift(TrackNeighborInfo nb, float[] cumulative, float along)
    {
        if (cumulative == null) return 0f;
        int hi = System.Array.BinarySearch(cumulative, along);
        if (hi >= 0) return nb.OuterShifts[hi];
        hi = ~hi;
        if (hi <= 0) return nb.OuterShifts[0];
        if (hi >= cumulative.Length) return nb.OuterShifts[cumulative.Length - 1];
        float span = cumulative[hi] - cumulative[hi - 1];
        float t = span > 1e-6f ? (along - cumulative[hi - 1]) / span : 0f;
        return Mathf.Lerp(nb.OuterShifts[hi - 1], nb.OuterShifts[hi], t);
    }

    /// <summary>Return point-list XZ AABB expanded by margin (empty Rect for invalid points).</summary>
    internal static Rect ComputeExpandedBoundsXZ(List<Vector3> points, float margin)
    {
        if (points == null || points.Count == 0) return new Rect(0f, 0f, -1f, -1f);
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var p in points)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }
        return new Rect(minX - margin, minZ - margin, (maxX - minX) + margin * 2f, (maxZ - minZ) + margin * 2f);
    }

    /// <summary>
    /// Find the nearest polyline point in XZ. closest Y is segment-interpolated
    /// (for height-difference tests); tangent is nearest segment's horizontal unit direction.
    /// clampedAtPolylineEnd is true when nearest point clamps to start/end
    /// (true perpendicular foot lies at endpoint or outside polyline).
    /// Callers use it to reject intersection-clipped neighbor endpoints
    /// as false adjacency. along is cumulative 3D distance to the nearest point.
    /// </summary>
    internal static bool TryClosestPointOnPolylineXZ(List<Vector3> points, Vector3 query, out Vector3 closest, out Vector3 tangent, out bool clampedAtPolylineEnd, out float along)
    {
        closest = query;
        tangent = Vector3.forward;
        clampedAtPolylineEnd = false;
        along = 0f;
        float bestSq = float.MaxValue;
        bool found = false;
        float start = 0f;

        for (int i = 0; i < points.Count - 1; i++, start += Vector3.Distance(points[i - 1], points[i]))
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];
            float abx = b.x - a.x;
            float abz = b.z - a.z;
            float lenSq = abx * abx + abz * abz;
            if (lenSq < 1e-8f) continue;

            float tRaw = ((query.x - a.x) * abx + (query.z - a.z) * abz) / lenSq;
            float t = Mathf.Clamp01(tRaw);
            Vector3 p = a + (b - a) * t;
            float dx = p.x - query.x;
            float dz = p.z - query.z;
            float dSq = dx * dx + dz * dz;
            if (dSq < bestSq)
            {
                bestSq = dSq;
                closest = p;
                Vector3 tan = b - a;
                tan.y = 0f;
                tangent = tan.sqrMagnitude > 1e-8f ? tan.normalized : Vector3.forward;
                clampedAtPolylineEnd = (i == 0 && tRaw <= 0f) || (i == points.Count - 2 && tRaw >= 1f);
                along = start + Vector3.Distance(a, b) * t;
                found = true;
            }
        }
        return found;
    }
}
