using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 工事現場（建設中 STATUS_CONSTRUCTING / 解体中 STATUS_DEMOLISHING）の「工事現場」メッシュ生成器。
/// model_type に関わらず、BuildingMeshBuilder.Generate の冒頭で status により
/// このビルダーへ分岐する（完成品のメッシュは一切生成しない）。
///
/// 建設中と解体中は構成も積み上げロジックも全く同じで、使用するパーツと建機の種別だけが違う。
/// その差分は <see cref="ConstructionSiteProfile"/>（コンストラクタで受け取る）に集約し、
/// 1つのビルダーを両状態で共用する。
///
/// 生成物:
/// - 土マウンド … 敷地（土地区画ポリゴン。無ければ建物底面）全体を覆う、厚みと
///   Perlinノイズのランダムな凹凸を持つ土の盛り上がり。マテリアルは Resources/ConstructionDirt
///   （未配置時は ConstructionMaterial → BuildingMaterial にフォールバック）。
/// - 基盤 … 完成品と同じ「建物底面をY方向に押し出した土台」（BuildingMeshBuilder.GenerateFoundationMesh）を
///   土マテリアルで生成する。傾斜地対応の対象 model_type（IsApplicableModelType）のみ。
/// - 足場リング … profile の scaffold 壁/角パーツを建物底面ポリゴンの外周に沿って
///   フェンスと同型のロジックで配置した1階分のリングを、工事進捗に応じた階数
///   (builtFloors) 分Y方向に積み上げる。
/// - 建機 … profile の MachineKind により分岐する。
///   StackedCrane（建設中）: crane-base / crane-mid / crane-top を prebuilt と同じ規則で垂直に積む。
///     base/mid はY方向のみ階高スケール、top は完全実寸。一定階数(MachineMinFloors)以上の建物のみ。
///   GroundVehicle（解体中）: ショベルカーをマウンド上面に実寸で1台据える（階数ゲートなし）。
///
/// パーツのフォールバック: a{variant} → b{bd_type}（既存の prefix 解決）→ 標準パーツ
/// Models/Buildings/common/（BuildingMeshUtils.GetPartWithCommonFallback）。
/// パーツ・マテリアルのアセットが未配置でも破綻しないオプショナル設計
/// （scaffold/建機はスキップ、土はフォールバックマテリアルで描画される）。
///
/// 工事進捗はAPIの construction_progress（= 調達済み建設体積の百分率。サーバー計算）を使う。
/// 進捗が増えるとバックエンドが version を bump するため、毎エポックの差分ロードで
/// このビルダーが再実行され、足場・建機が伸びていく（解体中も同一ロジック）。
///
/// 既知の割り切り:
/// - MODEL_MODULAR_GROUND（畑・庭）と MODEL_PORT（岸壁）は土マウンドのみ（足場・建機なし）。
/// - MODEL_PORT では敷地が海岸線を含むため、土が海面上に描かれることがある。
/// </summary>
public class ConstructingBuildingMeshBuilder : IBuildingMeshGenerator
{
    private readonly ConstructionSiteProfile profile;

