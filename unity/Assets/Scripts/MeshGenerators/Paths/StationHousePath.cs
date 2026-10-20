using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>
/// Station-house path (PATH_STATION_HOUSE, model_type="station_house") mesh generator.
///
/// Stations are waypoint polylines rendered in two elements:
///   1. Passage: passage_mode selects overhead/ground/underground. Underground has no rendering/strip collider. Set each anchor to terrain elevation directly below XZ;
///      join anchors with straight lines (no terrain-following subdivision). Deck clearance/piers are body-prefab
///      geometry (origin=ground; actual-size placement without width stretching).
///   2. Buildings: place station models (Models/Stations/{variant}/body) in selected terminal exclusive rectangles,
///      covering rectangles with ground panels (floor-part material). outside rectangles extend outward from terminals;
///      inside shifts their outer edges to track outer edges. house_offset is shared. Model origin sits at
///      rectangle inner-edge midpoint (modelOrigin, same as path endpoint without sliding)
///      because prefab geometry supplies depth/lateral offset. Extend girders to that position too.
///
/// Calculate exclusive rectangles with the same deterministic algorithm as backend geo_station_house.py
/// (ComputeEndFootprints). Keep both implementations consistent.
///
/// Picking uses PathObject (path details) like other paths; station=station-house path,
/// with no dedicated station window (UI path-details panel shows station sections).
/// Return bridge strip collider as PathMeshResult.Mesh; PathManager attaches the standard
/// collider/tag/handler to pathGO. Building colliders are on children,
/// so also give them PathObject tag, PathInputHandler, and ObjectIdInfo
/// (InputHandler checks the hit collider's own GameObject).
/// </summary>
public class StationHousePath : IPathMeshGenerator
{
    private const string PATH_TAG = "PathObject";

    // Placeholder box height if building prefab is missing (m). Only placeholders may scale; prefabs use actual dimensions.
    private const float FALLBACK_HOUSE_HEIGHT = 8f;

    // Deduplicate missing-building-prefab warnings to once per variant.
    private static readonly HashSet<int> missingHousePrefabWarnedVariants = new HashSet<int>();

    /// <summary>
    /// Exclusive-area rectangle (building footprint). center/direction lie in XZ (Vector2.y=z).
    ///
    /// center includes lateral house_offset (= occupied land position).
    /// modelOrigin is inner-edge midpoint; place building-prefab origin there.
    /// Without track-avoidance sliding, modelOrigin equals the station-house path endpoint;
    /// with sliding, extend footbridge to it. Prefab geometry supplies depth/lateral offset,
    /// so code must not recenter it or add house_offset.
    /// </summary>
    public struct StationHouseFootprint
    {
        public bool atEnd;
        public bool inside;
        public Vector2 center;
        public Vector2 modelOrigin;
        public Vector2 direction;
        public float rotationDeg;
        public float width;
        public float depth;
    }

