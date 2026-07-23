using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;

/// <summary>
/// グリッドの2D/3Dオーバーレイ表示とスナップ機能を管理するシングルトン。
///
/// 表示モード:
///   - Off:  非表示
///   - Line: 2Dスクリーンオーバーレイにラインを描画（地形に追従）
///
/// パフォーマンス対策:
///   - LINEモード: GameObjectプーリング。毎フレームDestroyしない。
///   - カメラが一定距離(0.5m)以上移動 or 一定角度(1°)以上回転した時のみ再構築。
///   - Materialは1つ共有。毎回newしない。
/// </summary>
public class GridOverlay : MonoBehaviour
{
    public static GridOverlay Instance { get; private set; }

    // グリッド表示状態変更イベント
    public delegate void GridVisibilityChanged(bool isVisible);
    public static event GridVisibilityChanged OnGridVisibilityChanged;

    // グリッドモード変更イベントとモード定義
    public enum GridDisplayMode { Off, Line }
    public delegate void GridModeChanged(GridDisplayMode mode);
    public static event GridModeChanged OnGridModeChanged;

    [Header("Grid Settings")]
    [SerializeField] private float gridSize = 5f;
    [SerializeField] private Material lineMaterial;
    /// <summary>グリッド全体の基本描画色。半透明にして視認性を確保しつつ画面の邪魔にならないようにする。</summary>
    [SerializeField] private Color gridColor = new Color(1f, 1f, 1f, 0.4f);
    /// <summary>グリッド線の太さ。視認性を向上させるために3.0pxをデフォルト値とする。</summary>
    [SerializeField] private float lineThickness = 3f;
    /// <summary>視野中心から何メートル先までグリッドを描画するか。描画負荷と表示範囲のバランスをとる。</summary>
    [SerializeField] private float gridRadius = 300f;
    /// <summary>フェードアウトを開始する距離の比率（0.0〜1.0）。gridRadiusに対する割合。</summary>
    [SerializeField] private float fadeStartFactor = 0.5f;
    /// <summary>フェードアウトが完了して完全に透明になる距離の比率（0.0〜1.0）。gridRadiusに対する割合。</summary>
    [SerializeField] private float fadeEndFactor = 1.0f;
    /// <summary>長いグリッド線をどれくらいの長さ（メートル）で分割して描画するか。分割することで端に行くほど徐々に薄くなる綺麗なフェード効果を実現する。</summary>
    [SerializeField] private float segmentLength = 30f;

    /// <summary>
    /// スクリーン上に描画される1本のグリッド線の情報。
    /// 座標変換後の2D位置と、中心からの距離に基づくフェードアウト用の不透明度を保持する。
    /// </summary>
    private struct ScreenLineInfo
    {
        public Vector2 start;
        public Vector2 end;
        public float opacity;
    }

    // LINEモード用
    private GameObject lineCanvasRoot;
    private Canvas lineCanvas;
    private RectTransform lineCanvasRect;
    /// <summary>現在アクティブに表示中のライン。</summary>
    private readonly List<GameObject> activeLines = new List<GameObject>();
    /// <summary>非アクティブ化して再利用待ちのライン。</summary>
    private readonly Queue<GameObject> pooledLines = new Queue<GameObject>();

    public GridDisplayMode Mode { get; private set; } = GridDisplayMode.Off;

    public float GridAngle { get; private set; } = 0f;
    public bool IsVisible => Mode != GridDisplayMode.Off;
    public Vector3 GridOrigin { get; private set; } = Vector3.zero;

    public void SetGridOrigin(Vector3 origin)
    {
        GridOrigin = origin;
        if (Mode == GridDisplayMode.Line)
        {
            RebuildOverlayLines();
        }
    }

    // カメラ変化検知用
    private Vector3 lastCameraPos;
    private Quaternion lastCameraRot;
    private const float CAMERA_POS_THRESHOLD = 0.5f;
    private const float CAMERA_ROT_THRESHOLD = 1.0f;

