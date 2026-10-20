using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Mashup building (MODEL_MASHUP) mesh generator.
/// Assemble facilities with multiple wings, such as schools (classroom/gym) and substations (office/transformers),
/// using a placement plan (BuildingSummary.sub_buildings) dividing parcels into 8m×8m cells.
/// The backend resolves cell division and wing allocation; Unity receives only each sub-building's
/// position (relative to center_point), rotation, and floor count (no cell-division logic).
///
/// Assemble each sub-building by stacking base / mid / top parts from the bottom:
/// - mashup-{model_prefix}-base-prebuilt … first floor.
/// - mashup-{model_prefix}-mid-prebuilt  … one repeated floor (count varies with floors).
/// - mashup-{model_prefix}-top-prebuilt  … top; may represent the complete building.
/// Render all parts at authored size without XYZ scaling; stacking Y follows cumulative authored heights
/// (unlike prebuilt, no mid floor-height Y scaling or floor-height grid).
/// height_per_floor determines only fallback white-box height for wings with no parts.
/// Unlike prebuilt, resolve parts from exterior model-number folders (independent scan per part).
/// No rooftop props or entrance.
///
/// As with prebuilt, rest at the building center's terrain elevation (excluded from IsApplicableModelType slope handling;
/// no concrete foundation). Since large parcels can span slopes, always adjust each sub-building Y
/// to terrain elevation at its own position (difference from center elevation).
///
/// Draw site decorations along the parcel: fences (fence-w / fence-c) and ground panels (floor, only with master draw_floor=yes).
/// Do not place tiles (tile).
/// Extrude the parcel polygon to the maximum rendered height for the collider
/// (so clicking anywhere in the parcel selects the building).
///
/// Open pits (MODEL_OPEN_PIT) use the same assembly; facilities arrive as sub-buildings arranged in the forecourt.
/// The rear parcel is a hole excavated into terrain by the backend. Avoid closing it with parcel extrusions
/// (colliders/white boxes); use a terrain-following parcel surface and facility boxes as colliders.
///
/// For Far, <see cref="FarBuildingMeshBuilder"/> places wing-specific distant models in the same layout.
/// </summary>
public class MashupBuildingMeshBuilder : IBuildingMeshGenerator
{
    private readonly bool openPit;

    /// <param name="openPit">Generate as an open pit (MODEL_OPEN_PIT).</param>
    public MashupBuildingMeshBuilder(bool openPit = false)
    {
        this.openPit = openPit;
    }

