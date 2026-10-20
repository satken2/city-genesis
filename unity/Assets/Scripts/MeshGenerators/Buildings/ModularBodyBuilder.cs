// Assets/Scripts/MeshGenerators/Buildings/ModularBodyBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Generate building walls, corners, and ground panels by assembling modular parts.
/// Handles only the body (walls/ground), excluding roof and rooftop props.
/// Return CombineInstance lists for callers to combine with roofs/rooftop props.
/// </summary>
public static class ModularBodyBuilder
{
    public const float GROUND_OFFSET = 0.005f;

    /// <summary>
    /// Holds wall/corner/ground-panel results from modular assembly.
    /// </summary>
    public struct BodyMeshData
    {
        public List<CombineInstance> CombineInstances;
        public List<Material> Materials;
        public List<Mesh> TemporaryMeshes;
        /// <summary>
        /// Resolved model-number folder name. Used for roof material and rooftop-prop lookup.
        /// Return 0 for DefaultBodyBuilder fallback.
        /// </summary>
        public int ResolvedSuffix;

        /// <summary>
        /// Body height (m) = floors × floor height. Parts always occupy this floor-height grid,
        /// so authored heights of unscaled first/top-floor parts do not affect it.
        /// Use this for roof/rooftop-prop Y placement and collider height.
        /// </summary>
        public float BuildingHeight;

        public bool IsValid => CombineInstances != null && CombineInstances.Count > 0;
    }

    /// <summary>
    /// targetHeight value meaning no Y scaling for wall/corner parts.
    /// First-floor (base), top-floor (top), and fence parts retain authored height.
    /// Only middle floors (mid) and construction scaffolding pass positive targetHeight to match floor height.
    /// </summary>
    private const float NO_Y_SCALE = 0f;

    /// <summary>
    /// Threshold (m) for detecting implausibly small actual-size parts.
    /// Without scaling, FBXs exported in centimeters or as 1×1 unit geometry
    /// would render extremely small. Warn so this can be noticed.
    /// </summary>
    private const float SUSPICIOUS_PART_SIZE = 0.3f;

    /// <summary>Tracks suspicious-dimension warnings, limiting them to once per mesh name.</summary>
    private static readonly HashSet<string> warnedUndersizedParts = new HashSet<string>();

    /// <summary>
    /// Warn for parts with extremely small actual XZ dimensions, once per mesh name.
    /// Render at authored size without correction (re-exporting at actual size is the proper fix).
    /// </summary>
    private static void WarnUndersizedPartOnce(Mesh mesh)
    {
        if (mesh == null) return;
        Vector3 size = mesh.bounds.size;
        if (Mathf.Max(size.x, size.z) >= SUSPICIOUS_PART_SIZE) return;
        if (!warnedUndersizedParts.Add(mesh.name)) return;
        Debug.LogWarning(
            $"[MBB] 寸法が極端に小さいパーツを検出: mesh='{mesh.name}' size={size}。" +
            $"パーツはスケールされないためこのサイズのまま描画されます。" +
            $"実寸(1ユニット=1m)で再エクスポートしてください。");
    }

    /// <summary>
    /// Simple structure holding segment generation results.
    /// </summary>
    public struct SegmentMeshData
    {
        public Mesh Mesh;
        public Material[] Materials;
        // Already placed individual meshes need no matrix. Place shared geometry during combining; avoid duplicate ownership registration.
        public Matrix4x4? Transform;
        public bool Shared;

        public bool IsValid => Mesh != null && Mesh.vertexCount > 0 && Mesh.subMeshCount > 0;
    }

    /// <summary>
    /// Assemble modular walls, corners, and ground panels.
    /// Callers perform final combining of generated CombineInstance / Material / Mesh lists.
    /// </summary>
    /// <param name="footprint">Building footprint vertices (CCW).</param>
    /// <param name="floors">Floor count.</param>
    /// <param name="prefix">Part prefix (b{bd_type} or a{variant}).</param>
    /// <param name="floorHeight">Height per floor. Determines floor Y (floor × floor height) and body height. Mid-floor walls/corners scale Y to this; first/top-floor parts remain unscaled.</param>
    /// <param name="visual_variant">Exterior variant (selects exterior models via BuildingMeshUtils.ResolveModelId).</param>
    /// <param name="building_shape">Building shape type (SHAPE_FULL processes only the top floor).</param>
    /// <param name="modelType">Model type. MODEL_MODULAR_GROUND omits the body, drawing only fences.</param>
    /// <param name="fenceFootprint">Parcel vertices for fences (CCW, local XZ relative to center_point; same as footprint). Null omits fences.</param>
    /// <param name="centerPointWorld">World coordinates of building center_point, used for terrain-height sampling.</param>
    /// <param name="floorMaterial">Ground-panel material already resolved by caller from master draw_floor and floor parts (a→b→common). Null omits panels.</param>
    /// <param name="occupiedFootprint">Footprint before entrance/exterior-stair setbacks. Scatter parts lie outside it. Null uses footprint.</param>
    /// <param name="visual_variant2">Random seed for scatter placement.</param>
    public static BodyMeshData GenerateBody(
        Vector2[] footprint,
        int floors,
        string prefix,
        float floorHeight,
        float visual_variant,
        int building_shape,
        int modelType = Constants.MODEL_MODULAR_FLAT,
        Vector2[] fenceFootprint = null,
        Vector3 centerPointWorld = default,
        bool isDynamic = false,
        Material floorMaterial = null,
        ExteriorStairBuilder.Layout stairs = null,
        Vector2[] occupiedFootprint = null,
        float visual_variant2 = 0f)
    {
        int variantNumber = BuildingMeshUtils.ResolveModelId(prefix, visual_variant);
        bool isGroundModel = modelType == Constants.MODEL_MODULAR_GROUND;

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] GenerateBody: prefix={prefix}, floors={floors}, floorHeight={floorHeight}, variantNumber={variantNumber}, building_shape={building_shape}, modelType={modelType}");

        int resolvedSuffix = ResolveBuildingVariant(prefix, variantNumber);
        if (resolvedSuffix < 0)
        {
            if (isGroundModel)
            {
                resolvedSuffix = 0;
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] mid-w1 なしだが MODEL_MODULAR_GROUND のため suffix=0 で継続 (prefix={prefix})");
            }
            else
            {
                Debug.LogWarning($"[MBB] mid-w1 が全く存在しないため DefaultBodyBuilder にフォールバック (prefix={prefix})");
                return DefaultBodyBuilder.GenerateBody(footprint, floors, floorHeight);
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] resolvedSuffix={resolvedSuffix}");

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> allMaterials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();
        var wallSources = new Dictionary<GameObject, Mesh>();
        var cornerSources = new Dictionary<(GameObject module, float angle), Mesh>();

        // Body height always follows the floor-height grid (floor Y = floor × floor height).
        // Unscaled first/top-floor parts whose authored heights differ from floor height may leave gaps
        // to the next floor or overlap its parts; this is intentional.
        float resolvedBuildingHeight = floors * floorHeight;

        // --- Building body (base / mid / top) ---
        if (!isGroundModel)
        {
            int n = footprint.Length;

            int floorStart, floorEnd;
            if (building_shape == Constants.SHAPE_FULL)
            {
                floorStart = floors - 1;
                floorEnd = floors;
            }
            else
            {
                floorStart = 0;
                floorEnd = floors;
            }

            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] 階範囲: {floorStart}〜{floorEnd - 1} (全{floorEnd - floorStart}階), 頂点数={n}");

            int midStart = -1, midCount = 0;
            float midY = 0f;

