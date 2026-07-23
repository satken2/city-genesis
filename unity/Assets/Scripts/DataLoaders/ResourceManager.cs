using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;

[Serializable]
public class ResourceItem
{
    public int resource_id;
    public int resource_type;
    public int product_id;
    public string resource_type_label;
    public float resource_amount;
    public float recovery_rate;
    public List<List<float>> polygon;
    public List<float> center_point;
}

[Serializable]
public class ResourcesListResponse
{
    public List<ResourceItem> resources;
}

/// <summary>
/// 天然資源（油田・鉱脈・レアメタル・森林・水源）の分布を、地形に追従する
/// ヒートマップ風の半透明オーバーレイとして描画するマネージャー。
///
/// 描画方式:
/// - 各資源ポリゴンのAABB内を一定間隔のグリッドで細分化し、各頂点の高さを地形に合わせる。
///   これにより「単なる板が地形に埋もれる」問題を解決し、シーツ状に地形へ張り付く。
/// - 頂点アルファでポリゴン境界をソフトフェードさせ、Custom/ResourceHeatmap シェーダーで
///   プロシージャルノイズによる「雲/ヒートマップ」的なもやっとした見た目を実現する。
///
/// 再構築タイミング:
/// - リソースデータのロード完了時
/// - TerrainManager.OnTerrainTileReady（地形タイル更新）で該当セル上に資源がある場合
///   （1フレームに複数回のイベントは LateUpdate で1回にまとめる）
/// </summary>
public class ResourceManager : BaseApiDataManager
{
    public static ResourceManager Instance { get; private set; }

    private bool areResourcesLoaded = false;
    private bool isLoadInProgress = false;
    private bool isVisible = false;

    private List<ResourceItem> resourceCache = new List<ResourceItem>();

    // ヒートマップ描画用の専用メッシュオブジェクト。
    // OverlayMeshRenderer(他マネージャーと共用)は使わず、UV付きの専用メッシュを持つ。
    private GameObject heatmapMeshObj;
    private MeshFilter heatmapMeshFilter;
    private MeshRenderer heatmapMeshRenderer;
    private Mesh heatmapMesh;
    private Material heatmapMaterial;

    // 資源が存在する(可能性がある)セル集合。
    // OnTerrainTileReady の発火セルがこの集合に含まれる場合のみ再構築し、
    // 無関係なセルでの再構築オーバーヘッドを避ける。
    private HashSet<Vector2Int> resourceTouchedCells = new HashSet<Vector2Int>();

    // 1フレームに複数の OnTerrainTileReady が来た場合に1回の再構築にまとめるための遅延フラグ。
    private bool isMeshDirty = false;

    // ---- 資源名ラベル描画（ZoneManager と同じ ScreenSpaceOverlay Canvas 方式） ----

    // 資源名ラベル用のCanvas。ZoneManager の UI Canvas とは別に持ち、
    // 資源表示のON/OFFを独立して制御できるようにする。
    private GameObject uiCanvasRoot;
    private Canvas uiCanvas;
    private RectTransform uiCanvasRect;

    // 資源IDごとのラベル GameObject。
    private Dictionary<int, GameObject> resourceLabelUIs = new Dictionary<int, GameObject>();

    // 資源IDごとの center_point（XZ平面）。ラベル位置更新時に resourceCache の線形探索を
    // 避けるため、ロード時にキャッシュする。
    private Dictionary<int, Vector2> resourceCentersById = new Dictionary<int, Vector2>();

    // カメラの位置・回転変化が小さい場合は WorldToScreenPoint の再計算をスキップし、
    // ラベル位置更新の CPU 負荷を下げる。ZoneManager と同じ閾値。
    private Vector3 lastCamPos = Vector3.negativeInfinity;
    private Quaternion lastCamRot = Quaternion.identity;
    private const float CAMERA_POS_THRESHOLD = 1.0f;
    private const float CAMERA_ROT_THRESHOLD = 2.0f;

