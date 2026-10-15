using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;
using NetTopologySuite.Geometries;

/// <summary>
/// 港（MODEL_PORT）のメッシュ生成器。
///
/// 海岸土地区画（PARCEL_SHORE）に立地する港に適用する。水際（船が着く線）と、その陸側のヤード（荷役の地面）を決め、
/// 水際に荷役クレーン（port_crane）、区画の辺0（道路側）に施設モジュール（port_building。ゲートと建物）、
/// その間の空きに港の種類ごとのヤードの荷（段積みするコンテナ port_container、または地面に1個ずつ置く荷 port_cargo）を置く。
/// 配置は <see cref="PortLayout"/> が決める。
///
/// 水際とヤードの決め方は2通りで、そこから先の配置の規則は同じ。
/// - 区画が埋立地の岸壁の陸側にある（<see cref="FindFacingReclaimedQuays"/>。埋立地の上に引いた港）: 水際は埋立地の岸壁
///   （<see cref="ReclaimedLandManager"/> が天端と垂直の面を描く）に任せてスラブを作らない。水際=面している岸壁区間、
///   ヤード=区画の陸の部分。
/// - それ以外（天然の海岸）: バックエンドは海岸区画を海岸線でクリップせず元の形状のまま保持する（B_土地形状 B.5）ので、
///   区画の中の水際の両端を結ぶ弦を左右とも <see cref="QUAY_PROTRUSION"/> だけ海側へ平行に移した直線で底面をカットし、
///   厚みのある板（岸壁スラブ）を押し出す。水際=カット直線上の辺、ヤード=スラブの範囲。
///
/// visual_variant(1) は他の建物と同じく外観バリアントの閾値フォルダ（F_建物メッシュの生成）、visual_variant2 はヤードの荷の
/// 配置の乱数の種に使う。接写は `_close` のパーツで同じ配置を描く。遠景は他の建物と同じ遠景モデルか箱で、この生成器は通らない。
///
/// 全体像は city-genesis/docs/docs/geometries/F4_港湾岸壁のメッシュ生成.md を参照。
/// </summary>
public class PortBuildingMeshBuilder : IBuildingMeshGenerator
{
    /// <summary>岸壁上面の海面からの高さ(m)。施設モジュールもこの高さより下には置かない。</summary>
    public const float QUAY_TOP_HEIGHT = 2f;

    /// <summary>天然の海岸の岸壁が、水際の両端（海岸線の上）から海へ張り出す距離(m)。</summary>
    public const float QUAY_PROTRUSION = 20f;

    /// <summary>海側エッジ上とみなす距離の許容差(m)。カット直線と岸壁フットプリントの辺の照合に使う。</summary>
    private const float SEAWARD_EDGE_EPSILON = 0.05f;

    /// <summary>
    /// 区画を膨らませて埋立地の岸壁区間を拾う距離(m)。区画の辺に重なる岸壁が数値誤差で区画の外へ出ても取りこぼさないため。
    /// </summary>
    private const float QUAY_FACE_TOLERANCE = 0.1f;

    /// <summary>
    /// 面しているとみなす岸壁の最短の長さ(m)。角や頂点が岸壁に触れるだけの点接触でも、斜めに交わると許容差の範囲を
    /// 横切る短い区間が残る(交角10°でも1m未満)ので、これ以下は面していないとみなす。
    /// </summary>
    private const float QUAY_FACE_MIN_LENGTH = 1f;

    /// <summary>
    /// 区画が岸壁の陸側にあるとみなす、陸側の帯（岸壁区間から内向きに <see cref="Constants.QUAY_PROBE_DISTANCE"/> の幅）を
    /// 区画が覆う割合の下限。辺が岸壁に重なる区画は、陸側なら許容差ぶんずれても8割以上、海側なら2割以下しか覆わない。
    /// </summary>
    private const double QUAY_LAND_SIDE_MIN_COVER = 0.5;

    /// <summary>区画の陸の部分とみなす最小の面積(m²)。これ未満なら区画は海岸線に外から接しているだけとみなす。</summary>
    private const double MIN_LAND_PART_AREA = 1.0;

    /// <summary>岸壁の両端の候補の周りで、区画の中に陸と海の両方があるかを見る円の半径(m)。</summary>
    private const double QUAY_END_PROBE_RADIUS = 0.5;