    /// <summary>
    /// Generate a mashup building mesh from BuildingSummary.
    /// When isDynamic is true, fences/ground panels follow terrain.
    /// </summary>
    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null)
        {
            Debug.LogError("MashupBuildingMeshBuilder: buildingData が null です。");
            return null;
        }

        BuildingMeshUtils.InitializeCache();

        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        string glbPrefix = BuildingMeshUtils.GetGlbPrefix1(buildingData);
        int variantNumber = BuildingMeshUtils.ResolveModelId(glbPrefix, buildingData.visual_variant);

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // Parcel polygon (local XZ relative to center_point) and placement-reference world coordinates (terrain elevation at center).
        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(
            buildingData, out _, out Vector3 centerPointWorld);

        // --- Stack sub-buildings ---
        // maxTopY is the highest rendered sub-building top (local Y), used for collider height.
        // facilityBounds are per-wing bounding boxes for open-pit colliders (building-local coordinates).
        float maxTopY = 0f;
        var facilityBounds = new List<Bounds>();
        if (buildingData.sub_buildings != null)
        {
            foreach (SubBuildingPlacement placement in buildingData.sub_buildings)
            {
                if (!IsValidPlacement(placement, buildingData))
                {
                    continue;
                }
                int firstMesh = temporaryMeshes.Count;
                float topY = GenerateSubBuilding(
                    placement, glbPrefix, variantNumber, floorHeight, centerPointWorld,
                    combineInstances, materials, temporaryMeshes);
                maxTopY = Mathf.Max(maxTopY, topY);
                if (openPit && TryEncapsulate(temporaryMeshes, firstMesh, out Bounds bounds))
                {
                    facilityBounds.Add(bounds);
                }
            }
        }

        // --- Site decorations (fences/ground panels) ---
        // Shared with modular/prebuilt buildings. Pass null for tilePolygon to omit tiles.
        // Buildings rest at center terrain elevation; make decorations terrain-following in ultra-near mode (isDynamic).
        Vector2[] footprint = (buildingData.polygonArray != null && buildingData.polygonArray.Length >= 3)
            ? BuildingMeshUtils.GetFootprint(buildingData)
            : null;
        Vector2[] sitePolygon = (fenceFootprint != null && fenceFootprint.Length >= 3) ? fenceFootprint : footprint;
        Material floorMaterial = BuildingMeshUtils.ResolveFloorMaterialFromMaster(buildingData, glbPrefix);
        ModularBodyBuilder.GenerateSiteDecorations(
            glbPrefix, 0,
            fenceFootprint, null, sitePolygon,
            floorMaterial,
            centerPointWorld, deformToTerrain: isDynamic,
            combineInstances, materials, temporaryMeshes);

        if (combineInstances.Count == 0 && !openPit)
        {
            // If no sub-buildings are registered and no decoration parts exist, show only a white footprint box to indicate presence.
            CleanupMeshes(temporaryMeshes);
            return GenerateFallbackBox(buildingData);
        }

        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateBounds();
        finalMesh.RecalculateNormals();

        CleanupMeshes(temporaryMeshes);

        // Extrude the whole parcel (or footprint if unavailable) to maximum rendered height for the collider.
        // Open-pit origins lie at the pit bottom, so extrusion cannot reach the forecourt; use terrain-following surfaces and facility boxes.
        Vector2[] colliderFootprint = sitePolygon;
        float colliderHeight = Mathf.Max(maxTopY, floorHeight);
        Mesh colliderMesh;
        if (openPit)
        {
            colliderMesh = GenerateOpenPitCollider(colliderFootprint, centerPointWorld, facilityBounds);
        }
        else
        {
            colliderMesh = (colliderFootprint != null && colliderFootprint.Length >= 3)
                ? BuildingMeshBuilder.GenerateColliderMesh(colliderFootprint, colliderHeight)
                : null;
        }

        // Submeshes grow with cell count × part count; merge identical materials to limit draw calls.
        return BuildingMeshBuilder.MergeSubMeshes(new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh));
    }

    private static bool IsValidPlacement(SubBuildingPlacement placement, BuildingSummary buildingData)
    {
        if (placement != null && placement.position != null && placement.position.Length >= 2
            && !string.IsNullOrEmpty(placement.model_prefix))
        {
            return true;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[Mashup] agent_id={buildingData.agent_id} の sub_buildings に不正なエントリがあるためスキップします。");
        return false;
    }

    /// <summary>
    /// Stack one sub-building and add it to the CombineInstance list.
    /// </summary>
    /// <returns>Local Y of this wing's top (for collider height).</returns>
    private static float GenerateSubBuilding(
        SubBuildingPlacement placement, string glbPrefix, int variantNumber, float floorHeight,
        Vector3 centerPointWorld,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        // Resolve each part by independently scanning model-number folders (no common fallback).
        GameObject baseModule = BuildingMeshUtils.GetPartBySuffixScan(glbPrefix, $"mashup-{placement.model_prefix}-base-prebuilt", variantNumber);
        GameObject middleModule = BuildingMeshUtils.GetPartBySuffixScan(glbPrefix, $"mashup-{placement.model_prefix}-mid-prebuilt", variantNumber);
        GameObject topModule = BuildingMeshUtils.GetPartBySuffixScan(glbPrefix, $"mashup-{placement.model_prefix}-top-prebuilt", variantNumber);

        Vector2 xzOffset = new Vector2(placement.position[0], placement.position[1]);
        int floors = Mathf.Max(1, placement.floors);
        // Used for white-box height when a wing has no parts.
        float subFloorHeight = (placement.height_per_floor.HasValue && placement.height_per_floor.Value > 0f)
            ? placement.height_per_floor.Value
            : floorHeight;

        // Apply rotation using the wall/entrance convention (model X axis along the edge).
        // This aligns model X (width in Blender) with the master width cell-allocation direction
        // (road-facing edge), placing the front corresponding to the wall exterior toward the road.
        Quaternion rotation = Quaternion.Euler(0, -placement.rotation + 180f, 0);

        // Use each wing's placement terrain elevation as its Y reference so it neither floats nor sinks
        // across sloped parcels (subtract center elevation because the building GameObject sits there).
        float terrainY = Utils.GetGroundHeight(
            new Vector3(centerPointWorld.x + xzOffset.x, 1000f, centerPointWorld.z + xzOffset.y));
        float baseY = terrainY - centerPointWorld.y;

        if (baseModule == null && middleModule == null && topModule == null)
        {
            Debug.LogWarning(
                $"MashupBuildingMeshBuilder: サブ建物のプレハブが見つかりませんでした " +
                $"(prefix={glbPrefix}, model_prefix={placement.model_prefix})。1セル分の白い箱で代替します。");
            float boxHeight = floors * subFloorHeight;
            AddFallbackCellBox(xzOffset, rotation, baseY, boxHeight, combineInstances, materials, temporaryMeshes);
            return baseY + boxHeight;
        }

        // Stack all parts at actual size (fitHeight=null), with Y determined by cumulative authored height
        // (each actual-size top becomes the next bottom, avoiding gaps or burial).
        // Advance levels only for existing parts; top rests on the ground when base/mid are absent
        // (allowing a complete model represented by top alone).
        float stackY = baseY;
        float maxDrawnTop = baseY;

        if (baseModule != null)
        {
            float drawn = BuildingMeshUtils.AddStackedModule(baseModule, stackY, null,
                combineInstances, materials, temporaryMeshes,
                xzOffset: xzOffset, rotation: rotation, alignByOrigin: true);
            stackY += drawn;
            maxDrawnTop = Mathf.Max(maxDrawnTop, stackY);
        }

        if (middleModule != null)
        {
            // base/top consume one floor each; mid fills remaining floors.
            int midCount = Mathf.Max(0, floors - (baseModule != null ? 1 : 0) - (topModule != null ? 1 : 0));
            for (int i = 0; i < midCount; i++)
            {
                float drawn = BuildingMeshUtils.AddStackedModule(middleModule, stackY, null,
                    combineInstances, materials, temporaryMeshes,
                    xzOffset: xzOffset, rotation: rotation, alignByOrigin: true);
                stackY += drawn;
                maxDrawnTop = Mathf.Max(maxDrawnTop, stackY);
            }
        }

        if (topModule != null)
        {
            float drawn = BuildingMeshUtils.AddStackedModule(topModule, stackY, null,
                combineInstances, materials, temporaryMeshes,
                xzOffset: xzOffset, rotation: rotation, alignByOrigin: true);
            maxDrawnTop = Mathf.Max(maxDrawnTop, stackY + drawn);
        }

        return maxDrawnTop;
    }

    /// <summary>
    /// Replace a sub-building with no parts by a one-cell (MASHUP_CELL_SIZE square)
    /// white box at its placement. Add null material; EnsureMaterials supplies the default.
    /// </summary>
    private static void AddFallbackCellBox(
        Vector2 xzOffset, Quaternion rotation, float baseY, float height,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        float half = Constants.MASHUP_CELL_SIZE * 0.5f;
        Vector2[] localCorners =
        {
            new Vector2(-half, -half),
            new Vector2(half, -half),
            new Vector2(half, half),
            new Vector2(-half, half),
        };

        // Transform the cell rectangle into building-local space in AddStackedModule order (rotate → translate).
        Vector2[] rect = new Vector2[localCorners.Length];
        for (int i = 0; i < localCorners.Length; i++)
        {
            Vector3 rotated = rotation * new Vector3(localCorners[i].x, 0f, localCorners[i].y);
            rect[i] = new Vector2(rotated.x + xzOffset.x, rotated.z + xzOffset.y);
        }

        Mesh box = BuildingMeshBuilder.GenerateColliderMesh(rect, height);
        if (box == null)
        {
            return;
        }

        if (!Mathf.Approximately(baseY, 0f))
        {
            Vector3[] vertices = box.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i].y += baseY;
            }
            box.vertices = vertices;
        }
        box.RecalculateNormals();
        box.RecalculateBounds();

        combineInstances.Add(new CombineInstance
        {
            mesh = box,
            subMeshIndex = 0,
            transform = Matrix4x4.identity,
        });
        materials.Add(null);
        temporaryMeshes.Add(box);
    }

    /// <summary>
    /// Return bounds of temporaryMeshes from firstMesh onward (one wing's parts, already placed).
    /// </summary>
    private static bool TryEncapsulate(List<Mesh> meshes, int firstMesh, out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        for (int i = firstMesh; i < meshes.Count; i++)
        {
            if (meshes[i] == null) continue;
            if (!found)
            {
                bounds = meshes[i].bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(meshes[i].bounds);
            }
        }
        return found;
    }

    /// <summary>
    /// Open-pit collider. Combine a parcel-covering terrain-following surface (pickable at pit bottom, benches, and forecourt)
    /// with facility bounding boxes (pickable from the side).
    /// </summary>
    private static Mesh GenerateOpenPitCollider(Vector2[] sitePolygon, Vector3 centerPointWorld, List<Bounds> facilityBounds)
    {
        var parts = new List<CombineInstance>();
        var owned = new List<Mesh>();
        Mesh ground = ModularBodyBuilder.GenerateTerrainConformingFloorMesh(sitePolygon, centerPointWorld);
        if (ground != null)
        {
            owned.Add(ground);
            parts.Add(new CombineInstance { mesh = ground, transform = Matrix4x4.identity });
        }
        foreach (Bounds b in facilityBounds)
        {
            Vector2[] rect =
            {
                new Vector2(b.min.x, b.min.z),
                new Vector2(b.max.x, b.min.z),
                new Vector2(b.max.x, b.max.z),
                new Vector2(b.min.x, b.max.z),
            };
            Mesh box = BuildingMeshBuilder.GenerateColliderMesh(rect, Mathf.Max(0.1f, b.size.y));
            if (box == null) continue;
            owned.Add(box);
            parts.Add(new CombineInstance { mesh = box, transform = Matrix4x4.Translate(new Vector3(0f, b.min.y, 0f)) });
        }
        if (parts.Count == 0)
        {
            CleanupMeshes(owned);
            return null;
        }
        var collider = new Mesh { name = "OpenPitCollider", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        collider.CombineMeshes(parts.ToArray(), true, true);
        collider.RecalculateBounds();
        CleanupMeshes(owned);
        return collider;
    }

    /// <summary>
    /// White-box fallback when neither sub-buildings nor site decorations could be generated.
    /// </summary>
    private static BuildingMeshResult GenerateFallbackBox(BuildingSummary buildingData)
    {
        if (buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"MashupBuildingMeshBuilder: polygon が不正なため空メッシュを返します (agent_id={buildingData.agent_id})。");
            return new BuildingMeshResult(new Mesh(), null);
        }

        Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
        int floors = Mathf.Max(1, buildingData.floors ?? 1);
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, floors, floorHeight);
    }

    private static void CleanupMeshes(List<Mesh> meshes)
    {
        foreach (var mesh in meshes)
        {
            if (mesh != null)
            {
                Object.Destroy(mesh);
            }
        }
    }
}