    public PathMeshResult Generate(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        if (points == null || points.Count < 2)
        {
            Debug.LogWarning($"[StationHousePath] ポイント不足 ({points?.Count ?? 0}件)。生成スキップ。");
            return new PathMeshResult(null, false);
        }

        RoadPath.GetPathMasterParams(pathData, out float width, out _, out _, out _, out _, out _, out _,
            out _, out _, out _, out bool singleSpan);

        GameObject container = new GameObject($"StationHouse_{pathData.path_id}");

        List<StationHouseFootprint> footprints = ComputeEndFootprints(pathData);

        // Underground passages have no surface rendering/picking plane; select stations via buildings.
        Mesh bridgeColliderMesh = null;
        if (pathData.passage_mode != "underground")
        {
            List<Vector3> anchors = BuildBridgeAnchorPoints(pathData, points, footprints);
            SplineComputer spline = RoadPath.BuildSpline(container, anchors, false);
            bool ground = pathData.passage_mode == "ground";
            string part = ground ? "passage_ground" : "passage_elevated";
            string bodyPath = $"Models/Paths/p{pathData.path_variant}/{part}";
            string bodyFallbackPath = $"Models/Paths/p{Constants.PATH_STATION_HOUSE}/{part}";

            // Use existing dedicated passages for supported modes; resolve other combinations to shared parts.
            bool existingBody = ground
                ? pathData.path_variant == 2004 || pathData.path_variant == 2007
                : pathData.path_variant == 2002 || pathData.path_variant == 2005;
            if (existingBody) bodyPath = $"Models/Paths/p{pathData.path_variant}/body";
            RoadPath.CreateSplineMesh(container, spline, bodyPath, width,
                0f, RoadPartHeights.STATION_BRIDGE_BASE_ABOVE_SPLINE, false, bodyFallbackPath,
                stretchToWidth: false, singleSpan: singleSpan, maxTileLength: 0f, normalizeBodySurface: false);

            bridgeColliderMesh = RoadPath.BuildColliderMeshFromSpline(spline, width);
            if (bridgeColliderMesh != null)
            {
                OffsetMeshY(bridgeColliderMesh, GetBodyTopHeight(bodyPath, bodyFallbackPath));
                bridgeColliderMesh.name = $"StationHouseBridgeCollider_{pathData.path_id}";
            }
        }

        // ---- Buildings at selected terminals ----
        foreach (var footprint in footprints)
        {
            CreateHouseObject(container.transform, pathData, footprint);
        }

        return new PathMeshResult(bridgeColliderMesh, true, container);
    }

    // =====================================================================
    // Footbridge.
    // =====================================================================

