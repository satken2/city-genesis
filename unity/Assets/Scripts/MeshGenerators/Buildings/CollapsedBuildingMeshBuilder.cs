// Assets/Scripts/MeshGenerators/Buildings/CollapsedBuildingMeshBuilder.cs
using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 廃墟(STATUS_COLLAPSED=老朽倒壊 / STATUS_COLLAPSED_FIRE=焼失)の建物の「瓦礫」メッシュ生成器。
/// model_type に関わらず、BuildingMeshBuilder.Generate の冒頭で status により
/// このビルダーへ分岐する（完成品のメッシュは一切生成しない）。
/// 焼失は瓦礫マウンドのマテリアルを焦げた瓦礫(CollapsedRubbleBurned)に差し替えて描き分ける。
///
/// 生成物:
/// - 瓦礫マウンド … 敷地（土地区画ポリゴン。無ければ建物底面）全体を覆う、厚みと Perlinノイズの
///   凹凸を持つ盛り上がり。工事現場の土マウンド生成（ConstructingBuildingMeshBuilder.GenerateDirtMoundMesh）を
///   流用し、土より厚く荒い瓦礫パラメータ（CONSTRUCTION_RUBBLE_*）で生成する。
///   マテリアルは Resources/CollapsedRubble（未配置時は ConstructionDirt → デフォルトにフォールバック）。
///   任意ポリゴンに完全追従するため、非矩形区画でもはみ出さない。
/// - 任意の倒壊片 … collapse パーツが存在すれば、footprint の XZ バウンディングボックスへ
///   非一様スケールでフィットさせて1つ載せる（崩れた躯体・瓦礫の山などの既成モデル）。
///   高さは低く抑える。パーツが無ければマウンドのみで成立する（オプショナル）。
///
/// パーツ・マテリアルのアセットが未配置でも破綻しないオプショナル設計（工事現場ビルダーと同じ思想）。
/// </summary>
public class CollapsedBuildingMeshBuilder : IBuildingMeshGenerator
{
    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null)
        {
            Debug.LogError("CollapsedBuildingMeshBuilder: buildingData が null です。");
            return null;
        }

        BuildingMeshUtils.InitializeCache();

        int modelType = BuildingMeshBuilder.GetModelType(buildingData);

        // 建物底面（ローカル座標・CCW）。polygon が不正でも瓦礫だけは描けるよう null 許容
        Vector2[] footprint = (buildingData.polygonArray != null && buildingData.polygonArray.Length >= 3)
            ? BuildingMeshUtils.GetFootprint(buildingData)
            : null;

        // 敷地コンテキスト。高さ基準は工事現場・完成品と同じ規則にし、状態遷移で建物が上下に跳ねないようにする
        Vector2[] parcelFootprint = BuildingMeshUtils.ResolveParcelContext(
            buildingData, useParcelAltitude: BuildingMeshBuilder.IsApplicableModelType(modelType),
            out ParcelSummary parcel, out Vector3 centerPointWorld);

        // 瓦礫マウンドの対象ポリゴン（ローカルXZ）。区画が無ければ建物底面を使う
        Vector2[] rubblePolygon = parcelFootprint ?? footprint;

        if (rubblePolygon == null)
        {
            Debug.LogWarning($"CollapsedBuildingMeshBuilder: polygon も parcel も不正なため空メッシュを返します (agent_id={buildingData.agent_id})。");
            return new BuildingMeshResult(new Mesh(), null);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> allMaterials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // --- 1. 瓦礫マウンド（土マウンド生成を瓦礫パラメータで流用） ---
        // 焼失(STATUS_COLLAPSED_FIRE)は焦げた瓦礫マテリアルで老朽倒壊と描き分ける
        Material rubbleMaterial = (buildingData.status == Constants.STATUS_COLLAPSED_FIRE)
            ? BuildingMeshBuilder.GetBurnedRubbleMaterial()
            : BuildingMeshBuilder.GetCollapsedRubbleMaterial();
        Mesh moundMesh = ConstructingBuildingMeshBuilder.GenerateDirtMoundMesh(
            rubblePolygon, centerPointWorld, isDynamic,
            thickness: LocalConstants.CONSTRUCTION_RUBBLE_THICKNESS,
            bumpAmplitude: LocalConstants.CONSTRUCTION_RUBBLE_BUMP_AMPLITUDE);
        if (moundMesh != null)
        {
            combineInstances.Add(new CombineInstance { mesh = moundMesh, subMeshIndex = 0, transform = Matrix4x4.identity });
            allMaterials.Add(rubbleMaterial);
            temporaryMeshes.Add(moundMesh);
        }

        // --- 2. 任意の倒壊片（collapse パーツを footprint に合わせてフィット） ---
        // 地面アセット（畑・庭）と岸壁には躯体片を載せない（瓦礫のみ）
        bool wantsFragment = footprint != null
            && modelType != Constants.MODEL_MODULAR_GROUND
            && modelType != Constants.MODEL_PORT;

        if (wantsFragment)
        {
            string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
            float visualVariant = buildingData.visual_variant;
            AddCollapseFragment(footprint, prefix, visualVariant,
                combineInstances, allMaterials, temporaryMeshes);
        }

        if (combineInstances.Count == 0)
        {
            foreach (Mesh temp in temporaryMeshes)
            {
                Object.Destroy(temp);
            }
            return new BuildingMeshResult(new Mesh(), null);
        }

        // --- 3. 結合 ---
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // --- 4. コライダー ---
        // 瓦礫は低いので、footprint（無ければ区画）を瓦礫のおおよその天端まで押し出した低い箱。
        Vector2[] colliderFootprint = footprint ?? rubblePolygon;
        float colliderHeight = LocalConstants.CONSTRUCTION_RUBBLE_THICKNESS + LocalConstants.CONSTRUCTION_RUBBLE_BUMP_AMPLITUDE;
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(colliderFootprint, colliderHeight);

        var result = new BuildingMeshResult(finalMesh, allMaterials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    /// <summary>
    /// 倒壊片（collapse パーツ）を footprint の XZ バウンディングボックスへフィットさせて登録する。
    /// - XZ: モデルの XZ bounds を footprint AABB（× COLLAPSE_FRAGMENT_FILL の余白）へ非一様スケール。
    ///   任意ポリゴンでも AABB に収めるので、矩形でない区画でも大きくはみ出さない。
    /// - Y: 実寸を基本としつつ COLLAPSE_FRAGMENT_MAX_HEIGHT で上限クランプし、瓦礫を低く保つ。
    /// - 位置: モデルの XZ 中心を footprint AABB の中心へ、下端を地面(ローカルY=0)へ合わせる。
    /// パーツが無ければ何もしない（オプショナル）。
    /// </summary>
    private static void AddCollapseFragment(Vector2[] footprint, string prefix, float visualVariant,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        GameObject fragmentModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, "collapse", visualVariant);
        if (fragmentModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[CLPS] 倒壊片: collapse パーツなし (prefix={prefix})。瓦礫マウンドのみで描画します。");
            return;
        }

        Mesh moduleMesh = BuildingMeshUtils.CombineModuleMeshes(fragmentModule, out Material[] moduleMaterials);
        if (moduleMesh == null || moduleMesh.subMeshCount == 0)
        {
            if (moduleMesh != null) Object.Destroy(moduleMesh);
            return;
        }

        // footprint の AABB（ローカルXZ）
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < footprint.Length; i++)
        {
            minX = Mathf.Min(minX, footprint[i].x);
            maxX = Mathf.Max(maxX, footprint[i].x);
            minZ = Mathf.Min(minZ, footprint[i].y);
            maxZ = Mathf.Max(maxZ, footprint[i].y);
        }
        float footprintSizeX = Mathf.Max(maxX - minX, 0.01f);
        float footprintSizeZ = Mathf.Max(maxZ - minZ, 0.01f);
        Vector2 footprintCenter = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);

        Bounds bounds = moduleMesh.bounds;
        float moduleSizeX = Mathf.Max(bounds.size.x, 0.01f);
        float moduleSizeZ = Mathf.Max(bounds.size.z, 0.01f);
        float moduleSizeY = Mathf.Max(bounds.size.y, 0.01f);

        float fill = LocalConstants.COLLAPSE_FRAGMENT_FILL;
        float scaleX = (footprintSizeX * fill) / moduleSizeX;
        float scaleZ = (footprintSizeZ * fill) / moduleSizeZ;
        // Y は実寸(=1)を基本に、上限高さを超える場合だけ縮める（瓦礫を低く保ち、無闇に伸ばさない）
        float scaleY = Mathf.Min(1f, LocalConstants.COLLAPSE_FRAGMENT_MAX_HEIGHT / moduleSizeY);

        Vector3[] vertices = moduleMesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            // モデルの XZ 中心を原点へ寄せてスケールし、footprint 中心へ平行移動。下端を地面(Y=0)へ。
            v.x = (v.x - bounds.center.x) * scaleX + footprintCenter.x;
            v.z = (v.z - bounds.center.z) * scaleZ + footprintCenter.y;
            v.y = (v.y - bounds.min.y) * scaleY;
            vertices[i] = v;
        }
        moduleMesh.vertices = vertices;
        moduleMesh.RecalculateNormals();
        moduleMesh.RecalculateBounds();

        for (int subIndex = 0; subIndex < moduleMesh.subMeshCount; subIndex++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = moduleMesh,
                subMeshIndex = subIndex,
                transform = Matrix4x4.identity,
            });
            Material material = (moduleMaterials != null && subIndex < moduleMaterials.Length) ? moduleMaterials[subIndex] : null;
            allMaterials.Add(material);
        }
        temporaryMeshes.Add(moduleMesh);
    }
}