    /// <summary>
    /// 建設中/解体中の共用ビルダー。profile で使用パーツと建機種別を切り替える。
    /// profile が null の場合は建設中プリセットにフォールバックする。
    /// </summary>
    public ConstructingBuildingMeshBuilder(ConstructionSiteProfile profile)
    {
        this.profile = profile ?? ConstructionSiteProfile.Construction;
    }

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null)
        {
            Debug.LogError("ConstructingBuildingMeshBuilder: buildingData が null です。");
            return null;
        }

        BuildingMeshUtils.InitializeCache();

        int modelType = BuildingMeshBuilder.GetModelType(buildingData);

        // 建物底面（ローカル座標・CCW）。polygon が不正な建物でも敷地の土だけは描けるよう null 許容
        Vector2[] footprint = (buildingData.polygonArray != null && buildingData.polygonArray.Length >= 3)
            ? BuildingMeshUtils.GetFootprint(buildingData)
            : null;

        // 敷地コンテキスト（区画ポリゴンは center_point 基準のローカルXZ座標）。
        // 高さの基準は完成品の描画と同じ規則にし、工事中→完成の切り替えで建物が上下に跳ねないようにする
        Vector2[] parcelFootprint = BuildingMeshUtils.ResolveParcelContext(
            buildingData, useParcelAltitude: BuildingMeshBuilder.IsApplicableModelType(modelType),
            out ParcelSummary parcel, out Vector3 centerPointWorld);

        // 土マウンドの対象ポリゴン（ローカルXZ）。区画が無ければ建物底面を使う
        Vector2[] dirtPolygon = parcelFootprint ?? footprint;

        if (dirtPolygon == null)
        {
            Debug.LogWarning($"ConstructingBuildingMeshBuilder: polygon も parcel も不正なため空メッシュを返します (agent_id={buildingData.agent_id})。");
            return new BuildingMeshResult(new Mesh(), null);
        }

        // 工事進捗% → 積み上げ済み階数。0%やnull（未調達）でも1段は出し、「着工済みの現場」が視認できるようにする
        int floors = Mathf.Max(1, buildingData.floors ?? 1);
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        float progressPercent = GeneratedModelUtils.ToFloat(buildingData.construction_progress);
        float progress01 = Mathf.Clamp01(progressPercent / 100f);
        int builtFloors = Mathf.Clamp(Mathf.CeilToInt(progress01 * floors), 1, floors);

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> allMaterials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        Material dirtMaterial = BuildingMeshBuilder.GetConstructionDirtMaterial();

        // --- 1. 土マウンド（厚み + Perlin凹凸の上面と、地面までのスカート） ---
        Mesh moundMesh = GenerateDirtMoundMesh(dirtPolygon, centerPointWorld, isDynamic);
        if (moundMesh != null)
        {
            combineInstances.Add(new CombineInstance { mesh = moundMesh, subMeshIndex = 0, transform = Matrix4x4.identity });
            allMaterials.Add(dirtMaterial);
            temporaryMeshes.Add(moundMesh);
        }

        // --- 2. 基盤（斜面の土台）。完成品と同じく建物底面の押し出しだが、マテリアルは土にする ---
        // 完成品に基盤が生成されない model_type（GROUND / PORT / PREBUILT）は工事中も生成しない
        // （工事中→完成の切り替えで土台の有無が変わらないようにする）。
        if (footprint != null && BuildingMeshBuilder.IsApplicableModelType(modelType))
        {
            Mesh foundationMesh = BuildingMeshBuilder.GenerateFoundationMesh(footprint, centerPointWorld);
            if (foundationMesh != null)
            {
                combineInstances.Add(new CombineInstance { mesh = foundationMesh, subMeshIndex = 0, transform = Matrix4x4.identity });
                allMaterials.Add(dirtMaterial);
                temporaryMeshes.Add(foundationMesh);
            }
        }

        // --- 3. 足場リングとクレーン ---
        // 地面アセット（畑・庭）・岸壁・車両基地には足場・クレーンを立てない（土のみの現場表現）
        bool wantsStructures = footprint != null
            && modelType != Constants.MODEL_MODULAR_GROUND
            && !BuildingMeshBuilder.IsOpenSpaceModel(modelType)
            && modelType != Constants.MODEL_PORT
            && modelType != Constants.MODEL_RAIL_YARD;

        if (wantsStructures)
        {
            string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
            float visualVariant = buildingData.visual_variant;

            AddScaffoldRings(footprint, prefix, visualVariant, floorHeight, builtFloors,
                profile.ScaffoldWallPart, profile.ScaffoldCornerPart,
                combineInstances, allMaterials, temporaryMeshes);

            // 建機。profile の種別で分岐する（建設中=タワークレーン / 解体中=地上のショベルカー）。
            // GroundVehicle は MachineMinFloors=0 なので階数に関わらず据える。
            if (floors >= profile.MachineMinFloors)
            {
                if (profile.MachineKind == SiteMachineKind.StackedCrane)
                {
                    AddCrane(footprint, prefix, visualVariant, buildingData.visual_variant2, floorHeight, builtFloors,
                        profile.CraneBasePart, profile.CraneMidPart, profile.CraneTopPart,
                        combineInstances, allMaterials, temporaryMeshes);
                }
                else // GroundVehicle
                {
                    AddGroundMachine(footprint, prefix, visualVariant, buildingData.visual_variant2,
                        profile.GroundMachinePart, combineInstances, allMaterials, temporaryMeshes);
                }
            }

            // 施工済み階の天面を footprint 形状のパネル(ceil マテリアル)で塞ぐ。
            // 足場リングは中空なので、これが無いと建物内部が上から覗けてしまう。
            AddConstructionCeil(footprint, prefix, visualVariant, builtFloors * floorHeight,
                profile.CeilPart, combineInstances, allMaterials, temporaryMeshes);
        }

        if (combineInstances.Count == 0)
        {
            foreach (Mesh temp in temporaryMeshes)
            {
                Object.Destroy(temp);
            }
            return new BuildingMeshResult(new Mesh(), null);
        }

        // --- 4. 結合 ---
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // --- 5. コライダー ---
        // 足場の箱でクリック判定する。クレーンのはみ出しはクリック対象外で許容
        // （prebuilt の top はみ出し許容と同じ思想）。敷地（土）のクリックは ParcelObject 側が担う。
        Vector2[] colliderFootprint = footprint ?? dirtPolygon;
        float colliderHeight = Mathf.Max(builtFloors * floorHeight, floorHeight);
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(colliderFootprint, colliderHeight);

        var result = new BuildingMeshResult(finalMesh, allMaterials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    // ------------------------------------------------------------------
    // 足場
    // ------------------------------------------------------------------

    /// <summary>
    /// 足場（profile の 壁/角パーツ）を builtFloors 段ぶん生成して登録する。
    /// 配置は通常建物の壁・角と全く同じロジック（ModularBodyBuilder.GenerateScaffoldSegments）を
    /// フットプリント上でそのまま使う。壁パーツが無ければ何もしない。
    /// </summary>
    private static void AddScaffoldRings(Vector2[] footprint, string prefix, float visualVariant,
        float floorHeight, int builtFloors, string wallPart, string cornerPart,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        GameObject wallModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, wallPart, visualVariant);
        if (wallModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[CBMB] 足場: {wallPart} パーツなし (prefix={prefix})。足場をスキップします。");
            return;
        }
        Material[] wallMaterials = BuildingMeshUtils.GetModuleMaterials(wallModule);

        GameObject cornerModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, cornerPart, visualVariant);
        Material[] cornerMaterials = cornerModule != null ? BuildingMeshUtils.GetModuleMaterials(cornerModule) : null;

        ModularBodyBuilder.GenerateScaffoldSegments(
            footprint, wallModule, wallMaterials, cornerModule, cornerMaterials,
            floorHeight, builtFloors, combineInstances, allMaterials, temporaryMeshes);
    }

    /// <summary>
    /// 施工済み階の天面を footprint 形状の水平パネルで塞ぐ（工事中の建物内部が上から見えないように）。
    /// マテリアルは ceil パーツ（a{variant}→b{bd_type}→common の順に解決）から取り、無ければ
    /// デフォルト建物マテリアルにフォールバックする。prebuilt の積み上げ天面（§17）と同じ考え方。
    /// </summary>
    private static void AddConstructionCeil(Vector2[] footprint, string prefix, float visualVariant, float height,
        string ceilPartName,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        if (footprint == null || footprint.Length < 3) return;

        Mesh ceilMesh = BuildingMeshBuilder.GenerateHorizontalPolygonMesh(footprint, height);
        if (ceilMesh == null) return;

        Material ceilMat = null;
        GameObject ceilPart = BuildingMeshUtils.GetPartWithCommonFallback(prefix, ceilPartName, visualVariant);
        if (ceilPart != null)
        {
            Material[] mats = BuildingMeshUtils.GetModuleMaterials(ceilPart);
            if (mats != null && mats.Length > 0) ceilMat = mats[0];
        }
        if (ceilMat == null) ceilMat = BuildingMeshBuilder.GetDefaultBuildingMaterial();

        combineInstances.Add(new CombineInstance { mesh = ceilMesh, subMeshIndex = 0, transform = Matrix4x4.identity });
        allMaterials.Add(ceilMat);
        temporaryMeshes.Add(ceilMesh);
    }

    // ------------------------------------------------------------------
    // 建機（クレーン / 地上建機）
    // ------------------------------------------------------------------

    /// <summary>
    /// 建機の設置位置と向きを決める（クレーン・地上建機で共通）。
    /// 位置は footprint 重心 + visual_variant2 シードのオフセット、向きは最初の辺に合わせた
    /// Yaw（いずれも塔屋 GenerateRoofprop と同じ決め方）。
    /// </summary>
    private static void ComputeMachinePlacement(Vector2[] footprint, float visualVariant2,
        out Vector2 xzOffset, out Quaternion rotation)
    {
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < footprint.Length; i++)
        {
            centroid += footprint[i];
        }
        centroid /= footprint.Length;

        float seed = Mathf.Clamp01(visualVariant2);
        float angle = seed * Mathf.PI * 2f;
        const float radius = 0.5f;
        xzOffset = new Vector2(centroid.x + Mathf.Cos(angle) * radius, centroid.y + Mathf.Sin(angle) * radius);

        Vector2 firstEdge = footprint.Length >= 2 ? (footprint[1] - footprint[0]).normalized : Vector2.right;
        float edgeAngle = Mathf.Atan2(firstEdge.y, firstEdge.x) * Mathf.Rad2Deg;
        rotation = Quaternion.Euler(0, -edgeAngle + 90, 0);
    }

    /// <summary>
    /// タワークレーン（base/mid/top パーツ）を prebuilt と同じ積み上げ規則で登録する。
    /// 総段数は builtFloors + CONSTRUCTION_CRANE_EXTRA_LEVELS（工事進捗に連動し、常に建物より
    /// 少し高く伸びる）。base は最下段1段分・mid は残り段数分（いずれもY方向のみ階高スケール）、
    /// top は頂部に完全実寸で載せる。base と mid が両方無い場合は何もしない
    /// （top だけあると接地したジブになってしまうため）。
    /// </summary>
    private static void AddCrane(Vector2[] footprint, string prefix, float visualVariant, float visualVariant2,
        float floorHeight, int builtFloors,
        string basePart, string midPart, string topPart,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        GameObject baseModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, basePart, visualVariant);
        GameObject midModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, midPart, visualVariant);
        GameObject topModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, topPart, visualVariant);

        if (baseModule == null && midModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[CBMB] クレーン: {basePart} / {midPart} パーツなし (prefix={prefix})。クレーンをスキップします。");
            return;
        }

        ComputeMachinePlacement(footprint, visualVariant2, out Vector2 xzOffset, out Quaternion rotation);

        int totalLevels = builtFloors + LocalConstants.CONSTRUCTION_CRANE_EXTRA_LEVELS;
        float stackY = 0f;
        int remainingLevels = totalLevels;

        // クレーンのパーツは「原点(y=0)=接続点(下端)」で著作されている前提で alignByOrigin=true で積む。
        // これにより、原点より下に張り出しのある top でも接続点がぴたり合い、段間の隙間が出ない。
        if (baseModule != null)
        {
            BuildingMeshUtils.AddStackedModule(baseModule, stackY, floorHeight,
                combineInstances, allMaterials, temporaryMeshes, xzOffset, rotation, alignByOrigin: true);
            stackY += floorHeight;
            remainingLevels--;
        }

        if (midModule != null)
        {
            for (int i = 0; i < remainingLevels; i++)
            {
                BuildingMeshUtils.AddStackedModule(midModule, stackY, floorHeight,
                    combineInstances, allMaterials, temporaryMeshes, xzOffset, rotation, alignByOrigin: true);
                stackY += floorHeight;
            }
        }

        if (topModule != null)
        {
            // top は一切スケールせず実寸のまま、原点(=最上段midのてっぺん=接続点)を stackY に置く
            BuildingMeshUtils.AddStackedModule(topModule, stackY, null,
                combineInstances, allMaterials, temporaryMeshes, xzOffset, rotation, alignByOrigin: true);
        }
    }

    /// <summary>
    /// 地上建機（ショベルカー等）を1台、マウンド上面に実寸で据える。
    /// クレーンのように段積みはせず、Yスケールもしない。位置・向きはクレーンと同じシードロジック。
    /// パーツが無ければ何もしない（オプショナル）。
    /// </summary>
    private static void AddGroundMachine(Vector2[] footprint, string prefix, float visualVariant, float visualVariant2,
        string machinePart,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        GameObject machineModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, machinePart, visualVariant);
        if (machineModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[CBMB] 地上建機: {machinePart} パーツなし (prefix={prefix})。建機をスキップします。");
            return;
        }

        ComputeMachinePlacement(footprint, visualVariant2, out Vector2 xzOffset, out Quaternion rotation);

        // マウンド上面（土の基本厚み分）に、モデル原点(y=0)=接地点として実寸で1台載せる。
        // fitHeight=null でYスケールせず、alignByOrigin=true で原点を接地させる。
        BuildingMeshUtils.AddStackedModule(machineModule, LocalConstants.CONSTRUCTION_DIRT_THICKNESS, null,
            combineInstances, allMaterials, temporaryMeshes, xzOffset, rotation, alignByOrigin: true);
    }

    // ------------------------------------------------------------------
    // 土マウンド
    // ------------------------------------------------------------------

    /// <summary>
    /// 敷地ポリゴン（center_point 基準のローカルXZ座標）を覆う土マウンドメッシュを生成する。
    ///
    /// 形状の作り方:
    /// 1. ポリゴンを耳切り法で三角形分割し、辺長が CONSTRUCTION_DIRT_MAX_EDGE を超える辺を
    ///    中点分割して内部頂点を作る（中点は辺の共有キーで溶接し、T字接合の隙間を防ぐ）。
    /// 2. 上面の高さ = 厚み + Perlinノイズ × 境界フェード。境界フェードは「元ポリゴン境界までの
    ///    距離 / CONSTRUCTION_DIRT_EDGE_FADE」で、外周頂点では凹凸0になり隣接区画と隙間ができない。
    ///    ノイズ入力はワールド座標（ローカル + centerPointWorld）なので、再生成しても模様は安定する。
    /// 3. 外周（境界辺 = 片側の三角形にしか使われない辺）から地面(ローカルY=0)までスカートを張る。
    /// 4. isDynamic（超近景セル）の場合は各頂点に地形高さの差分を加算して地形追従する
    ///    （フェンス・地面パネルと同じ2段階追従の慣習）。
    /// </summary>
    /// <param name="thickness">上面の基本厚み(m)。倒壊の瓦礫マウンドは土より厚くするためオーバーライドする。</param>
    /// <param name="bumpAmplitude">上面ノイズ凹凸の振幅(m)。瓦礫は土より荒くするためオーバーライドする。</param>
    internal static Mesh GenerateDirtMoundMesh(Vector2[] localPolygon, Vector3 centerPointWorld, bool isDynamic,
        float thickness = LocalConstants.CONSTRUCTION_DIRT_THICKNESS,
        float bumpAmplitude = LocalConstants.CONSTRUCTION_DIRT_BUMP_AMPLITUDE)
    {
        if (localPolygon == null || localPolygon.Length < 3) return null;

        var localVerts = new List<Vector2>(localPolygon);
        Vector2[] boundaryPolygon = localPolygon;

        List<int> baseTriangles = BuildingMeshBuilder.TriangulateOrFan(boundaryPolygon);
        if (baseTriangles.Count < 3) return null;

        // --- 細分化（長辺の中点分割。中点頂点は localVerts に追記される） ---
        var finalTriangles = BuildingMeshBuilder.SubdivideTrianglesByEdgeLength(
            localVerts, baseTriangles, LocalConstants.CONSTRUCTION_DIRT_MAX_EDGE);

        // --- 頂点の高さを決定 ---
        float bumpAmp = bumpAmplitude;
        float edgeFade = LocalConstants.CONSTRUCTION_DIRT_EDGE_FADE;

        int vertexCount = localVerts.Count;
        var topHeights = new float[vertexCount];
        var terrainDeltas = new float[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            Vector2 local = localVerts[i];
            float worldX = local.x + centerPointWorld.x;
            float worldZ = local.y + centerPointWorld.z;

            float boundaryDistance = PolygonUtils.DistanceToBoundary(boundaryPolygon, local);
            float edgeFactor = Mathf.Clamp01(boundaryDistance / edgeFade);
            // 多オクターブの土ノイズ（土ストリップと共通。大きなうねり+細かい土塊）
            float noise = ConstructionMeshUtil.DirtNoise(worldX, worldZ);
            topHeights[i] = thickness + noise * bumpAmp * edgeFactor;

            terrainDeltas[i] = 0f;
            if (isDynamic)
            {
                terrainDeltas[i] = Utils.GetGroundHeight(new Vector3(worldX, 1000f, worldZ)) - centerPointWorld.y;
            }
        }

        // --- メッシュ組み立て ---
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var uvs = new List<Vector2>();

        // 上面。2D三角形（XZ平面でCCW）をそのまま貼ると法線が下向きになるため、巻き順を反転して上向きにする
        for (int i = 0; i < vertexCount; i++)
        {
            Vector2 local = localVerts[i];
            vertices.Add(new Vector3(local.x, topHeights[i] + terrainDeltas[i], local.y));
            uvs.Add(new Vector2(local.x * 0.1f, local.y * 0.1f));
        }
        foreach (var (a, b, c) in finalTriangles)
        {
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(b);
        }

        // 境界辺の抽出: 逆向きの辺が存在しない有向辺が外周（CCW順で並ぶ）
        var directedEdges = new HashSet<long>();
        foreach (var (a, b, c) in finalTriangles)
        {
            directedEdges.Add(BuildingMeshBuilder.EdgeKey(a, b));
            directedEdges.Add(BuildingMeshBuilder.EdgeKey(b, c));
            directedEdges.Add(BuildingMeshBuilder.EdgeKey(c, a));
        }

        // スカート（外周 → ローカルY=0）
        foreach (var (a, b, c) in finalTriangles)
        {
            AddSkirtIfBoundary(a, b, directedEdges, localVerts, topHeights, terrainDeltas, vertices, triangles, uvs);
            AddSkirtIfBoundary(b, c, directedEdges, localVerts, topHeights, terrainDeltas, vertices, triangles, uvs);
            AddSkirtIfBoundary(c, a, directedEdges, localVerts, topHeights, terrainDeltas, vertices, triangles, uvs);
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// 有向辺(i→j)が外周（逆向きの辺を持たない）ならスカートの四角形を張る。
    /// 巻き順は GenerateFoundationMesh の側面と同じ（CCW外周で外向きの面になる）。
    /// </summary>
    private static void AddSkirtIfBoundary(int i, int j, HashSet<long> directedEdges,
        List<Vector2> localVerts, float[] topHeights, float[] terrainDeltas,
        List<Vector3> vertices, List<int> triangles, List<Vector2> uvs)
    {
        if (directedEdges.Contains(BuildingMeshBuilder.EdgeKey(j, i))) return; // 逆向きが存在 = 内部辺

        Vector2 pi = localVerts[i];
        Vector2 pj = localVerts[j];
        float dist = Vector2.Distance(pi, pj);

        int start = vertices.Count;
        vertices.Add(new Vector3(pi.x, terrainDeltas[i], pi.y));                 // 下端 i
        vertices.Add(new Vector3(pj.x, terrainDeltas[j], pj.y));                 // 下端 j
        vertices.Add(new Vector3(pi.x, topHeights[i] + terrainDeltas[i], pi.y)); // 上端 i
        vertices.Add(new Vector3(pj.x, topHeights[j] + terrainDeltas[j], pj.y)); // 上端 j

        uvs.Add(new Vector2(0f, 0f));
        uvs.Add(new Vector2(dist * 0.1f, 0f));
        uvs.Add(new Vector2(0f, topHeights[i] * 0.1f));
        uvs.Add(new Vector2(dist * 0.1f, topHeights[j] * 0.1f));

        triangles.Add(start + 0);
        triangles.Add(start + 2);
        triangles.Add(start + 1);

        triangles.Add(start + 1);
        triangles.Add(start + 2);
        triangles.Add(start + 3);
    }
}
