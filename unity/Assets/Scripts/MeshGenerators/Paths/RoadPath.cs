using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;
using System;
using GeneratedModels;

/// <summary>
/// 車道向けのメッシュ生成 (Dreamteck Splines使用)
/// </summary>
public class RoadPath : IPathMeshGenerator
{
    private static readonly Dictionary<string, Material> MaterialCache = new Dictionary<string, Material>();
    private static readonly Dictionary<Mesh, Mesh> MirroredMeshCache = new Dictionary<Mesh, Mesh>();
    private static readonly Dictionary<(Mesh, float), Mesh> PartMeshCache = new Dictionary<(Mesh, float), Mesh>();
    private static readonly Dictionary<Mesh, Mesh> BodyMeshCache = new Dictionary<Mesh, Mesh>();
    private static readonly Dictionary<Mesh, Bounds> BodySurfaceBoundsCache = new Dictionary<Mesh, Bounds>();
    /// <summary>歩道コネクタが写すパーツの頂点・UV・三角形。キーは共有プレハブ由来の不変メッシュなので読むだけに使う。</summary>
    private static readonly Dictionary<Mesh, (Vector3[] vertices, Vector2[] uv, int[][] triangles)> ConnectorSourceCache
        = new Dictionary<Mesh, (Vector3[], Vector2[], int[][])>();

    // Extrude配置の1タイルの最大長（m）。
    // SplineMesh はタイル内の頂点Z位置でしかスプラインを評価しないため、
    // Z方向の分割を持たないプレハブ（bodyは10mの箱）をそのままタイルにすると、
    // 縦カーブ（尾根・谷）でタイル全体が弦（直線）になり、細かく分割される他パーツ
    // （ラインなど）と上下にずれて貫通・浮きが発生する。タイル長を制限して弦誤差を抑える。
    public const float MAX_EXTRUDE_TILE_LENGTH = 1f;

    /// <summary>
    /// SplineMesh 1個あたりの生成頂点数の上限。Unity標準メッシュの上限(65535頂点)を超えると
    /// Dreamteck が毎フレームエラーを出し続けるため、余裕を持たせた値で count をクランプする。
    /// </summary>
    public const int SPLINE_MESH_VERTEX_BUDGET = 60000;

    /// <summary>
    /// 道路パーツ用のマテリアルをキャッシュ経由で取得する。
    /// FBX由来のマテリアルを、同名の Resources マテリアルがあればそちらへ再割当し、
    /// Zファイティング回避の renderQueue+1 を一度だけ適用する。
    /// 交差点メッシュ・歩道コネクタなど、道路と見た目を揃えたいすべての生成器から使うこと
    /// （プレハブの sharedMaterials を直接使うと、通常道路とマテリアルの解決結果がずれる）。
    /// </summary>
    /// <summary>
    /// 索道系バリアント(索道・索道駅)かを返す。索道は線形検証・路面高さ補正・駅殻生成で
    /// 道路と別扱いになるため、判定を一箇所に集約する。
    /// </summary>
    public static bool IsRopewayVariant(int variant)
        => variant == Constants.PATH_VARIANT_ROPEWAY || variant == Constants.PATH_VARIANT_ROPEWAY_STATION;

    public static Material GetOrCreateRoadMaterial(Material source)
    {
        if (source == null) return null;
        string key = source.name;

        Material fromResources = GetOrCreateRoadMaterialByName(key);
        if (fromResources != null) return fromResources;

        // Resources に同名マテリアルが無いので、FBX由来のマテリアルを複製して共有プールに載せる。
        Material targetMat = new Material(source);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   マテリアルキャッシュ生成(clone): '{key}'");
        RegisterRoadMaterial(key, targetMat);
        return targetMat;
    }

    /// <summary>
    /// マテリアル名だけを手がかりに、道路パーツ用のマテリアルをキャッシュ経由で取得する。
    /// マスターデータで路面のマテリアルを名指しする経路（交差点アスファルトの body_material）で使う。
    /// Resources 直下に同名の .mat が無ければ null を返す（呼び出し側でフォールバックすること）。
    /// </summary>
    public static Material GetOrCreateRoadMaterialByName(string materialName)
    {
        if (string.IsNullOrEmpty(materialName)) return null;
        if (MaterialCache.TryGetValue(materialName, out Material cached))
            return cached;

        Material loadedMat = Resources.Load<Material>(materialName);
        if (loadedMat == null) return null;

        Material targetMat = new Material(loadedMat);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   マテリアルキャッシュ生成(Resources): '{materialName}'");
        RegisterRoadMaterial(materialName, targetMat);
        return targetMat;
    }

    private static void RegisterRoadMaterial(string key, Material material)
    {
        int baseQueue = material.renderQueue < 0 ? material.shader.renderQueue : material.renderQueue;
        material.renderQueue = baseQueue + 1;
        MaterialCache[key] = material;
    }

    private static Material GetFallbackRoadMaterial()
    {
        // URP対応のアセットを共有プール経由で使い、ビルド時のシェーダー参照も保持する。
        return GetOrCreateRoadMaterialByName("BuildingMaterial");
    }

    /// <summary>
    /// 路面幅は桁の張り出しを含む全体 bounds ではなく、最上面の幅から求める。
    /// 引数は共有プレハブ由来の不変メッシュなので、メッシュごとに1回だけ頂点を走査する。
    /// </summary>
    public static Bounds GetBodySurfaceBounds(Mesh mesh)
    {
        if (BodySurfaceBoundsCache.TryGetValue(mesh, out Bounds cached)) return cached;
        Bounds result = ComputeBodySurfaceBounds(mesh);
        BodySurfaceBoundsCache[mesh] = result;
        return result;
    }

    private static Bounds ComputeBodySurfaceBounds(Mesh mesh)
    {
        Bounds bounds = mesh.bounds;
        float left = float.PositiveInfinity, right = float.NegativeInfinity;
        foreach (var vertex in mesh.vertices)
        {
            if (bounds.max.y - vertex.y > 0.002f) continue;
            left = Mathf.Min(left, vertex.x);
            right = Mathf.Max(right, vertex.x);
        }
        if (right - left < 0.001f) return bounds;
        return new Bounds(new Vector3((left + right) * 0.5f, bounds.max.y, bounds.center.z),
            new Vector3(right - left, 0f, bounds.size.z));
    }

    private static Mesh GetBodySurfaceMesh(Mesh source)
    {
        if (BodyMeshCache.TryGetValue(source, out var cached)) return cached;
        var mesh = UnityEngine.Object.Instantiate(source);
        // FBX の微小な原点誤差も吸収し、交差点と共有する路面の高さを y=0 に揃える。
        var vertices = mesh.vertices;
        float surfaceY = source.bounds.max.y;
        for (int i = 0; i < vertices.Length; i++) vertices[i].y -= surfaceY;
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
        BodyMeshCache[source] = mesh;
        return mesh;
    }

    private static Mesh GetOrCreateMirroredMesh(Mesh originalMesh)
    {
        if (originalMesh == null) return null;
        Mesh key = originalMesh;
        if (MirroredMeshCache.TryGetValue(key, out Mesh cached))
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   ミラーメッシュキャッシュヒット: '{key}'");
            return cached;
        }

