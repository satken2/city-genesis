// Assets/Scripts/MeshGenerators/Buildings/ModularBodyBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// モジュラーパーツ組み立てによる建物の壁・角・地面パネルを生成するクラス。
/// 屋根や塔屋の生成は含まず、純粋に建物の躯体（壁面と地面）のみを担当する。
/// 生成結果は CombineInstance リストとして返され、呼び出し側で屋根や塔屋と結合する。
/// </summary>
public static class ModularBodyBuilder
{
    public const float GROUND_OFFSET = 0.005f;

    /// <summary>
    /// モジュラーパーツ組み立てによる壁・角・地面パネルの生成結果を保持する構造体。
    /// </summary>
    public struct BodyMeshData
    {
        public List<CombineInstance> CombineInstances;
        public List<Material> Materials;
        public List<Mesh> TemporaryMeshes;
        /// <summary>
        /// 確定した閾値フォルダ名。屋根マテリアルや塔屋の検索に使用する。
        /// DefaultBodyBuilder フォールバック時は 0 を返す。
        /// </summary>
        public int ResolvedSuffix;

        /// <summary>
        /// 躯体の高さ(m) = floors × 階高。パーツの配置は常にこの階高グリッドに乗るため、
        /// スケールしない1階・最上階のパーツの著作高さとは無関係に決まる。
        /// 屋根・塔屋の配置Yやコライダー高さはこの値を使うこと。
        /// </summary>
        public float BuildingHeight;

        public bool IsValid => CombineInstances != null && CombineInstances.Count > 0;
    }

    /// <summary>
    /// 壁・角パーツをYスケールしないことを表す targetHeight の値。
    /// 1階(base)・最上階(top)・フェンスはこの規約で著作高さのまま描画される。
    /// 中層(mid)の階と工事中の足場だけは階高に合わせるため正の targetHeight を渡す。
    /// </summary>
    private const float NO_Y_SCALE = 0f;

    /// <summary>
    /// 実寸として不自然に小さいパーツを検出する閾値(m)。
    /// パーツはスケールされないため、cm単位や1×1の単位スケールでエクスポートされたFBXは
    /// そのまま極小メッシュとして描画されてしまう。気づけるよう警告を出す。
    /// </summary>
    private const float SUSPICIOUS_PART_SIZE = 0.3f;

    /// <summary>寸法が疑わしいパーツの警告を1メッシュ名につき1回に抑えるための記録。</summary>
    private static readonly HashSet<string> warnedUndersizedParts = new HashSet<string>();

    /// <summary>
    /// XZ実寸が極端に小さいパーツを検出したら警告する。同じメッシュ名では1回だけ出力する。
    /// 描画は補正せず著作サイズのまま行う（実寸で再エクスポートするのが正しい対処）。
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
    /// セグメント生成結果を保持するための簡易構造体。
    /// </summary>
    public struct SegmentMeshData
    {
        public Mesh Mesh;
        public Material[] Materials;

        public bool IsValid => Mesh != null && Mesh.vertexCount > 0 && Mesh.subMeshCount > 0;
    }

