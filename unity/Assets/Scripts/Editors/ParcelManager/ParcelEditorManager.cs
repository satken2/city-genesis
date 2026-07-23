using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

/// <summary>
/// 土地区画の選択・分割・プレビュー・API連携などエディタ操作全体を管理するクラス
/// </summary>
public class ParcelEditorManager : MonoBehaviour
{
    [Header("References")]
    public ParcelPlanRenderer parcelPlanRenderer;  // 土地区画プレビュー描画クラス
    public Camera gameCamera;                      // ゲーム用カメラ
    public ParcelManager parcelManager;            // 確定済み区画描画を担う既存マネージャ

    // 選択モード
    private bool pointSelectionMode = false;
    private bool pathSelectionMode = false;
    private bool parcelSelectionMode = false;

    // 選択されたオブジェクト情報
    private Vector3 selectedPoint;
    private bool hasSelectedPoint = false;
    private int selectedParcelId = -1;

    // 選択された区画のプレビューデータ
    private List<Vector3> previewBlockOutline = new List<Vector3>();
    private List<List<Vector3>> previewParcels = new List<List<Vector3>>();

    // empty_area 用の矩形パラメータと回転ドラッグ状態
    private float currentWidth = 10f;
    private float currentHeight = 20f;
    private float currentAngle = 0f;
    private bool isRotating = false;
    private bool justSelectedPoint = false;

    // レイキャスト用レイヤーマスク
    private int groundLayerMask;
    private int pathLayerMask;
    private int parcelLayerMask;

    void Start()
    {
        // レイヤーマスクを初期化
        groundLayerMask = LayerMask.GetMask("Ground", "Terrain");
        pathLayerMask = LayerMask.GetMask("Path");
        parcelLayerMask = LayerMask.GetMask("Parcel");

        // ParcelPlanRendererの自動検索・生成
        if (parcelPlanRenderer == null)
        {
            parcelPlanRenderer = FindAnyObjectByType<ParcelPlanRenderer>();
            if (parcelPlanRenderer == null)
            {
                Debug.Log("ParcelPlanRendererが見つかりません。このGameObjectに追加します。");
                parcelPlanRenderer = gameObject.AddComponent<ParcelPlanRenderer>();
            }
        }

        // 初期状態は地点選択モード
        ClearWorldSelectionState();
        pointSelectionMode = true;
    }

    void Update()
    {
        // いずれかの選択モードが有効な場合のみ処理
        if (!pointSelectionMode && !pathSelectionMode && !parcelSelectionMode)
            return;

        // Escキーでキャンセル（UI上でも有効）
        if (InputUtil.KeyWasPressedThisFrame(Key.Escape))
        {
            ClearWorldSelectionState();
            ClearPreview();
            
            return;
        }

        // 注：各種HandleXXXSelection関数はInputHandlerシステムから呼び出される形に修正
        // 実際の選択処理は各InputHandlerクラスで実行されるため、
        // ここでは特別な処理は不要
    }

    #region 選択モード制御

    /// <summary>
    /// すべての選択モードを無効化
    /// </summary>
    public void ClearWorldSelectionState()
    {
        pointSelectionMode = false;
        pathSelectionMode = false;
        parcelSelectionMode = false;
        hasSelectedPoint = false;
        isRotating = false;
        justSelectedPoint = false;
        currentAngle = 0f;

        // 視覚フィードバックをクリア
        ClearPreview();
    }

    /// <summary>
    /// 地点選択モードを有効化
    /// </summary>
    public void EnablePointSelectionMode()
    {
        ClearWorldSelectionState();
        pointSelectionMode = true;
        Debug.Log($"地点選択モードを有効化しました。pointSelectionMode = {pointSelectionMode}");
    }

    /// <summary>
    /// 道路選択モードを有効化
    /// </summary>
    public void EnablePathSelectionMode()
    {
        ClearWorldSelectionState();
        pathSelectionMode = true;
        Debug.Log("道路選択モードを有効化しました");
    }

