
using UnityEngine;
using UnityEngine.InputSystem;
using System;
using System.Collections.Generic;
using UnityEngine.EventSystems;

/// <summary>
/// オブジェクトタイプを示す定数
/// </summary>
public static class InputObjectType
{
    public const int BUILDING = 1;
    public const int PARCEL = 2;
    public const int PATH = 3;
    public const int NODE = 4;
    public const int TERRAIN = 5;
    public const int ZONE = 6;
    public const int PROP = 7; // コライダーを持たずレイキャストの分類には出ない。ブルドーザの削除済み集合のキーにだけ使う

    /// <summary>ログ・デバッグ表示用の日本語名。</summary>
    public static string GetName(int objectType)
    {
        switch (objectType)
        {
            case BUILDING: return "建物";
            case PARCEL: return "土地区画";
            case PATH: return "道路";
            case NODE: return "ノード";
            case TERRAIN: return "地面";
            case ZONE: return "地区";
            case PROP: return "プロップ";
            default: return "不明";
        }
    }
}

/// <summary>
/// 選択モードを示す定数
/// </summary>
public static class InputMode
{
    public const int MODE_NONE = 0;
    public const int MODE_NORMAL = 1;
    public const int MODE_ROAD_CONSTRUCTION = 2;
    public const int MODE_BUILDING_CONSTRUCTION = 3;
    public const int MODE_PARCEL_CREATION_POINT = 4;
    public const int MODE_PARCEL_CREATION_PATH = 5;
    public const int MODE_PARCEL_CREATION_SPLIT = 6;
    public const int MODE_EDIT = 7;
    public const int MODE_PARCEL_SHAPE_EDIT = 10;
    public const int MODE_SET_GRID = 11;
    public const int MODE_EDIT_RESOURCE = 12;
    public const int MODE_EDIT_TRANSIT = 13;
    public const int MODE_EDIT_PROP = 14;
    // 施設の接続端子ノード(インフラ接続点 / 走行網接続点)のドラッグ移動編集モード。
    // 停留所配置(MODE_EDIT_TRANSIT)と同じく地形ドラッグとして扱い、
    // 実体の掴み判定・移動は ConnectorEditManager がスクリーン座標のマーカー当たり判定で行う。
    public const int MODE_EDIT_CONNECTOR = 15;
    // ブルドーザ(オブジェクト削除)モード。空き地区画・計画中の建物/道路を
    // 左クリックで削除する(ドラッグでは削除しない)。実処理は RemovalManager が担う。
    public const int MODE_REMOVE = 17;
    // パス結合(merge)の相手選択モード。詳細パネルで開いているパスと共有ノードで繋がる
    // 候補パスだけをクリックできる。候補判定と選択通知は PathTopologyEditManager が担う。
    public const int MODE_PATH_MERGE = 18;
    // パス分割(split)の分割点選択モード。対象パスの中心線へ射影した点を分割点として選ぶ。
    // 射影・マーカー描画・選択通知は PathTopologyEditManager が担う。
    public const int MODE_PATH_SPLIT = 19;
    // 既存建物コピー建設の「コピー元建物」選択モード。建物だけをクリック可能にし、
    // クリックされた建物の variant / floors / visual_variant を UI へ通知する(建設はしない)。
    // 通知は BuildingInputHandler が building_construction:copy_source として送る。
    public const int MODE_PICK_BUILDING = 20;
    // 道路(名前付き道路)の区間編集モード。街路・高速道路の区間のクリックを UI へ通知するだけで、
    // 所属の付け外しは UI が API で行う。実処理は RoadMemberEditManager。
    public const int MODE_EDIT_ROAD_MEMBERS = 22;
    // 造成範囲の作図モード。資源エリアの作図と同じ地形クリック・頂点ドラッグで、実処理は GradingAreaEditManager。
    public const int MODE_EDIT_GRADING = 23;
}

/// <summary>
/// モードプリセットを示す定数
/// </summary>
public static class InputModePreset
{
    public const int ALL_NONE = 0;
    public const int ALL_NORMAL = 1;
    public const int ROAD_CONSTRUCTION = 2;
    public const int BUILDING_CONSTRUCTION = 3;
    public const int PARCEL_CREATION_POINT = 4;
    public const int PARCEL_CREATION_PATH = 5;
    public const int PARCEL_CREATION_SPLIT = 6;
    public const int PARCEL_SHAPE_EDIT = 9;
    public const int SET_GRID = 10;
    public const int EDIT_RESOURCE = 11;
    public const int TRANSIT_CONSTRUCTION = 12;
    public const int PROP_PLACEMENT = 13;
    public const int CONNECTOR_EDIT = 14;
    // ブルドーザ(オブジェクト削除)モードのプリセット。建物・区画・道路・地形を
    // すべて MODE_REMOVE にして削除対象として拾えるようにする。
    public const int REMOVE_OBJECTS = 16;
    // パス結合の相手選択プリセット。パスだけをクリック可能にする。
    public const int PATH_MERGE = 17;
    // パス分割の分割点選択プリセット。パスと地形をクリック可能にする
    // (パスから少し外れた地点でも垂線の足で分割点を選べるようにするため)。
    public const int PATH_SPLIT = 18;
    // 既存建物コピー建設のコピー元選択プリセット。建物だけをクリック可能にする。
    public const int PICK_BUILDING = 19;
    // 道路の区間編集プリセット。パスだけをクリック可能にする(パス結合の相手選択と同型)。
    public const int ROAD_MEMBER_EDIT = 21;
    // 造成範囲の作図プリセット。地形だけをクリック可能にする(資源エリアの作図と同型)。
    public const int EDIT_GRADING = 22;
}

/// <summary>
/// グローバルなクリック・ホバーイベント管理システム
/// </summary>
public class InputHandler : MonoBehaviour
{
    public static InputHandler Instance { get; private set; }

    [Header("Ray Settings")]
    public float rayDistance = 5000f;
    public LayerMask rayLayerMask = -1;

    [Header("Terrain Settings")]
    public LayerMask terrainLayerMask = 1;

    // マウス位置（吸着対応）
    public float posX { get; private set; }
    public float posZ { get; private set; }