        Mesh mirrored = CreateMirroredMeshInternal(originalMesh);
        MirroredMeshCache[key] = mirrored;
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   ミラーメッシュキャッシュ生成: '{key}'");
        return mirrored;
    }

    /// <summary>同じモデル系列の路面原点を使い、付属パーツに含まれる路面高を二重加算しない。</summary>
    public static Mesh GetRoadPartMesh(Mesh source, string resourcePath)
    {
        if (source == null) return null;
        string part = resourcePath.Substring(resourcePath.LastIndexOf('/') + 1);
        // 接写パーツ(sidewalk_close 等)も無印と同じ路面原点で扱う。
        if (part.EndsWith(PathDrawContext.CLOSE_SUFFIX, StringComparison.Ordinal))
        {
            part = part.Substring(0, part.Length - PathDrawContext.CLOSE_SUFFIX.Length);
        }
        if (part != "sidewalk" && part != "line") return source;
        string bodyPath = resourcePath.Substring(0, resourcePath.LastIndexOf('/') + 1) + "body";
        var body = ModelManager.Instance?.GetModel(bodyPath)?.GetComponent<MeshFilter>()?.sharedMesh;
        float surfaceY = body != null ? body.bounds.max.y : 0f;
        if (Mathf.Abs(surfaceY) < 1e-6f) return source;
        var key = (source, surfaceY);
        if (PartMeshCache.TryGetValue(key, out var cached)) return cached;
        var mesh = UnityEngine.Object.Instantiate(source);
        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++) vertices[i].y -= surfaceY;
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
        PartMeshCache[key] = mesh;
        return mesh;
    }

    public PathMeshResult Generate(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] Generate開始: path_id={pathData?.path_id ?? -1}, path_variant={pathData?.path_variant ?? -1}, path_type={pathData?.path_type ?? -1}, points={points?.Count ?? 0}");

        if (points == null || points.Count < 2)
        {
            Debug.LogWarning($"[RoadPath] ポイント不足 ({points?.Count ?? 0}件)。生成スキップ。");
            return new PathMeshResult(null, false);
        }

        bool isCurved = pathData.curved;
        bool hasWaypoints = pathData.GetWaypointList() != null && pathData.GetWaypointList().Count > 0;

        // パラメータ取得（single_span 判定のために地形追従処理より前に取得する）
        GetPathMasterParams(pathData, out float width, out float sidewalkWidth, out float sideWidth, out float? treeSpacing, out int lanes, out float wallWidth, out float centerWidth, out float? pillarSpacing, out bool heightEditable, out float baseHeight, out bool singleSpan);

        // single_span の場合、曲線パスもアンカー点のみの直線セグメント化して扱う。
        // body がアンカー点(柱)間の直線スパンになるよう、地形追従せずアンカー点だけでスプラインを構築する。
        if (singleSpan && isCurved)
        {
            points = ExtractAnchorPointsForSingleSpan(pathData, points);
            isCurved = false;
        }

        if (!isCurved && hasWaypoints)
        {
            return GenerateLinearPathWithCorners(pathData, points, startSidewalkSetback, endSidewalkSetback, singleSpan);
        }

        // 直線パス（ウェイポイントなし）の場合、制御点が始点・終点のみで
        // 中間点が存在しないため地形追従が機能しない。
        // 一定間隔で中間点を補間し、地形高さを反映させる。
        // single_span の場合は地形追従せず、アンカー点をそのままスプライン制御点にする。
        if (!isCurved && !singleSpan && points.Count == 2)
        {
            var startList = pathData.GetStartPointList();
            var endList = pathData.GetEndPointList();
            float startRelY = (startList != null && startList.Count >= 3) ? startList[1] : 0f;
            float endRelY = (endList != null && endList.Count >= 3) ? endList[1] : 0f;
            bool followTerrain = PathManager.GetPathFollowTerrain(pathData)
                && startRelY == 0f && endRelY == 0f;
            points = PathManager.SubdividePointsWithTerrain(points, PathManager.TERRAIN_SAMPLE_INTERVAL, followTerrain);
        }

        // コンテナ作成
        GameObject container = new GameObject($"Road_{pathData.path_id}");
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] コンテナ作成: {container.name}");

        SplineComputer spline = BuildSpline(container, points, isCurved);

        float splineLength = spline.CalculateLength();
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] スプライン長: {splineLength:F2}m");
        PathDrawContext.SetAdaptiveTileLength(PathDrawContext.ComputeAdaptiveTileLength(spline, MAX_EXTRUDE_TILE_LENGTH));

        // 道路パーツ生成
        CreateRoadParts(container, spline, width, sidewalkWidth, sideWidth, lanes, treeSpacing, wallWidth, centerWidth, pillarSpacing, heightEditable, baseHeight, singleSpan, pathData, points, startSidewalkSetback, endSidewalkSetback);

        // コライダー用メッシュの生成
        Mesh colliderMesh = BuildColliderMeshFromSpline(spline, width);
        if (colliderMesh != null)
        {
            colliderMesh.name = $"RoadCollider_{pathData.path_id}";
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] コライダーメッシュ生成成功: 頂点数={colliderMesh.vertexCount}");
        }
        else
        {
            Debug.LogWarning("[RoadPath] コライダーメッシュ生成失敗");
        }

        int childCount = container.transform.childCount;
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] Generate完了: 子オブジェクト数={childCount}, colliderMesh={(colliderMesh != null ? "あり" : "なし")}");

        // デバッグ用: 生成直後のメッシュ状態を確認
        if (LocalConstants.UNITY_LOG_ROAD)
        {
            var allRenderers = container.GetComponentsInChildren<Renderer>(true);
            if (allRenderers.Length == 0)
            {
                Debug.LogWarning($"[RoadPath] Generate直後: containerにRendererが1件もありません。メッシュが生成されていないか、SplineMeshの遅延生成が原因の可能性があります。");
            }
            else
            {
                Bounds totalBounds = allRenderers[0].bounds;
                for (int i = 1; i < allRenderers.Length; i++) totalBounds.Encapsulate(allRenderers[i].bounds);
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] Generate直後: Renderer={allRenderers.Length}件, bounds中心={totalBounds.center}, boundsサイズ={totalBounds.size}");
            }
        }

        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>
    /// SplineMeshコンポーネントを使用して、パスに沿ってメッシュを生成します。
    /// RoadPath（道路）と TrackPath（線路）が共用するパーツ配置の共通処理。
    /// </summary>
    /// <param name="centerOffsetX">
    /// パーツの中心を置く、スプライン中心線からの横方向距離。
    /// プレハブメッシュのピボットが中心・端どちらにあっても、メッシュ bounds の中心が
    /// この位置に来るよう内部で補正する（ピボット位置の規約差を吸収するため）。
    /// </param>
    /// <param name="mirrorX">trueの場合、メッシュのX軸を反転させます（左右対称用）</param>
    /// <param name="isDashed">trueの場合、Placeモードで等間隔配置し点線として表示します</param>
    /// <param name="stretchToWidth">
    /// trueの場合、メッシュのX幅が width に一致するよう引き伸ばす（道路の body 等、幅可変のパーツ用）。
    /// falseの場合、Xスケールは常に1（実寸配置）。レール（軌間が変わってはいけない）や架線のように
    /// 断面寸法が固定のパーツで使う。width はピボット補正には使われず実寸のまま配置される。
    /// </param>
    /// <param name="clipFrom">
    /// パーツを配置する区間の始点（スプライン percent）。歩道のエンドキャップ用に、
    /// 交差点側の端だけパーツを短く切り詰めるために使う（Extrude配置のみ。点線には適用しない）。
    /// </param>
    /// <param name="clipTo">パーツを配置する区間の終点（スプライン percent）。</param>
    /// <param name="compensatePivot">
    /// trueの場合、メッシュ bounds の中心が centerOffsetX に来るようピボット位置の規約差を吸収する（既定）。
    /// falseの場合、補正を行わずプレハブの原点（ローカルXの0）を centerOffsetX に置く。
    /// プレハブの原点自体に意味があり、それをスプライン中心線基準の座標としてそのまま使いたい
    /// パーツ（駅のプラットフォーム＝原点が線路中心線）で使う。
    /// </param>
    /// <param name="maxTileLength">
    /// Extrude配置の1タイルの上限長（m）。既定は MAX_EXTRUDE_TILE_LENGTH（弦誤差抑制用）。
    /// 0以下を渡すと上限なし＝プレハブのZ実寸をそのままタイル長にする。縦カーブを持たない
    /// 折れ線スプライン（駅舎パスの連絡橋など）で、造形単位を著作サイズのまま並べたいときに使う。
    /// </param>
    /// <param name="normalizeBodySurface">
    /// body の上面を路面原点Y=0へ揃える。地面原点の駅舎橋は false とし、モデルの高さを保つ。
    /// false のパーツは、スプライン面へ置き換える道路用の区間LODも適用しない。
    /// </param>
    public static GameObject CreateSplineMesh(GameObject parent, SplineComputer spline, string prefabName, float width, float centerOffsetX, float offsetY, bool mirrorX = false, string fallbackPrefabName = null, bool isDashed = false, bool stretchToWidth = true, double clipFrom = 0.0, double clipTo = 1.0, bool singleSpan = false, bool compensatePivot = true, float maxTileLength = MAX_EXTRUDE_TILE_LENGTH, bool normalizeBodySurface = true)
    {
        // 施工中の共通入口でも、索道のケーブルを道路幅へ拡大しない。
        if (prefabName == "Models/Paths/p601/body" || prefabName == "Models/Paths/p602/body")
        {
            stretchToWidth = false;
            normalizeBodySurface = false;
        }
        // 線路のつなぎ目のS字などの変形。区間LODの遅延生成・頂点上限での分割にも同じ変形を引き継ぐ。
        PartWarp warp = PathDrawContext.Warp;

        // プレハブのロード。描画LODが接写なら `_close` を優先し、バリアント→タイプの順で解決する
        // (PathDrawContext.ResolvePrefab)。接写パーツは無印と bbox・原点が一致する規約なので配置計算は共通。
        GameObject prefab = null;
        string resolvedPrefabName = prefabName;
        if (ModelManager.Instance == null)
        {
            Debug.LogWarning($"[RoadPath] ModelManager.Instance が null。プレハブ '{prefabName}' をロードできません。");
        }
        else
        {
            prefab = PathDrawContext.ResolvePrefab(prefabName, fallbackPrefabName, out resolvedPrefabName);
            if (prefab != null && resolvedPrefabName != prefabName)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] '{prefabName}' → '{resolvedPrefabName}' を使用");
            }
        }

        if(prefab == null)
        {
            Debug.LogWarning($"[RoadPath] プレハブなし: '{prefabName}'");
            return null;
        }

        if (normalizeBodySurface && !singleSpan && !isDashed && !PathDrawContext.SuppressDetailRange && spline.CalculateLength() >= 256f
            && parent.GetComponentInParent<SplinePartLod>() == null)
        {
            var group = new GameObject(prefabName + "_LOD");
            group.transform.SetParent(parent.transform, false);
            float adaptive = PathDrawContext.AdaptiveTileLength;
            group.AddComponent<SplinePartLod>().Configure(spline, width, centerOffsetX, offsetY,
                prefabName.EndsWith("/body", StringComparison.Ordinal), GetOrCreateRoadMaterial(prefab.GetComponent<MeshRenderer>()?.sharedMaterial), clipFrom, clipTo,
                (lod, from, to) =>
                {
                    PathDrawContext.Begin(lod, from, to);
                    PathDrawContext.SetAdaptiveTileLength(adaptive);
                    PathDrawContext.Warp = warp;
                    try
                    {
                        return CreateSplineMesh(group, spline, prefabName, width, centerOffsetX, offsetY,
                            mirrorX, fallbackPrefabName, isDashed, stretchToWidth, from, to, singleSpan,
                            compensatePivot, maxTileLength, normalizeBodySurface);
                    }
                    finally { PathDrawContext.End(); }
                });
            return group;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] プレハブ取得成功: '{prefabName}', mirrorX={mirrorX}");

        GameObject go = new GameObject(prefabName);
        go.transform.SetParent(parent.transform, false);

        SplineMesh splineMesh = go.AddComponent<SplineMesh>();
        splineMesh.spline = spline;

        MeshFilter mf = prefab.GetComponent<MeshFilter>();
        MeshRenderer mr = prefab.GetComponent<MeshRenderer>();
        SplineMesh.Channel channel = null;
        float scaleX = stretchToWidth ? width : 1f;
        // メッシュのピボットが中心にない場合の補正量。
        // SplineMesh は頂点座標をピボット基準のままオフセットへ加算するため、
        // ピボットが端にあるメッシュをそのまま配置するとパーツが横にずれる。
        // bounds の中心が centerOffsetX に一致するよう offset を補正して、
        // プレハブごとのピボット規約の違い（中心/端）を吸収する。
        float pivotCompensationX = 0f;
        // 変形の倍率に合わせて動かすピボット補正の基準(bounds 中心 × 横倍率)。
        float warpPivotX = 0f;

        if(mf != null && mf.sharedMesh != null)
        {
             bool isBody = normalizeBodySurface && prefabName.EndsWith("/body", StringComparison.Ordinal);
             Mesh meshToUse = isBody ? GetBodySurfaceMesh(mf.sharedMesh) : GetRoadPartMesh(mf.sharedMesh, resolvedPrefabName);
             Bounds fittingBounds = isBody ? GetBodySurfaceBounds(meshToUse) : meshToUse.bounds;
             UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   メッシュ: '{mf.sharedMesh.name}', bounds={mf.sharedMesh.bounds.size}, vertexCount={mf.sharedMesh.vertexCount}");

             // ミラーリングが必要な場合はメッシュを複製して反転処理を行う
             if (mirrorX)
              {
                  meshToUse = GetOrCreateMirroredMesh(meshToUse);
              }

             // メッシュ付きでチャンネル追加
             channel = splineMesh.AddChannel(meshToUse, prefabName);

             // 元のメッシュ幅から適切なスケールを計算（実寸配置パーツはスケールしない）
             float meshWidth = fittingBounds.size.x;
             if(stretchToWidth && meshWidth > 0.001f)
             {
                 scaleX = width / meshWidth;
             }

             // ピボット補正: ミラー時は bounds 中心のX符号も反転する。
             // compensatePivot=false のパーツ（プラットフォーム）はプレハブ原点をそのまま使うため補正しない。
             float meshCenterX = fittingBounds.center.x;
             if (mirrorX) meshCenterX = -meshCenterX;
             if (compensatePivot) pivotCompensationX = -(meshCenterX * scaleX);
             if (compensatePivot) warpPivotX = meshCenterX * scaleX;
             UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   scaleX計算: meshWidth={meshWidth:F3} → scaleX={scaleX:F3} (targetWidth={width}), pivotCompensationX={pivotCompensationX:F3}");

             MeshRenderer targetMr = go.GetComponent<MeshRenderer>();
             if (targetMr == null) targetMr = go.AddComponent<MeshRenderer>();

            if(mr != null)
            {
                Material[] originalMaterials = mr.sharedMaterials;
                Material[] cachedMaterials = new Material[originalMaterials.Length];

                for (int i = 0; i < originalMaterials.Length; i++)
                {
                    cachedMaterials[i] = GetOrCreateRoadMaterial(originalMaterials[i]);
                }
                targetMr.sharedMaterials = cachedMaterials;
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   マテリアル割当完了: {cachedMaterials.Length}件");
            }
            else
            {
                targetMr.sharedMaterial = GetFallbackRoadMaterial();
                Debug.LogWarning($"[RoadPath]   プレハブにMeshRendererなし。Standardシェーダーのマテリアルを使用。");
            }

             if (go.GetComponent<MeshFilter>() == null) go.AddComponent<MeshFilter>();
        }
        else
        {
            // メッシュなしでチャンネル追加（空のチャンネル）
            channel = splineMesh.AddChannel(prefabName);
            Debug.LogWarning($"[RoadPath]   プレハブ '{prefabName}' にMeshFilter/sharedMeshなし。空チャンネル追加。");
        }

        if (channel != null)
        {
            float offsetX = centerOffsetX + pivotCompensationX;
            splineMesh.offset = new Vector3(offsetX, offsetY, 0f);
            float splineLen = spline.CalculateLength();

            // Z方向の実寸（タイル長・ダッシュ長の計算に使う）
            float meshLenZ = 1.0f;
            float meshCenterZ = 0f;
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.bounds.size.z > 0.001f)
            {
                meshLenZ = mf.sharedMesh.bounds.size.z;
                meshCenterZ = mf.sharedMesh.bounds.center.z;
            }

            if (isDashed)
            {
                // 点線表示: Placeモードで等間隔に配置し、破線の長さを調整する
                channel.type = SplineMesh.Channel.Type.Place;
                channel.autoCount = false;

                const float dashSpacing = 4.0f; // 破線周期（メートル）
                const float dashLength = 2.0f;  // 破線部分の長さ（メートル）

                float scaleZ = dashLength / meshLenZ;

                // Placeモードは配置点にメッシュのピボットを置くため、Z方向にもピボット補正を行い、
                // ダッシュの中心が配置点に一致するようにする（メッシュのピボットが後端にあると、
                // 補正なしでは各ダッシュが配置点から進行方向後ろへずれる）。
                splineMesh.offset = new Vector3(offsetX, offsetY, -(meshCenterZ * scaleZ));

                if (splineLen > dashLength * 2f)
                {
                    // ダッシュ（長さ dashLength）が道路の端から突き出さないよう、
                    // 配置区間を両端から dashLength/2 ずつ内側に制限する。
                    // さらに描画LODの詳細描画区間(PathDrawContext)の外には敷かない。
                    double halfDashPercent = (dashLength * 0.5f) / splineLen;
                    double dashFrom = halfDashPercent;
                    double dashTo = 1.0 - halfDashPercent;
                    if (!PathDrawContext.IntersectDetailRange(dashFrom, dashTo, out dashFrom, out dashTo))
                    {
                        UnityEngine.Object.Destroy(go);
                        return null;
                    }
                    channel.clipFrom = dashFrom;
                    channel.clipTo = dashTo;
                    float usableLen = splineLen * (float)(dashTo - dashFrom);
                    channel.count = Mathf.Max(1, Mathf.FloorToInt(usableLen / dashSpacing) + 1);
                }
                else
                {
                    // ダッシュ2個分もない短い区間は、中央に1個だけ置く
                    channel.clipFrom = 0.5;
                    channel.clipTo = 0.5;
                    channel.count = 1;
                }

                channel.minScale = new Vector3(scaleX, 1f, scaleZ);
                channel.maxScale = new Vector3(scaleX, 1f, scaleZ);
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   点線チャンネル設定: offset=({offsetX:F3}, {offsetY:F3}), count={channel.count}, clip=({channel.clipFrom:F3}〜{channel.clipTo:F3}), scale=({scaleX:F3}, 1, {scaleZ:F3})");
            }
            else
            {
                channel.minScale = new Vector3(scaleX, 1f, 1f);
                channel.maxScale = new Vector3(scaleX, 1f, 1f);

                // 描画LODの詳細描画区間(PathDrawContext)と呼び出し側のクリップ区間の共通部分だけに敷く。
                // 区間外は PathManager が遠景の帯で埋める。single_span(桁橋のスパン)は1枚で
                // 両端を結ぶ造形なので区間で切らない。
                if (!singleSpan)
                {
                    if (!PathDrawContext.IntersectDetailRange(clipFrom, clipTo, out double detailClipFrom, out double detailClipTo))
                    {
                        UnityEngine.Object.Destroy(go);
                        return null;
                    }
                    clipFrom = detailClipFrom;
                    clipTo = detailClipTo;
                }

                // 歩道のエンドキャップ用に端を切り詰める場合は配置区間を制限する。
                // clip 値は呼び出し側で 0〜1 かつ clipFrom < clipTo を保証すること。
                if (clipFrom > 0.0 || clipTo < 1.0)
                {
                    channel.clipFrom = clipFrom;
                    channel.clipTo = clipTo;
                }

                // タイル数は autoCount（メッシュのZ長基準）ではなく、弦誤差を抑えるための
                // 上限タイル長(maxTileLength。既定 MAX_EXTRUDE_TILE_LENGTH)から明示的に決める
                // （同定数のコメント参照）。上限なし(0以下)ならプレハブのZ実寸がタイル長になる。
                // single_span の場合はタイル化せず count=1 とし、メッシュ両端のZ頂点が
                // スプライン始点・終点に写像されることでアンカー点間の弦（直線スパン）になる。
                channel.autoCount = false;
                if (singleSpan)
                {
                    channel.count = 1;
                }
                else
                {
                    // 既定の上限(1m)で呼ばれたパーツは、スプラインの曲率に応じたタイル長へ置き換える
                    // (PathDrawContext.AdaptiveTileLength。直線・平坦なら長く、急カーブ・起伏では1mまで詰まる)。
                    // 一律1mだと直線道路でも著作長の数倍の頂点を敷き、道床のような密なパーツで頂点上限に当たる。
                    float tileLimit = maxTileLength;
                    if (maxTileLength > 0f && Mathf.Approximately(maxTileLength, MAX_EXTRUDE_TILE_LENGTH)
                        && PathDrawContext.AdaptiveTileLength > 0f)
                    {
                        tileLimit = PathDrawContext.AdaptiveTileLength;
                    }
                    float tileLen = tileLimit > 0f ? Mathf.Min(meshLenZ, tileLimit) : meshLenZ;
                    float clippedLen = splineLen * (float)System.Math.Max(0.0, clipTo - clipFrom);
                    channel.count = Mathf.Max(1, Mathf.RoundToInt(clippedLen / tileLen));
                }
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath]   チャンネル設定: offset=({offsetX:F3}, {offsetY:F3}), scale={scaleX:F3}, count={channel.count} (singleSpan={singleSpan}, clip={clipFrom:F3}〜{clipTo:F3})");
            }

            // 変形はスプライン割合ごとのずれと倍率としてチャンネルへ渡す(Dreamteck が断面ごとに評価する)。
            if (warp != null && (warp.Shift != null || warp.Scale != null))
            {
                var shift = warp.Shift;
                var scale = warp.Scale;
                float pivot = warpPivotX;
                if (scale != null) channel.scaleHandler = percent => new Vector3(scale(percent), 1f, 1f);
                channel.offsetHandler = percent => new Vector2(
                    (shift != null ? shift(percent) : 0f) - (scale != null ? pivot * (scale(percent) - 1f) : 0f), 0f);
            }

            // ===== 生成前の頂点数バジェットガード =====
            // SplineMesh の生成頂点数は「パーツの頂点数 × タイル数(count)」で決まる。
            // Z実寸が極端に小さいFBX(cm単位エクスポート等)や高密度なFBXに差し替えられると
            // count×頂点数が爆発し、Unity標準メッシュの上限(65535頂点)を超えて Dreamteck が
            // 毎フレームエラーを出し続け、エディタが操作不能になる(既往障害)。
            // 上限内に収まるよう count をクランプし、パーツ1個で超える場合は生成自体を中止する。
            if (mf != null && mf.sharedMesh != null)
            {
                int partVerts = mf.sharedMesh.vertexCount;
                if (partVerts > SPLINE_MESH_VERTEX_BUDGET)
                {
                    Debug.LogError($"[RoadPath] パーツ '{prefabName}' の頂点数({partVerts})が単体でSplineMeshの上限({SPLINE_MESH_VERTEX_BUDGET})を超えています。生成を中止します。軽量なモデルで再エクスポートしてください。");
                    UnityEngine.Object.Destroy(go);
                    return null;
                }
                if (partVerts > 0 && (long)partVerts * channel.count > SPLINE_MESH_VERTEX_BUDGET)
                {
                    int clampedCount = Mathf.Max(1, SPLINE_MESH_VERTEX_BUDGET / partVerts);
                    if (maxTileLength <= 0f && !singleSpan && !isDashed)
                    {
                        // 造形周期を持つパーツはタイル数を減らさず、整数タイル単位で別メッシュへ分ける。
                        int totalCount = channel.count;
                        UnityEngine.Object.DestroyImmediate(go);
                        var group = new GameObject(prefabName + "_Sections");
                        group.transform.SetParent(parent.transform, false);
                        for (int first = 0; first < totalCount; first += clampedCount)
                        {
                            int last = Mathf.Min(totalCount, first + clampedCount);
                            double from = clipFrom + (clipTo - clipFrom) * first / totalCount;
                            double to = clipFrom + (clipTo - clipFrom) * last / totalCount;
                            CreateSplineMesh(group, spline, prefabName, width, centerOffsetX, offsetY,
                                mirrorX, fallbackPrefabName, false, stretchToWidth, from, to,
                                false, compensatePivot, maxTileLength, normalizeBodySurface);
                        }
                        return group;
                    }
                    Debug.LogWarning($"[RoadPath] パーツ '{prefabName}' のタイル数 {channel.count} は頂点数上限を超えるため {clampedCount} にクランプします(1個あたり頂点{partVerts}、Z実寸={meshLenZ:F3}m)。Z実寸が異常に小さい場合はcm単位でエクスポートされたFBXの疑いがあります(1 Blenderユニット=1mで再エクスポートすること)。");
                    channel.count = clampedCount;
                }
            }
        }

        try
        {
            splineMesh.RebuildImmediate();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RoadPath] SplineMesh Rebuild失敗 '{prefabName}': {ex}");
        }
        SealSplineMesh(splineMesh);

        if (!singleSpan && !isDashed && (prefabName.EndsWith("/body", StringComparison.Ordinal)
            || prefabName.EndsWith("/sidewalk", StringComparison.Ordinal) || prefabName.EndsWith("/lrt_track", StringComparison.Ordinal)))
            RemoveInternalTileCaps(go.GetComponent<MeshFilter>()?.sharedMesh, channel);

        if (normalizeBodySurface && prefabName.EndsWith("/body", StringComparison.Ordinal))
            parent.GetComponentInParent<RoadMarkingContext>()?.Apply(go);

        return go;
    }

    /// <summary>連続する舗装・軌道の内部端面が斜光で細かな影を作るのを防ぎ、敷設区間の両端だけを閉じる。</summary>
    private static void RemoveInternalTileCaps(Mesh mesh, SplineMesh.Channel channel)
    {
        if (mesh == null || channel == null || channel.count < 2 || channel.GetMeshCount() != 1
            || channel.type != SplineMesh.Channel.Type.Extrude || channel.spacing != 0f) return;
        var source = channel.GetMesh(0).vertices;
        int stride = source.Length;
        // Dreamteck の Extrude/Combine は元頂点の順序を保ったままタイル順に連結する。
        // 変形後の座標では判定しないため、曲線・勾配・ミラーでも同じ端面を選べる。
        if (stride == 0 || mesh.vertexCount != (long)stride * channel.count) return;
        float min = float.MaxValue, max = float.MinValue;
        foreach (var vertex in source) { min = Mathf.Min(min, vertex.z); max = Mathf.Max(max, vertex.z); }
        if (max - min < 0.001f) return;
        var ends = new int[stride];
        for (int i = 0; i < stride; i++)
            ends[i] = Mathf.Abs(source[i].z - min) < 0.00001f ? -1 : Mathf.Abs(source[i].z - max) < 0.00001f ? 1 : 0;
        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            var triangles = mesh.GetTriangles(submesh);
            var kept = new List<int>(triangles.Length);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                int tile = a / stride, end = ends[a % stride];
                bool cap = end != 0 && b / stride == tile && c / stride == tile
                    && ends[b % stride] == end && ends[c % stride] == end;
                if (cap && (end < 0 ? tile > 0 : tile < channel.count - 1)) continue;
                kept.Add(a); kept.Add(b); kept.Add(c);
            }
            if (kept.Count != triangles.Length) mesh.SetTriangles(kept, submesh, false);
        }
    }

    /// <summary>
    /// 構築済みの SplineMesh を静的な表示物として確定する。
    /// Dreamteck は生成メッシュを破棄しないので GO に所有させ、コンポーネントを無効化して毎フレームの更新を止める。
    /// 無効でも RebuildImmediate は動くが、スプラインや Transform を後から変えても自動では再構築されない。
    /// </summary>
    public static void SealSplineMesh(SplineMesh splineMesh)
    {
        MeshFilter filter = splineMesh.GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
            splineMesh.gameObject.AddComponent<IntersectionMeshOwner>().OwnedMesh = filter.sharedMesh;
        splineMesh.enabled = false;
    }

    /// <summary>
    /// 点列からスプライン（SplineComputer）を構築する共通処理。
    /// curved=true は内部点を Catmull-Rom 相当の Bézier で補間し、端の接線は切断断面に合わせる。
    /// RebuildImmediate 済みで返すため、直後の CalculateLength / Evaluate が正しい値を返す。
    /// 点を後から変えない前提で無効化して返す(Evaluate / Project / CalculateLength は無効でも使える)。
    /// 無効なので Transform の移動にも追従しない。生成後にコンテナのワールド座標を動かさないこと。
    /// </summary>
    public static SplineComputer BuildSpline(GameObject container, List<Vector3> points, bool isCurved)
    {
        SplineComputer spline = container.AddComponent<SplineComputer>();

        SplinePoint[] splinePoints = new SplinePoint[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            SplinePoint p = new SplinePoint();
            p.position = points[i];
            p.normal = Vector3.up;
            p.size = 1f;
            p.color = Color.white;
            // curved=False の場合は角を維持し、直線で結ぶ
            p.type = isCurved ? SplinePoint.Type.SmoothMirrored : SplinePoint.Type.Broken;
            if (isCurved)
            {
                p.type = SplinePoint.Type.Broken;
                Vector3 tangent = (points[Mathf.Min(i + 1, points.Count - 1)] - points[Mathf.Max(0, i - 1)]) / 6f;
                if (i == 0 || i == points.Count - 1) tangent *= 2f;
                p.tangent = p.position - tangent;
                p.tangent2 = p.position + tangent;
            }
            splinePoints[i] = p;
        }

        spline.SetPoints(splinePoints);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] スプラインポイント設定: {splinePoints.Length}点 curved={isCurved}");

        spline.type = isCurved ? Spline.Type.Bezier : Spline.Type.Linear;
        spline.sampleMode = SplineComputer.SampleMode.Uniform;
        spline.RebuildImmediate();
        spline.enabled = false;
        return spline;
    }

    /// <summary>
    /// 構築済みスプラインを約1m間隔でサンプリングし、クリック判定用の帯状コライダーメッシュを生成する。
    /// メッシュ名は呼び出し側で設定すること。
    /// </summary>
    /// <summary>
    /// クリック判定用の全幅ストリップメッシュをスプラインから生成する。
    /// lateralOffset を指定すると、ストリップの中心線をスプラインから水平右方向
    /// （Cross(up, forward)）へオフセットして生成する。駅パスのようにプラットフォームで
    /// 敷地が片側に張り出すパスは「width=全幅（線路+ホーム）、lateralOffset=中心のずれ」で
    /// 非対称のコライダーを作る。
    /// </summary>
    public static Mesh BuildColliderMeshFromSpline(SplineComputer spline, float width, float lateralOffset = 0f)
    {
        try
        {
            float length = spline.CalculateLength();
            if (length <= 0) return null;

            float sampleSpacing = 1.0f;
            int sampleCount = Mathf.Max(2, Mathf.CeilToInt(length / sampleSpacing) + 1);

            List<Vector3> sampledPoints = new List<Vector3>(sampleCount);
            for (int i = 0; i < sampleCount; i++)
            {
                double percent = (double)i / (sampleCount - 1);
                SplineSample sample = spline.Evaluate(percent);
                Vector3 position = sample.position;
                if (lateralOffset != 0f)
                {
                    // 地形の傾斜でストリップが斜めにずれないよう、水平面上の右方向を使う
                    Vector3 forward = sample.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
                    Vector3 right = Vector3.Cross(Vector3.up, forward.normalized);
                    position += right * lateralOffset;
                }
                sampledPoints.Add(position);
            }

            return PathMeshFactory.BuildStripMesh(sampledPoints, width);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RoadPath] コライダーメッシュ生成で例外: {ex}");
            return null;
        }
    }

    /// <summary>
    /// モデルがロードできない場合のフォールバック（スプラインに沿った白い板）。
    /// </summary>
    public static GameObject CreateFallbackRoadBody(GameObject parent, SplineComputer spline, float width)
    {
        float length = spline.CalculateLength();
        if (length <= 0 || spline.sampleCount == 0) return null;

        float sampleSpacing = 1.0f;
        int sampleCount = Mathf.Max(2, Mathf.CeilToInt(length / sampleSpacing) + 1);

        List<Vector3> sampledPoints = new List<Vector3>(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            double percent = (double)i / (sampleCount - 1);
            SplineSample sample = spline.Evaluate(percent);
            // 道路本体の高さオフセットに合わせる
            sampledPoints.Add(sample.position + new Vector3(0f, RoadPartHeights.BODY_ABOVE_SPLINE, 0f));
        }

        Mesh mesh = PathMeshFactory.BuildStripMesh(sampledPoints, width);
        if (mesh == null)
        {
            Debug.LogWarning("[RoadPath] FallbackRoadBody: BuildStripMesh が null を返しました。");
            return null;
        }

        mesh.name = "FallbackRoadBody";

        GameObject go = new GameObject("FallbackRoadBody");
        go.transform.SetParent(parent.transform, false);

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;

        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetFallbackRoadMaterial();

        return go;
    }

    /// <summary>
    /// メッシュをX軸で反転させた新しいメッシュを作成します。
    /// 法線も適切に反転し、三角形のインデックス順序を逆にして面が裏返らないようにします。
    /// キャッシュ経由で呼び出される内部メソッド。
    /// </summary>
    private static Mesh CreateMirroredMeshInternal(Mesh originalMesh)
    {
        Mesh newMesh = new Mesh();
        newMesh.name = originalMesh.name + "_Mirrored";

        // 頂点の反転
        Vector3[] vertices = originalMesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].x *= -1;
        }
        newMesh.vertices = vertices;

        // 法線の反転
        Vector3[] normals = originalMesh.normals;
        if (normals != null && normals.Length > 0)
        {
            for (int i = 0; i < normals.Length; i++)
            {
                normals[i].x *= -1;
            }
            newMesh.normals = normals;
        }

        // UVはそのまま（テクスチャが左右反転するが、形状の対称性を優先）
        newMesh.uv = originalMesh.uv;
        
        // サブメッシュごとの三角形インデックス順序を反転
        newMesh.subMeshCount = originalMesh.subMeshCount;
        for (int i = 0; i < originalMesh.subMeshCount; i++)
        {
            int[] triangles = originalMesh.GetTriangles(i);
            // 配列を逆順にするだけでは不十分ですが、
            // 全体を逆順にすることでカリング順序（時計回り/反時計回り）が逆になり、
            // 頂点反転による裏返りと相殺されて正しい面向きになります。
            Array.Reverse(triangles);
            newMesh.SetTriangles(triangles, i);
        }

        newMesh.RecalculateBounds();
        return newMesh;
    }

    /// <summary>
    /// 道路パーツ（車道、ライン、歩道、街路樹、防音壁、中央分離帯）をスプラインに沿って配置する共通処理。
    /// Generate と GenerateLinearPathWithCorners の両方から呼び出される。
    /// </summary>
    /// <param name="startSidewalkSetback">
    /// スプライン始端側の歩道セットバック。交差相手に歩道がない交差点では、歩道だけを
    /// 車道の切断面より手前で終わらせ、空いた区間に交差点側がエンドキャップを敷く
    /// （RoadIntersectionMeshGenerator.BuildSidewalkEndCapSpec 参照）。
    /// 左右は node-frame（ノードから道路外側を見た左右）で渡されるため、ここでスプライン視点へ写像する。
    /// </param>
    /// <param name="endSidewalkSetback">スプライン終端側の歩道セットバック。</param>
    /// <param name="segmentPlatformZones">
    /// 角のある直線道路のセグメントでは null 以外。LRT停留所のホームは呼び出し側がパス全体で描くのでここでは描かず、
    /// このセグメントのスプライン距離へ写したホーム区間(303 の設備抑止用)を受け取る。
    /// </param>
    private void CreateRoadParts(GameObject container, SplineComputer spline, float width, float sidewalkWidth, float sideWidth, int lanes, float? treeSpacing, float wallWidth, float centerWidth, float? pillarSpacing, bool heightEditable, float baseHeight, bool singleSpan, PathSummary pathData, List<Vector3> controlPoints, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default, List<Vector2> segmentPlatformZones = null, bool startApproach = true, bool endApproach = true)
    {
        string variantBase = $"Models/Paths/p{pathData.path_variant}";
        string typeBase = $"Models/Paths/p{pathData.path_type}";
        // 車線描画のデバッグログはパフォーマンス確保のため、UNITY_LOG_ROADが有効な場合のみ出力する。
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] CreateRoadParts開始: variant={variantBase}, type={typeBase}, width={width}, lanes={lanes}, sidewalkWidth={sidewalkWidth}, sideWidth={sideWidth}, wallWidth={wallWidth}, centerWidth={centerWidth}");

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] モデルベースパス: variant={variantBase}, type(フォールバック)={typeBase}");

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] --- 道路パーツ生成開始 ---");

        var markingContext = RoadMarkingContext.Create(container, spline, pathData, startApproach, endApproach);
        var access = RoadAccessPlan.Create(pathData, spline, controlPoints, width);
        markingContext?.OverrideSideLine(true, access.Left, false);
        markingContext?.OverrideSideLine(false, access.Right, false);

        // 高速道路の本線とランプが近接する区間は一つの路面として描く(RoadAdjacency)。
        // 路側線の描き分けは body の生成時にシェーダーへ渡るので、body より先に決める。
        bool ramp = RoadAdjacency.IsRamp(pathData);
        RoadCorridorInfo corridor = ResolveRoadCorridor(pathData, controlPoints, width, wallWidth, ramp);
        if (corridor != null && markingContext != null)
        {
            // 本線は結合区間の路側線を合流車線との境界の点線にし、ランプは本線側の路側線を描かない。
            markingContext.OverrideSideLine(true, SplineDistances(corridor.GetMergedPercentRanges(true), markingContext.Length), !ramp);
            markingContext.OverrideSideLine(false, SplineDistances(corridor.GetMergedPercentRanges(false), markingContext.Length), !ramp);
        }
        RoadEndJoin startJoin = ResolveEndJoin(pathData, true, startApproach, controlPoints);
        RoadEndJoin endJoin = ResolveEndJoin(pathData, false, endApproach, controlPoints);

        // Road Body
        // 地形解像度限界・Zファイティングによる埋没を回避するため、道路本体はスプラインから浮かせる。
        // single_span=true の場合は body をタイル化せず1枚で引き伸ばす（アンカー点間の直線スパン）。
        // 索道はケーブルの実寸間隔と原点を保ち、駅の屋根を路面として高さ補正しない。
        // base_height を持つバリアント（送電線）はスプラインが地表に置かれ、モデル自体が電線の地上高を
        // 持つ規約なので、上面をY=0へ揃える補正を掛けると電線が地面に落ちる。
        bool ropeway = IsRopewayVariant(pathData.path_variant);
        GameObject roadBody = CreateSplineMesh(container, spline, $"{variantBase}/body", width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body", singleSpan: singleSpan, normalizeBodySurface: baseHeight <= 0f);
        if (ropeway)
            CreateSplineMesh(container, spline, $"{variantBase}/station_shell", width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE,
                stretchToWidth: false, maxTileLength: 0, normalizeBodySurface: false);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] body: {(roadBody != null ? "生成成功" : "生成失敗（プレハブなし？）")}");

        if (roadBody == null)
        {
            // モデルがないため、ただの白い板にフォールバックする
            roadBody = CreateFallbackRoadBody(container, spline, width);
            markingContext?.Apply(roadBody);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] body: モデルなしのため白い板にフォールバック");
        }
        if (corridor != null && !ramp) CreateMergeFillers(container, corridor, width * 0.5f, ResolveBodyMaterial(pathData, variantBase, typeBase));
        access.GenerateSurface(container, ResolveBodyMaterial(pathData, variantBase, typeBase) ?? roadBody.GetComponent<MeshRenderer>()?.sharedMaterial);

        // 併用軌道(222/242/303)と停留所のホーム。303 はホーム区間に沿道設備を置かない(下の RoadEquipmentGenerator)。
        List<LrtPlatformParts.Span> lrtPlatforms = segmentPlatformZones == null
            ? LrtPlatformParts.Resolve(pathData, controlPoints, spline.CalculateLength()) : null;
        LrtRoadParts.Generate(container, spline, pathData, lrtPlatforms);

        // オフセット計算（CreateSplineMesh に渡すのはすべて「パーツ中心の位置」。
        // プレハブのピボット位置の差は CreateSplineMesh 側で bounds 中心基準に補正される）
        bool hasSidewalk = sidewalkWidth > 0f;

        // 歩道は道路の端に覆いかぶさる帯 [width/2 - sidewalkWidth, width/2] を占める。
        // 街路樹は縁石（帯の内側端）から歩道幅の1/4程度外側に配置する。

        // 道路脇の線は、歩道の内側端（縁石）から sideWidth/2 だけ内側に入った位置を中心として配置する。
        var laneLayout = new RoadLaneLayout(width, sidewalkWidth, sideWidth, centerWidth, lanes, pathData.travel_direction);
        float sideLineCenterOffset = laneLayout.Outer;
        // 線の描画太さは sideWidth の3割に統一する。
        float lineWidth = sideWidth * 0.3f;
        // ラインは道路本体の上面（BODY_ABOVE_SPLINE）より高く浮かせてZファイティングを回避する
        float lineOffsetY = RoadPartHeights.LINE_ABOVE_SPLINE;

        // Side Lines (道路脇の実線)
        // side_width=0 は「区画線を持たない道路」を意味するため、側線も中央線も一切引かない
        // （線の太さは side_width から導くので、0 のまま描くと潰れたメッシュになる）。
        bool hasLine = sideWidth > 0f && markingContext == null;
        if (hasLine)
        {
            // 左側は mirrorX で反転させ、プレハブが非対称でも左右対称に見えるようにする。
            GameObject leftLine = null;
            foreach (var range in access.Remaining(-1))
                leftLine = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, -sideLineCenterOffset, lineOffsetY, true, $"{typeBase}/line", clipFrom: range.x, clipTo: range.y);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] side line(左): {(leftLine != null ? "生成成功" : "生成失敗")}");
            GameObject rightLine = null;
            foreach (var range in access.Remaining(1))
                rightLine = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, sideLineCenterOffset, lineOffsetY, false, $"{typeBase}/line", clipFrom: range.x, clipTo: range.y);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] side line(右): {(rightLine != null ? "生成成功" : "生成失敗")}");
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] sideWidth <= 0: 区画線なし");
        }

        // 中央分離帯（center）の配置。中央分離帯があればセンターラインは不要。
        // 中央分離帯のパーツがなければ line パーツにフォールバックする。
        if (centerWidth > 0f)
        {
            float length = spline.CalculateLength();
            bool rounded = PathDrawContext.ResolvePrefab($"{variantBase}/center_start", $"{typeBase}/center_start", out _) != null
                && PathDrawContext.ResolvePrefab($"{variantBase}/center_end", $"{typeBase}/center_end", out _) != null;
            // 本線の続きへ直接つながる端は、分離帯も途切れさせずに続ける。
            bool roundStart = rounded && !startJoin.Continuous, roundEnd = rounded && !endJoin.Continuous;
            double from = roundStart ? ClampSplinePercent(spline.Travel(0, centerWidth * .5f, Spline.Direction.Forward)) : 0;
            double to = roundEnd ? ClampSplinePercent(spline.Travel(1, centerWidth * .5f, Spline.Direction.Backward)) : 1;
            // 短い切断区間へ半円を重ねず、通行口を空ける。LODの境界には新しい端部を作らない。
            bool fits = !rounded || length > centerWidth + .1f;
            GameObject centerObj = fits ? CreateSplineMesh(container, spline, $"{variantBase}/center", centerWidth, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/center", clipFrom: from, clipTo: to) : null;
            if (rounded && fits)
            {
                if (roundStart && PathDrawContext.IntersectDetailRange(0, from, out _, out _))
                    CreateSplineMesh(container, spline, $"{variantBase}/center_start", centerWidth, 0f, RoadPartHeights.BODY_ABOVE_SPLINE,
                    fallbackPrefabName: $"{typeBase}/center_start", clipFrom: 0, clipTo: from, maxTileLength: 0, singleSpan: true);
                if (roundEnd && PathDrawContext.IntersectDetailRange(to, 1, out _, out _))
                    CreateSplineMesh(container, spline, $"{variantBase}/center_end", centerWidth, 0f, RoadPartHeights.BODY_ABOVE_SPLINE,
                    fallbackPrefabName: $"{typeBase}/center_end", clipFrom: to, clipTo: 1, maxTileLength: 0, singleSpan: true);
            }
            if (centerObj == null && hasLine)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] center パーツなし。line にフォールバック。");
                centerObj = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, 0f, lineOffsetY, false, $"{typeBase}/line");
            }
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] center: {(centerObj != null ? "生成成功" : "生成失敗")}");
        }

        // 車線中心と同じ走行帯を使う。片側通行の全幅レーンも共通定義で分割する。
        if (hasLine)
        {
            foreach (var boundary in laneLayout.Boundaries())
            {
                CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, boundary.offset,
                    lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line", isDashed: boundary.dashed);
            }
        }

        // Sidewalks
        if (hasSidewalk)
        {
            // 道路本体はマスターデータの幅（全体幅）で生成されているため、歩道をその上に重ねて配置する。
            // 歩道は道路の端に覆いかぶさる帯 [width/2 - sidewalkWidth, width/2] を占めるので、
            // 中心はその帯の真ん中。プレハブは「縁石（道路中心側の面）が +X＝進行方向右側」
            // （＝左側用パーツ）の規約なので、左側は原型のまま、右側は X ミラーで配置する。
            float sidewalkCenterOffset = (width * 0.5f) - (sidewalkWidth * 0.5f);

            // 歩道セットバック（エンドキャップ用の端の切り詰め）を clip 値へ写像する。
            // setback の左右は node-frame（ノードから道路外側を見た左右）:
            //   始端ノードでは外向き＝スプライン進行方向 → node-left ＝ スプライン左。
            //   終端ノードでは外向き＝スプライン逆方向   → node-left ＝ スプライン右。
            float splineLenForClip = spline.CalculateLength();
            GetSidewalkClipRange(splineLenForClip, startSidewalkSetback.LeftOutwardSetback, endSidewalkSetback.RightOutwardSetback, out double leftClipFrom, out double leftClipTo);
            GetSidewalkClipRange(splineLenForClip, startSidewalkSetback.RightOutwardSetback, endSidewalkSetback.LeftOutwardSetback, out double rightClipFrom, out double rightClipTo);

            GameObject leftSidewalk = null;
            foreach (var range in access.Remaining(-1, leftClipFrom, leftClipTo))
                leftSidewalk = CreateSplineMesh(container, spline, $"{variantBase}/sidewalk", sidewalkWidth, -sidewalkCenterOffset, RoadPartHeights.SIDEWALK_ABOVE_SPLINE, false, $"{typeBase}/sidewalk", clipFrom: range.x, clipTo: range.y);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] sidewalk(左): {(leftSidewalk != null ? "生成成功" : "生成失敗")} clip=({leftClipFrom:F3}〜{leftClipTo:F3})");
            GameObject rightSidewalk = null;
            foreach (var range in access.Remaining(1, rightClipFrom, rightClipTo))
                rightSidewalk = CreateSplineMesh(container, spline, $"{variantBase}/sidewalk", sidewalkWidth, sidewalkCenterOffset, RoadPartHeights.SIDEWALK_ABOVE_SPLINE, true, $"{typeBase}/sidewalk", clipFrom: range.x, clipTo: range.y);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] sidewalk(右・ミラー): {(rightSidewalk != null ? "生成成功" : "生成失敗")} clip=({rightClipFrom:F3}〜{rightClipTo:F3})");
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] sidewalkWidth <= 0: 歩道なし");
        }

        // Wall (防音壁・ガードレール)
        if (wallWidth > 0f)
        {
            // 壁は道路の外側 [width/2, width/2 + wallWidth] に立てる。
            // 「右側用パーツ」の規約で、右側は原型のまま、左側は X ミラー
            // （歩道は逆の「左側用」規約なので注意。CreateRoadParts の歩道ブロック参照）。
            // 高速道路の本線とランプの結合区間では、間の壁と相手の路面に掛かる壁を立てない。
            float wallCenterOffset = (width * 0.5f) + (wallWidth * 0.5f);
            foreach (var range in WallRanges(corridor, left: true))
                foreach (var remaining in access.Remaining(-1, range.From, range.To))
                    CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, -wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, true, $"{typeBase}/wall", clipFrom: remaining.x, clipTo: remaining.y);
            foreach (var range in WallRanges(corridor, left: false))
                foreach (var remaining in access.Remaining(1, range.From, range.To))
                    CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, false, $"{typeBase}/wall", clipFrom: remaining.x, clipTo: remaining.y);
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] wallWidth <= 0: 壁なし");
        }

        // 結合区間は一つの路面なので、路肩の設備を路面の中に立てない。
        RoadEquipmentGenerator.Generate(container, spline, pathData, width, sidewalkWidth, treeSpacing,
            pathData.path_variant == 303
                ? segmentPlatformZones ?? LrtPlatformParts.ZoneRanges(lrtPlatforms, LrtRoadParts.PlatformRampLength(303), LrtRoadParts.PLATFORM_EQUIPMENT_ZONE_MARGIN)
                : corridor != null ? SplineDistances(corridor.GetMergedPercentRanges(null), spline.CalculateLength()) : null, access);
        SuspensionBridgeGenerator.Generate(container, spline, pathData, width);

        // 柱
        // 地下パス（負の相対Yを持つ）は路面が地面より低く、路面支持の柱は不要なので配置しない
        // （開削区間の路面は掘削後の地面に接する。高架と地下が混在するパスは想定外で柱なしになる）。
        if (heightEditable && pillarSpacing.HasValue && pillarSpacing.Value > 0
            && !TunnelSectionUtil.IsUndergroundPath(pathData))
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] 柱: spacing={pillarSpacing.Value}m, base_height={baseHeight}m で配置開始");
            // 本線の続きへ直接つながる端の橋脚は、つながる2本のうち片方だけが立てる。
            GeneratePillars(container, spline, pillarSpacing.Value, baseHeight, pathData, variantBase, typeBase,
                startJoin.Continuous && !startJoin.OwnsSupport, endJoin.Continuous && !endJoin.OwnsSupport);
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] 柱: height_editable=false/地下パス/spacing未指定のためスキップ");
        }

        // 全体の生成結果サマリーを出力する
        int totalChildren = container.transform.childCount;
        int rendererCount = 0;
        int meshFilterCount = 0;
        int totalVerts = 0;
        for (int ci = 0; ci < totalChildren; ci++)
        {
            Transform child = container.transform.GetChild(ci);
            MeshFilter mf = child.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                meshFilterCount++;
                totalVerts += mf.sharedMesh.vertexCount;
            }
            // SplineMesh は子にメッシュを生成するため、子の子も含めて検査する
            MeshFilter[] childMfs = child.GetComponentsInChildren<MeshFilter>(true);
            MeshRenderer[] childMrs = child.GetComponentsInChildren<MeshRenderer>(true);
            if (childMfs.Length > 0) meshFilterCount += childMfs.Length;
            if (childMrs.Length > 0) rendererCount += childMrs.Length;
            foreach (var cmf in childMfs)
            {
                if (cmf.sharedMesh != null) totalVerts += cmf.sharedMesh.vertexCount;
            }
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] --- 道路パーツ生成完了 --- 子オブジェクト数={totalChildren}, MeshFilter={meshFilterCount}, Renderer={rendererCount}, 総頂点数={totalVerts}");
    }

    /// <summary>
    /// 歩道のセットバック量（メートル）をスプラインの clip 範囲（percent）へ変換する。
    /// 両端の切り詰めで区間が消滅・逆転する場合は、比率を保ったまま縮めて破綻を防ぐ。
    /// </summary>
    private static void GetSidewalkClipRange(float splineLen, float startSetback, float endSetback, out double clipFrom, out double clipTo)
    {
        clipFrom = 0.0;
        clipTo = 1.0;
        if (splineLen <= 0f)
        {
            return;
        }
        float total = Mathf.Max(0f, startSetback) + Mathf.Max(0f, endSetback);
        if (total <= 0f)
        {
            return;
        }
        // 道路が短くセットバック合計が長さの8割を超える場合は、比率を保って縮める。
        float scale = total > splineLen * 0.8f ? (splineLen * 0.8f) / total : 1f;
        clipFrom = Mathf.Max(0f, startSetback) * scale / splineLen;
        clipTo = 1.0 - Mathf.Max(0f, endSetback) * scale / splineLen;
    }

    private const float MERGE_WALL_MIN_RUN_LENGTH = 1.0f; // 結合区間の間に残る細切れの壁を立てない長さ(m)
    private const float MERGE_FILLER_EDGE_INSET = 0.3f; // 隙間の舗装を両側の路面の下へ潜らせる幅(m)
    private const float MERGE_FILLER_MIN_LENGTH = 1.0f; // これより短い隙間の舗装は作らない(m)

    /// <summary>高速道路の本線とランプの結合区間。点列はこのスプラインの制御点。</summary>
    private static RoadCorridorInfo ResolveRoadCorridor(PathSummary pathData, List<Vector3> points, float width, float wallWidth, bool ramp)
    {
        if (!RoadAdjacency.IsMotorway(pathData) || RoadAdjacency.NeighborProvider == null || points == null) return null;
        var neighbors = RoadAdjacency.NeighborProvider(pathData);
        return neighbors != null && neighbors.Count > 0 ? RoadAdjacency.Compute(points, width, wallWidth, ramp, neighbors) : null;
    }

    private static List<Vector2> SplineDistances(List<TrackCorridorInfo.PercentRange> ranges, float length)
    {
        var result = new List<Vector2>(ranges.Count);
        foreach (var range in ranges) result.Add(new Vector2((float)range.From * length, (float)range.To * length));
        return result;
    }

    /// <summary>描画用に分割した区間の端(トンネルの坑口など)は道路の端として扱わない。</summary>
    private static RoadEndJoin ResolveEndJoin(PathSummary pathData, bool start, bool pathEnd, List<Vector3> points)
    {
        if (!pathEnd || points == null || points.Count == 0) return default;
        Vector3 point = start ? points[0] : points[points.Count - 1];
        if (RoadMarkingRules.IsApproachEndpoint != null && !RoadMarkingRules.IsApproachEndpoint(pathData, start, point)) return default;
        return RoadAdjacency.EndJoin(pathData, start);
    }

    private static List<TrackCorridorInfo.PercentRange> WallRanges(RoadCorridorInfo corridor, bool left)
        => corridor != null ? corridor.GetWallPercentIntervals(left, MERGE_WALL_MIN_RUN_LENGTH)
            : new List<TrackCorridorInfo.PercentRange> { new TrackCorridorInfo.PercentRange { From = 0.0, To = 1.0 } };

    /// <summary>交差点の路面(RoadIntersectionMeshGenerator)と同じく、マスターの body_material を路面の材質の正とする。</summary>
    private static Material ResolveBodyMaterial(PathSummary pathData, string variantBase, string typeBase)
    {
        var master = MasterDataStore.Instance?.GetPathMaster(pathData.path_variant);
        Material named = master != null ? GetOrCreateRoadMaterialByName(master.body_material) : null;
        if (named != null) return named;
        GameObject prefab = PathDrawContext.ResolvePrefab($"{variantBase}/body", $"{typeBase}/body", out _);
        Material source = prefab != null ? prefab.GetComponent<MeshRenderer>()?.sharedMaterial : null;
        return source != null ? GetOrCreateRoadMaterial(source) : null;
    }

    /// <summary>
    /// 本線とランプの路面の隙間を舗装する帯。二重に作らないよう本線だけが生成する。
    /// 本線の路面から ROAD_MERGE_SINK の2倍下げ、本線とランプ(本線より ROAD_MERGE_SINK 低い)の路面の下へ縁を潜らせる。
    /// </summary>
    private static void CreateMergeFillers(GameObject container, RoadCorridorInfo corridor, float halfWidth, Material material)
    {
        if (material == null) return;
        foreach (bool left in new[] { true, false })
        {
            float[] fillTo = left ? corridor.LeftFillTo : corridor.RightFillTo;
            int n = corridor.Points.Count, i = 0, run = 0;
            while (i < n)
            {
                if (float.IsNaN(fillTo[i])) { i++; continue; }
                int first = i;
                while (i < n && !float.IsNaN(fillTo[i])) i++;
                int last = i - 1;
                if (last - first < 1 || corridor.CumulativeLength[last] - corridor.CumulativeLength[first] < MERGE_FILLER_MIN_LENGTH) continue;
                BuildMergeFiller(container, corridor, first, last, left, halfWidth, fillTo, material, run++);
            }
        }
    }

    private static void BuildMergeFiller(GameObject container, RoadCorridorInfo corridor, int first, int last, bool left,
        float halfWidth, float[] fillTo, Material material, int run)
    {
        int count = last - first + 1;
        var vertices = new Vector3[count * 2];
        var normals = new Vector3[count * 2];
        var uvs = new Vector2[count * 2];
        var triangles = new int[(count - 1) * 6];
        float near = halfWidth - MERGE_FILLER_EDGE_INSET;
        float lift = RoadPartHeights.BODY_ABOVE_SPLINE - 2f * LocalConstants.ROAD_MERGE_SINK;
        for (int k = 0; k < count; k++)
        {
            int index = first + k;
            Vector3 origin = corridor.Points[index] + Vector3.up * lift;
            float far = fillTo[index] + MERGE_FILLER_EDGE_INSET;
            // 頂点は横位置の小さい側から並べ、上向きの面の巻き順を保つ。
            float a = left ? -far : near, b = left ? -near : far;
            vertices[k * 2] = origin + corridor.RightDir[index] * a;
            vertices[k * 2 + 1] = origin + corridor.RightDir[index] * b;
            normals[k * 2] = normals[k * 2 + 1] = Vector3.up;
            float v = corridor.CumulativeLength[index] * 0.25f;
            uvs[k * 2] = new Vector2(a * 0.25f, v);
            uvs[k * 2 + 1] = new Vector2(b * 0.25f, v);
        }
        for (int k = 0, t = 0; k < count - 1; k++)
        {
            int v0 = k * 2;
            triangles[t++] = v0; triangles[t++] = v0 + 2; triangles[t++] = v0 + 1;
            triangles[t++] = v0 + 1; triangles[t++] = v0 + 2; triangles[t++] = v0 + 3;
        }
        var mesh = new Mesh { name = $"RoadMergeFiller_{(left ? "L" : "R")}_{run}" };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        var go = new GameObject(mesh.name);
        go.transform.SetParent(container.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
    }

    /// <summary>
    /// single_span=true のため、曲線パスの細分化済み点列からアンカー点のみを抽出する。
    /// 始点・終点は渡された点列の先頭・末尾をそのまま使う（交差点切断点の補正が既に適用済みのため）。
    /// 中間のウェイポイントは pathData から絶対座標を取得し、CalculatePathPoints と同じ
    /// Y補正（relY=0 の場合は地形標高+ROAD_SPLINE_OFFSET、それ以外は groundY+relY）を適用する。
    /// 抽出されたアンカー点でLinear スプラインを構築すると、アンカー点間が直線セグメントになる。
    /// </summary>
    private static List<Vector3> ExtractAnchorPointsForSingleSpan(PathSummary pathData, List<Vector3> subdividedPoints)
    {
        List<Vector3> anchors = new List<Vector3>();

        if (subdividedPoints == null || subdividedPoints.Count == 0)
            return anchors;

        // 始点（交差点切断点の補正が既に適用済み）
        anchors.Add(subdividedPoints[0]);

        // ウェイポイント（CalculatePathPoints の non-curved ブロックと同じ Y 補正）
        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                Vector3 wpPos = wp.GetAbsolutePosition();
                float wpRelY = (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
                if (wpRelY == 0f)
                {
                    float groundHeight = Utils.GetGroundHeight(new Vector3(wpPos.x, 1000f, wpPos.z));
                    wpPos.y = groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET;
                }
                anchors.Add(wpPos);
            }
        }

        // 終点（交差点切断点の補正が既に適用済み）
        if (subdividedPoints.Count >= 2)
            anchors.Add(subdividedPoints[subdividedPoints.Count - 1]);

        return anchors;
    }

    /// <summary>
    /// curved=False かつウェイポイントがある道路を生成する。
    /// セグメントごとに独立したスプラインを作り、角（ウェイポイント）では
    /// RoadIntersectionMeshGenerator と同じロジックで交差領域を計算し、
    /// 接続断面で道路を区切り、角を共通外周のメッシュで埋める。
    /// </summary>
    private PathMeshResult GenerateLinearPathWithCorners(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default, bool singleSpan = false)
    {
        // セグメントごとに別スプラインを持つため、パス全長の割合で表す詳細描画区間は適用できない
        // (全長を同じ詳細度で描く)。
        PathDrawContext.SuppressDetailRange = true;
        GameObject container = new GameObject($"Road_{pathData.path_id}");
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] 角あり直線道路生成開始: path_id={pathData.path_id}, segments={points.Count - 1}");

        // パラメータ取得
        GetPathMasterParams(pathData, out float width, out float sidewalkWidth, out float sideWidth, out float? treeSpacing, out int lanes, out float wallWidth, out float centerWidth, out float? pillarSpacing, out bool heightEditable, out float baseHeight, out bool _);

        Vector3[] segmentStarts = new Vector3[points.Count - 1];
        Vector3[] segmentEnds = new Vector3[points.Count - 1];

        // セグメントごとの歩道セットバック。実ノード側（最初のセグメントの始端・最後のセグメントの終端）は
        // 呼び出し元（交差点ノードの生成結果）から渡され、折れ点側は角の生成結果から得る。
        SidewalkSetback[] segStartSetbacks = new SidewalkSetback[points.Count - 1];
        SidewalkSetback[] segEndSetbacks = new SidewalkSetback[points.Count - 1];
        segStartSetbacks[0] = startSidewalkSetback;
        segEndSetbacks[points.Count - 2] = endSidewalkSetback;

        for (int i = 0; i < points.Count - 1; i++)
        {
            segmentStarts[i] = points[i];
            segmentEnds[i] = points[i + 1];
        }

        var waypoints = pathData.GetWaypointList();
        if (waypoints != null && waypoints.Count > 0 && waypoints.Count == points.Count - 2)
        {
            var gf = new NetTopologySuite.Geometries.GeometryFactory();

            for (int i = 0; i < waypoints.Count; i++)
            {
                int pointIndex = i + 1;
                var wp = waypoints[i];
                Vector3 wpPos = points[pointIndex];
                Vector3 prevPos = points[pointIndex - 1];
                Vector3 nextPos = points[pointIndex + 1];

                Vector2 dirA = (new Vector2(wpPos.x, wpPos.z) - new Vector2(prevPos.x, prevPos.z)).normalized;
                Vector2 dirB = (new Vector2(nextPos.x, nextPos.z) - new Vector2(wpPos.x, wpPos.z)).normalized;

                if (Vector2.Dot(dirA, dirB) > 0.99f) continue;

                PathSummary dummyA = new PathSummary
                {
                    path_id = pathData.path_id * 10000 + i * 2,
                    path_type = pathData.path_type,
                    path_variant = pathData.path_variant,
                    width = width,
                    start_node_id = -1,
                    end_node_id = -2,
                    start_point = new List<object> { wpPos.x, wpPos.y, wpPos.z },
                    end_point = new List<object> { prevPos.x, prevPos.y, prevPos.z },
                    waypoints = new List<object>(),
                    curved = false
                };

                PathSummary dummyB = new PathSummary
                {
                    path_id = pathData.path_id * 10000 + i * 2 + 1,
                    path_type = pathData.path_type,
                    path_variant = pathData.path_variant,
                    width = width,
                    start_node_id = -1,
                    end_node_id = -3,
                    start_point = new List<object> { wpPos.x, wpPos.y, wpPos.z },
                    end_point = new List<object> { nextPos.x, nextPos.y, nextPos.z },
                    waypoints = new List<object>(),
                    curved = false
                };

                List<PathSummary> dummyPaths = new List<PathSummary> { dummyA, dummyB };
                float wpRelY = (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
                NodeMeshResult cornerResult = NodeMeshFactory.GenerateNodeMesh(-1, wpPos, dummyPaths, wpRelY,
                    p => new List<Vector3> { wpPos, p == dummyA ? prevPos : nextPos },
                    p => Vector2.Distance(IntersectionPolygonUtil.XZ(wpPos), IntersectionPolygonUtil.XZ(p == dummyA ? prevPos : nextPos)) * 0.45f);

                if (cornerResult != null)
                {
                    if (cornerResult.ConnectionPoints != null)
                    {
                        if (cornerResult.ConnectionPoints.TryGetValue(dummyA.path_id, out Vector3 cutA))
                        {
                            segmentEnds[pointIndex - 1] = cutA;
                        }
                        if (cornerResult.ConnectionPoints.TryGetValue(dummyB.path_id, out Vector3 cutB))
                        {
                            segmentStarts[pointIndex] = cutB;
                        }
                    }

                    // 角の生成結果に歩道セットバックがあれば、対応するセグメント端へ反映する。
                    // dummyA は角ノードから前セグメント方向（＝前セグメントの終端側）、
                    // dummyB は次セグメント方向（＝次セグメントの始端側）。
                    if (cornerResult.SidewalkSetbacks != null)
                    {
                        if (cornerResult.SidewalkSetbacks.TryGetValue(dummyA.path_id, out SidewalkSetback setbackA))
                        {
                            segEndSetbacks[pointIndex - 1] = setbackA;
                        }
                        if (cornerResult.SidewalkSetbacks.TryGetValue(dummyB.path_id, out SidewalkSetback setbackB))
                        {
                            segStartSetbacks[pointIndex] = setbackB;
                        }
                    }

                    if (cornerResult.Mesh != null)
                    {
                        GameObject cornerObj = new GameObject($"Corner_{i}");
                        cornerObj.transform.SetParent(container.transform, false);
                        cornerObj.transform.position = wpPos;
                        MeshFilter mf = cornerObj.AddComponent<MeshFilter>();
                        mf.mesh = cornerResult.Mesh;
                        cornerObj.AddComponent<IntersectionMeshOwner>().OwnedMesh = cornerResult.Mesh;
                        MeshRenderer mr = cornerObj.AddComponent<MeshRenderer>();
                        if (cornerResult.Material != null) mr.material = cornerResult.Material;
                        if (cornerResult.Markings != null) cornerObj.AddComponent<RoadMarkingSurface>().Configure(cornerResult.Markings);

                        // 折れ点も実ノードと同じ接続形状を使い、切断した本線の間を軌道と架線でつなぐ。
                        if (cornerResult.RailConnectors != null)
                        {
                            GameObject railGO = new GameObject("Rail");
                            railGO.transform.SetParent(cornerObj.transform, false);
                            for (int j = 0; j < cornerResult.RailConnectors.Count; j++)
                                TrackPath.CreateRailConnectorObject(railGO.transform, cornerResult.RailConnectors[j], j);
                        }

                        if (cornerResult.SidewalkConnectors != null && cornerResult.SidewalkConnectors.Count > 0)
                        {
                            GameObject sidewalkGO = new GameObject("Sidewalk");
                            sidewalkGO.transform.SetParent(cornerObj.transform, false);
                            sidewalkGO.transform.localPosition = Vector3.zero;
                            sidewalkGO.transform.localRotation = Quaternion.identity;
                            sidewalkGO.transform.localScale = Vector3.one;

                            for (int j = 0; j < cornerResult.SidewalkConnectors.Count; j++)
                            {
                                CreateSidewalkConnectorObject(sidewalkGO.transform, cornerResult.SidewalkConnectors[j], j);
                            }
                        }
                    }
                }
            }
        }

        // セグメントごとのスプライン点列(折れ点では角の切断位置で終わる)
        var segmentPoints = new List<Vector3>[segmentStarts.Length];
        List<Vector3> allSubdividedPoints = new List<Vector3>();
        for (int i = 0; i < segmentStarts.Length; i++)
        {
            Vector3 segStart = segmentStarts[i];
            Vector3 segEnd = segmentEnds[i];
            if (Vector3.Distance(segStart, segEnd) < 0.01f) continue;

            // single_span の場合は地形追従の細分化を行わない。
            // アンカー点(始点・終点)だけの2点スプラインとし、body は count=1 で
            // 両端を結ぶ直線スパンとして引き伸ばされる。
            List<Vector3> splinePoints;
            if (singleSpan)
            {
                splinePoints = new List<Vector3> { segStart, segEnd };
            }
            else
            {
                float relYA = GetSegmentStartRelY(pathData, i);
                float relYB = GetSegmentEndRelY(pathData, i, segmentStarts.Length);
                bool followTerrain = PathManager.GetPathFollowTerrain(pathData)
                    && relYA == 0f && relYB == 0f;

                List<Vector3> anchorPoints = new List<Vector3> { segStart, segEnd };
                splinePoints = PathManager.SubdividePointsWithTerrain(anchorPoints, PathManager.TERRAIN_SAMPLE_INTERVAL, followTerrain);
            }
            allSubdividedPoints.AddRange(splinePoints);
            segmentPoints[i] = splinePoints;
        }

        List<Vector2>[] segmentPlatformZones = CreateCornerPathLrtPlatforms(container, pathData, points, segmentPoints);

        // セグメントごとにスプラインを生成
        for (int i = 0; i < segmentPoints.Length; i++)
        {
            if (segmentPoints[i] == null) continue;
            GameObject segObj = new GameObject($"Segment_{i}");
            segObj.transform.SetParent(container.transform, false);
            SplineComputer spline = BuildSpline(segObj, segmentPoints[i], false);
            PathDrawContext.SetAdaptiveTileLength(PathDrawContext.ComputeAdaptiveTileLength(spline, MAX_EXTRUDE_TILE_LENGTH));

            CreateRoadParts(segObj, spline, width, sidewalkWidth, sideWidth, lanes, treeSpacing, wallWidth, centerWidth, pillarSpacing, heightEditable, baseHeight, singleSpan, pathData, segmentPoints[i], segStartSetbacks[i], segEndSetbacks[i], segmentPlatformZones?[i], i == 0, i == segmentPoints.Length - 1);
        }

        // コライダー用メッシュの生成
        Mesh colliderMesh = BuildColliderMeshForSegments(allSubdividedPoints, width);
        if (colliderMesh != null)
        {
            colliderMesh.name = $"RoadCollider_{pathData.path_id}";
            // MeshCollider は PathManager 側で pathGO に追加し、sharedMesh を設定する
        }

        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>
    /// 角のある直線道路(222/242/303)のLRT停留所のホームを、セグメントの点列と角の頂点を順につないだパス全体の
    /// 折れ線スプライン1本に描く(角で切ると、角の区間にデッキが無く、角ごとにスロープと上屋が付くため)。
    /// 角の上は折れ線の線路(TrackPath)と同じく折れたまま押し出す簡易表現。
    /// LRTの併用軌道でなければ null、そうでなければ各セグメントのスプライン距離へ写したホーム区間
    /// (303 の設備抑止用。303 以外・停留所なしは空)をセグメントごとに返す。
    /// </summary>
    private static List<Vector2>[] CreateCornerPathLrtPlatforms(GameObject container, PathSummary pathData, List<Vector3> corners, List<Vector3>[] segmentPoints)
    {
        if (!LrtRoadParts.IsEmbedded(pathData.path_variant)) return null;
        var line = new List<Vector3>();
        var offsets = new float[segmentPoints.Length];
        float length = 0f;
        void Append(Vector3 point)
        {
            if (line.Count > 0)
            {
                float step = Vector3.Distance(line[line.Count - 1], point);
                if (step < 0.01f) return;
                length += step;
            }
            line.Add(point);
        }
        for (int i = 0; i < segmentPoints.Length; i++)
        {
            if (segmentPoints[i] == null) continue;
            // 角の区間は平らな角メッシュの上なので地形追従しない。Linear スプラインは各区間の接線を前の区間の向きと
            // 平均する(linearAverageDirection)ので、細かく刻んで角の後ろの接線のずれを1区間に留める。
            var legs = line.Count > 0 ? new List<Vector3> { line[line.Count - 1], corners[i], segmentPoints[i][0] } : new List<Vector3> { segmentPoints[i][0] };
            foreach (var point in PathManager.SubdividePointsWithTerrain(legs, PathManager.TERRAIN_SAMPLE_INTERVAL, false)) Append(point);
            offsets[i] = length;
            for (int k = 1; k < segmentPoints[i].Count; k++) Append(segmentPoints[i][k]);
        }

        List<Vector2> zones = null;
        List<LrtPlatformParts.Span> spans = line.Count >= 2 ? LrtPlatformParts.Resolve(pathData, line, length) : null;
        if (spans != null && spans.Count > 0)
        {
            if (pathData.path_variant == 303)
                zones = LrtPlatformParts.ZoneRanges(spans, LrtRoadParts.PlatformRampLength(303), LrtRoadParts.PLATFORM_EQUIPMENT_ZONE_MARGIN);
            var platformObj = new GameObject("LrtPlatforms");
            platformObj.transform.SetParent(container.transform, false);
            SplineComputer spline = BuildSpline(platformObj, line, false);
            // スプライン長は角のサンプリングで点列長とわずかに違うので、スプライン距離へ比例換算する。
            float scale = spline.CalculateLength() / length;
            for (int i = 0; i < spans.Count; i++)
            {
                var span = spans[i];
                span.From *= scale;
                span.To *= scale;
                spans[i] = span;
            }
            LrtPlatformParts.Generate(platformObj, spline, pathData, spans);
        }

        var result = new List<Vector2>[segmentPoints.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new List<Vector2>();
            if (zones != null)
                foreach (var zone in zones) result[i].Add(zone - new Vector2(offsets[i], offsets[i]));
        }
        return result;
    }

    /// <summary>
    /// 交差点や折れ点の角に配置する歩道コネクタ（SidewalkConnectorSpec）を、
    /// 通常道路と同じパーツの頂点を共通外周へ写し、幅だけを補間する。
    /// PathManager（交差点のノードメッシュ）と RoadPath（curved=false の折れ点）の両方から使う。
    /// マテリアルは GetOrCreateRoadMaterial のキャッシュを経由し、
    /// 通常道路の歩道と交差点内の歩道で色味・描画設定（renderQueue調整）が一致するようにする。
    /// </summary>
    public static void CreateSidewalkConnectorObject(Transform parent, SidewalkConnectorSpec connector, int index)
    {
        if (connector?.Points == null || connector.Points.Count < 2 || ModelManager.Instance == null) return;
        string part = connector.Part;
        string resourcePath = $"Models/Paths/p{connector.PrefabVariant}/{part}";
        GameObject prefab = ModelManager.Instance.GetModel(resourcePath, suppressWarning: true);
        if (prefab == null)
        {
            resourcePath = $"Models/Paths/p{connector.FallbackType}/{part}";
            prefab = ModelManager.Instance.GetModel(resourcePath);
        }
        Mesh source = prefab != null ? prefab.GetComponent<MeshFilter>()?.sharedMesh : null;
        source = GetRoadPartMesh(source, resourcePath);
        if (source == null) return;
        Bounds bounds = source.bounds;
        if (bounds.size.x < 1e-5f || bounds.size.z < 1e-5f) return;
        float length = IntersectionPolygonUtil.Length(connector.Points);
        if (length < 1e-5f) return;
        GameObject endPrefab = connector.EndPrefabVariant >= 0
            ? ModelManager.Instance.GetModel($"Models/Paths/p{connector.EndPrefabVariant}/{part}", suppressWarning: true)
                ?? ModelManager.Instance.GetModel($"Models/Paths/p{connector.EndFallbackType}/{part}") : prefab;
        Mesh endSource = endPrefab != null ? endPrefab.GetComponent<MeshFilter>()?.sharedMesh : null;
        float endHeight = endSource != null ? endSource.bounds.size.y : bounds.size.y;
        int tiles = Mathf.Max(1, Mathf.CeilToInt(length / 0.5f));
        if (!ConnectorSourceCache.TryGetValue(source, out var sourceArrays))
        {
            var triangles = new int[source.subMeshCount][];
            for (int sub = 0; sub < triangles.Length; sub++) triangles[sub] = source.GetTriangles(sub);
            sourceArrays = (source.vertices, source.uv, triangles);
            ConnectorSourceCache[source] = sourceArrays;
        }
        Vector3[] original = sourceArrays.vertices;
        Vector2[] originalUv = sourceArrays.uv;
        int[][] sourceTriangles = sourceArrays.triangles;
        var vertices = new List<Vector3>();
        var uv = new List<Vector2>();
        var indices = new List<int>[sourceTriangles.Length];
        for (int sub = 0; sub < indices.Length; sub++) indices[sub] = new List<int>();
        for (int tile = 0; tile < tiles; tile++)
        {
            if (connector.Dashed && (tile * length / tiles) % 6f >= 3f) continue;
            int offset = vertices.Count;
            foreach (var vertex in original)
            {
                float t = (tile + (vertex.z - bounds.min.z) / bounds.size.z) / tiles;
                EvaluateConnector(connector, t, out Vector3 position, out Vector2 forward, out float width);
                Vector3 right = new Vector3(forward.y, 0f, -forward.x);
                // 幅だけをX方向へ適用する。SplinePoint.sizeで高さまで変形させない。
                float heightScale = bounds.size.y > 1e-5f ? Mathf.Lerp(bounds.size.y, endHeight, t*t*(3f-2f*t)) / bounds.size.y : 1f;
                vertices.Add(position + right * ((vertex.x - bounds.center.x) * width / bounds.size.x) + Vector3.Slerp(connector.StartNormal, connector.EndNormal, t*t*(3f-2f*t)).normalized * (vertex.y * heightScale));
            }
            for (int v = 0; v < original.Length; v++) uv.Add(v < originalUv.Length ? originalUv[v] : Vector2.zero);
            for (int sub = 0; sub < indices.Length; sub++) foreach (int triangle in sourceTriangles[sub]) indices[sub].Add(offset + triangle);
        }
        var mesh = new Mesh { name = $"Intersection_{part}_{index}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.subMeshCount = indices.Length;
        for (int sub = 0; sub < indices.Length; sub++) mesh.SetTriangles(indices[sub], sub);
        mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
        var go = new GameObject($"{part}_{index}");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        var sourceRenderer = prefab.GetComponent<MeshRenderer>();
        if (sourceRenderer != null)
        {
            var materials = sourceRenderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) materials[i] = GetOrCreateRoadMaterial(materials[i]);
            renderer.sharedMaterials = materials;
        }
    }

    private static void EvaluateConnector(SidewalkConnectorSpec connector, float t, out Vector3 position, out Vector2 forward, out float width)
    {
        float f = Mathf.Clamp01(t) * (connector.Points.Count - 1);
        int index = Mathf.Min(Mathf.FloorToInt(f), connector.Points.Count - 2);
        float fraction = f - index;
        position = Vector3.Lerp(connector.Points[index], connector.Points[index + 1], fraction);
        forward = IntersectionPolygonUtil.XZ(connector.Points[index + 1] - connector.Points[index]).normalized;
        if (connector.Directions != null) forward = Vector2.Lerp(connector.Directions[index], connector.Directions[index+1], fraction).normalized;
        if (t <= 1e-5f && connector.StartDirection.sqrMagnitude > 0f) forward = connector.StartDirection;
        if (t >= 1f - 1e-5f && connector.EndDirection.sqrMagnitude > 0f) forward = connector.EndDirection;
        width = connector.Widths != null ? Mathf.Lerp(connector.Widths[index], connector.Widths[index + 1], fraction) : 0.5f;
    }
    /// <summary>
    /// 指定セグメントの始点側のrelativeYをPathSummaryから取得する。
    /// </summary>
    private float GetSegmentStartRelY(PathSummary pathData, int segmentIndex)
    {
        var startList = pathData.GetStartPointList();
        if (segmentIndex == 0)
            return (startList != null && startList.Count >= 3) ? startList[1] : 0f;

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null && segmentIndex - 1 < waypointList.Count)
        {
            var wp = waypointList[segmentIndex - 1];
            return (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
        }
        return 0f;
    }

    /// <summary>
    /// 指定セグメントの終点側のrelativeYをPathSummaryから取得する。
    /// </summary>
    private float GetSegmentEndRelY(PathSummary pathData, int segmentIndex, int totalSegments)
    {
        var endList = pathData.GetEndPointList();
        if (segmentIndex == totalSegments - 1)
            return (endList != null && endList.Count >= 3) ? endList[1] : 0f;

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null && segmentIndex < waypointList.Count)
        {
            var wp = waypointList[segmentIndex];
            return (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
        }
        return 0f;
    }

    /// <summary>
    /// セグメントごとに独立した帯状メッシュを生成し、結合してコライダー用メッシュとする。
    /// </summary>
    private Mesh BuildColliderMeshForSegments(List<Vector3> points, float width)
    {
        List<CombineInstance> combineInstances = new List<CombineInstance>();

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 segStart = points[i];
            Vector3 segEnd = points[i + 1];
            if (Vector3.Distance(segStart, segEnd) < 0.01f) continue;

            List<Vector3> segPoints = new List<Vector3> { segStart, segEnd };
            Mesh segMesh = PathMeshFactory.BuildStripMesh(segPoints, width);
            if (segMesh != null)
            {
                combineInstances.Add(new CombineInstance { mesh = segMesh, transform = Matrix4x4.identity });
            }
        }

        if (combineInstances.Count == 0) return null;

        Mesh colliderMesh = new Mesh();
        colliderMesh.CombineMeshes(combineInstances.ToArray(), true, false);
        colliderMesh.RecalculateBounds();
        return colliderMesh;
    }

    /// <summary>
    /// パスに沿って柱（pillar）を配置する。
    /// height_editable=true のバリアントに対してのみ配置される。
    /// 各セグメントの終点を配置候補とし、セグメント長が pillar_spacing を超える場合は
    /// その間隔で追加配置する。セグメントの始点には配置しない。
    ///
    /// base_height は3D描画用の高さオフセット(m)。CalculatePathPoints でスプライン点のYから
    /// base_height が引かれているため、スプラインは (relY - base_height) の高さで構築されている。
    /// GeneratePillars はアンカー点のYからも base_height を引いてスプラインと合わせる。
    /// RoadPath（高架道路）と TrackPath（高架線路）の共通処理。
    /// </summary>
    /// <param name="skipStart">始端の橋脚を立てない(同じ位置に続きの道路が立てる端)。</param>
    /// <param name="skipEnd">終端の橋脚を立てない。</param>
    public static void GeneratePillars(GameObject container, SplineComputer spline, float pillarSpacing, float baseHeight, PathSummary pathData, string variantBase, string typeBase,
        bool skipStart = false, bool skipEnd = false)
    {
        GeneratePillarsCore(container, spline, pillarSpacing, baseHeight, pathData, variantBase, typeBase, null, skipStart, skipEnd);
    }

    /// <summary>完成時と同じ干渉判定済みの橋脚を、接地原点を持つ施工単位として返す。</summary>
    public static void GenerateConstructionPillars(GameObject container, SplineComputer spline, float spacing,
        float baseHeight, PathSummary path, string variantBase, string typeBase, System.Action<GameObject, double> onSupport)
    {
        GeneratePillarsCore(container, spline, spacing, baseHeight, path, variantBase, typeBase, onSupport);
    }

    private static void GeneratePillarsCore(GameObject container, SplineComputer spline, float pillarSpacing,
        float baseHeight, PathSummary pathData, string variantBase, string typeBase, System.Action<GameObject, double> onSupport,
        bool skipStart = false, bool skipEnd = false)
    {
        // プレハブのロード（バリアント→タイプのフォールバック）
        string pillarPath = $"{variantBase}/pillar";
        string fallbackPillarPath = $"{typeBase}/pillar";
        GameObject pillarPrefab = null;
        if (ModelManager.Instance != null)
        {
            pillarPrefab = ModelManager.Instance.GetModel(pillarPath);
            if (pillarPrefab == null && !string.IsNullOrEmpty(fallbackPillarPath))
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar '{pillarPath}' なし。フォールバック試行: '{fallbackPillarPath}'");
                pillarPrefab = ModelManager.Instance.GetModel(fallbackPillarPath);
                if (pillarPrefab != null)
                {
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar フォールバック成功: '{fallbackPillarPath}'");
                }
            }
        }

        // pillar_cap のロード
        string pillarCapPath = $"{variantBase}/pillar_cap";
        string fallbackPillarCapPath = $"{typeBase}/pillar_cap";
        GameObject pillarCapPrefab = null;
        if (ModelManager.Instance != null)
        {
            pillarCapPrefab = ModelManager.Instance.GetModel(pillarCapPath);
            if (pillarCapPrefab == null && !string.IsNullOrEmpty(fallbackPillarCapPath))
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar_cap '{pillarCapPath}' なし。フォールバック試行: '{fallbackPillarCapPath}'");
                pillarCapPrefab = ModelManager.Instance.GetModel(fallbackPillarCapPath);
                if (pillarCapPrefab != null)
                {
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar_cap フォールバック成功: '{fallbackPillarCapPath}'");
                }
            }
        }

        if (pillarPrefab == null && pillarCapPrefab == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar/pillar_cap プレハブ両方なし: '{pillarPath}', '{pillarCapPath}'");
            return;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] pillar プレハブロード状況: pillar={(pillarPrefab != null ? "あり" : "なし")}, cap={(pillarCapPrefab != null ? "あり" : "なし")}");

        // アンカー点のリストを構築（始点、ウェイポイント、終点）。
        // CalculatePathPoints で base_height 分のYシフトが適用済みのスプラインに投影するため、
        // アンカーのYも base_height を引いてスプラインと合わせる。
        List<Vector3> anchors = new List<Vector3>();

        var startList = GeneratedModelUtils.ToFloatList(pathData.start_point);
        if (startList != null && startList.Count >= 2)
        {
            float startRelY = startList.Count > 2 ? startList[1] : 0f;
            float startGroundY = Utils.GetGroundHeight(new Vector3(startList[0], 0f, startList[startList.Count - 1]));
            anchors.Add(new Vector3(startList[0], startGroundY + startRelY - baseHeight, startList[startList.Count - 1]));
        }

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                Vector3 wpAnchor = wp.GetAbsolutePosition();
                wpAnchor.y -= baseHeight;
                anchors.Add(wpAnchor);
            }
        }

        var endList = GeneratedModelUtils.ToFloatList(pathData.end_point);
        if (endList != null && endList.Count >= 2)
        {
            float endRelY = endList.Count > 2 ? endList[1] : 0f;
            float endGroundY = Utils.GetGroundHeight(new Vector3(endList[0], 0f, endList[endList.Count - 1]));
            anchors.Add(new Vector3(endList[0], endGroundY + endRelY - baseHeight, endList[endList.Count - 1]));
        }

        if (anchors.Count < 2)
        {
            Debug.LogWarning("[RoadPath] アンカー点が2点未満のため柱を配置できません");
            return;
        }

        float splineLength = spline.CalculateLength();
        if (splineLength <= 0f || spline.sampleCount == 0)
        {
            Debug.LogWarning("[RoadPath] スプライン長が0以下のため柱を配置できません");
            return;
        }

        // 各アンカー点に最も近いスプライン上のパーセントを特定する
        List<double> anchorPercents = new List<double>();
        for (int i = 0; i < anchors.Count; i++)
        {
            double pct = FindClosestPercentOnSpline(spline, anchors[i]);
            anchorPercents.Add(pct);
        }

        var obstacles = PillarClearance.NeighborProvider?.Invoke(pathData);
        var pillarBounds = PillarClearance.GetPrefabBounds(pillarPrefab);
        var capBounds = PillarClearance.GetPrefabBounds(pillarCapPrefab);
        var portal = PortalPillar.Load(variantBase, typeBase);
        int placedCount = 0;
        // 橋脚の梁は、その地点の路面の横幅の倍率(線路のつなぎ目のS字。PathDrawContext.Warp)だけ横に伸ばす。
        PartWarp warp = PathDrawContext.Warp;

        bool TryPlace(double percent)
        {
            float capWidthScale = warp?.Scale != null ? warp.Scale(ClampSplinePercent(percent)) : 1f;
            if (onSupport == null)
                return PlacePillar(container, pillarPrefab, pillarCapPrefab, pillarBounds, capBounds, obstacles, spline, percent, baseHeight, capWidthScale, portal);
            Vector3 position = spline.EvaluatePosition(ClampSplinePercent(percent));
            float ground = Utils.GetGroundHeight(position);
            // キャップ下端が地面に届く高さが無い地点は施工単位にしない（PlacePillar と同じキャップ厚の定義）。
            float capThickness = capBounds.HasValue ? Mathf.Max(0f, -capBounds.Value.min.y * (pillarCapPrefab != null ? pillarCapPrefab.transform.localScale.y : 1f)) : 0f;
            if (position.y - ground + baseHeight - capThickness <= 0.1f) return false;
            var support = new GameObject("ConstructionSupport");
            support.transform.SetParent(container.transform, false);
            support.transform.position = new Vector3(position.x, ground, position.z);
            if (!PlacePillar(support, pillarPrefab, pillarCapPrefab, pillarBounds, capBounds, obstacles, spline, percent, baseHeight, capWidthScale, portal))
            {
                UnityEngine.Object.Destroy(support);
                return false;
            }
            onSupport(support, percent);
            return true;
        }

        // パス全体の開始点（最初のアンカー点）も干渉判定付きの配置候補とする。
        // 各セグメントのループでは終端側のみに柱を配置するため、開始点にも個別に配置することで、
        // 重複を防ぎつつパス全体の開始端と終了端の両方に柱を過不足なく配置するための仕様。
        if (anchorPercents.Count > 0 && !skipStart)
        {
            if (TryPlace(anchorPercents[0]))
            {
                placedCount++;
            }
        }

        // 各セグメント（アンカー点間）ごとに処理
        for (int i = 0; i < anchors.Count - 1; i++)
        {
            double startPct = anchorPercents[i];
            double endPct = anchorPercents[i + 1];
            bool lastSegment = i == anchors.Count - 2;

            // セグメントのスプライン上の長さを概算
            float segSplineLength = EstimateSegmentSplineLength(spline, startPct, endPct);
            if (segSplineLength <= 0.01f) continue;

            if (segSplineLength > pillarSpacing)
            {
                // セグメント長が pillar_spacing を超える場合は均等分割して配置する。
                // これにより、短いセグメントが連続した際に柱が密集するのを防ぐ。
                int divisions = Mathf.CeilToInt(segSplineLength / pillarSpacing);
                for (int j = 1; j <= divisions; j++)
                {
                    if (skipEnd && lastSegment && j == divisions) continue;
                    double t = (double)j / divisions;
                    double placementPct = startPct + (endPct - startPct) * t;
                    if (TryPlace(placementPct))
                    {
                        placedCount++;
                    }
                }
            }
            else
            {
                // 短いセグメントは終点に1本のみ配置する
                if (!(skipEnd && lastSegment) && TryPlace(endPct))
                {
                    placedCount++;
                }
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[RoadPath] 柱配置完了: {placedCount}本");
    }

    /// <summary>
    /// スプライン評価用パーセントを [0,1] に丸める。
    /// Dreamteck の SplineComputer.Evaluate はパーセントをクランプせず、
    /// 1.0 を浮動小数点誤差で 1ULP でも超えると内部サンプル配列の範囲外アクセス
    /// (IndexOutOfRangeException) になる (startPct + (endPct - startPct) * 1.0 の丸めで発生しうる)。
    /// NaN も FloorInt 経由で不正なインデックスになるため 0 に落とす。
    /// スプラインを Evaluate/Project する際は必ずこれを通すこと。
    /// </summary>
    public static double ClampSplinePercent(double percent)
    {
        if (double.IsNaN(percent)) return 0.0;
        if (percent < 0.0) return 0.0;
        if (percent > 1.0) return 1.0;
        return percent;
    }

    /// <summary>
    /// スプライン上で指定した点に最も近いパーセントを求める。
    /// </summary>
    private static double FindClosestPercentOnSpline(SplineComputer spline, Vector3 worldPos)
    {
        return ClampSplinePercent(spline.Project(worldPos).percent);
    }

    /// <summary>
    /// スプラインの2つのパーセント間の長さを概算する。
    /// 中間点を10点サンプリングして累積距離を計算する。
    /// </summary>
    private static float EstimateSegmentSplineLength(SplineComputer spline, double startPct, double endPct)
    {
        int samples = 10;
        float totalDist = 0f;
        Vector3 prevPos = spline.Evaluate(ClampSplinePercent(startPct)).position;

        for (int i = 1; i <= samples; i++)
        {
            double pct = ClampSplinePercent(startPct + (endPct - startPct) * i / samples);
            Vector3 pos = spline.Evaluate(pct).position;
            totalDist += Vector3.Distance(prevPos, pos);
            prevPos = pos;
        }

        return totalDist;
    }

    /// <summary>
    /// 指定したスプライン位置に柱（pillar）およびキャップ（pillar_cap）を配置する。
    /// pillar_cap はY方向スケールせず路面位置（=サンプル点）に置く。
    /// pillar は cap の厚み分だけ下にオフセットした位置から地面（localGroundY）まで伸びる。
    /// CalculatePathPoints で base_height 分のYシフトが適用済みのため、sample.position.y は
    /// すでに (groundY + (relY - base_height) + offset) になっており、柱は地面まで伸びる。
    /// 「3D描画上の都合」で、ゲームロジックには影響しない。
    /// base_height を持つバリアント（送電線）はスプラインが地表にあり、キャップ＝鉄塔がモデル自体で
    /// 地上高を持つため、路面と地表の差に base_height を足した値を「構造物の高さ」として
    /// 置く／置かないを判定する。柱本体は路面と地表の差の分だけ伸ばす。
    /// </summary>
    private static bool PlacePillar(GameObject container, GameObject pillarPrefab, GameObject pillarCapPrefab,
        Bounds? pillarBounds, Bounds? capBounds, List<PillarClearance.Corridor> obstacles,
        SplineComputer spline, double percent, float baseHeight, float capWidthScale = 1f, PortalPillar portal = null)
    {
        SplineSample sample = spline.Evaluate(ClampSplinePercent(percent));
        Vector3 forward = sample.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        Quaternion rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        Vector3 capScale = pillarCapPrefab != null ? pillarCapPrefab.transform.localScale : Vector3.one;
        capScale.x *= capWidthScale;
        float capThickness = capBounds.HasValue ? Mathf.Max(0f, -capBounds.Value.min.y * capScale.y) : 0f;
        Vector3 pillarPosition = sample.position + Vector3.down * capThickness;
        float height = pillarPosition.y - Utils.GetGroundHeight(sample.position);
        // 地表に張り付いた区間（地上線路・切り通し）には柱もキャップも置かない。
        if (height + baseHeight <= 0.1f) return false;
        // 柱本体は路面と地表の隙間を埋める部材なので、隙間が無ければキャップだけを置く。
        bool needsPillar = pillarPrefab != null && height > 0.1f;
        Vector3 pillarScale = pillarPrefab != null ? pillarPrefab.transform.localScale : Vector3.one;
        // 橋脚モデルは実寸で著作され、種類ごとに高さが異なる。1mモデルとは限らない。
        pillarScale.y = height / Mathf.Max(0.001f, pillarBounds.GetValueOrDefault().size.y);

        // 通常橋脚を優先し、干渉する地点だけ独立した柱と横梁で通行帯をまたぐ。
        bool blocked = capBounds.HasValue && PillarClearance.Intersects(Utils.TransformBounds(capBounds.Value,
                Matrix4x4.TRS(sample.position, rotation, capScale)), obstacles);
        blocked |= needsPillar && pillarBounds.HasValue && PillarClearance.Intersects(Utils.TransformBounds(pillarBounds.Value,
                Matrix4x4.TRS(pillarPosition, rotation, pillarScale)), obstacles);
        if (blocked)
        {
            float top = sample.position.y + (capBounds.HasValue ? capBounds.Value.max.y * capScale.y : 0f);
            float halfWidth = capBounds.HasValue ? capBounds.Value.extents.x * capScale.x : pillarBounds.GetValueOrDefault().extents.x;
            return portal != null && portal.TryPlace(container, sample.position, rotation, top, halfWidth, obstacles);
        }

        bool placed = false;
        if (pillarCapPrefab != null)
        {
            var cap = UnityEngine.Object.Instantiate(pillarCapPrefab, container.transform);
            cap.transform.position = sample.position;
            cap.transform.rotation = rotation;
            cap.transform.localScale = capScale;
            placed = true;
        }
        if (needsPillar)
        {
            var pillar = UnityEngine.Object.Instantiate(pillarPrefab, container.transform);
            pillar.transform.position = pillarPosition;
            pillar.transform.rotation = rotation;
            pillar.transform.localScale = pillarScale;
            placed = true;
        }
        return placed;
    }

    // PathManager の工事中パス生成（土ストリップの幅・柱の配置判定）からも参照するため public。
    public static void GetPathMasterParams(PathSummary pathData, out float width, out float sidewalkWidth, out float sideWidth, out float? treeSpacing, out int lanes, out float wallWidth, out float centerWidth, out float? pillarSpacing, out bool heightEditable, out float baseHeight, out bool singleSpan)
    {
        width = 4.0f;
        sidewalkWidth = 0.5f;
        sideWidth = 0.5f;
        treeSpacing = null;
        lanes = 2;
        wallWidth = 0f;
        centerWidth = 0f;
        pillarSpacing = null;
        heightEditable = false;
        baseHeight = 0f;
        singleSpan = false;

        if (MasterDataStore.Instance == null)
        {
            Debug.LogWarning("[RoadPath] MasterDataStore.Instance が null。デフォルト幅を使用。");
            return;
        }

        var master = MasterDataStore.Instance.GetPathMaster(pathData.path_variant);
        if (master != null)
        {
            width = master.width;
            if (master.sidewalk_width.HasValue) sidewalkWidth = master.sidewalk_width.Value;
            if (master.side_width.HasValue) sideWidth = master.side_width.Value;
            if (master.tree_spacing.HasValue) treeSpacing = master.tree_spacing.Value;
            if (master.lanes.HasValue) lanes = master.lanes.Value;
            else lanes = Mathf.Max(1, Mathf.RoundToInt(width / 3.5f));
            if (master.wall_width.HasValue) wallWidth = master.wall_width.Value;
            if (master.center_width.HasValue) centerWidth = master.center_width.Value;
            if (master.pillar_spacing.HasValue) pillarSpacing = master.pillar_spacing.Value;
            if (master.height_editable.HasValue) heightEditable = master.height_editable.Value;
            if (master.base_height.HasValue) baseHeight = master.base_height.Value;
            if (master.single_span.HasValue) singleSpan = master.single_span.Value;
            if (LocalConstants.UNITY_LOG_ROAD) Debug.Log($"[RoadPath] マスターデータ取得成功: width={width}, sidewalkWidth={sidewalkWidth}, sideWidth={sideWidth}, treeSpacing={treeSpacing}, lanes={lanes}, wallWidth={wallWidth}, centerWidth={centerWidth}, pillarSpacing={pillarSpacing}, heightEditable={heightEditable}, baseHeight={baseHeight}, singleSpan={singleSpan}");
        }
        else
        {
            Debug.LogWarning($"[RoadPath] マスターデータなし (variant={pathData.path_variant})。デフォルト幅を使用。");
            lanes = Mathf.Max(1, Mathf.RoundToInt(width / 3.5f));
        }
    }
}


