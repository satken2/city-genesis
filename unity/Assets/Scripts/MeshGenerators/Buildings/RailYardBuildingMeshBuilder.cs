using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Railway depot (MODEL_RAIL_YARD) mesh generator.
///
/// Depot lead tracks weld to a running-network terminal (depot_node_id; shared with the power connection node
/// for railway/monorail depots). Render internal tracks converging to that terminal.
/// Procedurally build two zones from footprint/terminal position:
///
///   1. Parallel-track zone: equally spaced straight tracks (yard_track tiles)
///      opposite the terminal (TRACK_SPACING). Place buffer stops (yard_stop) at dead ends,
///      overhead wires (yard_wire) and portal supports (yard_pole + yard_beam) above.
///      Cover the first two tracks with a maintenance shed (yard_shed) if room allows.
///   2. Convergence/connection zone: diagonal ladder tracks extend from the outermost tracks on each side of a centerline through the connection.
///      Each siding merges into its side's ladder track; both ladder tracks connect to the centerline.
/// Adjust tile remainders only lengthwise; deform curve vertices along the centerline.
/// Keep gauge/height at actual size. X-scale yard_beam between posts.
///
/// Resolve terminal position from PathManager's node cache. If unloaded or in test scenes,
/// fall back to backend initial placement (short-edge midpoint for rectangular parcels; otherwise midpoint of the edge farthest from frontage;
/// front-edge midpoint for LRT depots; _connection_node_position in crud_agents.py).
/// BuildingManager.OnTerminalNodeMoved regenerates this building after confirmed terminal movement.
///
/// If one double-track path connects to the terminal (LRT dedicated track 431 or combined double track), draw the lead
/// as double track with matching spacing. One track is the ladder lead; the other merges into it through a crossover. When connected tracks change,
/// BuildingManager.OnDepotTerminalPathsChanged rebuilds this building.
///
/// Plan placement on horizontal local Y=0; terrain conformity (isDynamic) raises each track/wire/post/stop/shed vertex
/// to terrain elevation at its horizontal position (relative to center_point terrain elevation, preventing buried tracks on slopes).
/// Cover the entire site with a{variant}'s own floor part (1060 uses PATH_ROADBED like the track bed).
///
/// See city-genesis/docs/ja/geometries/F1_建物メッシュの生成方式.md for the overview.
/// </summary>
public class RailYardBuildingMeshBuilder : IBuildingMeshGenerator
{
    /// <summary>Parallel-track center spacing (m). Same as path tracks (width=5.0).</summary>
    private const float TRACK_SPACING = 5f;

    /// <summary>Lateral margin from site boundary to outermost track center (m), including room for overhead-wire posts.</summary>
    private const float SIDE_MARGIN = 3.5f;

    /// <summary>End margin from site boundary to track end (m).</summary>
    private const float END_MARGIN = 2f;

    /// <summary>Maximum parallel tracks. Limits draw calls on huge sites.</summary>
    private const int MAX_TRACKS = 10;

    /// <summary>Crossover run for the second lead to merge (m). At 3.5m center spacing, minimum curve radius is about 17m.</summary>
    private const float SECOND_LEAD_RUN = 16f;

    /// <summary>Run per 1m lateral ladder-track movement. Adjacent merges are spaced 10m apart.</summary>
    private const float LADDER_RUN_PER_LATERAL = 2f;
    /// <summary>Run from siding bend start to ladder-track intersection.</summary>
    private const float ENTRY_APPROACH = 6f;
    /// <summary>Run from ladder-track intersection to tangent-aligned merge point.</summary>
    private const float ENTRY_OVERLAP = 4f;

    /// <summary>Margin treating tracks exactly on site boundaries as inside (m).</summary>
    private const float BOUNDARY_TOLERANCE = .01f;

    /// <summary>Minimum straight-zone length (m). Omit rows shorter than this.</summary>
    private const float MIN_STRAIGHT_LENGTH = 6f;

    /// <summary>Portal overhead-wire post spacing (m).</summary>
    private const float POLE_SPACING = 30f;

    /// <summary>Outward offset from outermost track center to overhead-wire post (m).</summary>
    private const float POLE_OFFSET = 2.0f;

    /// <summary>Authored yard_beam width (m). X-scale by the post-distance ratio.</summary>
    private const float BEAM_AUTHORED_WIDTH = 6f;

    /// <summary>Authored maintenance-shed (yard_shed) size (width/length, m). Match build_a1060.py.</summary>
    private const float SHED_WIDTH = 11.2f;
    private const float SHED_LENGTH = 24f;

    /// <summary>Collider extrusion height (m), approximately overhead-wire post top.</summary>
    private const float COLLIDER_HEIGHT = 7f;

    /// <summary>Rectangle angle tolerance (sin). Same as backend RECTANGLE_ANGLE_TOLERANCE (1°).</summary>
    private static readonly float RECTANGLE_ANGLE_TOLERANCE = Mathf.Sin(1f * Mathf.Deg2Rad);

    /// <summary>One track (row) plan in 2D U (track direction) / V (lateral) coordinates.</summary>
    private struct TrackRow
    {
        public float V;             // Row lateral offset.
        public float StraightStart; // Straight section start (dead-end side).
        public float StraightEnd;   // Straight section end (= curve start).
        public Vector2 Target;      // Merge point on the shared diagonal ladder track.
        public Vector2 Direction;   // Tangent of the target ladder track.
        public bool IsSpine;        // Outermost row defining the ladder-track origin.
        public int Side;            // Centerline=0, sides=-1/+1.
        public Vector2 MergePoint;  // Position where this side's ladder joins the centerline.
    }

