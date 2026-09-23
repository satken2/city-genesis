using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// モジュラーパーツ組み立て方式（modular）の建物メッシュ生成器。
/// ModularBodyBuilder で壁・地面を生成し、屋根ビルダー（Flat / House / Gable）で屋根を生成して、
/// 塔屋を配置し、最後に結合・ベイクを行う。
/// </summary>
public class ModularBuildingMeshBuilder : IBuildingMeshGenerator
{
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

        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(
            buildingData, useParcelAltitude: BuildingMeshBuilder.IsApplicableModelType(modelType),
            out ParcelSummary parcel, out Vector3 centerPointWorld);

        // MODEL_MODULAR_GROUND で parcel が取得できない場合は building footprint を fence 用にフォールバックする
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
    /// building_shape を明示的に指定してモジュラーパーツ組み立てによるメッシュを生成する。
    /// プレビュー用途など、マスターデータの値を上書きしたい場合に使用する。
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
        // プレビュー用途はワールド上の配置を持たないため、地形サンプリングを伴う基盤は生成しない
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

        Vector2[] occupiedFootprint = footprint;
        int stairSuffix = ModularBodyBuilder.ResolveBuildingVariant(prefix, BuildingMeshUtils.ComputeVariantNumber(visual_variant));
        var stairs = !isGroundModel && buildingShape != Constants.SHAPE_FULL
            ? ExteriorStairBuilder.Create(footprint, prefix, stairSuffix, floorHeight) : null;
        if (stairs != null) footprint = stairs.Body;

        // 1. 壁・地面・フェンス生成
        var bodyData = ModularBodyBuilder.GenerateBody(
            footprint, floors, prefix, floorHeight, visual_variant, buildingShape,
            modelType, fenceFootprint, centerPointWorld, isDynamic, floorMaterial, stairs);

        var combineInstances = bodyData.CombineInstances;
        var allMaterials = bodyData.Materials;
        var temporaryMeshes = bodyData.TemporaryMeshes;
        stairs?.Append(floors, floorHeight, combineInstances, allMaterials, temporaryMeshes);
        int resolvedSuffix = bodyData.ResolvedSuffix;
        // 屋根面は階高グリッドに置く。topパーツが持つパラペットや照明で屋根を持ち上げない。
        float buildingHeight = bodyData.BuildingHeight > 0f ? bodyData.BuildingHeight : floors * floorHeight;

        // 基盤（コンクリート基盤）: 建物底面をY方向に押し出し、傾斜地の谷側で建物が浮いて
        // 見える隙間を埋める。対象 model_type（IsApplicableModelType）のみ。
        // プレビュー経路（generateFoundation=false）はワールド配置が無く地形をサンプリングできないため生成しない。
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
            // 2. 屋根生成（model_type に応じて屋根ビルダーを切り替える）
            BuildingMeshResult roofResult = null;
            switch (modelType)
            {
                case Constants.MODEL_MODULAR_HOUSE:
                    // 壁・角・地面は FLAT と同じ。屋根だけ住宅風の寄棟にし、マテリアルは FLAT と同じ ceil パーツを使う。
                    roofResult = HouseRoofBuilder.GenerateRoof(
                        footprint, buildingHeight, floorHeight, prefix, resolvedSuffix,
                        allMaterials.Count > 0 ? allMaterials.ToArray() : null);
                    break;
                case Constants.MODEL_MODULAR_GABLE:
                    // 壁・角・地面は FLAT と同じ。屋根だけ切妻にし、マテリアルは FLAT と同じ ceil パーツを使う。
                    roofResult = GableRoofBuilder.GenerateRoof(
                        footprint, buildingHeight, floorHeight, prefix, resolvedSuffix,
                        allMaterials.Count > 0 ? allMaterials.ToArray() : null);
                    break;
                case Constants.MODEL_MODULAR_SHED:
                    // 壁・角・地面は FLAT と同じ。屋根だけ片流れ（トタン屋根）にする。
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

            // 3. 塔屋
            BuildingMeshBuilder.GenerateRoofprop(
                prefix, resolvedSuffix, visual_variant2, footprint, buildingHeight,
                combineInstances, allMaterials, temporaryMeshes);

            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBMB-DEBUG] After roofprop. combineInstances.Count={combineInstances.Count}");

            // 4. エントランス
            // SHAPE_FULL は最上階のみを描画するため、地上に壁が存在する場合(1階建て)に限って配置する。
            if (buildingShape != Constants.SHAPE_FULL || floors == 1)
            {
                BuildingMeshBuilder.GenerateEntrance(
                    prefix, resolvedSuffix, footprint,
                    combineInstances, allMaterials, temporaryMeshes);
            }
        }

        if (combineInstances.Count == 0)
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

        // 4. メッシュ結合
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // 5. コライダー生成
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
    /// 既存メッシュを CombineInstance リストに追加するユーティリティ。
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
