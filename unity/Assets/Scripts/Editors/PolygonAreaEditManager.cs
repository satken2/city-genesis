using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// 任意ポリゴン編集の進行フェーズ。
/// Placing: 左クリックで頂点を末尾に追加していく段階(まだポリゴンが閉じていない)。
/// Editing: 右クリックで形状を確定した後の段階。ParcelShapeEditManager と同じ操作感
///          (頂点ドラッグ・辺クリックで頂点挿入・右クリックで頂点削除)で調整できる。
/// </summary>
public enum PolygonEditPhase { Inactive, Placing, Editing }

/// <summary>
/// 地形上に「任意ポリゴン」を作成するための共通基底クラス。
///
/// RoadPlanManager の Placing フェーズ(左クリックで点を配置)と、
/// ParcelShapeEditManager の Editing フェーズ(頂点ドラッグ・辺挿入・右クリック削除)を
/// 組み合わせた操作感で、閉じたポリゴンを組み立てる。頂点編集・オーバーレイ描画・
/// ヒット判定・ドラッグ処理といった「ポリゴンの作り方」は完全にここへ集約し、
/// 天然資源エリア(<see cref="ResourceAreaEditManager"/>)と土地区画の任意ポリゴン作成
/// (<see cref="ParcelAreaEditManager"/>)の両方が本クラスを継承して再利用する。
///
/// 派生クラスが差し替えるのは「完成したポリゴンをどのIPCイベントでUIへ通知するか
/// (<see cref="EmitShape"/>)」と「オーバーレイの色・キャンバス名」だけである。
/// 確定(バックエンドへのPOST送信)はいずれのケースもUI(Tauri)側が直接行うため、
/// このクラスはポリゴン形状の組み立てとUIへの逐次通知のみを担当する。
/// </summary>
public abstract class PolygonAreaEditManager : MonoBehaviour
{
    [Header("References")]
    public Camera gameCamera;

    [Header("Settings")]
    [SerializeField] protected float pointCircleRadius = 8f;
    [SerializeField] protected float solidLineThickness = 1.5f;
    [SerializeField] protected float hoverLineThickness = 3f;

    protected PolygonEditPhase phase = PolygonEditPhase.Inactive;
    protected List<Vector3> vertices = new List<Vector3>();

    // 選択・ホバー状態(Editingフェーズのみ使用)
    protected int selectedVertexIndex = -1;
    protected int hoveredEdgeIndex = -1;

    // ドラッグ状態(Editingフェーズのみ使用)
    protected bool isDragging = false;
    protected int dragVertexIndex = -1;
    protected float dragScreenDepth;

    // オーバーレイ描画。ParcelShapeEditOverlayRenderer は頂点・辺の2D描画に特化した
    // 汎用コンポーネントで区画固有の状態を持たないため、そのまま再利用する。
    protected ParcelShapeEditOverlayRenderer overlayRenderer;
    protected GameObject overlayCanvasRoot;
    protected Canvas overlayCanvas;
    protected RectTransform overlayCanvasRect;

    // TerrainInputHandler への参照(ドラッグ中のスナップ処理の共通化用)
    protected TerrainInputHandler terrainInputHandler;

    public bool IsEditMode => phase != PolygonEditPhase.Inactive;
    public bool IsEditingPhase => phase == PolygonEditPhase.Editing;

    // =====================================================================
    // 派生クラスが差し替える要素
    // =====================================================================

    /// <summary>
    /// 現在のポリゴンをIPC経由でUIへ通知する。派生クラスがドメインごとのイベント名で送信する。
    /// polygon は各頂点を [x, z] で表した頂点列(ワールドXZ)。
    /// finalized は右クリックで形状を仮確定済み(Editingフェーズ)かどうか。
    /// </summary>
    protected abstract void EmitShape(List<List<float>> polygon, bool finalized);

    /// <summary>オーバーレイキャンバスに付けるGameObject名(デバッグ・重複防止用)。</summary>
    protected abstract string OverlayCanvasName { get; }