    /// <summary>
    /// 土地区画選択モードを有効化
    /// </summary>
    public void EnableParcelSelectionMode()
    {
        ClearWorldSelectionState();
        parcelSelectionMode = true;
        Debug.Log("土地区画選択モードを有効化しました");
    }

    /// <summary>
    /// 地点選択モードかどうかを返す
    /// </summary>
    public bool IsPointSelectionMode()
    {
        return pointSelectionMode;
    }

    /// <summary>
    /// 道路選択モードかどうかを返す
    /// </summary>
    public bool IsPathSelectionMode()
    {
        return pathSelectionMode;
    }

    /// <summary>
    /// 土地区画選択モードかどうかを返す
    /// </summary>
    public bool IsParcelSelectionMode()
    {
        return parcelSelectionMode;
    }

    /// <summary>
    /// 地点が選択されたときの処理。
    /// 既に地点が選ばれていて、かつ近距離（1m未満）の場合はドラッグ開始とみなし、
    /// 地点の更新を行わない。
    /// </summary>
    public void OnPointSelected(Vector3 point)
    {
        if (hasSelectedPoint)
        {
            float dist = Vector2.Distance(
                new Vector2(selectedPoint.x, selectedPoint.z),
                new Vector2(point.x, point.z)
            );
            if (dist < 1.0f)
            {
                return;
            }
        }

        selectedPoint = point;
        hasSelectedPoint = true;
        justSelectedPoint = true;
        Debug.Log($"地点が選択されました: {point}");

        // IPC: 土地区画作成モード用の地点選択イベントをUIに通知
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectPoint(point);
        }

