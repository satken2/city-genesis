using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;
using System;
using GeneratedModels;

/// <summary>
/// Roadway mesh generation (using Dreamteck Splines).
/// </summary>
public class RoadPath : IPathMeshGenerator
{
    private static readonly StageMarker splineMeshMarker = new StageMarker("Path.SplineMesh");
    private static readonly StageMarker splineMeshRebuildMarker = new StageMarker("Path.SplineMesh.Rebuild");
    private static readonly StageMarker splineMeshCapsMarker = new StageMarker("Path.SplineMesh.Caps");
    private static readonly StageMarker pillarCandidatesMarker = new StageMarker("Path.Pillars.Candidates");
    private static readonly StageMarker pillarPortalMarker = new StageMarker("Path.Pillars.Portal");
    private static readonly StageMarker pillarInstantiateMarker = new StageMarker("Path.Pillars.Instantiate");
    private static readonly Dictionary<string, Material> MaterialCache = new Dictionary<string, Material>();
    private static readonly Dictionary<Mesh, Mesh> MirroredMeshCache = new Dictionary<Mesh, Mesh>();
    private static readonly Dictionary<(Mesh, float), Mesh> PartMeshCache = new Dictionary<(Mesh, float), Mesh>();
    private static readonly Dictionary<Mesh, Mesh> BodyMeshCache = new Dictionary<Mesh, Mesh>();
    private static readonly Dictionary<Mesh, Bounds> BodySurfaceBoundsCache = new Dictionary<Mesh, Bounds>();
    /// <summary>Part vertices/UVs/triangles copied by sidewalk connectors. Keys are immutable shared-prefab meshes; read only.</summary>
    private static readonly Dictionary<Mesh, (Vector3[] vertices, Vector2[] uv, int[][] triangles)> ConnectorSourceCache
        = new Dictionary<Mesh, (Vector3[], Vector2[], int[][])>();

    // Shared-pool materials and part-mesh derivatives may reference asset-pack resources; discard on city switches.
    static RoadPath() => AssetPackStore.CityChanging += ClearSharedCaches;

    private static void ClearSharedCaches()
    {
        MaterialCache.Clear();
        MirroredMeshCache.Clear();
        PartMeshCache.Clear();
        BodyMeshCache.Clear();
        BodySurfaceBoundsCache.Clear();
        ConnectorSourceCache.Clear();
    }

    // Maximum tile length for Extrude placement (m).
    // SplineMesh evaluates splines only at tile vertex Z positions;
    // using prefabs without Z subdivisions (body is a 10m box) directly as tiles
    // turns each tile into a straight chord at vertical curves (ridges/valleys), misaligning with finer parts
    // such as lines and causing intersections/floating. Limit tile length to bound chord error.
    public const float MAX_EXTRUDE_TILE_LENGTH = 1f;

    /// <summary>
    /// Maximum vertices per SplineMesh. Exceeding Unity's standard 65535-vertex limit causes
    /// Dreamteck errors every frame; clamp count with a safety margin.
    /// </summary>
    public const int SPLINE_MESH_VERTEX_BUDGET = 60000;

    /// <summary>
    /// Get cached road-part materials.
    /// Reassign FBX materials to same-name Resources materials where available,
    /// applying renderQueue+1 once to prevent Z-fighting.
    /// Use from all generators matching roads, including intersections/sidewalk connectors
    /// (direct prefab sharedMaterials can resolve differently from normal roads).
    /// </summary>
    /// <summary>
    /// Whether the variant is a ropeway/ropeway station. Ropeways differ in alignment validation, surface-height correction, and station-shell generation;
    /// centralize this classification.
    /// </summary>
    public static bool IsRopewayVariant(int variant)
        => variant == Constants.PATH_VARIANT_ROPEWAY || variant == Constants.PATH_VARIANT_ROPEWAY_STATION;

    public static Material GetOrCreateRoadMaterial(Material source)
    {
        if (source == null) return null;
        string key = source.name;

        Material fromResources = GetOrCreateRoadMaterialByName(key);
        if (fromResources != null) return fromResources;

        // No same-name Resources material; clone the FBX material into the shared pool.
        Material targetMat = new Material(source);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   マテリアルキャッシュ生成(clone): '{key}'");
        RegisterRoadMaterial(key, targetMat);
        return targetMat;
    }

    /// <summary>
    /// Get cached road-part material by name alone.
    /// Used when master data names road materials (intersection asphalt body_material).
    /// Prefer same-name asset-pack material, otherwise same-name .mat directly under Resources;
    /// return null if neither exists (caller must fall back).
    /// </summary>
    public static Material GetOrCreateRoadMaterialByName(string materialName)
    {
        if (string.IsNullOrEmpty(materialName)) return null;
        if (MaterialCache.TryGetValue(materialName, out Material cached))
            return cached;

        Material loadedMat = AssetPackStore.LoadNamedMaterial(materialName);
        if (loadedMat == null) return null;

        Material targetMat = new Material(loadedMat);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   マテリアルキャッシュ生成(Resources): '{materialName}'");
        RegisterRoadMaterial(materialName, targetMat);
        return targetMat;
    }

    private static void RegisterRoadMaterial(string key, Material material)
    {
        int baseQueue = material.renderQueue < 0 ? material.shader.renderQueue : material.renderQueue;
        material.renderQueue = baseQueue + 1;
        MaterialCache[key] = material;
    }

    /// <summary>Paint material. Model parts using it are drawn in the path's color; the model decides which parts are painted.</summary>
    public const string PAINT_MATERIAL = "PATH_PAINT";

    /// <summary>The path's paint color (API `color`, "#RRGGBB"). Null when the variant uses no color.</summary>
    public static Color? PaintColorOf(PathSummary path)
        => !string.IsNullOrEmpty(path?.color) && ColorUtility.TryParseHtmlString(path.color, out Color color) ? color : null;

    /// <summary>
    /// Shared-pool material of a part of a path drawn in the paint color: a PATH_PAINT source becomes the pooled copy
    /// in that color (one instance per color, named by it so the pool resolves it again); other materials and a null
    /// color resolve as <see cref="GetOrCreateRoadMaterial"/>. Variant-wide caches must not store the result.
    /// </summary>
    public static Material GetOrCreatePartMaterial(Material source, Color? paint)
    {
        Material pooled = GetOrCreateRoadMaterial(source);
        if (pooled == null || paint is not Color color || !source.name.StartsWith(PAINT_MATERIAL, StringComparison.Ordinal)) return pooled;
        string key = $"{PAINT_MATERIAL}#{ColorUtility.ToHtmlStringRGB(color)}";
        if (MaterialCache.TryGetValue(key, out Material painted)) return painted;
        painted = new Material(pooled) { name = key, color = color, renderQueue = pooled.renderQueue };
        MaterialCache[key] = painted;
        return painted;
    }

    private static Material GetFallbackRoadMaterial()
    {
        // Use URP-compatible assets through the shared pool, preserving shader references in builds.
        return GetOrCreateRoadMaterialByName("BuildingMaterial");
    }

    /// <summary>
    /// Determine surface width from the topmost surface, not full bounds including girder projections.
    /// Inputs are immutable shared-prefab meshes; scan vertices only once per mesh.
    /// </summary>
    public static Bounds GetBodySurfaceBounds(Mesh mesh)
    {
        if (BodySurfaceBoundsCache.TryGetValue(mesh, out Bounds cached)) return cached;
        Bounds result = ComputeBodySurfaceBounds(mesh);
        BodySurfaceBoundsCache[mesh] = result;
        return result;
    }

    private static Bounds ComputeBodySurfaceBounds(Mesh mesh)
    {
        Bounds bounds = mesh.bounds;
        float left = float.PositiveInfinity, right = float.NegativeInfinity;
        foreach (var vertex in mesh.vertices)
        {
            if (bounds.max.y - vertex.y > 0.002f) continue;
            left = Mathf.Min(left, vertex.x);
            right = Mathf.Max(right, vertex.x);
        }
        if (right - left < 0.001f) return bounds;
        return new Bounds(new Vector3((left + right) * 0.5f, bounds.max.y, bounds.center.z),
            new Vector3(right - left, 0f, bounds.size.z));
    }

    private static Mesh GetBodySurfaceMesh(Mesh source)
    {
        if (BodyMeshCache.TryGetValue(source, out var cached)) return cached;
        var mesh = UnityEngine.Object.Instantiate(source);
        // Absorb tiny FBX-origin errors; align shared intersection road height to y=0.
        var vertices = mesh.vertices;
        float surfaceY = source.bounds.max.y;
        for (int i = 0; i < vertices.Length; i++) vertices[i].y -= surfaceY;
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
        BodyMeshCache[source] = mesh;
        return mesh;
    }

    private static Mesh GetOrCreateMirroredMesh(Mesh originalMesh)
    {
        if (originalMesh == null) return null;
        Mesh key = originalMesh;
        if (MirroredMeshCache.TryGetValue(key, out Mesh cached))
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   ミラーメッシュキャッシュヒット: '{key}'");
            return cached;
        }