    /// <summary>Terminal/lead rendering determined by the depot's running network and connected tracks.</summary>
    private struct YardProfile
    {
        public bool TerminalOnFrontage; // Initial terminal position is front-edge (vertex 0→1) midpoint.
        public float LeadHalfSpacing;   // Half track-center spacing of double-track terminal leads. Zero for single track.
    }

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null || buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"[RailYardBMB] 底面ポリゴンが不正です (Building ID: {buildingData?.agent_id})。白い箱にフォールバックします。");
            return BuildingMeshBuilder.GenerateSimpleBuilding(null, 1, LocalConstants.FLOOR_HEIGHT);
        }

        Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
        string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;

        // center_point world coordinates/terrain height. Rest at center terrain elevation, like PORT/PREBUILT.
        Vector3 centerPointWorld = Vector3.zero;
        if (buildingData.centerPointArray != null && buildingData.centerPointArray.Length >= 2)
        {
            float cx = buildingData.centerPointArray[0];
            float cz = buildingData.centerPointArray[1];
            float cy = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
            centerPointWorld = new Vector3(cx, cy, cz);
        }

        Vector2[] parcelPolygon = BuildingMeshUtils.ResolveParcelContext(buildingData, out ParcelSummary parcel, out _);
        Vector2[] sitePolygon = (parcelPolygon != null && parcelPolygon.Length >= 3) ? parcelPolygon : footprint;
        // Initial terminal placement depends on parcel vertex indices; inspect before normalizing winding.
        Vector2[] siteRing = ToLocalRing(parcel, centerPointWorld) ?? footprint;

        // Terminal local XZ (fallback mimics backend initial placement when unloaded).
        YardProfile profile = ResolveProfile(buildingData);
        Vector2 nodeLocal = ResolveTerminalNodeLocal(buildingData, centerPointWorld, footprint, siteRing, profile.TerminalOnFrontage);

        // Local basis: U=track direction toward initial site connection edge; V=left perpendicular.
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < footprint.Length; i++)
        {
            centroid += footprint[i];
        }
        centroid /= footprint.Length;

        // Use initial connection edge as direction reference so lateral terminal moves do not rotate all tracks.
        Vector2 u2 = ResolveTerminalNodeLocal(null, centerPointWorld, footprint, siteRing, profile.TerminalOnFrontage) - centroid;
        if (u2.sqrMagnitude < 1e-4f)
        {
            // Degenerate terminal-at-centroid case: use front-edge (vertex 0→1) normal.
            Vector2 front = (footprint[1] - footprint[0]).normalized;
            u2 = new Vector2(-front.y, front.x);
        }
        u2.Normalize();
        if (Vector2.Dot(nodeLocal - centroid, u2) < 0f) u2 = -u2;
        Vector2 v2 = new Vector2(-u2.y, u2.x);

        // Project footprint into UV coordinates.
        Vector2[] uv = new Vector2[footprint.Length];
        float vMin = float.MaxValue, vMax = float.MinValue;
        for (int i = 0; i < footprint.Length; i++)
        {
            Vector2 d = footprint[i] - centroid;
            uv[i] = new Vector2(Vector2.Dot(d, u2), Vector2.Dot(d, v2));
            vMin = Mathf.Min(vMin, uv[i].y);
            vMax = Mathf.Max(vMax, uv[i].y);
        }
        Vector2 nodeUV = new Vector2(Vector2.Dot(nodeLocal - centroid, u2), Vector2.Dot(nodeLocal - centroid, v2));
        // Double-track leads converge internal tracks to one terminal-side track position (single track uses terminal itself).
        Vector2 trunkNode = nodeUV - new Vector2(0f, profile.LeadHalfSpacing);

        // Allocate parallel rows (count from site width, centered).
        float usable = (vMax - vMin) - SIDE_MARGIN * 2f;
        int trackCount = Mathf.Clamp(Mathf.FloorToInt(usable / TRACK_SPACING) + 1, 1, MAX_TRACKS);
        float vCenter = (vMin + vMax) * 0.5f;

        List<TrackRow> rows = PlanRows(uv, trunkNode, vCenter, trackCount);


        if (rows.Count == 0)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[RailYardBMB] agent={buildingData.agent_id} 線路を1本も敷けない敷地形状。白い箱にフォールバック");
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // Load parts (without yard_track, tracks cannot render; use a white box).
        Mesh trackMesh = LoadPartMesh(prefix, "yard_track", out Material[] trackMats, temporaryMeshes);
        if (trackMesh == null)
        {
            foreach (Mesh temp in temporaryMeshes) Object.Destroy(temp);
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[RailYardBMB] agent={buildingData.agent_id} yard_track パーツ未配置。白い箱にフォールバック");
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
        }
        Mesh wireMesh = LoadPartMesh(prefix, "yard_wire", out Material[] wireMats, temporaryMeshes);
        Mesh poleMesh = LoadPartMesh(prefix, "yard_pole", out Material[] poleMats, temporaryMeshes);
        Mesh beamMesh = LoadPartMesh(prefix, "yard_beam", out Material[] beamMats, temporaryMeshes);
        Mesh stopMesh = LoadPartMesh(prefix, "yard_stop", out Material[] stopMats, temporaryMeshes);
        Mesh shedMesh = LoadPartMesh(prefix, "yard_shed", out Material[] shedMats, temporaryMeshes);

        // Actual tile dimensions (local +Z along track; rebased through bounds, independent of origin position).
        float tileLength = Mathf.Max(trackMesh.bounds.size.z, 0.5f);
        float tileMinZ = trackMesh.bounds.min.z;
        float wireMinZ = wireMesh != null ? wireMesh.bounds.min.z : 0f;

        // Coordinate helpers: UV → local XZ / world direction.
        Vector3 ToLocal3(Vector2 p) => new Vector3(
            centroid.x + u2.x * p.x + v2.x * p.y, 0f,
            centroid.y + u2.y * p.x + v2.y * p.y);
        Vector3 ToDir3(Vector2 d) => new Vector3(
            u2.x * d.x + v2.x * d.y, 0f,
            u2.y * d.x + v2.y * d.y).normalized;

        Quaternion forwardRot = Quaternion.LookRotation(ToDir3(Vector2.right), Vector3.up);

        // Tile straight/curved sections separately; only the target row draws shared sections.
        foreach (TrackRow row in rows)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt((row.StraightEnd - row.StraightStart) / tileLength));
            float step = (row.StraightEnd - row.StraightStart) / n;
            for (int t = 0; t < n; t++)
            {
                Vector3 pos = ToLocal3(new Vector2(row.StraightStart + t * step, row.V));
                Matrix4x4 m = Matrix4x4.TRS(pos, forwardRot, new Vector3(1f, 1f, step / tileLength))
                    * Matrix4x4.Translate(new Vector3(0f, 0f, -tileMinZ));
                AddInstances(trackMesh, trackMats, m, combineInstances, materials);
                if (wireMesh != null)
                    AddInstances(wireMesh, wireMats,
                        Matrix4x4.TRS(pos, forwardRot, new Vector3(1f, 1f, step / wireMesh.bounds.size.z))
                        * Matrix4x4.Translate(new Vector3(0f, 0f, -wireMinZ)), combineInstances, materials);
            }
            if (stopMesh != null)
                AddInstances(stopMesh, stopMats,
                    Matrix4x4.TRS(ToLocal3(new Vector2(row.StraightStart, row.V)), forwardRot, Vector3.one),
                    combineInstances, materials);

            Vector2 p0 = new Vector2(row.StraightEnd, row.V);
            float run = row.Target.x - p0.x;
            if (row.Side == 0) continue;
            PlaceTilesAlongBezier(p0, p0 + Vector2.right * (run * .4f),
                row.Target - row.Direction * ENTRY_OVERLAP, row.Target,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes,
                row.Side != 0 && !row.IsSpine);
        }

        // Converge from outer tracks on each side to the centerline. No curves returning lateral position.
        foreach (int side in new[] { -1, 1 })
        {
            TrackRow first = rows.Find(row => row.Side == side && row.IsSpine);
            if (first.Side == 0) continue;
            Vector2 spineStart = first.Target;
            Vector2 spineEnd = first.MergePoint + new Vector2(-8f, side * 2f);
            Vector2 delta = spineEnd - spineStart;
            PlaceTilesAlongBezier(spineStart, spineStart + delta / 3f, spineStart + delta * (2f / 3f), spineEnd,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, false);
            PlaceTilesAlongBezier(spineEnd, spineEnd + first.Direction * 3f,
                first.MergePoint - Vector2.right * 3f, first.MergePoint,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, true);
        }
        // Bend the second double-track lead directly from the terminal, merging through a crossover.
        float secondMergeU = float.NaN;
        if (profile.LeadHalfSpacing > 0f
            && TryPlanSecondLead(uv, rows, nodeUV, trunkNode, out Vector2 secondEnd, out Vector2 secondMerge))
        {
            Vector2 handle = Vector2.right * (SECOND_LEAD_RUN * .4f);
            PlaceTilesAlongBezier(secondEnd, secondEnd - handle, secondMerge + handle, secondMerge,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, true);
            secondMergeU = secondMerge.x;
        }

        // --- 3. Maintenance shed: cover dead ends of first two tracks (only with sufficient straight-zone length) ---
        // Fix shed placement before posts; omit portal posts in its section (beams would pierce the shed).
        float shedUStart = float.NaN, shedUEnd = float.NaN;
        if (shedMesh != null && rows.Count >= 2)
        {
            TrackRow rowA = rows[0];
            TrackRow rowB = rows[1];
            float uStart = Mathf.Max(rowA.StraightStart, rowB.StraightStart) + 1.5f;
            float uEnd = uStart + SHED_LENGTH;
            if (uEnd + 2f < Mathf.Min(rowA.StraightEnd, rowB.StraightEnd)
                && Mathf.Abs(rowA.V - rowB.V) <= TRACK_SPACING + .01f)
            {
                Vector3 pos = ToLocal3(new Vector2((uStart + uEnd) * 0.5f, (rowA.V + rowB.V) * 0.5f));
                AddInstances(shedMesh, shedMats, Matrix4x4.TRS(pos, forwardRot, Vector3.one), combineInstances, materials);
                shedUStart = uStart;
                shedUEnd = uEnd;
            }
        }

        // --- 4. Portal posts: evenly spaced in the shared straight section, excluding shed ---
        if (poleMesh != null && rows.Count > 0)
        {
            float vTrackMin = float.MaxValue, vTrackMax = float.MinValue;
            float uCommonStart = float.MinValue, uCommonEnd = float.MaxValue;
            foreach (TrackRow row in rows)
            {
                vTrackMin = Mathf.Min(vTrackMin, row.V);
                vTrackMax = Mathf.Max(vTrackMax, row.V);
                uCommonStart = Mathf.Max(uCommonStart, row.StraightStart);
                uCommonEnd = Mathf.Min(uCommonEnd, row.StraightEnd);
            }
            // The second-lead crossover crosses outside rows; omit posts there.
            if (!float.IsNaN(secondMergeU)) uCommonEnd = Mathf.Min(uCommonEnd, secondMergeU - 2f);
            float vLeft = vTrackMin - POLE_OFFSET;
            float vRight = vTrackMax + POLE_OFFSET;
            // yard_pole is authored for the right side (track center toward -X). Base rotation maps -X to +V,
            // so retain it on +V side (vRight); rotate yaw 180° on -V side (vLeft) to face track center.
            Quaternion rightRot = forwardRot;
            Quaternion leftRot = forwardRot * Quaternion.Euler(0f, 180f, 0f);
            for (float uPole = uCommonStart + 4f; uPole <= uCommonEnd - 2f; uPole += POLE_SPACING)
            {
                if (!float.IsNaN(shedUStart) && uPole > shedUStart - 1f && uPole < shedUEnd + 1f)
                {
                    continue;
                }
                AddInstances(poleMesh, poleMats,
                    Matrix4x4.TRS(ToLocal3(new Vector2(uPole, vRight)), rightRot, Vector3.one),
                    combineInstances, materials);
                AddInstances(poleMesh, poleMats,
                    Matrix4x4.TRS(ToLocal3(new Vector2(uPole, vLeft)), leftRot, Vector3.one),
                    combineInstances, materials);
                if (beamMesh != null)
                {
                    // X-scale the authored 6m portal beam to span posts (authored local ±X is lateral).
                    float span = vRight - vLeft;
                    Vector3 center = ToLocal3(new Vector2(uPole, (vLeft + vRight) * 0.5f));
                    AddInstances(beamMesh, beamMats,
                        Matrix4x4.TRS(center, forwardRot, new Vector3(span / BEAM_AUTHORED_WIDTH, 1f, 1f)),
                        combineInstances, materials);
                }
            }
        }

        // Site decorations handle their own terrain conformity; first conform only internal equipment.
        TerrainOffsetSampler terrain = isDynamic ? new TerrainOffsetSampler(centerPointWorld) : null;
        if (terrain != null) ConformToTerrain(terrain, combineInstances, materials, temporaryMeshes);

        // Cover track gaps with ground panels using the track-bed material. No shared-pavement fallback.
        Material floorMaterial = BuildingMeshUtils.ResolveFloorMaterialFromMaster(buildingData, prefix, ownPartOnly: true);
        ModularBodyBuilder.GenerateSiteDecorations(
            prefix, 0,
            sitePolygon, sitePolygon, sitePolygon,
            floorMaterial,
            centerPointWorld, deformToTerrain: isDynamic,
            combineInstances, materials, temporaryMeshes,
            fenceOpeningCenter: ResolveFenceOpening(sitePolygon,nodeLocal,u2), fenceOpeningRadius: 3f + profile.LeadHalfSpacing);

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[RailYardBMB] agent={buildingData.agent_id} prefix={prefix} tracks={rows.Count} " +
            $"node=({nodeUV.x:F1},{nodeUV.y:F1}) instances={combineInstances.Count}");

        // --- 6. Combine/collider ---
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(footprint, COLLIDER_HEIGHT);
        if (terrain != null && colliderMesh != null)
        {
            // Conform each footprint vertex to terrain so uphill tracks do not exceed collider top.
            Vector3[] colliderVertices = colliderMesh.vertices;
            for (int i = 0; i < colliderVertices.Length; i++)
                colliderVertices[i].y += terrain.Offset(colliderVertices[i].x, colliderVertices[i].z);
            colliderMesh.vertices = colliderVertices;
            colliderMesh.RecalculateNormals();
            colliderMesh.RecalculateBounds();
        }
        var result = new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    /// <summary>
    /// Resolve terminal-node local XZ (depot_node_id, same as infra_node_id when shared).
    /// If absent from PathManager node cache (unloaded/test scenes), fall back to a position mimicking
    /// backend initial placement (crud_agents._connection_node_position):
    /// front-edge midpoint for onFrontage; short-edge midpoint for rectangular siteRing parcels;
    /// otherwise midpoint of the edge farthest from frontage/front edge. Matches unless the user moved the terminal.
    /// </summary>
    private static Vector2 ResolveTerminalNodeLocal(BuildingSummary buildingData, Vector3 centerPointWorld,
        Vector2[] footprint, Vector2[] siteRing, bool onFrontage)
    {
        int nodeId = buildingData == null ? 0 : buildingData.depot_node_id > 0 ? buildingData.depot_node_id : buildingData.infra_node_id;
        if (nodeId > 0)
        {
            PathManager pathManager = Object.FindAnyObjectByType<PathManager>();
            NodeData node = pathManager != null ? pathManager.GetNodeData(nodeId) : null;
            if (node != null)
            {
                Vector3 abs = node.GetAbsolutePosition();
                return new Vector2(abs.x - centerPointWorld.x, abs.z - centerPointWorld.z);
            }
        }

        int n = footprint.Length;
        if (onFrontage) return (footprint[0] + footprint[1 % n]) * 0.5f;
        if (TryGetRectangleShortSideMidpoint(siteRing, out Vector2 shortSide)) return shortSide;

        // Fallback: midpoint of edge farthest from front edge (vertex 0→1).
        Vector2 frontA = footprint[0];
        Vector2 frontB = footprint[1 % n];
        Vector2 best = (footprint[0] + footprint[n / 2]) * 0.5f;
        float bestDist = -1f;
        for (int i = 1; i < n; i++)
        {
            Vector2 mid = (footprint[i] + footprint[(i + 1) % n]) * 0.5f;
            float dist = PolylineUtils.DistanceToSegment(mid, frontA, frontB);
            if (dist > bestDist)
            {
                bestDist = dist;
                best = mid;
            }
        }
        return best;
    }

    /// <summary>
    /// For rectangles, return short-edge midpoint (lower of the two edge indices; edge 0 for squares). Edge index is its start vertex index.
    /// Remove duplicate/intermediate edge vertices before corner classification. Same rules as backend initial terminal placement
    /// (crud_agents._rectangle_short_side_midpoint); do not change only one side.
    /// </summary>
    private static bool TryGetRectangleShortSideMidpoint(Vector2[] ring, out Vector2 midpoint)
    {
        midpoint = default;
        if (ring == null) return false;
        var points = new List<Vector2>(ring.Length);
        foreach (Vector2 p in ring)
            if (points.Count == 0 || Vector2.Distance(points[points.Count - 1], p) > 1e-4f) points.Add(p);
        if (points.Count > 1 && Vector2.Distance(points[0], points[points.Count - 1]) <= 1e-4f) points.RemoveAt(points.Count - 1);
        if (points.Count < 4) return false;

        // Absolute sin and cos of direction change at vertex b.
        void Turn(Vector2 a, Vector2 b, Vector2 c, out float sine, out float cosine)
        {
            Vector2 d0 = b - a, d1 = c - b;
            float norm = d0.magnitude * d1.magnitude;
            sine = Mathf.Abs(d0.x * d1.y - d0.y * d1.x) / norm;
            cosine = Vector2.Dot(d0, d1) / norm;
        }
        var corners = new List<Vector2>(4);
        for (int i = 0; i < points.Count; i++)
        {
            Turn(points[(i + points.Count - 1) % points.Count], points[i], points[(i + 1) % points.Count], out float sine, out float cosine);
            if (cosine < 0f || sine >= RECTANGLE_ANGLE_TOLERANCE) corners.Add(points[i]);
        }
        if (corners.Count != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            Turn(corners[(i + 3) % 4], corners[i], corners[(i + 1) % 4], out _, out float cosine);
            if (Mathf.Abs(cosine) >= RECTANGLE_ANGLE_TOLERANCE) return false;
        }
        float Length(int i) => Vector2.Distance(corners[i], corners[(i + 1) % 4]);
        int side = Length(1) + Length(3) < Length(0) + Length(2) ? 1 : 0;
        midpoint = (corners[side] + corners[side + 1]) * 0.5f;
        return true;
    }

    /// <summary>Transform parcel polygon to local XZ relative to center_point, preserving vertex indices (null if no parcel).</summary>
    private static Vector2[] ToLocalRing(ParcelSummary parcel, Vector3 centerPointWorld)
    {
        float[][] polygon = parcel?.GetPolygonArray();
        if (polygon == null || polygon.Length < 3) return null;
        var ring = new Vector2[polygon.Length];
        for (int i = 0; i < polygon.Length; i++)
            ring[i] = new Vector2(polygon[i][0] - centerPointWorld.x, polygon[i][1] - centerPointWorld.z);
        return ring;
    }

    /// <summary>
    /// Raise internal-equipment vertices (tracks/wires/posts/stops/shed) to terrain elevation at their horizontal positions.
    /// Preserve relative heights (rail surface/wire/shed eaves), keeping posts/walls vertical while following slopes.
    /// Straight sections share meshes with placement matrices; combine per material before moving vertices.
    /// </summary>
    private static void ConformToTerrain(TerrainOffsetSampler terrain,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        var groupMaterials = new List<Material>();
        var groups = new List<List<CombineInstance>>();
        for (int i = 0; i < combineInstances.Count; i++)
        {
            int group = groupMaterials.IndexOf(materials[i]);
            if (group < 0)
            {
                group = groups.Count;
                groupMaterials.Add(materials[i]);
                groups.Add(new List<CombineInstance>());
            }
            groups[group].Add(combineInstances[i]);
        }
        combineInstances.Clear();
        materials.Clear();
        for (int g = 0; g < groups.Count; g++)
        {
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(groups[g].ToArray(), true, true);
            Vector3[] vertices = mesh.vertices;
            for (int v = 0; v < vertices.Length; v++)
                vertices[v].y += terrain.Offset(vertices[v].x, vertices[v].z);
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
            temporaryMeshes.Add(mesh);
            combineInstances.Add(new CombineInstance { mesh = mesh, subMeshIndex = 0, transform = Matrix4x4.identity });
            materials.Add(groupMaterials[g]);
        }
    }

    /// <summary>
    /// Return terrain elevation minus center_point terrain elevation at center-relative local XZ.
    /// Sample only terrain-grid vertices; interpolate on grid triangles (diagonal from XZ minimum to maximum),
    /// matching terrain-following ground panels (BuildingMeshBuilder.GenerateTerrainSurfaceMesh).
    /// Far fewer samples than per-vertex terrain lookups (track tiles have many vertices).
    /// Use 1m-grid interpolation while terrain is unloaded; rebuild the building when terrain arrives.
    /// </summary>
    private sealed class TerrainOffsetSampler
    {
        private readonly Vector3 center;
        private readonly Vector2 origin;
        private readonly Vector2 spacing;
        private readonly Dictionary<Vector2Int, float> heights = new Dictionary<Vector2Int, float>();

        public TerrainOffsetSampler(Vector3 center)
        {
            this.center = center;
            if (!Utils.TryGetTerrainGrid(center, out origin, out spacing))
            {
                origin = Vector2.zero;
                spacing = Vector2.one;
            }
        }

        public float Offset(float localX, float localZ)
        {
            float gx = (localX + center.x - origin.x) / spacing.x;
            float gz = (localZ + center.z - origin.y) / spacing.y;
            int x = Mathf.FloorToInt(gx), z = Mathf.FloorToInt(gz);
            float fx = gx - x, fz = gz - z;
            float h00 = Height(x, z), h11 = Height(x + 1, z + 1);
            float h = fx >= fz
                ? h00 + (Height(x + 1, z) - h00) * fx + (h11 - Height(x + 1, z)) * fz
                : h00 + (Height(x, z + 1) - h00) * fz + (h11 - Height(x, z + 1)) * fx;
            return h - center.y;
        }

        private float Height(int x, int z)
        {
            var key = new Vector2Int(x, z);
            if (!heights.TryGetValue(key, out float h))
            {
                h = Utils.GetGroundHeight(new Vector3(origin.x + x * spacing.x, 1000f, origin.y + z * spacing.y));
                heights.Add(key, h);
            }
            return h;
        }
    }

    /// <summary>
    /// Derive terminal placement/lead rendering. Terminal position follows the running network of line types using
    /// the depot's vehicle goods (agent_master.supply). Networks running both weld-requiring tracks and streets (LRT) use front-edge terminals
    /// (same test as backend transit_route.depot_terminal_on_frontage).
    /// If connected track paths have at least two tracks, leads are double track with matching spacing
    /// (network alone is insufficient because single/double tracks coexist). With no connected tracks (unconnected/unloaded),
    /// draw using the network's minimum track count (single if available; double for LRT networks with double tracks only).
    /// Without master data (e.g. test scenes), use railway-depot rendering.
    /// </summary>
    private static YardProfile ResolveProfile(BuildingSummary building)
    {
        var profile = new YardProfile();
        MasterDataStore store = MasterDataStore.Instance;
        if (store == null || !store.GetAgentMasterDictionary().TryGetValue(building.variant, out var agent) || agent.supply == null)
            return profile;
        var paths = store.GetPathMasterDictionary();
        bool guided = false, road = false;
        float networkLead = float.MaxValue;
        foreach (var transit in store.GetTransitMasterDictionary().Values)
        {
            if (transit.vehicle != agent.supply) continue;
            foreach (List<int> variants in new[] { transit.route_path_variants, transit.stop_path_variants })
            {
                if (variants == null) continue;
                foreach (int variant in variants)
                {
                    if (!paths.TryGetValue(variant, out PathMasterItem path)) continue;
                    bool track = TrackIntersectionMeshGenerator.IsTrackType(path.type);
                    guided |= track;
                    road |= path.type == Constants.PATH_STREET || path.type == Constants.PATH_WALKWAY;
                    if (track) networkLead = Mathf.Min(networkLead, LeadHalfSpacing(path.width, path.lanes ?? 1, path.center_width ?? 0f));
                }
            }
        }
        profile.TerminalOnFrontage = guided && road;
        profile.LeadHalfSpacing = ConnectedLeadHalfSpacing(building) ?? (networkLead < float.MaxValue ? networkLead : 0f);
        return profile;
    }

    /// <summary>
    /// Lead half track-center spacing (zero for single track). Connected-track changes affect appearance without changing building version,
    /// so BuildingManager includes this in the reuse key.
    /// </summary>
    public static float ResolveLeadHalfSpacing(BuildingSummary building)
    {
        return ResolveProfile(building).LeadHalfSpacing;
    }

    /// <summary>Lead half-spacing determined by connected track paths. Null if none connect.</summary>
    private static float? ConnectedLeadHalfSpacing(BuildingSummary building)
    {
        int nodeId = building.depot_node_id > 0 ? building.depot_node_id : building.infra_node_id;
        var connected = nodeId > 0 ? TrackPath.ConnectedPathsProvider?.Invoke(nodeId) : null;
        if (connected == null) return null;
        float? spacing = null;
        foreach (PathSummary path in connected)
        {
            if (!TrackIntersectionMeshGenerator.IsTrackType(path.path_type)) continue;
            TrackPath.GetTrackMasterParams(path, out float width, out int lanes, out _, out _, out _, out _, out _, out _);
            spacing = Mathf.Max(spacing ?? 0f, LeadHalfSpacing(width, lanes, TrackPath.GetTrackCenterWidth(path)));
        }
        return spacing;
    }

    /// <summary>Half track-center spacing for paths with at least two tracks (includes central gap at island-platform stations). Zero for single track.</summary>
    private static float LeadHalfSpacing(float width, int lanes, float centerWidth)
    {
        return lanes >= 2 ? Mathf.Abs(TrackPath.GetTrackCenterOffset(width, lanes, 0, centerWidth)) : 0f;
    }

    /// <summary>
    /// Plan the second double-track lead. Bend directly from the terminal's other track position,
    /// merging into the ladder's straight section via SECOND_LEAD_RUN crossover. False if merge lies outside that straight section
    /// or the track cannot fit the site (only the ladder lead enters the terminal).
    /// </summary>
    private static bool TryPlanSecondLead(Vector2[] uv, List<TrackRow> rows, Vector2 node, Vector2 trunkNode,
        out Vector2 end, out Vector2 merge)
    {
        end = node + (node - trunkNode);
        merge = new Vector2(end.x - SECOND_LEAD_RUN, trunkNode.y);
        int trunk = rows.FindIndex(row => row.Side == 0);
        if (trunk < 0 || merge.x < rows[trunk].StraightStart) return false;
        Vector2 handle = Vector2.right * (SECOND_LEAD_RUN * .4f);
        for (int k = 0; k <= 64; k++)
        {
            float t = k / 64f;
            if (!PolygonUtils.Contains(uv, PolylineUtils.CubicBezierPoint(end, end - handle, merge + handle, merge, t), BOUNDARY_TOLERANCE))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Reserve the straight line through the connection first, then add sidings to left/right ladder tracks.
    /// Omit only rows outside boundaries, avoiding rejection of the whole facility when terminals move.
    /// </summary>
    private static List<TrackRow> PlanRows(Vector2[] uv, Vector2 node, float center, int count)
    {
        var rows = new List<TrackRow>();
        if (!TryGetRowStart(uv, node.y, node.x, out float mainStart)) return rows;
        if (node.x - mainStart < .5f) return rows;
        rows.Add(new TrackRow { V=node.y, StraightStart=mainStart, StraightEnd=node.x,
            Target=node, Direction=Vector2.right, Side=0 });
        float minV=float.MaxValue, maxV=float.MinValue;
        foreach (Vector2 point in uv) { minV=Mathf.Min(minV,point.y); maxV=Mathf.Max(maxV,point.y); }
        for (int rank=1; rank<MAX_TRACKS && rows.Count<count; rank++)
        {
            foreach (int side in new[] { -1, 1 })
            {
                if (rows.Count>=count) break;
                float v=node.y+side*rank*TRACK_SPACING;
                if (v<minV+SIDE_MARGIN || v>maxV-SIDE_MARGIN) continue;
                // Start both sides' branches at the terminal; accept only tracks fitting the site.
                Vector2 merge=node;
                Vector2 spineEnd=merge+new Vector2(-8f,side*2f);
                float distance=rank*TRACK_SPACING-ENTRY_OVERLAP/LADDER_RUN_PER_LATERAL;
                Vector2 target=spineEnd+new Vector2(-(distance-2f)*LADDER_RUN_PER_LATERAL,side*(distance-2f));
                Vector2 direction=new Vector2(LADDER_RUN_PER_LATERAL,-side).normalized;
                float end=target.x-ENTRY_OVERLAP-ENTRY_APPROACH;
                if (!TryGetRowStart(uv,v,end,out float start) || end-start<MIN_STRAIGHT_LENGTH) continue;
                Vector2 p0=new Vector2(end,v);
                bool fits=true;
                for (int k=0; k<=64; k++)
                {
                    float t=k/64f;
                    if (!PolygonUtils.Contains(uv,PolylineUtils.CubicBezierPoint(p0,p0+Vector2.right*((target.x-end)*.4f),target-direction*ENTRY_OVERLAP,target,t),BOUNDARY_TOLERANCE)
                        || !PolygonUtils.Contains(uv,Vector2.Lerp(target,spineEnd,t),BOUNDARY_TOLERANCE)
                        || !PolygonUtils.Contains(uv,PolylineUtils.CubicBezierPoint(spineEnd,spineEnd+direction*3f,merge-Vector2.right*3f,merge,t),BOUNDARY_TOLERANCE))
                    { fits=false; break; }
                }
                if (fits) rows.Add(new TrackRow { V=v,StraightStart=start,StraightEnd=end,Target=target,
                    Direction=direction,Side=side,MergePoint=merge });
            }
        }
        foreach (int side in new[] { -1, 1 })
        {
            int outer=-1;
            for (int i=0; i<rows.Count; i++)
                if (rows[i].Side==side && (outer<0 || rows[i].Target.x<rows[outer].Target.x)) outer=i;
            if (outer>=0) { TrackRow row=rows[outer]; row.IsSpine=true; rows[outer]=row; }
        }
        rows.Sort((a,b)=>a.V.CompareTo(b.V));
        return rows;
    }

    /// <summary>Choose the in-site interval containing the row endpoint. Avoid crossing another interval in concave parcels.</summary>
    private static bool TryGetRowStart(Vector2[] polygon,float v,float end,out float start)
    {
        start=0f;
        // Boundary horizontal edges escape half-open intersection tests; also try tiny offsets on both sides.
        foreach (float offset in new[] { 0f,-.001f,.001f })
        {
            float sampleV=v+offset;
            var hits=new List<float>();
            for (int i=0; i<polygon.Length; i++)
            {
                Vector2 a=polygon[i],b=polygon[(i+1)%polygon.Length];
                if ((a.y<=sampleV && b.y>sampleV)||(b.y<=sampleV && a.y>sampleV))
                    hits.Add(Mathf.Lerp(a.x,b.x,(sampleV-a.y)/(b.y-a.y)));
            }
            hits.Sort();
            for (int i=0; i+1<hits.Count; i+=2)
                if (end>=hits[i] && end<=hits[i+1]+.01f)
                {
                    start=hits[i]+END_MARGIN;
                    return true;
                }
        }
        return false;
    }

    /// <summary>First site-boundary crossing of the lead extending from terminal along track direction.</summary>
    private static Vector2 ResolveFenceOpening(Vector2[] polygon, Vector2 node, Vector2 direction)
    {
        float Cross(Vector2 a,Vector2 b) => a.x*b.y-a.y*b.x;
        float nearest=float.MaxValue;
        for (int i=0; i<polygon.Length; i++)
        {
            Vector2 a=polygon[i], edge=polygon[(i+1)%polygon.Length]-a;
            float denominator=Cross(direction,edge);
            if (Mathf.Abs(denominator)<.00001f) continue;
            float t=Cross(a-node,edge)/denominator;
            float u=Cross(a-node,direction)/denominator;
            if (t>=-.01f && u>=0f && u<=1f) nearest=Mathf.Min(nearest,Mathf.Max(0f,t));
        }
        return nearest<float.MaxValue ? node+direction*nearest : node;
    }

    /// <summary>Bend tile vertices along arc length. Neighboring tile boundaries use identical positions/tangents.</summary>
    private static void PlaceTilesAlongBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
        Mesh trackMesh, Material[] trackMats, Mesh wireMesh, Material[] wireMats,
        float tileLength, float tileMinZ, float wireMinZ,
        System.Func<Vector2, Vector3> toLocal3, System.Func<Vector2, Vector3> toDir3,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes,
        bool joinsTrack)
    {
        const int samples = 96;
        float[] lengths = new float[samples + 1];
        Vector2 prev = p0;
        for (int i = 1; i <= samples; i++)
        {
            Vector2 point = PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, i / (float)samples);
            lengths[i] = lengths[i - 1] + Vector2.Distance(prev, point);
            prev = point;
        }
        float total = lengths[samples];
        if (total < .01f) return;
        int tiles = Mathf.Max(1, Mathf.RoundToInt(total / tileLength));
        float step = total / tiles;
        float Parameter(float distance)
        {
            int i = System.Array.BinarySearch(lengths, Mathf.Clamp(distance, 0f, total));
            if (i >= 0) return i / (float)samples;
            i = Mathf.Clamp(~i, 1, samples);
            return (i - 1 + (distance - lengths[i - 1]) / (lengths[i] - lengths[i - 1])) / samples;
        }
        void Bend(Mesh source, Material[] mats, float minZ, int tile, bool isTrack)
        {
            if (source == null) return;
            Mesh mesh = Object.Instantiate(source);
            Vector3[] vertices = source.vertices;
            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 local = vertices[v];
                float t = Parameter((tile + (local.z - minZ) / source.bounds.size.z) * step);
                Vector2 at = PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, t);
                Vector2 tangent = 3f * (1f - t) * (1f - t) * (p1 - p0)
                    + 6f * (1f - t) * t * (p2 - p1) + 3f * t * t * (p3 - p2);
                Vector3 right = Vector3.Cross(Vector3.up, toDir3(tangent));
                vertices[v] = toLocal3(at) + right * local.x + Vector3.up * local.y;
            }
            mesh.vertices = vertices;
            // Trim at target support-material outer edge, joining branch track bed/sleepers without gaps.
            if (joinsTrack && isTrack)
            {
                var clippedVertices = new List<Vector3>(vertices);
                var uvs = new List<Vector2>(source.uv);
                while (uvs.Count < vertices.Length) uvs.Add(Vector2.zero);
                Vector2 tangent = (p3 - p2).normalized;
                Vector2 outward = new Vector2(-tangent.y, tangent.x);
                outward *= Mathf.Sign(Vector2.Dot(p0 - p3, outward));
                Vector3 normal = toDir3(outward);
                Vector3 origin = toLocal3(p3);
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    string name = mats[sub] != null ? mats[sub].name : "";
                    float edge = name.StartsWith("PATH_ROADBED") ? 1.55f
                        : (name.StartsWith("PATH_CONCRETE") || name.StartsWith("PATH_RAIL_METAL")) ? 1.3f : 0f;
                    // Thin single beams lack normal sleeper width. Connect to the submesh's actual cross-section.
                    if (edge > 0f && source.bounds.size.x < 2f)
                    {
                        edge = 0f;
                        Vector3[] authoredVertices = source.vertices;
                        foreach (int index in source.GetTriangles(sub))
                            edge = Mathf.Max(edge, Mathf.Abs(authoredVertices[index].x));
                    }
                    if (edge == 0f) continue;
                    int[] indices = source.GetTriangles(sub);
                    var triangles = new List<int>();
                    for (int f = 0; f < indices.Length; f += 3)
                    {
                        var polygon = new List<int>(4);
                        for (int k = 0; k < 3; k++)
                        {
                            int a = indices[f + k], b = indices[f + (k + 1) % 3];
                            float da = Vector3.Dot(vertices[a] - origin, normal) - edge;
                            float db = Vector3.Dot(vertices[b] - origin, normal) - edge;
                            if (da >= 0f) polygon.Add(a);
                            if ((da >= 0f) == (db >= 0f)) continue;
                            float ratio = da / (da - db);
                            polygon.Add(clippedVertices.Count);
                            clippedVertices.Add(Vector3.Lerp(vertices[a], vertices[b], ratio));
                            uvs.Add(Vector2.Lerp(uvs[a], uvs[b], ratio));
                        }
                        for (int k = 1; k + 1 < polygon.Count; k++)
                        {
                            triangles.Add(polygon[0]); triangles.Add(polygon[k]); triangles.Add(polygon[k + 1]);
                        }
                    }
                    mesh.SetVertices(clippedVertices);
                    mesh.SetUVs(0, uvs);
                    mesh.SetTriangles(triangles, sub);
                }
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            temporaryMeshes.Add(mesh);
            AddInstances(mesh, mats, Matrix4x4.identity, combineInstances, materials);
        }
        for (int tile = 0; tile < tiles; tile++)
        {
            Bend(trackMesh, trackMats, tileMinZ, tile, true);
            Bend(wireMesh, wireMats, wireMinZ, tile, false);
        }
    }

    /// <summary>
    /// Load part prefabs as combined meshes. Use <see cref="BuildingMeshUtils.CombineModuleMeshes"/>
    /// to include every object in multi-object FBXs.
    /// Null if absent (optional parts are skipped).
    /// </summary>
    private static Mesh LoadPartMesh(string prefix, string partName, out Material[] partMaterials, List<Mesh> temporaryMeshes)
    {
        partMaterials = null;
        GameObject prefab = BuildingMeshUtils.GetPartNoPosition(prefix, partName, 0, true);
        if (prefab == null)
        {
            return null;
        }
        Mesh mesh = BuildingMeshUtils.CombineModuleMeshes(prefab, out partMaterials);
        if (mesh != null)
        {
            temporaryMeshes.Add(mesh);
        }
        return mesh;
    }

    /// <summary>
    /// Add every submesh with the specified matrix to CombineInstance lists.
    /// Share mesh instances across placements, varying only matrices (same as PlacePortCranes).
    /// </summary>
    private static void AddInstances(Mesh mesh, Material[] subMeshMaterials, Matrix4x4 matrix,
        List<CombineInstance> combineInstances, List<Material> materials)
    {
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = mesh,
                subMeshIndex = s,
                transform = matrix
            });
            Material mat = (subMeshMaterials != null && subMeshMaterials.Length > 0)
                ? subMeshMaterials[Mathf.Min(s, subMeshMaterials.Length - 1)]
                : null;
            materials.Add(mat);
        }
    }
}