/// <summary>
/// 手続き生成したメッシュと SplineMesh が生成したメッシュを GO の寿命に合わせて解放する。共有プレハブのメッシュは登録しない。
/// 非同期ベイク中のメッシュを即座に破棄しないよう MeshBakeService 経由で解放する。
/// </summary>
public sealed class IntersectionMeshOwner : MonoBehaviour
{
    public Mesh OwnedMesh;
    private void OnDestroy()
    {
        MeshBakeService.Release(OwnedMesh);
    }
}

/// <summary>
/// 道路の区画線と車両描画が共用する走行帯。車線番号は進行方向の路肩側から数える。
/// 奇数の双方向道路は中央1車線を転回用として確保し、通常走行には使わない。
/// </summary>
public readonly struct RoadLaneLayout
{
    public float Outer { get; }
    public float Inner { get; }
    public int Count { get; }
    public bool OneWay { get; }
    private readonly bool centered;
    private readonly int totalLanes;
    private readonly float divider;

    public RoadLaneLayout(float width, float sidewalk, float side, float center, int lanes, int direction)
    {
        Outer = Mathf.Max(0, width * .5f - Mathf.Max(0, sidewalk) - Mathf.Max(0, side) * .5f);
        OneWay = direction != Constants.DIR_BOTH;
        totalLanes = Mathf.Max(1, lanes);
        divider = Mathf.Max(0, center) * .5f;
        Inner = OneWay ? 0 : divider;
        if (!OneWay && totalLanes > 1 && totalLanes % 2 != 0)
            Inner = Mathf.Max(Inner, Outer / totalLanes);
        centered = Outer <= Inner || (!OneWay && totalLanes <= 1);
        Count = centered ? 1 : (OneWay ? totalLanes : Mathf.Max(1, totalLanes / 2));
    }

    public float Center(int lane, int trafficHand)
    {
        if (centered) return 0;
        lane = Mathf.Clamp(lane, 0, Count - 1);
        float offset = OneWay ? Outer - 2 * Outer / Count * (lane + .5f)
            : Outer - (Outer - Inner) / Count * (lane + .5f);
        return offset * (trafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1);
    }

    public IEnumerable<(float offset, bool dashed)> Boundaries()
    {
        if (centered) yield break;
        if (OneWay)
        {
            for (int i = 1; i < Count; i++) yield return (-Outer + 2 * Outer * i / Count, true);
            yield break;
        }
        if (divider <= 0)
        {
            if (Inner > 0) { yield return (-Inner, true); yield return (Inner, true); }
            else yield return (0, totalLanes == 2);
        }
        for (int i = 1; i < Count; i++)
        {
            float offset = Inner + (Outer - Inner) * i / Count;
            yield return (-offset, true);
            yield return (offset, true);
        }
    }
}
