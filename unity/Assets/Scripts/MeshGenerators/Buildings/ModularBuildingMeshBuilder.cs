using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Building mesh generator using modular assembly.
/// Generate walls/ground with ModularBodyBuilder, roofs with Flat / House / Gable builders,
/// place rooftop props, then combine/bake.
/// </summary>
public class ModularBuildingMeshBuilder : IBuildingMeshGenerator
{
    private static readonly StageMarker roofMarker = new StageMarker("Building.Roof");
    private static readonly StageMarker combineMarker = new StageMarker("Building.Combine");
    private static readonly StageMarker normalsMarker = new StageMarker("Building.Normals");
    private static readonly StageMarker destroyTemporaryMarker = new StageMarker("Building.DestroyTemporary");

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"[MBMB] Invalid polygon data for Building ID: {buildingData.agent_id}. Using default building.");
            return BuildingMeshBuilder.GenerateSimpleBuilding(BuildingMeshUtils.GetFootprint(buildingData), 1, LocalConstants.FLOOR_HEIGHT);
        }

        Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
        int floors = Mathf.Max(1, buildingData.floors ?? 1);
        string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        float visual_variant = buildingData.visual_variant;
        float visual_variant2 = buildingData.visual_variant2;
        int modelType = GetModelType(buildingData);
        int buildingShape = GetBuildingShape(buildingData);

        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(buildingData, out ParcelSummary parcel, out Vector3 centerPointWorld);

        // For MODEL_MODULAR_GROUND without a parcel, fall back to the building footprint for fences.
        if (fenceFootprint == null && modelType == Constants.MODEL_MODULAR_GROUND)
        {
            fenceFootprint = (Vector2[])footprint.Clone();
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                () => $"[MBMB] parcel_id={buildingData.parcel_id} から fenceFootprint を取得できなかったため、building footprint をフォールバックします。");
        }

        Material floorMaterial = BuildingMeshUtils.ResolveFloorMaterialFromMaster(buildingData, prefix);

        return GenerateInternal(footprint, floors, prefix, floorHeight, visual_variant, visual_variant2, modelType, buildingShape, fenceFootprint, centerPointWorld, isDynamic, floorMaterial: floorMaterial);
    }

    /// <summary>
    /// Generate a modular mesh with explicit building_shape.
    /// Used for previews etc. that override master data.
    /// </summary>
    public static BuildingMeshResult GenerateWithBuildingShape(
        Vector2[] footprint,
        int floors,
        string prefix,
        float floorHeight,
        float visual_variant,
        float visual_variant2,
        int buildingShape)
    {
        int modelType = Constants.MODEL_MODULAR_FLAT;
        // Previews have no world placement, so omit foundations requiring terrain sampling.
        return GenerateInternal(footprint, floors, prefix, floorHeight, visual_variant, visual_variant2, modelType, buildingShape, null, Vector3.zero, generateFoundation: false);
    }

    private static BuildingMeshResult GenerateInternal(
        Vector2[] footprint,
        int floors,
        string prefix,
        float floorHeight,
        float visual_variant,
        float visual_variant2,
        int modelType,
        int buildingShape,
        Vector2[] fenceFootprint,
        Vector3 centerPointWorld,
        bool isDynamic = false,
        bool generateFoundation = true,
        Material floorMaterial = null)
    {
        bool isGroundModel = modelType == Constants.MODEL_MODULAR_GROUND;

        // Fit exterior stairs/entrance inside the original footprint; use the setback body outline for walls, roof, floors, foundation, and rooftop props.
        // SHAPE_FULL draws only the top floor, so omit exterior stairs; allow entrances only for single-floor buildings with ground-level walls.
        Vector2[] occupiedFootprint = footprint;
        int partSuffix = ModularBodyBuilder.ResolveBuildingVariant(prefix, BuildingMeshUtils.ResolveModelId(prefix, visual_variant));
        Bounds? entrance = null;
        if ((buildingShape != Constants.SHAPE_FULL || floors == 1)
            && BuildingMeshBuilder.TryGetEntranceBounds(prefix, partSuffix, out Bounds entranceBounds))
            entrance = entranceBounds;
        var layout = isGroundModel ? null : ExteriorStairBuilder.Create(
            footprint, prefix, partSuffix, floorHeight, buildingShape != Constants.SHAPE_FULL, entrance);
        if (layout != null) footprint = layout.Body;

        // 1. Generate walls, ground, fences, and scatter parts.
        var bodyData = ModularBodyBuilder.GenerateBody(
            footprint, floors, prefix, floorHeight, visual_variant, buildingShape,
            modelType, fenceFootprint, centerPointWorld, isDynamic, floorMaterial, layout,
            occupiedFootprint, visual_variant2);

        var combineInstances = bodyData.CombineInstances;
        var allMaterials = bodyData.Materials;
        var temporaryMeshes = bodyData.TemporaryMeshes;
        layout?.Append(floors, floorHeight, combineInstances, allMaterials, temporaryMeshes);
        int resolvedSuffix = bodyData.ResolvedSuffix;
        // Place roofs on the floor-height grid. Do not raise them for parapets/lights in top parts.
        float buildingHeight = bodyData.BuildingHeight > 0f ? bodyData.BuildingHeight : floors * floorHeight;

        // Foundation (concrete): extrude the footprint in Y to fill downhill gaps
        // that make buildings look suspended. Only for supported model_type values (IsApplicableModelType).
        // Previews (generateFoundation=false) lack world placement/terrain sampling and omit foundations.
        if (generateFoundation && BuildingMeshBuilder.IsApplicableModelType(modelType)
            && footprint != null && footprint.Length >= 3)
        {
            Mesh foundationMesh = BuildingMeshBuilder.GenerateFoundationMesh(footprint, centerPointWorld);
            if (foundationMesh != null)
            {
                combineInstances.Add(new CombineInstance
                {
                    mesh = foundationMesh,
                    subMeshIndex = 0,
                    transform = Matrix4x4.identity
                });
                allMaterials.Add(BuildingMeshBuilder.GetCommonConcreteMaterial());
                temporaryMeshes.Add(foundationMesh);
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBMB-DEBUG] bodyData.IsValid={bodyData.IsValid}, combineInstances.Count={combineInstances?.Count}, allMaterials.Count={allMaterials?.Count}, resolvedSuffix={resolvedSuffix}");

        if (!isGroundModel)
        {
            bool slabRoof = modelType == Constants.MODEL_MODULAR_FLAT;
            bool hasInteriorFloors = buildingShape != Constants.SHAPE_FULL && InteriorFloorBuilder.Append(
                footprint, floors, floorHeight, prefix, resolvedSuffix, slabRoof,
                combineInstances, allMaterials, temporaryMeshes);
            // 2. Generate roof (select builder by model_type).
            BuildingMeshResult roofResult = null;
            using (roofMarker.Auto())
            {
                switch (modelType)
                {
                    case Constants.MODEL_MODULAR_HOUSE:
                        // Walls/corners/ground match FLAT. Only the roof becomes a residential hip roof, using the same ceil material as FLAT.
                        roofResult = HouseRoofBuilder.GenerateRoof(
                            footprint, buildingHeight, floorHeight, prefix, resolvedSuffix,
                            allMaterials.Count > 0 ? allMaterials.ToArray() : null);
                        break;
                    case Constants.MODEL_MODULAR_GABLE:
                        // Walls/corners/ground match FLAT. Only the roof becomes a gable roof, using the same ceil material as FLAT.
                        roofResult = GableRoofBuilder.GenerateRoof(
                            footprint, buildingHeight, floorHeight, prefix, resolvedSuffix,
                            allMaterials.Count > 0 ? allMaterials.ToArray() : null);
                        break;
                    case Constants.MODEL_MODULAR_SHED:
                        // Walls/corners/ground match FLAT. Only the roof becomes a shed roof (corrugated metal).
                        roofResult = ShedRoofBuilder.GenerateRoof(
                            footprint, buildingHeight, floorHeight, prefix, resolvedSuffix,
                            allMaterials.Count > 0 ? allMaterials.ToArray() : null);
                        break;
                    case Constants.MODEL_MODULAR_FLAT:
                    default:
                        if (!hasInteriorFloors)
                            roofResult = FlatRoofBuilder.GenerateRoof(
                                footprint, buildingHeight, prefix, resolvedSuffix,
                                allMaterials.Count > 0 ? allMaterials.ToArray() : null);
                        break;
                }
            }

            if (roofResult?.Mesh != null)
            {
                AppendMeshInstances(roofResult.Mesh, roofResult.Materials, combineInstances, allMaterials);
                temporaryMeshes.Add(roofResult.Mesh);
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBMB-DEBUG] Roof appended. combineInstances.Count={combineInstances.Count}");
            }
            else if (!(hasInteriorFloors && slabRoof))
            {
                Debug.LogWarning($"[MBMB-DEBUG] Roof is null or mesh is null. roofResult={(roofResult == null ? "null" : "not null")}");
            }

            // 3. Rooftop props.
            BuildingMeshBuilder.GenerateRoofprop(
                prefix, resolvedSuffix, visual_variant2, footprint, buildingHeight,
                combineInstances, allMaterials, temporaryMeshes);

            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBMB-DEBUG] After roofprop. combineInstances.Count={combineInstances.Count}");

            // 4. Entrance (omit if rejected because it cannot fit inside the original footprint).
            if (layout?.Entrance != null)
            {
                BuildingMeshBuilder.GenerateEntrance(
                    prefix, resolvedSuffix, footprint,
                    combineInstances, allMaterials, temporaryMeshes);
            }
        }

        if (combineInstances.Count == 0 && !BuildingExteriorRenderer.HasCapturedWork)
        {
            Debug.LogWarning($"[MBMB] パーツが0件 → 白い箱フォールバック");
            foreach (Mesh temp in temporaryMeshes)
            {
                Object.Destroy(temp);
            }
            if (isGroundModel && fenceFootprint != null && fenceFootprint.Length >= 3)
            {
                return BuildingMeshBuilder.GenerateSimpleBuilding(fenceFootprint, 1, floorHeight);
            }
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, floors, floorHeight);
        }

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBMB] 結合: {combineInstances.Count}インスタンス, {allMaterials.Count}マテリアル");

        // 4. Combine meshes.
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        // A ground-only building whose only content is deferred (ground panel) has nothing to combine now.
        using (combineMarker.Auto())
            if (combineInstances.Count > 0) finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        using (normalsMarker.Auto())
        {
            finalMesh.RecalculateNormals();
            finalMesh.RecalculateBounds();
        }

        using (destroyTemporaryMarker.Auto())
        {
            foreach (Mesh temp in temporaryMeshes)
            {
                Object.Destroy(temp);
            }
        }

        // 5. Generate collider.
        Vector2[] colliderFootprint = isGroundModel && fenceFootprint != null && fenceFootprint.Length >= 3
            ? fenceFootprint : occupiedFootprint;
        float colliderHeight = isGroundModel ? floorHeight : buildingHeight;
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(colliderFootprint, colliderHeight);

        var result = new BuildingMeshResult(finalMesh, allMaterials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    private static int GetModelType(BuildingSummary buildingData)
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            var agentMaster = MasterDataStore.Instance.GetAgentMaster(buildingData.variant);
            if (agentMaster?.model_type != null)
            {
                return agentMaster.model_type.Value;
            }
        }
        return Constants.MODEL_MODULAR_FLAT;
    }

    private static int GetBuildingShape(BuildingSummary buildingData)
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            var agentMaster = MasterDataStore.Instance.GetAgentMaster(buildingData.variant);
            if (agentMaster?.building_shape != null)
            {
                return agentMaster.building_shape.Value;
            }
        }
        return Constants.SHAPE_OFFICE;
    }

    /// <summary>
    /// Utility adding existing meshes to the CombineInstance list.
    /// </summary>
    private static void AppendMeshInstances(Mesh source, Material[] sourceMaterials, List<CombineInstance> combineInstances, List<Material> materials)
    {
        if (source == null || source.subMeshCount == 0)
        {
            return;
        }

        for (int subIndex = 0; subIndex < source.subMeshCount; subIndex++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = source,
                subMeshIndex = subIndex,
                transform = Matrix4x4.identity
            });

            Material material = null;
            if (sourceMaterials != null && subIndex < sourceMaterials.Length)
            {
                material = sourceMaterials[subIndex];
            }
            materials.Add(material);
        }
    }
}