    /// <summary>
    /// 岸壁の両端の候補とみなすのに、円の中の区画の部分に陸と海がそれぞれ占める最小の割合。
    /// 隣の埋立地が区画にごくわずかに重なった細い陸（発注検証が許す 0.01m² 以内）を水際と取り違えないため。
    /// </summary>
    private const double QUAY_END_MIN_SHARE = 0.01;

    /// <summary>底面外周の上にあるとみなす、区画の陸の部分の頂点の距離(m)。</summary>
    private const double QUAY_END_OUTLINE_TOLERANCE = 0.01;

    /// <summary>ヤードの荷の種類違いのパーツ（port_cargo1, 2, … / port_container1, 2, …）を探す上限。</summary>
    private const int MAX_CARGO_PARTS = 16;

    /// <summary>
    /// 岸壁の海側境界（直線カットで生じたエッジ）を表す。水際の列挙に使う。
    /// カットできなかった建物では <see cref="Valid"/> が false。座標・方向はすべて center_point 基準のローカルXZ。
    /// </summary>
    private struct QuaySeawardEdge
    {
        public bool Valid;
        /// <summary>カット直線上の基準点（突き出し後の左端 A'）。</summary>
        public Vector2 Origin;
        /// <summary>カット直線の向き（A'→B'。海を左手に見る）。</summary>
        public Vector2 Dir;
        /// <summary>カット直線に垂直な、海を向く単位法線。</summary>
        public Vector2 Normal;
    }

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null || buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"[PortBMB] 底面ポリゴンが不正です (Building ID: {buildingData?.agent_id})。白い箱にフォールバックします。");
            return BuildingMeshBuilder.GenerateSimpleBuilding(null, 1, LocalConstants.FLOOR_HEIGHT);
        }

        // 底面（CCW・辺0が道路側、center_point 相対のローカル座標）
        Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
        string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
        int variantNumber = BuildingMeshUtils.ComputeVariantNumber(buildingData.visual_variant);

        // center_point のワールド座標（バックエンド座標）と地形高さ。
        // ModularBuildingMeshBuilder と同じ取得経路（Utils.GetGroundHeight）を使い、
        // BuildingManager が GameObject を配置する高さと基準を揃える。
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

        // 1. 水際・ヤード・地面の高さ（ヤードの地面は下のクレーン・施設・荷の接地に使う）。
        Vector2[] yard;
        List<PortLayout.Waterline> waterlines;
        float yardFloorLocalY;
        float colliderHeight;
        List<ReclaimedQuay> facingQuays = CollectFacingReclaimedQuays(buildingData, centerPointWorld);
        if (facingQuays.Count > 0)
        {
            // 1'. 埋立地の岸壁に面する港: スラブを作らず、面している岸壁区間を水際、区画の陸の部分をヤードにする。
            waterlines = FacingWaterlines(facingQuays, centerPointWorld, out float maxTopLocalY);
            yard = ExtractLandPart(footprint, centerPointWorld) ?? footprint;
            yardFloorLocalY = float.NegativeInfinity;
            colliderHeight = Mathf.Max(0.1f, maxTopLocalY);
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[PortBMB] agent={buildingData.agent_id} 埋立地の岸壁 {facingQuays.Count} 区間に面するためスラブを省略");
        }
        else
        {
            // 天然の海岸: 区画の水際の両端から弦を求め、左右とも一定距離だけ海側の直線でカットした岸壁フットプリントを得る。
            Vector2[] quayFootprint = BuildQuayFootprint(footprint, centerPointWorld, out QuaySeawardEdge seaward);

            // スラブ上面のワールドY = 海面(0m) + 2m 固定。GameObject は center_point 位置の地形高さに
            // 配置されるため、ローカルYは centerPointWorld.y を差し引く。
            float topLocalY = QUAY_TOP_HEIGHT - centerPointWorld.y;

            // スラブメッシュ（上面=topLocalY、下面=各頂点の地形=海底高さ）。subMesh0=上面、subMesh1=側面+下面。
            Mesh slabMesh = BuildSlabMesh(quayFootprint, topLocalY, centerPointWorld);
            if (slabMesh == null)
            {
                return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
            }

            // 上面=floor（ヤードの地面パネルと同一マテリアルで床続きにする）、側面/下面=ceil（岸壁の壁面）。
            // 地面パネルが無効(draw_floor未設定等)の場合は floor パーツ直参照、それも無ければデフォルトマテリアル。
            Material topMaterial = floorMaterial != null ? floorMaterial : ResolvePartMaterial(prefixes, "floor", variantNumber);
            Material sideMaterial = ResolvePartMaterial(prefixes, "ceil", variantNumber);
            AppendMesh(slabMesh, new[] { topMaterial, sideMaterial }, combineInstances, materials);
            temporaryMeshes.Add(slabMesh);

            waterlines = EnumerateSeawardWaterlines(quayFootprint, seaward, topLocalY);
            yard = quayFootprint;
            yardFloorLocalY = topLocalY;
            // クレーンのブームは海側へ張り出すがクリック対象には含めない（工事中のクレーンと同じ割り切り）。
            colliderHeight = Mathf.Max(0.1f, topLocalY);
        }

        // ヤードの地面のローカルY。天然の海岸はスラブがヤードを覆うので max(地形, 上面)。
        float Ground(Vector2 local)
        {
            float terrain = Utils.GetGroundHeight(new Vector3(local.x + centerPointWorld.x, 1000f, local.y + centerPointWorld.z)) - centerPointWorld.y;
            return Mathf.Max(terrain, yardFloorLocalY);
        }

        // 2. 配置計画。寸法は描画LODによらず通常モデルで測り、接写でも同じ位置に置く。
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

        // 3. 施設モジュール（ゲートと建物）。区画の辺0の中点の地面（海面+2m 以上）に接地する。
        if (plan.Module != null)
        {
            float y = Mathf.Max(Ground(plan.Module.Gate), QUAY_TOP_HEIGHT - centerPointWorld.y);
            AppendModel(DrawPart(prefix, "port_building", variantNumber),
                Matrix4x4.TRS(new Vector3(plan.Module.Position.x, y, plan.Module.Position.y), plan.Module.Rotation, Vector3.one),
                combineInstances, materials, temporaryMeshes);
        }

        // 4. 荷役クレーン。原点が水際の線上・上面高さ、ローカル+Z が海を向く。スケールしない（実寸）。
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

        // 5. 塔屋（ヤードの頂点の平均の位置に水際に沿わせて1つ）。
        if (roofpropRect != null)
        {
            AppendModel(DrawPart(prefix, "roofprop", variantNumber),
                Matrix4x4.TRS(new Vector3(roofpropAt.x, Ground(roofpropAt), roofpropAt.y), roofpropRotation, Vector3.one),
                combineInstances, materials, temporaryMeshes);
        }

        // 6. ヤードの荷。コンテナはパーツを段に積み、地面に置く荷は描画LODのパーツ（接写は `_close` 優先）を置き場ごとに1個置く。
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

        // 7. 敷地の装飾（フェンス・タイル・地面パネル）。モジュラー/prebuilt と共通の処理で、
        //    地面パネルは draw_floor=yes のときヤードに描き、岸壁上面と同一の floor マテリアルで床続きになる。
        //    区画の海の部分（岸壁より先）には描かない。ヤードは海岸線をまたいで高さが変わる（埋立地の天端と元の陸、
        //    スラブの下の浜）ので、距離によらず地形に追従させる。
        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(buildingData, out _, out _);
        ModularBodyBuilder.GenerateSiteDecorations(
            prefix, 0,
            fenceFootprint, yard, yard,
            floorMaterial,
            centerPointWorld, deformToTerrain: true,
            combineInstances, materials, temporaryMeshes);

        // 埋立地の岸壁に面する港はスラブが無いので、パーツが1つも解決できないと空のメッシュになる。
        if (combineInstances.Count == 0)
        {
            foreach (Mesh temp in temporaryMeshes)
            {
                Object.Destroy(temp);
            }
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
        }

        // 8. メッシュ結合。
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // 9. コライダー（ヤードを上面高さまで押し出し）。
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(yard, colliderHeight);

        var result = new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    /// <summary>
    /// 施設モジュール（port_building）の通常モデル。配置と間口は描画LODによらずこのモデルの寸法とマーカーで決める
    /// （<see cref="BuildingRoadAccess"/> が間口の索引を作るときにも使う）。無ければ null。
    /// </summary>
    public static GameObject ResolveModulePart(BuildingSummary buildingData)
    {
        string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
        return LayoutPart(prefix, "port_building", BuildingMeshUtils.ComputeVariantNumber(buildingData.visual_variant));
    }

    /// <summary>
    /// 船が横付けする水際を、メッシュ生成と同じ規則で求めてワールド座標で返す（P・Q はワールドXZ、Y は岸壁の天端の標高）。
    /// 建物の GameObject やセルの常駐によらず、建物データ・海岸線・埋立地のストアだけで決まる（入港する船の接岸位置に使う）。
    /// 水際が定まらなければ空。
    /// </summary>
    public static List<PortLayout.Waterline> ResolveWorldWaterlines(BuildingSummary buildingData)
    {
        var result = new List<PortLayout.Waterline>();
        if (buildingData?.polygonArray == null || buildingData.polygonArray.Length < 3 ||
            buildingData.centerPointArray == null || buildingData.centerPointArray.Length < 2)
        {
            return result;
        }
        // 高さ0の中心で求めると、ローカルの Y がそのままワールドの標高になる。
        var centerPointWorld = new Vector3(buildingData.centerPointArray[0], 0f, buildingData.centerPointArray[1]);
        List<ReclaimedQuay> facingQuays = CollectFacingReclaimedQuays(buildingData, centerPointWorld);
        List<PortLayout.Waterline> local;
        if (facingQuays.Count > 0)
        {
            local = FacingWaterlines(facingQuays, centerPointWorld, out _);
        }
        else
        {
            Vector2[] quayFootprint = BuildQuayFootprint(BuildingMeshUtils.GetFootprint(buildingData), centerPointWorld, out QuaySeawardEdge seaward);
            local = EnumerateSeawardWaterlines(quayFootprint, seaward, QUAY_TOP_HEIGHT);
        }
        var origin = new Vector2(centerPointWorld.x, centerPointWorld.z);
        foreach (PortLayout.Waterline waterline in local)
        {
            result.Add(new PortLayout.Waterline { P = waterline.P + origin, Q = waterline.Q + origin, Normal = waterline.Normal, Y = waterline.Y });
        }
        return result;
    }

    /// <summary>配置の寸法に使う通常モデル（無印）を閾値フォルダの走査で解決する。描画LODの `_close` は見ない。</summary>
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

    /// <summary>描画に使うパーツ（接写なら `_close` 優先）を閾値フォルダの走査で解決する。</summary>
    private static GameObject DrawPart(string prefix, string partName, int variantNumber)
    {
        return BuildingMeshUtils.GetPartBySuffixScan(prefix, partName, variantNumber);
    }

    /// <summary>
    /// 港のパーツを探すフォルダの順（a{variant} → b{bd_type}）。岸壁のマテリアルのように
    /// 港の種類をまたいで共有するパーツは b{bd_type} に置き、a{variant} に同名があればそちらを使う。
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
                // マスターに無いバリアントは建物データの bd_type を使う。
            }
        }
        string bPrefix = $"b{bdType}";
        return bPrefix == prefix ? new[] { prefix } : new[] { prefix, bPrefix };
    }

    /// <summary>マテリアル参照専用のパーツ（floor・ceil）の最初のマテリアルを prefixes の順に探す。無ければ既定の建物マテリアル。</summary>
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
    /// ヤードの荷の種類違いのパーツの列。フォルダ（a{variant} → b{bd_type}）の順に、地面に置く荷 port_cargo1, 2, … →
    /// 段積みするコンテナ port_container1, 2, … を1から連番で探し、最初に見つかった1組だけを使う（stacked はコンテナの組か）。
    /// 港の種類ごとに荷が違う（バルク港は野積みの山、タンカー埠頭は油槽）ので、荷は各港の a{variant} に置く。
    /// 有無は通常モデルで判定する（接写でも同じ配置にするため）。
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

    /// <summary>地面に置く荷の通常モデルの外接箱の和（置き場の大きさを決める）。</summary>
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
    /// 地面に置く荷を置き場ごとに1個置く。種類は <see cref="PortLayout.CargoKind"/>（ブロックの1列で同じ）で選び、
    /// 底は置き場の4隅の地面の最も低い高さ。パーツは原点が底面の中心で、ローカル+Z を水際に沿わせる。
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
    /// コンテナの山をパーツの段積みで描く。段 t の色は <see cref="PortLayout.TierColor"/> で選び、山の底は4隅の地面の最も低い高さ
    /// （斜面で浮かないよう高い側を地面へ少し沈める）。パーツは原点が底面の中心で、ローカル+Z を長手に揃えて置く。
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

    /// <summary>山の底のローカルY。長さ length・幅 width の置き場の4隅の地面の最も低い高さ。</summary>
    private static float StackBase(PortLayout.Stack stack, float length, float width, System.Func<Vector2, float> ground)
    {
        Vector2 u = stack.Along * (length * 0.5f);
        Vector2 v = new Vector2(-stack.Along.y, stack.Along.x) * (width * 0.5f);
        return Mathf.Min(Mathf.Min(ground(stack.Center - u - v), ground(stack.Center + u - v)),
            Mathf.Min(ground(stack.Center + u + v), ground(stack.Center - u + v)));
    }

    /// <summary>プレハブ配下の全メッシュを1つに結合して返す（一時メッシュとして登録する）。パーツが無ければ null。</summary>
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

    /// <summary>パーツを変換 matrix で1つ置く。パーツが無ければ何もしない。</summary>
    private static void AppendModel(GameObject part, Matrix4x4 matrix,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        Mesh mesh = CombinePart(part, out Material[] partMaterials, temporaryMeshes);
        if (mesh != null)
        {
            AppendInstance(mesh, partMaterials, matrix, combineInstances, materials);
        }
    }

    /// <summary>メッシュの各サブメッシュを変換 matrix 付きで CombineInstance リストへ積む。</summary>
    private static void AppendInstance(Mesh mesh, Material[] meshMaterials, Matrix4x4 matrix,
        List<CombineInstance> combineInstances, List<Material> materials)
    {
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            combineInstances.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = matrix });
            materials.Add(meshMaterials != null && meshMaterials.Length > 0 ? meshMaterials[Mathf.Min(s, meshMaterials.Length - 1)] : null);
        }
    }

    /// <summary>面している岸壁区間を水際（ローカル座標・区間ごとの外向き法線・その埋立地の天端）にする。</summary>
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
    /// ストアから区画付近の岸壁区間を引き、区画が面しているものを <see cref="FindFacingReclaimedQuays"/> で選ぶ。
    /// 底面は SHAPE_FULL なので区画ポリゴンと同じ形で、一括ロードで parcel_id が無くても使える。
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
        float margin = QUAY_FACE_TOLERANCE + SEAWARD_EDGE_EPSILON;
        ReclaimedLandStore.GetQuaySegments(Rect.MinMaxRect(minX - margin, minZ - margin, maxX + margin, maxZ + margin), candidates);
        return candidates.Count == 0 ? candidates : FindFacingReclaimedQuays(parcelWorld, candidates);
    }

    /// <summary>
    /// 区画（ワールドXZのポリゴン）が面している埋立地の岸壁区間を、区画に掛かる部分に切り詰めて返す。面していなければ空。
    ///
    /// 区画を <see cref="QUAY_FACE_TOLERANCE"/> 膨らませた範囲と岸壁区間の交わりをとり、範囲の縁で途切れた端は
    /// 区画の外へ張り出した許容差ぶんを削る。残りが <see cref="QUAY_FACE_MIN_LENGTH"/> より長く、かつ区画がその岸壁の
    /// 陸側（外向き法線の逆側。そこは埋立地の中）にある区間があれば「面している」。陸側かどうかは、残りの区間から内向きに
    /// <see cref="Constants.QUAY_PROBE_DISTANCE"/> の帯を区画が <see cref="QUAY_LAND_SIDE_MIN_COVER"/> 以上覆うかで決める。
    /// 区画を横切る岸壁と区画の海側の辺に重なる岸壁（埋立地の上に引いた港）は拾い、区画の辺に海側から重なる岸壁
    /// （既存の港の隣の埋立。岸壁の外向き法線が区画の水面を向く）と、角や頂点で点接触するだけの岸壁は拾わない。
    /// 検証ハーネス（Tests/Quay）がリフレクションで呼ぶので、名前と引数を変えないこと。
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
            Geometry area = parcel.Buffer(QUAY_FACE_TOLERANCE);
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
                    // 区間の端点そのものではない端は範囲の縁（区画の外 QUAY_FACE_TOLERANCE）なので、その分を削る。
                    if (t0 * length > 1e-4)
                    {
                        t0 += QUAY_FACE_TOLERANCE / length;
                    }
                    if ((1.0 - t1) * length > 1e-4)
                    {
                        t1 -= QUAY_FACE_TOLERANCE / length;
                    }
                    if ((t1 - t0) * length <= QUAY_FACE_MIN_LENGTH)
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
    /// 区画が岸壁区間 p-q の陸側にあるか。区間から外向き法線の逆へ <see cref="Constants.QUAY_PROBE_DISTANCE"/> の帯
    /// （埋立地の中）を区画が <see cref="QUAY_LAND_SIDE_MIN_COVER"/> 以上覆えば陸側とする。
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
        return stripArea > 0.0 && parcel.Intersection(strip).Area >= QUAY_LAND_SIDE_MIN_COVER * stripArea;
    }

    /// <summary>Geometry（LineString / MultiLineString / Collection）から LineString を再帰的に収集する。点は長さが無いので捨てる。</summary>
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
    /// 底面（center_point 基準のローカル座標）と陸地の交わりのうち最大の多角形を、ローカル座標の CCW リングで返す。
    /// 海岸線が無い・交わりが無い・幾何演算に失敗したときは null。
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

    /// <summary>頂点の単純平均。塔屋の置き場所の基準（面積重心ではない）。</summary>
    private static Vector2 VertexCentroid(Vector2[] polygon)
    {
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < polygon.Length; i++)
        {
            centroid += polygon[i];
        }
        return centroid / polygon.Length;
    }

    /// <summary>ローカル座標の多角形を offset だけ平行移動し、始点の複製で閉じた NTS の座標列にする。</summary>
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

    /// <summary>底面（center_point 基準のローカル座標）をワールド座標の NTS ポリゴンにする。自己交差などで無効なら buffer(0) で直す。</summary>
    private static Geometry ToWorldPolygon(GeometryFactory gf, Vector2[] footprint, Vector3 centerPointWorld)
    {
        Geometry polygon = gf.CreatePolygon(ToClosedCoordinates(footprint, new Vector2(centerPointWorld.x, centerPointWorld.z)));
        return polygon.IsValid ? polygon : polygon.Buffer(0);
    }

    /// <summary>Geometry の直下の要素のうち面積最大の Polygon。無ければ null。</summary>
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
    /// NTS の Polygon の外周を center_point 基準のローカル座標の CCW リングにする。閉路の重複点と 1cm 未満の近接重複点は
    /// 三角形分割の縮退を避けるため取り除き、3点未満なら null。
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

    // 海岸線リングを偶奇合成した陸地領域（NTS Geometry、ワールド座標）のキャッシュ。
    // 海岸線は埋立の完了で広がり、取り直すたびに CoastlineStore.Version が進むので、版が変わったときだけ作り直す。
    private static Geometry cachedLandGeometry;
    private static int cachedCoastlineVersion = -1;

    /// <summary>
    /// 海岸線の全リングから陸地領域の NTS ジオメトリを構築して返す（キャッシュ付き）。
    /// リングは外周と穴（湖）を区別せず保持されているため、偶奇合成（XOR）で陸地領域になる
    /// （<see cref="CoastlineStore.IsLand"/> の even-odd 判定と同じ解釈）。
    /// 陸地が存在しない場合は null を返す。
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
            // NTS の LinearRing は始点と終点の厳密一致を要求するため、
            // 終端が始点と（ほぼ）同じでも必ず始点の複製で閉じる。
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
    /// 底面ポリゴン（center_point 相対のローカル座標）から岸壁フットプリントを生成する。
    ///
    /// 区画の水際が底面外周に届く点（<see cref="CollectQuayEndCandidates"/>）のうち最も離れた2点を岸壁の左端A・右端Bとし、
    /// A・B をそれぞれ海側へ <see cref="QUAY_PROTRUSION"/> だけ突き出した2点を結ぶ直線で底面をカットして陸側を残す。
    /// 海側境界は海岸線をなぞらず、常に弦A-Bと平行な1本の直線になる（左右の張り出しは等しい）。
    /// 突き出しは弦から底面の海側最遠頂点までの距離（奥行き上限）で頭打ちにし、岸壁は常に土地区画の範囲内に収まる。
    /// 海岸線未ロード・水際の端が2つ未満・海側頂点なし・幾何演算失敗の場合は元の底面をそのまま返す。
    /// 検証ハーネス（Tests/Quay）が <see cref="CoastlineStore"/> に海岸線を入れてリフレクションで呼ぶので、名前と引数を変えないこと。
    /// </summary>
    /// <param name="seaward">海側境界（直線カットで生じたエッジ）。水際の列挙に使う。カット不能時は Valid=false。</param>
    private static Vector2[] BuildQuayFootprint(Vector2[] footprint, Vector3 centerPointWorld, out QuaySeawardEdge seaward)
    {
        seaward = default;
        int n = footprint.Length;

        if (!CoastlineStore.IsLoaded || !CoastlineStore.HasCoastline)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[PortBMB] 海岸線未ロード (IsLoaded={CoastlineStore.IsLoaded})。カットせず元形状を使用");
            return (Vector2[])footprint.Clone();
        }

        Geometry land = GetLandGeometry();
        if (land == null || land.IsEmpty)
        {
            return (Vector2[])footprint.Clone();
        }

        try
        {
            var gf = new GeometryFactory();
            Geometry footprintPoly = ToWorldPolygon(gf, footprint, centerPointWorld);

            // 区画の水際が底面外周に届く点を列挙し、最も離れた2点を左端A・右端Bとする。
            var hits = new List<Vector2>();
            CollectQuayEndCandidates(gf, footprintPoly, land, hits);
            if (hits.Count < 2)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                    $"[PortBMB] 水際の端が{hits.Count}点のためカットせず元形状を使用（全域が陸または海）");
                return (Vector2[])footprint.Clone();
            }
            Vector2 a = Vector2.zero, b = Vector2.zero;
            float bestSq = -1f;
            for (int i = 0; i < hits.Count; i++)
            {
                for (int j = i + 1; j < hits.Count; j++)
                {
                    float sq = (hits[i] - hits[j]).sqrMagnitude;
                    if (sq > bestSq)
                    {
                        bestSq = sq;
                        a = hits[i];
                        b = hits[j];
                    }
                }
            }
            if (bestSq < 1f)
            {
                return (Vector2[])footprint.Clone();
            }

            // 海側方向の決定: 陸地に含まれない底面頂点が、弦A-Bに対して平均的に存在する側を海側とする。
            Vector2 u = (b - a).normalized;
            Vector2 perp = new Vector2(-u.y, u.x);
            float sideSum = 0f;
            var seaOffsets = new List<float>();
            for (int i = 0; i < n; i++)
            {
                float wx = footprint[i].x + centerPointWorld.x;
                float wz = footprint[i].y + centerPointWorld.z;
                if (!CoastlineStore.IsLand(wx, wz))
                {
                    float s = Vector2.Dot(new Vector2(wx, wz) - a, perp);
                    sideSum += s;
                    seaOffsets.Add(s);
                }
            }
            if (seaOffsets.Count == 0)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                    "[PortBMB] 海側の底面頂点が無いためカットせず元形状を使用");
                return (Vector2[])footprint.Clone();
            }
            if (sideSum < 0f)
            {
                perp = -perp;
                for (int i = 0; i < seaOffsets.Count; i++)
                {
                    seaOffsets[i] = -seaOffsets[i];
                }
            }
            // A→B を「海を左手に見る」向きへ正規化する（同じ建物では常に同じ対応になる）。
            if (Vector2.Dot(perp, new Vector2(-u.y, u.x)) < 0f)
            {
                Vector2 tmp = a;
                a = b;
                b = tmp;
                u = -u;
            }

            // 奥行き上限 = 弦から底面の海側最遠頂点までの距離。
            float maxDepth = 0f;
            foreach (float s in seaOffsets)
            {
                if (s > maxDepth)
                {
                    maxDepth = s;
                }
            }
            if (maxDepth < 0.5f)
            {
                return (Vector2[])footprint.Clone();
            }

            // 水際の両端（海岸線の上）から左右とも同じ距離だけ海側に引く。
            float depth = Mathf.Min(QUAY_PROTRUSION, maxDepth);
            Vector2 aOut = a + perp * depth;
            Vector2 bOut = b + perp * depth;

            // カット線 A'-B' の陸側半平面（十分大きな矩形）と底面の交差 = 岸壁フットプリント。
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

            // 外周リングをローカル座標へ戻す。
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
                $"[PortBMB] 直線カット適用: 頂点 {n}→{quayFootprint.Length}, 張り出し {depth:F1}m (上限{maxDepth:F1}m)");
            return quayFootprint;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortBMB] 岸壁の直線カットに失敗しました: {ex.Message}。元の形状を使用します。");
            return (Vector2[])footprint.Clone();
        }
    }

    /// <summary>
    /// 岸壁の左端・右端の候補＝区画の水際が底面外周に届く点を、ワールド座標で output に集める。
    ///
    /// 区画の中に陸（面積 <see cref="MIN_LAND_PART_AREA"/> 以上）があれば、水際はその陸と区画の海の部分の境目である。
    /// 陸の部分の頂点のうち底面外周の上にあり、周り（半径 <see cref="QUAY_END_PROBE_RADIUS"/> の円）の区画の中に陸と海が
    /// それぞれ <see cref="QUAY_END_MIN_SHARE"/> 以上あるものを採る。隣の埋立地が区画の辺や角に外から接する所は、
    /// 海岸線が区画の外周に触れても区画の中に陸が無いので採らない（採ると最遠の2点の弦が斜めになる）。
    /// 区画の中に陸が無ければ区画は海岸線に外から接しているだけなので、底面外周と海岸線の交わりをそのまま使う。
    /// </summary>
    private static void CollectQuayEndCandidates(GeometryFactory gf, Geometry footprintPoly, Geometry land, List<Vector2> output)
    {
        Geometry landPart = PolygonalPart(gf, footprintPoly.Intersection(land));
        if (landPart == null || landPart.Area < MIN_LAND_PART_AREA)
        {
            CollectCoordinates(footprintPoly.Boundary.Intersection(land.Boundary), output);
            return;
        }

        Geometry outline = footprintPoly.Boundary;
        foreach (Coordinate c in landPart.Coordinates)
        {
            Geometry point = gf.CreatePoint(c);
            if (outline.Distance(point) > QUAY_END_OUTLINE_TOLERANCE)
            {
                continue;
            }
            Geometry disk = point.Buffer(QUAY_END_PROBE_RADIUS);
            double parcelArea = disk.Intersection(footprintPoly).Area;
            double landArea = disk.Intersection(landPart).Area;
            double minArea = QUAY_END_MIN_SHARE * parcelArea;
            if (landArea > minArea && parcelArea - landArea > minArea)
            {
                output.Add(new Vector2((float)c.X, (float)c.Y));
            }
        }
    }

    /// <summary>重ね合わせの結果から面（Polygon）だけを取り出す。接するだけで生じる線や点は捨てる。面が無ければ null。</summary>
    private static Geometry PolygonalPart(GeometryFactory gf, Geometry geom)
    {
        if (geom == null || geom.IsEmpty)
        {
            return null;
        }
        var polygons = new List<Polygon>();
        for (int i = 0; i < geom.NumGeometries; i++)
        {
            if (geom.GetGeometryN(i) is Polygon p && !p.IsEmpty)
            {
                polygons.Add(p);
            }
        }
        if (polygons.Count == 0)
        {
            return null;
        }
        return polygons.Count == 1 ? (Geometry)polygons[0] : gf.CreateMultiPolygon(polygons.ToArray());
    }

    /// <summary>Geometry（Point / MultiPoint / LineString / Collection）から頂点座標を再帰的に収集する。</summary>
    private static void CollectCoordinates(Geometry geom, List<Vector2> output)
    {
        if (geom == null || geom.IsEmpty)
        {
            return;
        }
        if (geom is GeometryCollection)
        {
            for (int i = 0; i < geom.NumGeometries; i++)
            {
                CollectCoordinates(geom.GetGeometryN(i), output);
            }
            return;
        }
        foreach (Coordinate c in geom.Coordinates)
        {
            output.Add(new Vector2((float)c.X, (float)c.Y));
        }
    }

    /// <summary>
    /// 岸壁フットプリントから厚みのあるスラブメッシュを生成する。
    /// 上面は <paramref name="topLocalY"/> の水平面、下面は各頂点位置の地形（海底）高さに合わせる。
    /// subMesh0=上面、subMesh1=側面+下面 の2サブメッシュ構成で返す。
    /// 巻き方は <see cref="BuildingMeshBuilder.GenerateSimpleBuilding"/> と同じ（外向き法線）。
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

        // 各頂点の下面ローカルY = その水平位置の地形（海底）高さ − center_point 位置の地形高さ。
        // 上面より上に来ないようにクランプし、浅瀬で地形が上面を超える場合のメッシュ反転を防ぐ。
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

        // 上面（topLocalY、上向き法線 → 巻きを反転）
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

        // 下面（頂点ごとの bottomY、下向き法線 → 自然な巻き）
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

        // 側面（各辺を下面→上面へ押し出し。GenerateSimpleBuilding と同じ巻き）
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
    /// 直線カットの岸壁の水際＝岸壁フットプリントのうちカット直線上に乗っている辺を、フットプリントの辺の順に列挙する。
    /// 海側境界が特定できなかった建物（海岸線未ロード・カット不能）では水際が定まらないため空。
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

            // 両端の法線方向オフセットが0とみなせる辺だけがカット直線上にある。
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
    /// メッシュの各サブメッシュを CombineInstance リストに追加する。サブメッシュごとに対応するマテリアルを積む。
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