    // オブジェクトタイプごとの現在の選択モード
    private Dictionary<int, int> objectTypeModes = new Dictionary<int, int>
    {
        { InputObjectType.BUILDING, InputMode.MODE_NORMAL },
        { InputObjectType.PARCEL, InputMode.MODE_NORMAL },
        { InputObjectType.PATH, InputMode.MODE_NORMAL },
        { InputObjectType.NODE, InputMode.MODE_NORMAL },
        { InputObjectType.TERRAIN, InputMode.MODE_NORMAL },
        { InputObjectType.ZONE, InputMode.MODE_NORMAL }
    };

    /// <summary>独自のキー処理を持つ編集モードと、撮影・乗車の操作を同時に有効にしない。</summary>
    public bool IsNormalMode
    {
        get
        {
            foreach (int mode in objectTypeModes.Values)
                if (mode != InputMode.MODE_NORMAL) return false;
            return true;
        }
    }

    // イベントコールバック
    private Action<int, Vector3> clickEventCallback;
    private Action<int, Vector3, GameObject> hoverEventCallback;
    private Action<int, Vector3> rightClickEventCallback;

    // 現在ホバー中のオブジェクト
    private GameObject currentHoveredObject;
    private IInputHandler currentHoveredHandler;
    private int currentHoveredObjectType = -1;

    // ドラッグ関連
    private bool isDragging = false;
    private Vector3 dragStartPosition;
    private GameObject draggedObject;
    private int draggedObjectType = -1;

    // クリック候補（マウスダウン時に記録し、マウスアップ時に移動距離が閾値未満ならクリックとして発火する）
    // 対象は RaycastHit ではなく GameObject + ヒット点で保持する。
    // 起伏地形で区画コライダーが地形に埋まった場合、レイキャスト結果（地形）とは別の
    // オブジェクト（区画）へディスパッチ先を差し替える必要があるため。
    private bool hasPendingClick = false;
    private GameObject pendingClickObject;
    private Vector3 pendingClickPoint;
    private int pendingClickObjectType = -1;
    private Vector3 pendingClickScreenPos;
    private bool hasPendingTerrainClick = false;
    private Vector3 pendingTerrainClickScreenPos;
    private const float CLICK_DRAG_THRESHOLD = 5f; // ピクセル

    // 右クリック候補（押下時に記録し、リリース時にカメラ回転ドラッグが確定しなかった
    // ＝閾値未満の移動だった場合のみ右クリックとして発火する）。
    // カメラ回転は閾値超で開始する保留方式のため、移動量で「右クリック」と「右ドラッグ回転」を区別できる。
    private bool hasPendingRightClick = false;
    private GameObject pendingRightClickObject;
    private Vector3 pendingRightClickPoint;
    private int pendingRightClickObjectType = -1;
    private bool hasPendingTerrainRightClick = false;
    private Vector3 pendingRightClickScreenPos;

    private Ray lastRay;
    private bool lastUndergroundView;
    private PathManager cachedPathManager;

    // キャッシュされたマネージャー参照
    private RoadPlanManager cachedRoadPlanManager;
    private TerrainManager cachedTerrainManager;
    private ParcelEditorManager cachedParcelEditorManager;
    private ParcelShapeEditManager cachedParcelShapeEditManager;
    private ResourceAreaEditManager cachedResourceAreaEditManager;
    private ParcelManager cachedParcelManager;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        cachedRoadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        cachedTerrainManager = FindAnyObjectByType<TerrainManager>();
        cachedParcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        cachedParcelShapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        cachedResourceAreaEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
        cachedParcelManager = FindAnyObjectByType<ParcelManager>();

