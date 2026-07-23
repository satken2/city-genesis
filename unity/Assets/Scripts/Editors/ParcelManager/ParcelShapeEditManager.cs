using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// 土地区画形状編集マネージャ。
/// RoadPlanManager の Editing フェーズと同じ操作感で、
/// 既存の土地区画ポリゴンの頂点を編集できるようにする。
///
/// 操作感:
///   - ドラッグ: 頂点を移動
///   - 辺上クリック: 頂点を追加
///   - 右クリック: 頂点を削除（最低3点は維持）
///   - Escキー: キャンセル（UIに通知）
///
/// 編集中は `edit_parcel:update_shape` でUIにポリゴン変更を随時通知する。
/// 確定(PATCH /parcels/{id})はUI(Tauri)側が直接行い、保存成功後に届く
/// `edit_parcel:finish_shape_edit` で CompleteEditMode が呼ばれて編集を終了する。
/// </summary>
public class ParcelShapeEditManager : MonoBehaviour
{
    [Header("References")]
    public Camera gameCamera;
    public ParcelManager parcelManager;

    [Header("Settings")]
    [SerializeField] private float pointCircleRadius = 8f;
    [SerializeField] private float solidLineThickness = 1.5f;
    [SerializeField] private float hoverLineThickness = 3f;

    // 編集対象
    private int editingParcelId = -1;
    private List<Vector3> vertices = new List<Vector3>();

    // 選択・ホバー状態
    private int selectedVertexIndex = -1;
    private int hoveredEdgeIndex = -1;

    // ドラッグ状態
    private bool isDragging = false;
    private int dragVertexIndex = -1;
    private float dragScreenDepth;

    // オーバーレイ
    private ParcelShapeEditOverlayRenderer overlayRenderer;
    private GameObject overlayCanvasRoot;
    private Canvas overlayCanvas;
    private RectTransform overlayCanvasRect;

    // TerrainInputHandler への参照（スナップ処理の共通化用）
    private TerrainInputHandler terrainInputHandler;

    public bool IsEditMode => editingParcelId >= 0;

    // =====================================================================
    // ライフサイクル
    // =====================================================================

    void Start()
    {
        EnsureOverlayCanvas();
        terrainInputHandler = FindAnyObjectByType<TerrainInputHandler>();
        if (terrainInputHandler == null)
        {
            Debug.LogError("ParcelShapeEditManager: TerrainInputHandlerが見つかりません");
        }
        if (parcelManager == null)
        {
            parcelManager = FindAnyObjectByType<ParcelManager>();
        }
    }

    void Update()
    {
        if (!IsEditMode) return;

        UpdateHoverState();
        UpdateOverlay();

        if (InputUtil.KeyWasPressedThisFrame(Key.Escape))
        {
            CancelEditMode();
        }
    }

    // =====================================================================
    // Canvas / Overlay セットアップ
    // =====================================================================

    private void EnsureOverlayCanvas()
    {
        if (overlayCanvasRoot != null) return;

        overlayCanvasRoot = new GameObject("ParcelShapeEditOverlayCanvas");
        overlayCanvas = overlayCanvasRoot.AddComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.sortingOrder = 52; // ParcelUICanvas(50) より上
        overlayCanvasRect = overlayCanvasRoot.GetComponent<RectTransform>();
        overlayCanvasRoot.AddComponent<CanvasScaler>();

        overlayRenderer = overlayCanvasRoot.AddComponent<ParcelShapeEditOverlayRenderer>();
        overlayRenderer.Initialize(overlayCanvasRect);
    }

    // =====================================================================
    // パブリックAPI: モード遷移
    // =====================================================================

    /// <summary>
    /// 形状編集モードを初期化する。
    /// 指定されたparcel_idとpolygonから編集状態を構築する。
    /// </summary>
    public void InitializeEditMode(int parcelId, float[][] polygon)
    {
        Debug.Log($"ParcelShapeEditManager: 形状編集モード開始 parcel={parcelId}");
        EnsureOverlayCanvas();

        editingParcelId = parcelId;
        vertices.Clear();
        selectedVertexIndex = -1;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragVertexIndex = -1;

        if (polygon != null)
        {
            foreach (var pt in polygon)
            {
                if (pt == null || pt.Length < 2) continue;
                float x = pt[0];
                float z = pt[1];
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
                vertices.Add(new Vector3(x, y, z));
            }
        }

        // 既存の区画オブジェクトを非表示にして、編集中のオーバーレイだけを見せる
        HideOriginalParcelObject(parcelId);

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

        // 非表示にしていた元の区画オブジェクトを再表示する
        ShowOriginalParcelObject(editingParcelId);

        editingParcelId = -1;
        vertices.Clear();
        selectedVertexIndex = -1;
        hoveredEdgeIndex = -1;
        isDragging = false;
        dragVertexIndex = -1;
    }