    /// <summary>
    /// Build footbridge anchors (start/waypoints/end) at terrain elevation
    /// directly below their XZ; join straight (no terrain-following subdivision).
    /// Prefab body geometry supplies deck clearance, so do not raise anchors.
    /// For unavailable anchors (degenerate data), ground the supplied points by the same rule.
    ///
    /// If exclusive areas slide outward to avoid tracks, extend the bridge to building attachment
    /// (footprint.modelOrigin). inside moves the passage end to the same attachment.
    /// </summary>
    private static List<Vector3> BuildBridgeAnchorPoints(PathSummary pathData, List<Vector3> points, List<StationHouseFootprint> footprints)
    {
        var anchors = new List<Vector3>();

        var startList = pathData.GetStartPointList();
        if (startList != null && startList.Count >= 2)
        {
            anchors.Add(MakeDeckPoint(startList[0], startList[startList.Count - 1]));
        }

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                if (wp.position == null || wp.position.Length < 2) continue;
                anchors.Add(MakeDeckPoint(wp.position[0], wp.position[wp.position.Length - 1]));
            }
        }

        var endList = pathData.GetEndPointList();
        if (endList != null && endList.Count >= 2)
        {
            anchors.Add(MakeDeckPoint(endList[0], endList[endList.Count - 1]));
        }

        if (anchors.Count < 2)
        {
            anchors.Clear();
            foreach (var p in points)
            {
                anchors.Add(MakeDeckPoint(p.x, p.z));
            }
        }

        // Preserve endpoint identity even with only one side. Inside placement shortens passages to building attachments.
        if (anchors.Count >= 2 && footprints != null)
        {
            foreach (var footprint in footprints)
            {
                int index = footprint.atEnd ? anchors.Count - 1 : 0;
                Vector2 origin = footprint.modelOrigin;
                Vector3 point = MakeDeckPoint(origin.x, origin.y);
                if (footprint.inside) anchors[index] = point;
                else if (!IsSameXZ(anchors[index], origin))
                {
                    if (footprint.atEnd) anchors.Add(point);
                    else anchors.Insert(0, point);
                }
            }
        }
        return anchors;
    }

    private static bool IsSameXZ(Vector3 anchor, Vector2 xz)
    {
        return Mathf.Abs(anchor.x - xz.x) < 1e-3f && Mathf.Abs(anchor.z - xz.y) < 1e-3f;
    }

    private static Vector3 MakeDeckPoint(float x, float z)
    {
        float groundY = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        return new Vector3(x, groundY + RoadPartHeights.ROAD_SPLINE_OFFSET, z);
    }

    /// <summary>
    /// Return body-prefab mesh top (local maximum Y). Prefab geometry supplies girder height;
    /// use this to raise colliders to deck height. Zero if absent
    /// (fallback white strips render on the spline plane).
    /// </summary>
    private static float GetBodyTopHeight(string bodyPath, string fallbackPath)
    {
        if (ModelManager.Instance == null) return 0f;

        GameObject prefab = ModelManager.Instance.GetModel(bodyPath, suppressWarning: true)
            ?? ModelManager.Instance.GetModel(fallbackPath, suppressWarning: true);
        var mf = prefab != null ? prefab.GetComponent<MeshFilter>() : null;
        if (mf == null || mf.sharedMesh == null) return 0f;
        return Mathf.Max(0f, mf.sharedMesh.bounds.max.y);
    }

    /// <summary>Translate all mesh vertices along Y.</summary>
    private static void OffsetMeshY(Mesh mesh, float offsetY)
    {
        if (mesh == null || Mathf.Approximately(offsetY, 0f)) return;

        Vector3[] vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].y += offsetY;
        }
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
    }

    // =====================================================================
    // Buildings.
    // =====================================================================

    /// <summary>
    /// Place buildings. Resolve Models/Stations/{variant}/body → Models/Stations/body;
    /// never scale prefabs (actual-size building-part convention). If absent, use placeholder boxes
    /// (house_width × FALLBACK_HOUSE_HEIGHT × house_depth; only placeholders may scale).
    ///
    /// Prefab positions use modelOrigin (inner-edge midpoint, equal to station-house path endpoint
    /// without sliding); placeholders use rectangle center. Prefab geometry supplies depth
    /// and lateral offset, so place origins directly. Placeholders visualize rectangle dimensions
    /// and therefore sit at rectangle positions.
    /// Each Y rests on local terrain elevation. Ground panels cover the exclusive rectangles.
    /// </summary>
    private static void CreateHouseObject(Transform parent, PathSummary pathData, StationHouseFootprint footprint)
    {
        CreateFootprintFloor(parent, pathData, footprint);

        GameObject prefab = ResolveStationPart(pathData.path_variant, "body");

        GameObject go;
        Vector2 position;
        if (prefab != null)
        {
            go = Object.Instantiate(prefab, parent);
            position = footprint.modelOrigin;
        }
        else
        {
            WarnMissingHousePrefabOnce(pathData.path_variant);
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(footprint.width, FALLBACK_HOUSE_HEIGHT, footprint.depth);
            position = footprint.center;
        }
        go.name = "House";

        float groundY = Utils.GetGroundHeight(new Vector3(position.x, 1000f, position.y));
        go.transform.localPosition = new Vector3(position.x, groundY, position.y);
        go.transform.localRotation = Quaternion.Euler(0f, footprint.rotationDeg, 0f);
        go.SetActive(true);

        EnsureHouseCollider(go);

        // Add the three path-detail picking components to every collider-bearing object
        // (InputHandler checks the hit collider's own tag/handler).
        foreach (var col in go.GetComponentsInChildren<Collider>())
        {
            SetupPathClick(col.gameObject, pathData);
        }
    }

    /// <summary>
    /// Resolve station parts from Models/Stations/{variant}/{partName} → Models/Stations/{partName}.
    /// Variant-specific folders take priority; otherwise use shared parts directly under
    /// Stations. Null if absent from both.
    /// </summary>
    private static GameObject ResolveStationPart(int pathVariant, string partName)
    {
        if (ModelManager.Instance == null) return null;

        return ModelManager.Instance.GetModel($"Models/Stations/{pathVariant}/{partName}", suppressWarning: true)
            ?? ModelManager.Instance.GetModel($"Models/Stations/{partName}", suppressWarning: true);
    }

    /// <summary>
    /// Cover exclusive rectangles with ground panels, like building ground panels
    /// (F_建物メッシュの生成). floor parts supply only material;
    /// generate surface geometry from polygons. Omit drawing if no floor part exists anywhere
    /// (no default-material fallback, avoiding unwanted slabs).
    /// Always follow terrain (rectangles span tens of meters; horizontal panels would sink/float on slopes).
    /// </summary>
    private static void CreateFootprintFloor(Transform parent, PathSummary pathData, StationHouseFootprint footprint)
    {
        GameObject floorPart = ResolveStationPart(pathData.path_variant, "floor");
        if (floorPart == null) return;

        Material[] materials = BuildingMeshUtils.GetModuleMaterials(floorPart);
        if (materials == null || materials.Length == 0 || materials[0] == null) return;

        // Build rectangle corners in unrotated local XZ with center as origin
        // (same convention as ModularBodyBuilder ground panels). CCW order,
        // since ear clipping/panel winding reversal assume CCW input; CW would face downward.
        Vector2 dir = footprint.direction;
        Vector2 halfDepth = dir * (footprint.depth / 2f);
        Vector2 halfWidth = RightAxis(dir) * (footprint.width / 2f);
        var polygon = new Vector2[]
        {
            halfWidth - halfDepth,
            halfWidth + halfDepth,
            -halfWidth + halfDepth,
            -halfWidth - halfDepth,
        };

        float groundY = Utils.GetGroundHeight(new Vector3(footprint.center.x, 1000f, footprint.center.y));
        Vector3 centerWorld = new Vector3(footprint.center.x, groundY, footprint.center.y);

        Mesh mesh = ModularBodyBuilder.GenerateTerrainConformingFloorMesh(polygon, centerWorld);
        if (mesh == null) return;
        mesh.name = $"StationHouseFloor_{pathData.path_id}";

        GameObject go = new GameObject("Floor");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = centerWorld;
        go.AddComponent<MeshFilter>().mesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = materials[0];
    }

    /// <summary>
    /// Ensure picking colliders. If prefabs lack them, add BoxCollider from combined renderer bounds
    /// (Y-only rotation approximation is sufficient).
    /// </summary>
    private static void EnsureHouseCollider(GameObject go)
    {
        if (go.GetComponentInChildren<Collider>() != null) return;

        var renderers = go.GetComponentsInChildren<Renderer>();
        var box = go.AddComponent<BoxCollider>();
        if (renderers.Length == 0) return;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }
        box.center = go.transform.InverseTransformPoint(bounds.center);
        box.size = go.transform.InverseTransformVector(bounds.size);
    }

    /// <summary>
    /// Add PathObject tag, PathInputHandler, and ObjectIdInfo for opening path details on click,
    /// matching PathManager's pathGO setup.
    /// </summary>
    private static void SetupPathClick(GameObject go, PathSummary pathData)
    {
        go.tag = PATH_TAG;

        var idInfo = go.GetComponent<ObjectIdInfo>();
        if (idInfo == null) idInfo = go.AddComponent<ObjectIdInfo>();
        idInfo.id = pathData.path_id;
        idInfo.status = pathData.status;
        idInfo.isFarObject = false;

        var handler = go.GetComponent<PathInputHandler>();
        if (handler == null) handler = go.AddComponent<PathInputHandler>();
        handler.pathId = pathData.path_id;
    }

    private static void WarnMissingHousePrefabOnce(int pathVariant)
    {
        if (missingHousePrefabWarnedVariants.Add(pathVariant))
        {
            Debug.LogWarning($"[StationHousePath] Prefab not found: Models/Stations/{pathVariant}/body (仮箱で代替表示します)");
        }
    }

    // =====================================================================
    // Exclusive-area calculation (ported from geo_station_house.py).
    // =====================================================================

    /// <summary>
    /// Return exclusive areas (building footprints) at selected station-house terminals, at most two in start/end order.
    ///
    /// Same algorithm as backend geo_station_house.station_house_end_rects:
    /// solve one-dimensional interval avoidance along s (outward terminal direction),
    /// deterministically finding minimum inner-edge position s_in avoiding nearby track-path occupancy.
    /// Rectangles extend outward from terminals (inner edge sits on terminal), so center lies at
    /// s_in + half depth, plus lateral house_offset (positive rightward facing outward).
    /// Also works for preview PathSummary (path_id=0); skip own path only when
    /// path_id matches.
    /// </summary>
    public static List<StationHouseFootprint> ComputeEndFootprints(PathSummary pathData)
    {
        var result = new List<StationHouseFootprint>();

        GetHouseFootprintParams(pathData.path_variant, out float houseWidth, out float houseDepth, out float houseOffset);
        List<Vector2> anchors = PathAnchorPoints2D(pathData);
        if (anchors.Count < 2) return result;

        var pathManager = Object.FindAnyObjectByType<PathManager>();

        for (int endIndex = 0; endIndex < 2; endIndex++)
        {
            bool atEnd = endIndex == 1;
            string ends = pathData.house_ends ?? "both";
            if (ends != "both" && ends != (atEnd ? "end" : "start")) continue;
            if (!TryGetEndpointOutwardDir(anchors, atEnd, out Vector2 dir)) continue;
            Vector2 tip = atEnd ? anchors[anchors.Count - 1] : anchors[0];

            List<Vector2> intervals = CollectOccupiedIntervals(pathManager, tip, dir, houseWidth, houseOffset, pathData.path_id);
            bool inside = MasterDataStore.Instance?.GetPathMaster(pathData.path_variant)?.house_placement == "inside";
            float sIn = ResolveHousePosition(intervals, houseDepth, inside);
            Vector2 innerCenter = tip + dir * sIn;

            result.Add(new StationHouseFootprint
            {
                atEnd = atEnd,
                inside = inside,
                center = innerCenter + dir * (houseDepth / 2f) + RightAxis(dir) * houseOffset,
                modelOrigin = innerCenter,
                direction = dir,
                rotationDeg = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg,
                width = houseWidth,
                depth = houseDepth,
            });
        }
        return result;
    }

    /// <summary>
    /// Right direction of outward dir in XZ, matching rotated building local +X via rotationDeg.
    /// Positive house_offset and rectangle-width direction.
    /// </summary>
    private static Vector2 RightAxis(Vector2 dir) => new Vector2(dir.y, -dir.x);

    /// <summary>
    /// Return station-variant exclusive-area parameters (width/depth/offset), m.
    /// width is perpendicular to terminal segment; depth is along it;
    /// offset shifts laterally (positive rightward facing outward).
    /// Missing master dimensions use defaults; missing offset uses zero
    /// (same as backend get_house_footprint_params).
    /// </summary>
    public static void GetHouseFootprintParams(int pathVariant, out float width, out float depth, out float offset)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(pathVariant) : null;
        width = (master != null && master.house_width.HasValue && master.house_width.Value > 0f)
            ? master.house_width.Value : Constants.STATION_HOUSE_DEFAULT_WIDTH;
        depth = (master != null && master.house_depth.HasValue && master.house_depth.Value > 0f)
            ? master.house_depth.Value : Constants.STATION_HOUSE_DEFAULT_DEPTH;
        offset = (master != null && master.house_offset.HasValue) ? master.house_offset.Value : 0f;
    }

    /// <summary>Return path anchors (start/waypoints/end) as a 2D XZ list.</summary>
    public static List<Vector2> PathAnchorPoints2D(PathSummary pathData)
    {
        var points = new List<Vector2>();

        var startList = pathData.GetStartPointList();
        if (startList != null && startList.Count >= 2)
        {
            points.Add(new Vector2(startList[0], startList[startList.Count - 1]));
        }
        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                if (wp.position == null || wp.position.Length < 2) continue;
                points.Add(new Vector2(wp.position[0], wp.position[wp.position.Length - 1]));
            }
        }
        var endList = pathData.GetEndPointList();
        if (endList != null && endList.Count >= 2)
        {
            points.Add(new Vector2(endList[0], endList[endList.Count - 1]));
        }
        return points;
    }

    /// <summary>
    /// Return terminal outward unit vector; false if degenerate (all points coincident).
    /// Skip consecutive endpoint duplicates until a nonzero distance determines direction.
    /// </summary>
    private static bool TryGetEndpointOutwardDir(List<Vector2> anchors, bool atEnd, out Vector2 dir)
    {
        dir = Vector2.zero;
        if (anchors.Count < 2) return false;

        Vector2 tip = atEnd ? anchors[anchors.Count - 1] : anchors[0];
        for (int i = 1; i < anchors.Count; i++)
        {
            Vector2 baseP = atEnd ? anchors[anchors.Count - 1 - i] : anchors[i];
            Vector2 d = tip - baseP;
            float norm = d.magnitude;
            if (norm > 1e-6f)
            {
                dir = d / norm;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Return left/right occupancy half-widths from track centerline (m). Add platform extensions
    /// (resolved by TrackPath.ResolvePlatformExtents) to track-corridor half-width,
    /// matching backend site polygons. Sides follow geometric start→end direction
    /// (same as backend _rail_side_extents).
    /// Single-track stations may have only one platform; adding platform_width on both sides
    /// would leave an unnecessary platform-width gap on the empty side (two-track stations have both).
    /// </summary>
    private static void RailSideExtents(PathSummary rail, out float extLeft, out float extRight)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(rail.path_variant) : null;
        float half = ((master != null && master.width > 0f) ? master.width : 5f) / 2f;
        float platformWidth = (master != null && master.platform_width.HasValue) ? master.platform_width.Value : 0f;
        TrackPath.ResolvePlatformExtents(rail, platformWidth, out float platformLeft, out float platformRight);
        extLeft = half + platformLeft;
        extRight = half + platformRight;
    }

    /// <summary>
    /// Return nearby track occupancy intervals on s (terminal outward direction).
    /// With tip as origin, direction=+s and its right=+lat, project track segments
    /// overlapping the exclusive-area width band onto s; expand by track occupancy
    /// and collect (x=s_min, y=s_max)
    /// (same as backend _occupied_intervals; segments supplied by PathManager spatial hash).
    ///
    /// Track occupancy is asymmetric (wider only on platform sides); distribute its extension
    /// to each axis from projections (cs, cr) of segment-right onto s/lat,
    /// as occupancy half-width × absolute component. Center the lateral corridor
    /// on the rectangle shifted by houseOffset.
    /// </summary>
    private static List<Vector2> CollectOccupiedIntervals(PathManager pathManager, Vector2 tip, Vector2 direction, float houseWidth, float houseOffset, int excludePathId)
    {
        var intervals = new List<Vector2>();
        if (pathManager == null) return intervals;

        Vector2 right = RightAxis(direction);
        float hw = houseWidth / 2f;
        float margin = Constants.STATION_HOUSE_TRACK_CLEARANCE_MARGIN;
        var segments = pathManager.QueryNearbySegments(
            new Vector3(tip.x, 0f, tip.y), Constants.STATION_HOUSE_TRACK_SEARCH_RADIUS);

        foreach (var seg in segments)
        {
            var rail = seg.path;
            if (rail == null) continue;
            if (rail.path_type != Constants.PATH_TRACK && rail.path_type != Constants.PATH_MONORAIL) continue;
            if (excludePathId != 0 && rail.path_id == excludePathId) continue;

            Vector2 u = new Vector2(seg.b.x - seg.a.x, seg.b.z - seg.a.z);
            if (u.magnitude < 1e-9f) continue;
            u.Normalize();

            RailSideExtents(rail, out float extLeft, out float extRight);
            // Track-segment right direction. Occupancy extends extLeft leftward, extRight rightward.
            Vector2 railRight = new Vector2(u.y, -u.x);
            float cs = railRight.x * direction.x + railRight.y * direction.y;
            float cr = railRight.x * right.x + railRight.y * right.y;
            float extSPlus = (cs >= 0f ? extRight : extLeft) * Mathf.Abs(cs) + margin;
            float extSMinus = (cs >= 0f ? extLeft : extRight) * Mathf.Abs(cs) + margin;
            float extLatPlus = (cr >= 0f ? extRight : extLeft) * Mathf.Abs(cr) + margin;
            float extLatMinus = (cr >= 0f ? extLeft : extRight) * Mathf.Abs(cr) + margin;

            float la = (seg.a.x - tip.x) * right.x + (seg.a.z - tip.y) * right.y - houseOffset;
            float lb = (seg.b.x - tip.x) * right.x + (seg.b.z - tip.y) * right.y - houseOffset;
            float sa = (seg.a.x - tip.x) * direction.x + (seg.a.z - tip.y) * direction.y;
            float sb = (seg.b.x - tip.x) * direction.x + (seg.b.z - tip.y) * direction.y;

            // Clip centerline to lat ∈ [lo, hi] where occupancy overlaps exclusive-area width.
            float lo = -hw - extLatPlus;
            float hi = hw + extLatMinus;
            if ((la < lo && lb < lo) || (la > hi && lb > hi)) continue;

            float t0 = 0f, t1 = 1f;
            float dl = lb - la;
            if (Mathf.Abs(dl) > 1e-9f)
            {
                float tLo = (lo - la) / dl;
                float tHi = (hi - la) / dl;
                t0 = Mathf.Max(t0, Mathf.Min(tLo, tHi));
                t1 = Mathf.Min(t1, Mathf.Max(tLo, tHi));
            }
            if (t0 > t1) continue;

            float s0 = sa + (sb - sa) * t0;
            float s1 = sa + (sb - sa) * t1;
            intervals.Add(new Vector2(Mathf.Min(s0, s1) - extSMinus, Mathf.Max(s0, s1) + extSPlus));
        }
        return intervals;
    }

    /// <summary>Merge intervals ascending (same as backend _merge_intervals).</summary>
    private static List<Vector2> MergeIntervals(List<Vector2> intervals)
    {
        var merged = new List<Vector2>();
        if (intervals.Count == 0) return merged;

        intervals.Sort((x, y) => x.x != y.x ? x.x.CompareTo(y.x) : x.y.CompareTo(y.y));
        merged.Add(intervals[0]);
        for (int i = 1; i < intervals.Count; i++)
        {
            Vector2 last = merged[merged.Count - 1];
            if (intervals[i].x <= last.y)
            {
                last.y = Mathf.Max(last.y, intervals[i].y);
                merged[merged.Count - 1] = last;
            }
            else
            {
                merged.Add(intervals[i]);
            }
        }
        return merged;
    }

    /// <summary>
    /// Return minimum inner-edge s_in avoiding track occupancy intervals.
    /// Rectangle occupies [s_in, s_in + houseDepth] (s_in=0 is initial placement with inner edge
    /// on terminal). Candidates are terminal itself (s_in=0) and each occupancy interval end;
    /// choose minimum nonoverlapping candidate (same as backend _resolve_slide_distance).
    /// </summary>
    public static float ResolveHousePosition(List<Vector2> intervals, float depth, bool inside)
    {
        if (!inside) return ResolveSlideDistance(intervals, depth);
        var merged = MergeIntervals(intervals);
        if (merged.Count == 0) return -depth;
        Vector2 nearest = merged[0];
        float distance = Mathf.Max(nearest.x, -nearest.y, 0f);
        foreach (var interval in merged)
        {
            float candidate = Mathf.Max(interval.x, -interval.y, 0f);
            if (candidate < distance || (candidate == distance && interval.y > nearest.y))
            {
                nearest = interval;
                distance = candidate;
            }
        }
        return nearest.y - Constants.STATION_HOUSE_TRACK_CLEARANCE_MARGIN - depth;
    }

    private static float ResolveSlideDistance(List<Vector2> intervals, float houseDepth)
    {
        List<Vector2> merged = MergeIntervals(intervals);

        var candidates = new List<float> { 0f };
        foreach (var interval in merged)
        {
            candidates.Add(interval.y);
        }
        candidates.Sort();

        foreach (float cand in candidates)
        {
            if (cand < 0f) continue;
            bool free = true;
            float lo = cand, hi = cand + houseDepth;
            foreach (var interval in merged)
            {
                if (lo < interval.y && hi > interval.x)
                {
                    free = false;
                    break;
                }
            }
            if (free) return cand;
        }
        return 0f;
    }
}
