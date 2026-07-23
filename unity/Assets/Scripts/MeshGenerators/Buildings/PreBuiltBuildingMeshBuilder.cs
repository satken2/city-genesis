using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 既成パーツ（prebuilt）による特殊建物のメッシュ生成器。
/// base-prebuilt / mid-prebuilt / top-prebuilt のパーツを下から積み上げて建物を組み立てる。
///
/// パーツの意味とスケール規則:
/// - base-prebuilt … 1階部分。高さが階高(height_per_floor)になるようY方向のみスケールされる。XZは実寸のまま。
/// - mid-prebuilt  … 繰り返しフロア1階分。baseと同じくY方向のみ階高スケール。XZは実寸のまま。
/// - top-prebuilt  … 頂部。一切スケールされず実寸のまま配置される（尖塔や完成済みモデル丸ごとに使う）。
///
/// 積み上げ規則:
/// - base と top はそれぞれ論理階数(floors)のうち1階分を消費し、mid は残りの階数分繰り返される。
/// - 各パーツのY位置は「実際に描画したパーツの累積高さ」で決まる。ファイルが存在しないパーツは
///   描画も階の消費もスキップ…ではなく、階の消費は「ファイルが存在するパーツ」だけが行う。
///   このため base/mid が無い建物では top が接地する（完成済みモデルを top だけで表現できる）。
/// - top の実寸が1階分より大きい場合、描画上の建物はシミュレーション上の高さ
///   (floors × 階高)より高くなるが、これは仕様として許容する。
/// - 積み上げの天面を塞ぐパネルは生成しない。prebuilt パーツはモジュラーの壁パーツと違い
///   完成された3Dモデルなので、上から見える面はモデル側で閉じておくこと。
///
/// 敷地の装飾: モジュラー建物と共通の処理（ModularBodyBuilder.GenerateSiteDecorations）で、
/// 土地区画の外周フェンス（fence-w / fence-c）・敷地内のタイル（tile）・地面パネル（floor）を
/// 配置する。いずれも該当パーツが存在する場合のみ描画されるオプション要素
/// （地面パネルは floor パーツがあるときだけ。デフォルトマテリアルへのフォールバックはしない）。
///
/// 傾斜地対応（IsApplicableModelType）の対象外: 建物は区画基準標高ではなく中心位置の
/// 地形標高に接地し（BuildingManager 側の配置規則）、コンクリート基盤も生成しない。
/// このため超近景（isDynamic）ではフェンス・タイル・地面パネルを地形に追従させる
/// （MODEL_MODULAR_GROUND と同じ扱い）。
/// </summary>
public class PreBuiltBuildingMeshBuilder : IBuildingMeshGenerator
{
    /// <summary>
    /// BuildingSummaryから既成建物メッシュを生成。
    /// isDynamic が true の場合、フェンス・タイル・地面パネルを地形に追従させる。
    /// </summary>
    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null)
        {
            Debug.LogError("PreBuiltBuildingMeshBuilder: buildingData が null です。");
            return null;
        }

        BuildingMeshUtils.InitializeCache();

        int floors = Mathf.Max(1, buildingData.floors ?? 1);
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        string glbPrefix = BuildingMeshUtils.GetGlbPrefix1(buildingData);

        GameObject baseModule = BuildingMeshUtils.GetPartNoPosition(glbPrefix, "base-prebuilt", 0, true);
        GameObject middleModule = BuildingMeshUtils.GetPartNoPosition(glbPrefix, "mid-prebuilt", 0, true);
        GameObject topModule = BuildingMeshUtils.GetPartNoPosition(glbPrefix, "top-prebuilt", 0, true);

        if (baseModule == null && middleModule == null && topModule == null)
        {
            Debug.LogWarning($"PreBuiltBuildingMeshBuilder: プレハブモジュールが見つかりませんでした (prefix={glbPrefix})。白い箱にフォールバックします。");
            return GenerateFallbackBox(buildingData);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // --- 積み上げ ---
        // 各パーツのY位置は floors からの絶対計算ではなく、実際に描画したパーツの累積高さで決める。
        // これにより base/mid が存在しない建物でも top が浮かず接地する。
        float stackY = 0f;

        Vector2[] footprint = (buildingData.polygonArray != null && buildingData.polygonArray.Length >= 3)
            ? BuildingMeshUtils.GetFootprint(buildingData)
            : null;

        Quaternion? rotation = null;
        if (footprint != null && footprint.Length >= 2)
        {
            Vector2 firstEdge = (footprint[1] - footprint[0]).normalized;
            float edgeAngle = Mathf.Atan2(firstEdge.y, firstEdge.x) * Mathf.Rad2Deg;
            rotation = Quaternion.Euler(0, -edgeAngle + 90f, 0);
        }

        if (baseModule != null)
        {
            // モデルの原点を地面（または前段の天面）の高さに揃えて積み上げるため、alignByOrigin: true を指定する。
            BuildingMeshUtils.AddStackedModule(baseModule, stackY, floorHeight, combineInstances, materials, temporaryMeshes, rotation: rotation, alignByOrigin: true);
            stackY += floorHeight;
        }

        if (middleModule != null)
        {
            // base と top はそれぞれ1階分を消費し、mid は残りの階数分を埋める
            int midCount = Mathf.Max(0, floors - (baseModule != null ? 1 : 0) - (topModule != null ? 1 : 0));
            for (int i = 0; i < midCount; i++)
            {
                // モデルの原点を前段の天面の高さに揃えて積み上げるため、alignByOrigin: true を指定する。
                BuildingMeshUtils.AddStackedModule(middleModule, stackY, floorHeight, combineInstances, materials, temporaryMeshes, rotation: rotation, alignByOrigin: true);
                stackY += floorHeight;
            }
        }

        // --- top(実寸・スケールなし) ---
        float topHeight = 0f;
        if (topModule != null)
        {
            // モデルの原点を積み上げ天面（base/mid がない場合は地面）の高さに揃えて配置するため、alignByOrigin: true を指定する。
            topHeight = BuildingMeshUtils.AddStackedModule(topModule, stackY, null, combineInstances, materials, temporaryMeshes, rotation: rotation, alignByOrigin: true);
        }

        // --- 敷地の装飾（フェンス・タイル・地面パネル）---
        // モジュラー建物と共通の処理で、土地区画の外周にフェンス、敷地内にタイル・地面パネルを配置する。
        // フェンス・タイルは a{variant}（またはフォールバック先 b{bd_type}）に該当パーツがある場合のみ描画。
        // 地面パネルは master の draw_floor=yes の建物だけ描画し、マテリアルは floor パーツを
        // a{variant}→b{bd_type}→common でフォールバック解決する（floorMaterial が非nullのときだけ描く）。
        // prebuilt は建物中心位置の地形標高に接地する（傾斜地対応の対象外）ため、
        // 超近景（isDynamic）ではフェンス・タイル・地面パネルを地形に追従させる。
        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(
            buildingData, useParcelAltitude: false, out _, out Vector3 centerPointWorld);
        Vector2[] sitePolygon = (fenceFootprint != null && fenceFootprint.Length >= 3) ? fenceFootprint : footprint;
        Material floorMaterial = BuildingMeshUtils.ResolveFloorMaterialFromMaster(buildingData, glbPrefix);
        ModularBodyBuilder.GenerateSiteDecorations(
            glbPrefix, 0,
            fenceFootprint, sitePolygon, sitePolygon,
            floorMaterial,
            centerPointWorld, deformToTerrain: isDynamic,
            combineInstances, materials, temporaryMeshes);

        if (combineInstances.Count == 0)
        {
            CleanupMeshes(temporaryMeshes);
            return GenerateFallbackBox(buildingData);
        }

        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateBounds();
        finalMesh.RecalculateNormals();

        CleanupMeshes(temporaryMeshes);

        // コライダーは描画された実高さに合わせる。top が論理高さ(floors × 階高)を超えて
        // はみ出す場合(風力発電所など)でもクリックできるようにするため。
        Vector2[] colliderFootprint = footprint;
        float colliderHeight = Mathf.Max(stackY + topHeight, floorHeight);
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(colliderFootprint, colliderHeight);

        return new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh);
    }

    /// <summary>
    /// パーツが1つも取得できなかった場合の白い箱フォールバック。
    /// </summary>
    private static BuildingMeshResult GenerateFallbackBox(BuildingSummary buildingData)
    {
        if (buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"PreBuiltBuildingMeshBuilder: polygon が不正なため空メッシュを返します (agent_id={buildingData.agent_id})。");
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
