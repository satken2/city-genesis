using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;
using NetTopologySuite.Geometries;

/// <summary>
/// Port (MODEL_PORT) mesh generator.
///
/// For ports on coastal parcels (PARCEL_SHORE). Determine the water edge (ship docking line) and landward yard (cargo-handling ground);
/// place handling cranes (port_crane) at the water edge, facility modules (port_building: gate/buildings) at parcel edge 0 (road side),
/// and port-specific yard cargo between them (stacked port_container or individual ground-level port_cargo).
/// <see cref="PortLayout"/> determines placement.
///
/// Two methods determine water edge/yard; subsequent placement rules are identical.
/// - Parcel landward of reclaimed quay (<see cref="FindFacingReclaimedQuays"/>, a port drawn on reclamation): leave the water edge to the reclaimed quay
///   (<see cref="ReclaimedLandManager"/> draws top/vertical faces); generate no slab. Water edge = facing quay sections;
///   yard = parcel land portion.
/// - Otherwise (natural coast): backend preserves coastal parcels without coastline clipping (B_土地形状 B.5)
///   and decides the quay line itself (the parcel's berth_line: the water-edge chord shifted seaward, capped by the parcel),
///   because it judges the port's required water depth along that line. Cut the footprint along berth_line and extrude
///   a thick board (quay slab); never derive the line here. Water edge = cut-line edges; yard = slab area.
///
/// visual_variant(1) selects exterior models as for other buildings (F_建物メッシュの生成); visual_variant2 seeds
/// yard cargo placement. Close-up uses `_close` parts at identical positions. Far uses distant models/boxes like other buildings, bypassing this generator.
///
/// See city-genesis/docs/ja/geometries/F1_建物メッシュの生成方式.md for the overview.
/// </summary>
public class PortBuildingMeshBuilder : IBuildingMeshGenerator
{
    /// <summary>Quay-top height above sea level (m). Facility modules also never sit below this.</summary>
    public const float QUAY_TOP_HEIGHT = 2f;

    /// <summary>Distance tolerance for sea-edge membership (m). Matches cut line to quay-footprint edges.</summary>
    private const float SEAWARD_EDGE_EPSILON = 0.05f;

    /// <summary>Minimum land-portion area (m²) for the yard of a port facing reclaimed quays; smaller pieces are slivers.</summary>
    private const double MIN_LAND_PART_AREA = 1.0;

    /// <summary>Search limit for yard cargo variants (port_cargo1, 2, … / port_container1, 2, …).</summary>
    private const int MAX_CARGO_PARTS = 16;