    /// <summary>
    /// モジュラーパーツを組み立てて壁・角・地面パネルを生成する。
    /// 生成された CombineInstance / Material / Mesh のリストは呼び出し側で最終的な結合を行う。
    /// </summary>
    /// <param name="footprint">建物底面の頂点列（CCW）</param>
    /// <param name="floors">階数</param>
    /// <param name="prefix">パーツのプレフィックス（b{bd_type} または a{variant}）</param>
    /// <param name="floorHeight">1階あたりの高さ。各階の床面Y（階 × 階高）と躯体高さを決める。中層(mid)の階の壁・角はこの高さへYスケールされるが、1階・最上階のパーツはスケールされない。</param>
    /// <param name="visual_variant">外観バリアント（閾値フォルダの選択に使用）</param>
    /// <param name="building_shape">建物の形状タイプ（SHAPE_FULL の場合は最上階のみを処理する）</param>
    /// <param name="modelType">モデルタイプ。MODEL_MODULAR_GROUND の場合は建物本体を描画せず fence のみを描画する。</param>
    /// <param name="fenceFootprint">フェンスを配置する土地区画の頂点列（CCW・center_point 基準のローカルXZ座標。footprint と同じ規約）。null の場合は fence を描画しない。</param>
    /// <param name="centerPointWorld">建物の center_point に対応するワールド座標。地形追従時の高さサンプリングに使用する。</param>
    /// <param name="floorMaterial">地面パネル用マテリアル。呼び出し側が master の draw_floor と floor パーツ（a→b→common）から解決済み。null なら地面パネルを描画しない。</param>
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
        Material floorMaterial = null)
    {
        int variantNumber = BuildingMeshUtils.ComputeVariantNumber(visual_variant);
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

        // 躯体の高さは常に階高グリッドで決まる（各階の床面Y = 階 × 階高）。
        // パーツをスケールしない1階・最上階では、パーツの著作高さが階高と食い違うぶん
        // 上の階との間に隙間ができたり、上の階のパーツに埋もれたりするが、これは仕様。
        float resolvedBuildingHeight = floors * floorHeight;

        // --- 建物部分（base / mid / top）---
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

            for (int floor = floorStart; floor < floorEnd; floor++)
            {
                float floorY = floor * floorHeight;

                string level = (floor == 0) ? "base" : (floor == floors - 1) ? "top" : "mid";

                // 中層の階だけ階高へYスケールし、1階・最上階は著作高さのまま描画する。
                // 判定は「階のレベル」で行うので、base/top のパーツが無くて mid-w1 等へ
                // フォールバックした1階・最上階もスケールされない。
                float targetHeight = (level == "mid") ? floorHeight : NO_Y_SCALE;

                for (int i = 0; i < n; i++)
                {
                    int position = i + 1;
                    Vector2 p0 = footprint[(i + n - 1) % n];
                    Vector2 p1 = footprint[i];
                    Vector2 p2 = footprint[(i + 1) % n];

                    GameObject wallModule = LoadPartWithFallback(prefix, level, "w", position, resolvedSuffix, out Material[] wallMaterials);
                    GameObject cornerModule = LoadPartWithFallback(prefix, level, "c", position, resolvedSuffix, out Material[] cornerMaterials);

                    if (wallModule != null)
                    {
                        Vector2 p3 = footprint[(i + 2) % n];
                        int nextPosition = (i + 1) % n + 1;
                        GameObject nextCornerModule = LoadPartWithFallback(prefix, level, "c", nextPosition, resolvedSuffix, out _);

                        // 壁の敷き詰め範囲は、両端の角パーツがこの辺へ実際に張り出す長さぶん内側へ狭める。
                        // 手前側は頂点 p1 の角パーツの送り側アーム、奥側は頂点 p2 の角パーツの受け側アーム。
                        float startOffset = MeasureCornerExtentAlongEdge(cornerModule, p0, p1, p2, targetHeight, true);
                        float endOffset = MeasureCornerExtentAlongEdge(nextCornerModule, p1, p2, p3, targetHeight, false);

                        List<SegmentMeshData> wallSegments = GenerateWallSegments(p1, p2, floorY, wallModule, wallMaterials, targetHeight, startOffset, endOffset);
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
                        SegmentMeshData cornerSegment = GenerateCornerSegment(p0, p1, p2, floorY, cornerModule, cornerMaterials, targetHeight);
                        if (cornerSegment.IsValid)
                        {
                            AddSegmentToCombineList(cornerSegment, combineInstances, allMaterials, temporaryMeshes);
                            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB] F{floor} 角 pos{position}: OK, materials={FormatMaterials(cornerMaterials)}");
                        }
                    }
                }
            }
        }

        // 建物本体が 0 パーツでかつ GROUND 以外 → 白い箱フォールバック
        if (!isGroundModel && combineInstances.Count == 0)
        {
            Debug.LogWarning($"[MBB] 建物パーツが0件のため DefaultBodyBuilder にフォールバック (prefix={prefix})");
            return DefaultBodyBuilder.GenerateBody(footprint, floors, floorHeight);
        }

        // --- 敷地の装飾（フェンス・タイル・地面パネル）---
        // フェンスは全 modelType で描画を試行し、タイルは MODEL_MODULAR_GROUND のみ。
        // 地面パネルは master の draw_floor=yes の建物のみ（floorMaterial が非nullのときだけ）描画する。
        // タイル・地面パネルの対象は土地区画ポリゴン(fenceFootprint)全体。建物底面だけに張ると
        // パネルが建物本体の真下に隠れて見えないため。区画が取得できない場合は建物底面へフォールバックする。
        // 傾斜地対応の対象建物(IsApplicableModelType=FLAT/HOUSE/GABLE)は建物本体こそ水平面+
        // コンクリート基盤で接地するが、敷地の装飾（フェンス・タイル・地面パネル）は地形に追従させる。
        // 追従計算は centerPointWorld.y 基準（IsApplicableModelType では parcel.altitude、
        // それ以外は地形標高）で、BuildingManager.UpdateBuildingHeight の配置高さと一致する。
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
            combineInstances, allMaterials, temporaryMeshes);

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
    /// 敷地の装飾（フェンス・タイル・地面パネル）を生成して結合リストへ追加する。
    /// モジュラー建物（GenerateBody）と既成建物（PreBuiltBuildingMeshBuilder）で共通の処理。
    /// 各ポリゴンは center_point 基準のローカルXZ座標（footprint と同じ規約）で受け取る
    /// （区画ポリゴンは BuildingMeshUtils.ResolveParcelContext がローカル変換済みのものを渡すこと）。
    ///
    /// - フェンス: fenceFootprint の外周に fence-w / fence-c パーツを配置する（無スケール・接地）。
    ///   パーツが解決できない辺・角はスキップされる。null でフェンスなし。
    /// - タイル: tilePolygon の内部に tile パーツを敷き詰める（GenerateTiles）。tile パーツが
    ///   無ければ何もしない。null でタイルなし。
    /// - 地面パネル: floorPolygon 形状の水平パネルを GROUND_OFFSET の高さに張る。
    ///   描画するかどうかとマテリアルは呼び出し側が master の draw_floor フラグと
    ///   floor パーツ（a{variant}→b{bd_type}→common）から解決済みの floorMaterial で決まる。
    ///   floorMaterial が null（draw_floor 無効、または floor パーツがどこにも無い）なら
    ///   地面パネルは描画しない（既定マテリアルへのフォールバックはしない）。floorPolygon が
    ///   null の場合も描画しない。
    /// - deformToTerrain=true の場合、フェンス・タイル・地面パネルの頂点を地形に追従させる
    ///   （超近景の GROUND / PREBUILT 用）。
    /// </summary>
    public static void GenerateSiteDecorations(
        string prefix, int suffix,
        Vector2[] fenceFootprint, Vector2[] tilePolygon, Vector2[] floorPolygon, Material floorMaterial,
        Vector3 centerPointWorld, bool deformToTerrain,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        // --- フェンス部分（fence）---
        // パーツが見つからない場合はスキップ。
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

                    // 建物の壁と同じく、両端のフェンス角パーツが辺へ張り出す長さぶん内側から敷き詰める。
                    // フェンス角は無スケール配置（targetHeight=0）なので、実寸がそのまま張り出し量になる。
                    float startOffset = MeasureCornerExtentAlongEdge(cornerModule, p0, p1, p2, 0f, true);
                    float endOffset = MeasureCornerExtentAlongEdge(nextCornerModule, p1, p2, p3, 0f, false);

                    List<SegmentMeshData> wallSegments = GenerateFenceSegments(p1, p2, wallModule, wallMaterials, startOffset, endOffset);
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

                if (cornerModule != null)
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

        // --- タイルオブジェクト ---
        if (tilePolygon != null && tilePolygon.Length >= 3)
        {
            GenerateTiles(tilePolygon, prefix, suffix, centerPointWorld, deformToTerrain,
                          combineInstances, allMaterials, temporaryMeshes);
        }

        // --- 地面パネル ---
        // floorMaterial は呼び出し側で draw_floor フラグと floor パーツの
        // a{variant}→b{bd_type}→common フォールバックを解決済み。null なら描画しない。
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
    /// 壁パーツのソースメッシュを取得する。実配置(BuildWallRun)と同じく MeshFilter を優先する。
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
    /// 角パーツプレハブの計測用ソースメッシュを取得する。
    /// 実配置(GenerateCornerCore)と同じく SkinnedMeshRenderer を優先する。
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
    /// 地面パネル用の水平ポリゴンメッシュを生成する。
    /// </summary>
    public static Mesh GenerateFloorMesh(Vector2[] footprint, float height)
    {
        return BuildingMeshBuilder.GenerateHorizontalPolygonMesh(footprint, height);
    }

    /// <summary>
    /// 地面パネルを地形に沿って傾斜させたメッシュを生成する。
    /// 外周頂点間を1枚の大三角形が張ると区画内部の起伏を拾えず地形を突き抜けるため、ポリゴンを
    /// 三角形分割したうえで長辺を中点分割して内部頂点を作り（BuildingMeshBuilder.SubdivideTrianglesByEdgeLength、
    /// 最大辺長は LocalConstants.SITE_FLOOR_MAX_EDGE）、外周・内部を問わず全頂点を地形標高に合わせる。
    ///
    /// 各頂点の高さは center_point 位置での地形高さ（centerPointWorld.y）を基準にローカル座標化する。
    /// GameObject が center_point 位置の高さに配置されることを前提とする
    /// （BuildingManager.CreateOrUpdateBuildingObject の配置高さと対応）。
    ///
    /// 建物以外の地面パネル（駅舎パスの専有エリア＝StationHousePath）からも共有する。
    /// footprint は中心を原点とするローカルXZ、回転なしの座標系で渡すこと。
    /// </summary>
    public static Mesh GenerateTerrainConformingFloorMesh(Vector2[] footprint, Vector3 centerPointWorld)
    {
        if (footprint == null || footprint.Length < 3) return null;

        float baseGround = centerPointWorld.y;

        // 耳切り分割（凹ポリゴンでも破綻しない）→ 長辺の中点分割で内部頂点を作る。
        List<int> baseTriangles = BuildingMeshBuilder.TriangulateOrFan(footprint);
        if (baseTriangles.Count < 3) return null;

        var verts = new List<Vector2>(footprint);
        var triangles3 = BuildingMeshBuilder.SubdivideTrianglesByEdgeLength(
            verts, baseTriangles, LocalConstants.SITE_FLOOR_MAX_EDGE);
        if (triangles3.Count == 0) return null;

        // 全頂点（内部頂点含む）を地形標高に合わせた共有頂点として組み立てる。
        var vertices = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            vertices[i] = GetTerrainConformingVertex(verts[i], centerPointWorld, baseGround);
        }

        var triangles = new List<int>(triangles3.Count * 3);
        foreach (var (a, b, c) in triangles3)
        {
            // CCWポリゴンをそのままXZに貼ると法線が下向きになるため、巻き順を反転して
            // 上向きにする（GenerateHorizontalPolygonMesh と同じ規約）。
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
    /// 指定された2D座標での地形高さを取得し、center_point 位置の地形高さを基準としたローカル座標を返す。
    /// ローカルYは terrainHeight - baseGround + GROUND_OFFSET となり、
    /// GameObject を baseGround の高さに配置すると各頂点が地形に沿って GROUND_OFFSET だけ浮いた位置になる。
    /// </summary>
    private static Vector3 GetTerrainConformingVertex(Vector2 point, Vector3 centerPointWorld, float baseGround)
    {
        float h = HeightmapTextureManager.GetTerrainHeight(new Vector3(point.x + centerPointWorld.x, 1000f, point.y + centerPointWorld.z));
        return new Vector3(point.x, h - baseGround + GROUND_OFFSET, point.y);
    }

    /// <summary>
    /// メッシュの各頂点を、その頂点の水平位置での地形高さに合わせて変形する。
    /// 元のメッシュ頂点の相対的な高さ関係（フェンスの上端など）は維持する。
    /// </summary>
    private static void DeformMeshToTerrain(Mesh mesh, Vector3 centerPointWorld)
    {
        if (mesh == null || mesh.vertexCount == 0) return;

        Vector3[] vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 worldPos = vertices[i] + centerPointWorld;
            float terrainY = HeightmapTextureManager.GetTerrainHeight(new Vector3(worldPos.x, 1000f, worldPos.z));
            vertices[i].y = terrainY + vertices[i].y - centerPointWorld.y;
        }
        mesh.vertices = vertices;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    /// <summary>
    /// MODEL_MODULAR_GROUND 専用。tile パーツを底面エリアにタイル状に敷き詰める。
    /// モデルのスケールは変更せず、ポリゴン内側に収まるグリッド位置にそのまま配置する。
    /// isDynamic の場合は配置後に地形に合わせて変形する。
    /// tile パーツが存在しない場合は何もしない（フェンス・地面パネルのみ描画される）。
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
            // tile パーツはオプショナル。無ければ何も描画しない(正常系)。
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

        // タイルのサイズが極端に小さい場合は描画をスキップしてエラーログを出力
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

        // 土地面積に対するタイルの見積もり配置数を計算し、多すぎる場合はフリーズ防止のためスキップ
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

                if (!IsPointInPolygon2D(new Vector2(centerX, centerZ), polygon))
                {
                    continue;
                }

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

    /// <summary>
    /// 2D点が多角形の内部にあるかを判定する（ray casting algorithm）。
    /// </summary>
    private static bool IsPointInPolygon2D(Vector2 point, Vector2[] polygon)
    {
        bool inside = false;
        int j = polygon.Length - 1;
        for (int i = 0; i < polygon.Length; i++)
        {
            if (((polygon[i].y > point.y) != (polygon[j].y > point.y)) &&
                (point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x))
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }

    /// <summary>
    /// mid-w1 のバリアントを確定する。
    /// 指定された variantNumber に対して、標準閾値候補（0, 100, 200, ..., 1000）の中から
    /// `mid-w1` パーツが実際に存在する最大の suffix を見つける。
    /// mid-w1 が全く存在しない場合は -1 を返す。
    /// MODEL_MODULAR_GROUND の場合は呼び出し側で suffix=0 にフォールバックして継続する。
    /// </summary>
    private static int ResolveBuildingVariant(string prefix, int variantNumber)
    {
        List<int> candidates = new List<int> { 0 };
        for (int v = 100; v <= 1000; v += 100)
        {
            candidates.Add(v);
        }

        int bestSuffix = -1;
        foreach (int suffix in candidates)
        {
            if (suffix > variantNumber) continue;
            bool exists = BuildingMeshUtils.HasPart(prefix, "mid", "w", 1, suffix);
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   閾値候補 {suffix}: mid-w1 {(exists ? "あり" : "なし")}");
            if (exists)
            {
                if (suffix > bestSuffix) bestSuffix = suffix;
            }
        }

        return bestSuffix;
    }

    /// <summary>
    /// パーツをロードする。位置フォールバック（w3→w1）とレベルフォールバック
    /// （base→mid, top→mid, fence→mid）を壁(w)・角(c)共通で適用する。
    /// mid にも角パーツが無い建物では null を返し、角は描画されず、
    /// 張り出し量が 0 になるため壁パーツが辺の端まで敷き詰められる（角は建物単位のオプトイン）。
    /// </summary>
    private static GameObject LoadPartWithFallback(string prefix, string level, string type, int position, int suffix, out Material[] materials)
    {
        string desc = $"{prefix}/{suffix}/{level}-{type}{position}";

        // 1. 指定位置のパーツを試す
        GameObject part = BuildingMeshUtils.GetPart(prefix, level, type, position, suffix, true);
        if (part != null)
        {
            materials = BuildingMeshUtils.GetModuleMaterials(part);
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   {desc} → 取得成功");
            return part;
        }

        // 2. 位置フォールバック: 位置1を試す
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

        // 3. レベルフォールバック: mid を試す
        if (level != "mid")
        {
            part = BuildingMeshUtils.GetPart(prefix, "mid", type, position, suffix, true);
            if (part != null)
            {
                materials = BuildingMeshUtils.GetModuleMaterials(part);
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   {desc} → レベルフォールバック → mid-{type}{position} 取得成功");
                return part;
            }

            // 位置フォールバック + レベルフォールバック
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

        // 角パーツはどこにも用意されていない構成が正常系（角なし＝壁を辺いっぱいに敷く）なので警告しない。
        if (type == "c")
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[MBB]   {desc} → 角パーツなし（角を描画せず壁を端まで敷く）");
        }
        else
        {
            Debug.LogWarning($"[MBB]   {desc} → すべてのフォールバック失敗 (パーツなし)");
        }
        materials = null;
        return null;
    }

    /// <summary>
    /// SegmentMeshData を CombineInstance リストに追加する。
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
                transform = Matrix4x4.identity
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
        temporaryMeshes.Add(segment.Mesh);
    }

    /// <summary>
    /// 角パーツを頂点 p1 に配置したとき、隣接する辺の方向へ実際に張り出す長さ(m)を返す。
    /// 壁パーツの敷き詰め開始・終了位置（＝角パーツの終わり）を決めるために使う。
    ///
    /// ローカルX/Z軸のAABBを測るのではなく、**実際の配置と同じ回転・スケールを掛けた頂点を
    /// 辺方向へ射影した最大値**を採る。これにより
    /// - アームが +X/+Z・-X/-Z どちらの向きで著作されていても正しい値になる
    /// - 庇や胴差しのような辺方向への造作も張り出しに含まれる
    /// - 角パーツが無い場合は 0（＝辺いっぱいに壁を敷く）になる
    /// ため、「壁が角パーツにめり込む／角パーツから離れる」事故が原理的に起きない。
    ///
    /// ボーン変形（GenerateSkinnedCorner）で角を開閉する場合でも、アームは辺と一緒に
    /// リジッド回転する。そこで射影先には**バインドポーズ（内角90°）での辺の向き**
    /// （二等分線から ±45°）を使う。変形前のメッシュを変形前の辺方向へ射影した値は、
    /// 変形後のメッシュを実際の辺方向へ射影した値と一致するため、変形の実装詳細に依存しない。
    /// </summary>
    /// <param name="towardNext">true=送り側（p1→p2 の辺）、false=受け側（p0→p1 の辺）への張り出しを測る</param>
    private static float MeasureCornerExtentAlongEdge(
        GameObject cornerModule, Vector2 p0, Vector2 p1, Vector2 p2, float targetHeight, bool towardNext)
    {
        if (cornerModule == null) return 0f;

        // 実際の配置（GenerateCornerCore）は SkinnedMeshRenderer を優先して使うため、計測もそれに揃える。
        Mesh sourceMesh = GetPartSourceMesh(cornerModule);
        if (sourceMesh == null || sourceMesh.vertexCount == 0) return 0f;

        Quaternion rotation = ComputeCornerRotation(p0, p1, p2, out Vector2 bisector, out _);
        Vector3 scale = DetermineCornerScale(sourceMesh, targetHeight);

        // バインドポーズでの辺方向（二等分線から ±45°）。符号は「送り側の辺が二等分線の
        // どちら回りにあるか」で決まるので、ポリゴンの巻き方向に依存せず求まる。
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
    /// 張り出し計測用の頂点配列キャッシュ。`Mesh.vertices` は呼ぶたびに配列を複製するため、
    /// 階数×頂点数ぶん繰り返す計測ではGCを圧迫する。対象はプレハブアセットの共有メッシュで、
    /// 破棄されず内容も不変なのでメッシュ単位でキャッシュしてよい。
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
    /// 角パーツの配置回転（Y軸まわり）を求める。配置（GenerateCornerCore）と
    /// 張り出し計測（MeasureCornerExtentAlongEdge）で必ず同じ値を使うための共通処理。
    ///
    /// 2辺の二等分線方向へ角パーツの正面を向ける。末尾の +135 は角パーツの著作向きに合わせた
    /// オフセット（回転0でエクスポートされた角パーツはローカルの正面が Y 軸まわりに 135° ずれている）。
    /// ボーン曲げはこの rotation とは独立に行われるため、この 135° は曲げに影響せず
    /// 曲げ後の角全体の向きだけを補正する。
    /// </summary>
    /// <param name="bisector">2辺の二等分線（単位ベクトル）。退化時は辺 v2 の垂線</param>
    /// <param name="interiorAngle">内角(度)。0〜360</param>
    private static Quaternion ComputeCornerRotation(
        Vector2 p0, Vector2 p1, Vector2 p2, out Vector2 bisector, out float interiorAngle)
    {
        Vector2 v1 = (p0 - p1).normalized;
        Vector2 v2 = (p2 - p1).normalized;

        interiorAngle = Vector2.SignedAngle(v2, v1);
        if (interiorAngle < 0f) interiorAngle += 360f;

        // 内角が 180°付近（2辺がほぼ一直線）だと v1+v2 がほぼ 0 になり normalized が
        // NaN 化して回転が暴れる。その場合は辺 v2 に垂直な向きでフォールバックする。
        Vector2 bisectorRaw = v1 + v2;
        bisector = bisectorRaw.sqrMagnitude < 1e-6f
            ? new Vector2(-v2.y, v2.x)
            : bisectorRaw.normalized;

        float bisectorAngle = Mathf.Atan2(bisector.y, bisector.x) * Mathf.Rad2Deg;
        return Quaternion.Euler(0, -bisectorAngle + 90 + 135, 0);
    }

    /// <summary>辺の長さ・敷き詰め有効長を「実質ゼロ」とみなす閾値(m)。</summary>
    private const float MIN_WALL_RUN_LENGTH = 0.01f;

    /// <summary>
    /// 辺セグメントのメッシュを生成する。
    /// 純粋に配置計算のみを行い、個別のユニットメッシュを返す（結合は行わない）。
    ///
    /// スケール規則: 奥行き(Z)は常に著作サイズのまま。高さ(Y)は targetHeight が正の場合だけ
    /// そこへ合わせ（中層の階・工事中の足場）、NO_Y_SCALE のときは著作高さのまま
    /// （1階・最上階・フェンス）。パーツの著作幅を1ユニットとして辺に敷き詰め、端数はXストレッチで
    /// 均等に吸収する。
    /// </summary>
    /// <param name="startOffset">辺の始点側で角パーツが占める長さ(m)。MeasureCornerExtentAlongEdge で求める</param>
    /// <param name="endOffset">辺の終点側で角パーツが占める長さ(m)</param>
    public static List<SegmentMeshData> GenerateWallSegments(
        Vector2 p0, Vector2 p1, float floorY,
        GameObject wallModule, Material[] sharedMaterials,
        float targetHeight,
        float startOffset, float endOffset)
    {
        return BuildWallRun(p0, p1, floorY, wallModule, sharedMaterials, targetHeight, startOffset, endOffset);
    }

    /// <summary>
    /// フェンス用の辺セグメントメッシュを生成する。
    /// 壁パーツの元のサイズを保ち、Y方向にはスケールしない（著作実寸のまま）。
    /// 敷き詰めと端数の吸収は建物の壁と同じロジック（BuildWallRun）を使う。
    /// </summary>
    /// <param name="startOffset">辺の始点側でフェンス角パーツが占める長さ(m)</param>
    /// <param name="endOffset">辺の終点側でフェンス角パーツが占める長さ(m)</param>
    public static List<SegmentMeshData> GenerateFenceSegments(
        Vector2 p0, Vector2 p1, GameObject wallModule, Material[] sharedMaterials,
        float startOffset, float endOffset)
    {
        return BuildWallRun(p0, p1, 0f, wallModule, sharedMaterials, NO_Y_SCALE, startOffset, endOffset);
    }

    /// <summary>
    /// 壁系パーツ（base/mid/top-w・fence-w・scaffold-wall）を1辺に敷き詰める共通処理。
    ///
    /// 配置の考え方:
    /// 1. 辺の全長から両端の角パーツが占める長さ（startOffset / endOffset）を引いて「有効長」を求める。
    ///    壁は角パーツが終わったところから始まり、次の角パーツが始まるところで終わる。
    /// 2. 有効長をパーツの著作幅で割り、**四捨五入**で敷き詰め数を決める。切り捨てだと端数を
    ///    引き伸ばす方向にしか吸収できず最大2倍まで間延びするため、縮小側も使って歪みを抑える。
    /// 3. 有効長を敷き詰め数でちょうど割った幅を1スロットとし、X方向スケールでパーツをスロットへ
    ///    合わせ込む。したがって合計は常に有効長と厳密に一致し、はみ出しも隙間も生じない。
    /// 4. 各ユニットは**メッシュのX範囲がスロットに一致するよう**配置する。ピボットが幅の中央から
    ///    ずれた（左右非対称な）パーツでも端がずれない。中央ピボットの標準パーツでは
    ///    スロット中央に原点が来て従来と同じ結果になる。
    ///
    /// targetHeight が 0 以下の場合はYスケールを行わない（1階・最上階の壁・フェンス）。
    /// 正の値を渡すのは中層の階と工事中の足場で、その場合のみ高さを targetHeight へ合わせる。
    /// </summary>
    private static List<SegmentMeshData> BuildWallRun(
        Vector2 p0, Vector2 p1, float baseY,
        GameObject wallModule, Material[] sharedMaterials,
        float targetHeight,
        float startOffset, float endOffset)
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

        // 角パーツが両端で占める分を差し引いた、壁を敷き詰める有効長。
        float clampedStart = Mathf.Max(0f, startOffset);
        float clampedEnd = Mathf.Max(0f, endOffset);
        float effectiveLength = totalLength - (clampedStart + clampedEnd);

        // 辺が両端の角パーツで埋まってしまう短い辺では壁を置かない。
        // 置くと角パーツへめり込み、マイナススケールでメッシュが反転する。
        if (effectiveLength <= MIN_WALL_RUN_LENGTH)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[MBB-WALL] 有効長なし（角パーツで辺が埋まる）: totalLength={totalLength:F3} startOffset={clampedStart:F3} endOffset={clampedEnd:F3}");
            return segments;
        }

        WarnUndersizedPartOnce(sourceMesh);

        // 高さは targetHeight が正のときだけそこへ合わせ、幅・奥行きは常に著作サイズを維持する。
        // targetHeight <= 0（建物本体の壁・フェンス）は著作高さのまま描画する。
        float sourceHeight = Mathf.Max(originalSize.y, 0.001f);
        float scaleY = targetHeight > 0f ? targetHeight / sourceHeight : 1f;
        float unitActualLength = originalSize.x;

        // ユニット幅が無効(ほぼ0)だと配置数の計算が発散するため、描画をスキップする
        if (unitActualLength <= 0.001f)
        {
            Debug.LogWarning($"[MBB] 壁パーツのユニット幅が無効です (mesh='{sourceMesh.name}', unitActualLength={unitActualLength:F5})。配置をスキップします。");
            return segments;
        }

        // 四捨五入で敷き詰め数を決めることで、有効長が著作幅の 1.5 倍なら 2 枚を縮めて敷く。
        // 最低1枚は必ず置く（隙間を作らないことを優先し、極端に短い辺では潰れた1枚になる）。
        int numUnits = Mathf.Max(1, Mathf.RoundToInt(effectiveLength / unitActualLength));

        // スロット幅は有効長を敷き詰め数でちょうど割った値。合計は厳密に有効長に一致する。
        float slotLength = effectiveLength / numUnits;
        float xStretchFactor = slotLength / unitActualLength;
        float scaleX = xStretchFactor;
        Vector3 scale = new Vector3(scaleX, scaleY, 1f);

        // 辺の方向（direction）に合わせて、Y軸まわりの回転角度を計算する。
        // Quaternion.FromToRotation は対向方向での特異点（Y軸反転）が発生するため、
        // Atan2 を用いて常に上向き（Y軸）を維持した水平回転を計算する。
        float angle = Mathf.Atan2(direction.z, direction.x) * Mathf.Rad2Deg;
        // 壁系パーツ（base/mid/top-w・fence-w・scaffold-wall）は、著作モデルの向きに合わせて
        // 辺方向へ向けたうえで Y 軸まわりに 180° 反転して配置する。
        // 角パーツ（*-c、GenerateCornerCore）は二等分線ベースの向き決定なので対象外。
        Quaternion rotation = Quaternion.Euler(0, -angle + 180f, 0);

        // 180°反転配置によりローカル +X 側が辺の始点側に来る。原点をスロット始端から
        // bounds.max.x ぶん進めると、メッシュのX範囲がスロットにちょうど収まる。
        // 幅中央ピボットの標準パーツでは bounds.max.x = 幅/2 なのでスロット中央になる。
        float pivotShift = bounds.max.x * scaleX;

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[MBB-WALL] p0=({p0.x:F3},{p0.y:F3}) p1=({p1.x:F3},{p1.y:F3}) baseY={baseY:F3} targetHeight={targetHeight:F3}\n" +
            $"[MBB-WALL]   totalLength={totalLength:F4} startOffset={clampedStart:F4} endOffset={clampedEnd:F4} effectiveLength={effectiveLength:F4}\n" +
            $"[MBB-WALL]   meshName={sourceMesh.name} boundsSize=({originalSize.x:F4},{originalSize.y:F4},{originalSize.z:F4}) boundsCenterX={bounds.center.x:F4}\n" +
            $"[MBB-WALL]   unitActualLength={unitActualLength:F4} numUnits={numUnits} slotLength={slotLength:F4} xStretchFactor={xStretchFactor:F4}\n" +
            $"[MBB-WALL]   finalScale=({scale.x:F4},{scale.y:F4},{scale.z:F4}) pivotShift={pivotShift:F4}"
        );

        for (int i = 0; i < numUnits; i++)
        {
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

            Vector3 unitPos = start + direction * (clampedStart + i * slotLength + pivotShift);
            Matrix4x4 matrix = Matrix4x4.TRS(unitPos, rotation, scale);

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
    /// フェンス用の角セグメントメッシュを生成する。
    /// 元のメッシュサイズのまま一切スケールせずに配置する。
    /// </summary>
    public static SegmentMeshData GenerateFenceCorner(Vector2 p0, Vector2 p1, Vector2 p2, GameObject cornerModule, Material[] sharedMaterials)
    {
        // 本体角と同じロジック。フェンスは floorY=0・無スケールで配置する。
        return GenerateCornerCore(p0, p1, p2, 0f, cornerModule, sharedMaterials, NO_Y_SCALE);
    }

    /// <summary>
    /// 工事中建物の足場を生成する。**通常建物の壁・角と全く同じ配置ロジック**
    /// （GenerateWallSegments / GenerateCornerSegment / MeasureCornerExtentAlongEdge）を、
    /// 建物フットプリント上でそのまま使い回す。角パーツのヒンジ（原点）はフットプリント頂点に
    /// 一致し、壁は両端を角パーツの張り出しぶん空けて配置される。オフセットは行わない。
    /// フェンスと違い各階を targetHeight(階高)へYスケールし、builtFloors 段ぶん生成する。
    /// wallModule / cornerModule は全辺・全頂点で共通の単一パーツ。
    /// </summary>
    /// <param name="footprint">建物底面の頂点列（CCW、ローカル座標）</param>
    /// <param name="wallModule">scaffold-wall パーツ。null の場合は何も生成しない</param>
    /// <param name="cornerModule">scaffold-corner パーツ。null 可（壁のみになる）</param>
    /// <param name="floorHeight">1階あたりの高さ(m)。パーツはこの高さへY方向スケールされる</param>
    /// <param name="builtFloors">積む段数（施工済み階数）</param>
    public static void GenerateScaffoldSegments(
        Vector2[] footprint,
        GameObject wallModule, Material[] wallMaterials,
        GameObject cornerModule, Material[] cornerMaterials,
        float floorHeight, int builtFloors,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        if (footprint == null || footprint.Length < 3 || wallModule == null) return;
        int n = footprint.Length;

        for (int level = 0; level < builtFloors; level++)
        {
            float floorY = level * floorHeight;

            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = footprint[(i + n - 1) % n];
                Vector2 p1 = footprint[i];
                Vector2 p2 = footprint[(i + 1) % n];
                Vector2 p3 = footprint[(i + 2) % n];

                // 壁の両端に空ける角パーツの張り出し量。全頂点で同一パーツだが、張り出しは
                // 内角に依存する（角パーツはボーンで開閉する）ため頂点ごとに測る。
                float startOffset = MeasureCornerExtentAlongEdge(cornerModule, p0, p1, p2, floorHeight, true);
                float endOffset = MeasureCornerExtentAlongEdge(cornerModule, p1, p2, p3, floorHeight, false);

                // 壁: p1 → p2（通常建物と同じ GenerateWallSegments。両端を角の張り出しぶん空ける）
                List<SegmentMeshData> wallSegments = GenerateWallSegments(
                    p1, p2, floorY, wallModule, wallMaterials, floorHeight, startOffset, endOffset);
                foreach (var seg in wallSegments)
                {
                    if (seg.IsValid) AddSegmentToCombineList(seg, combineInstances, allMaterials, temporaryMeshes);
                }

                // 角: フットプリント頂点 p1 にヒンジ（原点）を合わせて配置
                //     （通常建物と同じ GenerateCornerSegment。内角に応じたボーン変形もそのまま効く）
                if (cornerModule != null)
                {
                    SegmentMeshData corner = GenerateCornerSegment(p0, p1, p2, floorY, cornerModule, cornerMaterials, floorHeight);
                    if (corner.IsValid) AddSegmentToCombineList(corner, combineInstances, allMaterials, temporaryMeshes);
                }
            }
        }
    }

    /// <summary>
    /// 角セグメントのメッシュを生成する。
    /// </summary>
    public static SegmentMeshData GenerateCornerSegment(Vector2 p0, Vector2 p1, Vector2 p2, float floorY, GameObject cornerModule, Material[] sharedMaterials, float targetHeight)
    {
        // 本体角は floorY を基準に、高さが targetHeight になるように配置する。
        return GenerateCornerCore(p0, p1, p2, floorY, cornerModule, sharedMaterials, targetHeight);
    }

    /// <summary>
    /// 角パーツに適用するスケールを決定する。
    /// - targetHeight が 0 以下 … 無スケール（1階・最上階の角、フェンス角。著作高さのまま描画）
    /// - targetHeight が正 … 高さが targetHeight になるようY方向のみスケール（中層の階、工事中の足場）。
    ///   XZは常に著作サイズを維持する。
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
    /// 角セグメントメッシュ生成の共通処理。本体角（cornerY=floorY, 高さ=targetHeight）と
    /// フェンス角（cornerY=0, targetHeight=0=無スケール）の両方から呼ばれる。
    /// bisector（二等分線）から角パーツの向きを決定し、SkinnedMeshRenderer を持つ場合は
    /// ボーンで任意角に開閉、そうでなければ TRS で配置する。
    /// </summary>
    private static SegmentMeshData GenerateCornerCore(
        Vector2 p0, Vector2 p1, Vector2 p2, float cornerY,
        GameObject cornerModule, Material[] sharedMaterials, float targetHeight)
    {
        if (cornerModule == null) return default;

        Vector3 corner = new Vector3(p1.x, cornerY, p1.y);

        // 向きは MeasureCornerExtentAlongEdge と共通の計算を使う（壁の敷き詰め開始位置は
        // この回転を前提に角パーツの張り出しを測るため、両者がずれると壁と角がずれる）。
        Quaternion rotation = ComputeCornerRotation(p0, p1, p2, out _, out float interiorAngle);

        if (interiorAngle > 180f)
        {
            // 凹角（内角 > 180°）。角パーツは凸角前提で作られているため見た目が乱れる
            // 可能性があるが、現状は補正せず警告のみに留める（意図的な挙動）。
            Debug.LogWarning($"ModularBodyBuilder: 内角が180°を超えています ({interiorAngle:F1}°)。凹多角形の可能性があります。");
        }

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

    /// <summary>
    /// スキンメッシュの角を生成（ボーン変形対応）。
    /// 建物の底面形状の角度に合わせてボーンを回転させる。
    /// scale は DetermineCornerScale で決定済みのスケール（無スケール、または足場のYのみスケール）。
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
            Transform bone0 = smr.bones[0];
            Transform bone1 = smr.bones[1];

            float halfAngleDiff = (angle - 90f) * 0.5f;

            // ボーン曲げは localRotation（親基準の premultiply）ではなく、ワールド回転で行う。
            // 各ボーンの head（付け根）を軸に、ワールドY軸まわりで各アームを独立に回すことで、
            // ボーンが兄弟でも親子チェーンでも左右対称に開閉する（階層非依存）。
            // 内角 90° では halfAngleDiff=0 で回転しないため、従来どおり無変形。
            Vector3 head0Before = bone0.position;
            Vector3 head1Before = bone1.position;

            bone0.rotation = Quaternion.AngleAxis(halfAngleDiff, Vector3.up) * bone0.rotation;
            bone1.rotation = Quaternion.AngleAxis(-halfAngleDiff, Vector3.up) * bone1.rotation;

            // 親子チェーンのリグでは bone0 の回転で子である bone1 の head が動いてしまい、
            // アームの付け根が頂点からずれて壁との継ぎ目に隙間が出る。各 head を曲げ前の
            // 位置（＝バインドポーズの付け根）へ再アンカーしてこれを防ぐ。
            // 兄弟リグでは head は動かないため実質 no-op。
            Vector3 head1Displaced = bone1.position;
            bone0.position = head0Before;
            bone1.position = head1Before;

            if (LocalConstants.UNITY_LOG_BUILDING)
            {
                // 診断: bone1 head が bone0 回転で動いたか（チェーンリグ検出）と再アンカー量。
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