            for (int floor = floorStart; floor < floorEnd; floor++)
            {
                float floorY = floor * floorHeight;

                string level = (floor == 0) ? "base" : (floor == floors - 1) ? "top" : "mid";

                // Mid-floor components/openings/heights are identical; reuse the first placement with Y translation.
                if (level == "mid" && midStart >= 0)
                {
                    AppendTranslatedFloor(midStart, midCount, floorY - midY, combineInstances, allMaterials);
                    continue;
                }
                int floorPartStart = combineInstances.Count;

                // Scale only mid-floor Y to floor height; first/top floors retain authored height.
                // Check the floor level, so first/top floors falling back to mid-w1 etc.
                // due to missing base/top parts also remain unscaled.
                float targetHeight = (level == "mid") ? floorHeight : NO_Y_SCALE;

                for (int i = 0; i < n; i++)
                {
                    int position = i + 1;
                    Vector2 p0 = footprint[(i + n - 1) % n];
                    Vector2 p1 = footprint[i];
                    Vector2 p2 = footprint[(i + 1) % n];

                    GameObject wallModule = LoadPartWithFallback(prefix, level, "w", position, resolvedSuffix, out Material[] wallMaterials);
                    GameObject cornerModule = LoadPartWithFallback(prefix, level, "c", position, resolvedSuffix, out Material[] cornerMaterials);
                    if (stairs != null && stairs.OmitsCorner(i))
                        cornerModule = null;

                    if (wallModule != null)
                    {
                        Vector2 p3 = footprint[(i + 2) % n];
                        int nextPosition = (i + 1) % n + 1;
                        GameObject nextCornerModule = LoadPartWithFallback(prefix, level, "c", nextPosition, resolvedSuffix, out _);
                        if (stairs != null && stairs.OmitsCorner((i + 1) % n))
                            nextCornerModule = null;

                        // Inset wall tiling by the actual corner-part projections along this edge at each end.
                        // At the start, use vertex p1's outgoing arm; at the end, use vertex p2's incoming arm.
                        float startOffset = MeasureCornerExtentAlongEdge(cornerModule, p0, p1, p2, targetHeight, true);
                        float endOffset = MeasureCornerExtentAlongEdge(nextCornerModule, p1, p2, p3, targetHeight, false);

                        List<SegmentMeshData> wallSegments;
                        if (stairs != null && stairs.TryGetOpening(i, out float openingStart, out float openingEnd))
                        {
                            // Omit walls in connection strips. Stair parts provide opening railings/doors.
                            float length = Vector2.Distance(p1,p2);
                            Vector2 direction = (p2-p1).normalized;
                            float from = Mathf.Clamp(openingStart, startOffset, length-endOffset);
                            float to = Mathf.Clamp(openingEnd, startOffset, length-endOffset);
                            wallSegments = new List<SegmentMeshData>();
                            if (from > startOffset + .01f)
                                wallSegments.AddRange(BuildWallRun(p1,p1+direction*from,floorY,wallModule,wallMaterials,targetHeight,startOffset,0,wallSources,temporaryMeshes));
                            if (to < length-endOffset-.01f)
                                wallSegments.AddRange(BuildWallRun(p1+direction*to,p2,floorY,wallModule,wallMaterials,targetHeight,0,endOffset,wallSources,temporaryMeshes));
                        }
                        else wallSegments = BuildWallRun(p1, p2, floorY, wallModule, wallMaterials, targetHeight, startOffset, endOffset, wallSources, temporaryMeshes);
                        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] F{floor} 壁 pos{position}: {wallSegments.Count}セグメント, materials={FormatMaterials(wallMaterials)}");
                        foreach (var wallSegment in wallSegments)
                        {
                            if (wallSegment.IsValid)
                            {
                                AddSegmentToCombineList(wallSegment, combineInstances, allMaterials, temporaryMeshes);
                            }
                        }
                    }

                    if (cornerModule != null)
                    {
                        SegmentMeshData cornerSegment = CreateCornerPlacement(p0, p1, p2, floorY, cornerModule, cornerMaterials, targetHeight, cornerSources, temporaryMeshes);
                        if (cornerSegment.IsValid)
                        {
                            AddSegmentToCombineList(cornerSegment, combineInstances, allMaterials, temporaryMeshes);
                            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] F{floor} 角 pos{position}: OK, materials={FormatMaterials(cornerMaterials)}");
                        }
                    }
                }
                if (level == "mid")
                {
                    midStart = floorPartStart;
                    midCount = combineInstances.Count - midStart;
                    midY = floorY;
                }
            }
        }

        // No body parts and not GROUND → white-box fallback.
        if (!isGroundModel && combineInstances.Count == 0)
        {
            Debug.LogWarning($"[MBB] 建物パーツが0件のため DefaultBodyBuilder にフォールバック (prefix={prefix})");
            return DefaultBodyBuilder.GenerateBody(footprint, floors, floorHeight);
        }

        // --- Site decorations (fences/tiles/scatter/ground panels) ---
        // Try fences/scatter for all modelType values; tiles only for MODEL_MODULAR_GROUND.
        // Draw ground panels only with master draw_floor=yes (non-null floorMaterial).
        // Tiles/ground panels cover the full parcel polygon (fenceFootprint). Footprint-only panels
        // would be hidden beneath the body. Fall back to footprint when the parcel is unavailable.
        // Slope-supporting buildings (IsApplicableModelType=FLAT/HOUSE/GABLE) rest on a horizontal plane
        // plus concrete foundation, but site decorations (fences/tiles/ground panels) follow terrain.
        // Conformity uses centerPointWorld.y (terrain elevation at building center), matching
        // BuildingManager.ResolveBuildingBaseHeight placement on land.
        bool deformToTerrain = isDynamic;
        Vector2[] sitePolygon = fenceFootprint != null && fenceFootprint.Length >= 3
            ? fenceFootprint : footprint;
        GenerateSiteDecorations(
            prefix, resolvedSuffix,
            fenceFootprint,
            isGroundModel ? sitePolygon : null,
            sitePolygon,
            floorMaterial,
            centerPointWorld, deformToTerrain,
            combineInstances, allMaterials, temporaryMeshes,
            scatterFootprint: occupiedFootprint ?? footprint, scatterSeed: visual_variant2);

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] 壁・角・地面完了: {combineInstances.Count}インスタンス, {allMaterials.Count}マテリアル");

        return new BodyMeshData
        {
            CombineInstances = combineInstances,
            Materials = allMaterials,
            TemporaryMeshes = temporaryMeshes,
            ResolvedSuffix = resolvedSuffix,
            BuildingHeight = resolvedBuildingHeight
        };
    }

    /// <summary>
    /// Generate site decorations (fences/tiles/ground panels) and add them to combine lists.
    /// Shared by modular (GenerateBody) and prebuilt (PreBuiltBuildingMeshBuilder) buildings.
    /// Receive polygons in local XZ relative to center_point (same convention as footprint)
    /// (pass parcel polygons already transformed by BuildingMeshUtils.ResolveParcelContext).
    ///
    /// - Fences: place unscaled ground-level fence-w / fence-c parts around fenceFootprint.
    ///   Skip edges/corners with unresolved parts. Null omits fences.
    ///   If opening center/radius are given, omit walls/corners within that circle for access.
    /// - Tiles: tile the interior of tilePolygon with tile parts (GenerateTiles).
    ///   Do nothing if no tile part exists. Null omits tiles.
    /// - Scatter: place scatter parts in floorPolygon's vacant area outside building scatterFootprint
    ///   (GenerateScatter). scatterSeed controls randomness. Null scatterFootprint omits scatter.
    /// - Ground panels: create a horizontal floorPolygon panel at GROUND_OFFSET.
    ///   Visibility/material follow caller-resolved floorMaterial from master draw_floor
    ///   and floor parts (a{variant}→b{bd_type}→common).
    ///   Null floorMaterial (draw_floor disabled or no floor parts anywhere) omits
    ///   ground panels (no default material fallback). Null floorPolygon
    ///   also omits them.
    /// - deformToTerrain=true makes fence/tile/ground-panel vertices follow terrain
    ///   (for ultra-near GROUND / PREBUILT).
    /// </summary>
    public static void GenerateSiteDecorations(
        string prefix, int suffix,
        Vector2[] fenceFootprint, Vector2[] tilePolygon, Vector2[] floorPolygon, Material floorMaterial,
        Vector3 centerPointWorld, bool deformToTerrain,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes,
        Vector2? fenceOpeningCenter = null, float fenceOpeningRadius = 0f,
        Vector2[] scatterFootprint = null, float scatterSeed = 0f)
    {
        if (BuildingExteriorRenderer.Capture != null)
        {
            BuildingExteriorRenderer.Capture.Add((meshes, materials, temporary) => GenerateSiteDecorations(
                prefix, suffix, fenceFootprint, tilePolygon, floorPolygon, floorMaterial, centerPointWorld,
                deformToTerrain, meshes, materials, temporary, fenceOpeningCenter, fenceOpeningRadius,
                scatterFootprint, scatterSeed));
            return;
        }
        // --- Fences (fence) ---
        // Skip missing parts.
        if (fenceFootprint != null && fenceFootprint.Length >= 3)
        {
            int fn = fenceFootprint.Length;
            for (int i = 0; i < fn; i++)
            {
                int position = i + 1;
                Vector2 p0 = fenceFootprint[(i + fn - 1) % fn];
                Vector2 p1 = fenceFootprint[i];
                Vector2 p2 = fenceFootprint[(i + 1) % fn];

                GameObject wallModule = LoadPartWithFallback(prefix, "fence", "w", position, suffix, out Material[] wallMaterials);
                GameObject cornerModule = LoadPartWithFallback(prefix, "fence", "c", position, suffix, out Material[] cornerMaterials);

                if (wallModule != null)
                {
                    Vector2 p3 = fenceFootprint[(i + 2) % fn];
                    int nextPosition = (i + 1) % fn + 1;
                    GameObject nextCornerModule = LoadPartWithFallback(prefix, "fence", "c", nextPosition, suffix, out _);

                    // As for building walls, inset tiling by fence-corner projections along the edge at both ends.
                    // Fence corners are unscaled (targetHeight=0), so actual dimensions give the projection.
                    float startOffset = MeasureCornerExtentAlongEdge(cornerModule, p0, p1, p2, 0f, true);
                    float endOffset = MeasureCornerExtentAlongEdge(nextCornerModule, p1, p2, p3, 0f, false);

                    List<SegmentMeshData> wallSegments;
                    Vector2 edge = p2-p1;
                    float length = edge.magnitude;
                    Vector2 direction = length > .001f ? edge/length : Vector2.zero;
                    Vector2 toOpening = fenceOpeningCenter.HasValue ? fenceOpeningCenter.Value-p1 : Vector2.zero;
                    float along = Vector2.Dot(toOpening,direction);
                    float perpendicularSquared = (toOpening-direction*along).sqrMagnitude;
                    if (fenceOpeningCenter.HasValue && fenceOpeningRadius > 0f && length > .001f
                        && perpendicularSquared < fenceOpeningRadius*fenceOpeningRadius)
                    {
                        // Leave the circle/edge intersection open. Near corners, the same opening affects both adjacent edges.
                        float half = Mathf.Sqrt(fenceOpeningRadius*fenceOpeningRadius-perpendicularSquared);
                        wallSegments = GenerateFenceSegments(p1,p2,wallModule,wallMaterials,startOffset,
                            Mathf.Max(endOffset,length-(along-half)));
                        wallSegments.AddRange(GenerateFenceSegments(p1,p2,wallModule,wallMaterials,
                            Mathf.Max(startOffset,along+half),endOffset));
                    }
                    else wallSegments = GenerateFenceSegments(p1,p2,wallModule,wallMaterials,startOffset,endOffset);
                    foreach (var wallSegment in wallSegments)
                    {
                        if (wallSegment.IsValid)
                        {
                            if (deformToTerrain)
                            {
                                DeformMeshToTerrain(wallSegment.Mesh, centerPointWorld);
                            }
                            AddSegmentToCombineList(wallSegment, combineInstances, allMaterials, temporaryMeshes);
                        }
                    }
                }

                if (cornerModule != null && (!fenceOpeningCenter.HasValue
                    || Vector2.Distance(p1,fenceOpeningCenter.Value) >= fenceOpeningRadius))
                {
                    SegmentMeshData cornerSegment = GenerateFenceCorner(p0, p1, p2, cornerModule, cornerMaterials);
                    if (cornerSegment.IsValid)
                    {
                        if (deformToTerrain)
                        {
                            DeformMeshToTerrain(cornerSegment.Mesh, centerPointWorld);
                        }
                        AddSegmentToCombineList(cornerSegment, combineInstances, allMaterials, temporaryMeshes);
                    }
                }
            }
        }

        // --- Tile objects ---
        if (tilePolygon != null && tilePolygon.Length >= 3)
        {
            GenerateTiles(tilePolygon, prefix, suffix, centerPointWorld, deformToTerrain,
                          combineInstances, allMaterials, temporaryMeshes);
        }

        // --- Scatter parts ---
        if (scatterFootprint != null && floorPolygon != null && floorPolygon.Length >= 3)
        {
            GenerateScatter(prefix, suffix, floorPolygon, scatterFootprint, scatterSeed, centerPointWorld, deformToTerrain,
                            combineInstances, allMaterials, temporaryMeshes);
        }

        // --- Ground panels ---
        // The caller has already resolved floorMaterial from draw_floor and floor parts
        // with a{variant}→b{bd_type}→common fallbacks. Null omits drawing.
        if (floorMaterial != null && floorPolygon != null && floorPolygon.Length >= 3)
        {
            Mesh floorMesh = deformToTerrain
                ? GenerateTerrainConformingFloorMesh(floorPolygon, centerPointWorld)
                : GenerateFloorMesh(floorPolygon, GROUND_OFFSET);
            if (floorMesh != null)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] 地面パネル: material={FormatMaterial(floorMaterial)}");
                combineInstances.Add(new CombineInstance
                {
                    mesh = floorMesh,
                    subMeshIndex = 0,
                    transform = Matrix4x4.identity
                });
                allMaterials.Add(floorMaterial);
                temporaryMeshes.Add(floorMesh);
            }
        }
    }

    /// <summary>
    /// Get the source wall mesh. Prefer MeshFilter, matching actual placement (BuildWallRun).
    /// </summary>
    private static Mesh GetWallSourceMesh(GameObject module)
    {
        if (module == null) return null;

        MeshFilter mf = module.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            return mf.sharedMesh;
        }

        SkinnedMeshRenderer smr = module.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr != null && smr.sharedMesh != null)
        {
            return smr.sharedMesh;
        }

        return null;
    }

    /// <summary>
    /// Get the corner-prefab source mesh for measurement.
    /// Prefer SkinnedMeshRenderer, matching actual placement (GenerateCornerCore).
    /// </summary>
    private static Mesh GetPartSourceMesh(GameObject module)
    {
        if (module == null) return null;

        SkinnedMeshRenderer smr = module.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr != null && smr.sharedMesh != null)
        {
            return smr.sharedMesh;
        }

        MeshFilter mf = module.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            return mf.sharedMesh;
        }

        return null;
    }

    /// <summary>
    /// Generate a horizontal ground-panel polygon mesh.
    /// </summary>
    public static Mesh GenerateFloorMesh(Vector2[] footprint, float height)
    {
        return BuildingMeshBuilder.GenerateHorizontalPolygonMesh(footprint, height);
    }

    /// <summary>
    /// Generate a ground-panel mesh sloped to follow terrain.
    /// Clip terrain-grid triangles to the site outline, keeping interiors coplanar with terrain.
    /// Use provisional SITE_FLOOR_MAX_EDGE subdivision only while tiles are unloaded; regenerate after terrain arrives.
    ///
    /// Convert each vertex height to local coordinates relative to center_point terrain height (centerPointWorld.y).
    /// Assumes the GameObject is placed at center_point height
    /// (matching BuildingManager.CreateOrUpdateBuildingObject placement).
    ///
    /// Also shared by non-building ground panels (station-house path exclusive area, StationHousePath).
    /// Pass footprint in unrotated local XZ with origin at the center.
    /// </summary>
    public static Mesh GenerateTerrainConformingFloorMesh(Vector2[] footprint, Vector3 centerPointWorld)
    {
        if (footprint == null || footprint.Length < 3) return null;

        Mesh aligned = BuildingMeshBuilder.GenerateTerrainSurfaceMesh(footprint, centerPointWorld, GROUND_OFFSET);
        if (aligned != null) return aligned;

        float baseGround = centerPointWorld.y;

        // Ear clipping (valid for concave polygons) → long-edge midpoint splitting to create interior vertices.
        List<int> baseTriangles = BuildingMeshBuilder.TriangulateOrFan(footprint);
        if (baseTriangles.Count < 3) return null;

        var verts = new List<Vector2>(footprint);
        var triangles3 = BuildingMeshBuilder.SubdivideTrianglesByEdgeLength(
            verts, baseTriangles, LocalConstants.SITE_FLOOR_MAX_EDGE);
        if (triangles3.Count == 0) return null;

        // Assemble all vertices (including interior ones) as shared vertices at terrain elevation.
        var vertices = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            vertices[i] = GetTerrainConformingVertex(verts[i], centerPointWorld, baseGround);
        }

        var triangles = new List<int>(triangles3.Count * 3);
        foreach (var (a, b, c) in triangles3)
        {
            // A CCW polygon directly on XZ has downward normals; reverse winding
            // to face upward (same convention as GenerateHorizontalPolygonMesh).
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(b);
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Get terrain height at the specified 2D position and return local coordinates relative to center_point terrain height.
    /// Local Y = terrainHeight - baseGround + GROUND_OFFSET;
    /// placing the GameObject at baseGround raises each vertex GROUND_OFFSET above terrain.
    /// </summary>
    private static Vector3 GetTerrainConformingVertex(Vector2 point, Vector3 centerPointWorld, float baseGround)
    {
        float h = Utils.GetGroundHeight(new Vector3(point.x + centerPointWorld.x, 1000f, point.y + centerPointWorld.z));
        return new Vector3(point.x, h - baseGround + GROUND_OFFSET, point.y);
    }

    /// <summary>
    /// Deform each mesh vertex to terrain height at its horizontal position.
    /// Preserve relative source vertex heights (e.g. fence tops).
    /// </summary>
    private static void DeformMeshToTerrain(Mesh mesh, Vector3 centerPointWorld)
    {
        if (mesh == null || mesh.vertexCount == 0) return;

        Vector3[] vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 worldPos = vertices[i] + centerPointWorld;
            float terrainY = Utils.GetGroundHeight(new Vector3(worldPos.x, 1000f, worldPos.z));
            vertices[i].y = terrainY + vertices[i].y - centerPointWorld.y;
        }
        mesh.vertices = vertices;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    /// <summary>
    /// MODEL_MODULAR_GROUND only. Tile the footprint area with tile parts.
    /// Do not rescale models; place them on grid positions fitting inside the polygon.
    /// With isDynamic, deform to terrain after placement.
    /// Do nothing if no tile part exists (only fences/ground panels render).
    /// </summary>
    private static void GenerateTiles(
        Vector2[] polygon,
        string prefix,
        int suffix,
        Vector3 centerPointWorld,
        bool isDynamic,
        List<CombineInstance> combineInstances,
        List<Material> allMaterials,
        List<Mesh> temporaryMeshes)
    {
        if (polygon == null || polygon.Length < 3) return;

        GameObject tileModule = BuildingMeshUtils.GetPartNoPosition(prefix, "tile", suffix, true);
        if (tileModule == null)
        {
            // tile parts are optional. Missing parts draw nothing (normal behavior).
            return;
        }

        MeshFilter mf = tileModule.GetComponentInChildren<MeshFilter>();
        SkinnedMeshRenderer smr = tileModule.GetComponentInChildren<SkinnedMeshRenderer>();

        Mesh sourceMesh = null;
        Mesh bakedMesh = null;
        if (smr != null && smr.sharedMesh != null)
        {
            bakedMesh = new Mesh();
            smr.BakeMesh(bakedMesh);
            sourceMesh = bakedMesh;
        }
        else if (mf != null && mf.sharedMesh != null)
        {
            sourceMesh = mf.sharedMesh;
        }
        if (sourceMesh == null)
        {
            if (bakedMesh != null) Object.Destroy(bakedMesh);
            return;
        }

        Vector3 meshSize = sourceMesh.bounds.size;
        Vector3 meshMin = sourceMesh.bounds.min;
        if (meshSize.x <= 0.001f || meshSize.z <= 0.001f)
        {
            Debug.LogWarning($"[MBB] tile mesh size is near zero: {meshSize} (tile={prefix}/{suffix}/tile). Skipping.");
            if (bakedMesh != null) Object.Destroy(bakedMesh);
            return;
        }

        float stepX = meshSize.x;
        float stepZ = meshSize.z;

        // Skip drawing and log an error if tile size is extremely small.
        if (stepX < 0.1f || stepZ < 0.1f)
        {
            Debug.LogError($"[MBB] タイルのサイズが極端に小さいため描画をスキップしました。サイズを確認してください (tile={prefix}/{suffix}/tile, size=({stepX:F4},{meshSize.y:F4},{stepZ:F4}))");
            if (bakedMesh != null) Object.Destroy(bakedMesh);
            return;
        }

        Material[] tileMaterials = BuildingMeshUtils.GetModuleMaterials(tileModule);

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector2 p in polygon)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minZ) minZ = p.y;
            if (p.y > maxZ) maxZ = p.y;
        }

        // Estimate tile count from land area; skip excessive counts to prevent freezing.
        float widthX = maxX - minX;
        float widthZ = maxZ - minZ;
        float stepsX = widthX / stepX;
        float stepsZ = widthZ / stepZ;
        long estimatedTiles = (long)Mathf.Ceil(stepsX) * (long)Mathf.Ceil(stepsZ);

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            () => $"[MBB] GenerateTiles: prefix={prefix}, suffix={suffix}, tileMeshSize={meshSize}, AABB=({widthX:F2}x{widthZ:F2}), steps=({stepsX:F1}x{stepsZ:F1}), estimatedTiles={estimatedTiles}, isDynamic={isDynamic}");

        if (estimatedTiles > 10000)
        {
            Debug.LogError($"[MBB] タイルの見積もり配置数({estimatedTiles})が多すぎるため描画をスキップしました。土地の広さに対してタイルのサイズが小さすぎます (tile={prefix}/{suffix}/tile, size=({stepX:F4},{meshSize.y:F4},{stepZ:F4}))");
            if (bakedMesh != null) Object.Destroy(bakedMesh);
            return;
        }

        int tileCount = 0;

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[MBB] タイル配置開始: meshSize=({meshSize.x:F3},{meshSize.y:F3},{meshSize.z:F3}), " +
            $"AABB=({minX:F3},{minZ:F3})-({maxX:F3},{maxZ:F3}), isDynamic={isDynamic}");

        bool canDeform = isDynamic && sourceMesh.isReadable;
        if (isDynamic && !sourceMesh.isReadable)
        {
            Debug.LogWarning($"[MBB] tile mesh '{sourceMesh.name}' is not readable. Terrain deformation skipped.");
        }

        for (float gx = minX; gx + stepX <= maxX + 0.001f; gx += stepX)
        {
            for (float gz = minZ; gz + stepZ <= maxZ + 0.001f; gz += stepZ)
            {
                float centerX = gx + stepX * 0.5f;
                float centerZ = gz + stepZ * 0.5f;

                if (!PolygonUtils.Contains(polygon, new Vector2(centerX, centerZ)))
                {
                    continue;
                }

                // Align the minimum bounding-box corner, not origin, to grid corner/ground. Underground extensions raise the entire tile by their depth.
                Vector3 offset = new Vector3(gx - meshMin.x, GROUND_OFFSET - meshMin.y, gz - meshMin.z);

                if (canDeform)
                {
                    Mesh meshInstance = Object.Instantiate(sourceMesh);
                    Vector3[] vertices = meshInstance.vertices;
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        vertices[v] += offset;
                    }
                    meshInstance.vertices = vertices;
                    meshInstance.RecalculateNormals();
                    meshInstance.RecalculateBounds();

                    DeformMeshToTerrain(meshInstance, centerPointWorld);

                    SegmentMeshData tileSegment = new SegmentMeshData
                    {
                        Mesh = meshInstance,
                        Materials = tileMaterials,
                    };
                    AddSegmentToCombineList(tileSegment, combineInstances, allMaterials, temporaryMeshes);
                }
                else
                {
                    Matrix4x4 matrix = Matrix4x4.Translate(offset);
                    int subMeshCount = sourceMesh.subMeshCount;
                    for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                    {
                        CombineInstance ci = new CombineInstance
                        {
                            mesh = sourceMesh,
                            subMeshIndex = subMeshIndex,
                            transform = matrix
                        };
                        combineInstances.Add(ci);

                        Material material = null;
                        if (tileMaterials != null && tileMaterials.Length > 0)
                        {
                            int materialIndex = Mathf.Min(subMeshIndex, tileMaterials.Length - 1);
                            material = tileMaterials[materialIndex];
                        }
                        allMaterials.Add(material);
                    }
                }

                tileCount++;
            }
        }

        if (bakedMesh != null)
        {
            Object.Destroy(bakedMesh);
        }

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, () => $"[MBB] タイル: {tileCount}個配置 ({prefix}/{suffix}/tile)");
    }

    /// <summary>Search limit for scatter parts (scatter1, 2, …).</summary>
    private const int MAX_SCATTER_PARTS = 16;

    /// <summary>
    /// Find scatter1, 2, … sequentially from 1 (stop at gaps); place actual-size parts at <see cref="ScatterLayout"/> positions.
    /// Combine parts including node transforms; align origin to placement ground. For terrain conformity, ground is the lowest
    /// of four bounding-box-corner heights, burying the uphill side to prevent floating (same as port-yard cargo). Do nothing if absent.
    /// </summary>
    private static void GenerateScatter(
        string prefix, int suffix, Vector2[] site, Vector2[] footprint, float seed,
        Vector3 centerPointWorld, bool deformToTerrain,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        var meshes = new List<Mesh>();
        var meshMaterials = new List<Material[]>();
        var kinds = new List<Bounds>();
        for (int n = 1; n <= MAX_SCATTER_PARTS; n++)
        {
            GameObject part = BuildingMeshUtils.GetPartNoPosition(prefix, $"scatter{n}", suffix, true);
            if (part == null)
            {
                break;
            }
            Mesh mesh = BuildingMeshUtils.CombineModuleMeshes(part, out Material[] materials);
            if (mesh == null)
            {
                continue;
            }
            temporaryMeshes.Add(mesh);
            meshes.Add(mesh);
            meshMaterials.Add(materials);
            kinds.Add(mesh.bounds);
        }
        if (kinds.Count == 0)
        {
            return;
        }

        List<ScatterLayout.Item> items = ScatterLayout.Plan(site, footprint, kinds, Mathf.FloorToInt(Mathf.Clamp01(seed) * 1000000f));
        foreach (ScatterLayout.Item item in items)
        {
            float baseY = 0f;
            if (deformToTerrain)
            {
                baseY = float.MaxValue;
                foreach (Vector2 corner in item.Corners)
                {
                    float ground = Utils.GetGroundHeight(new Vector3(corner.x + centerPointWorld.x, 1000f, corner.y + centerPointWorld.z));
                    baseY = Mathf.Min(baseY, ground - centerPointWorld.y);
                }
            }
            Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(item.Position.x, baseY, item.Position.y), item.Rotation, Vector3.one);
            Mesh mesh = meshes[item.Kind];
            Material[] materials = meshMaterials[item.Kind];
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                combineInstances.Add(new CombineInstance { mesh = mesh, subMeshIndex = subMeshIndex, transform = matrix });
                allMaterials.Add(materials != null && subMeshIndex < materials.Length ? materials[subMeshIndex] : null);
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, () => $"[MBB] 散布パーツ: {items.Count}個配置 ({prefix}/{suffix}/scatter*)");
    }

    /// <summary>
    /// Resolve the mid-w1 folder.
    /// For the selected model number (BuildingMeshUtils.ResolveModelId), find the largest suffix
    /// at or below it in the model-number list where `mid-w1` actually exists.
    /// Return -1 if mid-w1 is entirely absent.
    /// For MODEL_MODULAR_GROUND, callers fall back to suffix=0 and continue.
    /// </summary>
    public static int ResolveBuildingVariant(string prefix, int variantNumber)
    {
        int bestSuffix = -1;
        foreach (int suffix in BuildingMeshUtils.ModelIds(prefix))
        {
            if (suffix > variantNumber) continue;
            bool exists = BuildingMeshUtils.HasPart(prefix, "mid", "w", 1, suffix);
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   モデル {suffix}: mid-w1 {(exists ? "あり" : "なし")}");
            if (exists)
            {
                if (suffix > bestSuffix) bestSuffix = suffix;
            }
        }

        return bestSuffix;
    }

    /// <summary>
    /// Load a part. Apply position fallback (w3→w1) and level fallback
    /// (base→mid, top→mid) to both walls (w) and corners (c).
    /// Fences search only down to position 1 at the same fence level, never substituting building walls/corners.
    /// Buildings with no mid corner parts return null and omit corners;
    /// zero projection lets walls fill the whole edge (corners are optional per building).
    /// </summary>
    private static GameObject LoadPartWithFallback(string prefix, string level, string type, int position, int suffix, out Material[] materials)
    {
        string desc = $"{prefix}/{suffix}/{level}-{type}{position}";

        // 1. Try the specified position.
        GameObject part = BuildingMeshUtils.GetPart(prefix, level, type, position, suffix, true);
        if (part != null)
        {
            materials = BuildingMeshUtils.GetModuleMaterials(part);
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   {desc} → 取得成功");
            return part;
        }

        // 2. Position fallback: try position 1.
        if (position != 1)
        {
            part = BuildingMeshUtils.GetPart(prefix, level, type, 1, suffix, true);
            if (part != null)
            {
                materials = BuildingMeshUtils.GetModuleMaterials(part);
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   {desc} → 位置フォールバック → {level}-{type}1 取得成功");
                return part;
            }
        }

        // 3. Level fallback: try mid.
        if (level == "base" || level == "top")
        {
            part = BuildingMeshUtils.GetPart(prefix, "mid", type, position, suffix, true);
            if (part != null)
            {
                materials = BuildingMeshUtils.GetModuleMaterials(part);
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   {desc} → レベルフォールバック → mid-{type}{position} 取得成功");
                return part;
            }

            // Position + level fallback.
            if (position != 1)
            {
                part = BuildingMeshUtils.GetPart(prefix, "mid", type, 1, suffix, true);
                if (part != null)
                {
                    materials = BuildingMeshUtils.GetModuleMaterials(part);
                    UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   {desc} → レベル+位置フォールバック → mid-{type}1 取得成功");
                    return part;
                }
            }
        }

        // Corners/fences are optional; omit missing parts without warnings.
        if (type == "c" || level == "fence")
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   {desc} → 任意パーツなし（配置を省略）");
        }
        else
        {
            Debug.LogWarning($"[MBB]   {desc} → すべてのフォールバック失敗 (パーツなし)");
        }
        materials = null;
        return null;
    }

    /// <summary>
    /// Add SegmentMeshData to the CombineInstance list.
    /// </summary>
    private static void AddSegmentToCombineList(SegmentMeshData segment, List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        int subMeshCount = segment.Mesh.subMeshCount;
        for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
        {
            CombineInstance ci = new CombineInstance
            {
                mesh = segment.Mesh,
                subMeshIndex = subMeshIndex,
                transform = segment.Transform ?? Matrix4x4.identity
            };
            combineInstances.Add(ci);

            Material material = null;
            if (segment.Materials != null && segment.Materials.Length > 0)
            {
                int materialIndex = Mathf.Min(subMeshIndex, segment.Materials.Length - 1);
                material = segment.Materials[materialIndex];
            }
            allMaterials.Add(material);
        }
        if (!segment.Shared) temporaryMeshes.Add(segment.Mesh);
    }

    private static void AppendTranslatedFloor(int start, int count, float y,
        List<CombineInstance> instances, List<Material> materials)
    {
        for (int i = start; i < start + count; i++)
        {
            var part = instances[i];
            var transform = part.transform;
            transform.m13 += y;
            part.transform = transform;
            instances.Add(part);
            materials.Add(materials[i]);
        }
    }

    /// <summary>
    /// Return actual corner-part projection (m) along an adjacent edge when placed at p1.
    /// Used to determine wall tiling start/end (= corner-part endpoint).
    ///
    /// Instead of measuring local X/Z AABB, take the maximum projection along the edge of vertices
    /// with exactly the same rotation/scale as actual placement. This ensures:
    /// - Correct values for arms authored along either +X/+Z or -X/-Z.
    /// - Edgeward details such as canopies/stringcourses count toward projection.
    /// - Missing corner parts return 0 (= tile walls across the whole edge).
    /// Walls therefore cannot sink into corners or separate from them.
    ///
    /// Even when bone deformation (GenerateSkinnedCorner) opens/closes corners, arms rotate rigidly
    /// with their edges. Project onto edge directions in the bind pose (90° interior angle),
    /// ±45° from the bisector. Projecting undeformed geometry onto undeformed edges equals
    /// projecting deformed geometry onto actual edges, independent of deformation details.
    /// </summary>
    /// <param name="towardNext">true measures outgoing projection (p1→p2); false measures incoming projection (p0→p1).</param>
    private static float MeasureCornerExtentAlongEdge(
        GameObject cornerModule, Vector2 p0, Vector2 p1, Vector2 p2, float targetHeight, bool towardNext)
    {
        if (cornerModule == null) return 0f;

        // Actual placement (GenerateCornerCore) prefers SkinnedMeshRenderer; match that for measurement.
        Mesh sourceMesh = GetPartSourceMesh(cornerModule);
        if (sourceMesh == null || sourceMesh.vertexCount == 0) return 0f;

        Quaternion rotation = ComputeCornerRotation(p0, p1, p2, out Vector2 bisector, out float interiorAngle);
        if (IsUnsupportedConcaveCorner(cornerModule, interiorAngle)) return 0f;
        Vector3 scale = DetermineCornerScale(sourceMesh, targetHeight);

        // Bind-pose edge directions (±45° from bisector). The sign depends on which side
        // of the bisector the outgoing edge lies, independent of polygon winding.
        Vector2 v2 = (p2 - p1).normalized;
        float side = (bisector.x * v2.y - bisector.y * v2.x) >= 0f ? 1f : -1f;
        float bindAngleRad =
            (Mathf.Atan2(bisector.y, bisector.x) * Mathf.Rad2Deg + side * (towardNext ? 45f : -45f)) * Mathf.Deg2Rad;
        Vector3 edgeDir = new Vector3(Mathf.Cos(bindAngleRad), 0f, Mathf.Sin(bindAngleRad));

        Vector3[] vertices = GetCachedPartVertices(sourceMesh);
        float maxExtent = 0f;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 placed = rotation * Vector3.Scale(vertices[i], scale);
            float t = placed.x * edgeDir.x + placed.z * edgeDir.z;
            if (t > maxExtent) maxExtent = t;
        }

        return maxExtent;
    }

    /// <summary>
    /// Vertex-array cache for projection measurement. `Mesh.vertices` copies arrays on every call,
    /// creating GC pressure when measuring floors × vertices. Targets are shared prefab-asset meshes,
    /// never destroyed or modified, so per-mesh caching is safe.
    /// </summary>
    private static readonly Dictionary<Mesh, Vector3[]> partVertexCache = new Dictionary<Mesh, Vector3[]>();

    private static Vector3[] GetCachedPartVertices(Mesh mesh)
    {
        if (!partVertexCache.TryGetValue(mesh, out Vector3[] vertices))
        {
            vertices = mesh.vertices;
            partVertexCache[mesh] = vertices;
        }
        return vertices;
    }

    /// <summary>
    /// Compute corner-part Y rotation. Shared by placement (GenerateCornerCore) and
    /// projection measurement (MeasureCornerExtentAlongEdge) to guarantee identical values.
    ///
    /// Face the corner toward the two-edge bisector. The final +135 offsets the authored orientation
    /// (zero-rotation exported corners have local front shifted 135° around Y).
    /// Bone bending is independent of this rotation, so 135° does not affect bending,
    /// only the orientation of the entire bent corner.
    /// </summary>
    /// <param name="bisector">Interior-angle bisector (unit vector), inward even for concave corners.</param>
    /// <param name="interiorAngle">Interior angle in degrees, 0–360.</param>
    private static Quaternion ComputeCornerRotation(
        Vector2 p0, Vector2 p1, Vector2 p2, out Vector2 bisector, out float interiorAngle)
    {
        Vector2 v1 = (p0 - p1).normalized;
        Vector2 v2 = (p2 - p1).normalized;

        interiorAngle = Vector2.SignedAngle(v2, v1);
        if (interiorAngle < 0f) interiorAngle += 360f;

        // Vector sums always bisect the smaller angle, pointing outward at concave corners.
        // Rotate halfway through the interior angle from the CCW outgoing edge for continuous inward direction near 180°.
        float interiorBisectorAngle = Mathf.Atan2(v2.y, v2.x) + interiorAngle * 0.5f * Mathf.Deg2Rad;
        bisector = new Vector2(Mathf.Cos(interiorBisectorAngle), Mathf.Sin(interiorBisectorAngle));

        float bisectorAngle = Mathf.Atan2(bisector.y, bisector.x) * Mathf.Rad2Deg;
        return Quaternion.Euler(0, -bisectorAngle + 90 + 135, 0);
    }

    /// <summary>Threshold (m) treating edge length/effective tiling length as effectively zero.</summary>
    private const float MIN_WALL_RUN_LENGTH = 0.01f;

    /// <summary>
    /// Generate edge-segment meshes.
    /// Only compute placement; return individual unit meshes without combining.
    ///
    /// Scaling: depth (Z) always retains authored size. Match height (Y) to targetHeight only when positive
    /// (middle floors/construction scaffolding); NO_Y_SCALE retains authored height
    /// (first/top floors/fences). Tile the edge in authored-width units, absorbing remainders evenly
    /// with X stretching.
    /// </summary>
    /// <param name="startOffset">Length occupied by the start corner (m), from MeasureCornerExtentAlongEdge.</param>
    /// <param name="endOffset">Length occupied by the end corner (m).</param>
    public static List<SegmentMeshData> GenerateWallSegments(
        Vector2 p0, Vector2 p1, float floorY,
        GameObject wallModule, Material[] sharedMaterials,
        float targetHeight,
        float startOffset, float endOffset)
    {
        return BuildWallRun(p0, p1, floorY, wallModule, sharedMaterials, targetHeight, startOffset, endOffset);
    }

    /// <summary>
    /// Generate fence-edge segment meshes.
    /// Keep source wall dimensions without Y scaling (authored actual size).
    /// Use building-wall logic (BuildWallRun) for tiling/remainder distribution.
    /// </summary>
    /// <param name="startOffset">Length occupied by the start fence corner (m).</param>
    /// <param name="endOffset">Length occupied by the end fence corner (m).</param>
    public static List<SegmentMeshData> GenerateFenceSegments(
        Vector2 p0, Vector2 p1, GameObject wallModule, Material[] sharedMaterials,
        float startOffset, float endOffset)
    {
        return BuildWallRun(p0, p1, 0f, wallModule, sharedMaterials, NO_Y_SCALE, startOffset, endOffset);
    }

    /// <summary>
    /// Shared tiling of wall-style parts (base/mid/top-w, fence-w, scaffold-wall) along one edge.
    ///
    /// Placement:
    /// 1. Subtract corner lengths at both ends (startOffset / endOffset) from edge length for effective length.
    ///    Walls begin where the first corner ends and end where the next corner begins.
    /// 2. Divide by authored part width and round to nearest for tile count. Flooring would absorb
    ///    remainders only by stretching (up to 2×); allowing shrinkage limits distortion.
    /// 3. Divide effective length exactly by tile count for slot width; X-scale parts
    ///    to fit. The total always exactly matches effective length without protrusions/gaps.
    /// 4. Place units so mesh X bounds match their slots. Parts with pivots offset
    ///    from their width centers (asymmetric parts) still align at the ends. For standard centered-pivot parts,
    ///    the origin lies at slot center, matching existing placement.
    ///
    /// targetHeight ≤ 0 disables Y scaling (first/top-floor walls/fences).
    /// Positive values fit height to targetHeight only for middle floors and construction scaffolding.
    /// </summary>
    private static List<SegmentMeshData> BuildWallRun(
        Vector2 p0, Vector2 p1, float baseY,
        GameObject wallModule, Material[] sharedMaterials,
        float targetHeight,
        float startOffset, float endOffset,
        Dictionary<GameObject, Mesh> sharedSources = null, List<Mesh> temporaryMeshes = null)
    {
        List<SegmentMeshData> segments = new List<SegmentMeshData>();

        if (wallModule == null) return segments;

        Vector3 start = new Vector3(p0.x, baseY, p0.y);
        Vector3 end = new Vector3(p1.x, baseY, p1.y);
        float totalLength = Vector3.Distance(start, end);
        if (totalLength <= MIN_WALL_RUN_LENGTH) return segments;
        Vector3 direction = (end - start) / totalLength;

        SkinnedMeshRenderer smr = wallModule.GetComponentInChildren<SkinnedMeshRenderer>();
        Mesh sourceMesh = GetWallSourceMesh(wallModule);
        if (sourceMesh == null) return segments;

        Bounds bounds = sourceMesh.bounds;
        Vector3 originalSize = bounds.size;

        // Effective wall tiling length after subtracting corner occupancy at both ends.
        float clampedStart = Mathf.Max(0f, startOffset);
        float clampedEnd = Mathf.Max(0f, endOffset);
        float effectiveLength = totalLength - (clampedStart + clampedEnd);

        // Omit walls on short edges entirely occupied by their corner parts.
        // Otherwise they overlap corners and negative scale inverts the mesh.
        if (effectiveLength <= MIN_WALL_RUN_LENGTH)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[MBB-WALL] 有効長なし（角パーツで辺が埋まる）: totalLength={totalLength:F3} startOffset={clampedStart:F3} endOffset={clampedEnd:F3}");
            return segments;
        }

        WarnUndersizedPartOnce(sourceMesh);

        // Fit height only for positive targetHeight; width/depth retain authored size.
        // targetHeight <= 0 (body walls/fences) retains authored height.
        float sourceHeight = Mathf.Max(originalSize.y, 0.001f);
        float scaleY = targetHeight > 0f ? targetHeight / sourceHeight : 1f;
        float unitActualLength = originalSize.x;

        // Skip drawing for invalid (near-zero) unit width, which would make placement-count calculations diverge.
        if (unitActualLength <= 0.001f)
        {
            Debug.LogWarning($"[MBB] 壁パーツのユニット幅が無効です (mesh='{sourceMesh.name}', unitActualLength={unitActualLength:F5})。配置をスキップします。");
            return segments;
        }

        // Round tile count: at 1.5× authored width, shrink two parts to fit.
        // Always place at least one (prioritize avoiding gaps; very short edges receive one flattened part).
        int numUnits = Mathf.Max(1, Mathf.RoundToInt(effectiveLength / unitActualLength));

        // Slot width is effective length divided exactly by tile count, ensuring exact total length.
        float slotLength = effectiveLength / numUnits;
        float xStretchFactor = slotLength / unitActualLength;
        float scaleX = xStretchFactor;
        Vector3 scale = new Vector3(scaleX, scaleY, 1f);

        // Calculate Y rotation matching edge direction.
        // Quaternion.FromToRotation has an opposite-direction singularity (Y-axis flip);
        // use Atan2 for horizontal rotation that always preserves upward Y.
        float angle = Mathf.Atan2(direction.z, direction.x) * Mathf.Rad2Deg;
        // Align wall-style parts (base/mid/top-w, fence-w, scaffold-wall) to the edge,
        // then rotate 180° about Y to match authored orientation. Parts assume local +Z (Blender −Y) is exterior
        // and origin height is floor level; flipping makes local +Z point outside the footprint.
        // Corner parts (*-c, GenerateCornerCore) use bisector orientation and are excluded.
        Quaternion rotation = Quaternion.Euler(0, -angle + 180f, 0);

        // The 180° placement puts local +X toward the edge start. Advancing origin from the slot start
        // by bounds.max.x fits mesh X bounds exactly into the slot.
        // For standard centered-pivot parts, bounds.max.x = width/2, placing origin at slot center.
        float pivotShift = bounds.max.x * scaleX;

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[MBB-WALL] p0=({p0.x:F3},{p0.y:F3}) p1=({p1.x:F3},{p1.y:F3}) baseY={baseY:F3} targetHeight={targetHeight:F3}\n" +
            $"[MBB-WALL]   totalLength={totalLength:F4} startOffset={clampedStart:F4} endOffset={clampedEnd:F4} effectiveLength={effectiveLength:F4}\n" +
            $"[MBB-WALL]   meshName={sourceMesh.name} boundsSize=({originalSize.x:F4},{originalSize.y:F4},{originalSize.z:F4}) boundsCenterX={bounds.center.x:F4}\n" +
            $"[MBB-WALL]   unitActualLength={unitActualLength:F4} numUnits={numUnits} slotLength={slotLength:F4} xStretchFactor={xStretchFactor:F4}\n" +
            $"[MBB-WALL]   finalScale=({scale.x:F4},{scale.y:F4},{scale.z:F4}) pivotShift={pivotShift:F4}"
        );

        // Place body/scaffolding with combine matrices. Bake skinned walls once per part per building.
        Mesh sharedSource = sourceMesh;
        if (sharedSources != null && smr != null)
        {
            if (!sharedSources.TryGetValue(wallModule, out sharedSource))
            {
                sharedSource = new Mesh();
                smr.BakeMesh(sharedSource);
                sharedSources.Add(wallModule, sharedSource);
                temporaryMeshes.Add(sharedSource);
            }
        }
        for (int i = 0; i < numUnits; i++)
        {
            Vector3 unitPos = start + direction * (clampedStart + i * slotLength + pivotShift);
            Matrix4x4 matrix = Matrix4x4.TRS(unitPos, rotation, scale);
            if (sharedSources != null)
            {
                segments.Add(new SegmentMeshData { Mesh = sharedSource, Materials = sharedMaterials, Transform = matrix, Shared = true });
                continue;
            }

            Mesh meshInstance;
            if (smr != null)
            {
                Mesh bakedMesh = new Mesh();
                smr.BakeMesh(bakedMesh);
                meshInstance = Object.Instantiate(bakedMesh);
                Object.Destroy(bakedMesh);
            }
            else
            {
                meshInstance = Object.Instantiate(sourceMesh);
            }
            Vector3[] vertices = meshInstance.vertices;

            for (int v = 0; v < vertices.Length; v++)
            {
                vertices[v] = matrix.MultiplyPoint3x4(vertices[v]);
            }
            meshInstance.vertices = vertices;
            meshInstance.RecalculateNormals();
            meshInstance.RecalculateBounds();

            segments.Add(new SegmentMeshData
            {
                Mesh = meshInstance,
                Materials = sharedMaterials,
            });
        }

        return segments;
    }

    /// <summary>
    /// Generate fence-corner segment meshes.
    /// Place at original mesh size without any scaling.
    /// </summary>
    public static SegmentMeshData GenerateFenceCorner(Vector2 p0, Vector2 p1, Vector2 p2, GameObject cornerModule, Material[] sharedMaterials)
    {
        // Same logic as body corners. Place fences at floorY=0, unscaled.
        return GenerateCornerCore(p0, p1, p2, 0f, cornerModule, sharedMaterials, NO_Y_SCALE);
    }

    /// <summary>
    /// Generate construction scaffolding using exactly the normal building wall/corner placement logic
    /// of GenerateWallSegments / GenerateCornerSegment / MeasureCornerExtentAlongEdge
    /// directly on the footprint. Corner hinges (origins) match footprint vertices;
    /// walls leave space for corner projections at both ends. No offset.
    /// Unlike fences, Y-scale each level to targetHeight (floor height), for builtFloors levels.
    /// wallModule / cornerModule are single parts shared across all edges/vertices.
    /// </summary>
    /// <param name="footprint">Building footprint vertices (CCW, local coordinates).</param>
    /// <param name="wallModule">scaffold-wall part. Null generates nothing.</param>
    /// <param name="cornerModule">scaffold-corner part. May be null (walls only).</param>
    /// <param name="floorHeight">Height per floor (m). Parts are Y-scaled to this.</param>
    /// <param name="builtFloors">Number of levels to stack (constructed floors).</param>
    public static void GenerateScaffoldSegments(
        Vector2[] footprint,
        GameObject wallModule, Material[] wallMaterials,
        GameObject cornerModule, Material[] cornerMaterials,
        float floorHeight, int builtFloors,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        if (footprint == null || footprint.Length < 3 || wallModule == null) return;
        int n = footprint.Length;
        var wallSources = new Dictionary<GameObject, Mesh>();
        var cornerSources = new Dictionary<(GameObject module, float angle), Mesh>();
        int floorStart = combineInstances.Count, floorCount = 0;

        for (int level = 0; level < builtFloors; level++)
        {
            float floorY = level * floorHeight;
            if (level > 0)
            {
                AppendTranslatedFloor(floorStart, floorCount, floorY, combineInstances, allMaterials);
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = footprint[(i + n - 1) % n];
                Vector2 p1 = footprint[i];
                Vector2 p2 = footprint[(i + 1) % n];
                Vector2 p3 = footprint[(i + 2) % n];

                // Corner projections to leave open at wall ends. The part is identical at every vertex, but projection
                // depends on interior angle (corners open/close through bones), so measure per vertex.
                float startOffset = MeasureCornerExtentAlongEdge(cornerModule, p0, p1, p2, floorHeight, true);
                float endOffset = MeasureCornerExtentAlongEdge(cornerModule, p1, p2, p3, floorHeight, false);

                // Wall: p1 → p2 (normal GenerateWallSegments, leaving corner projections open at both ends).
                List<SegmentMeshData> wallSegments = BuildWallRun(
                    p1, p2, floorY, wallModule, wallMaterials, floorHeight, startOffset, endOffset, wallSources, temporaryMeshes);
                foreach (var seg in wallSegments)
                {
                    if (seg.IsValid) AddSegmentToCombineList(seg, combineInstances, allMaterials, temporaryMeshes);
                }

                // Corner: place hinge (origin) at footprint vertex p1.
                //     (normal GenerateCornerSegment, including interior-angle bone deformation).
                if (cornerModule != null)
                {
                    SegmentMeshData corner = CreateCornerPlacement(p0, p1, p2, floorY, cornerModule, cornerMaterials, floorHeight, cornerSources, temporaryMeshes);
                    if (corner.IsValid) AddSegmentToCombineList(corner, combineInstances, allMaterials, temporaryMeshes);
                }
            }
            floorCount = combineInstances.Count - floorStart;
        }
    }

    /// <summary>Share corners bent at origin within one building; apply position/orientation/height during combining.</summary>
    private static SegmentMeshData CreateCornerPlacement(
        Vector2 p0, Vector2 p1, Vector2 p2, float floorY,
        GameObject module, Material[] materials, float targetHeight,
        Dictionary<(GameObject module, float angle), Mesh> sources, List<Mesh> temporary)
    {
        if (module == null) return default;
        Quaternion rotation = ComputeCornerRotation(p0, p1, p2, out _, out float angle);
        if (IsUnsupportedConcaveCorner(module, angle)) return default;
        Mesh source = GetPartSourceMesh(module);
        if (source == null) return default;
        var smr = module.GetComponentInChildren<SkinnedMeshRenderer>();
        Mesh mesh = source;
        if (smr != null)
        {
            var key = (module, angle);
            if (!sources.TryGetValue(key, out mesh))
            {
                var instance = Object.Instantiate(module);
                try
                {
                    mesh = GenerateSkinnedCorner(Vector3.zero, Quaternion.identity, angle,
                        instance.GetComponentInChildren<SkinnedMeshRenderer>(), Vector3.one);
                    if (mesh == null) return default;
                    sources.Add(key, mesh);
                    temporary.Add(mesh);
                }
                finally { Object.Destroy(instance); }
            }
        }
        return new SegmentMeshData {
            Mesh = mesh, Materials = materials, Shared = true,
            Transform = Matrix4x4.TRS(new Vector3(p1.x, floorY, p1.y), rotation, DetermineCornerScale(source, targetHeight))
        };
    }

    /// <summary>
    /// Generate corner-segment mesh.
    /// </summary>
    public static SegmentMeshData GenerateCornerSegment(Vector2 p0, Vector2 p1, Vector2 p2, float floorY, GameObject cornerModule, Material[] sharedMaterials, float targetHeight)
    {
        // Place body corners relative to floorY with height targetHeight.
        return GenerateCornerCore(p0, p1, p2, floorY, cornerModule, sharedMaterials, targetHeight);
    }

    /// <summary>
    /// Determine corner-part scale.
    /// - targetHeight ≤ 0: no scaling (first/top-floor corners and fence corners retain authored height).
    /// - targetHeight > 0: scale only Y to targetHeight (middle floors/construction scaffolding).
    ///   XZ always retain authored size.
    /// </summary>
    private static Vector3 DetermineCornerScale(Mesh sourceMesh, float targetHeight)
    {
        WarnUndersizedPartOnce(sourceMesh);

        if (targetHeight <= 0f)
        {
            return Vector3.one;
        }

        Vector3 size = sourceMesh != null ? sourceMesh.bounds.size : Vector3.zero;
        float sourceHeight = Mathf.Max(size.y, 0.001f);
        return new Vector3(1f, targetHeight / sourceHeight, 1f);
    }

    /// <summary>
    /// Shared corner-segment generation for body corners (cornerY=floorY, height=targetHeight)
    /// and fence corners (cornerY=0, targetHeight=0=unscaled).
    /// Determine orientation from bisector; use bones to open/close arbitrary angles for SkinnedMeshRenderer,
    /// otherwise place with TRS.
    /// </summary>
    private static SegmentMeshData GenerateCornerCore(
        Vector2 p0, Vector2 p1, Vector2 p2, float cornerY,
        GameObject cornerModule, Material[] sharedMaterials, float targetHeight)
    {
        if (cornerModule == null) return default;

        Vector3 corner = new Vector3(p1.x, cornerY, p1.y);

        // Use the same orientation calculation as MeasureCornerExtentAlongEdge (wall tiling measures projections
        // assuming this rotation; disagreement would misalign walls/corners).
        Quaternion rotation = ComputeCornerRotation(p0, p1, p2, out _, out float interiorAngle);
        if (IsUnsupportedConcaveCorner(cornerModule, interiorAngle)) return default;

        SkinnedMeshRenderer prefabSmr = cornerModule.GetComponentInChildren<SkinnedMeshRenderer>();
        if (prefabSmr != null)
        {
            GameObject cornerInstance = Object.Instantiate(cornerModule);
            try
            {
                SkinnedMeshRenderer smrInstance = cornerInstance.GetComponentInChildren<SkinnedMeshRenderer>();
                Vector3 skinnedScale = DetermineCornerScale(prefabSmr.sharedMesh, targetHeight);
                Mesh skinnedCorner = GenerateSkinnedCorner(corner, rotation, interiorAngle, smrInstance, skinnedScale);
                if (skinnedCorner != null)
                {
                    return new SegmentMeshData
                    {
                        Mesh = skinnedCorner,
                        Materials = sharedMaterials,
                    };
                }
            }
            finally
            {
                Object.Destroy(cornerInstance);
            }
        }

        MeshFilter mf = cornerModule.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return default;

        Mesh mesh = Object.Instantiate(mf.sharedMesh);
        Matrix4x4 matrix = Matrix4x4.TRS(corner, rotation, DetermineCornerScale(mf.sharedMesh, targetHeight));

        Vector3[] vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
        }
        mesh.vertices = vertices;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return new SegmentMeshData
        {
            Mesh = mesh,
            Materials = sharedMaterials,
        };
    }

    /// <summary>Omit parts unable to open to concave angles; extend walls to the vertex to connect.</summary>
    private static bool IsUnsupportedConcaveCorner(GameObject module, float interiorAngle)
    {
        if (interiorAngle <= 180f) return false;
        SkinnedMeshRenderer smr = module.GetComponentInChildren<SkinnedMeshRenderer>();
        return smr == null || smr.bones == null || smr.bones.Length < 2;
    }

    /// <summary>
    /// Generate a skinned-mesh corner (with bone deformation).
    /// Rotate bones to match footprint corner angles.
    /// scale is already determined by DetermineCornerScale (unscaled, or Y-only scaffolding scale).
    /// </summary>
    private static Mesh GenerateSkinnedCorner(Vector3 position, Quaternion rotation, float angle, SkinnedMeshRenderer smr, Vector3 scale)
    {
        Mesh finalMesh;

        if (smr == null)
        {
            Debug.LogWarning("ModularBodyBuilder: SkinnedMeshRenderer がありません。通常のメッシュとして処理します。");
            return null;
        }

        if (smr.bones == null || smr.bones.Length < 2)
        {
            Debug.LogWarning("ModularBodyBuilder: SkinnedMeshRendererに十分なボーンが含まれていません。通常のメッシュとして処理します。");
            finalMesh = Object.Instantiate(smr.sharedMesh);
        }
        else
        {
            // Corner parts are authored at 90° interior angle, with bones[0] on the outgoing arm (vertex→next vertex, Blender +X)
            // and bones[1] on the incoming arm (Blender +Y). This order determines opening/closing direction.
            Transform bone0 = smr.bones[0];
            Transform bone1 = smr.bones[1];

            float halfAngleDiff = (angle - 90f) * 0.5f;

            // Bend bones with world rotations, not localRotation (parent-relative premultiplication).
            // Independently rotate each arm about world Y through its bone head (root),
            // giving symmetric opening/closing for both sibling and parent-child rigs (hierarchy independent).
            // At 90° interior angle, halfAngleDiff=0 gives no rotation/deformation, as before.
            Vector3 head0Before = bone0.position;
            Vector3 head1Before = bone1.position;

            bone0.rotation = Quaternion.AngleAxis(halfAngleDiff, Vector3.up) * bone0.rotation;
            bone1.rotation = Quaternion.AngleAxis(-halfAngleDiff, Vector3.up) * bone1.rotation;

            // In parent-child rigs, rotating bone0 moves child bone1's head,
            // shifting the arm root off the vertex and opening wall seams. Re-anchor each head
            // to its pre-bend position (= bind-pose root) to prevent this.
            // Sibling-rig heads do not move, making this effectively a no-op.
            Vector3 head1Displaced = bone1.position;
            bone0.position = head0Before;
            bone1.position = head1Before;

            if (LocalConstants.UNITY_LOG_BUILDING)
            {
                // Diagnostics: whether bone0 rotation moved bone1's head (chain-rig detection), and re-anchoring distance.
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                    $"[MBB-CORNER] angle={angle:F1} halfAngleDiff={halfAngleDiff:F2}\n" +
                    $"[MBB-CORNER]   bone0 head={FormatVector(head0Before)} bone1 head={FormatVector(head1Before)}\n" +
                    $"[MBB-CORNER]   bone1 曲げ後変位={FormatVector(head1Displaced)} (Δ={Vector3.Distance(head1Before, head1Displaced):F4}) → 再アンカー");
            }

            finalMesh = new Mesh();
            smr.BakeMesh(finalMesh);
        }

        Matrix4x4 matrix = Matrix4x4.TRS(position, rotation, scale);
        Vector3[] vertices = finalMesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
        }
        finalMesh.vertices = vertices;
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        return finalMesh;
    }

    private static string FormatVector(Vector3 v)
    {
        return $"({v.x:F3},{v.y:F3},{v.z:F3})";
    }

    private static string FormatMaterial(Material mat)
    {
        if (mat == null) return "null";
        return $"'{mat.name}' (shader='{mat.shader?.name}')";
    }

    private static string FormatMaterials(Material[] mats)
    {
        if (mats == null) return "null[]";
        if (mats.Length == 0) return "empty[]";
        var parts = new List<string>();
        foreach (var m in mats)
        {
            parts.Add(FormatMaterial(m));
        }
        return $"[{string.Join(", ", parts)}]";
    }
}
