using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// VisualPanel 内にミニマップを表示するコンポーネント。
/// ミニマップ表示ボタンをクリックすることで、VisualPanel の表示/非表示を切り替える。
///
/// ミニマップはカメラの物理的位置（mainCamera.transform.position）を基準とし、
/// カメラの向いている方向が常に真上になるように周辺セルを2D俯瞰表示する。
/// 各セルのLODティアは塗り色で表現される。
/// </summary>
public class MinimapOverlay : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("ミニマップの表示/非表示を切り替えるボタン")]
    [SerializeField] private Button debugButton;

    [Header("Cell Display Settings")]
    [Tooltip("近景セルの塗り色。アルファで透過度を調整する。")]
    [SerializeField] private Color nearCellColor = new Color(0.3f, 0.6f, 1f, 0.25f);

    [Tooltip("遠景セルの塗り色。アルファで透過度を調整する。")]
    [SerializeField] private Color farCellColor = new Color(1f, 0.8f, 0.2f, 0.25f);

    [Tooltip("超遠景セルの塗り色。アルファで透過度を調整する。")]
    [SerializeField] private Color ultraFarCellColor = new Color(1f, 0.3f, 0.3f, 0.25f);

    [Header("Path & Building Overlay")]
    [Tooltip("ミニマップ上のパス（道路等）の描画色。ポリゴンを塗りつぶして描画する。")]
    [SerializeField] private Color pathColor = new Color(0.8f, 0.85f, 0.9f, 0.9f);

    [Tooltip("ミニマップ上の建物輪郭の描画色。形状のみ線で描画する。")]
    [SerializeField] private Color buildingColor = new Color(1f, 0.65f, 0.2f, 0.9f);

    [Header("VisualPanel Minimap Settings")]
    [Tooltip("ミニマップを描画する VisualPanel の RectTransform")]
    [SerializeField] private RectTransform visualPanelRoot;

    [Tooltip("ミニマップテクスチャの解像度（幅・高さのピクセル数）")]
    [SerializeField] private int minimapTextureSize = 512;

    [Tooltip("ミニマップ上での1セルあたりのサイズ（ピクセル）")]
    [SerializeField] private float minimapCellPixelSize = 40f;

    [Tooltip("ミニマップ中心から描画するセルの半径（例: 5なら11x11セル）")]
    [SerializeField] private int minimapDisplayRadius = 5;

    [Tooltip("ミニマップの背景色")]
    [SerializeField] private Color minimapBackgroundColor = new Color(0f, 0f, 0f, 0.6f);

    [Tooltip("ミニマップの中心マーカー（カメラ位置）の色")]
    [SerializeField] private Color minimapCenterMarkerColor = new Color(1f, 0.2f, 0.2f, 1f);

    [Tooltip("ミニマップ上のカメラ視野範囲の塗り色")]
    [SerializeField] private Color minimapFrustumFillColor = new Color(1f, 1f, 1f, 0.15f);

    [Tooltip("ミニマップ上のカメラ視野範囲の枠線色")]
    [SerializeField] private Color minimapFrustumBorderColor = new Color(1f, 1f, 1f, 0.6f);

    private TerrainManager terrainManager;
    private PathManager pathManager;
    private BuildingManager buildingManager;
    private Camera mainCamera;
    private bool isMinimapVisible = false;
    private Vector2Int lastCenterCell = new Vector2Int(int.MinValue, int.MinValue);

    // VisualPanel ミニマップ用
    private RawImage minimapImage;
    private Texture2D minimapTexture;
    private Color32[] minimapPixelBuffer;
    private RectTransform minimapContainer;
    private Vector3 lastMinimapWorldPos = Vector3.negativeInfinity;
    private float lastMinimapYaw = float.NaN;

    /// <summary>
    /// ミニマップ描画用のセル情報を保持する内部クラス。
    /// 中心セルが変わった時だけ再計算し、テクスチャへのピクセル描画に使い回す。
    /// </summary>
    private class MinimapCellInfo
    {
        public Vector2Int cellIndex;
        public LodTier tier;
        public Vector3[] worldCorners;
        public Vector3 worldCenter;
    }

    private readonly List<MinimapCellInfo> minimapCells = new List<MinimapCellInfo>();

    private void Awake()
    {
        terrainManager = FindAnyObjectByType<TerrainManager>();
        pathManager = FindAnyObjectByType<PathManager>();
        buildingManager = BuildingManager.Instance;
        mainCamera = Camera.main;

        if (debugButton != null)
        {
            debugButton.onClick.AddListener(ToggleMinimapVisibility);
        }

        if (visualPanelRoot != null)
        {
            visualPanelRoot.gameObject.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (debugButton != null)
        {
            debugButton.onClick.RemoveListener(ToggleMinimapVisibility);
        }
        DestroyMinimap();
    }

    private void Update()
    {
        if (!isMinimapVisible) return;
        if (mainCamera == null) return;
        if (visualPanelRoot == null || !visualPanelRoot.gameObject.activeInHierarchy) return;

        Vector2Int currentCenterCell = GetCameraPositionCell();

        // 中心セルが変わった場合はセル構成を再計算する
        if (currentCenterCell != lastCenterCell)
        {
            lastCenterCell = currentCenterCell;
            RebuildMinimapCells();
        }

        // カメラ移動・回転に合わせてミニマップテクスチャを更新する
        RefreshMinimap();
    }

    /// <summary>
    /// ミニマップの表示/非表示を切り替える。
    /// TerrainManager の IsDebugMode も同期する。
    /// </summary>
    private void ToggleMinimapVisibility()
    {
        isMinimapVisible = !isMinimapVisible;

        if (terrainManager != null)
        {
            terrainManager.IsDebugMode = isMinimapVisible;
        }

        if (visualPanelRoot != null)
        {
            visualPanelRoot.gameObject.SetActive(isMinimapVisible);
        }

        if (isMinimapVisible)
        {
            // 強制的に再構築をトリガーする
            lastCenterCell = new Vector2Int(int.MinValue, int.MinValue);
            EnsureMinimap();
        }
    }

    /// <summary>
    /// カメラの物理的位置の y=0 投影を取得する。
    /// ミニマップの中心基準として使用する。
    /// </summary>
    private Vector3 GetCameraWorldPosition()
    {
        if (mainCamera == null) return Vector3.zero;
        Vector3 pos = mainCamera.transform.position;
        return new Vector3(pos.x, 0f, pos.z);
    }

    /// <summary>
    /// カメラの物理的位置から現在のセルインデックスを計算する。
    /// </summary>
    private Vector2Int GetCameraPositionCell()
    {
        if (mainCamera == null) return Vector2Int.zero;
        Vector3 pos = mainCamera.transform.position;
        float cellSize = TerrainManager.CellSize;
        return new Vector2Int(
            Mathf.FloorToInt(pos.x / cellSize),
            Mathf.FloorToInt(pos.z / cellSize)
        );
    }

    /// <summary>
    /// ミニマップ用のセル情報リストを再構築する。
    /// 中心セルが変わった時のみ呼ばれる。
    /// </summary>
    private void RebuildMinimapCells()
    {
        minimapCells.Clear();
        if (mainCamera == null) return;

        float cellSize = TerrainManager.CellSize;
        int mapWidth = terrainManager != null ? terrainManager.GetMapWidth() : 10000;
        int mapHeight = terrainManager != null ? terrainManager.GetMapHeight() : 10000;

        Vector3 cameraPos = mainCamera.transform.position;
        Vector2Int centerCell = GetCameraPositionCell();
        Vector3 viewCenterPos = GetCameraWorldPosition();
        Vector3 cameraForward = mainCamera.transform.forward;

        // LODティア別のセルセットを計算
        // ミニマップの中心をカメラ位置に合わせるため、centerCell と viewCenterPos もカメラ位置ベースとする
        LodCellSets cellSets = LodConfig.CalculateCellSets(centerCell, viewCenterPos, cameraPos, cameraForward, terrainManager, cellSize, mapWidth, mapHeight);

        for (int dx = -minimapDisplayRadius; dx <= minimapDisplayRadius; dx++)
        {
            for (int dz = -minimapDisplayRadius; dz <= minimapDisplayRadius; dz++)
            {
                Vector2Int cellIndex = new Vector2Int(centerCell.x + dx, centerCell.y + dz);
                LodTier tier = DetermineTier(cellIndex, cellSets);

                // セルの4隅と中心のワールド座標（y=0 の平面上）
                Vector3[] corners = new Vector3[4];
                corners[0] = new Vector3(cellIndex.x * cellSize, 0f, cellIndex.y * cellSize);
                corners[1] = new Vector3((cellIndex.x + 1) * cellSize, 0f, cellIndex.y * cellSize);
                corners[2] = new Vector3((cellIndex.x + 1) * cellSize, 0f, (cellIndex.y + 1) * cellSize);
                corners[3] = new Vector3(cellIndex.x * cellSize, 0f, (cellIndex.y + 1) * cellSize);
                Vector3 centerWorld = new Vector3((cellIndex.x + 0.5f) * cellSize, 0f, (cellIndex.y + 0.5f) * cellSize);

                minimapCells.Add(new MinimapCellInfo
                {
                    cellIndex = cellIndex,
                    tier = tier,
                    worldCorners = corners,
                    worldCenter = centerWorld
                });
            }
        }
    }

    /// <summary>
    /// VisualPanel 内にミニマップ表示用の RawImage を生成する。
    /// VisualPanel のサイズいっぱいに広げて表示する。
    /// 既に存在する場合は何もしない。
    /// </summary>
    private void EnsureMinimap()
    {
        if (visualPanelRoot == null) return;
        if (minimapContainer != null) return;

        minimapContainer = new GameObject("Minimap").AddComponent<RectTransform>();
        minimapContainer.SetParent(visualPanelRoot, false);
        // VisualPanel のサイズいっぱいに広げる
        minimapContainer.anchorMin = Vector2.zero;
        minimapContainer.anchorMax = Vector2.one;
        minimapContainer.pivot = new Vector2(0.5f, 0.5f);
        minimapContainer.anchoredPosition = Vector2.zero;
        minimapContainer.sizeDelta = Vector2.zero;

        minimapImage = minimapContainer.gameObject.AddComponent<RawImage>();
        minimapTexture = new Texture2D(minimapTextureSize, minimapTextureSize, TextureFormat.RGBA32, false);
        minimapTexture.filterMode = FilterMode.Point;
        minimapImage.texture = minimapTexture;

        minimapPixelBuffer = new Color32[minimapTextureSize * minimapTextureSize];
    }

    /// <summary>
    /// VisualPanel ミニマップの GameObject とテクスチャを破棄する。
    /// </summary>
    private void DestroyMinimap()
    {
        if (minimapContainer != null)
        {
            Destroy(minimapContainer.gameObject);
            minimapContainer = null;
            minimapImage = null;
        }
        if (minimapTexture != null)
        {
            Destroy(minimapTexture);
            minimapTexture = null;
        }
        minimapPixelBuffer = null;
    }

    /// <summary>
    /// VisualPanel のミニマップテクスチャを毎フレーム更新する。
    /// カメラの向きと視点中心座標が変わるたびに、周辺セルを2D俯瞰図として再描画する。
    /// </summary>
    private void RefreshMinimap()
    {
        if (minimapTexture == null || visualPanelRoot == null) return;
        if (mainCamera == null) return;

        // ミニマップの中心はカメラの物理的位置を基準とする
        Vector3 camWorldPos = GetCameraWorldPosition();
        float yaw = mainCamera.transform.eulerAngles.y * Mathf.Deg2Rad;

        // 前回と同じ状態であれば再描画を省略する。
        // 閾値を 1.0m / 2.0° とし、微動による毎フレーム再描画を抑制する。
        // ミニマップは俯瞰情報の把握に使われるため、1m 未満のズレや 2° 未満の回転では視認性に影響しない。
        if (Vector3.Distance(camWorldPos, lastMinimapWorldPos) < 1.0f &&
            Mathf.Abs(Mathf.DeltaAngle(yaw * Mathf.Rad2Deg, lastMinimapYaw * Mathf.Rad2Deg)) < 2.0f)
        {
            return;
        }
        lastMinimapWorldPos = camWorldPos;
        lastMinimapYaw = yaw;

        // ピクセルバッファを背景色でクリアする
        Color32 bgColor = (Color32)minimapBackgroundColor;
        for (int i = 0; i < minimapPixelBuffer.Length; i++)
        {
            minimapPixelBuffer[i] = bgColor;
        }

        float pixelsPerUnit = minimapCellPixelSize / TerrainManager.CellSize;
        Vector2 centerOffset = new Vector2(minimapTextureSize * 0.5f, minimapTextureSize * 0.5f);

        // 各セルを描画する（境界線とセル番号は非表示）
        foreach (var cell in minimapCells)
        {
            Vector2[] miniCorners = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                miniCorners[i] = WorldToMinimap(cell.worldCorners[i], camWorldPos, yaw, pixelsPerUnit, centerOffset);
            }

            Color fillColor = GetTierColor(cell.tier);
            // ミニマップ上では塗り色をやや濃くして視認性を上げる
            fillColor.a = Mathf.Min(fillColor.a * 2.5f, 0.7f);

            // セル番号と境界線は描画しないため、枠線色は透明とする
            DrawMinimapConvexPolygon(miniCorners, fillColor, Color.clear);
        }

        // パスと建物をミニマップ上に描画する（形状のみ線で表現）
        DrawPathsOnMinimap(camWorldPos, yaw, pixelsPerUnit, centerOffset);
        DrawBuildingsOnMinimap(camWorldPos, yaw, pixelsPerUnit, centerOffset);

        // カメラの視野範囲（視錐台とy=0平面の交差）を描画する
        Vector3[] frustumCorners = GetFrustumGroundIntersections();
        if (frustumCorners != null && frustumCorners.Length >= 3)
        {
            Vector2[] miniFrustum = new Vector2[frustumCorners.Length];
            for (int i = 0; i < frustumCorners.Length; i++)
            {
                miniFrustum[i] = WorldToMinimap(frustumCorners[i], camWorldPos, yaw, pixelsPerUnit, centerOffset);
            }
            DrawMinimapConvexPolygon(miniFrustum, minimapFrustumFillColor, minimapFrustumBorderColor);
        }

        // 中心マーカー（カメラ位置）を描画する
        Vector2 markerCenter = centerOffset;
        DrawMinimapCenterMarker(markerCenter);

        minimapTexture.SetPixels32(minimapPixelBuffer);
        minimapTexture.Apply();
    }

    /// <summary>
    /// ワールド座標をミニマップテクスチャ上の座標に変換する。
    /// カメラの向いている方向がテクスチャの上方向（+Y）になるように回転を適用する。
    /// </summary>
    private Vector2 WorldToMinimap(Vector3 worldPos, Vector3 origin, float yaw, float pixelsPerUnit, Vector2 centerOffset)
    {
        Vector3 rel = worldPos - origin;
        Vector3 forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
        Vector3 right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
        float localX = Vector3.Dot(rel, right);
        float localZ = Vector3.Dot(rel, forward);
        return new Vector2(
            centerOffset.x + localX * pixelsPerUnit,
            centerOffset.y + localZ * pixelsPerUnit
        );
    }

    /// <summary>
    /// ミニマップテクスチャ上に凸多角形を描画する。
    /// 境界ボックス内のピクセルに対して内包判定を行い、塗りと枠線を描く。
    /// 枠線色のアルファが極めて低い場合は枠線を描画しない。
    /// </summary>
    private void DrawMinimapConvexPolygon(Vector2[] corners, Color fillColor, Color outlineColor)
    {
        if (minimapPixelBuffer == null || corners == null || corners.Length < 3) return;

        float minXf = corners[0].x, maxXf = corners[0].x;
        float minYf = corners[0].y, maxYf = corners[0].y;
        for (int i = 1; i < corners.Length; i++)
        {
            minXf = Mathf.Min(minXf, corners[i].x);
            maxXf = Mathf.Max(maxXf, corners[i].x);
            minYf = Mathf.Min(minYf, corners[i].y);
            maxYf = Mathf.Max(maxYf, corners[i].y);
        }

        int minX = Mathf.Max(0, Mathf.FloorToInt(minXf));
        int maxX = Mathf.Min(minimapTextureSize - 1, Mathf.CeilToInt(maxXf));
        int minY = Mathf.Max(0, Mathf.FloorToInt(minYf));
        int maxY = Mathf.Min(minimapTextureSize - 1, Mathf.CeilToInt(maxYf));

        Color32 fill = (Color32)fillColor;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                if (IsPointInConvexPolygon(p, corners))
                {
                    int idx = y * minimapTextureSize + x;
                    minimapPixelBuffer[idx] = AlphaBlend(minimapPixelBuffer[idx], fill);
                }
            }
        }

        // 枠線（アルファがほぼ0の場合は描画しない）
        if (outlineColor.a > 0.001f)
        {
            for (int i = 0; i < corners.Length; i++)
            {
                DrawMinimapLine(
                    Mathf.RoundToInt(corners[i].x), Mathf.RoundToInt(corners[i].y),
                    Mathf.RoundToInt(corners[(i + 1) % corners.Length].x), Mathf.RoundToInt(corners[(i + 1) % corners.Length].y),
                    outlineColor
                );
            }
        }
    }

    /// <summary>
    /// 点 p が凸多角形（時計回り・反時計回りのいずれでも可）の内部にあるか判定する。
    /// 各辺について、p が全て同じ側にあるかどうかで判定する。
    /// </summary>
    private bool IsPointInConvexPolygon(Vector2 p, Vector2[] corners)
    {
        bool positive = false;
        bool negative = false;
        int n = corners.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = corners[i];
            Vector2 b = corners[(i + 1) % n];
            float cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            if (cross > 0.001f) positive = true;
            else if (cross < -0.001f) negative = true;
            if (positive && negative) return false;
        }
        return true;
    }

    /// <summary>
    /// 背景色と前景色をアルファブレンドする。
    /// 単純な上書きではなく、半透明の重ね描きを行うために使用する。
    /// </summary>
    private Color32 AlphaBlend(Color32 bg, Color32 fg)
    {
        float a = fg.a / 255f;
        float ia = 1f - a;
        return new Color32(
            (byte)Mathf.Clamp(fg.r * a + bg.r * ia, 0, 255),
            (byte)Mathf.Clamp(fg.g * a + bg.g * ia, 0, 255),
            (byte)Mathf.Clamp(fg.b * a + bg.b * ia, 0, 255),
            (byte)Mathf.Clamp(fg.a + bg.a * ia, 0, 255)
        );
    }

    /// <summary>
    /// ミニマップテクスチャ上にブレゼンハムの線分描画アルゴリズムで線を引く。
    /// </summary>
    private void DrawMinimapLine(int x0, int y0, int x1, int y1, Color color)
    {
        int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        Color32 c = (Color32)color;

        while (true)
        {
            if (x0 >= 0 && x0 < minimapTextureSize && y0 >= 0 && y0 < minimapTextureSize)
            {
                minimapPixelBuffer[y0 * minimapTextureSize + x0] = c;
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// カメラの視錐台と y=0 地面平面との交点を取得する。
    /// 視点が地面と交差しない場合（真上を向いている等）は null を返す。
    /// 通常は4点の凸多角形を返すが、3点（三角）になるケースもある。
    /// </summary>
    private Vector3[] GetFrustumGroundIntersections()
    {
        if (mainCamera == null) return null;

        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        Vector3[] viewportCorners = new Vector3[4]
        {
            new Vector3(0, 0, 0), // 左下
            new Vector3(1, 0, 0), // 右下
            new Vector3(1, 1, 0), // 右上
            new Vector3(0, 1, 0)  // 左上
        };

        List<Vector3> intersections = new List<Vector3>(4);
        foreach (var vp in viewportCorners)
        {
            Ray ray = mainCamera.ViewportPointToRay(vp);
            float enter;
            if (groundPlane.Raycast(ray, out enter))
            {
                intersections.Add(ray.GetPoint(enter));
            }
        }

        if (intersections.Count < 3) return null;
        return intersections.ToArray();
    }

    /// <summary>
    /// ミニマップ中心にカメラ位置を示す小さな十字マーカーを描画する。
    /// </summary>
    private void DrawMinimapCenterMarker(Vector2 center)
    {
        int cx = Mathf.RoundToInt(center.x);
        int cy = Mathf.RoundToInt(center.y);
        Color32 c = (Color32)minimapCenterMarkerColor;

        // 小さな十字マーカー
        for (int i = -3; i <= 3; i++)
        {
            int px = cx + i;
            int py = cy;
            if (px >= 0 && px < minimapTextureSize && py >= 0 && py < minimapTextureSize)
                minimapPixelBuffer[py * minimapTextureSize + px] = c;

            px = cx;
            py = cy + i;
            if (px >= 0 && px < minimapTextureSize && py >= 0 && py < minimapTextureSize)
                minimapPixelBuffer[py * minimapTextureSize + px] = c;
        }
    }

    /// <summary>
    /// セルが属するLODティアを判定する。
    /// dynamic → near → far → ultraFar の優先順位で判定する。
    /// </summary>
    private LodTier DetermineTier(Vector2Int cellIndex, LodCellSets cellSets)
    {
        if (cellSets.dynamicCells != null && cellSets.dynamicCells.Contains(cellIndex))
            return LodTier.Dynamic;
        if (cellSets.nearCells != null && cellSets.nearCells.Contains(cellIndex))
            return LodTier.Near;
        if (cellSets.farCells != null && cellSets.farCells.Contains(cellIndex))
            return LodTier.Far;
        return LodTier.UltraFar;
    }

    /// <summary>
    /// LODティアに対応する塗り色を取得する。
    /// Dynamic は透明（色なし）を返す。
    /// </summary>
    private Color GetTierColor(LodTier tier)
    {
        switch (tier)
        {
            case LodTier.Near: return nearCellColor;
            case LodTier.Far: return farCellColor;
            case LodTier.UltraFar: return ultraFarCellColor;
            default: return Color.clear;
        }
    }

    // ================================================================
    // パス・建物のミニマップ描画
    // ================================================================

    /// <summary>
    /// ミニマップ表示範囲内のパス（道路等）をポリゴンとして描画する。
    /// バックエンドから取得した polygon を優先し、存在しない場合は中心線をフォールバックとして描く。
    /// </summary>
    private void DrawPathsOnMinimap(Vector3 camWorldPos, float yaw, float pixelsPerUnit, Vector2 centerOffset)
    {
        if (pathManager == null) return;

        List<PathSummary> paths = pathManager.GetAllPaths();
        if (paths == null) return;

        float range = minimapDisplayRadius * TerrainManager.CellSize;
        float rangeSq = range * range;

        foreach (PathSummary path in paths)
        {
            float[][] poly = path.GetPolygonArray();
            List<Vector3> worldPoints = new List<Vector3>();

            if (poly != null && poly.Length >= 3)
            {
                foreach (float[] pt in poly)
                {
                    if (pt != null && pt.Length >= 2)
                        worldPoints.Add(new Vector3(pt[0], 0f, pt[1]));
                }
            }

            // ポリゴンが取得できない場合は中心線をフォールバックとして使用する
            bool hasPolygon = worldPoints.Count >= 3;
            if (!hasPolygon)
            {
                worldPoints = GetPathWorldPoints(path);
            }

            if (worldPoints.Count < 2) continue;

            // 表示範囲内に少なくとも1点でも含まれるパスのみ描画する
            bool inRange = false;
            foreach (Vector3 pt in worldPoints)
            {
                float dx = pt.x - camWorldPos.x;
                float dz = pt.z - camWorldPos.z;
                if (dx * dx + dz * dz <= rangeSq)
                {
                    inRange = true;
                    break;
                }
            }
            if (!inRange) continue;

            if (hasPolygon)
            {
                Vector2[] miniPoly = new Vector2[worldPoints.Count];
                for (int i = 0; i < worldPoints.Count; i++)
                {
                    miniPoly[i] = WorldToMinimap(worldPoints[i], camWorldPos, yaw, pixelsPerUnit, centerOffset);
                }

                // 耳切り法で三角形化し、各三角形を塗りつぶす
                List<int> tris = TriangulatePolygon(miniPoly);
                Color fill = new Color(pathColor.r, pathColor.g, pathColor.b, pathColor.a * 0.5f);
                for (int i = 0; i < tris.Count; i += 3)
                {
                    DrawMinimapTriangle(miniPoly[tris[i]], miniPoly[tris[i + 1]], miniPoly[tris[i + 2]], fill);
                }

                // 輪郭線を描く
                for (int i = 0; i < miniPoly.Length; i++)
                {
                    Vector2 p0 = miniPoly[i];
                    Vector2 p1 = miniPoly[(i + 1) % miniPoly.Length];
                    DrawMinimapLine(
                        Mathf.RoundToInt(p0.x), Mathf.RoundToInt(p0.y),
                        Mathf.RoundToInt(p1.x), Mathf.RoundToInt(p1.y),
                        pathColor
                    );
                }
            }
            else
            {
                for (int i = 0; i < worldPoints.Count - 1; i++)
                {
                    Vector2 p0 = WorldToMinimap(worldPoints[i], camWorldPos, yaw, pixelsPerUnit, centerOffset);
                    Vector2 p1 = WorldToMinimap(worldPoints[i + 1], camWorldPos, yaw, pixelsPerUnit, centerOffset);
                    DrawMinimapLine(
                        Mathf.RoundToInt(p0.x), Mathf.RoundToInt(p0.y),
                        Mathf.RoundToInt(p1.x), Mathf.RoundToInt(p1.y),
                        pathColor
                    );
                }
            }
        }
    }

    /// <summary>
    /// PathSummary から始点・ウェイポイント・終点を結んだワールド座標リストを生成する。
    /// y=0 の平面に投影してミニマップ描画に使用する。
    /// </summary>
    private List<Vector3> GetPathWorldPoints(PathSummary path)
    {
        List<Vector3> points = new List<Vector3>();

        List<float> start = path.GetStartPointList();
        if (start != null && start.Count >= 2)
            points.Add(new Vector3(start[0], 0f, start[1]));

        List<PathWaypoint> waypoints = path.GetWaypointList();
        if (waypoints != null)
        {
            foreach (PathWaypoint wp in waypoints)
            {
                if (wp.position != null && wp.position.Length >= 2)
                    points.Add(new Vector3(wp.position[0], 0f, wp.position[1]));
            }
        }

        List<float> end = path.GetEndPointList();
        if (end != null && end.Count >= 2)
            points.Add(new Vector3(end[0], 0f, end[1]));

        return points;
    }

    /// <summary>
    /// ミニマップ表示範囲内の建物をポリゴンの輪郭線で描画する。
    /// ポリゴン頂点は center_point に対する相対座標であるため、中心座標を加算してワールド座標に変換する。
    /// 塗りつぶしは行わず、外形の線だけを描く。
    /// </summary>
    private void DrawBuildingsOnMinimap(Vector3 camWorldPos, float yaw, float pixelsPerUnit, Vector2 centerOffset)
    {
        if (buildingManager == null) return;

        List<BuildingSummary> buildings = buildingManager.GetAllBuildings();
        if (buildings == null) return;

        float range = minimapDisplayRadius * TerrainManager.CellSize;
        float rangeSq = range * range;

        foreach (BuildingSummary building in buildings)
        {
            float[][] poly = building.polygonArray;
            if (poly == null || poly.Length < 3) continue;

            // 中心座標で範囲フィルタを行う
            float[] cp = building.centerPointArray;
            if (cp != null && cp.Length >= 2)
            {
                float dx = cp[0] - camWorldPos.x;
                float dz = cp[1] - camWorldPos.z;
                if (dx * dx + dz * dz > rangeSq) continue;
            }

            // ポリゴン頂点は center_point に対する相対座標なので、中心座標を加算する
            float centerX = cp != null && cp.Length >= 2 ? cp[0] : 0f;
            float centerZ = cp != null && cp.Length >= 2 ? cp[1] : 0f;

            for (int i = 0; i < poly.Length; i++)
            {
                float[] pt0 = poly[i];
                float[] pt1 = poly[(i + 1) % poly.Length];
                if (pt0 == null || pt1 == null) continue;
                if (pt0.Length < 2 || pt1.Length < 2) continue;

                Vector3 w0 = new Vector3(pt0[0] + centerX, 0f, pt0[1] + centerZ);
                Vector3 w1 = new Vector3(pt1[0] + centerX, 0f, pt1[1] + centerZ);

                Vector2 p0 = WorldToMinimap(w0, camWorldPos, yaw, pixelsPerUnit, centerOffset);
                Vector2 p1 = WorldToMinimap(w1, camWorldPos, yaw, pixelsPerUnit, centerOffset);
                DrawMinimapLine(
                    Mathf.RoundToInt(p0.x), Mathf.RoundToInt(p0.y),
                    Mathf.RoundToInt(p1.x), Mathf.RoundToInt(p1.y),
                    buildingColor
                );
            }
        }
    }

    /// <summary>
    /// ミニマップテクスチャ上に三角形を塗りつぶす。枠線は描画しない。
    /// </summary>
    private void DrawMinimapTriangle(Vector2 a, Vector2 b, Vector2 c, Color fillColor)
    {
        if (minimapPixelBuffer == null) return;

        float minXf = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
        float maxXf = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
        float minYf = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
        float maxYf = Mathf.Max(a.y, Mathf.Max(b.y, c.y));

        int minX = Mathf.Max(0, Mathf.FloorToInt(minXf));
        int maxX = Mathf.Min(minimapTextureSize - 1, Mathf.CeilToInt(maxXf));
        int minY = Mathf.Max(0, Mathf.FloorToInt(minYf));
        int maxY = Mathf.Min(minimapTextureSize - 1, Mathf.CeilToInt(maxYf));

        Color32 fill = (Color32)fillColor;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                if (IsPointInTriangle(p, a, b, c))
                {
                    int idx = y * minimapTextureSize + x;
                    minimapPixelBuffer[idx] = AlphaBlend(minimapPixelBuffer[idx], fill);
                }
            }
        }
    }

    /// <summary>
    /// 点 p が三角形 abc の内部または辺上にあるか判定する。
    /// </summary>
    private static bool IsPointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float cross1 = Cross(b - a, p - a);
        float cross2 = Cross(c - b, p - b);
        float cross3 = Cross(a - c, p - c);
        bool hasNeg = cross1 < -1e-4f || cross2 < -1e-4f || cross3 < -1e-4f;
        bool hasPos = cross1 > 1e-4f || cross2 > 1e-4f || cross3 > 1e-4f;
        return !(hasNeg && hasPos);
    }

    /// <summary>
    /// 2Dベクトルの外積を計算する。
    /// </summary>
    private static float Cross(Vector2 u, Vector2 v)
    {
        return u.x * v.y - u.y * v.x;
    }

    /// <summary>
    /// 2D単純多角形を耳切り法で三角形の頂点インデックス列に分解する。
    /// BuildingMeshBuilder.TriangulatePolygon と同じアルゴリズムを使用する。
    /// </summary>
    private static List<int> TriangulatePolygon(Vector2[] polygon)
    {
        List<int> triangles = new List<int>();
        if (polygon == null || polygon.Length < 3)
            return triangles;

        List<int> vertexIndices = new List<int>(polygon.Length);
        for (int i = 0; i < polygon.Length; i++)
            vertexIndices.Add(i);

        int iterationLimit = polygon.Length * polygon.Length;
        int iterations = 0;

        while (vertexIndices.Count > 3 && iterations < iterationLimit)
        {
            bool earFound = false;
            for (int i = 0; i < vertexIndices.Count; i++)
            {
                int prev = vertexIndices[(i - 1 + vertexIndices.Count) % vertexIndices.Count];
                int curr = vertexIndices[i];
                int next = vertexIndices[(i + 1) % vertexIndices.Count];

                if (!IsTriangleConvex(polygon[prev], polygon[curr], polygon[next]))
                    continue;

                bool containsVertex = false;
                for (int j = 0; j < vertexIndices.Count; j++)
                {
                    int candidate = vertexIndices[j];
                    if (candidate == prev || candidate == curr || candidate == next)
                        continue;
                    if (IsPointInTriangleStrict(polygon[candidate], polygon[prev], polygon[curr], polygon[next]))
                    {
                        containsVertex = true;
                        break;
                    }
                }

                if (containsVertex)
                    continue;

                triangles.Add(prev);
                triangles.Add(curr);
                triangles.Add(next);
                vertexIndices.RemoveAt(i);
                earFound = true;
                break;
            }

            if (!earFound)
                break;

            iterations++;
        }

        if (vertexIndices.Count == 3)
        {
            triangles.Add(vertexIndices[0]);
            triangles.Add(vertexIndices[1]);
            triangles.Add(vertexIndices[2]);
        }

        return triangles;
    }

    private static bool IsTriangleConvex(Vector2 prev, Vector2 current, Vector2 next)
    {
        return Cross(prev, current, next) > 1e-5f;
    }

    private static bool IsPointInTriangleStrict(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        if (point == a || point == b || point == c)
            return false;

        float cross1 = Cross(a, b, point);
        float cross2 = Cross(b, c, point);
        float cross3 = Cross(c, a, point);

        bool hasNegative = cross1 < -1e-5f || cross2 < -1e-5f || cross3 < -1e-5f;
        bool hasPositive = cross1 > 1e-5f || cross2 > 1e-5f || cross3 > 1e-5f;

        return !(hasNegative && hasPositive);
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 ab = b - a;
        Vector2 ac = c - a;
        return ab.x * ac.y - ab.y * ac.x;
    }
}