    // 色定義（資源タイプ別）。
    // RGBのみをベース色として使う。アルファは頂点ごとにエッジフェードで計算する。
    // 1: Oil(石油), 2: Ore(鉱石), 3: Rare Metal(貴金属), 4: Forest(森林), 5: Water(水源), 6: Coal(石炭), 7: Natural Gas(天然ガス)
    private Dictionary<int, Color> resourceColors = new Dictionary<int, Color>
    {
        { 1, new Color(0.1f, 0.1f, 0.1f, 1.0f) },       // Oil (黒)
        { 2, new Color(0.6f, 0.4f, 0.2f, 1.0f) },       // Ore (茶色)
        { 3, new Color(1.0f, 0.85f, 0.0f, 1.0f) },      // Rare Metal / Precious Metal (黄色)
        { 4, new Color(0.18f, 0.49f, 0.2f, 1.0f) },     // Forest (緑)
        { 5, new Color(0.0f, 0.6f, 0.9f, 1.0f) },       // Water (水色)
        { 6, new Color(0.5f, 0.5f, 0.5f, 1.0f) },       // Coal (灰色)
        { 7, new Color(0.95f, 0.95f, 0.95f, 1.0f) }     // Natural Gas (白)
    };

    // 細分化グリッドの間隔（メートル）。
    // 大きすぎると地形追従が粗くなり、小さすぎると頂点数が増える。
    private const float GRID_STEP = 10f;

    // ポリゴン境界外側までのフェード距離（メートル）。
    // この距離まではポリゴン外の頂点も生成し、アルファを0へ漸減させることで軟らかい輪郭にする。
    private const float EDGE_FADE = 12f;

    // 地形表面からの浮かし量（メートル）。
    // Zファイティング防止と、雲が浮遊して見える効果の両立。
    private const float HEIGHT_OFFSET = 0.5f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnEnable()
    {
        // 地形タイルが更新されたら資源メッシュの高さ追従が必要なため購読する。
        // PathManager / BuildingManager と同じ購読パターン。
        TerrainManager.OnTerrainTileReady += OnTerrainTileReadyHandler;
    }

    private void OnDisable()
    {
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReadyHandler;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (heatmapMeshObj != null) Destroy(heatmapMeshObj);
        if (heatmapMesh != null) Destroy(heatmapMesh);
        if (heatmapMaterial != null) Destroy(heatmapMaterial);
        if (uiCanvasRoot != null) Destroy(uiCanvasRoot);
    }

    /// <summary>
    /// 資源表示のON/OFFを切り替える。
    /// OFF時はメッシュレンダラーとラベル用Canvasを無効化し、ON時は即座に再構築する。
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (isVisible == visible) return;
        isVisible = visible;

        if (heatmapMeshRenderer != null)
        {
            heatmapMeshRenderer.enabled = visible;
        }

        // CanvasごとSetActiveすることで、全ラベルの表示/非表示を一括制御する
        if (uiCanvasRoot != null)
        {
            uiCanvasRoot.SetActive(visible);
        }