        Mesh mirrored = CreateMirroredMeshInternal(originalMesh);
        MirroredMeshCache[key] = mirrored;
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   ミラーメッシュキャッシュ生成: '{key}'");
        return mirrored;
    }

    /// <summary>Use the same model family's road origin to avoid double-counting road height in attached parts.</summary>
    public static Mesh GetRoadPartMesh(Mesh source, string resourcePath)
    {
        if (source == null) return null;
        string part = resourcePath.Substring(resourcePath.LastIndexOf('/') + 1);
        // Close-up parts (sidewalk_close, etc.) use the same road origin as standard parts.
        if (part.EndsWith(PathDrawContext.CLOSE_SUFFIX, StringComparison.Ordinal))
        {
            part = part.Substring(0, part.Length - PathDrawContext.CLOSE_SUFFIX.Length);
        }
        if (part != "sidewalk" && part != "line") return source;
        string bodyPath = resourcePath.Substring(0, resourcePath.LastIndexOf('/') + 1) + "body";
        var body = ModelManager.Instance?.GetModel(bodyPath)?.GetComponent<MeshFilter>()?.sharedMesh;
        float surfaceY = body != null ? body.bounds.max.y : 0f;
        if (Mathf.Abs(surfaceY) < 1e-6f) return source;
        var key = (source, surfaceY);
        if (PartMeshCache.TryGetValue(key, out var cached)) return cached;
        var mesh = UnityEngine.Object.Instantiate(source);
        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++) vertices[i].y -= surfaceY;
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
        PartMeshCache[key] = mesh;
        return mesh;
    }

    public PathMeshResult Generate(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] Generate開始: path_id={pathData?.path_id ?? -1}, path_variant={pathData?.path_variant ?? -1}, path_type={pathData?.path_type ?? -1}, points={points?.Count ?? 0}");

        if (points == null || points.Count < 2)
        {
            Debug.LogWarning($"[RoadPath] ポイント不足 ({points?.Count ?? 0}件)。生成スキップ。");
            return new PathMeshResult(null, false);
        }

        bool isCurved = pathData.curved;
        bool hasWaypoints = pathData.GetWaypointList() != null && pathData.GetWaypointList().Count > 0;

        // Get parameters before terrain conformity (needed for single_span).
        GetPathMasterParams(pathData, out float width, out float sidewalkWidth, out float sideWidth, out float? treeSpacing, out int lanes, out float wallWidth, out float centerWidth, out float? pillarSpacing, out bool heightEditable, out float baseHeight, out bool singleSpan);

        // For single_span, treat curved paths as straight segments between anchors only.
        // Build spline only from anchors without terrain following, keeping body as straight spans between anchor posts.
        if (singleSpan && isCurved)
        {
            points = ExtractAnchorPointsForSingleSpan(pathData, points);
            isCurved = false;
        }

        // Process corners only on anchor sequences. Already subdivided section/profile points form one rendered-centerline spline.
        // Linear splines average neighboring directions (linearAverageDirection); using one anchor spline
        // misorients the whole section after a corner. With subdivided points, only the first section after it differs.
        if (!isCurved && hasWaypoints && PathManager.IsAnchorPolyline(pathData, points, out int firstSpan))
        {
            return GenerateLinearPathWithCorners(pathData, points, startSidewalkSetback, endSidewalkSetback, singleSpan, firstSpan);
        }

        // Straight paths without waypoints contain only start/end control points,
        // so terrain following lacks intermediate points.
        // Interpolate intermediate points at regular intervals and apply terrain heights.
        // single_span skips terrain following, using anchors directly as spline control points.
        if (!isCurved && !singleSpan && points.Count == 2)
        {
            var startList = pathData.GetStartPointList();
            var endList = pathData.GetEndPointList();
            float startRelY = (startList != null && startList.Count >= 3) ? startList[1] : 0f;
            float endRelY = (endList != null && endList.Count >= 3) ? endList[1] : 0f;
            bool followTerrain = PathManager.GetPathFollowTerrain(pathData)
                && startRelY == 0f && endRelY == 0f;
            points = PathManager.SubdividePointsWithTerrain(points, PathManager.TERRAIN_SAMPLE_INTERVAL, followTerrain);
        }

        // Create container.
        GameObject container = new GameObject($"Road_{pathData.path_id}");
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] コンテナ作成: {container.name}");

        SplineComputer spline = BuildSpline(container, points, isCurved);

        float splineLength = spline.CalculateLength();
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] スプライン長: {splineLength:F2}m");
        PathDrawContext.SetAdaptiveTileLength(PathDrawContext.ComputeAdaptiveTileLength(spline, MAX_EXTRUDE_TILE_LENGTH));

        // Generate road parts.
        CreateRoadParts(container, spline, width, sidewalkWidth, sideWidth, lanes, treeSpacing, wallWidth, centerWidth, pillarSpacing, heightEditable, baseHeight, singleSpan, pathData, points, startSidewalkSetback, endSidewalkSetback);
        SuspensionBridgeGenerator.Generate(container, points, pathData, width);

        // Generate collider mesh.
        Mesh colliderMesh = BuildColliderMeshFromSpline(spline, width);
        if (colliderMesh != null)
        {
            colliderMesh.name = $"RoadCollider_{pathData.path_id}";
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] コライダーメッシュ生成成功: 頂点数={colliderMesh.vertexCount}");
        }
        else
        {
            Debug.LogWarning("[RoadPath] コライダーメッシュ生成失敗");
        }

        int childCount = container.transform.childCount;
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] Generate完了: 子オブジェクト数={childCount}, colliderMesh={(colliderMesh != null ? "あり" : "なし")}");

        // Debug: inspect mesh state immediately after generation.
        if (LocalConstants.UNITY_LOG_ROAD)
        {
            var allRenderers = container.GetComponentsInChildren<Renderer>(true);
            if (allRenderers.Length == 0)
            {
                Debug.LogWarning($"[RoadPath] Generate直後: containerにRendererが1件もありません。メッシュが生成されていないか、SplineMeshの遅延生成が原因の可能性があります。");
            }
            else
            {
                Bounds totalBounds = allRenderers[0].bounds;
                for (int i = 1; i < allRenderers.Length; i++) totalBounds.Encapsulate(allRenderers[i].bounds);
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] Generate直後: Renderer={allRenderers.Length}件, bounds中心={totalBounds.center}, boundsサイズ={totalBounds.size}");
            }
        }

        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>
    /// Generate meshes along paths using SplineMesh.
    /// Shared part-placement process for RoadPath (roads) and TrackPath (tracks).
    /// </summary>
    /// <param name="centerOffsetX">
    /// Lateral distance from spline centerline to part center.
    /// Internally correct the pivot so mesh-bounds center lies here,
    /// whether prefab pivots are centered or edge-aligned.
    /// </param>
    /// <param name="mirrorX">True mirrors mesh X (left/right symmetry).</param>
    /// <param name="isDashed">True uses Place mode at regular intervals for dashed lines.</param>
    /// <param name="stretchToWidth">
    /// True stretches mesh X width to width (variable-width parts such as road body).
    /// False fixes X scale to 1 (actual size), for fixed cross-sections such as rails
    /// (gauge must remain unchanged) and overhead wires. width is not used for pivot correction; placement stays actual size.
    /// </param>
    /// <param name="clipFrom">
    /// Start of placement interval (spline percent). Used for sidewalk endcaps
    /// to shorten only the intersection-side end (Extrude only, not dashed placement).
    /// </param>
    /// <param name="clipTo">End of placement interval (spline percent).</param>
    /// <param name="compensatePivot">
    /// True (default) absorbs pivot conventions by putting mesh-bounds center at centerOffsetX.
    /// False applies no correction, placing prefab origin (local X=0) at centerOffsetX.
    /// Use for parts whose prefab origin has meaning and should map directly
    /// to spline-relative coordinates (station platforms: origin=track centerline).
    /// </param>
    /// <param name="maxTileLength">
    /// Maximum tile length for Extrude (m). Default MAX_EXTRUDE_TILE_LENGTH limits chord error.
    /// Nonpositive means unlimited, using prefab actual Z length. Used for polylines without vertical curves
    /// (e.g. station-house footbridges) to preserve authored design-unit size.
    /// </param>
    /// <param name="normalizeBodySurface">
    /// Align body top to road origin Y=0. False for ground-origin station bridges to preserve model height.
    /// False also disables road section LOD that substitutes spline-plane geometry.
    /// </param>
    public static GameObject CreateSplineMesh(GameObject parent, SplineComputer spline, string prefabName, float width, float centerOffsetX, float offsetY, bool mirrorX = false, string fallbackPrefabName = null, bool isDashed = false, bool stretchToWidth = true, double clipFrom = 0.0, double clipTo = 1.0, bool singleSpan = false, bool compensatePivot = true, float maxTileLength = MAX_EXTRUDE_TILE_LENGTH, bool normalizeBodySurface = true)
    {
        using var scope = splineMeshMarker.Auto();
        // Even through the shared construction entry point, do not widen ropeway cables to road width.
        if (prefabName == "Models/Paths/p601/body" || prefabName == "Models/Paths/p602/body")
        {
            stretchToWidth = false;
            normalizeBodySurface = false;
        }
        // Deformations such as track-join S curves propagate through deferred section LOD and vertex-limit splitting.
        PartWarp warp = PathDrawContext.Warp;
        var partCreated = PathDrawContext.PartCreated;

        // Load prefabs, preferring `_close` for close-up LOD, resolving variant→type
        // (PathDrawContext.ResolvePrefab). Close parts match standard bbox/origin, so placement calculations are shared.
        GameObject prefab = null;
        string resolvedPrefabName = prefabName;
        if (ModelManager.Instance == null)
        {
            Debug.LogWarning($"[RoadPath] ModelManager.Instance が null。プレハブ '{prefabName}' をロードできません。");
        }
        else
        {
            prefab = PathDrawContext.ResolvePrefab(prefabName, fallbackPrefabName, out resolvedPrefabName);
            if (prefab != null && resolvedPrefabName != prefabName)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] '{prefabName}' → '{resolvedPrefabName}' を使用");
            }
        }

        if(prefab == null)
        {
            Debug.LogWarning($"[RoadPath] プレハブなし: '{prefabName}'");
            return null;
        }

        if (normalizeBodySurface && !singleSpan && !isDashed && !PathDrawContext.SuppressDetailRange && spline.CalculateLength() >= 256f
            && parent.GetComponentInParent<SplinePartLod>() == null)
        {
            var group = new GameObject(prefabName + "_LOD");
            group.transform.SetParent(parent.transform, false);
            float adaptive = PathDrawContext.AdaptiveTileLength;
            Color? paint = PathDrawContext.PaintColor;
            bool isBody = prefabName.EndsWith("/body", StringComparison.Ordinal);
            group.AddComponent<SplinePartLod>().Configure(spline, width, centerOffsetX, offsetY,
                isBody, isBody ? PathDrawContext.PathVariant : 0, clipFrom, clipTo,
                (lod, from, to) =>
                {
                    PathDrawContext.Begin(lod, from, to);
                    PathDrawContext.SetAdaptiveTileLength(adaptive);
                    PathDrawContext.PaintColor = paint;
                    PathDrawContext.Warp = warp;
                    PathDrawContext.PartCreated = partCreated;
                    try
                    {
                        return CreateSplineMesh(group, spline, prefabName, width, centerOffsetX, offsetY,
                            mirrorX, fallbackPrefabName, isDashed, stretchToWidth, from, to, singleSpan,
                            compensatePivot, maxTileLength, normalizeBodySurface);
                    }
                    finally { PathDrawContext.End(); }
                });
            return group;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] プレハブ取得成功: '{prefabName}', mirrorX={mirrorX}");

        GameObject go = new GameObject(prefabName);
        go.transform.SetParent(parent.transform, false);

        SplineMesh splineMesh = go.AddComponent<SplineMesh>();
        splineMesh.spline = spline;

        MeshFilter mf = prefab.GetComponent<MeshFilter>();
        MeshRenderer mr = prefab.GetComponent<MeshRenderer>();
        SplineMesh.Channel channel = null;
        float scaleX = stretchToWidth ? width : 1f;
        // Correction when mesh pivot is not centered.
        // SplineMesh adds pivot-relative vertices directly to offsets,
        // placing edge-pivot meshes directly shifts parts laterally.
        // Correct offsets so bounds center matches centerOffsetX,
        // absorbing per-prefab pivot conventions (center/edge).
        float pivotCompensationX = 0f;
        // Pivot-correction reference following deformation scale (bounds center × lateral scale).
        float warpPivotX = 0f;

        if(mf != null && mf.sharedMesh != null)
        {
             bool isBody = normalizeBodySurface && prefabName.EndsWith("/body", StringComparison.Ordinal);
             Mesh meshToUse = isBody ? GetBodySurfaceMesh(mf.sharedMesh) : GetRoadPartMesh(mf.sharedMesh, resolvedPrefabName);
             if (warp?.SourceMesh != null) meshToUse = warp.SourceMesh(meshToUse);
             Bounds fittingBounds = isBody ? GetBodySurfaceBounds(meshToUse) : meshToUse.bounds;
             UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   メッシュ: '{mf.sharedMesh.name}', bounds={mf.sharedMesh.bounds.size}, vertexCount={mf.sharedMesh.vertexCount}");

             // Duplicate/mirror the mesh when mirroring is needed.
             if (mirrorX)
              {
                  meshToUse = GetOrCreateMirroredMesh(meshToUse);
              }

             // Add a channel with mesh.
             channel = splineMesh.AddChannel(meshToUse, prefabName);

             // Compute scale from source width (actual-size parts remain unscaled).
             float meshWidth = fittingBounds.size.x;
             if(stretchToWidth && meshWidth > 0.001f)
             {
                 scaleX = width / meshWidth;
             }

             // Pivot correction: mirroring also flips bounds-center X.
             // compensatePivot=false parts (platforms) retain prefab origins without correction.
             float meshCenterX = fittingBounds.center.x;
             if (mirrorX) meshCenterX = -meshCenterX;
             if (compensatePivot) pivotCompensationX = -(meshCenterX * scaleX);
             if (compensatePivot) warpPivotX = meshCenterX * scaleX;
             UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   scaleX計算: meshWidth={meshWidth:F3} → scaleX={scaleX:F3} (targetWidth={width}), pivotCompensationX={pivotCompensationX:F3}");

             MeshRenderer targetMr = go.GetComponent<MeshRenderer>();
             if (targetMr == null) targetMr = go.AddComponent<MeshRenderer>();

            if(mr != null)
            {
                Material[] originalMaterials = mr.sharedMaterials;
                Material[] cachedMaterials = new Material[originalMaterials.Length];

                for (int i = 0; i < originalMaterials.Length; i++)
                {
                    cachedMaterials[i] = GetOrCreatePartMaterial(originalMaterials[i], PathDrawContext.PaintColor);
                }
                targetMr.sharedMaterials = cachedMaterials;
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   マテリアル割当完了: {cachedMaterials.Length}件");
            }
            else
            {
                targetMr.sharedMaterial = GetFallbackRoadMaterial();
                Debug.LogWarning($"[RoadPath]   プレハブにMeshRendererなし。Standardシェーダーのマテリアルを使用。");
            }

             if (go.GetComponent<MeshFilter>() == null) go.AddComponent<MeshFilter>();
        }
        else
        {
            // Add a channel without mesh (empty channel).
            channel = splineMesh.AddChannel(prefabName);
            Debug.LogWarning($"[RoadPath]   プレハブ '{prefabName}' にMeshFilter/sharedMeshなし。空チャンネル追加。");
        }

        if (channel != null)
        {
            float offsetX = centerOffsetX + pivotCompensationX;
            splineMesh.offset = new Vector3(offsetX, offsetY, 0f);
            float splineLen = spline.CalculateLength();

            // Actual Z length (for tile/dash-length calculations).
            float meshLenZ = 1.0f;
            float meshCenterZ = 0f;
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.bounds.size.z > 0.001f)
            {
                meshLenZ = mf.sharedMesh.bounds.size.z;
                meshCenterZ = mf.sharedMesh.bounds.center.z;
            }

            if (isDashed)
            {
                // Dashed display: regularly spaced Place-mode instances with adjusted dash length.
                channel.type = SplineMesh.Channel.Type.Place;
                channel.autoCount = false;

                const float dashSpacing = 4.0f; // Dash period (meters).
                const float dashLength = 2.0f;  // Dash length (meters).

                float scaleZ = dashLength / meshLenZ;

                // Place mode positions mesh pivots at placement points; also correct Z
                // to center each dash there (rear-pivot meshes would otherwise
                // shift each dash backward from its placement point).
                splineMesh.offset = new Vector3(offsetX, offsetY, -(meshCenterZ * scaleZ));

                if (splineLen > dashLength * 2f)
                {
                    // Inset placement bounds by dashLength/2 at each end
                    // so dashes of dashLength do not protrude past road ends.
                    // Also restrict placement to rendering-LOD detail intervals (PathDrawContext).
                    double halfDashPercent = (dashLength * 0.5f) / splineLen;
                    double dashFrom = halfDashPercent;
                    double dashTo = 1.0 - halfDashPercent;
                    if (!PathDrawContext.IntersectDetailRange(dashFrom, dashTo, out dashFrom, out dashTo))
                    {
                        UnityEngine.Object.Destroy(go);
                        return null;
                    }
                    channel.clipFrom = dashFrom;
                    channel.clipTo = dashTo;
                    float usableLen = splineLen * (float)(dashTo - dashFrom);
                    channel.count = Mathf.Max(1, Mathf.FloorToInt(usableLen / dashSpacing) + 1);
                }
                else
                {
                    // For sections shorter than two dashes, place only one at the center.
                    channel.clipFrom = 0.5;
                    channel.clipTo = 0.5;
                    channel.count = 1;
                }

                channel.minScale = new Vector3(scaleX, 1f, scaleZ);
                channel.maxScale = new Vector3(scaleX, 1f, scaleZ);
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   点線チャンネル設定: offset=({offsetX:F3}, {offsetY:F3}), count={channel.count}, clip=({channel.clipFrom:F3}〜{channel.clipTo:F3}), scale=({scaleX:F3}, 1, {scaleZ:F3})");
            }
            else
            {
                channel.minScale = new Vector3(scaleX, 1f, 1f);
                channel.maxScale = new Vector3(scaleX, 1f, 1f);

                // Place only in the intersection of caller clip and rendering-LOD detail intervals (PathDrawContext).
                // PathManager fills outside with distant models. single_span (girder bridge spans) uses one model
                // connecting both ends, so do not clip by detail intervals.
                if (!singleSpan)
                {
                    if (!PathDrawContext.IntersectDetailRange(clipFrom, clipTo, out double detailClipFrom, out double detailClipTo))
                    {
                        UnityEngine.Object.Destroy(go);
                        return null;
                    }
                    clipFrom = detailClipFrom;
                    clipTo = detailClipTo;
                }

                // Restrict placement when trimming ends for sidewalk endcaps.
                // Callers must ensure clip values in 0–1 with clipFrom < clipTo.
                if (clipFrom > 0.0 || clipTo < 1.0)
                {
                    channel.clipFrom = clipFrom;
                    channel.clipTo = clipTo;
                }

                // Determine tile count explicitly from maximum tile length (maxTileLength,
                // default MAX_EXTRUDE_TILE_LENGTH) to limit chord error, rather than autoCount
                // (source Z length; see constant comments). Nonpositive limit uses prefab actual Z length.
                // single_span uses count=1 without tiling; endpoint Z vertices map to spline start/end,
                // forming a chord (straight span) between anchors.
                channel.autoCount = false;
                if (singleSpan)
                {
                    channel.count = 1;
                }
                else
                {
                    // Parts called with the default 1m cap use curvature-adaptive length
                    // (PathDrawContext.AdaptiveTileLength: longer on straight/flat, down to 1m on sharp curves/uneven terrain).
                    // Uniform 1m tiling multiplies vertices even on straight roads, reaching limits on dense parts such as track beds.
                    float tileLimit = maxTileLength;
                    if (maxTileLength > 0f && Mathf.Approximately(maxTileLength, MAX_EXTRUDE_TILE_LENGTH)
                        && PathDrawContext.AdaptiveTileLength > 0f)
                    {
                        tileLimit = PathDrawContext.AdaptiveTileLength;
                    }
                    float tileLen = tileLimit > 0f ? Mathf.Min(meshLenZ, tileLimit) : meshLenZ;
                    float clippedLen = splineLen * (float)System.Math.Max(0.0, clipTo - clipFrom);
                    channel.count = Mathf.Max(1, Mathf.RoundToInt(clippedLen / tileLen));
                }
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   チャンネル設定: offset=({offsetX:F3}, {offsetY:F3}), scale={scaleX:F3}, count={channel.count} (singleSpan={singleSpan}, clip={clipFrom:F3}〜{clipTo:F3})");
            }

            // Pass deformation as spline-fraction-dependent offset/scale to channels (Dreamteck evaluates per cross-section).
            if (warp != null && (warp.Shift != null || warp.Scale != null))
            {
                var shift = warp.Shift;
                var scale = warp.Scale;
                float pivot = warpPivotX;
                if (scale != null) channel.scaleHandler = percent => new Vector3(scale(percent), 1f, 1f);
                channel.offsetHandler = percent => new Vector2(
                    (shift != null ? shift(percent) : 0f) - (scale != null ? pivot * (scale(percent) - 1f) : 0f), 0f);
            }

            // ===== Pre-generation vertex-budget guard =====
            // SplineMesh vertices = source-part vertices × tile count (count).
            // Extremely short-Z FBXs (e.g. centimeter exports) or dense replacements can make
            // count×vertices explode past Unity's standard 65535-vertex limit, causing Dreamteck
            // errors every frame and making the editor unusable (previously observed).
            // Clamp count to fit; abort generation if one part alone exceeds the limit.
            if (mf != null && mf.sharedMesh != null)
            {
                int partVerts = mf.sharedMesh.vertexCount;
                if (partVerts > SPLINE_MESH_VERTEX_BUDGET)
                {
                    Debug.LogError($"[RoadPath] パーツ '{prefabName}' の頂点数({partVerts})が単体でSplineMeshの上限({SPLINE_MESH_VERTEX_BUDGET})を超えています。生成を中止します。軽量なモデルで再エクスポートしてください。");
                    UnityEngine.Object.Destroy(go);
                    return null;
                }
                if (partVerts > 0 && (long)partVerts * channel.count > SPLINE_MESH_VERTEX_BUDGET)
                {
                    int clampedCount = Mathf.Max(1, SPLINE_MESH_VERTEX_BUDGET / partVerts);
                    if (maxTileLength <= 0f && !singleSpan && !isDashed)
                    {
                        // For parts with design periods, preserve tile count and split into separate meshes at integer tile boundaries.
                        int totalCount = channel.count;
                        UnityEngine.Object.DestroyImmediate(go);
                        var group = new GameObject(prefabName + "_Sections");
                        group.transform.SetParent(parent.transform, false);
                        for (int first = 0; first < totalCount; first += clampedCount)
                        {
                            int last = Mathf.Min(totalCount, first + clampedCount);
                            double from = clipFrom + (clipTo - clipFrom) * first / totalCount;
                            double to = clipFrom + (clipTo - clipFrom) * last / totalCount;
                            CreateSplineMesh(group, spline, prefabName, width, centerOffsetX, offsetY,
                                mirrorX, fallbackPrefabName, false, stretchToWidth, from, to,
                                false, compensatePivot, maxTileLength, normalizeBodySurface);
                        }
                        return group;
                    }
                    Debug.LogWarning($"[RoadPath] パーツ '{prefabName}' のタイル数 {channel.count} は頂点数上限を超えるため {clampedCount} にクランプします(1個あたり頂点{partVerts}、Z実寸={meshLenZ:F3}m)。Z実寸が異常に小さい場合はcm単位でエクスポートされたFBXの疑いがあります(1 Blenderユニット=1mで再エクスポートすること)。");
                    channel.count = clampedCount;
                }
            }
        }

        try
        {
            using (splineMeshRebuildMarker.Auto())
                splineMesh.RebuildImmediate();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RoadPath] SplineMesh Rebuild失敗 '{prefabName}': {ex}");
        }
        SealSplineMesh(splineMesh);

        if (!singleSpan && !isDashed && (prefabName.EndsWith("/body", StringComparison.Ordinal)
            || prefabName.EndsWith("/sidewalk", StringComparison.Ordinal) || prefabName.EndsWith("/lrt_track", StringComparison.Ordinal)))
            using (splineMeshCapsMarker.Auto())
                RemoveInternalTileCaps(go.GetComponent<MeshFilter>()?.sharedMesh, channel);

        if (normalizeBodySurface && prefabName.EndsWith("/body", StringComparison.Ordinal))
            parent.GetComponentInParent<RoadMarkingContext>()?.Apply(go);

        if (mf != null && mf.sharedMesh != null)
            partCreated?.Invoke(go, spline, mf.sharedMesh, clipFrom, clipTo);
        return go;
    }

    /// <summary>Avoid tiny shadows from internal pavement/track end faces in low-angle light; close only placement-section ends.</summary>
    private static void RemoveInternalTileCaps(Mesh mesh, SplineMesh.Channel channel)
    {
        if (mesh == null || channel == null || channel.count < 2 || channel.GetMeshCount() != 1
            || channel.type != SplineMesh.Channel.Type.Extrude || channel.spacing != 0f) return;
        var source = channel.GetMesh(0).vertices;
        int stride = source.Length;
        // Dreamteck Extrude/Combine preserves source vertex order, concatenated by tile.
        // Tests use source geometry, so curves/grades/mirroring select identical end faces.
        if (stride == 0 || mesh.vertexCount != (long)stride * channel.count) return;
        float min = float.MaxValue, max = float.MinValue;
        foreach (var vertex in source) { min = Mathf.Min(min, vertex.z); max = Mathf.Max(max, vertex.z); }
        if (max - min < 0.001f) return;
        var ends = new int[stride];
        for (int i = 0; i < stride; i++)
            ends[i] = Mathf.Abs(source[i].z - min) < 0.00001f ? -1 : Mathf.Abs(source[i].z - max) < 0.00001f ? 1 : 0;
        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            var triangles = mesh.GetTriangles(submesh);
            var kept = new List<int>(triangles.Length);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                int tile = a / stride, end = ends[a % stride];
                bool cap = end != 0 && b / stride == tile && c / stride == tile
                    && ends[b % stride] == end && ends[c % stride] == end;
                if (cap && (end < 0 ? tile > 0 : tile < channel.count - 1)) continue;
                kept.Add(a); kept.Add(b); kept.Add(c);
            }
            if (kept.Count != triangles.Length) mesh.SetTriangles(kept, submesh, false);
        }
    }

    /// <summary>
    /// Finalize a built SplineMesh as a static display.
    /// Dreamteck does not destroy generated meshes; assign ownership to the GO and disable the component to stop per-frame updates.
    /// RebuildImmediate still works while disabled, but later spline/Transform changes no longer rebuild automatically.
    /// </summary>
    public static void SealSplineMesh(SplineMesh splineMesh)
    {
        MeshFilter filter = splineMesh.GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
            splineMesh.gameObject.AddComponent<IntersectionMeshOwner>().OwnedMesh = filter.sharedMesh;
        splineMesh.enabled = false;
    }

    /// <summary>
    /// Shared spline (SplineComputer) construction from points.
    /// curved=true interpolates interior points with Catmull-Rom-equivalent Bézier curves, aligning end tangents to cut sections.
    /// Returned after RebuildImmediate so immediate CalculateLength/Evaluate calls are correct.
    /// Disable assuming points will remain unchanged (Evaluate/Project/CalculateLength still work).
    /// Disabled splines do not follow Transform movement; do not move the container in world space after generation.
    /// </summary>
    public static SplineComputer BuildSpline(GameObject container, List<Vector3> points, bool isCurved)
    {
        SplineComputer spline = container.AddComponent<SplineComputer>();

        SplinePoint[] splinePoints = new SplinePoint[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            SplinePoint p = new SplinePoint();
            p.position = points[i];
            p.normal = Vector3.up;
            p.size = 1f;
            p.color = Color.white;
            // curved=False preserves corners, joining with straight lines.
            p.type = isCurved ? SplinePoint.Type.SmoothMirrored : SplinePoint.Type.Broken;
            if (isCurved)
            {
                p.type = SplinePoint.Type.Broken;
                Vector3 tangent = (points[Mathf.Min(i + 1, points.Count - 1)] - points[Mathf.Max(0, i - 1)]) / 6f;
                if (i == 0 || i == points.Count - 1) tangent *= 2f;
                p.tangent = p.position - tangent;
                p.tangent2 = p.position + tangent;
            }
            splinePoints[i] = p;
        }

        spline.SetPoints(splinePoints);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] スプラインポイント設定: {splinePoints.Length}点 curved={isCurved}");

        spline.type = isCurved ? Spline.Type.Bezier : Spline.Type.Linear;
        spline.sampleMode = SplineComputer.SampleMode.Uniform;
        spline.RebuildImmediate();
        spline.enabled = false;
        return spline;
    }

    /// <summary>
    /// Sample the built spline about every 1m to create strip collider meshes for picking.
    /// Caller must assign the mesh name.
    /// </summary>
    /// <summary>
    /// Generate a full-width picking strip from a spline.
    /// lateralOffset shifts its center horizontally right
    /// (Cross(up, forward)). For asymmetric paths such as stations with platforms
    /// projecting on one side, use width=full width (tracks+platforms), lateralOffset=center displacement
    /// to create an asymmetric collider.
    /// </summary>
    public static Mesh BuildColliderMeshFromSpline(SplineComputer spline, float width, float lateralOffset = 0f)
    {
        try
        {
            float length = spline.CalculateLength();
            if (length <= 0) return null;

            float sampleSpacing = 1.0f;
            int sampleCount = Mathf.Max(2, Mathf.CeilToInt(length / sampleSpacing) + 1);

            List<Vector3> sampledPoints = new List<Vector3>(sampleCount);
            for (int i = 0; i < sampleCount; i++)
            {
                double percent = (double)i / (sampleCount - 1);
                SplineSample sample = spline.Evaluate(percent);
                Vector3 position = sample.position;
                if (lateralOffset != 0f)
                {
                    // Use horizontal right so terrain slopes do not shift the strip sideways.
                    Vector3 forward = sample.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
                    Vector3 right = Vector3.Cross(Vector3.up, forward.normalized);
                    position += right * lateralOffset;
                }
                sampledPoints.Add(position);
            }

            return PathMeshFactory.BuildStripMesh(sampledPoints, width);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RoadPath] コライダーメッシュ生成で例外: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Fallback for unavailable models (white strip along spline).
    /// </summary>
    public static GameObject CreateFallbackRoadBody(GameObject parent, SplineComputer spline, float width)
    {
        float length = spline.CalculateLength();
        if (length <= 0 || spline.sampleCount == 0) return null;

        float sampleSpacing = 1.0f;
        int sampleCount = Mathf.Max(2, Mathf.CeilToInt(length / sampleSpacing) + 1);

        List<Vector3> sampledPoints = new List<Vector3>(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            double percent = (double)i / (sampleCount - 1);
            SplineSample sample = spline.Evaluate(percent);
            // Match road-body height offset.
            sampledPoints.Add(sample.position + new Vector3(0f, RoadPartHeights.BODY_ABOVE_SPLINE, 0f));
        }

        Mesh mesh = PathMeshFactory.BuildStripMesh(sampledPoints, width);
        if (mesh == null)
        {
            Debug.LogWarning("[RoadPath] FallbackRoadBody: BuildStripMesh が null を返しました。");
            return null;
        }

        mesh.name = "FallbackRoadBody";

        GameObject go = new GameObject("FallbackRoadBody");
        go.transform.SetParent(parent.transform, false);

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;

        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetFallbackRoadMaterial();

        return go;
    }

    /// <summary>
    /// Create a new mesh mirrored along X.
    /// Mirror normals and reverse triangle winding to preserve facing.
    /// Internal method accessed through the cache.
    /// </summary>
    private static Mesh CreateMirroredMeshInternal(Mesh originalMesh)
    {
        Mesh newMesh = new Mesh();
        newMesh.name = originalMesh.name + "_Mirrored";

        // Mirror vertices.
        Vector3[] vertices = originalMesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].x *= -1;
        }
        newMesh.vertices = vertices;

        // Mirror normals.
        Vector3[] normals = originalMesh.normals;
        if (normals != null && normals.Length > 0)
        {
            for (int i = 0; i < normals.Length; i++)
            {
                normals[i].x *= -1;
            }
            newMesh.normals = normals;
        }

        // Keep UVs (textures mirror horizontally; prioritize geometric symmetry).
        newMesh.uv = originalMesh.uv;
        
        // Reverse triangle indices for each submesh.
        newMesh.subMeshCount = originalMesh.subMeshCount;
        for (int i = 0; i < originalMesh.subMeshCount; i++)
        {
            int[] triangles = originalMesh.GetTriangles(i);
            // Reversing the array alone is insufficient, but
            // reversing the whole sequence changes winding (CW/CCW),
            // canceling vertex-mirroring inversion for correct facing.
            Array.Reverse(triangles);
            newMesh.SetTriangles(triangles, i);
        }

        newMesh.RecalculateBounds();
        return newMesh;
    }

    /// <summary>
    /// Shared placement of road parts (roadway/lines/sidewalks/trees/barriers/median) along splines.
    /// Called from both Generate and GenerateLinearPathWithCorners.
    /// </summary>
    /// <param name="startSidewalkSetback">
    /// Start-side sidewalk setback. At intersections with sidewalk-free roads, end sidewalks
    /// before the roadway cut; intersection generation fills the vacant section with endcaps
    /// (see RoadIntersectionMeshGenerator.BuildSidewalkEndCapSpec).
    /// Left/right arrive in node frame (looking outward from node); map to spline view here.
    /// </param>
    /// <param name="endSidewalkSetback">End-side sidewalk setback.</param>
    /// <param name="segmentPlatformZones">
    /// Non-null for segments of bent linear roads. Caller draws LRT platforms over the whole path; omit them here
    /// and receive platform intervals mapped to this segment's spline distances (303 equipment suppression).
    /// </param>
    private void CreateRoadParts(GameObject container, SplineComputer spline, float width, float sidewalkWidth, float sideWidth, int lanes, float? treeSpacing, float wallWidth, float centerWidth, float? pillarSpacing, bool heightEditable, float baseHeight, bool singleSpan, PathSummary pathData, List<Vector3> controlPoints, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default, List<Vector2> segmentPlatformZones = null, bool startApproach = true, bool endApproach = true)
    {
        string variantBase = $"Models/Paths/p{pathData.path_variant}";
        string typeBase = $"Models/Paths/p{pathData.path_type}";
        // Lane debug logs require UNITY_LOG_ROAD for performance.
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] CreateRoadParts開始: variant={variantBase}, type={typeBase}, width={width}, lanes={lanes}, sidewalkWidth={sidewalkWidth}, sideWidth={sideWidth}, wallWidth={wallWidth}, centerWidth={centerWidth}");

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] モデルベースパス: variant={variantBase}, type(フォールバック)={typeBase}");

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] --- 道路パーツ生成開始 ---");

        var markingContext = RoadMarkingContext.Create(container, spline, pathData, startApproach, endApproach);
        var access = RoadAccessPlan.Create(pathData, spline, controlPoints, width);
        markingContext?.OverrideSideLine(true, access.Left, false);
        markingContext?.OverrideSideLine(false, access.Right, false);

        // Draw nearby motorway main-line/ramp sections as one surface (RoadAdjacency).
        // Decide edge-line styles before body generation passes them to shaders.
        bool ramp = RoadAdjacency.IsRamp(pathData);
        RoadCorridorInfo corridor = ResolveRoadCorridor(pathData, controlPoints, width, wallWidth, ramp);
        if (corridor != null && markingContext != null)
        {
            // Main-line joined edges become dashed merge-lane dividers; ramps omit main-line-side edges.
            markingContext.OverrideSideLine(true, SplineDistances(corridor.GetMergedPercentRanges(true), markingContext.Length), !ramp);
            markingContext.OverrideSideLine(false, SplineDistances(corridor.GetMergedPercentRanges(false), markingContext.Length), !ramp);
        }
        RoadEndJoin startJoin = ResolveEndJoin(pathData, true, startApproach, controlPoints);
        RoadEndJoin endJoin = ResolveEndJoin(pathData, false, endApproach, controlPoints);

        // Road Body
        // Raise body above spline to prevent burial from terrain-resolution limits/Z-fighting.
        // single_span=true stretches one body without tiling (straight span between anchors).
        // Ropeways preserve actual cable spacing/origins; do not rebase station roofs as road surfaces.
        // For base_height variants (power lines), splines sit at ground and models supply wire clearance;
        // aligning their tops to Y=0 would drop wires to ground.
        bool ropeway = IsRopewayVariant(pathData.path_variant);
        Material bodyMaterial = ResolveBodyMaterial(pathData.path_variant, pathData.path_type);
        GameObject roadBody = CreateSplineMesh(container, spline, $"{variantBase}/body", width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body", singleSpan: singleSpan, normalizeBodySurface: baseHeight <= 0f);
        if (ropeway)
            CreateSplineMesh(container, spline, $"{variantBase}/station_shell", width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE,
                stretchToWidth: false, maxTileLength: 0, normalizeBodySurface: false);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] body: {(roadBody != null ? "生成成功" : "生成失敗（プレハブなし？）")}");

        if (roadBody == null)
        {
            // No model; fall back to a plain white plate.
            roadBody = CreateFallbackRoadBody(container, spline, width);
            markingContext?.Apply(roadBody);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] body: モデルなしのため白い板にフォールバック");
        }
        if (corridor != null && !ramp) CreateMergeFillers(container, corridor, width * 0.5f, bodyMaterial);
        access.GenerateSurface(container, bodyMaterial ?? roadBody.GetComponent<MeshRenderer>()?.sharedMaterial);

        // Shared-street tracks (222/242/303) and platforms. 303 excludes roadside equipment in platform intervals (RoadEquipmentGenerator below).
        List<LrtPlatformParts.Span> lrtPlatforms = segmentPlatformZones == null
            ? LrtPlatformParts.Resolve(pathData, controlPoints, spline.CalculateLength()) : null;
        LrtRoadParts.Generate(container, spline, pathData, lrtPlatforms);

        // Offset calculation (all CreateSplineMesh inputs are part-center positions;
        // CreateSplineMesh corrects prefab pivot differences relative to bounds center).
        bool hasSidewalk = sidewalkWidth > 0f;

        // Sidewalks occupy the road-edge strip [width/2 - sidewalkWidth, width/2].
        // Place trees about one quarter sidewalk width outward from curb (strip inner edge).

        // Center road-side lines sideWidth/2 inward from sidewalk inner edge (curb).
        var laneLayout = new RoadLaneLayout(width, sidewalkWidth, sideWidth, centerWidth, lanes, pathData.travel_direction);
        float sideLineCenterOffset = laneLayout.Outer;
        // Standardize visible line thickness to 30% of sideWidth.
        float lineWidth = sideWidth * 0.3f;
        // Raise lines above body top (BODY_ABOVE_SPLINE) to avoid Z-fighting.
        float lineOffsetY = RoadPartHeights.LINE_ABOVE_SPLINE;

        // Side Lines (solid road-edge lines).
        // side_width=0 means no markings, so omit side and center lines entirely
        // (thickness derives from side_width; drawing at zero creates collapsed meshes).
        bool hasLine = sideWidth > 0f && markingContext == null;
        if (hasLine)
        {
            // Mirror left-side X for symmetric results even with asymmetric prefabs.
            GameObject leftLine = null;
            foreach (var range in access.Remaining(-1))
                leftLine = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, -sideLineCenterOffset, lineOffsetY, true, $"{typeBase}/line", clipFrom: range.x, clipTo: range.y);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] side line(左): {(leftLine != null ? "生成成功" : "生成失敗")}");
            GameObject rightLine = null;
            foreach (var range in access.Remaining(1))
                rightLine = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, sideLineCenterOffset, lineOffsetY, false, $"{typeBase}/line", clipFrom: range.x, clipTo: range.y);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] side line(右): {(rightLine != null ? "生成成功" : "生成失敗")}");
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] sideWidth <= 0: 区画線なし");
        }

        // Place median (center). No centerline needed with a median.
        // Fall back to line parts when median parts are absent.
        if (centerWidth > 0f)
        {
            float length = spline.CalculateLength();
            bool rounded = PathDrawContext.ResolvePrefab($"{variantBase}/center_start", $"{typeBase}/center_start", out _) != null
                && PathDrawContext.ResolvePrefab($"{variantBase}/center_end", $"{typeBase}/center_end", out _) != null;
            // Ends continuing directly to main lines keep medians continuous too.
            bool roundStart = rounded && !startJoin.Continuous, roundEnd = rounded && !endJoin.Continuous;
            double from = roundStart ? ClampSplinePercent(spline.Travel(0, centerWidth * .5f, Spline.Direction.Forward)) : 0;
            double to = roundEnd ? ClampSplinePercent(spline.Travel(1, centerWidth * .5f, Spline.Direction.Backward)) : 1;
            // Avoid overlapping semicircles on short cuts; leave passage clear. Create no new ends at LOD boundaries.
            bool fits = !rounded || length > centerWidth + .1f;
            GameObject centerObj = fits ? CreateSplineMesh(container, spline, $"{variantBase}/center", centerWidth, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/center", clipFrom: from, clipTo: to) : null;
            if (rounded && fits)
            {
                // End parts stretch as single pieces over [0, from]/[to, 1]; author rounded sides toward −Z for center_start, +Z for center_end.
                if (roundStart && PathDrawContext.IntersectDetailRange(0, from, out _, out _))
                    CreateSplineMesh(container, spline, $"{variantBase}/center_start", centerWidth, 0f, RoadPartHeights.BODY_ABOVE_SPLINE,
                    fallbackPrefabName: $"{typeBase}/center_start", clipFrom: 0, clipTo: from, maxTileLength: 0, singleSpan: true);
                if (roundEnd && PathDrawContext.IntersectDetailRange(to, 1, out _, out _))
                    CreateSplineMesh(container, spline, $"{variantBase}/center_end", centerWidth, 0f, RoadPartHeights.BODY_ABOVE_SPLINE,
                    fallbackPrefabName: $"{typeBase}/center_end", clipFrom: to, clipTo: 1, maxTileLength: 0, singleSpan: true);
            }
            if (centerObj == null && hasLine)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] center パーツなし。line にフォールバック。");
                centerObj = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, 0f, lineOffsetY, false, $"{typeBase}/line");
            }
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] center: {(centerObj != null ? "生成成功" : "生成失敗")}");
        }

        // Use the same travel bands as lane centers. Shared definitions also divide full-width one-way lanes.
        if (hasLine)
        {
            foreach (var boundary in laneLayout.Boundaries())
            {
                CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, boundary.offset,
                    lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line", isDashed: boundary.dashed);
            }
        }

        // Sidewalks
        if (hasSidewalk)
        {
            // Road bodies use full master width; overlay sidewalks on them.
            // Sidewalks cover [width/2 - sidewalkWidth, width/2],
            // centered in that strip. Prefabs follow curb (road-center face) at +X=travel-right
            // (left-side part convention); retain source on left, X-mirror on right.
            float sidewalkCenterOffset = (width * 0.5f) - (sidewalkWidth * 0.5f);

            // Map sidewalk setbacks (end trimming for endcaps) to clip values.
            // setback sides use node frame (looking outward from node):
            //   Start node: outward=spline forward → node-left=spline left.
            //   End node: outward=spline backward → node-left=spline right.
            float splineLenForClip = spline.CalculateLength();
            GetSidewalkClipRange(splineLenForClip, startSidewalkSetback.LeftOutwardSetback, endSidewalkSetback.RightOutwardSetback, out double leftClipFrom, out double leftClipTo);
            GetSidewalkClipRange(splineLenForClip, startSidewalkSetback.RightOutwardSetback, endSidewalkSetback.LeftOutwardSetback, out double rightClipFrom, out double rightClipTo);

            GameObject leftSidewalk = null;
            foreach (var range in access.Remaining(-1, leftClipFrom, leftClipTo))
                leftSidewalk = CreateSplineMesh(container, spline, $"{variantBase}/sidewalk", sidewalkWidth, -sidewalkCenterOffset, RoadPartHeights.SIDEWALK_ABOVE_SPLINE, false, $"{typeBase}/sidewalk", clipFrom: range.x, clipTo: range.y);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] sidewalk(左): {(leftSidewalk != null ? "生成成功" : "生成失敗")} clip=({leftClipFrom:F3}〜{leftClipTo:F3})");
            GameObject rightSidewalk = null;
            foreach (var range in access.Remaining(1, rightClipFrom, rightClipTo))
                rightSidewalk = CreateSplineMesh(container, spline, $"{variantBase}/sidewalk", sidewalkWidth, sidewalkCenterOffset, RoadPartHeights.SIDEWALK_ABOVE_SPLINE, true, $"{typeBase}/sidewalk", clipFrom: range.x, clipTo: range.y);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] sidewalk(右・ミラー): {(rightSidewalk != null ? "生成成功" : "生成失敗")} clip=({rightClipFrom:F3}〜{rightClipTo:F3})");
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] sidewalkWidth <= 0: 歩道なし");
        }

        // Wall (sound barriers/guardrails).
        var tunnels = PathTunnelParts.Ranges(spline, pathData);
        if (wallWidth > 0f)
        {
            // Walls stand outside roads at [width/2, width/2 + wallWidth].
            // Right-side part convention: source on right, X-mirror on left
            // (opposite sidewalks' left-side convention; see CreateRoadParts sidewalk block).
            // Omit intervening walls and walls overlapping neighboring surfaces at main-line/ramp joins.
            float wallCenterOffset = (width * 0.5f) + (wallWidth * 0.5f);
            foreach (var range in WallRanges(corridor, left: true))
                foreach (var ground in PathTunnelParts.Outside(range.From, range.To, tunnels))
                    foreach (var remaining in access.Remaining(-1, ground.From, ground.To))
                        CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, -wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, true, $"{typeBase}/wall", clipFrom: remaining.x, clipTo: remaining.y);
            foreach (var range in WallRanges(corridor, left: false))
                foreach (var ground in PathTunnelParts.Outside(range.From, range.To, tunnels))
                    foreach (var remaining in access.Remaining(1, ground.From, ground.To))
                        CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, false, $"{typeBase}/wall", clipFrom: remaining.x, clipTo: remaining.y);
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] wallWidth <= 0: 壁なし");
        }

        PathTunnelParts.GenerateWall(container, spline, pathData, width * .5f, wallWidth, true, tunnels, WallRanges(corridor, true));
        PathTunnelParts.GenerateWall(container, spline, pathData, width * .5f, wallWidth, false, tunnels, WallRanges(corridor, false));
        PathTunnelParts.GenerateCeiling(container, spline, pathData, width * .5f, width * .5f, tunnels);

        // Joined sections form one surface; keep roadside equipment out of it.
        RoadEquipmentGenerator.Generate(container, spline, pathData, width, sidewalkWidth, treeSpacing,
            pathData.path_variant == 303
                ? segmentPlatformZones ?? LrtPlatformParts.ZoneRanges(lrtPlatforms, LrtRoadParts.PlatformRampLength(303), LrtRoadParts.PLATFORM_EQUIPMENT_ZONE_MARGIN)
                : corridor != null ? SplineDistances(corridor.GetMergedPercentRanges(null), spline.CalculateLength()) : null, access);
        SuspensionBridgeGenerator.GenerateTruss(container, spline, pathData, width);

        // Posts.
        // Underground paths (negative relative Y) lie below ground and need no surface-support posts
        // (cuttings rest on excavated ground; mixed elevated/underground paths are unsupported and omit posts).
        if (heightEditable && pillarSpacing.HasValue && pillarSpacing.Value > 0
            && !TunnelSectionUtil.IsUndergroundPath(pathData))
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] 柱: spacing={pillarSpacing.Value}m, base_height={baseHeight}m で配置開始");
            // At direct main-line continuations, only one connected path places join piers.
            GeneratePillars(container, spline, pillarSpacing.Value, baseHeight, pathData, variantBase, typeBase,
                startJoin.Continuous && !startJoin.OwnsSupport, endJoin.Continuous && !endJoin.OwnsSupport);
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] 柱: height_editable=false/地下パス/spacing未指定のためスキップ");
        }

        // Output overall generation summary.
        int totalChildren = container.transform.childCount;
        int rendererCount = 0;
        int meshFilterCount = 0;
        int totalVerts = 0;
        for (int ci = 0; ci < totalChildren; ci++)
        {
            Transform child = container.transform.GetChild(ci);
            MeshFilter mf = child.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                meshFilterCount++;
                totalVerts += mf.sharedMesh.vertexCount;
            }
            // SplineMesh creates child meshes; inspect nested descendants too.
            MeshFilter[] childMfs = child.GetComponentsInChildren<MeshFilter>(true);
            MeshRenderer[] childMrs = child.GetComponentsInChildren<MeshRenderer>(true);
            if (childMfs.Length > 0) meshFilterCount += childMfs.Length;
            if (childMrs.Length > 0) rendererCount += childMrs.Length;
            foreach (var cmf in childMfs)
            {
                if (cmf.sharedMesh != null) totalVerts += cmf.sharedMesh.vertexCount;
            }
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] --- 道路パーツ生成完了 --- 子オブジェクト数={totalChildren}, MeshFilter={meshFilterCount}, Renderer={rendererCount}, 総頂点数={totalVerts}");
    }

    /// <summary>
    /// Convert sidewalk setbacks (meters) to spline clip percent.
    /// If trimming both ends removes/inverts the interval, shrink proportionally to remain valid.
    /// </summary>
    private static void GetSidewalkClipRange(float splineLen, float startSetback, float endSetback, out double clipFrom, out double clipTo)
    {
        clipFrom = 0.0;
        clipTo = 1.0;
        if (splineLen <= 0f)
        {
            return;
        }
        float total = Mathf.Max(0f, startSetback) + Mathf.Max(0f, endSetback);
        if (total <= 0f)
        {
            return;
        }
        // For short roads with total setback over 80% of length, shrink proportionally.
        float scale = total > splineLen * 0.8f ? (splineLen * 0.8f) / total : 1f;
        clipFrom = Mathf.Max(0f, startSetback) * scale / splineLen;
        clipTo = 1.0 - Mathf.Max(0f, endSetback) * scale / splineLen;
    }

    private const float MERGE_WALL_MIN_RUN_LENGTH = 1.0f; // Minimum leftover wall length between joined sections (m).
    private const float MERGE_FILLER_EDGE_INSET = 0.3f; // Width sinking gap pavement under both surfaces (m).
    private const float MERGE_FILLER_MIN_LENGTH = 1.0f; // Minimum gap-pavement length (m).

    /// <summary>Motorway main-line/ramp joining intervals. Points are this spline's control points.</summary>
    private static RoadCorridorInfo ResolveRoadCorridor(PathSummary pathData, List<Vector3> points, float width, float wallWidth, bool ramp)
    {
        if (!RoadAdjacency.IsMotorway(pathData) || RoadAdjacency.NeighborProvider == null || points == null) return null;
        var neighbors = RoadAdjacency.NeighborProvider(pathData);
        return neighbors != null && neighbors.Count > 0 ? RoadAdjacency.Compute(points, width, wallWidth, ramp, neighbors) : null;
    }

    private static List<Vector2> SplineDistances(List<TrackCorridorInfo.PercentRange> ranges, float length)
    {
        var result = new List<Vector2>(ranges.Count);
        foreach (var range in ranges) result.Add(new Vector2((float)range.From * length, (float)range.To * length));
        return result;
    }

    /// <summary>Rendering-section endpoints (e.g. tunnel portals) are not road ends.</summary>
    private static RoadEndJoin ResolveEndJoin(PathSummary pathData, bool start, bool pathEnd, List<Vector3> points)
    {
        if (!pathEnd || points == null || points.Count == 0) return default;
        Vector3 point = start ? points[0] : points[points.Count - 1];
        if (RoadMarkingRules.IsApproachEndpoint != null && !RoadMarkingRules.IsApproachEndpoint(pathData, start, point)) return default;
        return RoadAdjacency.EndJoin(pathData, start);
    }

    private static List<TrackCorridorInfo.PercentRange> WallRanges(RoadCorridorInfo corridor, bool left)
        => corridor != null ? corridor.GetWallPercentIntervals(left, MERGE_WALL_MIN_RUN_LENGTH)
            : new List<TrackCorridorInfo.PercentRange> { new TrackCorridorInfo.PercentRange { From = 0.0, To = 1.0 } };

    /// <summary>Like intersections (RoadIntersectionMeshGenerator), master body_material defines road-surface material.</summary>
    public static Material ResolveBodyMaterial(int variant, int type)
    {
        var master = MasterDataStore.Instance?.GetPathMaster(variant);
        Material named = master != null ? GetOrCreateRoadMaterialByName(master.body_material) : null;
        if (named != null) return named;
        GameObject prefab = PathDrawContext.ResolvePrefab($"Models/Paths/p{variant}/body", $"Models/Paths/p{type}/body", out _);
        Material source = prefab != null ? prefab.GetComponent<MeshRenderer>()?.sharedMaterial : null;
        return source != null ? GetOrCreateRoadMaterial(source) : null;
    }

    /// <summary>
    /// Strip paving gaps between main-line/ramp surfaces. Only main lines generate it to avoid duplicates.
    /// Sink twice ROAD_MERGE_SINK below main line, burying edges beneath both main-line and ramp (ROAD_MERGE_SINK lower) surfaces.
    /// </summary>
    private static void CreateMergeFillers(GameObject container, RoadCorridorInfo corridor, float halfWidth, Material material)
    {
        if (material == null) return;
        foreach (bool left in new[] { true, false })
        {
            float[] fillTo = left ? corridor.LeftFillTo : corridor.RightFillTo;
            int n = corridor.Points.Count, i = 0, run = 0;
            while (i < n)
            {
                if (float.IsNaN(fillTo[i])) { i++; continue; }
                int first = i;
                while (i < n && !float.IsNaN(fillTo[i])) i++;
                int last = i - 1;
                if (last - first < 1 || corridor.CumulativeLength[last] - corridor.CumulativeLength[first] < MERGE_FILLER_MIN_LENGTH) continue;
                BuildMergeFiller(container, corridor, first, last, left, halfWidth, fillTo, material, run++);
            }
        }
    }

    private static void BuildMergeFiller(GameObject container, RoadCorridorInfo corridor, int first, int last, bool left,
        float halfWidth, float[] fillTo, Material material, int run)
    {
        int count = last - first + 1;
        var vertices = new Vector3[count * 2];
        var normals = new Vector3[count * 2];
        var uvs = new Vector2[count * 2];
        var triangles = new int[(count - 1) * 6];
        float near = halfWidth - MERGE_FILLER_EDGE_INSET;
        float lift = RoadPartHeights.BODY_ABOVE_SPLINE - 2f * LocalConstants.ROAD_MERGE_SINK;
        for (int k = 0; k < count; k++)
        {
            int index = first + k;
            Vector3 origin = corridor.Points[index] + Vector3.up * lift;
            float far = fillTo[index] + MERGE_FILLER_EDGE_INSET;
            // Order vertices from smaller lateral position, preserving upward winding.
            float a = left ? -far : near, b = left ? -near : far;
            vertices[k * 2] = origin + corridor.RightDir[index] * a;
            vertices[k * 2 + 1] = origin + corridor.RightDir[index] * b;
            normals[k * 2] = normals[k * 2 + 1] = Vector3.up;
            float v = corridor.CumulativeLength[index] * 0.25f;
            uvs[k * 2] = new Vector2(a * 0.25f, v);
            uvs[k * 2 + 1] = new Vector2(b * 0.25f, v);
        }
        for (int k = 0, t = 0; k < count - 1; k++)
        {
            int v0 = k * 2;
            triangles[t++] = v0; triangles[t++] = v0 + 2; triangles[t++] = v0 + 1;
            triangles[t++] = v0 + 1; triangles[t++] = v0 + 2; triangles[t++] = v0 + 3;
        }
        var mesh = new Mesh { name = $"RoadMergeFiller_{(left ? "L" : "R")}_{run}" };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        var go = new GameObject(mesh.name);
        go.transform.SetParent(container.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
    }

    /// <summary>
    /// For single_span=true, extract only anchors from subdivided curved-path points.
    /// Use supplied first/last points directly (intersection cuts already applied).
    /// Get intermediate waypoint absolute coordinates from pathData with CalculatePathPoints-equivalent
    /// Y correction (relY=0: terrain elevation+ROAD_SPLINE_OFFSET; otherwise groundY+relY).
    /// A Linear spline through extracted anchors gives straight segments between anchors.
    /// </summary>
    private static List<Vector3> ExtractAnchorPointsForSingleSpan(PathSummary pathData, List<Vector3> subdividedPoints)
    {
        List<Vector3> anchors = new List<Vector3>();

        if (subdividedPoints == null || subdividedPoints.Count == 0)
            return anchors;

        // Start (intersection cut already applied).
        anchors.Add(subdividedPoints[0]);

        // Waypoints (same Y correction as CalculatePathPoints non-curved block).
        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                Vector3 wpPos = wp.GetAbsolutePosition();
                float wpRelY = (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
                if (wpRelY == 0f)
                {
                    float groundHeight = Utils.GetPathSurfaceHeight(new Vector3(wpPos.x, 1000f, wpPos.z));
                    wpPos.y = groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET;
                }
                anchors.Add(wpPos);
            }
        }

        // End (intersection cut already applied).
        if (subdividedPoints.Count >= 2)
            anchors.Add(subdividedPoints[subdividedPoints.Count - 1]);

        return anchors;
    }

    /// <summary>
    /// 切断後の折れ線を区間ごとに描き、経由点の角を交差点と同じ生成器で埋める。
    /// firstSpan は元のアンカー列のスパン番号。交差点内で除かれた経由点を飛ばし、残る角と高さ指定を対応させる。
    /// </summary>
    private PathMeshResult GenerateLinearPathWithCorners(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default, bool singleSpan = false, int firstSpan = 0)
    {
        // Separate segment splines cannot use detail intervals expressed as full-path fractions
        // (draw full length at one detail level).
        PathDrawContext.SuppressDetailRange = true;
        GameObject container = new GameObject($"Road_{pathData.path_id}");
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] 角あり直線道路生成開始: path_id={pathData.path_id}, segments={points.Count - 1}");

        // Get parameters.
        GetPathMasterParams(pathData, out float width, out float sidewalkWidth, out float sideWidth, out float? treeSpacing, out int lanes, out float wallWidth, out float centerWidth, out float? pillarSpacing, out bool heightEditable, out float baseHeight, out bool _);

        Vector3[] segmentStarts = new Vector3[points.Count - 1];
        Vector3[] segmentEnds = new Vector3[points.Count - 1];

        // Per-segment sidewalk setbacks. Real-node ends (first-segment start/last-segment end)
        // come from caller intersection results; corner ends come from corner generation.
        SidewalkSetback[] segStartSetbacks = new SidewalkSetback[points.Count - 1];
        SidewalkSetback[] segEndSetbacks = new SidewalkSetback[points.Count - 1];
        segStartSetbacks[0] = startSidewalkSetback;
        segEndSetbacks[points.Count - 2] = endSidewalkSetback;

        for (int i = 0; i < points.Count - 1; i++)
        {
            segmentStarts[i] = points[i];
            segmentEnds[i] = points[i + 1];
        }

        var waypoints = pathData.GetWaypointList();
        if (waypoints != null && waypoints.Count > 0)
        {
            var gf = new NetTopologySuite.Geometries.GeometryFactory();

            for (int i = 0; i < points.Count - 2; i++)
            {
                int pointIndex = i + 1;
                var wp = waypoints[firstSpan + i];
                Vector3 wpPos = points[pointIndex];
                Vector3 prevPos = points[pointIndex - 1];
                Vector3 nextPos = points[pointIndex + 1];

                Vector2 dirA = (new Vector2(wpPos.x, wpPos.z) - new Vector2(prevPos.x, prevPos.z)).normalized;
                Vector2 dirB = (new Vector2(nextPos.x, nextPos.z) - new Vector2(wpPos.x, wpPos.z)).normalized;

                if (Vector2.Dot(dirA, dirB) > 0.99f) continue;

                PathSummary dummyA = new PathSummary
                {
                    path_id = pathData.path_id * 10000 + i * 2,
                    path_type = pathData.path_type,
                    path_variant = pathData.path_variant,
                    width = width,
                    start_node_id = -1,
                    end_node_id = -2,
                    start_point = new List<object> { wpPos.x, wpPos.y, wpPos.z },
                    end_point = new List<object> { prevPos.x, prevPos.y, prevPos.z },
                    waypoints = new List<object>(),
                    curved = false
                };

                PathSummary dummyB = new PathSummary
                {
                    path_id = pathData.path_id * 10000 + i * 2 + 1,
                    path_type = pathData.path_type,
                    path_variant = pathData.path_variant,
                    width = width,
                    start_node_id = -1,
                    end_node_id = -3,
                    start_point = new List<object> { wpPos.x, wpPos.y, wpPos.z },
                    end_point = new List<object> { nextPos.x, nextPos.y, nextPos.z },
                    waypoints = new List<object>(),
                    curved = false
                };

                List<PathSummary> dummyPaths = new List<PathSummary> { dummyA, dummyB };
                float wpRelY = (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
                NodeMeshResult cornerResult = NodeMeshFactory.GenerateNodeMesh(-1, wpPos, dummyPaths, wpRelY,
                    p => new List<Vector3> { wpPos, p == dummyA ? prevPos : nextPos },
                    p => Vector2.Distance(IntersectionPolygonUtil.XZ(wpPos), IntersectionPolygonUtil.XZ(p == dummyA ? prevPos : nextPos)) * 0.45f);

                if (cornerResult != null)
                {
                    if (cornerResult.ConnectionPoints != null)
                    {
                        if (cornerResult.ConnectionPoints.TryGetValue(dummyA.path_id, out Vector3 cutA))
                        {
                            segmentEnds[pointIndex - 1] = cutA;
                        }
                        if (cornerResult.ConnectionPoints.TryGetValue(dummyB.path_id, out Vector3 cutB))
                        {
                            segmentStarts[pointIndex] = cutB;
                        }
                    }

                    // Apply generated corner sidewalk setbacks to corresponding segment ends.
                    // dummyA points from corner node toward previous segment (= previous segment end);
                    // dummyB toward next segment (= next segment start).
                    if (cornerResult.SidewalkSetbacks != null)
                    {
                        if (cornerResult.SidewalkSetbacks.TryGetValue(dummyA.path_id, out SidewalkSetback setbackA))
                        {
                            segEndSetbacks[pointIndex - 1] = setbackA;
                        }
                        if (cornerResult.SidewalkSetbacks.TryGetValue(dummyB.path_id, out SidewalkSetback setbackB))
                        {
                            segStartSetbacks[pointIndex] = setbackB;
                        }
                    }

                    if (cornerResult.Mesh != null)
                    {
                        GameObject cornerObj = new GameObject($"Corner_{i}");
                        cornerObj.transform.SetParent(container.transform, false);
                        cornerObj.transform.position = wpPos;
                        MeshFilter mf = cornerObj.AddComponent<MeshFilter>();
                        mf.mesh = cornerResult.Mesh;
                        cornerObj.AddComponent<IntersectionMeshOwner>().OwnedMesh = cornerResult.Mesh;
                        MeshRenderer mr = cornerObj.AddComponent<MeshRenderer>();
                        if (cornerResult.Material != null) mr.material = cornerResult.Material;
                        if (cornerResult.Markings != null) cornerObj.AddComponent<RoadMarkingSurface>().Configure(cornerResult.Markings);

                        // Corners use real-node connection geometry, joining clipped main paths with tracks/wires.
                        if (cornerResult.RailConnectors != null)
                        {
                            GameObject railGO = new GameObject("Rail");
                            railGO.transform.SetParent(cornerObj.transform, false);
                            for (int j = 0; j < cornerResult.RailConnectors.Count; j++)
                                TrackPath.CreateRailConnectorObject(railGO.transform, cornerResult.RailConnectors[j], j);
                        }

                        if (cornerResult.SidewalkConnectors != null && cornerResult.SidewalkConnectors.Count > 0)
                        {
                            GameObject sidewalkGO = new GameObject("Sidewalk");
                            sidewalkGO.transform.SetParent(cornerObj.transform, false);
                            sidewalkGO.transform.localPosition = Vector3.zero;
                            sidewalkGO.transform.localRotation = Quaternion.identity;
                            sidewalkGO.transform.localScale = Vector3.one;

                            for (int j = 0; j < cornerResult.SidewalkConnectors.Count; j++)
                            {
                                CreateSidewalkConnectorObject(sidewalkGO.transform, cornerResult.SidewalkConnectors[j], j);
                            }
                        }
                    }
                }
            }
        }

        // Spline points per segment (end at corner cut positions).
        var segmentPoints = new List<Vector3>[segmentStarts.Length];
        List<Vector3> allSubdividedPoints = new List<Vector3>();
        for (int i = 0; i < segmentStarts.Length; i++)
        {
            Vector3 segStart = segmentStarts[i];
            Vector3 segEnd = segmentEnds[i];
            if (Vector3.Distance(segStart, segEnd) < 0.01f) continue;

            // single_span skips terrain-following subdivision.
            // Use two-point splines with start/end anchors only; body count=1
            // stretches a straight span between them.
            List<Vector3> splinePoints;
            if (singleSpan)
            {
                splinePoints = new List<Vector3> { segStart, segEnd };
            }
            else
            {
                List<Vector3> anchorPoints = new List<Vector3> { segStart, segEnd };
                splinePoints = PathManager.SubdividePointsWithTerrain(anchorPoints, PathManager.TERRAIN_SAMPLE_INTERVAL, PathManager.LinearSpanFollowsTerrain(pathData, firstSpan + i));
            }
            allSubdividedPoints.AddRange(splinePoints);
            segmentPoints[i] = splinePoints;
        }

        List<Vector2>[] segmentPlatformZones = CreateCornerPathLrtPlatforms(container, pathData, points, segmentPoints);

        // Generate splines per segment.
        for (int i = 0; i < segmentPoints.Length; i++)
        {
            if (segmentPoints[i] == null) continue;
            GameObject segObj = new GameObject($"Segment_{i}");
            segObj.transform.SetParent(container.transform, false);
            SplineComputer spline = BuildSpline(segObj, segmentPoints[i], false);
            PathDrawContext.SetAdaptiveTileLength(PathDrawContext.ComputeAdaptiveTileLength(spline, MAX_EXTRUDE_TILE_LENGTH));

            CreateRoadParts(segObj, spline, width, sidewalkWidth, sideWidth, lanes, treeSpacing, wallWidth, centerWidth, pillarSpacing, heightEditable, baseHeight, singleSpan, pathData, segmentPoints[i], segStartSetbacks[i], segEndSetbacks[i], segmentPlatformZones?[i], i == 0, i == segmentPoints.Length - 1);
        }
        // Corner towers are shared by the spans on both sides, so the bridge structure is drawn once along the whole polyline.
        SuspensionBridgeGenerator.Generate(container, points, pathData, width);

        // Generate collider mesh.
        Mesh colliderMesh = BuildColliderMeshForSegments(allSubdividedPoints, width);
        if (colliderMesh != null)
        {
            colliderMesh.name = $"RoadCollider_{pathData.path_id}";
            // PathManager adds MeshCollider to pathGO and assigns sharedMesh.
        }

        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>
    /// Draw LRT platforms on bent linear roads (222/242/303) using one whole-path polyline spline joining segment points/corner vertices in order
    /// (cutting at corners would omit decks there and add ramps/shelters per corner).
    /// Across corners, use simplified bent extrusion like polyline tracks (TrackPath).
    /// Null for non-shared-street LRT; otherwise return platform intervals mapped to each segment's spline distance
    /// (for 303 equipment suppression; empty for non-303/no stops), one entry per segment.
    /// </summary>
    private static List<Vector2>[] CreateCornerPathLrtPlatforms(GameObject container, PathSummary pathData, List<Vector3> corners, List<Vector3>[] segmentPoints)
    {
        if (!LrtRoadParts.IsEmbedded(pathData.path_variant)) return null;
        var line = new List<Vector3>();
        var offsets = new float[segmentPoints.Length];
        float length = 0f;
        void Append(Vector3 point)
        {
            if (line.Count > 0)
            {
                float step = Vector3.Distance(line[line.Count - 1], point);
                if (step < 0.01f) return;
                length += step;
            }
            line.Add(point);
        }
        for (int i = 0; i < segmentPoints.Length; i++)
        {
            if (segmentPoints[i] == null) continue;
            // Corners sit on flat corner meshes; do not follow terrain there. Linear splines average segment tangents
            // with the previous direction (linearAverageDirection); subdivide finely to limit post-corner tangent deviation to one segment.
            var legs = line.Count > 0 ? new List<Vector3> { line[line.Count - 1], corners[i], segmentPoints[i][0] } : new List<Vector3> { segmentPoints[i][0] };
            foreach (var point in PathManager.SubdividePointsWithTerrain(legs, PathManager.TERRAIN_SAMPLE_INTERVAL, false)) Append(point);
            offsets[i] = length;
            for (int k = 1; k < segmentPoints[i].Count; k++) Append(segmentPoints[i][k]);
        }

        List<Vector2> zones = null;
        List<LrtPlatformParts.Span> spans = line.Count >= 2 ? LrtPlatformParts.Resolve(pathData, line, length) : null;
        if (spans != null && spans.Count > 0)
        {
            if (pathData.path_variant == 303)
                zones = LrtPlatformParts.ZoneRanges(spans, LrtRoadParts.PlatformRampLength(303), LrtRoadParts.PLATFORM_EQUIPMENT_ZONE_MARGIN);
            var platformObj = new GameObject("LrtPlatforms");
            platformObj.transform.SetParent(container.transform, false);
            SplineComputer spline = BuildSpline(platformObj, line, false);
            // Corner sampling slightly changes spline length from point-list length; convert distances proportionally.
            float scale = spline.CalculateLength() / length;
            for (int i = 0; i < spans.Count; i++)
            {
                var span = spans[i];
                span.From *= scale;
                span.To *= scale;
                spans[i] = span;
            }
            LrtPlatformParts.Generate(platformObj, spline, pathData, spans);
        }

        var result = new List<Vector2>[segmentPoints.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new List<Vector2>();
            if (zones != null)
                foreach (var zone in zones) result[i].Add(zone - new Vector2(offsets[i], offsets[i]));
        }
        return result;
    }

    /// <summary>
    /// Place sidewalk connectors (SidewalkConnectorSpec) at intersections/polyline corners
    /// by mapping normal-part vertices to the shared perimeter, interpolating only width.
    /// Shared by PathManager (node intersections) and RoadPath (curved=false corners).
    /// Use GetOrCreateRoadMaterial cache so ordinary/intersection sidewalks
    /// match color/render settings (renderQueue correction).
    /// </summary>
    public static void CreateSidewalkConnectorObject(Transform parent, SidewalkConnectorSpec connector, int index)
    {
        if (connector?.Points == null || connector.Points.Count < 2 || ModelManager.Instance == null) return;
        string part = connector.Part;
        string resourcePath = $"Models/Paths/p{connector.PrefabVariant}/{part}";
        GameObject prefab = ModelManager.Instance.GetModel(resourcePath, suppressWarning: true);
        if (prefab == null)
        {
            resourcePath = $"Models/Paths/p{connector.FallbackType}/{part}";
            prefab = ModelManager.Instance.GetModel(resourcePath);
        }
        Mesh source = prefab != null ? prefab.GetComponent<MeshFilter>()?.sharedMesh : null;
        source = GetRoadPartMesh(source, resourcePath);
        if (source == null) return;
        Bounds bounds = source.bounds;
        if (bounds.size.x < 1e-5f || bounds.size.z < 1e-5f) return;
        float length = IntersectionPolygonUtil.Length(connector.Points);
        if (length < 1e-5f) return;
        GameObject endPrefab = connector.EndPrefabVariant >= 0
            ? ModelManager.Instance.GetModel($"Models/Paths/p{connector.EndPrefabVariant}/{part}", suppressWarning: true)
                ?? ModelManager.Instance.GetModel($"Models/Paths/p{connector.EndFallbackType}/{part}") : prefab;
        Mesh endSource = endPrefab != null ? endPrefab.GetComponent<MeshFilter>()?.sharedMesh : null;
        float endHeight = endSource != null ? endSource.bounds.size.y : bounds.size.y;
        int tiles = Mathf.Max(1, Mathf.CeilToInt(length / 0.5f));
        if (!ConnectorSourceCache.TryGetValue(source, out var sourceArrays))
        {
            var triangles = new int[source.subMeshCount][];
            for (int sub = 0; sub < triangles.Length; sub++) triangles[sub] = source.GetTriangles(sub);
            sourceArrays = (source.vertices, source.uv, triangles);
            ConnectorSourceCache[source] = sourceArrays;
        }
        Vector3[] original = sourceArrays.vertices;
        Vector2[] originalUv = sourceArrays.uv;
        int[][] sourceTriangles = sourceArrays.triangles;
        var vertices = new List<Vector3>();
        var uv = new List<Vector2>();
        var indices = new List<int>[sourceTriangles.Length];
        for (int sub = 0; sub < indices.Length; sub++) indices[sub] = new List<int>();
        for (int tile = 0; tile < tiles; tile++)
        {
            if (connector.Dashed && (tile * length / tiles) % 6f >= 3f) continue;
            int offset = vertices.Count;
            foreach (var vertex in original)
            {
                float t = (tile + (vertex.z - bounds.min.z) / bounds.size.z) / tiles;
                EvaluateConnector(connector, t, out Vector3 position, out Vector2 forward, out float width);
                Vector3 right = new Vector3(forward.y, 0f, -forward.x);
                // Apply only width in X. Do not deform height through SplinePoint.size.
                float heightScale = bounds.size.y > 1e-5f ? Mathf.Lerp(bounds.size.y, endHeight, t*t*(3f-2f*t)) / bounds.size.y : 1f;
                vertices.Add(position + right * ((vertex.x - bounds.center.x) * width / bounds.size.x) + Vector3.Slerp(connector.StartNormal, connector.EndNormal, t*t*(3f-2f*t)).normalized * (vertex.y * heightScale));
            }
            for (int v = 0; v < original.Length; v++) uv.Add(v < originalUv.Length ? originalUv[v] : Vector2.zero);
            for (int sub = 0; sub < indices.Length; sub++) foreach (int triangle in sourceTriangles[sub]) indices[sub].Add(offset + triangle);
        }
        var mesh = new Mesh { name = $"Intersection_{part}_{index}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.subMeshCount = indices.Length;
        for (int sub = 0; sub < indices.Length; sub++) mesh.SetTriangles(indices[sub], sub);
        mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
        var go = new GameObject($"{part}_{index}");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        var sourceRenderer = prefab.GetComponent<MeshRenderer>();
        if (sourceRenderer != null)
        {
            var materials = sourceRenderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) materials[i] = GetOrCreateRoadMaterial(materials[i]);
            renderer.sharedMaterials = materials;
        }
    }

    private static void EvaluateConnector(SidewalkConnectorSpec connector, float t, out Vector3 position, out Vector2 forward, out float width)
    {
        float f = Mathf.Clamp01(t) * (connector.Points.Count - 1);
        int index = Mathf.Min(Mathf.FloorToInt(f), connector.Points.Count - 2);
        float fraction = f - index;
        position = Vector3.Lerp(connector.Points[index], connector.Points[index + 1], fraction);
        forward = IntersectionPolygonUtil.XZ(connector.Points[index + 1] - connector.Points[index]).normalized;
        if (connector.Directions != null) forward = Vector2.Lerp(connector.Directions[index], connector.Directions[index+1], fraction).normalized;
        if (t <= 1e-5f && connector.StartDirection.sqrMagnitude > 0f) forward = connector.StartDirection;
        if (t >= 1f - 1e-5f && connector.EndDirection.sqrMagnitude > 0f) forward = connector.EndDirection;
        width = connector.Widths != null ? Mathf.Lerp(connector.Widths[index], connector.Widths[index + 1], fraction) : 0.5f;
    }
    /// <summary>
    /// Generate independent strip meshes per segment; combine for collider mesh.
    /// </summary>
    private Mesh BuildColliderMeshForSegments(List<Vector3> points, float width)
    {
        List<CombineInstance> combineInstances = new List<CombineInstance>();

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 segStart = points[i];
            Vector3 segEnd = points[i + 1];
            if (Vector3.Distance(segStart, segEnd) < 0.01f) continue;

            List<Vector3> segPoints = new List<Vector3> { segStart, segEnd };
            Mesh segMesh = PathMeshFactory.BuildStripMesh(segPoints, width);
            if (segMesh != null)
            {
                combineInstances.Add(new CombineInstance { mesh = segMesh, transform = Matrix4x4.identity });
            }
        }

        if (combineInstances.Count == 0) return null;

        Mesh colliderMesh = new Mesh();
        colliderMesh.CombineMeshes(combineInstances.ToArray(), true, false);
        colliderMesh.RecalculateBounds();
        return colliderMesh;
    }

    /// <summary>
    /// Place pillars along paths.
    /// Only for height_editable=true variants.
    /// Each segment end is a candidate; if segment length exceeds pillar_spacing,
    /// add posts at that interval. Omit segment starts.
    ///
    /// base_height is a rendering offset (m). CalculatePathPoints subtracts it from spline Y,
    /// so the spline lies at relY - base_height.
    /// GeneratePillars also subtracts base_height from anchor Y to match the spline.
    /// Shared by RoadPath (elevated roads) and TrackPath (elevated tracks).
    /// </summary>
    /// <param name="skipStart">Omit start pier (the continuation path places one there).</param>
    /// <param name="skipEnd">Omit end pier.</param>
    public static void GeneratePillars(GameObject container, SplineComputer spline, float pillarSpacing, float baseHeight, PathSummary pathData, string variantBase, string typeBase,
        bool skipStart = false, bool skipEnd = false)
    {
        GeneratePillarsCore(container, spline, pillarSpacing, baseHeight, pathData, variantBase, typeBase, null, skipStart, skipEnd);
    }

    /// <summary>Return piers with completed-path interference checks as construction units with ground-contact origins.</summary>
    public static void GenerateConstructionPillars(GameObject container, SplineComputer spline, float spacing,
        float baseHeight, PathSummary path, string variantBase, string typeBase, System.Action<GameObject, double> onSupport)
    {
        GeneratePillarsCore(container, spline, spacing, baseHeight, path, variantBase, typeBase, onSupport);
    }

    private static void GeneratePillarsCore(GameObject container, SplineComputer spline, float pillarSpacing,
        float baseHeight, PathSummary pathData, string variantBase, string typeBase, System.Action<GameObject, double> onSupport,
        bool skipStart = false, bool skipEnd = false)
    {
        // Load prefab (variant→type fallback).
        string pillarPath = $"{variantBase}/pillar";
        string fallbackPillarPath = $"{typeBase}/pillar";
        GameObject pillarPrefab = null;
        if (ModelManager.Instance != null)
        {
            pillarPrefab = ModelManager.Instance.GetModel(pillarPath);
            if (pillarPrefab == null && !string.IsNullOrEmpty(fallbackPillarPath))
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar '{pillarPath}' なし。フォールバック試行: '{fallbackPillarPath}'");
                pillarPrefab = ModelManager.Instance.GetModel(fallbackPillarPath);
                if (pillarPrefab != null)
                {
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar フォールバック成功: '{fallbackPillarPath}'");
                }
            }
        }

        // Load pillar_cap.
        string pillarCapPath = $"{variantBase}/pillar_cap";
        string fallbackPillarCapPath = $"{typeBase}/pillar_cap";
        GameObject pillarCapPrefab = null;
        if (ModelManager.Instance != null)
        {
            pillarCapPrefab = ModelManager.Instance.GetModel(pillarCapPath);
            if (pillarCapPrefab == null && !string.IsNullOrEmpty(fallbackPillarCapPath))
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar_cap '{pillarCapPath}' なし。フォールバック試行: '{fallbackPillarCapPath}'");
                pillarCapPrefab = ModelManager.Instance.GetModel(fallbackPillarCapPath);
                if (pillarCapPrefab != null)
                {
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar_cap フォールバック成功: '{fallbackPillarCapPath}'");
                }
            }
        }

        if (pillarPrefab == null && pillarCapPrefab == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar/pillar_cap プレハブ両方なし: '{pillarPath}', '{pillarCapPath}'");
            return;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar プレハブロード状況: pillar={(pillarPrefab != null ? "あり" : "なし")}, cap={(pillarCapPrefab != null ? "あり" : "なし")}");

        // Build anchor list (start, waypoints, end).
        // Projection targets a spline already shifted by base_height in CalculatePathPoints;
        // subtract base_height from anchor Y too.
        List<Vector3> anchors = new List<Vector3>();

        var startList = GeneratedModelUtils.ToFloatList(pathData.start_point);
        if (startList != null && startList.Count >= 2)
        {
            float startRelY = startList.Count > 2 ? startList[1] : 0f;
            float startGroundY = Utils.GetPathSurfaceHeight(new Vector3(startList[0], 0f, startList[startList.Count - 1]));
            anchors.Add(new Vector3(startList[0], startGroundY + startRelY - baseHeight, startList[startList.Count - 1]));
        }

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                Vector3 wpAnchor = wp.GetAbsolutePosition();
                wpAnchor.y -= baseHeight;
                anchors.Add(wpAnchor);
            }
        }

        var endList = GeneratedModelUtils.ToFloatList(pathData.end_point);
        if (endList != null && endList.Count >= 2)
        {
            float endRelY = endList.Count > 2 ? endList[1] : 0f;
            float endGroundY = Utils.GetPathSurfaceHeight(new Vector3(endList[0], 0f, endList[endList.Count - 1]));
            anchors.Add(new Vector3(endList[0], endGroundY + endRelY - baseHeight, endList[endList.Count - 1]));
        }

        if (anchors.Count < 2)
        {
            Debug.LogWarning("[RoadPath] アンカー点が2点未満のため柱を配置できません");
            return;
        }

        float splineLength = spline.CalculateLength();
        if (splineLength <= 0f || spline.sampleCount == 0)
        {
            Debug.LogWarning("[RoadPath] スプライン長が0以下のため柱を配置できません");
            return;
        }

        // Find nearest spline percent for each anchor.
        List<double> anchorPercents = new List<double>();
        for (int i = 0; i < anchors.Count; i++)
        {
            double pct = FindClosestPercentOnSpline(spline, anchors[i]);
            anchorPercents.Add(pct);
        }

        PillarClearance.ObstacleQuery obstacles;
        using (pillarCandidatesMarker.Auto())
            obstacles = PillarClearance.NeighborProvider?.Invoke(pathData);
        var pillarBounds = PillarClearance.GetPrefabBounds(pillarPrefab);
        var capBounds = PillarClearance.GetPrefabBounds(pillarCapPrefab);
        var portal = PortalPillar.Load(variantBase, typeBase);
        int placedCount = 0;
        // Stretch pier beams laterally by local road-width scale (track-join S curves, PathDrawContext.Warp).
        PartWarp warp = PathDrawContext.Warp;

        bool TryPlace(double percent)
        {
            float capWidthScale = warp?.Scale != null ? warp.Scale(ClampSplinePercent(percent)) : 1f;
            if (onSupport == null)
                return PlacePillar(container, pillarPrefab, pillarCapPrefab, pillarBounds, capBounds, obstacles, spline, percent, baseHeight, capWidthScale, portal);
            Vector3 position = spline.EvaluatePosition(ClampSplinePercent(percent));
            float ground = Utils.GetGroundHeight(position);
            // Exclude sites without enough height for cap bottom to reach ground (same cap-thickness definition as PlacePillar).
            float capThickness = capBounds.HasValue ? Mathf.Max(0f, -capBounds.Value.min.y * (pillarCapPrefab != null ? pillarCapPrefab.transform.localScale.y : 1f)) : 0f;
            if (position.y - ground + baseHeight - capThickness <= 0.1f) return false;
            var support = new GameObject("ConstructionSupport");
            support.transform.SetParent(container.transform, false);
            support.transform.position = new Vector3(position.x, ground, position.z);
            if (!PlacePillar(support, pillarPrefab, pillarCapPrefab, pillarBounds, capBounds, obstacles, spline, percent, baseHeight, capWidthScale, portal))
            {
                UnityEngine.Object.Destroy(support);
                return false;
            }
            onSupport(support, percent);
            return true;
        }

        // Include whole-path start (first anchor) as an interference-tested candidate.
        // Segment loops place posts only at ends; separately placing the start ensures
        // both whole-path endpoints have exactly one post without duplicates.
        if (anchorPercents.Count > 0 && !skipStart)
        {
            if (TryPlace(anchorPercents[0]))
            {
                placedCount++;
            }
        }

        // Process each anchor-to-anchor segment.
        for (int i = 0; i < anchors.Count - 1; i++)
        {
            double startPct = anchorPercents[i];
            double endPct = anchorPercents[i + 1];
            bool lastSegment = i == anchors.Count - 2;

            // Approximate segment spline length.
            float segSplineLength = EstimateSegmentSplineLength(spline, startPct, endPct);
            if (segSplineLength <= 0.01f) continue;

            if (segSplineLength > pillarSpacing)
            {
                // If length exceeds pillar_spacing, divide evenly for post placement.
                // Prevents clustering across consecutive short segments.
                int divisions = Mathf.CeilToInt(segSplineLength / pillarSpacing);
                for (int j = 1; j <= divisions; j++)
                {
                    if (skipEnd && lastSegment && j == divisions) continue;
                    double t = (double)j / divisions;
                    double placementPct = startPct + (endPct - startPct) * t;
                    if (TryPlace(placementPct))
                    {
                        placedCount++;
                    }
                }
            }
            else
            {
                // Short segments place only one post at the end.
                if (!(skipEnd && lastSegment) && TryPlace(endPct))
                {
                    placedCount++;
                }
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] 柱配置完了: {placedCount}本");
    }

    /// <summary>
    /// Clamp spline evaluation percent to [0,1].
    /// Dreamteck SplineComputer.Evaluate does not clamp;
    /// even one ULP beyond 1.0 causes internal sample-array out-of-bounds access
    /// (IndexOutOfRangeException), possible from rounding startPct + (endPct - startPct) * 1.0.
    /// NaN also yields an invalid index through FloorInt; map to zero.
    /// Always use this for spline Evaluate/Project.
    /// </summary>
    public static double ClampSplinePercent(double percent)
    {
        if (double.IsNaN(percent)) return 0.0;
        if (percent < 0.0) return 0.0;
        if (percent > 1.0) return 1.0;
        return percent;
    }

    /// <summary>
    /// Find spline percent nearest the specified point.
    /// </summary>
    private static double FindClosestPercentOnSpline(SplineComputer spline, Vector3 worldPos)
    {
        return ClampSplinePercent(spline.Project(worldPos).percent);
    }

    /// <summary>
    /// Approximate length between two spline percents.
    /// Sum distances across ten intermediate samples.
    /// </summary>
    private static float EstimateSegmentSplineLength(SplineComputer spline, double startPct, double endPct)
    {
        int samples = 10;
        float totalDist = 0f;
        Vector3 prevPos = spline.Evaluate(ClampSplinePercent(startPct)).position;

        for (int i = 1; i <= samples; i++)
        {
            double pct = ClampSplinePercent(startPct + (endPct - startPct) * i / samples);
            Vector3 pos = spline.Evaluate(pct).position;
            totalDist += Vector3.Distance(prevPos, pos);
            prevPos = pos;
        }

        return totalDist;
    }

    /// <summary>
    /// Place pillar/pillar_cap at the specified spline position.
    /// Place pillar_cap at road level (=sample point), without Y scaling.
    /// pillar extends from below the cap by its thickness to ground (localGroundY).
    /// CalculatePathPoints already shifts Y by base_height, so sample.position.y is
    /// groundY + (relY - base_height) + offset; pillars extend down to ground.
    /// Rendering only; no game-logic effect.
    /// For base_height variants (power lines), splines sit at ground and cap=tower models
    /// supply clearance. Use road-ground difference + base_height as structure height
    /// to decide placement. Scale pillar body only to the road-ground gap.
    /// </summary>
    private static bool PlacePillar(GameObject container, GameObject pillarPrefab, GameObject pillarCapPrefab,
        Bounds? pillarBounds, Bounds? capBounds, PillarClearance.ObstacleQuery obstacles,
        SplineComputer spline, double percent, float baseHeight, float capWidthScale = 1f, PortalPillar portal = null)
    {
        SplineSample sample = spline.Evaluate(ClampSplinePercent(percent));
        Vector3 forward = sample.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        Quaternion rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        Vector3 capScale = pillarCapPrefab != null ? pillarCapPrefab.transform.localScale : Vector3.one;
        capScale.x *= capWidthScale;
        float capThickness = capBounds.HasValue ? Mathf.Max(0f, -capBounds.Value.min.y * capScale.y) : 0f;
        Vector3 pillarPosition = sample.position + Vector3.down * capThickness;
        float height = pillarPosition.y - Utils.GetGroundHeight(sample.position);
        // No posts/caps where the surface rests on ground (surface tracks/cuttings).
        if (height + baseHeight <= 0.1f) return false;
        // Pillars fill road-ground gaps; without a gap, place cap only.
        bool needsPillar = pillarPrefab != null && height > 0.1f;
        Vector3 pillarScale = pillarPrefab != null ? pillarPrefab.transform.localScale : Vector3.one;
        // Pier models use actual dimensions with type-specific heights, not necessarily 1m.
        // Author pillars downward from top origin (place origin at cap bottom, scale height to ground). Separate unscaled beams/etc. into pillar_cap.
        pillarScale.y = height / Mathf.Max(0.001f, pillarBounds.GetValueOrDefault().size.y);

        // Prefer normal piers; only where posts cannot fit, span roadways/tracks with separate posts/beams.
        // Test only ground-contact members (posts, or caps at low sites without posts); caps may project over passage space.
        bool blocked = needsPillar
            ? pillarBounds.HasValue && PillarClearance.Intersects(Utils.TransformBounds(pillarBounds.Value,
                Matrix4x4.TRS(pillarPosition, rotation, pillarScale)), obstacles)
            : capBounds.HasValue && PillarClearance.Intersects(Utils.TransformBounds(capBounds.Value,
                Matrix4x4.TRS(sample.position, rotation, capScale)), obstacles);
        if (blocked)
        {
            float top = sample.position.y + (capBounds.HasValue ? capBounds.Value.max.y * capScale.y : 0f);
            float halfWidth = capBounds.HasValue ? capBounds.Value.extents.x * capScale.x : pillarBounds.GetValueOrDefault().extents.x;
            // Portal legs stay within MaxSpan of the pier position, so only passage spaces within that reach matter.
            using (pillarPortalMarker.Auto())
                return portal != null && portal.TryPlace(container, sample.position, rotation, top, halfWidth,
                    obstacles?.Near(sample.position, PortalPillar.MaxSpan + PillarClearance.Margin));
        }

        using var instantiateScope = pillarInstantiateMarker.Auto();
        bool placed = false;
        if (pillarCapPrefab != null)
        {
            var cap = UnityEngine.Object.Instantiate(pillarCapPrefab, container.transform);
            cap.transform.position = sample.position;
            cap.transform.rotation = rotation;
            cap.transform.localScale = capScale;
            placed = true;
        }
        if (needsPillar)
        {
            var pillar = UnityEngine.Object.Instantiate(pillarPrefab, container.transform);
            pillar.transform.position = pillarPosition;
            pillar.transform.rotation = rotation;
            pillar.transform.localScale = pillarScale;
            placed = true;
        }
        return placed;
    }

    // Public for PathManager construction generation (dirt-strip width/post-placement tests).
    public static void GetPathMasterParams(PathSummary pathData, out float width, out float sidewalkWidth, out float sideWidth, out float? treeSpacing, out int lanes, out float wallWidth, out float centerWidth, out float? pillarSpacing, out bool heightEditable, out float baseHeight, out bool singleSpan)
    {
        width = 4.0f;
        sidewalkWidth = 0.5f;
        sideWidth = 0.5f;
        treeSpacing = null;
        lanes = 2;
        wallWidth = 0f;
        centerWidth = 0f;
        pillarSpacing = null;
        heightEditable = false;
        baseHeight = 0f;
        singleSpan = false;

        if (MasterDataStore.Instance == null)
        {
            Debug.LogWarning("[RoadPath] MasterDataStore.Instance が null。デフォルト幅を使用。");
            return;
        }

        var master = MasterDataStore.Instance.GetPathMaster(pathData.path_variant);
        if (master != null)
        {
            width = master.width;
            if (master.sidewalk_width.HasValue) sidewalkWidth = master.sidewalk_width.Value;
            if (master.side_width.HasValue) sideWidth = master.side_width.Value;
            if (master.tree_spacing.HasValue) treeSpacing = master.tree_spacing.Value;
            if (master.lanes.HasValue) lanes = master.lanes.Value;
            else lanes = Mathf.Max(1, Mathf.RoundToInt(width / 3.5f));
            if (master.wall_width.HasValue) wallWidth = master.wall_width.Value;
            if (master.center_width.HasValue) centerWidth = master.center_width.Value;
            if (master.pillar_spacing.HasValue) pillarSpacing = master.pillar_spacing.Value;
            if (master.height_editable.HasValue) heightEditable = master.height_editable.Value;
            if (master.base_height.HasValue) baseHeight = master.base_height.Value;
            if (master.single_span.HasValue) singleSpan = master.single_span.Value;
            if (LocalConstants.UNITY_LOG_ROAD) Debug.Log($"[RoadPath] マスターデータ取得成功: width={width}, sidewalkWidth={sidewalkWidth}, sideWidth={sideWidth}, treeSpacing={treeSpacing}, lanes={lanes}, wallWidth={wallWidth}, centerWidth={centerWidth}, pillarSpacing={pillarSpacing}, heightEditable={heightEditable}, baseHeight={baseHeight}, singleSpan={singleSpan}");
        }
        else
        {
            Debug.LogWarning($"[RoadPath] マスターデータなし (variant={pathData.path_variant})。デフォルト幅を使用。");
            lanes = Mathf.Max(1, Mathf.RoundToInt(width / 3.5f));
        }
    }
}


