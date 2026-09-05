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
    private static Material cachedFallbackMaterial;

    // Extrude配置の1タイルの最大長（m）。
    // SplineMesh はタイル内の頂点Z位置でしかスプラインを評価しないため、
    // Z方向の分割を持たないプレハブ（bodyは10mの箱）をそのままタイルにすると、
    // 縦カーブ（尾根・谷）でタイル全体が弦（直線）になり、細かく分割される他パーツ
    // （ラインなど）と上下にずれて貫通・浮きが発生する。タイル長を制限して弦誤差を抑える。
    public const float MAX_EXTRUDE_TILE_LENGTH = 2.5f;

    /// <summary>
    /// 道路パーツ用のマテリアルをキャッシュ経由で取得する。
    /// FBX由来のマテリアルを、同名の Resources マテリアルがあればそちらへ再割当し、
    /// Zファイティング回避の renderQueue+1 を一度だけ適用する。
    /// 交差点メッシュ・歩道コネクタなど、道路と見た目を揃えたいすべての生成器から使うこと
    /// （プレハブの sharedMaterials を直接使うと、通常道路とマテリアルの解決結果がずれる）。
    /// </summary>
    public static Material GetOrCreateRoadMaterial(Material source)
    {
        if (source == null) return null;
        string key = source.name;

        Material fromResources = GetOrCreateRoadMaterialByName(key);
        if (fromResources != null) return fromResources;

        // Resources に同名マテリアルが無いので、FBX由来のマテリアルを複製して共有プールに載せる。
        Material targetMat = new Material(source);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   マテリアルキャッシュ生成(clone): '{key}'");
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
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   マテリアルキャッシュ生成(Resources): '{materialName}'");
        RegisterRoadMaterial(materialName, targetMat);
        return targetMat;
    }

    private static void RegisterRoadMaterial(string key, Material material)
    {
        int baseQueue = material.renderQueue < 0 ? material.shader.renderQueue : material.renderQueue;
        material.renderQueue = baseQueue + 1;
        MaterialCache[key] = material;
    }

    private static Material GetFallbackStandardMaterial()
    {
        if (cachedFallbackMaterial != null)
            return cachedFallbackMaterial;
        cachedFallbackMaterial = new Material(Shader.Find("Standard"));
        return cachedFallbackMaterial;
    }

    /// <summary>路面幅は桁の張り出しを含む全体 bounds ではなく、最上面の幅から求める。</summary>
    public static Bounds GetBodySurfaceBounds(Mesh mesh)
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
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   ミラーメッシュキャッシュヒット: '{key}'");
            return cached;
        }

        Mesh mirrored = CreateMirroredMeshInternal(originalMesh);
        MirroredMeshCache[key] = mirrored;
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   ミラーメッシュキャッシュ生成: '{key}'");
        return mirrored;
    }

    /// <summary>同じモデル系列の路面原点を使い、付属パーツに含まれる路面高を二重加算しない。</summary>
    public static Mesh GetRoadPartMesh(Mesh source, string resourcePath)
    {
        if (source == null) return null;
        string part = resourcePath.Substring(resourcePath.LastIndexOf('/') + 1);
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
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] Generate開始: path_id={pathData?.path_id ?? -1}, path_variant={pathData?.path_variant ?? -1}, path_type={pathData?.path_type ?? -1}, points={points?.Count ?? 0}");

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
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] コンテナ作成: {container.name}");

        SplineComputer spline = BuildSpline(container, points, isCurved);

        float splineLength = spline.CalculateLength();
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] スプライン長: {splineLength:F2}m");

        // 道路パーツ生成
        CreateRoadParts(container, spline, width, sidewalkWidth, sideWidth, lanes, treeSpacing, wallWidth, centerWidth, pillarSpacing, heightEditable, baseHeight, singleSpan, pathData, points, startSidewalkSetback, endSidewalkSetback);

        // コライダー用メッシュの生成
        Mesh colliderMesh = BuildColliderMeshFromSpline(spline, width);
        if (colliderMesh != null)
        {
            colliderMesh.name = $"RoadCollider_{pathData.path_id}";
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] コライダーメッシュ生成成功: 頂点数={colliderMesh.vertexCount}");
        }
        else
        {
            Debug.LogWarning("[RoadPath] コライダーメッシュ生成失敗");
        }

        int childCount = container.transform.childCount;
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] Generate完了: 子オブジェクト数={childCount}, colliderMesh={(colliderMesh != null ? "あり" : "なし")}");

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
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] Generate直後: Renderer={allRenderers.Length}件, bounds中心={totalBounds.center}, boundsサイズ={totalBounds.size}");
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
    public static GameObject CreateSplineMesh(GameObject parent, SplineComputer spline, string prefabName, float width, float centerOffsetX, float offsetY, bool mirrorX = false, string fallbackPrefabName = null, bool isDashed = false, bool stretchToWidth = true, double clipFrom = 0.0, double clipTo = 1.0, bool singleSpan = false, bool compensatePivot = true, float maxTileLength = MAX_EXTRUDE_TILE_LENGTH)
    {
        // プレハブのロード
        GameObject prefab = null;
        string resolvedPrefabName = prefabName;
        if (ModelManager.Instance == null)
        {
            Debug.LogWarning($"[RoadPath] ModelManager.Instance が null。プレハブ '{prefabName}' をロードできません。");
        }
        else
        {
            prefab = ModelManager.Instance.GetModel(prefabName);
            if (prefab == null && !string.IsNullOrEmpty(fallbackPrefabName))
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] '{prefabName}' なし。タイプフォールバック試行: '{fallbackPrefabName}'");
                prefab = ModelManager.Instance.GetModel(fallbackPrefabName);
                if (prefab != null)
                {
                    resolvedPrefabName = fallbackPrefabName;
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] フォールバック成功: '{fallbackPrefabName}' を使用");
                }
            }
        }

        if(prefab == null)
        {
            Debug.LogWarning($"[RoadPath] プレハブなし: '{prefabName}'");
            return null;
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] プレハブ取得成功: '{prefabName}', mirrorX={mirrorX}");

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

        if(mf != null && mf.sharedMesh != null)
        {
             bool isBody = prefabName.EndsWith("/body", StringComparison.Ordinal);
             Mesh meshToUse = isBody ? GetBodySurfaceMesh(mf.sharedMesh) : GetRoadPartMesh(mf.sharedMesh, resolvedPrefabName);
             Bounds fittingBounds = isBody ? GetBodySurfaceBounds(meshToUse) : meshToUse.bounds;
             UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   メッシュ: '{mf.sharedMesh.name}', bounds={mf.sharedMesh.bounds.size}, vertexCount={mf.sharedMesh.vertexCount}");

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
             UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   scaleX計算: meshWidth={meshWidth:F3} → scaleX={scaleX:F3} (targetWidth={width}), pivotCompensationX={pivotCompensationX:F3}");

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
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   マテリアル割当完了: {cachedMaterials.Length}件");
            }
            else
            {
                targetMr.material = GetFallbackStandardMaterial();
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
                    double halfDashPercent = (dashLength * 0.5f) / splineLen;
                    channel.clipFrom = halfDashPercent;
                    channel.clipTo = 1.0 - halfDashPercent;
                    float usableLen = splineLen - dashLength;
                    channel.count = Mathf.Max(2, Mathf.FloorToInt(usableLen / dashSpacing) + 1);
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
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   点線チャンネル設定: offset=({offsetX:F3}, {offsetY:F3}), count={channel.count}, clip=({channel.clipFrom:F3}〜{channel.clipTo:F3}), scale=({scaleX:F3}, 1, {scaleZ:F3})");
            }
            else
            {
                channel.minScale = new Vector3(scaleX, 1f, 1f);
                channel.maxScale = new Vector3(scaleX, 1f, 1f);

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
                    float tileLen = maxTileLength > 0f ? Mathf.Min(meshLenZ, maxTileLength) : meshLenZ;
                    float clippedLen = splineLen * (float)System.Math.Max(0.0, clipTo - clipFrom);
                    channel.count = Mathf.Max(1, Mathf.RoundToInt(clippedLen / tileLen));
                }
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath]   チャンネル設定: offset=({offsetX:F3}, {offsetY:F3}), scale={scaleX:F3}, count={channel.count} (singleSpan={singleSpan}, clip={clipFrom:F3}〜{clipTo:F3})");
            }

            // ===== 生成前の頂点数バジェットガード =====
            // SplineMesh の生成頂点数は「パーツの頂点数 × タイル数(count)」で決まる。
            // Z実寸が極端に小さいFBX(cm単位エクスポート等)や高密度なFBXに差し替えられると
            // count×頂点数が爆発し、Unity標準メッシュの上限(65535頂点)を超えて Dreamteck が
            // 毎フレームエラーを出し続け、エディタが操作不能になる(既往障害)。
            // 上限内に収まるよう count をクランプし、パーツ1個で超える場合は生成自体を中止する。
            if (mf != null && mf.sharedMesh != null)
            {
                const int SPLINE_MESH_VERTEX_BUDGET = 60000;
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
                    Debug.LogWarning($"[RoadPath] パーツ '{prefabName}' のタイル数 {channel.count} は頂点数上限を超えるため {clampedCount} にクランプします(1個あたり頂点{partVerts}、Z実寸={meshLenZ:F3}m)。Z実寸が異常に小さい場合はcm単位でエクスポートされたFBXの疑いがあります(1 Blenderユニット=1mで再エクスポートすること)。");
                    channel.count = clampedCount;
                }
            }
        }

        splineMesh.updateMethod = SplineUser.UpdateMethod.Update;

        try
        {
            splineMesh.Rebuild();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RoadPath] SplineMesh Rebuild失敗 '{prefabName}': {ex}");
        }

        return go;
    }

    /// <summary>
    /// 点列からスプライン（SplineComputer）を構築する共通処理。
    /// curved=true は内部点を Catmull-Rom 相当の Bézier で補間し、端の接線は切断断面に合わせる。
    /// RebuildImmediate 済みで返すため、直後の CalculateLength / Evaluate が正しい値を返す。
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
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] スプラインポイント設定: {splinePoints.Length}点 curved={isCurved}");

        spline.type = isCurved ? Spline.Type.Bezier : Spline.Type.Linear;
        spline.sampleMode = SplineComputer.SampleMode.Uniform;
        spline.RebuildImmediate();
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

        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetFallbackStandardMaterial();

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
    /// パスに沿って街路樹を配置します。
    /// </summary>
    private void GenerateTrees(GameObject container, SplineComputer spline, float spacing, float offset, List<Vector3> originalPoints, string pathBase, string fallbackPathBase)
    {
        if (spacing <= 0f)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 街路樹: spacing={spacing} は0以下のためスキップ");
            return;
        }

        string treePath = $"{pathBase}/tree";
        string fallbackTreePath = $"{fallbackPathBase}/tree";
        GameObject treePrefab = null;
        if (ModelManager.Instance != null)
        {
            treePrefab = ModelManager.Instance.GetModel(treePath);
            if (treePrefab == null && !string.IsNullOrEmpty(fallbackTreePath))
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 街路樹 '{treePath}' なし。フォールバック試行: '{fallbackTreePath}'");
                treePrefab = ModelManager.Instance.GetModel(fallbackTreePath);
                if (treePrefab != null)
                {
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 街路樹フォールバック成功: '{fallbackTreePath}'");
                }
            }
        }

        if (treePrefab == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 街路樹プレハブなし: '{treePath}' (フォールバック '{fallbackTreePath}' もなし)");
            return;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 街路樹プレハブ取得成功: '{treePath}'");

        float length = spline.CalculateLength();
        if (length <= 0 || spline.sampleCount == 0)
        {
            Debug.LogWarning("[RoadPath] スプライン長が0以下、またはサンプル数が0。街路樹配置スキップ。");
            return;
        }

        float cornerClearance = 0.5f;

        // 道路の両端（交差点の切断面）ぎりぎりに木が立たないよう、
        // 端から間隔の半分だけ内側から配置を始め、終端も同じだけ手前で打ち切る。
        float endClearance = spacing * 0.5f;
        if (length <= endClearance * 2f)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] 街路樹: 道路が短すぎるため配置なし");
            return;
        }
        double currentPercent = spline.Travel(0.0, endClearance, Spline.Direction.Forward);
        double endPercent = spline.Travel(1.0, endClearance, Spline.Direction.Backward);
        if (endPercent <= currentPercent)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] 街路樹: 配置可能区間なし");
            return;
        }
        int maxCount = Mathf.FloorToInt(length / spacing) + 2;
        int currentCount = 0;
        int placedCount = 0;

        while (currentPercent <= endPercent && currentCount < maxCount)
        {
            SplineSample sample = spline.Evaluate(currentPercent);

            if (!IsNearPathCorner(sample.position, originalPoints, cornerClearance))
            {
                PlaceSingleTree(container, treePrefab, sample, -offset);
                PlaceSingleTree(container, treePrefab, sample, offset);
                placedCount += 2;
            }

            double nextPercent = spline.Travel(currentPercent, spacing, Spline.Direction.Forward);

            if (nextPercent <= currentPercent) break;

            currentPercent = nextPercent;
            currentCount++;
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 街路樹配置完了: {placedCount}本 (iterations={currentCount})");
    }

    /// <summary>
    /// パス制御点（角）付近かどうか。角付近では街路樹が重なりやすいため配置をスキップする。
    /// </summary>
    private static bool IsNearPathCorner(Vector3 worldPos, List<Vector3> pathControlPoints, float clearanceMeters)
    {
        if (pathControlPoints == null || pathControlPoints.Count == 0 || clearanceMeters <= 0f)
        {
            return false;
        }

        float clearSqr = clearanceMeters * clearanceMeters;
        for (int i = 0; i < pathControlPoints.Count; i++)
        {
            Vector3 p = pathControlPoints[i];
            float dx = worldPos.x - p.x;
            float dz = worldPos.z - p.z;
            if (dx * dx + dz * dz <= clearSqr)
            {
                return true;
            }
        }

        return false;
    }

    private void PlaceSingleTree(GameObject container, GameObject prefab, SplineSample sample, float offset)
    {
        // 街路樹は地形の傾斜に追従して斜めに立たず、常にワールド上方向に対して垂直に立つ。
        // 位置はスプライン接線の水平成分を向き軸にして横へずらし、回転はYawのみ（ピッチ・ロールなし）とする。
        // これにより、坂道・カントの有無にかかわらず木が直立する。
        Vector3 forward = sample.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        // 水平面上の右方向: WorldUp × 水平Forward。
        // SplineSample の up はスプラインの連続性維持のため terrain-follow で傾くことがあるため、
        // 常に固定の WorldUp を使い、樹の立ち位置が斜面に沿ってずれないようにする。
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        Vector3 position = sample.position + (right * offset);

        // Yawのみの回転（UpDown成分ゼロ）にして、木が垂直を保つようにする。
        // 左右対称に見せるため、道路右側（offset > 0）の木はY軸で180°回転させる。
        Quaternion yawRotation = Quaternion.LookRotation(forward, Vector3.up);
        Quaternion extra = Quaternion.Euler(0f, offset > 0f ? 180f : 0f, 0f);

        GameObject tree = UnityEngine.Object.Instantiate(prefab, container.transform);
        tree.transform.position = position;
        tree.transform.rotation = yawRotation * extra;
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
    private void CreateRoadParts(GameObject container, SplineComputer spline, float width, float sidewalkWidth, float sideWidth, int lanes, float? treeSpacing, float wallWidth, float centerWidth, float? pillarSpacing, bool heightEditable, float baseHeight, bool singleSpan, PathSummary pathData, List<Vector3> controlPoints, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        string variantBase = $"Models/Paths/p{pathData.path_variant}";
        string typeBase = $"Models/Paths/p{pathData.path_type}";
        // 車線描画のデバッグログはパフォーマンス確保のため、UNITY_LOG_ROADが有効な場合のみ出力する。
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] CreateRoadParts開始: variant={variantBase}, type={typeBase}, width={width}, lanes={lanes}, sidewalkWidth={sidewalkWidth}, sideWidth={sideWidth}, wallWidth={wallWidth}, centerWidth={centerWidth}");

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] モデルベースパス: variant={variantBase}, type(フォールバック)={typeBase}");

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] --- 道路パーツ生成開始 ---");

        // Road Body
        // 地形解像度限界・Zファイティングによる埋没を回避するため、道路本体はスプラインから浮かせる。
        // single_span=true の場合は body をタイル化せず1枚で引き伸ばす（アンカー点間の直線スパン）。
        GameObject roadBody = CreateSplineMesh(container, spline, $"{variantBase}/body", width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body", singleSpan: singleSpan);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] body: {(roadBody != null ? "生成成功" : "生成失敗（プレハブなし？）")}");

        if (roadBody == null)
        {
            // モデルがないため、ただの白い板にフォールバックする
            roadBody = CreateFallbackRoadBody(container, spline, width);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] body: モデルなしのため白い板にフォールバック");
        }

        // オフセット計算（CreateSplineMesh に渡すのはすべて「パーツ中心の位置」。
        // プレハブのピボット位置の差は CreateSplineMesh 側で bounds 中心基準に補正される）
        bool hasSidewalk = sidewalkWidth > 0f;

        // 歩道は道路の端に覆いかぶさる帯 [width/2 - sidewalkWidth, width/2] を占める。
        // 街路樹は縁石（帯の内側端）から歩道幅の1/4程度外側に配置する。
        float treeOffset = (width * 0.5f) - sidewalkWidth + (sidewalkWidth * 0.25f);

        // 道路脇の線は、歩道の内側端（縁石）から sideWidth/2 だけ内側に入った位置を中心として配置する。
        float sideLineCenterOffset = (width * 0.5f) - sidewalkWidth - (sideWidth * 0.5f);
        // 線の描画太さは sideWidth の3割に統一する。
        float lineWidth = sideWidth * 0.3f;
        // ラインは道路本体の上面（BODY_ABOVE_SPLINE）より高く浮かせてZファイティングを回避する
        float lineOffsetY = RoadPartHeights.LINE_ABOVE_SPLINE;

        // Side Lines (道路脇の実線)
        // side_width=0 は「区画線を持たない道路」を意味するため、側線も中央線も一切引かない
        // （線の太さは side_width から導くので、0 のまま描くと潰れたメッシュになる）。
        bool hasLine = sideWidth > 0f;
        if (hasLine)
        {
            // 左側は mirrorX で反転させ、プレハブが非対称でも左右対称に見えるようにする。
            GameObject leftLine = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, -sideLineCenterOffset, lineOffsetY, true, $"{typeBase}/line");
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] side line(左): {(leftLine != null ? "生成成功" : "生成失敗")}");
            GameObject rightLine = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, sideLineCenterOffset, lineOffsetY, false, $"{typeBase}/line");
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] side line(右): {(rightLine != null ? "生成成功" : "生成失敗")}");
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] sideWidth <= 0: 区画線なし");
        }

        // 中央分離帯（center）の配置。中央分離帯があればセンターラインは不要。
        // 中央分離帯のパーツがなければ line パーツにフォールバックする。
        bool hasCenterObj = false;
        if (centerWidth > 0f)
        {
            GameObject centerObj = CreateSplineMesh(container, spline, $"{variantBase}/center", centerWidth, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/center");
            if (centerObj == null && hasLine)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] center パーツなし。line にフォールバック。");
                centerObj = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, 0f, lineOffsetY, false, $"{typeBase}/line");
            }
            hasCenterObj = centerObj != null;
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] center: {(centerObj != null ? "生成成功" : "生成失敗")}");
        }

        // センターライン・通行帯線
        if (!hasLine)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] sideWidth <= 0: 中央線・車線境界線なし");
        }
        else if (hasCenterObj)
        {
            // 中央分離帯あり: センターラインは不要。
            // 中央分離帯と側線の間をレーン数で等分し、境界に点線を配置する。
            // 例: 4車線 → 片側2レーンなので中央分離帯と側線の間に点線1本。
            // 例: 6車線 → 片側3レーンなので中央分離帯と側線の間に点線2本。
            float halfCenter = centerWidth * 0.5f;
            float halfLaneCount = lanes / 2;
            float laneSectionWidth = sideLineCenterOffset - halfCenter;
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 中央分離帯あり: halfCenter={halfCenter:F3}, halfLaneCount={halfLaneCount}, laneSectionWidth={laneSectionWidth:F3}, sideLineCenterOffset={sideLineCenterOffset:F3}");
            if (laneSectionWidth > 0f && halfLaneCount > 1)
            {
                float laneWidth = laneSectionWidth / halfLaneCount;
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 中央分離帯あり laneWidth={laneWidth:F3}");
                for (int i = 1; i < halfLaneCount; i++)
                {
                    float offsetX = halfCenter + (laneWidth * i);
                    GameObject leftDash = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, offsetX, lineOffsetY, false, $"{typeBase}/line", isDashed: true);
                    GameObject rightDash = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, -offsetX, lineOffsetY, false, $"{typeBase}/line", isDashed: true);
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] lane line[{i}] offsetX={offsetX:F3} (左): {(leftDash != null ? "生成成功" : "生成失敗")}, (右): {(rightDash != null ? "生成成功" : "生成失敗")}");
                }
            }
            else
            {
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] 中央分離帯ありだが laneSectionWidth <= 0 または halfLaneCount <= 1 のため lane line を生成しません");
            }
        }
        else
        {
            // 中央分離帯なし: 従来通りのセンターライン・車線境界線を描画する。
            float laneWidth = width / Mathf.Max(1, lanes);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 中央分離帯なし: lanes={lanes}, laneWidth={laneWidth:F3}");
            if (lanes == 2)
            {
                // 2車線: 中央に点線1本
                GameObject centerLine = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, 0f, lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line", isDashed: true);
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] center line(点線): {(centerLine != null ? "生成成功" : "生成失敗")}");
            }
            else if (lanes % 2 == 0)
            {
                // 4車線以上の偶数: 中央に実線、各車線境界に点線
                GameObject centerSolid = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, 0f, lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line");
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] center line(実線): {(centerSolid != null ? "生成成功" : "生成失敗")}");
                for (int i = 1; i < lanes / 2; i++)
                {
                    float offsetX = laneWidth * i;
                    GameObject leftDash = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, offsetX, lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line", isDashed: true);
                    GameObject rightDash = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, -offsetX, lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line", isDashed: true);
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] lane line[{i}] offsetX={offsetX:F3} (左): {(leftDash != null ? "生成成功" : "生成失敗")}, (右): {(rightDash != null ? "生成成功" : "生成失敗")}");
                }
            }
            else if (lanes <= 1)
            {
                // 1車線: 分ける対向車線が存在しないため、中央線も車線境界線も引かない。
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] lanes <= 1: 中央線・車線境界線なし");
            }
            else
            {
                // 奇数車線: 中央に点線、さらに車線境界にも点線
                GameObject centerLine = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, 0f, lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line", isDashed: true);
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] center line(点線): {(centerLine != null ? "生成成功" : "生成失敗")}");
                for (int i = 1; i <= lanes / 2; i++)
                {
                    float offsetX = laneWidth * i;
                    GameObject leftDash = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, offsetX, lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line", isDashed: true);
                    GameObject rightDash = CreateSplineMesh(container, spline, $"{variantBase}/line", lineWidth, -offsetX, lineOffsetY + RoadPartHeights.CENTER_LINE_EXTRA, false, $"{typeBase}/line", isDashed: true);
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] lane line[{i}] offsetX={offsetX:F3} (左): {(leftDash != null ? "生成成功" : "生成失敗")}, (右): {(rightDash != null ? "生成成功" : "生成失敗")}");
                }
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

            GameObject leftSidewalk = CreateSplineMesh(container, spline, $"{variantBase}/sidewalk", sidewalkWidth, -sidewalkCenterOffset, RoadPartHeights.SIDEWALK_ABOVE_SPLINE, false, $"{typeBase}/sidewalk", clipFrom: leftClipFrom, clipTo: leftClipTo);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] sidewalk(左): {(leftSidewalk != null ? "生成成功" : "生成失敗")} clip=({leftClipFrom:F3}〜{leftClipTo:F3})");
            GameObject rightSidewalk = CreateSplineMesh(container, spline, $"{variantBase}/sidewalk", sidewalkWidth, sidewalkCenterOffset, RoadPartHeights.SIDEWALK_ABOVE_SPLINE, true, $"{typeBase}/sidewalk", clipFrom: rightClipFrom, clipTo: rightClipTo);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] sidewalk(右・ミラー): {(rightSidewalk != null ? "生成成功" : "生成失敗")} clip=({rightClipFrom:F3}〜{rightClipTo:F3})");
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
            float wallCenterOffset = (width * 0.5f) + (wallWidth * 0.5f);
            GameObject leftWall = CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, -wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, true, $"{typeBase}/wall");
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] wall(左・ミラー): {(leftWall != null ? "生成成功" : "生成失敗")}");
            GameObject rightWall = CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, false, $"{typeBase}/wall");
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] wall(右): {(rightWall != null ? "生成成功" : "生成失敗")}");
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] wallWidth <= 0: 壁なし");
        }

        // 街路樹
        if (treeSpacing.HasValue && treeSpacing.Value > 0)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 街路樹: spacing={treeSpacing.Value}m で配置開始");
            GenerateTrees(container, spline, treeSpacing.Value, treeOffset, controlPoints, variantBase, typeBase);
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[RoadPath] 街路樹: spacing未指定のためスキップ");
        }

        // 柱
        // 地下パス（負の相対Yを持つ）は路面が地面より低く、路面支持の柱は不要なので配置しない
        // （開削区間の路面は掘削後の地面に接する。高架と地下が混在するパスは想定外で柱なしになる）。
        if (heightEditable && pillarSpacing.HasValue && pillarSpacing.Value > 0
            && !TunnelSectionUtil.IsUndergroundPath(pathData))
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 柱: spacing={pillarSpacing.Value}m, base_height={baseHeight}m で配置開始");
            GeneratePillars(container, spline, pillarSpacing.Value, baseHeight, pathData, variantBase, typeBase);
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
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] --- 道路パーツ生成完了 --- 子オブジェクト数={totalChildren}, MeshFilter={meshFilterCount}, Renderer={rendererCount}, 総頂点数={totalVerts}");
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
                    float groundHeight = HeightmapTextureManager.GetTerrainHeight(new Vector3(wpPos.x, 1000f, wpPos.z));
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
        GameObject container = new GameObject($"Road_{pathData.path_id}");
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 角あり直線道路生成開始: path_id={pathData.path_id}, segments={points.Count - 1}");

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

        // セグメントごとにスプラインを生成
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

            GameObject segObj = new GameObject($"Segment_{i}");
            segObj.transform.SetParent(container.transform, false);
            SplineComputer spline = BuildSpline(segObj, splinePoints, false);

            CreateRoadParts(segObj, spline, width, sidewalkWidth, sideWidth, lanes, treeSpacing, wallWidth, centerWidth, pillarSpacing, heightEditable, baseHeight, singleSpan, pathData, splinePoints, segStartSetbacks[i], segEndSetbacks[i]);
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
        GameObject prefab = ModelManager.Instance.GetModel(resourcePath);
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
            ? ModelManager.Instance.GetModel($"Models/Paths/p{connector.EndPrefabVariant}/{part}")
                ?? ModelManager.Instance.GetModel($"Models/Paths/p{connector.EndFallbackType}/{part}") : prefab;
        Mesh endSource = endPrefab != null ? endPrefab.GetComponent<MeshFilter>()?.sharedMesh : null;
        float endHeight = endSource != null ? endSource.bounds.size.y : bounds.size.y;
        int tiles = Mathf.Max(1, Mathf.CeilToInt(length / 0.5f));
        Vector3[] original = source.vertices;
        Vector2[] originalUv = source.uv;
        var vertices = new List<Vector3>();
        var uv = new List<Vector2>();
        var indices = new List<int>[source.subMeshCount];
        var sourceTriangles = new int[source.subMeshCount][];
        for (int sub = 0; sub < sourceTriangles.Length; sub++) sourceTriangles[sub] = source.GetTriangles(sub);
        for (int sub = 0; sub < indices.Length; sub++) indices[sub] = new List<int>();
        for (int tile = 0; tile < tiles; tile++)
        {
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
    /// 各セグメントの終点には必ず1本配置し、セグメント長が pillar_spacing を超える場合は
    /// その間隔で追加配置する。セグメントの始点には配置しない。
    ///
    /// base_height は3D描画用の高さオフセット(m)。CalculatePathPoints でスプライン点のYから
    /// base_height が引かれているため、スプラインは (relY - base_height) の高さで構築されている。
    /// GeneratePillars はアンカー点のYからも base_height を引いてスプラインと合わせる。
    /// RoadPath（高架道路）と TrackPath（高架線路）の共通処理。
    /// </summary>
    public static void GeneratePillars(GameObject container, SplineComputer spline, float pillarSpacing, float baseHeight, PathSummary pathData, string variantBase, string typeBase)
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
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] pillar '{pillarPath}' なし。フォールバック試行: '{fallbackPillarPath}'");
                pillarPrefab = ModelManager.Instance.GetModel(fallbackPillarPath);
                if (pillarPrefab != null)
                {
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] pillar フォールバック成功: '{fallbackPillarPath}'");
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
                UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] pillar_cap '{pillarCapPath}' なし。フォールバック試行: '{fallbackPillarCapPath}'");
                pillarCapPrefab = ModelManager.Instance.GetModel(fallbackPillarCapPath);
                if (pillarCapPrefab != null)
                {
                    UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] pillar_cap フォールバック成功: '{fallbackPillarCapPath}'");
                }
            }
        }

        if (pillarPrefab == null && pillarCapPrefab == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] pillar/pillar_cap プレハブ両方なし: '{pillarPath}', '{pillarCapPath}'");
            return;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] pillar プレハブロード状況: pillar={(pillarPrefab != null ? "あり" : "なし")}, cap={(pillarCapPrefab != null ? "あり" : "なし")}");

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

        int placedCount = 0;

        // パス全体の開始点（最初のアンカー点）に柱を配置する。
        // 各セグメントのループでは終端側のみに柱を配置するため、開始点にも個別に配置することで、
        // 重複を防ぎつつパス全体の開始端と終了端の両方に柱を過不足なく配置するための仕様。
        if (anchorPercents.Count > 0)
        {
            if (PlacePillar(container, pillarPrefab, pillarCapPrefab, spline, baseHeight, anchorPercents[0]))
            {
                placedCount++;
            }
        }

        // 各セグメント（アンカー点間）ごとに処理
        for (int i = 0; i < anchors.Count - 1; i++)
        {
            double startPct = anchorPercents[i];
            double endPct = anchorPercents[i + 1];

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
                    double t = (double)j / divisions;
                    double placementPct = startPct + (endPct - startPct) * t;
                    if (PlacePillar(container, pillarPrefab, pillarCapPrefab, spline, baseHeight, placementPct))
                    {
                        placedCount++;
                    }
                }
            }
            else
            {
                // 短いセグメントは終点に1本のみ配置する
                if (PlacePillar(container, pillarPrefab, pillarCapPrefab, spline, baseHeight, endPct))
                {
                    placedCount++;
                }
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] 柱配置完了: {placedCount}本");
    }

    /// <summary>
    /// スプライン評価用パーセントを [0,1] に丸める。
    /// Dreamteck の SplineComputer.Evaluate はパーセントをクランプせず、
    /// 1.0 を浮動小数点誤差で 1ULP でも超えると内部サンプル配列の範囲外アクセス
    /// (IndexOutOfRangeException) になる (startPct + (endPct - startPct) * 1.0 の丸めで発生しうる)。
    /// NaN も FloorInt 経由で不正なインデックスになるため 0 に落とす。
    /// スプラインを Evaluate/Project する際は必ずこれを通すこと。
    /// </summary>
    private static double ClampSplinePercent(double percent)
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
    /// </summary>
    private static bool PlacePillar(GameObject container, GameObject pillarPrefab, GameObject pillarCapPrefab, SplineComputer spline, float baseHeight, double percent)
    {
        SplineSample sample = spline.Evaluate(ClampSplinePercent(percent));
        float capThickness = 0f;
        bool placed = false;

        // 柱は地形の傾斜に追従せず、常にワールド上方向に対して垂直に立つ。
        // 回転はYawのみ（Yaw=パス進行方向から導出し、ピッチ・ロールは0に固定）し、
        // スケールも常にローカルY軸をワールド上方向へ伸ばす簡易計算にする。
        Vector3 forward = sample.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();
        Quaternion yawRotation = Quaternion.LookRotation(forward, Vector3.up);

        // 1. pillar_cap の配置 (Y方向にはスケールしない)
        if (pillarCapPrefab != null)
        {
            GameObject pillarCap = UnityEngine.Object.Instantiate(pillarCapPrefab, container.transform);
            pillarCap.transform.position = sample.position;
            pillarCap.transform.rotation = yawRotation;
            pillarCap.transform.localScale = pillarCapPrefab.transform.localScale;
            placed = true;

            // pillar_cap の厚みを計算 (ピボットが上端にある規約に基づき、負のmin.yを厚みとする)
            MeshFilter capMf = pillarCapPrefab.GetComponentInChildren<MeshFilter>();
            if (capMf != null && capMf.sharedMesh != null)
            {
                float localMinY = capMf.sharedMesh.bounds.min.y;
                capThickness = Mathf.Max(0f, -localMinY) * pillarCap.transform.localScale.y;
            }
        }

        // 2. pillar の配置
        if (pillarPrefab != null)
        {
            // 柱は路面（サンプル点）から下方向に、棒の下端が localGroundY（地面）に達するまで伸びる。
            // CalculatePathPoints で base_height 分のYシフトが適用済みのため、sample.position.y は
            // すでに (groundY + (relY - base_height) + offset) になっており、柱の下端は localGroundY でよい。
            Vector3 samplePos = sample.position;
            // y=1000 は「十分に高い位置」を示すダミー値。HeightmapTextureManager は XZ をキーに
            // CPU 高さ配列を O(1) ルックアップするため y は結果に影響しないが、RoadIntersectionMeshGenerator
            // と同じ呼び出し方をしておく。
            float localGroundY = HeightmapTextureManager.GetTerrainHeight(new Vector3(samplePos.x, 1000f, samplePos.z));
            float roadRelY = samplePos.y - localGroundY;

            // cap の厚み分だけ柱の上端を下にずらす（cap の下面 = 柱の上端）。
            Vector3 pillarPos = samplePos;
            if (capThickness > 0f)
            {
                // Yawのみの回転で柱は垂直に立つため、下方向は常にワールド下方向（Vector3.down）。
                pillarPos = samplePos + Vector3.down * capThickness;
            }

            // 柱の長さ = (上端Y − 下端Y) = (pillarPos.y) − localGroundY。
            // CalculatePathPoints で base_height 分だけYが下げられているため、
            // すでに pillarPos.y は (groundY + effectiveRelY + offset − capTh) になっており、
            // 柱の下端は localGroundY でよい（base_height は加算しない）。
            float pillarBottomY = localGroundY;
            float height = pillarPos.y - pillarBottomY;
            if (height > 0.1f)
            {
                GameObject pillar = UnityEngine.Object.Instantiate(pillarPrefab, container.transform);
                pillar.transform.position = pillarPos;
                pillar.transform.rotation = yawRotation;
                placed = true;

                // ローカルY軸がワールド上方向に一致するYaw回転のため、
                // Yスケールに height をそのまま適用すると柱の下端が pillarBottomY に接する。
                Vector3 scale = pillar.transform.localScale;
                scale.y = height;
                pillar.transform.localScale = scale;
            }
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
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[RoadPath] マスターデータ取得成功: width={width}, sidewalkWidth={sidewalkWidth}, sideWidth={sideWidth}, treeSpacing={treeSpacing}, lanes={lanes}, wallWidth={wallWidth}, centerWidth={centerWidth}, pillarSpacing={pillarSpacing}, heightEditable={heightEditable}, baseHeight={baseHeight}, singleSpan={singleSpan}");
        }
        else
        {
            Debug.LogWarning($"[RoadPath] マスターデータなし (variant={pathData.path_variant})。デフォルト幅を使用。");
            lanes = Mathf.Max(1, Mathf.RoundToInt(width / 3.5f));
        }
    }
}


/// <summary>手続き生成した交差点メッシュのみを所有し、共有プレハブのメッシュは破棄しない。</summary>
public sealed class IntersectionMeshOwner : MonoBehaviour
{
    public Mesh OwnedMesh;
    private void OnDestroy()
    {
        if (OwnedMesh != null) Destroy(OwnedMesh);
    }
}