    // カーソル位置変化検知用
    private Vector3 lastCursorPos;
    private const float CURSOR_POS_THRESHOLD = 0.5f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnViewSetGrid += HandleViewSetGrid;
        }
    }

    private void OnDestroy()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnViewSetGrid -= HandleViewSetGrid;
        }
    }

    /// <summary>
    /// UIから view:set_grid イベントを受信した際のハンドラ。
    /// グリッドの表示モード・サイズ・角度を反映し、
    /// グリッド吸着の有効/無効を自動で切り替える。
    /// </summary>
    private void HandleViewSetGrid(string mode, float size, float angle, float? originX, float? originZ)
    {
        GridDisplayMode targetMode = mode.ToLower() switch
        {
            "line" => GridDisplayMode.Line,
            _ => GridDisplayMode.Off,
        };

        SetMode(targetMode);

        if (size > 0f)
        {
            SetGridSize(size);
        }

        SetGridAngle(angle);

        if (originX.HasValue && originZ.HasValue)
        {
            SetGridOrigin(new Vector3(originX.Value, 0f, originZ.Value));
        }
        else
        {
            SetGridOrigin(Vector3.zero);
        }

        // グリッド表示時は地形クリックのスナップを有効化する
        TerrainInputHandler.EnableGridSnapping = (targetMode != GridDisplayMode.Off);
    }

    public void SetGridVisible(bool visible)
    {
        SetMode(visible ? GridDisplayMode.Line : GridDisplayMode.Off);
    }

    /// <summary>
    /// ラジアルメニューからのグリッドトグル。表示とグリッド吸着を連動して切り替え、
    /// UIパネル(グリッド設定)の表示状態が食い違わないよう view:grid_toggled で通知する。
    /// サイズ・角度・原点は直近に view:set_grid で受信した値(未受信ならシリアライズ既定値)を維持する。
    /// </summary>
    public void ToggleVisibility()
    {
        bool newVisible = !IsVisible;
        SetMode(newVisible ? GridDisplayMode.Line : GridDisplayMode.Off);
        TerrainInputHandler.EnableGridSnapping = newVisible;
        IpcClient.Instance?.EmitViewGridToggled(newVisible);
    }

    public void SetMode(GridDisplayMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;

        switch (Mode)
        {
            case GridDisplayMode.Off:
                HideLineCanvas();
                break;
            case GridDisplayMode.Line:
                EnsureLineCanvas();
                RebuildOverlayLines();
                break;
        }

        OnGridVisibilityChanged?.Invoke(Mode != GridDisplayMode.Off);
        OnGridModeChanged?.Invoke(Mode);
    }

    // =====================================================================
    // 可視セル取得
    // =====================================================================
    // ※ セルに依存しない半径ベース描画へ移行したため、セルインデックスの取得処理は廃止されました。

    // =====================================================================
    // ライフサイクル (カメラ変化検知)
    // =====================================================================

    private void Update()
    {
        if (Mode == GridDisplayMode.Off) return;

        Camera mainCamera = Camera.main;
        if (mainCamera == null) return;

        Vector3 currentPos = mainCamera.transform.position;
        Quaternion currentRot = mainCamera.transform.rotation;

        bool posChanged = Vector3.Distance(currentPos, lastCameraPos) > CAMERA_POS_THRESHOLD;
        bool rotChanged = Quaternion.Angle(currentRot, lastCameraRot) > CAMERA_ROT_THRESHOLD;

        if (Mode == GridDisplayMode.Line)
        {
            Vector3 currentCursorPos = InputHandler.Instance != null
                ? new Vector3(InputHandler.Instance.posX, 0f, InputHandler.Instance.posZ)
                : Vector3.zero;
            bool cursorChanged = Vector3.Distance(currentCursorPos, lastCursorPos) > CURSOR_POS_THRESHOLD;

            if (posChanged || rotChanged || cursorChanged)
            {
                RebuildOverlayLines();
                lastCameraPos = currentPos;
                lastCameraRot = currentRot;
                lastCursorPos = currentCursorPos;
            }
        }
    }

    // =====================================================================
    // LINE モード (2D Overlay + プーリング)
    // =====================================================================

    private void EnsureLineCanvas()
    {
        if (lineCanvasRoot != null) return;

        lineCanvasRoot = new GameObject("GridOverlayCanvas");
        DontDestroyOnLoad(lineCanvasRoot);
        lineCanvas = lineCanvasRoot.AddComponent<Canvas>();
        lineCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        lineCanvas.sortingOrder = 60;
        lineCanvasRect = lineCanvasRoot.GetComponent<RectTransform>();
        lineCanvasRoot.AddComponent<CanvasScaler>();
        lineCanvasRoot.AddComponent<GraphicRaycaster>();
    }

    private void HideLineCanvas()
    {
        if (lineCanvasRoot != null)
        {
            // アクティブラインをすべてプールに戻す
            foreach (var go in activeLines)
            {
                if (go != null) ReturnLineToPool(go);
            }
            activeLines.Clear();
            lineCanvasRoot.SetActive(false);
        }
    }

    private void RebuildOverlayLines()
    {
        if (Camera.main == null) return;
        EnsureLineCanvas();
        lineCanvasRoot.SetActive(true);

        // 今回必要なライン定義を構築
        List<ScreenLineInfo> requiredLines = new List<ScreenLineInfo>();
        CollectRequiredLines(requiredLines);

        // 既存ラインを一旦全てプールに戻す
        foreach (var go in activeLines)
        {
            if (go != null) ReturnLineToPool(go);
        }
        activeLines.Clear();

        // 必要なラインをプールから復帰 or 新規生成
        foreach (var line in requiredLines)
        {
            GameObject go = GetOrCreateLine();
            UpdateLineTransform(go, line.start, line.end);

            // グリッドの基本描画色（半透明）に、中心からの距離減衰不透明度を乗算して適用する。
            Image img = go.GetComponent<Image>();
            if (img != null)
            {
                img.color = new Color(gridColor.r, gridColor.g, gridColor.b, gridColor.a * line.opacity);
            }

            go.SetActive(true);
            activeLines.Add(go);
        }
    }

    /// <summary>
    /// 視野の中心（カメラ注視点）から一定半径の範囲内にグリッド線を計算し、スクリーン座標上のラインとして収集する。
    /// セル単位の枠にとらわれず、視野中心からの距離に応じた滑らかな描画を行う。
    /// また、長いグリッド線を一定間隔（segmentLength）でセグメント分割し、各線分の中点と視野中心との距離に応じて
    /// 不透明度（opacity）を算出することで、端に行くほど滑らかにフェードアウトする効果を実現する。
    /// </summary>
    private void CollectRequiredLines(List<ScreenLineInfo> requiredLines)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 cameraPos = cam.transform.position;
        // グリッド描画中心: カーソルと地形の交点を基準とする。InputHandlerが未初期化の場合は視点中心にフォールバック。
        Vector3 centerWorldPos;
        if (InputHandler.Instance != null)
            centerWorldPos = new Vector3(InputHandler.Instance.posX, 0f, InputHandler.Instance.posZ);
        else if (CameraCellChecker.Instance != null)
            centerWorldPos = CameraCellChecker.Instance.CurrentWorldPosition;
        else
            centerWorldPos = new Vector3(cameraPos.x, 0f, cameraPos.z);
        centerWorldPos.y = 0f; // 高低差による距離の歪みを防ぐため、水平面上の座標のみで距離を測る

        // グリッド原点を考慮した相対中心座標
        Vector3 relativeCenter = centerWorldPos - GridOrigin;

        // グリッド回転角を考慮したローカル座標系に変換
        Quaternion rot = Quaternion.Euler(0, GridAngle, 0);
        Quaternion invRot = Quaternion.Inverse(rot);
        Vector3 localCenter = invRot * relativeCenter;

        // ローカル座標上での描画範囲
        float minLocalX = localCenter.x - gridRadius;
        float maxLocalX = localCenter.x + gridRadius;
        float minLocalZ = localCenter.z - gridRadius;
        float maxLocalZ = localCenter.z + gridRadius;

        float dStart = gridRadius * fadeStartFactor;
        float dEnd = gridRadius * fadeEndFactor;

        // 1. 垂直ライン (X固定、Z方向に走る)
        int startIdxX = Mathf.CeilToInt(minLocalX / gridSize);
        int endIdxX = Mathf.FloorToInt(maxLocalX / gridSize);
        for (int i = startIdxX; i <= endIdxX; i++)
        {
            float lx = i * gridSize;
            // Z方向の線分を segmentLength 刻みで分割して描画
            for (float lz = minLocalZ; lz < maxLocalZ; lz += segmentLength)
            {
                float nextLz = Mathf.Min(lz + segmentLength, maxLocalZ);
                Vector3 localA = new Vector3(lx, 0f, lz);
                Vector3 localB = new Vector3(lx, 0f, nextLz);

                // ワールド座標に逆変換（原点オフセットを適用）
                Vector3 worldA = rot * localA + GridOrigin;
                Vector3 worldB = rot * localB + GridOrigin;
                worldA.y = Utils.GetGroundHeight(worldA);
                worldB.y = Utils.GetGroundHeight(worldB);

                // 各セグメントの中点と中心との水平距離を計算
                Vector3 mid = (worldA + worldB) * 0.5f;
                mid.y = 0f;
                float dist = Vector3.Distance(mid, centerWorldPos);

                if (dist < dEnd)
                {
                    float opacity = 1.0f;
                    if (dist > dStart)
                    {
                        opacity = Mathf.Clamp01(1.0f - (dist - dStart) / (dEnd - dStart));
                    }
                    TryAddScreenLine(requiredLines, cam, worldA, worldB, opacity);
                }
            }
        }

        // 2. 水平ライン (Z固定、X方向に走る)
        int startIdxZ = Mathf.CeilToInt(minLocalZ / gridSize);
        int endIdxZ = Mathf.FloorToInt(maxLocalZ / gridSize);
        for (int i = startIdxZ; i <= endIdxZ; i++)
        {
            float lz = i * gridSize;
            // X方向の線分を segmentLength 刻みで分割して描画
            for (float lx = minLocalX; lx < maxLocalX; lx += segmentLength)
            {
                float nextLx = Mathf.Min(lx + segmentLength, maxLocalX);
                Vector3 localA = new Vector3(lx, 0f, lz);
                Vector3 localB = new Vector3(nextLx, 0f, lz);

                // ワールド座標に逆変換（原点オフセットを適用）
                Vector3 worldA = rot * localA + GridOrigin;
                Vector3 worldB = rot * localB + GridOrigin;
                worldA.y = Utils.GetGroundHeight(worldA);
                worldB.y = Utils.GetGroundHeight(worldB);

                // 各セグメントの中点と中心との水平距離を計算
                Vector3 mid = (worldA + worldB) * 0.5f;
                mid.y = 0f;
                float dist = Vector3.Distance(mid, centerWorldPos);

                if (dist < dEnd)
                {
                    float opacity = 1.0f;
                    if (dist > dStart)
                    {
                        opacity = Mathf.Clamp01(1.0f - (dist - dStart) / (dEnd - dStart));
                    }
                    TryAddScreenLine(requiredLines, cam, worldA, worldB, opacity);
                }
            }
        }
    }

    /// <summary>
    /// 3D空間の2点（ラインの端点）をカメラのNearPlaneでクリッピングし、スクリーン座標へ変換してリストに追加する。
    /// </summary>
    private void TryAddScreenLine(List<ScreenLineInfo> list, Camera cam, Vector3 worldA, Vector3 worldB, float opacity)
    {
        Vector3 ca = worldA;
        Vector3 cb = worldB;
        if (!ClipLineToNearPlane(ref ca, ref cb, cam)) return;

        Vector3 spA = cam.WorldToScreenPoint(ca);
        Vector3 spB = cam.WorldToScreenPoint(cb);
        list.Add(new ScreenLineInfo
        {
            start = new Vector2(spA.x, spA.y),
            end = new Vector2(spB.x, spB.y),
            opacity = opacity
        });
    }

    private GameObject GetOrCreateLine()
    {
        if (pooledLines.Count > 0)
        {
            return pooledLines.Dequeue();
        }

        GameObject go = new GameObject("GridLine");
        go.transform.SetParent(lineCanvasRect, false);

        var image = go.AddComponent<Image>();
        image.color = gridColor;
        // グリッド線は純粋な視覚装飾であり、ポインタを遮ってはならない。
        // raycastTarget を true（既定）のままにすると、グリッド線の上にカーソルがある間
        // EventSystem.IsPointerOverGameObject() が真になり、InputHandler がクリック/ホバーを
        // 「UI上」とみなして破棄してしまう（吸着で交点＝線上を狙うため大半のクリックが線上に乗り、
        // 2点目が置けなくなる）。視覚専用なので必ず無効化する。
        image.raycastTarget = false;

        RectTransform rt = image.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);

        return go;
    }

    private void ReturnLineToPool(GameObject go)
    {
        if (go == null) return;
        go.SetActive(false);
        pooledLines.Enqueue(go);
    }

    private void UpdateLineTransform(GameObject go, Vector2 start, Vector2 end)
    {
        RectTransform rt = go.GetComponent<RectTransform>();
        if (rt == null) return;

        Vector2 dir = end - start;
        float length = dir.magnitude;
        if (length < 0.01f)
        {
            rt.sizeDelta = Vector2.zero;
            return;
        }

        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

        rt.sizeDelta = new Vector2(length, lineThickness);
        rt.anchoredPosition = start + dir * 0.5f;
        rt.localRotation = Quaternion.Euler(0, 0, angle);
    }

    private bool ClipLineToNearPlane(ref Vector3 a, ref Vector3 b, Camera cam)
    {
        const float epsilon = 0.001f;
        Vector3 planePoint = cam.transform.position + cam.transform.forward * (cam.nearClipPlane + epsilon);
        Plane nearPlane = new Plane(cam.transform.forward, planePoint);

        float da = nearPlane.GetDistanceToPoint(a);
        float db = nearPlane.GetDistanceToPoint(b);

        if (da < 0f && db < 0f) return false;

        if (da < 0f || db < 0f)
        {
            Vector3 dir = b - a;
            float t = da / (da - db);
            Vector3 p = a + dir * t;
            if (da < 0f) a = p; else b = p;
        }

        return true;
    }

    // =====================================================================
    // パブリックAPI
    // =====================================================================

    public void SetGridAngle(float angle)
    {
        GridAngle = angle;
        // 角度変更時はライン再構築が必要
        if (Mode == GridDisplayMode.Line)
        {
            RebuildOverlayLines();
        }
    }

    public void SetGridSize(float size)
    {
        gridSize = Mathf.Max(0.1f, size);
        if (Mode == GridDisplayMode.Line)
        {
            RebuildOverlayLines();
        }
    }

    public Vector3 SnapPosition(Vector3 worldPos)
    {
        Vector3 relativePos = worldPos - GridOrigin;
        Quaternion inv = Quaternion.Inverse(Quaternion.Euler(0, GridAngle, 0));
        Vector3 local = inv * relativePos;
        float x = Mathf.Round(local.x / gridSize) * gridSize;
        float z = Mathf.Round(local.z / gridSize) * gridSize;

        // スナップされたローカルXZ座標からワールドXZ座標を求める（地面高さを取得するため）
        Vector3 localFlat = new Vector3(x, 0f, z);
        Vector3 worldFlat = Quaternion.Euler(0, GridAngle, 0) * localFlat + GridOrigin;
        float groundHeight = Utils.GetGroundHeight(worldFlat);

        // 高さはスナップ後のワールド座標での地面の高さとする
        return new Vector3(worldFlat.x, groundHeight, worldFlat.z);
    }
}