/// <summary>
/// Release procedural/SplineMesh meshes with GO lifetime; never register shared prefab meshes.
/// Release via MeshBakeService to avoid destroying meshes during asynchronous baking.
/// </summary>
public sealed class IntersectionMeshOwner : MonoBehaviour
{
    public Mesh OwnedMesh;
    private void OnDestroy()
    {
        MeshBakeService.Release(OwnedMesh);
    }
}

/// <summary>
/// Travel bands shared by road markings/vehicles. Lane indices count from travel-direction shoulder.
/// Odd-count bidirectional roads reserve one central lane for turning, excluding normal travel.
/// </summary>
public readonly struct RoadLaneLayout
{
    public float Outer { get; }
    public float Inner { get; }
    public int Count { get; }
    public bool OneWay { get; }
    private readonly bool centered;
    private readonly int totalLanes;
    private readonly float divider;

    public RoadLaneLayout(float width, float sidewalk, float side, float center, int lanes, int direction)
    {
        Outer = Mathf.Max(0, width * .5f - Mathf.Max(0, sidewalk) - Mathf.Max(0, side) * .5f);
        OneWay = direction != Constants.DIR_BOTH;
        totalLanes = Mathf.Max(1, lanes);
        divider = Mathf.Max(0, center) * .5f;
        Inner = OneWay ? 0 : divider;
        if (!OneWay && totalLanes > 1 && totalLanes % 2 != 0)
            Inner = Mathf.Max(Inner, Outer / totalLanes);
        centered = Outer <= Inner || (!OneWay && totalLanes <= 1);
        Count = centered ? 1 : (OneWay ? totalLanes : Mathf.Max(1, totalLanes / 2));
    }

    public float Center(int lane, int trafficHand)
    {
        if (centered) return 0;
        lane = Mathf.Clamp(lane, 0, Count - 1);
        float offset = OneWay ? Outer - 2 * Outer / Count * (lane + .5f)
            : Outer - (Outer - Inner) / Count * (lane + .5f);
        return offset * (trafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1);
    }

    // 街路では駐車車両との間隔を広げるため、車線内で中央寄りを走る。区画線の位置は変えない。
    public float VehicleCenter(int lane, int trafficHand, int pathType)
    {
        float offset = Center(lane, trafficHand);
        if (pathType != Constants.PATH_STREET || totalLanes < 2 || centered) return offset;
        float laneWidth = OneWay ? 2 * Outer / Count : (Outer - Inner) / Count;
        return Mathf.MoveTowards(offset, 0, Mathf.Min(.35f, laneWidth * .1f));
    }

    public IEnumerable<(float offset, bool dashed)> Boundaries()
    {
        if (centered) yield break;
        if (OneWay)
        {
            for (int i = 1; i < Count; i++) yield return (-Outer + 2 * Outer * i / Count, true);
            yield break;
        }
        if (divider <= 0)
        {
            if (Inner > 0) { yield return (-Inner, true); yield return (Inner, true); }
            else yield return (0, totalLanes == 2);
        }
        for (int i = 1; i < Count; i++)
        {
            float offset = Inner + (Outer - Inner) * i / Count;
            yield return (-offset, true);
            yield return (offset, true);
        }
    }
}