    /// <summary>
    /// 編集をキャンセルし、通常モードに戻る。
    /// UIにキャンセル通知を送信する。
    /// </summary>
    public void CancelEditMode()
    {
        if (!IsEditMode) return;
        Debug.Log("ParcelShapeEditManager: 編集キャンセル");
        FinishEditMode();
        if (IpcClient.Instance != null)
        {
            // UI側にキャンセルを通知し、通常モードに戻す
            // 実際には IpcStateCoordinator が OnEditParcelCancelShapeEdit を
            // ハンドルして ApplyStateNormal を呼ぶため、ここでは通知のみ
        }
        // 入力モードを通常に戻す
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ALL_NORMAL);
        }

        // VisualMode を通常に戻す
        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        if (viewCoordinator != null)
        {
            viewCoordinator.ApplyAllLayerModes();
        }
    }

    /// <summary>
    /// UI側でのポリゴン保存(PATCH /parcels/{id})成功後に呼ばれる編集終了処理。
    /// バックエンドへの送信自体はUI(Tauri)側が直接行うため、ここでは編集状態のクリアと
    /// 最新データの再取得・区画枠線の再描画のみを行う。
    /// </summary>
    public void CompleteEditMode()
    {
        FinishEditMode();

        // 差分再描画。version比較により変更のないオブジェクトの再描画はスキップされる。
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }

        // カメラが動いていなくても区画枠線が即座に更新されるよう、強制再描画を要求する。
        if (parcelManager != null)
        {
            parcelManager.ForceOutlineRedraw();
        }
    }

    // =====================================================================
    // 入力ハンドラ（TerrainInputHandlerから呼ばれる）
    // =====================================================================

    /// <summary>
    /// 地形左クリック。
    /// 頂点ドラッグ開始、または辺上に頂点を追加する。
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!IsEditMode) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        Vector2 mouseScreen = InputUtil.MousePosition;
        var hit = TestHit(mouseScreen, cam);
        Debug.Log($"ParcelShapeEditManager.OnTerrainLeftClick: hit={hit.type} index={hit.index} mouseScreen={mouseScreen}");

        if (hit.type == HitTargetType.Point)
        {
            selectedVertexIndex = hit.index;
            isDragging = true;
            dragVertexIndex = hit.index;
            dragScreenDepth = cam.WorldToScreenPoint(vertices[hit.index]).z;
            Debug.Log($"ParcelShapeEditManager: 頂点{hit.index}を選択してドラッグ開始");
        }
        else if (hit.type == HitTargetType.Edge)
        {
            InsertVertexOnEdge(hit.index, worldPos);
            Debug.Log($"ParcelShapeEditManager: 辺{hit.index}に頂点を挿入");
        }
        else
        {
            selectedVertexIndex = -1;
        }
    }

    /// <summary>
    /// 地形右クリック。
    /// 頂点を削除する（最低3点は維持）。
    /// </summary>
    public void OnTerrainRightClick(Vector3 worldPos)
    {
        if (!IsEditMode) return;

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
    /// ドラッグ開始（InputHandlerから呼ばれる）。
    /// マウス位置で頂点ヒット判定を行い、ヒットしていればドラッグ状態を初期化する。
    /// </summary>
    public void StartDrag(Vector2 screenPos)
    {
        if (!IsEditMode) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        var hit = TestHit(screenPos, cam);

        if (hit.type == HitTargetType.Point)
        {
            selectedVertexIndex = hit.index;
            isDragging = true;
            dragVertexIndex = hit.index;
            dragScreenDepth = cam.WorldToScreenPoint(vertices[hit.index]).z;
            Debug.Log($"ParcelShapeEditManager: 頂点{hit.index}を選択してドラッグ開始");
        }
    }

    /// <summary>
    /// 指定スクリーン座標が編集中の頂点上にあるか判定する。
    /// CameraController との入力優先競合を解決するために使用する。
    /// </summary>
    public bool IsMouseOverVertex(Vector2 screenPos, Camera cam)
    {
        if (!IsEditMode) return false;
        if (cam == null) return false;

        var hit = TestHit(screenPos, cam);
        return hit.type == HitTargetType.Point;
    }

    /// <summary>
    /// ドラッグ中（InputHandlerから呼ばれる）。
    /// </summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (!IsEditMode) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        // ドラッグ未開始の場合、現在のマウス位置でドラッグ対象を決定する。
        // InputHandler がマウスダウンとドラッグを区別するようになったため、
        // ドラッグ確定時には既に OnTerrainLeftClick が呼ばれていない可能性がある。
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
                Debug.Log($"ParcelShapeEditManager: 頂点{hit.index}を選択してドラッグ開始");
            }
            else
            {
                // ドラッグ対象がない場合は何もしない
                return;
            }
        }

        Vector3 mouseScreenPos = InputUtil.MousePosition;
        Vector3 newWorldPos = cam.ScreenToWorldPoint(new Vector3(mouseScreenPos.x, mouseScreenPos.y, dragScreenDepth));
        newWorldPos.y = Utils.GetGroundHeight(newWorldPos);

        // スナップ適用
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
    /// ドラッグ終了（InputHandlerから呼ばれる）。
    /// </summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        if (isDragging && IsEditMode)
        {
            isDragging = false;
            dragVertexIndex = -1;
            EmitShapeUpdate();
        }
    }

    // =====================================================================
    // 頂点操作
    // =====================================================================

    /// <summary>
    /// 辺上に頂点を挿入する。
    /// </summary>
    private void InsertVertexOnEdge(int edgeIndex, Vector3 worldPos)
    {
        if (edgeIndex < 0 || edgeIndex >= vertices.Count) return;

        worldPos.y = Utils.GetGroundHeight(worldPos);
        int insertIndex = edgeIndex + 1;
        vertices.Insert(insertIndex, worldPos);

        if (selectedVertexIndex >= insertIndex) selectedVertexIndex++;

        EmitShapeUpdate();
        Debug.Log($"ParcelShapeEditManager: 辺{edgeIndex}に頂点挿入 (total: {vertices.Count})");
    }

    /// <summary>
    /// 頂点を削除する。常に最低3点は維持する。
    /// </summary>
    private void RemoveVertex(int index)
    {
        if (vertices.Count <= 3) return;
        if (index < 0 || index >= vertices.Count) return;

        vertices.RemoveAt(index);

        if (selectedVertexIndex == index) selectedVertexIndex = -1;
        else if (selectedVertexIndex > index) selectedVertexIndex--;

        EmitShapeUpdate();
        Debug.Log($"ParcelShapeEditManager: 頂点{index}を削除 (残り: {vertices.Count})");
    }

    // =====================================================================
    // ヒット判定
    // =====================================================================

    private enum HitTargetType { None, Point, Edge }

    private struct HitResult
    {
        public HitTargetType type;
        public int index;
    }

    /// <summary>
    /// スクリーン座標でヒット判定を行う。
    /// 頂点（円）を優先し、次に辺（線分）を判定する。
    /// </summary>
    private HitResult TestHit(Vector2 mouseScreen, Camera cam)
    {
        // 頂点ヒット（優先）
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

    private static float PointToSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSqr = ab.sqrMagnitude;
        if (lenSqr < 1e-6f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSqr);
        Vector2 closest = a + ab * t;
        return Vector2.Distance(p, closest);
    }

    // =====================================================================
    // ホバー状態更新
    // =====================================================================

    private void UpdateHoverState()
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

    private void UpdateOverlay()
    {
        if (overlayRenderer == null) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        overlayRenderer.BeginFrame();

        DrawEdges(cam);
        DrawVertices(cam);

        if (hoveredEdgeIndex >= 0)
        {
            DrawHoverHighlight(cam);
        }

        overlayRenderer.EndFrame();
    }

    /// <summary>
    /// ポリゴンの辺を描画する。
    /// </summary>
    private void DrawEdges(Camera cam)
    {
        Color edgeColor = new Color(0.3f, 0.6f, 1f);

        for (int i = 0; i < vertices.Count; i++)
        {
            int next = (i + 1) % vertices.Count;
            Vector3 s1 = cam.WorldToScreenPoint(vertices[i]);
            Vector3 s2 = cam.WorldToScreenPoint(vertices[next]);

            if (s1.z < 0 && s2.z < 0) continue;

            overlayRenderer.AddSolidLine(s1.x, s1.y, s2.x, s2.y, edgeColor, solidLineThickness);
        }
    }

    /// <summary>
    /// 頂点を円で描画する。
    /// 選択中の頂点は緑色、その他は青色。
    /// </summary>
    private void DrawVertices(Camera cam)
    {
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector3 screen = cam.WorldToScreenPoint(vertices[i]);
            if (screen.z < 0) continue;

            Color c;
            if (i == selectedVertexIndex)
            {
                c = new Color(0.2f, 1f, 0.4f);
            }
            else
            {
                c = new Color(0.54f, 0.71f, 0.98f);
            }

            overlayRenderer.AddCircle(screen.x, screen.y, pointCircleRadius, c);
        }
    }

    /// <summary>
    /// ホバー中の辺を黄色い太線でハイライトする。
    /// </summary>
    private void DrawHoverHighlight(Camera cam)
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
    /// 現在のポリゴンをIPC経由でUIに通知する。
    /// </summary>
    private void EmitShapeUpdate()
    {
        if (IpcClient.Instance == null) return;

        var polygon = new List<List<float>>();
        foreach (var v in vertices)
        {
            polygon.Add(new List<float> { v.x, v.z });
        }

        IpcClient.Instance.EmitEditParcelUpdateShape(editingParcelId, polygon);
    }

    // =====================================================================
    // 既存区画オブジェクトの表示制御
    // =====================================================================

    private void HideOriginalParcelObject(int parcelId)
    {
        if (parcelManager == null) return;
        GameObject obj = parcelManager.GetParcelObject(parcelId);
        if (obj != null)
        {
            obj.SetActive(false);
        }
    }

    private void ShowOriginalParcelObject(int parcelId)
    {
        if (parcelManager == null) return;
        GameObject obj = parcelManager.GetParcelObject(parcelId);
        if (obj != null)
        {
            obj.SetActive(true);
        }
    }
}