        // 海面(Sea)オブジェクトにTerrainInputHandlerを付与する。
        // SeaはTerrainManagerが管理しない板ポリゴンでTerrainコンポーネントを持たないため、
        // 既定では GetObjectType で TERRAIN 扱いされずクリックが無反応になる。
        // SeaObjectタグを付与した上で TerrainInputHandler をアタッチすることで、
        // 地形タイルと同等のクリック処理(区画作成の点配置等)を海面上でも有効にする。
        // 既に TerrainInputHandler が付与済み(シーン上で事前配置等)なら重複付与しない。
        GameObject seaObject = GameObject.FindWithTag("SeaObject");
        if (seaObject != null && seaObject.GetComponent<TerrainInputHandler>() == null)
        {
            seaObject.AddComponent<TerrainInputHandler>();
        }
    }

    private void Update()
    {
        if (!ApiClient.BackendConnected && isDragging) EndDrag();
        // フライ・撮影モード中はマウス入力（クリック・ホバー・ドラッグ）を一切受け付けない。
        // カメラを自由飛行させている間は3Dオブジェクトの選択やホバー（ツールチップ含む）を
        // 完全に無効化するため、他のどの判定よりも先に即抜けする。
        if (!ApiClient.BackendConnected || TutorialRuntime.BlocksWorldInput || DriveMode.IsOpen || (CameraController.Instance != null && CameraController.Instance.BlocksWorldInput))
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            TransitSelection.Cancel();
            CitizenPickSelection.Cancel();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            return;
        }

        // ラジアルメニュー表示中も3Dオブジェクトへのマウス入力を一切受け付けない。
        // フライモードと同じ非破壊ゲート方式で、メニューを閉じれば元の入力状態がそのまま復帰する
        // (メニューのCanvasにはGraphicRaycasterが無いため、EventSystemのUI判定では止まらない)。
        if (RadialMenuController.IsOpen)
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            TransitSelection.Cancel();
            CitizenPickSelection.Cancel();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            return;
        }

        // UI上をクリックした場合は処理を中断
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            // UIクリック時はペンディング中のクリックを破棄する
            TransitSelection.Cancel();
            CitizenPickSelection.Cancel();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            return;
        }

        // カメラドラッグ確定中はオブジェクト選択・ドラッグを一切受け付けない
        // IsPendingDrag は除外し、InputHandler 自身がマウスダウンとドラッグを区別する。
        // これにより CameraController のドラッグ待機中でも InputHandler はクリック候補を記録できる。
        bool isCameraDragging = CameraController.Instance != null &&
            CameraController.Instance.IsMouseDragging;
        if (isCameraDragging)
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            // カメラドラッグ確定時はペンディング中のクリックを破棄する
            TransitSelection.Cancel();
            CitizenPickSelection.Cancel();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            return;
        }

        // メインカメラが存在しない場合は処理を中断
        // ローディング画面などでカメラを意図的に非表示にしている際に、
        // NullReferenceExceptionが発生するのを防ぐため
        if (Camera.main == null)
        {
            return;
        }

        if (GetModeForObjectType(InputObjectType.PATH) == InputMode.MODE_NORMAL
            && GetModeForObjectType(InputObjectType.TERRAIN) == InputMode.MODE_NORMAL)
        {
            if (CitizenPickSelection.ProcessPointer(Camera.main) || TransitSelection.ProcessPointer(Camera.main))
            {
                ClearHover(); TooltipManager.Instance?.Hide();
                hasPendingClick = false; hasPendingTerrainClick = false;
                return;
            }
        }
        else { CitizenPickSelection.Cancel(); TransitSelection.Cancel(); }

        // カメラからマウス位置へRayを飛ばす
        Vector3 mousePosition = InputUtil.MousePosition;
        Ray ray = Camera.main.ScreenPointToRay(mousePosition);

        // Rayの変化チェック & 入力状態チェック
        // クリックやドラッグなどの入力トリガーがある場合は、静止していても処理を継続する必要がある。
        bool hasInputEvent = InputUtil.LeftMouseWasPressedThisFrame ||
                             InputUtil.RightMouseWasPressedThisFrame ||
                             InputUtil.LeftMouseWasReleasedThisFrame ||
                             InputUtil.RightMouseWasReleasedThisFrame ||
                             isDragging;

        bool rayChanged = ray.origin != lastRay.origin || ray.direction != lastRay.direction || lastUndergroundView != UndergroundView.IsActive;

        if (!rayChanged && !hasInputEvent)
        {
            // マウスもカメラも動いておらず、入力状態変化もないため、重い判定をすべてスキップする。
            return;
        }

        lastRay = ray;
        lastUndergroundView = UndergroundView.IsActive;

        // まずは地形との交点を計算してposX, posZを更新
        UpdateTerrainPosition(ray);

        // オブジェクトとの当たり判定
        // MODE_NONEのオブジェクトを貫通するためにRaycastAllを使用
        RaycastHit[] hits = Physics.RaycastAll(ray, rayDistance, rayLayerMask);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        GameObject validHitObject = null;
        Vector3 validHitPoint = Vector3.zero;
        int validHitObjectType = -1;

        if (TryGetObjectHit(hits, out var selectedHit))
        {
            validHitObject = selectedHit.collider.gameObject;
            validHitPoint = selectedHit.point;
            validHitObjectType = GetObjectType(validHitObject);
        }

        // 地形ヒットの区画への差し替え:
        // 区画のMeshCollider上面はポリゴンの角の頂点でしか地形高さをサンプルしないため、
        // 起伏地形では頂点間の地形の盛り上がりにコライダーが埋まり、レイキャストでは
        // 手前の地形が必ず勝って区画を選択できない。区画は概念的に「地面上の領域」なので、
        // 最初の有効ヒットが地形の場合はヒット点のXZがどの区画ポリゴンに含まれるかを
        // 2D内外判定で解決し、選択可能な区画に含まれる場合だけディスパッチ先を差し替える。
        // 区画コライダーが地形より上に出ている場合は従来どおりレイキャストが先に区画を拾う。
        if (validHitObjectType == InputObjectType.TERRAIN)
        {
            GameObject parcelObj = GetSelectableParcelAtPoint(validHitPoint);
            if (parcelObj != null)
            {
                validHitObject = parcelObj;
                validHitObjectType = InputObjectType.PARCEL;
            }
        }

        // ブルドーザ中は、コライダーを持たない地形プロップのホバー枠を RemovalManager に更新させる
        // (ヒット物より手前のプロップだけが対象。何もヒットしなければレイの最大距離まで探す)。
        if (GetModeForObjectType(InputObjectType.TERRAIN) == InputMode.MODE_REMOVE && RemovalManager.Instance != null)
        {
            RemovalManager.Instance.UpdatePropHover(ray,
                UndergroundView.IsActive ? 0f :
                validHitObject != null ? Vector3.Distance(ray.origin, validHitPoint) : rayDistance);
        }

        if (validHitObject != null)
        {
            HandleObjectHit(validHitObject, validHitPoint, validHitObjectType);

            // 左クリックダウン処理：クリック候補を記録する（即座に発火しない）
            if (InputUtil.LeftMouseWasPressedThisFrame)
            {
                hasPendingClick = true;
                pendingClickObject = validHitObject;
                pendingClickPoint = validHitPoint;
                pendingClickObjectType = validHitObjectType;
                pendingClickScreenPos = InputUtil.MousePosition;
            }

            // 右クリック候補を記録する（即座に発火しない）。
            // リリース時に移動量が閾値未満（＝カメラ回転ドラッグに化けていない）の場合のみ発火する。
            if (InputUtil.RightMouseWasPressedThisFrame)
            {
                hasPendingRightClick = true;
                hasPendingTerrainRightClick = false;
                pendingRightClickObject = validHitObject;
                pendingRightClickPoint = validHitPoint;
                pendingRightClickObjectType = validHitObjectType;
                pendingRightClickScreenPos = InputUtil.MousePosition;
            }
        }
        else
        {
            // 何もヒットしなかった場合
            ClearHover();

            // 左クリックダウン処理：地形クリック候補を記録する
            if (InputUtil.LeftMouseWasPressedThisFrame)
            {
                hasPendingTerrainClick = true;
                pendingTerrainClickScreenPos = InputUtil.MousePosition;
            }

            // 右クリック候補を記録する（地形）。リリース時に移動量が閾値未満の場合のみ発火する。
            if (InputUtil.RightMouseWasPressedThisFrame)
            {
                hasPendingTerrainRightClick = true;
                hasPendingRightClick = false;
                pendingRightClickScreenPos = InputUtil.MousePosition;
            }
        }

        // マウス移動中：クリック候補が閾値を超えたら破棄し、ドラッグとして確定する
        if (hasPendingClick)
        {
            if (CameraController.Instance != null && CameraController.Instance.IsMouseDragging)
            {
                hasPendingClick = false;
            }
            else if ((InputUtil.MousePosition - pendingClickScreenPos).magnitude >= CLICK_DRAG_THRESHOLD)
            {
                hasPendingClick = false;
                StartDrag(pendingClickObject, pendingClickPoint, pendingClickObjectType);
            }
        }
        if (hasPendingTerrainClick)
        {
            if (CameraController.Instance != null && CameraController.Instance.IsMouseDragging)
            {
                hasPendingTerrainClick = false;
            }
            else if ((InputUtil.MousePosition - pendingTerrainClickScreenPos).magnitude >= CLICK_DRAG_THRESHOLD)
            {
                hasPendingTerrainClick = false;
                StartTerrainDrag();
            }
        }

        // ドラッグ更新
        if (isDragging && InputUtil.LeftMouseIsPressed)
        {
            UpdateDrag();
        }

        // マウスアップ処理
        if (InputUtil.LeftMouseWasReleasedThisFrame)
        {
            // クリック候補が残っていたらマウスアップ時にクリックとして発火する。
            // ブルドーザ中はコライダーを持たない地形プロップを先に判定し、ヒット物より手前にあれば
            // プロップの削除だけを行う(背後の建物・区画へは配送しない)。
            if (hasPendingClick)
            {
                float hitDistance = Vector3.Distance(ray.origin, pendingClickPoint);
                if (IsUndergroundSelection(pendingClickObject, pendingClickPoint)
                    || !TryRemovePropAtScreenPoint(pendingClickScreenPos, hitDistance))
                {
                    HandleObjectClick(pendingClickObject, pendingClickPoint, pendingClickObjectType);
                }
                hasPendingClick = false;
            }
            if (hasPendingTerrainClick)
            {
                if (!TryRemovePropAtScreenPoint(pendingTerrainClickScreenPos, rayDistance))
                {
                    HandleTerrainClick();
                }
                hasPendingTerrainClick = false;
            }

            // ドラッグ終了
            if (isDragging)
            {
                EndDrag();
            }
        }

        // 右マウスアップ処理：押下からの移動量が閾値未満（＝カメラ回転ドラッグに化けていない）の
        // 場合のみ、記録しておいた右クリック候補を発火する。
        // これにより「右クリック（その場）＝確定」「右ドラッグ＝カメラ回転」を両立させる。
        if (InputUtil.RightMouseWasReleasedThisFrame)
        {
            float rightMoveDist = (InputUtil.MousePosition - pendingRightClickScreenPos).magnitude;
            if (rightMoveDist < CLICK_DRAG_THRESHOLD)
            {
                if (hasPendingRightClick)
                {
                    HandleObjectRightClick(pendingRightClickObject, pendingRightClickPoint, pendingRightClickObjectType);
                }
                else if (hasPendingTerrainRightClick)
                {
                    HandleTerrainRightClick();
                }
            }
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
        }
    }

    private void UpdateTerrainPosition(Ray ray)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, rayDistance, terrainLayerMask);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        bool foundTerrain = false;
        foreach (var hit in hits)
        {
            GameObject hitObject = hit.collider.gameObject;
            if (hitObject.GetComponent<Terrain>() != null || IsTerrainSurfaceObject(hitObject))
            {
                posX = hit.point.x;
                posZ = hit.point.z;

                // 地形のホバー処理を呼び出し
                HandleTerrainHover(hit);
                foundTerrain = true;
                break;
            }
        }

        if (!foundTerrain)
        {
            // 地形がヒットしなかった場合はy=0平面との交点を計算
            float distance = -ray.origin.y / ray.direction.y;
            if (distance > 0)
            {
                Vector3 point = ray.origin + ray.direction * distance;
                posX = point.x;
                posZ = point.z;
            }
        }
    }

    private void HandleObjectHit(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        // ホバー処理
        if (currentHoveredObject != hitObject)
        {
            if (currentHoveredHandler != null)
            {
                currentHoveredHandler.onHoverEnd(GetModeForObjectType(currentHoveredObjectType));
            }

            // 新しいオブジェクトのホバー開始
            currentHoveredObject = hitObject;
            currentHoveredObjectType = objectType;
            currentHoveredHandler = hitObject.GetComponent<IInputHandler>();
            if (currentHoveredHandler != null)
            {
                currentHoveredHandler.onHoverStart(GetModeForObjectType(objectType));
            }
        }

        // 継続的なホバー処理を呼び出し
        if (currentHoveredHandler != null)
        {
            currentHoveredHandler.onHover(GetModeForObjectType(objectType));
        }

        // ホバーイベントコールバック
        hoverEventCallback?.Invoke(objectType, hitPoint, hitObject);
    }

    private void HandleTerrainHover(RaycastHit hit)
    {
        GameObject hitObject = hit.collider.gameObject;

        // 地形の場合は継続的にホバー処理を呼び出し
        if (hitObject.GetComponent<Terrain>() != null || IsTerrainSurfaceObject(hitObject))
        {
            var terrainHandler = hitObject.GetComponent<IInputHandler>();
            terrainHandler?.onHover(GetModeForObjectType(InputObjectType.TERRAIN));
        }
    }

    private void HandleObjectClick(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        // マウスダウンからマウスアップまでの間にセルリロード等で対象が破棄されている可能性がある
        if (!CanSelectObject(hitObject, hitPoint, objectType)) return;

        int currentMode = GetModeForObjectType(objectType);
        var handler = hitObject.GetComponent<IInputHandler>();
        handler?.onClick(currentMode);

        // クリックイベントコールバック
        clickEventCallback?.Invoke(objectType, hitPoint);
    }

    /// <summary>
    /// ブルドーザ中に限り、押下時の画面位置のレイで地形プロップの削除を試みる。
    /// maxDistance はそのレイでヒットした建物・区画・地形までの距離(手前判定)。
    /// プロップを処理した(削除発行・確認要求・処理済み)場合は true を返し、通常のクリック配送を抑止する。
    /// </summary>
    private bool TryRemovePropAtScreenPoint(Vector2 screenPos, float maxDistance)
    {
        if (UndergroundView.IsActive) return false; // 地下表示では手前の地上プロップに地下パスの選択を奪わせない
        if (GetModeForObjectType(InputObjectType.TERRAIN) != InputMode.MODE_REMOVE) return false;
        if (RemovalManager.Instance == null || Camera.main == null) return false;
        return RemovalManager.Instance.TryRemovePropAt(Camera.main.ScreenPointToRay(screenPos), maxDistance);
    }

    private void HandleTerrainClick()
    {
        Vector3 terrainPoint = new Vector3(posX, 0, posZ);

        // TerrainManagerが管理するterrainタイルからTerrainInputHandlerを取得
        TerrainInputHandler terrainHandler = GetTerrainInputHandlerAtPosition(terrainPoint);
        if (terrainHandler != null)
        {
            int currentMode = GetModeForObjectType(InputObjectType.TERRAIN);
            terrainHandler.onClick(currentMode);
        }

        clickEventCallback?.Invoke(InputObjectType.TERRAIN, terrainPoint);
    }

    private void HandleObjectRightClick(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        // マウスダウンからマウスアップまでの間にセルリロード等で対象が破棄されている可能性がある
        if (!CanSelectObject(hitObject, hitPoint, objectType)) return;

        int currentMode = GetModeForObjectType(objectType);
        var handler = hitObject.GetComponent<IInputHandler>();
        handler?.onRightClick(currentMode);

        // 右クリックイベントコールバック
        rightClickEventCallback?.Invoke(objectType, hitPoint);
    }

    private void HandleTerrainRightClick()
    {
        Vector3 terrainPoint = new Vector3(posX, 0, posZ);

        // TerrainManagerが管理するterrainタイルからTerrainInputHandlerを取得
        TerrainInputHandler terrainHandler = GetTerrainInputHandlerAtPosition(terrainPoint);
        if (terrainHandler != null)
        {
            int currentMode = GetModeForObjectType(InputObjectType.TERRAIN);
            terrainHandler.onRightClick(currentMode);
        }

        rightClickEventCallback?.Invoke(InputObjectType.TERRAIN, terrainPoint);
    }

    private void ClearHover()
    {
        if (currentHoveredHandler != null)
        {
            currentHoveredHandler.onHoverEnd(GetModeForObjectType(currentHoveredObjectType));
        }

        // パス以外のホバーが解除された場合、地区ツールチップも消す
        if (currentHoveredObjectType != InputObjectType.PATH)
        {
            TooltipManager.Instance?.Hide();
        }

        currentHoveredObject = null;
        currentHoveredHandler = null;
        currentHoveredObjectType = -1;
    }

    /// <summary>
    /// Tagベースでオブジェクトタイプを判定（文字列解析より堅牢）
    /// </summary>
    private int GetObjectType(GameObject obj)
    {
        if (obj.CompareTag("BuildingObject")) return InputObjectType.BUILDING;
        if (obj.CompareTag("ParcelObject")) return InputObjectType.PARCEL;
        if (obj.CompareTag("PathObject")) return InputObjectType.PATH;
        if (obj.CompareTag("PathNode")) return InputObjectType.NODE;
        if (obj.CompareTag("ZoneObject")) return InputObjectType.ZONE;
        if (obj.CompareTag("SeaObject")) return InputObjectType.TERRAIN;
        if (obj.GetComponent<Terrain>() != null) return InputObjectType.TERRAIN;
        if (IsTerrainSurfaceObject(obj)) return InputObjectType.TERRAIN;

        return -1; // 不明なタイプ
    }

    /// <summary>地形越しの地下パスを優先し、それ以外は選択可能な最初のヒットを返す。</summary>
    private bool TryGetObjectHit(RaycastHit[] hits, out RaycastHit selected)
    {
        if (TryGetUndergroundHit(hits, out selected)) return true;
        foreach (var hit in hits)
        {
            var obj = hit.collider.gameObject;
            if (!CanSelectObject(obj, hit.point, GetObjectType(obj))) continue;
            selected = hit;
            return true;
        }
        return false;
    }

    private bool CanSelectObject(GameObject obj, Vector3 point, int objectType)
    {
        if (obj == null || GetModeForObjectType(objectType) == InputMode.MODE_NONE) return false;
        if (!UndergroundView.IsActive) return true;
        // 地上モデルは位置の案内として残すが、ホバー・クリック・保留中のドラッグには使わない。
        if (objectType == InputObjectType.BUILDING || objectType == InputObjectType.PARCEL) return false;
        return objectType != InputObjectType.PATH || IsUndergroundSelection(obj, point);
    }

    private GameObject GetSelectableParcelAtPoint(Vector3 point)
    {
        if (GetModeForObjectType(InputObjectType.PARCEL) == InputMode.MODE_NONE || cachedParcelManager == null) return null;
        int parcelId = cachedParcelManager.GetParcelIdAtPoint(point);
        if (parcelId < 0) return null;
        GameObject parcel = cachedParcelManager.GetParcelObject(parcelId);
        // 地形の内外判定から拾った区画にも、コライダーのヒットと同じ選択制限を適用する。
        return parcel != null && parcel.activeInHierarchy && CanSelectObject(parcel, point, InputObjectType.PARCEL) ? parcel : null;
    }

    private bool TryGetUndergroundHit(RaycastHit[] hits, out RaycastHit selected)
    {
        selected = default;
        if (!UndergroundView.IsActive || GetModeForObjectType(InputObjectType.PATH) == InputMode.MODE_NONE) return false;
        // 地上の物体は位置の案内として半透明に残る。敷設などPATHが無効なモードは通常の地形レイを使う。
        foreach (var hit in hits)
        {
            if (!IsUndergroundSelection(hit.collider.gameObject, hit.point)) continue;
            if (selected.collider == null || hit.distance < selected.distance) selected = hit;
        }
        return selected.collider != null;
    }

    private bool IsUndergroundSelection(GameObject obj, Vector3 point)
    {
        if (!UndergroundView.IsActive || obj == null) return false;
        var handler = obj.GetComponent<PathInputHandler>();
        if (handler == null) return false;
        if (cachedPathManager == null) cachedPathManager = FindAnyObjectByType<PathManager>();
        var path = cachedPathManager?.GetPathData(handler.pathId);
        return path != null && TunnelSectionUtil.IsTunnelPoint(point, TunnelSectionUtil.GetClearanceHeight(path.path_variant));
    }

    /// <summary>
    /// Terrain コンポーネントを持たないが地形タイルと同じに扱う面か。
    /// マップの外の背景地形のチャンクは、都市外の設備(国境の貨物駅に停める路線の停留所など)を置く・選ぶため。
    /// 埋立地の岸壁(天端と垂直の面)は、その上のカーソル位置がコライダーを越えて海底へ抜けないようにするため。
    /// </summary>
    private bool IsTerrainSurfaceObject(GameObject obj) =>
        (cachedTerrainManager != null && cachedTerrainManager.IsBackgroundTerrain(obj)) ||
        (ReclaimedLandManager.Instance != null && ReclaimedLandManager.Instance.IsQuayObject(obj));

    private int GetModeForObjectType(int objectType)
    {
        if (objectTypeModes.ContainsKey(objectType))
        {
            return objectTypeModes[objectType];
        }
        return InputMode.MODE_NORMAL;
    }

    private void StartDrag(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        if (!CanSelectObject(hitObject, hitPoint, objectType)) return;

        int currentMode = GetModeForObjectType(objectType);

        // ブルドーザ(削除)・パス結合/分割の選択・道路の区間編集はクリックのみ対応。ドラッグは
        // 無効化し、誤って RoadPlanManager 等のドラッグ処理へフォールバックしないよう即座に return する。
        if (currentMode == InputMode.MODE_REMOVE
            || currentMode == InputMode.MODE_PATH_MERGE
            || currentMode == InputMode.MODE_PATH_SPLIT
            || currentMode == InputMode.MODE_EDIT_ROAD_MEMBERS)
        {
            return;
        }

        isDragging = true;
        dragStartPosition = hitPoint;
        draggedObject = hitObject;
        draggedObjectType = objectType;

        // ノードの場合、ドラッグ開始を通知
        if (objectType == InputObjectType.NODE)
        {
            var nodeHandler = draggedObject.GetComponent<NodeInputHandler>();
            nodeHandler?.onDragStart(currentMode);
        }
    }

    /// <summary>
    /// レイが何にも当たらなかったときだけ呼ばれる。地形に当たったドラッグは StartDrag(TERRAIN) から始まりここを通らないので、
    /// 地形ドラッグを受けるマネージャは、最初の ProcessMouseDrag のマウス位置から掴む対象を決める。
    /// </summary>
    private void StartTerrainDrag()
    {
        int currentMode = GetModeForObjectType(InputObjectType.TERRAIN);

        // ブルドーザ(削除)・パス分割の分割点選択はクリックのみ対応。地形ドラッグは開始しない。
        if (currentMode == InputMode.MODE_REMOVE
            || currentMode == InputMode.MODE_PATH_SPLIT)
        {
            return;
        }

        if (currentMode == InputMode.MODE_ROAD_CONSTRUCTION || currentMode == InputMode.MODE_EDIT)
        {
            // 道路編集中にマウスがハンドル上にある場合は Editor 側のドラッグを開始する
            if (cachedRoadPlanManager != null && cachedRoadPlanManager.IsEditMode)
            {
                if (cachedRoadPlanManager.IsMouseOverEditHandle(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    return;
                }
            }

            isDragging = true;
            dragStartPosition = new Vector3(posX, 0, posZ);
            draggedObject = null;
            draggedObjectType = InputObjectType.TERRAIN;
        }
        else if (currentMode == InputMode.MODE_PARCEL_CREATION_POINT)
        {
            if (cachedParcelEditorManager != null && cachedParcelEditorManager.HasSelectedPoint())
            {
                isDragging = true;
                dragStartPosition = new Vector3(posX, 0, posZ);
                draggedObject = null;
                draggedObjectType = InputObjectType.TERRAIN;
                cachedParcelEditorManager.StartRotationDrag();
            }
        }
        else if (currentMode == InputMode.MODE_PARCEL_SHAPE_EDIT)
        {
            var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
            if (shapeEditManager != null && shapeEditManager.IsEditMode)
            {
                // 頂点上でドラッグ開始した場合は Editor ドラッグを開始する
                if (shapeEditManager.IsMouseOverVertex(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    shapeEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_RESOURCE)
        {
            var resourceEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
            if (resourceEditManager != null && resourceEditManager.IsEditingPhase)
            {
                // 頂点上でドラッグ開始した場合は Editor ドラッグを開始する(Editingフェーズのみ)
                if (resourceEditManager.IsMouseOverVertex(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    resourceEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_GRADING)
        {
            var gradingEditManager = FindAnyObjectByType<GradingAreaEditManager>();
            if (gradingEditManager != null && gradingEditManager.IsEditingPhase)
            {
                // 頂点上でドラッグ開始した場合は Editor ドラッグを開始する(Editingフェーズのみ)
                if (gradingEditManager.IsMouseOverVertex(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    gradingEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_TRANSIT)
        {
            var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
            if (transitPlanManager != null && transitPlanManager.IsEditingPhase)
            {
                // 停留所・経由点のマーカー上なら点の移動、区間の上なら経由点を追加して動かすドラッグを開始する
                if (transitPlanManager.IsMouseOverDraggable(InputUtil.MousePosition))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    transitPlanManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_CONNECTOR)
        {
            var connectorEditManager = FindAnyObjectByType<ConnectorEditManager>();
            if (connectorEditManager != null && connectorEditManager.IsEditMode)
            {
                // 接続端子ノードのマーカー上でドラッグ開始した場合は端子の移動ドラッグを開始する
                if (connectorEditManager.IsMouseOverConnector(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    connectorEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
    }

    private void UpdateDrag()
    {
        Vector3 currentPosition = new Vector3(posX, 0, posZ);

        if (draggedObjectType == InputObjectType.NODE && draggedObject != null)
        {
            var nodeHandler = draggedObject.GetComponent<NodeInputHandler>();
            nodeHandler?.onDragUpdate(currentPosition);
        }
        else if (draggedObjectType == InputObjectType.TERRAIN)
        {
            int terrainMode = GetModeForObjectType(InputObjectType.TERRAIN);
            if (terrainMode == InputMode.MODE_PARCEL_CREATION_POINT)
            {
                cachedParcelEditorManager?.UpdateRotationPreview(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_PARCEL_SHAPE_EDIT)
            {
                var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
                shapeEditManager?.ProcessMouseDrag(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_EDIT_RESOURCE)
            {
                var resourceEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
                resourceEditManager?.ProcessMouseDrag(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_EDIT_GRADING)
            {
                var gradingEditManager = FindAnyObjectByType<GradingAreaEditManager>();
                gradingEditManager?.ProcessMouseDrag(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_EDIT_TRANSIT)
            {
                var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
                transitPlanManager?.ProcessMouseDrag(currentPosition);
            }
            else if (terrainMode == InputMode.MODE_EDIT_CONNECTOR)
            {
                var connectorEditManager = FindAnyObjectByType<ConnectorEditManager>();
                connectorEditManager?.ProcessMouseDrag(currentPosition);
            }
            else if (cachedRoadPlanManager != null)
            {
                cachedRoadPlanManager.ProcessMouseDrag(currentPosition);
            }
        }
        else
        {
            // RoadPlanManagerにドラッグ情報を送信
            if (cachedRoadPlanManager != null)
            {
                cachedRoadPlanManager.ProcessMouseDrag(currentPosition);
            }
        }
    }

    private void EndDrag()
    {
        if (isDragging)
        {
            Vector3 endPosition = new Vector3(posX, 0, posZ);

            if (draggedObjectType == InputObjectType.NODE && draggedObject != null)
            {
                var nodeHandler = draggedObject.GetComponent<NodeInputHandler>();
                nodeHandler?.onDragEnd(endPosition);
            }
            else if (draggedObjectType == InputObjectType.TERRAIN)
            {
                int terrainMode = GetModeForObjectType(InputObjectType.TERRAIN);
                if (terrainMode == InputMode.MODE_PARCEL_CREATION_POINT)
                {
                    cachedParcelEditorManager?.EndRotationDrag();
                }
                else if (terrainMode == InputMode.MODE_PARCEL_SHAPE_EDIT)
                {
                    var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
                    shapeEditManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (terrainMode == InputMode.MODE_EDIT_RESOURCE)
                {
                    var resourceEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
                    resourceEditManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (terrainMode == InputMode.MODE_EDIT_GRADING)
                {
                    var gradingEditManager = FindAnyObjectByType<GradingAreaEditManager>();
                    gradingEditManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (terrainMode == InputMode.MODE_EDIT_TRANSIT)
                {
                    var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
                    transitPlanManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (terrainMode == InputMode.MODE_EDIT_CONNECTOR)
                {
                    var connectorEditManager = FindAnyObjectByType<ConnectorEditManager>();
                    connectorEditManager?.ProcessMouseDragEnd(endPosition);
                }
                else if (cachedRoadPlanManager != null)
                {
                    cachedRoadPlanManager.ProcessMouseDragEnd(endPosition);
                }
            }
            else
            {
                // RoadPlanManagerにドラッグ終了情報を送信
                if (cachedRoadPlanManager != null)
                {
                    cachedRoadPlanManager.ProcessMouseDragEnd(endPosition);
                }
            }

            isDragging = false;
            draggedObject = null;
            draggedObjectType = -1;
        }
    }

    private TerrainInputHandler GetTerrainInputHandlerAtPosition(Vector3 position)
    {
        // 1. 最初にTerrainManagerが管理するterrainタイルから取得を試みる
        if (cachedTerrainManager != null)
        {
            Terrain terrain = cachedTerrainManager.GetTerrainAtPosition(position);
            if (terrain != null)
            {
                TerrainInputHandler handler = terrain.GetComponent<TerrainInputHandler>();
                if (handler != null)
                {
                    return handler;
                }
            }
        }

        // 2. フォールバック：シーン内の任意のTerrainInputHandlerを取得
        TerrainInputHandler fallbackHandler = FindAnyObjectByType<TerrainInputHandler>();
        if (fallbackHandler != null)
        {
            return fallbackHandler;
        }

        return null;
    }

    #region Public Methods

    public void SetClickEvent(Action<int, Vector3> callback)
    {
        clickEventCallback = callback;
    }

    public void SetHoverEvent(Action<int, Vector3, GameObject> callback)
    {
        hoverEventCallback = callback;
    }

    public void SetMode(int objectType, int mode)
    {
        if (objectTypeModes.ContainsKey(objectType))
        {
            objectTypeModes[objectType] = mode;
            Debug.Log($"InputHandler: オブジェクトタイプ {objectType} のモードを {mode} に変更しました");
        }
        else
        {
            Debug.LogWarning($"InputHandler: 不明なオブジェクトタイプ {objectType} です");
        }
    }

    public int GetMode(int objectType)
    {
        return GetModeForObjectType(objectType);
    }

    public void SetModePreset(int preset)
    {
        switch (preset)
        {
            case InputModePreset.ALL_NONE:
                SetAllObjectTypesMode(InputMode.MODE_NONE);
                Debug.Log("InputHandler: すべてのオブジェクトタイプをNONEモードに設定しました");
                break;

            case InputModePreset.ALL_NORMAL:
                SetAllObjectTypesMode(InputMode.MODE_NORMAL);
                Debug.Log("InputHandler: すべてのオブジェクトタイプをNORMALモードに設定しました");
                break;

            case InputModePreset.ROAD_CONSTRUCTION:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_ROAD_CONSTRUCTION);
                Debug.Log("InputHandler: 道路建設モードプリセットを設定しました");
                break;

            case InputModePreset.BUILDING_CONSTRUCTION:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_BUILDING_CONSTRUCTION);
                // 建設中の配置方法はBuildingPlanManagerが管理する。区画配置が初期値で、
                // 自由配置・道路沿い配置に切り替えるとPARCELをMODE_NONEにして地形へ貫通させる。
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_BUILDING_CONSTRUCTION);
                Debug.Log("InputHandler: 建物建設モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_POINT:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PARCEL_CREATION_POINT);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画作成（点選択）モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_PATH:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_PARCEL_CREATION_PATH);
                Debug.Log("InputHandler: 土地区画作成（パス選択）モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_SPLIT:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_PARCEL_CREATION_SPLIT);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画分割モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_SHAPE_EDIT:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PARCEL_SHAPE_EDIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画形状編集モードプリセットを設定しました");
                break;

            case InputModePreset.SET_GRID:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_SET_GRID);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: グリッド設定ノード選択モードプリセットを設定しました");
                break;

            case InputModePreset.EDIT_RESOURCE:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_RESOURCE);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 資源エリア編集モードプリセットを設定しました");
                break;

            case InputModePreset.EDIT_GRADING:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_GRADING);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 造成範囲の作図モードプリセットを設定しました");
                break;


            case InputModePreset.TRANSIT_CONSTRUCTION:
                // 停留所の設置は地形クリックで行う。下地パス(道路・駅パス)は貫通させて
                // 地形ヒットを採用し、TransitPlanManager 側で下地パスへ射影吸着する。
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_TRANSIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 公共交通設置モードプリセットを設定しました");
                break;

            case InputModePreset.PROP_PLACEMENT:
                // プロップ配置は地形クリックで行う。個別配置は1点ずつ、範囲散布はポリゴン頂点を置く。
                // PropPlacementManager 側でモード(individual/scatter)ごとに処理を振り分ける。
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_PROP);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: プロップ配置モードプリセットを設定しました");
                break;

            case InputModePreset.CONNECTOR_EDIT:
                // 接続点編集モード。接続端子ノードのマーカー(逆四角錐)を掴んでドラッグ移動する。
                // マーカーはノード実体の上空に浮くオーバーレイで、ノードのコライダーとは位置が
                // ずれるため、コライダーへのレイキャストではなく地形ドラッグとして受け、
                // ConnectorEditManager がスクリーン座標のマーカー当たり判定で掴む対象を決める
                // (停留所配置 MODE_EDIT_TRANSIT と同じ方式)。ノードはレイキャストを貫通させ
                // (MODE_NONE)、それ以外のオブジェクトも誤操作防止のためクリック無効化する。
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_CONNECTOR);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 接続点編集モードプリセットを設定しました");
                break;

            case InputModePreset.REMOVE_OBJECTS:
                // ブルドーザ(削除)モード。建物・区画・道路を削除対象として拾う。
                // TERRAIN も MODE_REMOVE にすることで、地形に埋もれた空き区画コライダーを
                // 地形→区画置換(GetParcelIdAtPoint)で解決でき、素の地形上からのドラッグ起点も拾える。
                SetMode(InputObjectType.BUILDING, InputMode.MODE_REMOVE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_REMOVE);
                SetMode(InputObjectType.PATH, InputMode.MODE_REMOVE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_REMOVE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: ブルドーザ(オブジェクト削除)モードプリセットを設定しました");
                break;

            case InputModePreset.PATH_MERGE:
                // パス結合の相手選択。候補パスのクリックだけを受け、他は誤操作防止で無効化する。
                SetMode(InputObjectType.PATH, InputMode.MODE_PATH_MERGE);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: パス結合の相手選択モードプリセットを設定しました");
                break;

            case InputModePreset.PATH_SPLIT:
                // パス分割の分割点選択。対象パス上のクリックに加え、パスの脇の地形クリックも
                // 受けて中心線への垂線の足を分割点にする(バックエンドの射影と同じ考え方)。
                SetMode(InputObjectType.PATH, InputMode.MODE_PATH_SPLIT);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PATH_SPLIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: パス分割の分割点選択モードプリセットを設定しました");
                break;

            case InputModePreset.PICK_BUILDING:
                // 既存建物コピー建設のコピー元選択。建物クリックだけを受け、他は無効化する。
                SetMode(InputObjectType.BUILDING, InputMode.MODE_PICK_BUILDING);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: コピー元建物選択モードプリセットを設定しました");
                break;

            case InputModePreset.ROAD_MEMBER_EDIT:
                // 道路の区間編集。区間のクリックだけを受け、他は誤操作防止で無効化する。
                SetMode(InputObjectType.PATH, InputMode.MODE_EDIT_ROAD_MEMBERS);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 道路の区間編集モードプリセットを設定しました");
                break;

            default:
                Debug.LogWarning($"InputHandler: 不明なプリセット {preset} です");
                break;
        }
    }

    private void SetAllObjectTypesMode(int mode)
    {
        var objectTypes = new int[]
        {
            InputObjectType.BUILDING,
            InputObjectType.PARCEL,
            InputObjectType.PATH,
            InputObjectType.NODE,
            InputObjectType.TERRAIN,
            InputObjectType.ZONE
        };

        foreach (int objectType in objectTypes)
        {
            SetMode(objectType, mode);
        }
    }

    public void OverrideMousePosition(float x, float z)
    {
        posX = x;
        posZ = z;
    }

    public string GetModeName(int mode)
    {
        switch (mode)
        {
            case InputMode.MODE_NONE: return "何もしない";
            case InputMode.MODE_NORMAL: return "通常";
            case InputMode.MODE_ROAD_CONSTRUCTION: return "道路建設";
            case InputMode.MODE_BUILDING_CONSTRUCTION: return "建物建設";
            case InputMode.MODE_PARCEL_CREATION_POINT: return "土地区画作成（点選択）";
            case InputMode.MODE_PARCEL_CREATION_PATH: return "土地区画作成（パス選択）";
            case InputMode.MODE_PARCEL_CREATION_SPLIT: return "土地区画分割";
            case InputMode.MODE_EDIT: return "編集";
            case InputMode.MODE_PARCEL_SHAPE_EDIT: return "土地区画形状編集";
            case InputMode.MODE_SET_GRID: return "グリッド角度設定";
            case InputMode.MODE_EDIT_RESOURCE: return "資源エリア編集";
            case InputMode.MODE_EDIT_TRANSIT: return "公共交通設置";
            case InputMode.MODE_EDIT_PROP: return "プロップ配置";
            case InputMode.MODE_EDIT_CONNECTOR: return "接続点編集";
            case InputMode.MODE_EDIT_ROAD_MEMBERS: return "道路の区間編集";
            case InputMode.MODE_EDIT_GRADING: return "造成範囲の作図";
            default: return "UNKNOWN";
        }
    }

    #endregion
}
