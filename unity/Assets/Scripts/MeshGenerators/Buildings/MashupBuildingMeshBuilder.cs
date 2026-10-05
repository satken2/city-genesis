using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// マッシュアップ建物（MODEL_MASHUP）のメッシュ生成器。
/// 学校（校舎＋体育館）や変電所（管理棟＋変圧器）のような「1施設＝複数棟」の建物を、
/// 土地区画を8m×8mセルに分割した配置計画（BuildingSummary.sub_buildings）に従って組み立てる。
/// セル分割と棟の割り付けはバックエンドが解決済みで、Unity側には各サブ建物の
/// 配置座標（center_point基準）・回転・階数だけが届く（セル分割ロジックは持たない）。
///
/// 各サブ建物は base / mid / top のパーツを下から積み上げて組み立てる:
/// - mashup-{model_prefix}-base-prebuilt … 1階部分
/// - mashup-{model_prefix}-mid-prebuilt  … 繰り返しフロア1階分（floors に応じて枚数が変わる）
/// - mashup-{model_prefix}-top-prebuilt  … 頂部。完成済みモデル丸ごとでも可
/// 全パーツをXYZいずれもスケールせず著作実寸のまま描画し、積み上げのY位置も著作高さの
/// 累積で決める（prebuilt と違い mid の階高Yスケール・階高グリッドは使わない）。
/// 階高 height_per_floor はパーツが1つも見つからない棟の白い箱の高さにだけ使う。
/// パーツは prebuilt と異なり外観バリアントの閾値フォルダから解決する（パーツごとの独立スキャン）。
/// 塔屋・エントランスは配置しない。
///
/// 接地は既成建物と同じく建物中心位置の地形標高（傾斜地対応 IsApplicableModelType の対象外・
/// コンクリート基盤なし）。ただし区画が広く斜面にまたがることがあるため、各サブ建物のY位置は
/// 自分の配置座標の地形標高（中心標高との差分）へ常時合わせる。
///
/// 敷地の装飾はフェンス（fence-w / fence-c）と地面パネル（floor、master の draw_floor=yes のみ）を
/// 土地区画ポリゴンに沿って描画する。タイル（tile）は敷かない。
/// コライダーは土地区画ポリゴンを描画された最大高さまで押し出す
/// （区画のどこをクリックしても建物を選択できるようにするため）。
///
/// 露天掘り（MODEL_OPEN_PIT）も同じ組み立てで、設備は前庭に並んだサブ建物として届く。
/// 区画の奥はバックエンドが地形そのものを掘り下げた穴なので、区画を押し出した形
/// （コライダー・白い箱）で穴を塞がず、地形に沿う区画の面と設備の箱をコライダーにする。
///
/// 遠景（Far）は <see cref="FarBuildingMeshBuilder"/> が棟ごとの遠景モデルを同じ配置で置く。
/// </summary>
public class MashupBuildingMeshBuilder : IBuildingMeshGenerator
{
    private readonly bool openPit;

    /// <param name="openPit">露天掘り（MODEL_OPEN_PIT）として生成する。</param>
    public MashupBuildingMeshBuilder(bool openPit = false)
    {
        this.openPit = openPit;
    }

    /// <summary>
    /// BuildingSummary からマッシュアップ建物メッシュを生成する。
    /// isDynamic が true の場合、フェンス・地面パネルを地形に追従させる。
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
        int variantNumber = BuildingMeshUtils.ComputeVariantNumber(buildingData.visual_variant);

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // 土地区画ポリゴン（center_point基準ローカルXZ）と配置基準のワールド座標（中心位置の地形標高）。
        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(
            buildingData, out _, out Vector3 centerPointWorld);

        // --- サブ建物の積み上げ ---
        // maxTopY は描画された最も高いサブ建物の天端（ローカルY）。コライダーの高さに使う。
        // facilityBounds は露天掘りのコライダーに使う棟ごとの外接箱（建物ローカル座標）。
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