        if (visible && areResourcesLoaded)
        {
            BuildHeatmapMesh();
            // 表示ON時にカメラ位置トラッキングをリセットし、次フレームで確実にラベル位置を更新する
            lastCamPos = Vector3.negativeInfinity;
        }
    }

    /// <summary>
    /// ヒートマップ描画用の GameObject / Mesh / Material を必要に応じて生成する。
    /// OverlayMeshRenderer は使わず、UVと頂点カラーを持つ通常のMeshRendererを利用する。
    /// </summary>
    private void EnsureHeatmapMeshObjects()
    {
        if (heatmapMeshObj != null) return;

        heatmapMeshObj = new GameObject("ResourceHeatmapMesh");
        DontDestroyOnLoad(heatmapMeshObj);
        heatmapMeshFilter = heatmapMeshObj.AddComponent<MeshFilter>();
        heatmapMeshRenderer = heatmapMeshObj.AddComponent<MeshRenderer>();

        Shader shader = Shader.Find("Custom/ResourceHeatmap");
        if (shader == null)
        {
            Debug.LogError("ResourceManager: Custom/ResourceHeatmap シェーダーが見つかりません。");
            return;
        }
        heatmapMaterial = new Material(shader);
        heatmapMeshRenderer.material = heatmapMaterial;
        heatmapMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        heatmapMeshRenderer.receiveShadows = false;

        heatmapMesh = new Mesh();
        heatmapMesh.MarkDynamic();
        heatmapMeshFilter.mesh = heatmapMesh;

        heatmapMeshRenderer.enabled = isVisible;
    }

    public override void LoadCells(List<Vector2Int> neededCells, bool isForce)
    {
        if (!isLoadInProgress && (isForce || !areResourcesLoaded))
        {
            StartCoroutine(LoadAllResources());
        }
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        // グローバルロード専用でセル単位ではない
    }

    public override void ClearAllCells()
    {
        resourceCache.Clear();
        areResourcesLoaded = false;
        resourceTouchedCells.Clear();
        if (heatmapMesh != null)
        {
            heatmapMesh.Clear();
        }
        ClearAllResourceLabels();
    }

    private IEnumerator LoadAllResources()
    {
        if (isLoadInProgress) yield break;
        isLoadInProgress = true;

        string url = "/resources"; // BASE_URL は ApiClient 側で付与される
        yield return ApiClient.Get<ResourcesListResponse>(
            url,
            response =>
            {
                if (response != null && response.resources != null)
                {
                    resourceCache = response.resources;
                    areResourcesLoaded = true;
                    RecomputeResourceTouchedCells();
                    RebuildResourceLabels();
                    if (isVisible)
                    {
                        BuildHeatmapMesh();
                    }
                }
                isLoadInProgress = false;
            },
            error =>
            {
                Debug.LogError($"ResourceManager: Failed to load resources: {error}");
                areResourcesLoaded = false;
                isLoadInProgress = false;
            }
        );
    }

    /// <summary>
    /// 現在の resourceCache から、資源が存在し得るセル集合を再計算する。
    /// OnTerrainTileReady の受信側で「無関係セルの再構築」をスキップするために使用する。
    /// 各資源ポリゴンのAABB（フェード分含む）が覆う全セルを登録する。
    /// </summary>
    private void RecomputeResourceTouchedCells()
    {
        resourceTouchedCells.Clear();
        float cellSize = TerrainManager.CellSize;
        foreach (var resource in resourceCache)
        {
            if (resource.polygon == null || resource.polygon.Count < 3) continue;

            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            foreach (var pt in resource.polygon)
            {
                if (pt[0] < minX) minX = pt[0];
                if (pt[0] > maxX) maxX = pt[0];
                if (pt[1] < minZ) minZ = pt[1];
                if (pt[1] > maxZ) maxZ = pt[1];
            }
            // フェード分だけ拡張して、境界セルも含める
            minX -= EDGE_FADE; maxX += EDGE_FADE;
            minZ -= EDGE_FADE; maxZ += EDGE_FADE;

            int cx0 = Mathf.FloorToInt(minX / cellSize);
            int cx1 = Mathf.FloorToInt(maxX / cellSize);
            int cz0 = Mathf.FloorToInt(minZ / cellSize);
            int cz1 = Mathf.FloorToInt(maxZ / cellSize);
            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    resourceTouchedCells.Add(new Vector2Int(cx, cz));
                }
            }
        }
    }

    private void OnTerrainTileReadyHandler(Vector2Int cellIndex, Terrain terrain)
    {
        if (!isVisible || !areResourcesLoaded) return;
        // 資源が存在するセルでのみ再構築フラグを立てる
        if (resourceTouchedCells.Contains(cellIndex))
        {
            isMeshDirty = true;
        }
    }

    private void LateUpdate()
    {
        // 1フレームに複数の OnTerrainTileReady イベントが来た場合でも、
        // 再構築は1回にまとめる（同じフレームの最終地形状態で描画すれば十分）。
        if (isMeshDirty)
        {
            isMeshDirty = false;
            BuildHeatmapMesh();
        }
    }

    // ---- 資源名ラベル（ZoneManager と同じ ScreenSpaceOverlay Canvas 方式） ----

    private void Update()
    {
        // ヒートマップメッシュは world-space なのでカメラ移動で再構築不要だが、
        // ラベルはスクリーン座標へ投影するためカメラが動いたら位置更新が必要。
        if (!isVisible || !areResourcesLoaded) return;
        if (uiCanvasRoot == null || !uiCanvasRoot.activeSelf) return;
        if (resourceLabelUIs.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // カメラの位置・回転が閾値未満の変化しかない場合はスキップし、
        // WorldToScreenPoint の呼び出し回数を減らす。ZoneManager と同じ間引き基準。
        Vector3 currentCamPos = cam.transform.position;
        Quaternion currentCamRot = cam.transform.rotation;
        bool posChanged = Vector3.Distance(currentCamPos, lastCamPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentCamRot, lastCamRot) > CAMERA_ROT_THRESHOLD;
        if (!posChanged && !rotChanged) return;

        lastCamPos = currentCamPos;
        lastCamRot = currentCamRot;

        UpdateResourceLabelPositions(cam);
    }

    /// <summary>
    /// 全資源ラベルのスクリーン座標を再計算し、RectTransform.anchoredPosition を更新する。
    /// 資源の center_point をワールド座標とし、地形高さ+2m の位置をスクリーンへ投影する。
    /// カメラ背面にあるラベルは非表示にする。
    /// </summary>
    private void UpdateResourceLabelPositions(Camera cam)
    {
        foreach (var kvp in resourceLabelUIs)
        {
            int resourceId = kvp.Key;
            GameObject labelObj = kvp.Value;
            if (labelObj == null) continue;
            if (!resourceCentersById.TryGetValue(resourceId, out Vector2 center)) continue;

            float groundY = Utils.GetGroundHeight(new Vector3(center.x, 1000f, center.y));
            Vector3 worldPos = new Vector3(center.x, groundY + 2f, center.y);
            Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
            bool isBehind = screenPos.z < 0;

            labelObj.SetActive(!isBehind);
            if (!isBehind)
            {
                RectTransform rt = labelObj.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(screenPos.x, screenPos.y);
            }
        }
    }

    /// <summary>
    /// 資源名ラベル用の ScreenSpaceOverlay Canvas を必要に応じて生成する。
    /// ZoneManager の Canvas(sortingOrder=60)とは別にして、資源表示のON/OFFを
    /// 独立して制御できるようにする。資源は「表示オプション」扱いで地区より優先度が
    /// 低いため、sortingOrder は 55 とする(地区ラベルの方が手前に出る)。
    /// </summary>
    private void EnsureUICanvas()
    {
        if (uiCanvasRoot != null) return;

        uiCanvasRoot = new GameObject("ResourceUICanvas");
        DontDestroyOnLoad(uiCanvasRoot);
        uiCanvas = uiCanvasRoot.AddComponent<Canvas>();
        uiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        uiCanvas.sortingOrder = 55;
        uiCanvasRect = uiCanvasRoot.GetComponent<RectTransform>();
        uiCanvasRoot.AddComponent<CanvasScaler>();
        uiCanvasRoot.AddComponent<GraphicRaycaster>();

        // 資源表示の現在の状態に合わせる
        uiCanvasRoot.SetActive(isVisible);
    }

    /// <summary>
    /// 1つの資源に対する名前ラベルを2D UI上に生成する。
    /// 実際の位置は Update で毎フレーム更新される。
    /// </summary>
    private void CreateResourceLabelUI(ResourceItem resource)
    {
        EnsureUICanvas();
        if (uiCanvasRect == null) return;

        GameObject labelGO = new GameObject($"ResourceLabelUI_{resource.resource_id}");
        labelGO.transform.SetParent(uiCanvasRect, false);

        Text text = labelGO.AddComponent<Text>();
        text.text = resource.resource_type_label ?? "";
        text.fontSize = 35;

        // OSにインストールされたフォントから多言語対応のものを優先して使用する。
        // ZoneManager.CreateZoneLabelUI と同じ優先順位・同じフォントリスト。
        // Yu Gothic UI / Meiryo は日本語・英語・数字に加え、キリル文字・アラビア文字・
        // タイ文字・CJK統合漢字など広範囲のグリフを含むWindows標準フォントである。
        string[] preferredFonts = new string[] {
            "Yu Gothic UI",
            "Meiryo",
            "Microsoft YaHei UI",
            "Malgun Gothic",
            "Leelawadee UI",
            "Segoe UI",
            "Arial"
        };
        Font osFont = Font.CreateDynamicFontFromOSFont(preferredFonts, text.fontSize);
        text.font = osFont != null ? osFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;

        // 読みやすさのためテキストに黒いアウトラインを付ける。
        // ヒートマップの色（油田=黒、水源=シアン等）に関わらず視認性を確保するため。
        Outline outline = labelGO.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(2.5f, 2.5f);

        RectTransform rt = text.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(500, 75);

        // クリック可能にして、クリック時に天然資源詳細ウィンドウを開く
        Button button = labelGO.AddComponent<Button>();
        int capturedResourceId = resource.resource_id;
        button.onClick.AddListener(() => OnResourceLabelClicked(capturedResourceId));

        resourceLabelUIs[resource.resource_id] = labelGO;
    }

    /// <summary>
    /// 天然資源ラベルがクリックされた時のハンドラ。
    /// Tauri UI に対して resource_detail ウィンドウを開くよう IPC イベントを送信する。
    /// </summary>
    private void OnResourceLabelClicked(int resourceId)
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("resource_detail", "resource", resourceId.ToString());
        }
    }

    /// <summary>
    /// 現在の resourceCache に合わせて資源名ラベルを再構築する。
    /// 既存ラベルを破棄してから新規作成する。LoadAllResources 完了時に呼ばれる。
    /// </summary>
    private void RebuildResourceLabels()
    {
        ClearAllResourceLabels();
        EnsureUICanvas();

        resourceCentersById.Clear();
        foreach (var resource in resourceCache)
        {
            if (resource.center_point == null || resource.center_point.Count < 2) continue;
            resourceCentersById[resource.resource_id] = new Vector2(resource.center_point[0], resource.center_point[1]);
            CreateResourceLabelUI(resource);
        }

        // 次フレームでラベル位置が確実に更新されるようにトラッキングをリセット
        lastCamPos = Vector3.negativeInfinity;
    }

    /// <summary>
    /// 全資源ラベルを破棄する。ClearAllCells / RebuildResourceLabels から呼ばれる。
    /// </summary>
    private void ClearAllResourceLabels()
    {
        foreach (var kvp in resourceLabelUIs)
        {
            if (kvp.Value != null) Destroy(kvp.Value);
        }
        resourceLabelUIs.Clear();
        resourceCentersById.Clear();
    }

    /// <summary>
    /// 現在の resourceCache と地形データから、ヒートマップメッシュを再構築する。
    /// 各資源ごとにグリッド細分化された頂点列を結合し、単一のMeshに書き込む。
    /// </summary>
    private void BuildHeatmapMesh()
    {
        if (!areResourcesLoaded) return;

        EnsureHeatmapMeshObjects();
        if (heatmapMesh == null) return;

        List<Vector3> verts = new List<Vector3>();
        List<Color> colors = new List<Color>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> indices = new List<int>();

        foreach (var resource in resourceCache)
        {
            if (resource.polygon == null || resource.polygon.Count < 3) continue;
            AddTessellatedResource(resource, verts, colors, uvs, indices);
        }

        heatmapMesh.Clear();
        if (verts.Count > 0)
        {
            heatmapMesh.SetVertices(verts);
            heatmapMesh.SetColors(colors);
            heatmapMesh.SetUVs(0, uvs);
            heatmapMesh.SetTriangles(indices, 0);
            heatmapMesh.RecalculateBounds();
        }
    }

    /// <summary>
    /// 1つの資源ポリゴンをグリッド細分化し、与えられたリストに頂点・色・UV・三角形を追加する。
    /// ポリゴン境界外側に EDGE_FADE の距離まで頂点を張り出し、アルファを0へ漸減させることで
    /// 軟らかい輪郭（輪郭線なし）を実現する。
    /// </summary>
    private void AddTessellatedResource(
        ResourceItem resource,
        List<Vector3> verts, List<Color> colors, List<Vector2> uvs, List<int> indices)
    {
        // AABB を計算し、境界フェード分だけ外側に拡張する
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        foreach (var pt in resource.polygon)
        {
            if (pt[0] < minX) minX = pt[0];
            if (pt[0] > maxX) maxX = pt[0];
            if (pt[1] < minZ) minZ = pt[1];
            if (pt[1] > maxZ) maxZ = pt[1];
        }
        minX -= EDGE_FADE; maxX += EDGE_FADE;
        minZ -= EDGE_FADE; maxZ += EDGE_FADE;

        int nx = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / GRID_STEP) + 1);
        int nz = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / GRID_STEP) + 1);

        Color baseColor = resourceColors.TryGetValue(resource.product_id, out var c)
            ? c
            : new Color(0.5f, 0.5f, 0.5f, 1.0f);

        // このリソースのグリッド頂点を生成。baseVertCount はリスト追加前のオフセット。
        int baseVertCount = verts.Count;
        // 頂点インデックスグリッド（三角形生成時に参照）
        int[,] idxGrid = new int[nx, nz];
        // 各頂点のエッジフェードアルファ（全頂点が0のセルは三角形生成をスキップするために使用）
        float[,] alphaGrid = new float[nx, nz];

        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < nz; j++)
            {
                float x = minX + i * GRID_STEP;
                float z = minZ + j * GRID_STEP;

                float signedDist = SignedDistanceToPolygon(x, z, resource.polygon);
                // signedDist が [EDGE_FADE, +∞) で alpha=1
                //                (-EDGE_FADE, EDGE_FADE) で alpha=0..1 の線形ランプ
                //                (-∞, -EDGE_FADE] で alpha=0（完全にポリゴン外）
                float alpha;
                if (signedDist >= EDGE_FADE)
                {
                    alpha = 1f;
                }
                else if (signedDist <= -EDGE_FADE)
                {
                    alpha = 0f;
                }
                else
                {
                    alpha = (signedDist + EDGE_FADE) / (2f * EDGE_FADE);
                }

                alphaGrid[i, j] = alpha;
                idxGrid[i, j] = baseVertCount + (i * nz) + j;

                // 地形高さをサンプリングし、Zファイティング防止のために少し浮かす
                float y = HeightmapTextureManager.GetTerrainHeight(new Vector3(x, 1000f, z)) + HEIGHT_OFFSET;

                Color vc = baseColor;
                vc.a = alpha;
                verts.Add(new Vector3(x, y, z));
                colors.Add(vc);
                // UVはワールドXZ座標をそのまま入れる。シェーダー側で _NoiseScale を乗算する。
                uvs.Add(new Vector2(x, z));
            }
        }

        // セルごとに2つの三角形を生成。4頂点すべてが完全透明ならスキップして
        // ポリゴン遠方の無駄なフラグメントシェーダー負荷を減らす。
        for (int i = 0; i < nx - 1; i++)
        {
            for (int j = 0; j < nz - 1; j++)
            {
                if (alphaGrid[i, j] <= 0f &&
                    alphaGrid[i + 1, j] <= 0f &&
                    alphaGrid[i, j + 1] <= 0f &&
                    alphaGrid[i + 1, j + 1] <= 0f)
                {
                    continue;
                }

                int v00 = idxGrid[i, j];
                int v10 = idxGrid[i + 1, j];
                int v01 = idxGrid[i, j + 1];
                int v11 = idxGrid[i + 1, j + 1];

                // 上向き（+Y）のワインディング。
                // 上空から見たときに表になるよう、v00→v01→v11→v10 の順で結ぶ。
                indices.Add(v00); indices.Add(v01); indices.Add(v11);
                indices.Add(v00); indices.Add(v11); indices.Add(v10);
            }
        }
    }

    /// <summary>
    /// 2Dレイキャストアルゴリズム（ray casting）による多角形の内外判定。
    /// ZoneManager.IsPointInPolygon と同じアルゴリズムだが、float[] 配列ではなく
    /// バックエンド応答の List&lt;List&lt;float&gt;&gt; 形式で受けるため独自実装している。
    /// </summary>
    private static bool IsPointInPolygon(float x, float z, List<List<float>> polygon)
    {
        bool inside = false;
        int count = polygon.Count;
        int j = count - 1;
        for (int i = 0; i < count; i++)
        {
            float ix = polygon[i][0], iz = polygon[i][1];
            float jx = polygon[j][0], jz = polygon[j][1];
            if (((iz > z) != (jz > z)) &&
                (x < (jx - ix) * (z - iz) / (jz - iz) + ix))
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }

    /// <summary>
    /// 多角形への符号付き距離（内側が正、外側が負）を計算する。
    /// 内外判定は IsPointInPolygon、距離は全エッジへの最短距離の最小値。
    /// 非凸多角形にも正しく動作する（符号は内外判定、絶対値は最短エッジ距離）。
    /// </summary>
    private static float SignedDistanceToPolygon(float x, float z, List<List<float>> polygon)
    {
        bool inside = IsPointInPolygon(x, z, polygon);
        float minDist = float.PositiveInfinity;
        int count = polygon.Count;
        for (int i = 0; i < count; i++)
        {
            int nextI = (i + 1) % count;
            float ax = polygon[i][0], az = polygon[i][1];
            float bx = polygon[nextI][0], bz = polygon[nextI][1];
            float d = PointToSegmentDistance(x, z, ax, az, bx, bz);
            if (d < minDist) minDist = d;
        }
        return inside ? minDist : -minDist;
    }

    /// <summary>
    /// 点と線分の最短距離。線分両端が同じ点の場合は点-点距離を返す。
    /// </summary>
    private static float PointToSegmentDistance(
        float px, float pz, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax;
        float dz = bz - az;
        float lenSq = dx * dx + dz * dz;
        if (lenSq < 1e-8f)
        {
            float ddx = px - ax, ddz = pz - az;
            return Mathf.Sqrt(ddx * ddx + ddz * ddz);
        }
        float t = ((px - ax) * dx + (pz - az) * dz) / lenSq;
        t = Mathf.Clamp01(t);
        float cx = ax + t * dx;
        float cz = az + t * dz;
        float ex = px - cx;
        float ez = pz - cz;
        return Mathf.Sqrt(ex * ex + ez * ez);
    }
}