        // 選択モードを無効化（次のクリックでドラッグ開始判定に使う）
        pointSelectionMode = false;
    }

    /// <summary>
    /// 道路が選択されたときの処理
    /// </summary>
    public void OnPathSelected(int pathId, Vector3[] waypoints)
    {
        Debug.Log($"道路が選択されました: Path_{pathId}");

        // IPC: 土地区画作成モード用のパス選択イベントをUIに通知
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectPath(pathId);
        }

        // 選択モードを無効化
        pathSelectionMode = false;
    }

    /// <summary>
    /// 土地区画が選択されたときの処理
    /// </summary>
    public void OnParcelSelected(int parcelId)
    {
        selectedParcelId = parcelId;
        Debug.Log($"土地区画が選択されました: Parcel_{parcelId}");

        // IPC: 土地区画作成モード用の区画選択イベントをUIに通知
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectParcel(parcelId);
        }

        // 選択モードを無効化
        parcelSelectionMode = false;
    }

    /// <summary>
    /// 基準点が既に選択済みかどうかを返す
    /// </summary>
    public bool HasSelectedPoint()
    {
        return hasSelectedPoint;
    }

    /// <summary>
    /// 左クリックドラッグで角度変更を開始する
    /// </summary>
    public void StartRotationDrag()
    {
        if (!hasSelectedPoint) return;
        if (justSelectedPoint)
        {
            // 同一フレーム内の地点選択クリックとドラッグ開始を区別する。
            // 地点を選んだ直後のクリックでは角度変更を開始せず、次のクリックから有効にする。
            justSelectedPoint = false;
            return;
        }
        isRotating = true;
    }

    /// <summary>
    /// ドラッグ中にマウス位置と基準点の角度を計算し、仮の矩形プレビューを描画する。
    /// リアルタイムフィードバックのため、バックエンド通信を待たずにローカルで計算する。
    /// </summary>
    public void UpdateRotationPreview(Vector3 currentPosition)
    {
        if (!isRotating) return;

        Vector3 diff = currentPosition - selectedPoint;
        currentAngle = Mathf.Atan2(diff.x, diff.z) * Mathf.Rad2Deg;

        // 仮の矩形ポリゴンを計算してプレビュー描画（デフォルトサイズで概形を示す）
        List<Vector3> polygon = CalculateRectanglePolygon(selectedPoint, currentWidth, currentHeight, currentAngle);
        previewBlockOutline = null;
        previewParcels = new List<List<Vector3>> { polygon };

        if (parcelPlanRenderer != null)
        {
            parcelPlanRenderer.RenderBlockPreview(null, previewParcels);
        }
    }

    /// <summary>
    /// 角度変更ドラッグを終了し、確定した角度をUIに通知する。
    /// UI側は角度を受信後、バックエンドから正式なプレビューを取得してUnityに送信する。
    /// </summary>
    public void EndRotationDrag()
    {
        if (!isRotating) return;
        isRotating = false;

        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelUpdateRotation(currentAngle);
        }
    }

    /// <summary>
    /// 中心点、縦横、角度から回転矩形の頂点リストを計算する。
    /// 角度0°はZ+方向（前方）とし、正の値で反時計回り（X+方向が+90°）となる。
    /// </summary>
    private List<Vector3> CalculateRectanglePolygon(Vector3 center, float width, float height, float angleDeg)
    {
        float halfW = width * 0.5f;
        float halfH = height * 0.5f;
        float rad = angleDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);

        Vector3[] localCorners = new Vector3[]
        {
            new Vector3(-halfW, 0f, -halfH),
            new Vector3( halfW, 0f, -halfH),
            new Vector3( halfW, 0f,  halfH),
            new Vector3(-halfW, 0f,  halfH),
        };

        List<Vector3> worldCorners = new List<Vector3>();
        foreach (var local in localCorners)
        {
            float x = local.x * cos - local.z * sin;
            float z = local.x * sin + local.z * cos;
            worldCorners.Add(new Vector3(center.x + x, center.y, center.z + z));
        }

        return worldCorners;
    }

    #endregion

    #region 地点選択の処理

    /// <summary>
    /// 空の土地区画のプレビューを描画
    /// empty_area の場合、元区画と新規区画が同じなので、
    /// 新規区画のみ（青色）で描画するよう blockPolygon は null にする。
    /// </summary>
    public void RenderEmptyParcelPreview(float[][] polygonPoints)
    {
        ClearPreview();

        // 頂点座標をVector3に変換
        List<Vector3> parcelPolygon = ConvertFloatArrayToVector3List(polygonPoints);
        
        // 空き地作成の場合、元区画と新規区画が同じなので
        // blockPolygon を null にして、新規区画のみ青色で描画する
        previewBlockOutline = null;
        
        // 土地区画として描画
        List<List<Vector3>> parcels = new List<List<Vector3>>();
        parcels.Add(parcelPolygon);
        previewParcels = parcels;

        // レンダラーで描画
        if (parcelPlanRenderer != null)
        {
            Debug.Log($"ParcelEditorManager: RenderBlockPreviewを呼び出します (Parcels Count: {previewParcels.Count})");
            parcelPlanRenderer.RenderBlockPreview(null, previewParcels);
        }
        else
        {
            Debug.LogError("ParcelEditorManager: parcelPlanRendererがnullです");
        }
    }

    #endregion

    #region 道路選択の処理

    #endregion

    #region 土地区画選択と分割の処理

    #endregion

    #region 共通処理

    /// <summary>
    /// プレビュー表示をクリア
    /// </summary>
    public void ClearPreview()
    {
        if (parcelPlanRenderer != null)
        {
            parcelPlanRenderer.ClearPreview();
        }
        if (previewBlockOutline != null)
            previewBlockOutline.Clear();
        else
            previewBlockOutline = new List<Vector3>();
        if (previewParcels != null)
            previewParcels.Clear();
        else
            previewParcels = new List<List<Vector3>>();
    }

    /// <summary>
    /// サーバから返却された [ [x,z], [x,z], ... ] を Vector3 の List に変換するヘルパー
    /// （y は地形から取得）
    /// </summary>
    private List<Vector3> ConvertFloatArrayToVector3List(float[][] coords)
    {
        List<Vector3> list = new List<Vector3>();
        foreach (var c in coords)
        {
            float x = c[0];
            float z = c[1];
            
            // 地形の高さを取得
            float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
            
            list.Add(new Vector3(x, y, z));
        }
        return list;
    }

    #endregion
}