        // --- 敷地の装飾（フェンス・地面パネル）---
        // モジュラー・既成建物と共通の処理。tilePolygon に null を渡してタイルは敷かない。
        // 中心位置の地形標高に接地するため、超近景（isDynamic）では地形に追従させる。
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
            // サブ建物が未登録で敷地装飾パーツも無い場合は、建物底面の白い箱で存在だけ示す。
            CleanupMeshes(temporaryMeshes);
            return GenerateFallbackBox(buildingData);
        }

        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateBounds();
        finalMesh.RecalculateNormals();

        CleanupMeshes(temporaryMeshes);

        // コライダーは土地区画全体（取得できなければ建物底面）を描画された最大高さまで押し出す。
        // 露天掘りは建物の原点が穴の底にあり押し出すと前庭に届かないので、地形に沿う面と設備の箱にする。
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

        // セル数×パーツ数でサブメッシュが増えるため、同一マテリアルを統合してドローコールを抑える。
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
    /// サブ建物1棟を積み上げて CombineInstance リストへ追加する。
    /// </summary>
    /// <returns>この棟の天端のローカルY（コライダー高さの算出に使う）。</returns>
    private static float GenerateSubBuilding(
        SubBuildingPlacement placement, string glbPrefix, int variantNumber, float floorHeight,
        Vector3 centerPointWorld,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        // パーツはパーツごとに閾値フォルダを独立スキャンして解決する（common へのフォールバックはしない）。
        GameObject baseModule = BuildingMeshUtils.GetPartBySuffixScan(glbPrefix, $"mashup-{placement.model_prefix}-base-prebuilt", variantNumber);
        GameObject middleModule = BuildingMeshUtils.GetPartBySuffixScan(glbPrefix, $"mashup-{placement.model_prefix}-mid-prebuilt", variantNumber);
        GameObject topModule = BuildingMeshUtils.GetPartBySuffixScan(glbPrefix, $"mashup-{placement.model_prefix}-top-prebuilt", variantNumber);

        Vector2 xzOffset = new Vector2(placement.position[0], placement.position[1]);
        int floors = Mathf.Max(1, placement.floors);
        // パーツが1つも見つからない棟の白い箱の高さに使う。
        float subFloorHeight = (placement.height_per_floor.HasValue && placement.height_per_floor.Value > 0f)
            ? placement.height_per_floor.Value
            : floorHeight;

        // rotation は壁・エントランスと同じ回転規約（モデルのX軸を辺方向に向ける）で適用する。
        // これによりモデルのX方向（Blenderでの幅方向）がマスター width のセル確保方向
        // （接道辺方向）に沿い、壁の外面にあたる正面が接道側を向く。
        Quaternion rotation = Quaternion.Euler(0, -placement.rotation + 180f, 0);

        // 区画が斜面にまたがっても棟が浮かない・めり込まないよう、各棟のY基準は
        // 自分の配置座標の地形標高とする（建物GameObjectは中心標高に置かれるため差分にする）。
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

        // 全パーツを実寸のまま（fitHeight=null）積み、Y位置は著作高さの累積で決める
        // （前段の実寸天端が次段の底面になるため隙間・埋もれは生じない）。
        // 段を進めるのは存在するパーツだけなので、base/mid が無い棟では top が接地する
        // （完成済みモデルを top だけで表現できる）。
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
            // base と top はそれぞれ1階分を消費し、mid は残りの階数分を埋める
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
    /// パーツが1つも見つからないサブ建物の代替として、1セル分（MASHUP_CELL_SIZE四方）の
    /// 白い箱を配置座標に置く。マテリアルは null を積み、EnsureMaterials が既定マテリアルで補完する。
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

        // AddStackedModule と同じ変換順（回転→平行移動）でセル矩形を建物ローカル座標へ移す
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
    /// temporaryMeshes の firstMesh 以降（1棟分のパーツ。配置変換済み）の外接箱を返す。
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
    /// 露天掘りのコライダー。区画を地形に沿って覆う面（穴の底・段・前庭のどこでもクリックできる）と、
    /// 設備ごとの外接箱（横から見た設備もクリックできる）を結合する。
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
    /// サブ建物も敷地装飾も1つも生成できなかった場合の白い箱フォールバック。
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