    /// <summary>
    /// Seaward quay boundary (edges created by straight-line clipping), used to enumerate water edges.
    /// <see cref="Valid"/> is false if the building could not be clipped. All positions/directions use local XZ relative to center_point.
    /// </summary>
    private struct QuaySeawardEdge
    {
        public bool Valid;
        /// <summary>Cut-line reference point (projected left endpoint A').</summary>
        public Vector2 Origin;
        /// <summary>Cut-line direction (A'→B', sea on the left).</summary>
        public Vector2 Dir;
        /// <summary>Unit seaward normal perpendicular to the cut line.</summary>
        public Vector2 Normal;
    }

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null || buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"[PortBMB] 底面ポリゴンが不正です (Building ID: {buildingData?.agent_id})。白い箱にフォールバックします。");
            return BuildingMeshBuilder.GenerateSimpleBuilding(null, 1, LocalConstants.FLOOR_HEIGHT);
        }

        // Footprint (CCW, edge 0 road-facing, local relative to center_point).
        Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
        string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
        int variantNumber = BuildingMeshUtils.ResolveModelId(prefix, buildingData.visual_variant);

        // center_point world coordinates (backend coordinates) and terrain height.
        // Use the same lookup (Utils.GetGroundHeight) as ModularBuildingMeshBuilder,
        // matching the height reference used by BuildingManager to place GameObjects.
        Vector3 centerPointWorld = Vector3.zero;
        if (buildingData.centerPointArray != null && buildingData.centerPointArray.Length >= 2)
        {
            float cx = buildingData.centerPointArray[0];
            float cz = buildingData.centerPointArray[1];
            float cy = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
            centerPointWorld = new Vector3(cx, cy, cz);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();
        string[] prefixes = PartPrefixes(buildingData, prefix);
        Material floorMaterial = BuildingMeshUtils.ResolveFloorMaterialFromMaster(buildingData, prefix);

        // 1. Water edge, yard, and ground height (yard ground positions cranes/facilities/cargo below).
        Vector2[] yard;
        List<PortLayout.Waterline> waterlines;
        float yardFloorLocalY;
        float colliderHeight;
        List<ReclaimedQuay> facingQuays = CollectFacingReclaimedQuays(buildingData, centerPointWorld);
        if (facingQuays.Count > 0)
        {
            // 1'. Ports facing reclaimed quays: no slab; water edge = facing sections, yard = parcel land portion.
            waterlines = FacingWaterlines(facingQuays, centerPointWorld, out float maxTopLocalY);
            yard = ExtractLandPart(footprint, centerPointWorld) ?? footprint;
            yardFloorLocalY = float.NegativeInfinity;
            colliderHeight = Mathf.Max(0.1f, maxTopLocalY);
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[PortBMB] agent={buildingData.agent_id} 埋立地の岸壁 {facingQuays.Count} 区間に面するためスラブを省略");
        }
        else
        {
            // Natural coast: clip the footprint along the backend's quay line (berth_line).
            Vector2[] quayFootprint = BuildQuayFootprint(footprint, buildingData.berthLineArray, centerPointWorld, out QuaySeawardEdge seaward);

            // Slab-top world Y = sea level (0m) + 2m, fixed. GameObject sits at center_point terrain height,
            // so subtract centerPointWorld.y for local Y.
            float topLocalY = QUAY_TOP_HEIGHT - centerPointWorld.y;

            // Slab mesh (top=topLocalY, bottom=terrain/seabed height at each vertex). subMesh0=top, subMesh1=sides+bottom.
            Mesh slabMesh = BuildSlabMesh(quayFootprint, topLocalY, centerPointWorld);
            if (slabMesh == null)
            {
                return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
            }

            // Top=floor (same as yard ground panels, making continuous pavement); sides/bottom=ceil (quay walls).
            // If ground panels are disabled (draw_floor unset, etc.), read floor directly; default material if absent.
            Material topMaterial = floorMaterial != null ? floorMaterial : ResolvePartMaterial(prefixes, "floor", variantNumber);
            Material sideMaterial = ResolvePartMaterial(prefixes, "ceil", variantNumber);
            AppendMesh(slabMesh, new[] { topMaterial, sideMaterial }, combineInstances, materials);
            temporaryMeshes.Add(slabMesh);

            waterlines = EnumerateSeawardWaterlines(quayFootprint, seaward, topLocalY);
            yard = quayFootprint;
            yardFloorLocalY = topLocalY;
            // Crane booms project seaward but are excluded from picking (same simplification as construction cranes).
            colliderHeight = Mathf.Max(0.1f, topLocalY);
        }

        // Yard-ground local Y. Natural-coast slabs cover the yard, so use max(terrain, top).
        float Ground(Vector2 local)
        {
            float terrain = Utils.GetGroundHeight(new Vector3(local.x + centerPointWorld.x, 1000f, local.y + centerPointWorld.z)) - centerPointWorld.y;
            return Mathf.Max(terrain, yardFloorLocalY);
        }

        // 2. Placement plan. Measure standard models regardless of rendering LOD; close-up positions are identical.
        GameObject moduleModel = LayoutPart(prefix, "port_building", variantNumber);
        GameObject craneModel = LayoutPart(prefix, "port_crane", variantNumber);
        GameObject roofpropModel = LayoutPart(prefix, "roofprop", variantNumber);
        List<(string prefix, string name)> cargoParts = YardCargoParts(prefixes, variantNumber, out bool stacked);
        PortLayout.CargoSpec? cargoSpec = null;
        if (cargoParts.Count > 0)
        {
            cargoSpec = stacked ? PortLayout.ContainerSpec : PortLayout.GroundCargoSpec(CargoBounds(cargoParts, variantNumber));
        }

        Vector2 alongQuay = waterlines.Count > 0
            ? new Vector2(waterlines[0].Normal.y, -waterlines[0].Normal.x)
            : (footprint[1] - footprint[0]).normalized;
        Vector2 roofpropAt = VertexCentroid(yard);
        Quaternion roofpropRotation = Quaternion.Euler(0, -Mathf.Atan2(alongQuay.y, alongQuay.x) * Mathf.Rad2Deg + 90, 0);
        Bounds? roofpropBounds = PortLayout.GetModelBounds(roofpropModel);
        Vector2[] roofpropRect = roofpropBounds.HasValue ? PortLayout.RotatedRect(roofpropAt, roofpropRotation, roofpropBounds.Value) : null;

        PortLayout.Plan plan = PortLayout.Create(footprint, yard, waterlines,
            PortLayout.GetModelBounds(moduleModel), PortLayout.GetModelBounds(craneModel), roofpropRect,
            cargoSpec, buildingData.visual_variant2);

        // 3. Facility modules (gates/buildings). Rest at parcel edge-0 midpoint ground (at least sea level+2m).
        if (plan.Module != null)
        {
            float y = Mathf.Max(Ground(plan.Module.Gate), QUAY_TOP_HEIGHT - centerPointWorld.y);
            AppendModel(DrawPart(prefix, "port_building", variantNumber),
                Matrix4x4.TRS(new Vector3(plan.Module.Position.x, y, plan.Module.Position.y), plan.Module.Rotation, Vector3.one),
                combineInstances, materials, temporaryMeshes);
        }

        // 4. Handling cranes. Origin on water-edge line at top height; local +Z seaward. Unscaled (actual size).
        if (plan.Cranes.Count > 0)
        {
            Mesh craneMesh = CombinePart(DrawPart(prefix, "port_crane", variantNumber), out Material[] craneMaterials, temporaryMeshes);
            if (craneMesh != null)
            {
                foreach (PortLayout.Crane crane in plan.Cranes)
                {
                    Quaternion rotation = Quaternion.LookRotation(new Vector3(crane.Normal.x, 0f, crane.Normal.y), Vector3.up);
                    AppendInstance(craneMesh, craneMaterials,
                        Matrix4x4.TRS(new Vector3(crane.Position.x, crane.Y, crane.Position.y), rotation, Vector3.one),
                        combineInstances, materials);
                }
            }
        }

        // 5. Rooftop prop (one at mean yard-vertex position, aligned to water edge).
        if (roofpropRect != null)
        {
            AppendModel(DrawPart(prefix, "roofprop", variantNumber),
                Matrix4x4.TRS(new Vector3(roofpropAt.x, Ground(roofpropAt), roofpropAt.y), roofpropRotation, Vector3.one),
                combineInstances, materials, temporaryMeshes);
        }

        // 6. Yard cargo. Stack container parts; place one ground cargo part per slot using rendering LOD (prefer `_close` for close-up).
        if (plan.Stacks.Count > 0)
        {
            if (!stacked)
            {
                AppendGroundCargo(plan.Stacks, plan.Cargo, cargoParts, variantNumber, Ground, combineInstances, materials, temporaryMeshes);
            }
            else
            {
                AppendContainers(plan.Stacks, cargoParts, variantNumber, Ground, combineInstances, materials, temporaryMeshes);
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[PortBMB] agent={buildingData.agent_id} prefix={prefix} lod={BuildingMeshUtils.CurrentDrawLod} 水際{waterlines.Count} " +
            $"施設{(plan.Module != null ? "あり" : "なし")} クレーン{plan.Cranes.Count}基 {(stacked ? "コンテナ" : "荷")}{plan.Stacks.Count}山");

        // 7. Site decorations (fences/tiles/ground panels). Shared with modular/prebuilt;
        //    draw yard ground panels for draw_floor=yes, continuous with quay top via the same floor material.
        //    Omit the parcel's sea area (beyond quay). Yard height changes across coastline (reclaimed top and original land,
        //    beach beneath slab), so fences always follow terrain; the yard panel follows it at close-up like every ground panel.
        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(buildingData, out _, out _);
        ModularBodyBuilder.GenerateSiteDecorations(
            prefix, 0,
            fenceFootprint, yard, yard,
            floorMaterial,
            centerPointWorld, deformToTerrain: true,
            combineInstances, materials, temporaryMeshes, frontFootprint: footprint);

        // Ports facing reclaimed quays have no slab; without any resolved part, their mesh is empty.
        if (combineInstances.Count == 0 && !BuildingExteriorRenderer.HasCapturedWork)
        {
            foreach (Mesh temp in temporaryMeshes)
            {
                Object.Destroy(temp);
            }
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
        }

        // 8. Combine meshes.
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        if (combineInstances.Count > 0) finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // 9. Collider (yard extruded to top height).
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(yard, colliderHeight);

        var result = new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    /// <summary>
    /// Standard facility module (port_building). Its dimensions/markers determine placement/openings regardless of rendering LOD
    /// (also used by <see cref="BuildingRoadAccess"/> to index openings). Null if absent.
    /// </summary>
    public static GameObject ResolveModulePart(BuildingSummary buildingData)
    {
        string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
        return LayoutPart(prefix, "port_building", BuildingMeshUtils.ResolveModelId(prefix, buildingData.visual_variant));
    }

    /// <summary>
    /// Determine ship docking water edges using mesh-generation rules, returning world coordinates (P/Q world XZ, Y quay-top elevation).
    /// Depends only on building data/coastline/reclamation stores, independent of GameObjects or resident cells (used for arriving ships' berthing positions).
    /// Empty if water edge is undefined.
    /// </summary>
    public static List<PortLayout.Waterline> ResolveWorldWaterlines(BuildingSummary buildingData)
    {
        var result = new List<PortLayout.Waterline>();
        if (buildingData?.polygonArray == null || buildingData.polygonArray.Length < 3 ||
            buildingData.centerPointArray == null || buildingData.centerPointArray.Length < 2)
        {
            return result;
        }
        // Using a zero-height center makes local Y equal world elevation.
        var centerPointWorld = new Vector3(buildingData.centerPointArray[0], 0f, buildingData.centerPointArray[1]);
        List<ReclaimedQuay> facingQuays = CollectFacingReclaimedQuays(buildingData, centerPointWorld);
        List<PortLayout.Waterline> local;
        if (facingQuays.Count > 0)
        {
            local = FacingWaterlines(facingQuays, centerPointWorld, out _);
        }
        else
        {
            Vector2[] quayFootprint = BuildQuayFootprint(BuildingMeshUtils.GetFootprint(buildingData), buildingData.berthLineArray, centerPointWorld, out QuaySeawardEdge seaward);
            local = EnumerateSeawardWaterlines(quayFootprint, seaward, QUAY_TOP_HEIGHT);
        }
        var origin = new Vector2(centerPointWorld.x, centerPointWorld.z);
        foreach (PortLayout.Waterline waterline in local)
        {
            result.Add(new PortLayout.Waterline { P = waterline.P + origin, Q = waterline.Q + origin, Normal = waterline.Normal, Y = waterline.Y });
        }
        return result;
    }

    /// <summary>Resolve standard (unsuffixed) models for placement dimensions by scanning model-number folders. Ignore rendering-LOD `_close`.</summary>
    private static GameObject LayoutPart(string prefix, string partName, int variantNumber)
    {
        DrawLod previous = BuildingMeshUtils.CurrentDrawLod;
        BuildingMeshUtils.CurrentDrawLod = DrawLod.Normal;
        try
        {
            return BuildingMeshUtils.GetPartBySuffixScan(prefix, partName, variantNumber);
        }
        finally
        {
            BuildingMeshUtils.CurrentDrawLod = previous;
        }
    }

    /// <summary>Resolve rendering parts by scanning model-number folders (prefer `_close` for close-up).</summary>
    private static GameObject DrawPart(string prefix, string partName, int variantNumber)
    {
        return BuildingMeshUtils.GetPartBySuffixScan(prefix, partName, variantNumber);
    }

    /// <summary>
    /// Port part-folder order (a{variant} → b{bd_type}). Parts shared across port types, such as quay materials,
    /// belong in b{bd_type}; a same-name a{variant} part takes precedence.
    /// </summary>
    private static string[] PartPrefixes(BuildingSummary buildingData, string prefix)
    {
        int bdType = buildingData.bd_type ?? 0;
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            try
            {
                bdType = MasterDataStore.Instance.GetAgentMaster(buildingData.variant)?.bd_type ?? bdType;
            }
            catch (System.Exception)
            {
                // For variants absent from master data, use building bd_type.
            }
        }
        string bPrefix = $"b{bdType}";
        return bPrefix == prefix ? new[] { prefix } : new[] { prefix, bPrefix };
    }

    /// <summary>Find the first material of material-reference-only parts (floor/ceil) in prefixes order. Default building material if absent.</summary>
    private static Material ResolvePartMaterial(string[] prefixes, string partName, int variantNumber)
    {
        foreach (string p in prefixes)
        {
            GameObject part = BuildingMeshUtils.GetPartBySuffixScan(p, partName, variantNumber);
            Material[] partMaterials = part != null ? BuildingMeshUtils.GetModuleMaterials(part) : null;
            if (partMaterials != null && partMaterials.Length > 0 && partMaterials[0] != null)
            {
                return partMaterials[0];
            }
        }
        return BuildingMeshBuilder.GetDefaultBuildingMaterial();
    }

    /// <summary>
    /// Yard cargo variant sequence. In folder order (a{variant} → b{bd_type}), search ground cargo port_cargo1, 2, … then
    /// stacked containers port_container1, 2, … sequentially from 1; use only the first set found (stacked indicates containers).
    /// Cargo differs by port type (bulk piles, tanker-terminal tanks), so place it in each port's a{variant}.
    /// Check standard models for existence (keeping close-up placement identical).
    /// </summary>
    private static List<(string prefix, string name)> YardCargoParts(string[] prefixes, int variantNumber, out bool stacked)
    {
        var parts = new List<(string prefix, string name)>();
        stacked = false;
        foreach (string p in prefixes)
        {
            foreach (string stem in new[] { "port_cargo", "port_container" })
            {
                for (int n = 1; n <= MAX_CARGO_PARTS; n++)
                {
                    string name = $"{stem}{n}";
                    if (LayoutPart(p, name, variantNumber) == null)
                    {
                        break;
                    }
                    parts.Add((p, name));
                }
                if (parts.Count > 0)
                {
                    stacked = stem == "port_container";
                    return parts;
                }
            }
        }
        return parts;
    }

    /// <summary>Union of standard ground-cargo model bounds (determines slot size).</summary>
    private static Bounds CargoBounds(List<(string prefix, string name)> parts, int variantNumber)
    {
        Bounds? result = null;
        foreach ((string prefix, string name) in parts)
        {
            Bounds? b = PortLayout.GetModelBounds(LayoutPart(prefix, name, variantNumber));
            if (!b.HasValue)
            {
                continue;
            }
            if (result.HasValue)
            {
                Bounds grown = result.Value;
                grown.Encapsulate(b.Value);
                result = grown;
            }
            else
            {
                result = b;
            }
        }
        return result ?? new Bounds(Vector3.zero, Vector3.zero);
    }

    /// <summary>
    /// Place one ground cargo item per slot. Choose kind via <see cref="PortLayout.CargoKind"/> (same within a block row);
    /// bottom is the lowest corner ground height. Parts have origin at bottom center and local +Z along the water edge.
    /// </summary>
    private static void AppendGroundCargo(List<PortLayout.Stack> stacks, PortLayout.CargoSpec spec,
        List<(string prefix, string name)> parts, int variantNumber, System.Func<Vector2, float> ground,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        var meshes = new Mesh[parts.Count];
        var meshMaterials = new Material[parts.Count][];
        for (int i = 0; i < parts.Count; i++)
        {
            meshes[i] = CombinePart(DrawPart(parts[i].prefix, parts[i].name, variantNumber), out meshMaterials[i], temporaryMeshes);
        }

        foreach (PortLayout.Stack stack in stacks)
        {
            int kind = PortLayout.CargoKind(stack, meshes.Length);
            if (meshes[kind] == null)
            {
                continue;
            }
            float baseY = StackBase(stack, spec.Length, spec.Width, ground);
            Quaternion rotation = Quaternion.LookRotation(new Vector3(stack.Along.x, 0f, stack.Along.y), Vector3.up);
            AppendInstance(meshes[kind], meshMaterials[kind],
                Matrix4x4.TRS(new Vector3(stack.Center.x, baseY, stack.Center.y), rotation, Vector3.one), combineInstances, materials);
        }
    }

    /// <summary>
    /// Render container piles by stacking parts. Select tier t color with <see cref="PortLayout.TierColor"/>; bottom is the lowest corner ground height
    /// (bury uphill sides slightly to avoid floating). Parts have origin at bottom center and local +Z along their length.
    /// </summary>
    private static void AppendContainers(List<PortLayout.Stack> stacks, List<(string prefix, string name)> parts, int variantNumber,
        System.Func<Vector2, float> ground, List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        var meshes = new Mesh[parts.Count];
        var meshMaterials = new Material[parts.Count][];
        for (int i = 0; i < parts.Count; i++)
        {
            meshes[i] = CombinePart(DrawPart(parts[i].prefix, parts[i].name, variantNumber), out meshMaterials[i], temporaryMeshes);
        }

        foreach (PortLayout.Stack stack in stacks)
        {
            float baseY = StackBase(stack, PortLayout.CONTAINER_LENGTH, PortLayout.CONTAINER_WIDTH, ground);
            Quaternion rotation = Quaternion.LookRotation(new Vector3(stack.Along.x, 0f, stack.Along.y), Vector3.up);
            for (int t = 0; t < stack.Tiers; t++)
            {
                int color = PortLayout.TierColor(stack, t, meshes.Length);
                if (meshes[color] == null)
                {
                    continue;
                }
                var position = new Vector3(stack.Center.x, baseY + t * PortLayout.CONTAINER_TIER_PITCH, stack.Center.y);
                AppendInstance(meshes[color], meshMaterials[color], Matrix4x4.TRS(position, rotation, Vector3.one),
                    combineInstances, materials);
            }
        }
    }

    /// <summary>Pile-bottom local Y: lowest ground height at four corners of a length×width slot.</summary>
    private static float StackBase(PortLayout.Stack stack, float length, float width, System.Func<Vector2, float> ground)
    {
        Vector2 u = stack.Along * (length * 0.5f);
        Vector2 v = new Vector2(-stack.Along.y, stack.Along.x) * (width * 0.5f);
        return Mathf.Min(Mathf.Min(ground(stack.Center - u - v), ground(stack.Center + u - v)),
            Mathf.Min(ground(stack.Center + u + v), ground(stack.Center - u + v)));
    }

    /// <summary>Combine all prefab meshes into one (registered as temporary). Null if no part exists.</summary>
    private static Mesh CombinePart(GameObject part, out Material[] partMaterials, List<Mesh> temporaryMeshes)
    {
        partMaterials = null;
        if (part == null)
        {
            return null;
        }
        Mesh mesh = BuildingMeshUtils.CombineModuleMeshes(part, out partMaterials);
        if (mesh != null)
        {
            temporaryMeshes.Add(mesh);
        }
        return mesh;
    }

    /// <summary>Place one part with matrix. Do nothing if absent.</summary>
    private static void AppendModel(GameObject part, Matrix4x4 matrix,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        Mesh mesh = CombinePart(part, out Material[] partMaterials, temporaryMeshes);
        if (mesh != null)
        {
            AppendInstance(mesh, partMaterials, matrix, combineInstances, materials);
        }
    }

    /// <summary>Add each submesh to the CombineInstance list with matrix.</summary>
    private static void AppendInstance(Mesh mesh, Material[] meshMaterials, Matrix4x4 matrix,
        List<CombineInstance> combineInstances, List<Material> materials)
    {
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            combineInstances.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = matrix });
            materials.Add(meshMaterials != null && meshMaterials.Length > 0 ? meshMaterials[Mathf.Min(s, meshMaterials.Length - 1)] : null);
        }
    }

    /// <summary>Use facing quay sections as water edges (local coordinates, outward normal per section, reclamation top height).</summary>
    private static List<PortLayout.Waterline> FacingWaterlines(List<ReclaimedQuay> facingQuays, Vector3 centerPointWorld, out float maxTopLocalY)
    {
        var origin = new Vector2(centerPointWorld.x, centerPointWorld.z);
        var waterlines = new List<PortLayout.Waterline>(facingQuays.Count);
        maxTopLocalY = float.NegativeInfinity;
        foreach (ReclaimedQuay quay in facingQuays)
        {
            ReclaimedLandSegment segment = quay.Segment;
            float y = quay.TopHeight - centerPointWorld.y;
            waterlines.Add(new PortLayout.Waterline { P = segment.A - origin, Q = segment.B - origin, Normal = segment.Normal.normalized, Y = y });
            maxTopLocalY = Mathf.Max(maxTopLocalY, y);
        }
        return waterlines;
    }

    /// <summary>
    /// Retrieve nearby quay sections from the store; choose facing sections with <see cref="FindFacingReclaimedQuays"/>.
    /// SHAPE_FULL footprint matches parcel shape, usable even when bulk loads omit parcel_id.
    /// </summary>
    private static List<ReclaimedQuay> CollectFacingReclaimedQuays(BuildingSummary buildingData, Vector3 centerPointWorld)
    {
        var candidates = new List<ReclaimedQuay>();
        if (ReclaimedLandStore.Lands.Count == 0)
        {
            return candidates;
        }

        float[][] polygon = buildingData.polygonArray;
        var parcelWorld = new Vector2[polygon.Length];
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i < polygon.Length; i++)
        {
            var p = new Vector2(polygon[i][0] + centerPointWorld.x, polygon[i][1] + centerPointWorld.z);
            parcelWorld[i] = p;
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
        }
        float margin = Constants.QUAY_FACE_TOLERANCE + SEAWARD_EDGE_EPSILON;
        ReclaimedLandStore.GetQuaySegments(Rect.MinMaxRect(minX - margin, minZ - margin, maxX + margin, maxZ + margin), candidates);
        return candidates.Count == 0 ? candidates : FindFacingReclaimedQuays(parcelWorld, candidates);
    }

    /// <summary>
    /// Return reclaimed quay sections facing the parcel (world-XZ polygon), clipped to its extent; empty if none face it.
    ///
    /// Intersect quay sections with the parcel expanded by <see cref="Constants.QUAY_FACE_TOLERANCE"/>; trim ends cut at the expanded boundary
    /// by the tolerance protruding beyond the parcel. A section faces the parcel if longer than <see cref="Constants.QUAY_FACE_MIN_LENGTH"/> and the parcel
    /// lies landward (opposite outward normal, inside reclamation). Test landward status by parcel coverage
    /// of a <see cref="Constants.QUAY_PROBE_DISTANCE"/> inward band, requiring <see cref="Constants.QUAY_LAND_SIDE_MIN_COVER"/>.
    /// The backend measures port water depth in front of the same sections (geo_berth.find_facing_reclaimed_quays); keep both rules identical.
    /// Include quays crossing the parcel or coinciding with its seaward edge (ports drawn on reclamation); exclude quays
    /// coinciding from the sea side (reclamation adjacent to an existing port, outward normal toward parcel water) or merely touching corners/vertices.
    /// Tests/Quay calls this via reflection; preserve name and arguments.
    /// </summary>
    public static List<ReclaimedQuay> FindFacingReclaimedQuays(Vector2[] parcelWorld, IReadOnlyList<ReclaimedQuay> quays)
    {
        var result = new List<ReclaimedQuay>();
        if (parcelWorld == null || parcelWorld.Length < 3 || quays == null || quays.Count == 0)
        {
            return result;
        }

        try
        {
            var gf = new GeometryFactory();
            Geometry parcel = gf.CreatePolygon(ToClosedCoordinates(parcelWorld, Vector2.zero));
            if (!parcel.IsValid)
            {
                parcel = parcel.Buffer(0);
            }
            Geometry area = parcel.Buffer(Constants.QUAY_FACE_TOLERANCE);
            var pieces = new List<LineString>();
            foreach (ReclaimedQuay quay in quays)
            {
                Vector2 a = quay.Segment.A;
                Vector2 b = quay.Segment.B;
                double dx = (double)b.x - a.x;
                double dz = (double)b.y - a.y;
                double length = System.Math.Sqrt(dx * dx + dz * dz);
                if (length < 1e-6)
                {
                    continue;
                }
                Geometry segment = gf.CreateLineString(new[] { new Coordinate(a.x, a.y), new Coordinate(b.x, b.y) });
                pieces.Clear();
                CollectLineStrings(segment.Intersection(area), pieces);
                foreach (LineString piece in pieces)
                {
                    double t0 = double.PositiveInfinity, t1 = double.NegativeInfinity;
                    foreach (Coordinate c in piece.Coordinates)
                    {
                        double t = ((c.X - a.x) * dx + (c.Y - a.y) * dz) / (length * length);
                        t0 = System.Math.Min(t0, t);
                        t1 = System.Math.Max(t1, t);
                    }
                    t0 = System.Math.Max(t0, 0.0);
                    t1 = System.Math.Min(t1, 1.0);
                    // An end not equal to the original section endpoint lies on the expanded boundary (QUAY_FACE_TOLERANCE outside parcel); trim that excess.
                    if (t0 * length > 1e-4)
                    {
                        t0 += Constants.QUAY_FACE_TOLERANCE / length;
                    }
                    if ((1.0 - t1) * length > 1e-4)
                    {
                        t1 -= Constants.QUAY_FACE_TOLERANCE / length;
                    }
                    if ((t1 - t0) * length <= Constants.QUAY_FACE_MIN_LENGTH)
                    {
                        continue;
                    }
                    Vector2 d = b - a;
                    Vector2 pa = a + d * (float)t0;
                    Vector2 pb = a + d * (float)t1;
                    if (!IsOnLandSide(gf, parcel, pa, pb, quay.Segment.Normal))
                    {
                        continue;
                    }
                    var clipped = new ReclaimedLandSegment(pa, pb, quay.Segment.Normal);
                    result.Add(new ReclaimedQuay(quay.LandId, clipped, quay.TopHeight));
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortBMB] 埋立地の岸壁との照合に失敗しました: {ex.Message}。面していないものとして扱います。");
            result.Clear();
        }
        return result;
    }

    /// <summary>
    /// Whether the parcel lies landward of quay section p-q. Take a band of width <see cref="Constants.QUAY_PROBE_DISTANCE"/> opposite the outward normal
    /// inside reclamation; require parcel coverage of at least <see cref="Constants.QUAY_LAND_SIDE_MIN_COVER"/>.
    /// </summary>
    private static bool IsOnLandSide(GeometryFactory gf, Geometry parcel, Vector2 p, Vector2 q, Vector2 normal)
    {
        Vector2 inward = -normal.normalized * Constants.QUAY_PROBE_DISTANCE;
        Geometry strip = gf.CreatePolygon(new[]
        {
            new Coordinate(p.x, p.y),
            new Coordinate(q.x, q.y),
            new Coordinate(q.x + inward.x, q.y + inward.y),
            new Coordinate(p.x + inward.x, p.y + inward.y),
            new Coordinate(p.x, p.y),
        });
        double stripArea = strip.Area;
        return stripArea > 0.0 && parcel.Intersection(strip).Area >= Constants.QUAY_LAND_SIDE_MIN_COVER * stripArea;
    }

    /// <summary>Recursively collect LineStrings from Geometry (LineString / MultiLineString / Collection). Discard points, which have no length.</summary>
    private static void CollectLineStrings(Geometry geom, List<LineString> output)
    {
        if (geom == null || geom.IsEmpty)
        {
            return;
        }
        if (geom is LineString line)
        {
            output.Add(line);
            return;
        }
        if (geom is GeometryCollection)
        {
            for (int i = 0; i < geom.NumGeometries; i++)
            {
                CollectLineStrings(geom.GetGeometryN(i), output);
            }
        }
    }

    /// <summary>
    /// Return the largest polygon in the footprint/land intersection as a CCW local ring (relative to center_point).
    /// Null for missing coastline, empty intersection, or geometry-operation failure.
    /// </summary>
    private static Vector2[] ExtractLandPart(Vector2[] footprint, Vector3 centerPointWorld)
    {
        if (!CoastlineStore.IsLoaded || !CoastlineStore.HasCoastline)
        {
            return null;
        }
        Geometry land = GetLandGeometry();
        if (land == null || land.IsEmpty)
        {
            return null;
        }
        try
        {
            Geometry footprintPoly = ToWorldPolygon(new GeometryFactory(), footprint, centerPointWorld);
            Polygon best = LargestPolygon(footprintPoly.Intersection(land));
            return best != null && best.Area >= MIN_LAND_PART_AREA ? ToLocalRing(best, centerPointWorld) : null;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortBMB] 区画の陸の部分の抽出に失敗しました: {ex.Message}。底面全体を使います。");
            return null;
        }
    }

    /// <summary>Simple vertex mean. Rooftop-prop placement reference (not area centroid).</summary>
    private static Vector2 VertexCentroid(Vector2[] polygon)
    {
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < polygon.Length; i++)
        {
            centroid += polygon[i];
        }
        return centroid / polygon.Length;
    }

    /// <summary>Translate a local polygon by offset and return NTS coordinates closed by duplicating the first point.</summary>
    private static Coordinate[] ToClosedCoordinates(Vector2[] polygon, Vector2 offset)
    {
        int n = polygon.Length;
        var coords = new Coordinate[n + 1];
        for (int i = 0; i < n; i++)
        {
            coords[i] = new Coordinate(polygon[i].x + offset.x, polygon[i].y + offset.y);
        }
        coords[n] = new Coordinate(coords[0].X, coords[0].Y);
        return coords;
    }

    /// <summary>Convert local footprint (relative to center_point) to a world-space NTS polygon. Repair invalid/self-intersecting geometry with buffer(0).</summary>
    private static Geometry ToWorldPolygon(GeometryFactory gf, Vector2[] footprint, Vector3 centerPointWorld)
    {
        Geometry polygon = gf.CreatePolygon(ToClosedCoordinates(footprint, new Vector2(centerPointWorld.x, centerPointWorld.z)));
        return polygon.IsValid ? polygon : polygon.Buffer(0);
    }

    /// <summary>Largest-area Polygon among direct Geometry children. Null if none.</summary>
    private static Polygon LargestPolygon(Geometry geom)
    {
        if (geom == null || geom.IsEmpty)
        {
            return null;
        }
        Polygon best = null;
        for (int i = 0; i < geom.NumGeometries; i++)
        {
            if (geom.GetGeometryN(i) is Polygon p && (best == null || p.Area > best.Area))
            {
                best = p;
            }
        }
        return best;
    }

    /// <summary>
    /// Convert an NTS Polygon exterior to a CCW local ring relative to center_point. Remove closing duplicates and near-duplicates within 1cm
    /// to avoid degenerate triangulation; return null if fewer than three points remain.
    /// </summary>
    private static Vector2[] ToLocalRing(Polygon polygon, Vector3 centerPointWorld)
    {
        Coordinate[] ring = polygon.ExteriorRing.Coordinates;
        var result = new List<Vector2>(ring.Length);
        for (int i = 0; i < ring.Length - 1; i++)
        {
            var v = new Vector2((float)ring[i].X - centerPointWorld.x, (float)ring[i].Y - centerPointWorld.z);
            if (result.Count > 0 && (v - result[result.Count - 1]).sqrMagnitude < 1e-4f)
            {
                continue;
            }
            result.Add(v);
        }
        while (result.Count > 1 && (result[0] - result[result.Count - 1]).sqrMagnitude < 1e-4f)
        {
            result.RemoveAt(result.Count - 1);
        }
        if (result.Count < 3)
        {
            return null;
        }
        Vector2[] local = result.ToArray();
        if (PolygonUtils.SignedArea(local) < 0f)
        {
            System.Array.Reverse(local);
        }
        return local;
    }

    // Cache of land geometry from even-odd coastline-ring composition (NTS Geometry, world coordinates).
    // Coastlines expand when reclamation completes; CoastlineStore.Version advances on refetch, so rebuild only on version changes.
    private static Geometry cachedLandGeometry;
    private static int cachedCoastlineVersion = -1;

    /// <summary>
    /// Build/return cached NTS land geometry from all coastline rings.
    /// Rings do not distinguish exteriors from holes (lakes); even-odd composition (XOR) yields land
    /// (same interpretation as <see cref="CoastlineStore.IsLand"/> even-odd testing).
    /// Return null if no land exists.
    /// </summary>
    private static Geometry GetLandGeometry()
    {
        if (cachedCoastlineVersion == CoastlineStore.Version)
        {
            return cachedLandGeometry;
        }
        cachedCoastlineVersion = CoastlineStore.Version;
        cachedLandGeometry = null;

        var gf = new GeometryFactory();
        Geometry land = null;
        foreach (Vector2[] ring in CoastlineStore.Rings)
        {
            if (ring == null || ring.Length < 3)
            {
                continue;
            }
            // NTS LinearRing requires exact equality of first/last points;
            // always close with a copy of the first, even when the last is nearly identical.
            bool closed = (ring[0] - ring[ring.Length - 1]).sqrMagnitude < 1e-6f;
            int count = ring.Length + (closed ? 0 : 1);
            var coords = new Coordinate[count];
            for (int i = 0; i < ring.Length; i++)
            {
                coords[i] = new Coordinate(ring[i].x, ring[i].y);
            }
            coords[count - 1] = new Coordinate(ring[0].x, ring[0].y);

            Geometry poly;
            try
            {
                poly = gf.CreatePolygon(coords);
                if (!poly.IsValid)
                {
                    poly = poly.Buffer(0);
                }
            }
            catch (System.Exception)
            {
                continue;
            }
            if (poly.IsEmpty)
            {
                continue;
            }
            land = land == null ? poly : land.SymmetricDifference(poly);
        }
        cachedLandGeometry = land;
        return land;
    }

    /// <summary>
    /// Clip the local footprint (relative to center_point) along the backend's natural-coast quay line, retaining the landward side.
    /// berthLine is world XZ [A'x, A'z, B'x, B'z] with the sea on the left of A'→B' (BuildingSummary.berth_line). The backend decides
    /// this line and judges the port's water depth just outside it (geo_berth), so it is never re-derived here.
    /// Return the original footprint if the line is absent (not a coastal parcel, or undetermined) or geometry operations fail.
    /// Tests/Quay invokes this via reflection; preserve name and arguments.
    /// </summary>
    /// <param name="seaward">Sea boundary (edges from line clipping), used for water-edge enumeration. Valid=false if clipping fails.</param>
    private static Vector2[] BuildQuayFootprint(Vector2[] footprint, float[] berthLine, Vector3 centerPointWorld, out QuaySeawardEdge seaward)
    {
        seaward = default;
        if (berthLine == null || berthLine.Length < 4)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, "[PortBMB] 岸壁の線が無いためカットせず元形状を使用");
            return (Vector2[])footprint.Clone();
        }
        var aOut = new Vector2(berthLine[0], berthLine[1]);
        var bOut = new Vector2(berthLine[2], berthLine[3]);
        if ((bOut - aOut).sqrMagnitude < 1f)
        {
            return (Vector2[])footprint.Clone();
        }
        Vector2 u = (bOut - aOut).normalized;
        var perp = new Vector2(-u.y, u.x);

        try
        {
            var gf = new GeometryFactory();
            Geometry footprintPoly = ToWorldPolygon(gf, footprint, centerPointWorld);

            // Intersect footprint with landward half-plane of cut line A'-B' (sufficiently large rectangle) for quay footprint.
            const float L = 1e4f;
            Vector2 r1 = aOut - u * L;
            Vector2 r2 = bOut + u * L;
            Vector2 r3 = r2 - perp * L;
            Vector2 r4 = r1 - perp * L;
            Geometry landSideRect = gf.CreatePolygon(new[]
            {
                new Coordinate(r1.x, r1.y),
                new Coordinate(r2.x, r2.y),
                new Coordinate(r3.x, r3.y),
                new Coordinate(r4.x, r4.y),
                new Coordinate(r1.x, r1.y),
            });

            Geometry quay = footprintPoly.Intersection(landSideRect);
            if (quay == null || quay.IsEmpty || quay.Area < 1.0)
            {
                return (Vector2[])footprint.Clone();
            }
            Polygon best = LargestPolygon(quay);
            if (best == null)
            {
                return (Vector2[])footprint.Clone();
            }

            Vector2[] quayFootprint = ToLocalRing(best, centerPointWorld);
            if (quayFootprint == null)
            {
                return (Vector2[])footprint.Clone();
            }

            seaward = new QuaySeawardEdge
            {
                Valid = true,
                Origin = new Vector2(aOut.x - centerPointWorld.x, aOut.y - centerPointWorld.z),
                Dir = u,
                Normal = perp,
            };
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[PortBMB] 直線カット適用: 頂点 {footprint.Length}→{quayFootprint.Length}");
            return quayFootprint;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortBMB] 岸壁の直線カットに失敗しました: {ex.Message}。元の形状を使用します。");
            return (Vector2[])footprint.Clone();
        }
    }

    /// <summary>
    /// Generate a thick slab mesh from the quay footprint.
    /// Top is horizontal at <paramref name="topLocalY"/>; bottom follows terrain/seabed height at each vertex.
    /// Return two submeshes: subMesh0=top, subMesh1=sides+bottom.
    /// Same winding as <see cref="BuildingMeshBuilder.GenerateSimpleBuilding"/> (outward normals).
    /// </summary>
    private static Mesh BuildSlabMesh(Vector2[] polygon, float topLocalY, Vector3 centerPointWorld)
    {
        if (polygon == null || polygon.Length < 3)
        {
            return null;
        }

        Vector2[] p = (Vector2[])polygon.Clone();
        if (PolygonUtils.SignedArea(p) < 0f)
        {
            System.Array.Reverse(p);
        }
        int n = p.Length;

        // Each bottom vertex local Y = terrain/seabed height at that position − terrain height at center_point.
        // Clamp below top to prevent mesh inversion where shallow terrain rises above slab top.
        float[] bottomY = new float[n];
        for (int i = 0; i < n; i++)
        {
            float wx = p[i].x + centerPointWorld.x;
            float wz = p[i].y + centerPointWorld.z;
            float seabed = Utils.GetGroundHeight(new Vector3(wx, 1000f, wz));
            bottomY[i] = Mathf.Min(seabed - centerPointWorld.y, topLocalY - 0.05f);
        }

        List<int> capTriangles = BuildingMeshBuilder.TriangulateOrFan(p);

        List<Vector3> vertices = new List<Vector3>();
        List<int> topTriangles = new List<int>();
        List<int> sideTriangles = new List<int>();

        // Top (topLocalY, upward normals → reverse winding).
        int topStart = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(new Vector3(p[i].x, topLocalY, p[i].y));
        }
        for (int t = 0; t < capTriangles.Count; t += 3)
        {
            topTriangles.Add(topStart + capTriangles[t]);
            topTriangles.Add(topStart + capTriangles[t + 2]);
            topTriangles.Add(topStart + capTriangles[t + 1]);
        }

        // Bottom (per-vertex bottomY, downward normals → natural winding).
        int bottomStart = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(new Vector3(p[i].x, bottomY[i], p[i].y));
        }
        for (int t = 0; t < capTriangles.Count; t += 3)
        {
            sideTriangles.Add(bottomStart + capTriangles[t]);
            sideTriangles.Add(bottomStart + capTriangles[t + 1]);
            sideTriangles.Add(bottomStart + capTriangles[t + 2]);
        }

        // Sides (extrude each edge bottom→top; same winding as GenerateSimpleBuilding).
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int s = vertices.Count;
            vertices.Add(new Vector3(p[i].x, bottomY[i], p[i].y));        // s   : bottom i
            vertices.Add(new Vector3(p[next].x, bottomY[next], p[next].y)); // s+1 : bottom next
            vertices.Add(new Vector3(p[i].x, topLocalY, p[i].y));          // s+2 : top i
            vertices.Add(new Vector3(p[next].x, topLocalY, p[next].y));    // s+3 : top next

            // tri1 = (bottom_i, top_i, bottom_next)
            sideTriangles.Add(s);
            sideTriangles.Add(s + 2);
            sideTriangles.Add(s + 1);
            // tri2 = (bottom_next, top_i, top_next)
            sideTriangles.Add(s + 1);
            sideTriangles.Add(s + 2);
            sideTriangles.Add(s + 3);
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(topTriangles, 0);
        mesh.SetTriangles(sideTriangles, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Enumerate straight-cut quay water edges: footprint edges lying on the cut line, in footprint order.
    /// Empty if sea boundary is unknown (unloaded coastline/failed clipping), since no water edge can be determined.
    /// </summary>
    private static List<PortLayout.Waterline> EnumerateSeawardWaterlines(Vector2[] quayFootprint, QuaySeawardEdge seaward, float topLocalY)
    {
        var waterlines = new List<PortLayout.Waterline>();
        if (!seaward.Valid || quayFootprint == null || quayFootprint.Length < 3)
        {
            return waterlines;
        }

        int n = quayFootprint.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 p = quayFootprint[i];
            Vector2 q = quayFootprint[(i + 1) % n];

            // Only edges whose endpoint normal offsets are both effectively zero lie on the cut line.
            if (Mathf.Abs(Vector2.Dot(p - seaward.Origin, seaward.Normal)) > SEAWARD_EDGE_EPSILON ||
                Mathf.Abs(Vector2.Dot(q - seaward.Origin, seaward.Normal)) > SEAWARD_EDGE_EPSILON)
            {
                continue;
            }
            waterlines.Add(new PortLayout.Waterline { P = p, Q = q, Normal = seaward.Normal, Y = topLocalY });
        }
        return waterlines;
    }

    /// <summary>
    /// Add each submesh to the CombineInstance list with its corresponding material.
    /// </summary>
    private static void AppendMesh(Mesh mesh, Material[] subMeshMaterials,
        List<CombineInstance> combineInstances, List<Material> materials)
    {
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = mesh,
                subMeshIndex = s,
                transform = Matrix4x4.identity
            });
            materials.Add(s < subMeshMaterials.Length ? subMeshMaterials[s] : null);
        }
    }
}