    /// <summary>ポリゴンの辺の色。ドメインごとに道路・他エリアと区別しやすい色にする。</summary>
    protected abstract Color EdgeColor { get; }

    /// <summary>未選択の頂点の色。</summary>
    protected abstract Color VertexColor { get; }

    /// <summary>選択中の頂点の色(既定は緑)。</summary>
    protected virtual Color SelectedVertexColor => new Color(0.2f, 1f, 0.4f);

    // =====================================================================
    // ライフサイクル
    // =====================================================================

    protected virtual void Start()
    {
        EnsureOverlayCanvas();
        terrainInputHandler = FindAnyObjectByType<TerrainInputHandler>();
        if (terrainInputHandler == null)
        {
            Debug.LogError($"{GetType().Name}: TerrainInputHandlerが見つかりません");
        }
    }

    protected virtual void Update()
    {
        if (!IsEditMode) return;

        if (phase == PolygonEditPhase.Editing)
        {
            UpdateHoverState();
        }
        UpdateOverlay();

        if (InputUtil.KeyWasPressedThisFrame(Key.Escape))
        {
            CancelEditMode();
        }
    }

    // =====================================================================
    // Canvas / Overlay セットアップ
    // =====================================================================

    protected void EnsureOverlayCanvas()
    {
        if (overlayCanvasRoot != null) return;

        overlayCanvasRoot = new GameObject(OverlayCanvasName);
        overlayCanvas = overlayCanvasRoot.AddComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.sortingOrder = 52; // ParcelShapeEditと同じく通常UIより手前に描画
        overlayCanvasRect = overlayCanvasRoot.GetComponent<RectTransform>();
        overlayCanvasRoot.AddComponent<CanvasScaler>();

        overlayRenderer = overlayCanvasRoot.AddComponent<ParcelShapeEditOverlayRenderer>();
        overlayRenderer.Initialize(overlayCanvasRect);
    }

    // =====================================================================
    // パブリックAPI: モード遷移
    // =====================================================================

    /// <summary>
    /// 任意ポリゴンの新規作成モードを初期化する。空のポリゴンからPlacingフェーズで開始する。
    /// </summary>
    public void InitializeCreateMode()
    {
        Debug.Log($"{GetType().Name}: 任意ポリゴン編集モード開始(新規作成)");
        EnsureOverlayCanvas();

        phase = PolygonEditPhase.Placing;
        vertices.Clear();
        selectedVertexIndex = -1;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragVertexIndex = -1;

        EmitShapeUpdate();
    }

    /// <summary>
    /// 編集モードを終了し、状態をクリアする。
    /// </summary>
    public void FinishEditMode()
    {
        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }

        phase = PolygonEditPhase.Inactive;
        vertices.Clear();
        selectedVertexIndex = -1;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragVertexIndex = -1;
    }

    /// <summary>
    /// 編集をキャンセルし、通常モードに戻る。
    /// </summary>
    public void CancelEditMode()
    {
        if (!IsEditMode) return;
        Debug.Log($"{GetType().Name}: 編集キャンセル");
        FinishEditMode();

        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ALL_NORMAL);
        }

        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        if (viewCoordinator != null)
        {
            viewCoordinator.ApplyAllLayerModes();
        }
    }

    // =====================================================================
    // 入力ハンドラ(TerrainInputHandlerから呼ばれる)
    // =====================================================================

    /// <summary>
    /// 地形左クリック。
    /// Placing: 頂点をリスト末尾に追加する。
    /// Editing: 頂点ドラッグ開始、または辺上に頂点を追加する(ParcelShapeEditManagerと同じ)。
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!IsEditMode) return;

        if (phase == PolygonEditPhase.Placing)
        {
            vertices.Add(worldPos);
            EmitShapeUpdate();
            Debug.Log($"{GetType().Name}: 頂点配置 {worldPos} (total: {vertices.Count})");
            return;
        }

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        var hit = TestHit(mouseScreen, cam);

        if (hit.type == HitTargetType.Point)
        {
            selectedVertexIndex = hit.index;
            isDragging = true;
            dragVertexIndex = hit.index;
            dragScreenDepth = cam.WorldToScreenPoint(vertices[hit.index]).z;
            Debug.Log($"{GetType().Name}: 頂点{hit.index}を選択してドラッグ開始");
        }
        else if (hit.type == HitTargetType.Edge)
        {
            InsertVertexOnEdge(hit.index, worldPos);
            Debug.Log($"{GetType().Name}: 辺{hit.index}に頂点を挿入");
        }
        else
        {
            selectedVertexIndex = -1;
        }
    }

    /// <summary>
    /// 地形右クリック。
    /// Placing: 頂点数が3以上ならポリゴンを確定してEditingフェーズへ移行する。
    /// Editing: 頂点を削除する(最低3点は維持)。
    /// </summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (!IsEditMode) return;

        if (phase == PolygonEditPhase.Placing)
        {
            if (vertices.Count >= 3)
            {
                phase = PolygonEditPhase.Editing;
                EmitShapeUpdate();
                Debug.Log($"{GetType().Name}: 形状確定、編集フェーズへ移行 (頂点数: {vertices.Count})");
            }
            return;
        }

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        var hit = TestHit(mouseScreen, cam);

        if (hit.type == HitTargetType.Point && vertices.Count > 3)
        {
            RemoveVertex(hit.index);
        }
    }

    /// <summary>
    /// 地形ホバー。
    /// </summary>
    public void OnTerrainHover(Vector3 worldPos)
    {
        // ホバー状態は UpdateHoverState で毎フレーム更新する
    }

    /// <summary>
    /// ドラッグ開始(InputHandlerから呼ばれる)。Editingフェーズでのみ有効。
    /// </summary>
    public void StartDrag(Vector2 screenPos)
    {
        if (phase != PolygonEditPhase.Editing) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        var hit = TestHit(screenPos, cam);
        if (hit.type == HitTargetType.Point)
        {
            selectedVertexIndex = hit.index;
            isDragging = true;
            dragVertexIndex = hit.index;
            dragScreenDepth = cam.WorldToScreenPoint(vertices[hit.index]).z;
            Debug.Log($"{GetType().Name}: 頂点{hit.index}を選択してドラッグ開始");
        }
    }

    /// <summary>
    /// 指定スクリーン座標が編集中の頂点上にあるか判定する(Editingフェーズでのみ有効)。
    /// CameraController との入力優先競合を解決するために使用する。
    /// </summary>
    public bool IsMouseOverVertex(Vector2 screenPos, Camera cam)
    {
        if (phase != PolygonEditPhase.Editing) return false;
        if (cam == null) return false;

        var hit = TestHit(screenPos, cam);
        return hit.type == HitTargetType.Point;
    }

    /// <summary>
    /// ドラッグ中(InputHandlerから呼ばれる)。Editingフェーズでのみ有効。
    /// </summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (phase != PolygonEditPhase.Editing) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        if (!isDragging)
        {
            Vector2 mouseScreen = InputUtil.MousePosition;
            var hit = TestHit(mouseScreen, cam);

            if (hit.type == HitTargetType.Point)
            {
                selectedVertexIndex = hit.index;
                isDragging = true;
                dragVertexIndex = hit.index;
                dragScreenDepth = cam.WorldToScreenPoint(vertices[hit.index]).z;
                Debug.Log($"{GetType().Name}: 頂点{hit.index}を選択してドラッグ開始");
            }
            else
            {
                return;
            }
        }

        Vector3 mouseScreenPos = InputUtil.MousePosition;
        Vector3 newWorldPos = cam.ScreenToWorldPoint(new Vector3(mouseScreenPos.x, mouseScreenPos.y, dragScreenDepth));
        newWorldPos.y = Utils.GetGroundHeight(newWorldPos);

        if (terrainInputHandler != null)
        {
            newWorldPos = terrainInputHandler.ApplySnapping(newWorldPos);
        }

        if (dragVertexIndex >= 0 && dragVertexIndex < vertices.Count)
        {
            vertices[dragVertexIndex] = newWorldPos;
            EmitShapeUpdate();
        }
    }

    /// <summary>
    /// ドラッグ終了(InputHandlerから呼ばれる)。
    /// </summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        if (isDragging && phase == PolygonEditPhase.Editing)
        {
            isDragging = false;
            dragVertexIndex = -1;
            EmitShapeUpdate();
        }
    }

    // =====================================================================
    // 頂点操作(Editingフェーズ)
    // =====================================================================

    /// <summary>
    /// 辺上に頂点を挿入する。
    /// </summary>
    protected void InsertVertexOnEdge(int edgeIndex, Vector3 worldPos)
    {
        if (edgeIndex < 0 || edgeIndex >= vertices.Count) return;

        worldPos.y = Utils.GetGroundHeight(worldPos);
        int insertIndex = edgeIndex + 1;
        vertices.Insert(insertIndex, worldPos);

        if (selectedVertexIndex >= insertIndex) selectedVertexIndex++;

        EmitShapeUpdate();
        Debug.Log($"{GetType().Name}: 辺{edgeIndex}に頂点挿入 (total: {vertices.Count})");
    }

    /// <summary>
    /// 頂点を削除する。常に最低3点は維持する。
    /// </summary>
    protected void RemoveVertex(int index)
    {
        if (vertices.Count <= 3) return;
        if (index < 0 || index >= vertices.Count) return;

        vertices.RemoveAt(index);

        if (selectedVertexIndex == index) selectedVertexIndex = -1;
        else if (selectedVertexIndex > index) selectedVertexIndex--;

        EmitShapeUpdate();
        Debug.Log($"{GetType().Name}: 頂点{index}を削除 (残り: {vertices.Count})");
    }

    // =====================================================================
    // ヒット判定(Editingフェーズでのみ意味を持つ。Placing中は常にNoneを返す)
    // =====================================================================

    protected enum HitTargetType { None, Point, Edge }

    protected struct HitResult
    {
        public HitTargetType type;
        public int index;
    }

    /// <summary>
    /// スクリーン座標でヒット判定を行う。
    /// 頂点(円)を優先し、次に辺(線分)を判定する。
    /// </summary>
    protected HitResult TestHit(Vector2 mouseScreen, Camera cam)
    {
        if (phase != PolygonEditPhase.Editing)
        {
            return new HitResult { type = HitTargetType.None, index = -1 };
        }

        // 頂点ヒット(優先)
        float pointHitRadius = pointCircleRadius + 4f;
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector3 screen = cam.WorldToScreenPoint(vertices[i]);
            if (screen.z < 0) continue;
            if (Vector2.Distance(mouseScreen, screen) <= pointHitRadius)
            {
                return new HitResult { type = HitTargetType.Point, index = i };
            }
        }

        // 辺ヒット
        float edgeHitThreshold = 8f;
        for (int i = 0; i < vertices.Count; i++)
        {
            int next = (i + 1) % vertices.Count;
            Vector3 s1 = cam.WorldToScreenPoint(vertices[i]);
            Vector3 s2 = cam.WorldToScreenPoint(vertices[next]);
            if (s1.z < 0 && s2.z < 0) continue;

            float dist = PointToSegmentDistance(mouseScreen, s1, s2);
            if (dist <= edgeHitThreshold)
            {
                return new HitResult { type = HitTargetType.Edge, index = i };
            }
        }

        return new HitResult { type = HitTargetType.None, index = -1 };
    }

    protected static float PointToSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSqr = ab.sqrMagnitude;
        if (lenSqr < 1e-6f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSqr);
        Vector2 closest = a + ab * t;
        return Vector2.Distance(p, closest);
    }

    // =====================================================================
    // ホバー状態更新(Editingフェーズ)
    // =====================================================================

    protected void UpdateHoverState()
    {
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        var hit = TestHit(mouseScreen, cam);
        hoveredEdgeIndex = (hit.type == HitTargetType.Edge) ? hit.index : -1;
    }

    // =====================================================================
    // 2Dオーバーレイ描画
    // =====================================================================

    protected void UpdateOverlay()
    {
        if (overlayRenderer == null) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();

        DrawEdges(cam);
        DrawVertices(cam);

        if (phase == PolygonEditPhase.Placing)
        {
            DrawPlacingPreviewLine(cam);
        }

        if (phase == PolygonEditPhase.Editing && hoveredEdgeIndex >= 0)
        {
            DrawHoverHighlight(cam);
        }

        overlayRenderer.EndFrame();
    }

    /// <summary>
    /// ポリゴンの辺を描画する。
    /// Placingフェーズはまだ閉じていない折れ線として描画する(最後の頂点→最初の頂点の辺は結ばない)。
    /// </summary>
    protected void DrawEdges(Camera cam)
    {
        Color edgeColor = EdgeColor;

        int segmentCount = phase == PolygonEditPhase.Placing ? vertices.Count - 1 : vertices.Count;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % vertices.Count;
            Vector3 s1 = cam.WorldToScreenPoint(vertices[i]);
            Vector3 s2 = cam.WorldToScreenPoint(vertices[next]);

            if (s1.z < 0 && s2.z < 0) continue;

            overlayRenderer.AddSolidLine(s1.x, s1.y, s2.x, s2.y, edgeColor, solidLineThickness);
        }
    }

    /// <summary>
    /// Placingフェーズで、最後に配置した頂点からマウス位置へのプレビュー線を描画する。
    /// </summary>
    protected void DrawPlacingPreviewLine(Camera cam)
    {
        if (vertices.Count == 0) return;

        Vector3 lastScreen = cam.WorldToScreenPoint(vertices[vertices.Count - 1]);
        if (lastScreen.z < 0) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        Color baseColor = EdgeColor;
        Color previewColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.5f);
        overlayRenderer.AddSolidLine(lastScreen.x, lastScreen.y, mouseScreen.x, mouseScreen.y, previewColor, solidLineThickness);
    }

    /// <summary>
    /// 頂点を円で描画する。
    /// 選択中の頂点は選択色、その他は通常色。
    /// </summary>
    protected void DrawVertices(Camera cam)
    {
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector3 screen = cam.WorldToScreenPoint(vertices[i]);
            if (screen.z < 0) continue;

            Color c = (i == selectedVertexIndex) ? SelectedVertexColor : VertexColor;
            overlayRenderer.AddCircle(screen.x, screen.y, pointCircleRadius, c);
        }
    }

    /// <summary>
    /// ホバー中の辺を黄色い太線でハイライトする。
    /// </summary>
    protected void DrawHoverHighlight(Camera cam)
    {
        if (hoveredEdgeIndex < 0 || hoveredEdgeIndex >= vertices.Count) return;

        int next = (hoveredEdgeIndex + 1) % vertices.Count;
        Vector2 s1 = cam.WorldToScreenPoint(vertices[hoveredEdgeIndex]);
        Vector2 s2 = cam.WorldToScreenPoint(vertices[next]);

        Color highlightColor = new Color(1f, 1f, 0.3f, 0.8f);
        overlayRenderer.AddSolidLine(s1.x, s1.y, s2.x, s2.y, highlightColor, hoverLineThickness);
    }

    // =====================================================================
    // IPC
    // =====================================================================

    /// <summary>
    /// 現在のポリゴンを [x, z] 頂点列に変換し、派生クラスの <see cref="EmitShape"/> でUIへ通知する。
    /// </summary>
    protected void EmitShapeUpdate()
    {
        var polygon = new List<List<float>>();
        foreach (var v in vertices)
        {
            polygon.Add(new List<float> { v.x, v.z });
        }

        EmitShape(polygon, phase == PolygonEditPhase.Editing);
    }
}
