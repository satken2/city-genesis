
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
    public const int PROP = 7;
    // 駅舎(StationManagerが生成する駅の建屋オブジェクト)。クリックで駅詳細パネルを開く。
    public const int STATION = 8;
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
    public const int MODE_ZONE_EDIT = 8;
    public const int MODE_UNLOCK_CELL = 9;
    public const int MODE_PARCEL_SHAPE_EDIT = 10;
    public const int MODE_SET_GRID = 11;
    public const int MODE_EDIT_RESOURCE = 12;
    public const int MODE_EDIT_TRANSIT = 13;
    public const int MODE_EDIT_PROP = 14;
    // 施設の接続端子ノード(インフラ接続点 / 走行網接続点)のドラッグ移動編集モード。
    // 停留所配置(MODE_EDIT_TRANSIT)と同じく地形ドラッグとして扱い、
    // 実体の掴み判定・移動は ConnectorEditManager がスクリーン座標のマーカー当たり判定で行う。
    public const int MODE_EDIT_CONNECTOR = 15;
    // 土地区画の「任意ポリゴン」作成モード。地形クリックで頂点を打ち、右クリックで仮確定、
    // 頂点ドラッグで調整する。資源エリア編集(MODE_EDIT_RESOURCE)と同じ地形ドラッグ方式で、
    // 実際のポリゴン組み立ては ParcelAreaEditManager が担う。
    public const int MODE_PARCEL_CREATION_POLYGON = 16;
    // ブルドーザ(オブジェクト削除)モード。空き地区画・計画中の建物/道路を
    // 左クリックまたは左ドラッグ(ペイント式)で削除する。実処理は RemovalManager が担う。
    public const int MODE_REMOVE = 17;
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
    public const int ZONE_EDIT = 7;
    public const int UNLOCK_CELL = 8;
    public const int PARCEL_SHAPE_EDIT = 9;
    public const int SET_GRID = 10;
    public const int EDIT_RESOURCE = 11;
    public const int TRANSIT_CONSTRUCTION = 12;
    public const int PROP_PLACEMENT = 13;
    public const int CONNECTOR_EDIT = 14;
    public const int PARCEL_CREATION_POLYGON = 15;
    // ブルドーザ(オブジェクト削除)モードのプリセット。建物・区画・道路・地形を
    // すべて MODE_REMOVE にして削除対象として拾えるようにする。
    public const int REMOVE_OBJECTS = 16;
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
        { InputObjectType.ZONE, InputMode.MODE_NORMAL },
        { InputObjectType.STATION, InputMode.MODE_NORMAL }
    };

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
    private bool pendingZoneDrag = false;
    private int pendingZoneDragId = -1;
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

    // 地区ドラッグ関連
    private bool isZoneDrag = false;
    private int draggedZoneId = -1;
    private int hoveredZoneId = -1;
    private Ray lastRay;

    // キャッシュされたマネージャー参照
    private RoadPlanManager cachedRoadPlanManager;
    private TerrainManager cachedTerrainManager;
    private ParcelEditorManager cachedParcelEditorManager;
    private ParcelShapeEditManager cachedParcelShapeEditManager;
    private ResourceAreaEditManager cachedResourceAreaEditManager;
    private ParcelManager cachedParcelManager;
    private PathManager cachedPathManager;

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
        cachedPathManager = FindAnyObjectByType<PathManager>();

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
        // フライモード中はマウス入力（クリック・ホバー・ドラッグ）を一切受け付けない。
        // カメラを自由飛行させている間は3Dオブジェクトの選択やホバー（ツールチップ含む）を
        // 完全に無効化するため、他のどの判定よりも先に即抜けする。
        if (CameraController.Instance != null && CameraController.Instance.IsFlyMode)
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            pendingZoneDrag = false;
            CancelZoneDrag();
            return;
        }

        // ラジアルメニュー表示中も3Dオブジェクトへのマウス入力を一切受け付けない。
        // フライモードと同じ非破壊ゲート方式で、メニューを閉じれば元の入力状態がそのまま復帰する
        // (メニューのCanvasにはGraphicRaycasterが無いため、EventSystemのUI判定では止まらない)。
        if (RadialMenuController.IsOpen)
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            pendingZoneDrag = false;
            CancelZoneDrag();
            return;
        }

        // 地区拡張ドラッグ中にマウスを離したフレームは、この後の「UI上ゲート」「カメラドラッグゲート」で
        // release フレームが握りつぶされてドラッグが宙ぶらりん(commit も cancel もされず
        // isZoneDrag=true のまま)になるのを防ぐため、ここで確実にドラッグ終了処理を行う。
        // 地区編集では地区名ラベル(クリック可能=GraphicRaycaster対象)が地区中心に表示されるため、
        // 拡張ドラッグの終点がラベル上に重なると下の IsPointerOverGameObject ゲートに掛かって
        // EndDrag が飛ばされ、バックエンドへの一括割当(POST /paths/batch-zone)が送られないバグがあった。
        // (フライモード・ラジアルメニュー中は上のゲートで既に CancelZoneDrag 済みなのでここには来ない。
        //  地区ドラッグの終端処理は EndDrag 内でマウス位置から直接レイキャストするため posX/posZ 更新前でも正しい)
        if (isZoneDrag && InputUtil.LeftMouseWasReleasedThisFrame && Camera.main != null)
        {
            EndDrag();
            return;
        }

        // UI上をクリックした場合は処理を中断
        if (EventSystem.current.IsPointerOverGameObject())
        {
            ClearHover();
            TooltipManager.Instance?.Hide();
            // UIクリック時はペンディング中のクリックを破棄する
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            pendingZoneDrag = false;
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
            hasPendingClick = false;
            hasPendingTerrainClick = false;
            hasPendingRightClick = false;
            hasPendingTerrainRightClick = false;
            pendingZoneDrag = false;
            return;
        }

        // メインカメラが存在しない場合は処理を中断
        // ローディング画面などでカメラを意図的に非表示にしている際に、
        // NullReferenceExceptionが発生するのを防ぐため
        if (Camera.main == null)
        {
            return;
        }

        // 地区編集モード中の Esc は進行中の地区拡張ドラッグのみをキャンセルする
        // (モード自体の終了は従来どおりUI起点の STATE_NORMAL に委ねる)。
        // レイ不変フレームでもドラッグ中は下の早期リターンに達しないため、ここで先に処理する。
        if (GetModeForObjectType(InputObjectType.TERRAIN) == InputMode.MODE_ZONE_EDIT
            && InputUtil.KeyWasPressedThisFrame(Key.Escape))
        {
            CancelZoneDrag();
        }

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

        bool rayChanged = ray.origin != lastRay.origin || ray.direction != lastRay.direction;

        if (!rayChanged && !hasInputEvent)
        {
            // マウスもカメラも動いておらず、入力状態変化もないため、重い判定をすべてスキップする。
            return;
        }

        lastRay = ray;

        // まずは地形との交点を計算してposX, posZを更新
        UpdateTerrainPosition(ray);

        // 地区ポリゴン内判定
        // 通常プレイ時の無駄な毎フレーム内外判定を排除するため、地区編集モード中のみ実行する。
        int prevHoveredZoneId = hoveredZoneId;
        var zoneEditCtx = ZoneEditContext.Instance;
        bool isZoneEditActive = zoneEditCtx != null && zoneEditCtx.IsActive;
        bool isZoneEditMode = GetModeForObjectType(InputObjectType.TERRAIN) == InputMode.MODE_ZONE_EDIT;

        if (isZoneEditActive || isZoneEditMode)
        {
            Vector3 mouseWorldPos = new Vector3(posX, 0, posZ);
            mouseWorldPos.y = Utils.GetGroundHeight(mouseWorldPos);
            hoveredZoneId = ZoneManager.Instance?.GetZoneIdAtPoint(mouseWorldPos) ?? -1;

            // ホバー中の既存地区を半透明フィルでハイライトする(ホバー地区が変わった時のみ再生成)。
            ZoneManager.Instance?.SetHoverFillZone(hoveredZoneId);

            // 地区内部のツールチップは拡張ツール時のみ表示する
            // (作成ツールは道路クリックが主操作。パスホバー優先のためパスがホバー中でない場合のみ)。
            bool isExpandTool = zoneEditCtx != null && zoneEditCtx.CurrentTool == ZoneEditTool.Expand;
            if (hoveredZoneId >= 0 && hoveredZoneId != prevHoveredZoneId && currentHoveredObjectType != InputObjectType.PATH)
            {
                if (isExpandTool)
                {
                    string zoneName = ZoneManager.Instance?.GetZoneName(hoveredZoneId) ?? "";
                    TooltipManager.Instance?.Show($"{zoneName}\nShift+ドラッグで地区を拡張");
                }
                else
                {
                    TooltipManager.Instance?.Hide();
                }
            }
            else if (hoveredZoneId < 0 && prevHoveredZoneId >= 0 && currentHoveredObjectType != InputObjectType.PATH)
            {
                TooltipManager.Instance?.Hide();
            }
        }
        else
        {
            hoveredZoneId = -1;
            // 地区編集モードを抜けたらホバーフィルを消す
            ZoneManager.Instance?.SetHoverFillZone(-1);
            if (prevHoveredZoneId >= 0 && currentHoveredObjectType != InputObjectType.PATH)
            {
                TooltipManager.Instance?.Hide();
            }
        }

        // オブジェクトとの当たり判定
        // MODE_NONEのオブジェクトを貫通するためにRaycastAllを使用
        RaycastHit[] hits = Physics.RaycastAll(ray, rayDistance, rayLayerMask);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        GameObject validHitObject = null;
        Vector3 validHitPoint = Vector3.zero;
        int validHitObjectType = -1;

        foreach (var hit in hits)
        {
            int objectType = GetObjectType(hit.collider.gameObject);
            int mode = GetModeForObjectType(objectType);

            if (mode != InputMode.MODE_NONE)
            {
                validHitObject = hit.collider.gameObject;
                validHitPoint = hit.point;
                validHitObjectType = objectType;
                break;
            }
        }

        // 地形ヒットの区画への差し替え:
        // 区画のMeshCollider上面はポリゴンの角の頂点でしか地形高さをサンプルしないため、
        // 起伏地形では頂点間の地形の盛り上がりにコライダーが埋まり、レイキャストでは
        // 手前の地形が必ず勝って区画を選択できない。区画は概念的に「地面上の領域」なので、
        // 最初の有効ヒットが地形の場合はヒット点のXZがどの区画ポリゴンに含まれるかを
        // 2D内外判定で解決し、含まれるならディスパッチ先をその区画に差し替える。
        // 区画コライダーが地形より上に出ている場合は従来どおりレイキャストが先に区画を拾う。
        if (validHitObjectType == InputObjectType.TERRAIN &&
            GetModeForObjectType(InputObjectType.PARCEL) != InputMode.MODE_NONE &&
            cachedParcelManager != null)
        {
            int parcelId = cachedParcelManager.GetParcelIdAtPoint(validHitPoint);
            if (parcelId >= 0)
            {
                GameObject parcelObj = cachedParcelManager.GetParcelObject(parcelId);
                if (parcelObj != null && parcelObj.activeInHierarchy)
                {
                    validHitObject = parcelObj;
                    validHitObjectType = InputObjectType.PARCEL;
                }
            }
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

                // 地区拡張のShift+ドラッグ候補を記録する。条件は:
                // Shift押下 / 地区編集がアクティブ / 拡張ツール / ドラッグ起点の地区が解決できる /
                // 地形モードが地区編集(MODE_ZONE_EDIT)。起点地区は「地区ポリゴン内部」または
                // 「地区所属の道路上」で解決する(ResolveZoneDragSourceZone)。
                // Shift無し(素のドラッグ)はカメラパンに任せ、地区ドラッグを開始しない。
                int terrainModeForZoneDrag = GetModeForObjectType(InputObjectType.TERRAIN);
                int sourceZoneId = ResolveZoneDragSourceZone(validHitObject, validHitObjectType);
                bool shiftHeld = InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
                var zoneDragCtx = ZoneEditContext.Instance;
                bool isZoneDragTarget = shiftHeld
                    && zoneDragCtx != null && zoneDragCtx.IsActive
                    && zoneDragCtx.CurrentTool == ZoneEditTool.Expand
                    && sourceZoneId > 0
                    && terrainModeForZoneDrag == InputMode.MODE_ZONE_EDIT;
                if (isZoneDragTarget)
                {
                    pendingZoneDrag = true;
                    pendingZoneDragId = sourceZoneId;
                }
                else
                {
                    pendingZoneDrag = false;
                }
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
                // レイキャストが何にもヒットしなかった場合は、起点地区は「地区ポリゴン内部」のみで解決する。
                // Shift+拡張ツール+地区編集モードのときだけ地区拡張ドラッグ候補にする。
                bool shiftHeldTerrain = InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
                var zoneDragCtxTerrain = ZoneEditContext.Instance;
                pendingZoneDrag = shiftHeldTerrain
                    && zoneDragCtxTerrain != null && zoneDragCtxTerrain.IsActive
                    && zoneDragCtxTerrain.CurrentTool == ZoneEditTool.Expand
                    && hoveredZoneId > 0
                    && GetModeForObjectType(InputObjectType.TERRAIN) == InputMode.MODE_ZONE_EDIT;
                pendingZoneDragId = hoveredZoneId;
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
                pendingZoneDrag = false;
            }
            else if ((InputUtil.MousePosition - pendingClickScreenPos).magnitude >= CLICK_DRAG_THRESHOLD)
            {
                hasPendingClick = false;
                // 地形ヒット時に地区ポリゴン内でドラッグが開始された場合は地区ドラッグを優先する
                if (pendingZoneDrag)
                {
                    StartZoneDrag(pendingZoneDragId);
                }
                else
                {
                    StartDrag(pendingClickObject, pendingClickPoint, pendingClickObjectType);
                }
            }
        }
        if (hasPendingTerrainClick)
        {
            if (CameraController.Instance != null && CameraController.Instance.IsMouseDragging)
            {
                hasPendingTerrainClick = false;
                pendingZoneDrag = false;
            }
            else if ((InputUtil.MousePosition - pendingTerrainClickScreenPos).magnitude >= CLICK_DRAG_THRESHOLD)
            {
                hasPendingTerrainClick = false;
                if (pendingZoneDrag)
                {
                    StartZoneDrag(pendingZoneDragId);
                }
                else
                {
                    StartTerrainDrag();
                }
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
            // クリック候補が残っていたらマウスアップ時にクリックとして発火する
            if (hasPendingClick)
            {
                HandleObjectClick(pendingClickObject, pendingClickPoint, pendingClickObjectType);
                hasPendingClick = false;
            }
            if (hasPendingTerrainClick)
            {
                HandleTerrainClick();
                hasPendingTerrainClick = false;
                pendingZoneDrag = false;
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
            if (hit.collider.gameObject.GetComponent<Terrain>() != null)
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
        if (hitObject.GetComponent<Terrain>() != null)
        {
            var terrainHandler = hitObject.GetComponent<IInputHandler>();
            terrainHandler?.onHover(GetModeForObjectType(InputObjectType.TERRAIN));
        }
    }

    private void HandleObjectClick(GameObject hitObject, Vector3 hitPoint, int objectType)
    {
        // マウスダウンからマウスアップまでの間にセルリロード等で対象が破棄されている可能性がある
        if (hitObject == null) return;

        int currentMode = GetModeForObjectType(objectType);
        var handler = hitObject.GetComponent<IInputHandler>();
        handler?.onClick(currentMode);

        // クリックイベントコールバック
        clickEventCallback?.Invoke(objectType, hitPoint);
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
        if (hitObject == null) return;

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
        if (obj.CompareTag("StationObject")) return InputObjectType.STATION;
        if (obj.CompareTag("SeaObject")) return InputObjectType.TERRAIN;
        if (obj.GetComponent<Terrain>() != null) return InputObjectType.TERRAIN;

        return -1; // 不明なタイプ
    }

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
        if (hitObject == null) return;

        int currentMode = GetModeForObjectType(objectType);

        // ブルドーザ(削除)モードはクリックのみ対応。ドラッグは無効化し、
        // 誤って RoadPlanManager 等のドラッグ処理へフォールバックしないよう即座に return する。
        if (currentMode == InputMode.MODE_REMOVE)
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
    /// 地区ポリゴン上でドラッグを開始する。拡張元地区を固定する。
    /// ドラッグ中になぞった道路は追加予定リストへ溜まり、EndDrag で一括割り当てされる。
    /// </summary>
    private void StartZoneDrag(int zoneId)
    {
        isDragging = true;
        isZoneDrag = true;
        draggedZoneId = zoneId;
        draggedObject = null;
        draggedObjectType = -1;
        dragStartPosition = new Vector3(posX, 0, posZ);
    }

    /// <summary>
    /// 地区拡張ドラッグの起点となる「拡張元地区ID」を解決する。
    /// (1) マウス位置が地区ポリゴン内部にある(hoveredZoneId>0)ならその地区。
    /// (2) さもなくばヒット対象が道路で、その道路が地区に所属している(zone_id>0)ならその地区
    ///     (=地区に含まれる道路上からもドラッグを開始できる)。
    /// (3) どちらでもなければ -1。
    /// </summary>
    private int ResolveZoneDragSourceZone(GameObject hitObject, int hitType)
    {
        if (hoveredZoneId > 0) return hoveredZoneId;

        if (hitType == InputObjectType.PATH && hitObject != null && cachedPathManager != null)
        {
            var pathHandler = hitObject.GetComponent<PathInputHandler>();
            if (pathHandler != null)
            {
                var pathData = cachedPathManager.GetPathData(pathHandler.pathId);
                if (pathData != null && pathData.zone_id > 0) return pathData.zone_id;
            }
        }
        return -1;
    }

    /// <summary>
    /// 進行中の地区拡張ドラッグをキャンセルする(コミットせずに破棄)。
    /// 追加予定リスト・一時色付け・プレビューを全て元に戻し、ドラッグ状態を解除する。
    /// フライモード/ラジアルメニューへの遷移や Esc 押下から呼ばれる。
    /// </summary>
    private void CancelZoneDrag()
    {
        if (!isZoneDrag) return;
        ZoneEditContext.Instance?.ClearDragPendingPaths();
        ZoneEditContext.Instance?.ClearDragTempVisuals();
        isZoneDrag = false;
        draggedZoneId = -1;
        isDragging = false;
        draggedObject = null;
        draggedObjectType = -1;
    }

    /// <summary>地区拡張ドラッグが進行中か(CameraController のフライモードトグル抑止判定に使う)。</summary>
    public bool IsZoneDragActive => isZoneDrag;

    private void StartTerrainDrag()
    {
        int currentMode = GetModeForObjectType(InputObjectType.TERRAIN);

        // ブルドーザ(削除)モードはクリックのみ対応。地形ドラッグは開始しない。
        if (currentMode == InputMode.MODE_REMOVE)
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
        else if (currentMode == InputMode.MODE_PARCEL_CREATION_POLYGON)
        {
            var parcelAreaEditManager = FindAnyObjectByType<ParcelAreaEditManager>();
            if (parcelAreaEditManager != null && parcelAreaEditManager.IsEditingPhase)
            {
                // 頂点上でドラッグ開始した場合は Editor ドラッグを開始する(Editingフェーズのみ)
                if (parcelAreaEditManager.IsMouseOverVertex(InputUtil.MousePosition, Camera.main))
                {
                    isDragging = true;
                    dragStartPosition = new Vector3(posX, 0, posZ);
                    draggedObject = null;
                    draggedObjectType = InputObjectType.TERRAIN;
                    parcelAreaEditManager.StartDrag(InputUtil.MousePosition);
                    return;
                }
            }
        }
        else if (currentMode == InputMode.MODE_EDIT_TRANSIT)
        {
            var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
            if (transitPlanManager != null && transitPlanManager.IsEditingPhase)
            {
                // 停留所マーカー上でドラッグ開始した場合は停留所の移動ドラッグを開始する
                if (transitPlanManager.IsMouseOverStop(InputUtil.MousePosition, Camera.main))
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

        if (isZoneDrag)
        {
            // 地区ドラッグ中はパスを探してハイライト
            Ray ray = Camera.main.ScreenPointToRay(InputUtil.MousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, rayDistance, rayLayerMask);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var hit in hits)
            {
                int objectType = GetObjectType(hit.collider.gameObject);
                if (objectType == InputObjectType.PATH)
                {
                    var pathHandler = hit.collider.GetComponent<PathInputHandler>();
                    pathHandler?.onZoneDragHover(draggedZoneId);
                    break;
                }
            }
        }
        else if (draggedObjectType == InputObjectType.NODE && draggedObject != null)
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
            else if (terrainMode == InputMode.MODE_PARCEL_CREATION_POLYGON)
            {
                var parcelAreaEditManager = FindAnyObjectByType<ParcelAreaEditManager>();
                parcelAreaEditManager?.ProcessMouseDrag(currentPosition);
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

            if (isZoneDrag)
            {
                // 地区ドラッグ終了: マウス下にパスがあればハイライトを解除する
                Ray ray = Camera.main.ScreenPointToRay(InputUtil.MousePosition);
                RaycastHit[] hits = Physics.RaycastAll(ray, rayDistance, rayLayerMask);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                foreach (var hit in hits)
                {
                    int objectType = GetObjectType(hit.collider.gameObject);
                    if (objectType == InputObjectType.PATH)
                    {
                        var pathHandler = hit.collider.GetComponent<PathInputHandler>();
                        pathHandler?.onZoneDragEnd(draggedZoneId);
                        break;
                    }
                }

                // ドラッグ中になぞった道路を拡張元地区へ一括で割り当てる。
                // パス上以外(地形など)で離した場合にも確定させるため、レイキャスト結果に
                // 依存せずここで必ずコミットする。
                ZoneEditContext.Instance?.CommitDragPendingPaths(draggedZoneId);

                isZoneDrag = false;
                draggedZoneId = -1;
            }
            else if (draggedObjectType == InputObjectType.NODE && draggedObject != null)
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
                else if (terrainMode == InputMode.MODE_PARCEL_CREATION_POLYGON)
                {
                    var parcelAreaEditManager = FindAnyObjectByType<ParcelAreaEditManager>();
                    parcelAreaEditManager?.ProcessMouseDragEnd(endPosition);
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
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_ROAD_CONSTRUCTION);
                Debug.Log("InputHandler: 道路建設モードプリセットを設定しました");
                break;

            case InputModePreset.BUILDING_CONSTRUCTION:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_BUILDING_CONSTRUCTION);
                // 地形(区画外)クリックを直接配置モードとして受けるため TERRAIN にも
                // 建設モードを設定する。区画内クリックは InputHandler のフォールバック
                // (GetParcelIdAtPoint)が ParcelInputHandler へ差し替えるため、
                // TerrainInputHandler へ来るのは「どの区画にも属さない地点」だけになる。
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_BUILDING_CONSTRUCTION);
                Debug.Log("InputHandler: 建物建設モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_POINT:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PARCEL_CREATION_POINT);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画作成（点選択）モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_PATH:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_PARCEL_CREATION_PATH);
                Debug.Log("InputHandler: 土地区画作成（パス選択）モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_SPLIT:
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_PARCEL_CREATION_SPLIT);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画分割モードプリセットを設定しました");
                break;

            case InputModePreset.ZONE_EDIT:
                SetMode(InputObjectType.PATH, InputMode.MODE_ZONE_EDIT);
                SetMode(InputObjectType.ZONE, InputMode.MODE_ZONE_EDIT);
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_ZONE_EDIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 地区編集モードプリセットを設定しました");
                break;

            case InputModePreset.UNLOCK_CELL:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_UNLOCK_CELL);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 地形セル開放モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_SHAPE_EDIT:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PARCEL_SHAPE_EDIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画形状編集モードプリセットを設定しました");
                break;

            case InputModePreset.SET_GRID:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_SET_GRID);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: グリッド設定ノード選択モードプリセットを設定しました");
                break;

            case InputModePreset.EDIT_RESOURCE:
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_RESOURCE);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 資源エリア編集モードプリセットを設定しました");
                break;

            case InputModePreset.PARCEL_CREATION_POLYGON:
                // 土地区画の任意ポリゴン作成。地形クリックで頂点を打つため、既存区画・パス・建物は
                // 貫通(MODE_NONE)させて地形ヒットを採用し、ParcelAreaEditManager 側で処理する。
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_PARCEL_CREATION_POLYGON);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                SetMode(InputObjectType.PATH, InputMode.MODE_NONE);
                SetMode(InputObjectType.NODE, InputMode.MODE_NONE);
                SetMode(InputObjectType.PARCEL, InputMode.MODE_NONE);
                SetMode(InputObjectType.ZONE, InputMode.MODE_NONE);
                Debug.Log("InputHandler: 土地区画作成（任意ポリゴン）モードプリセットを設定しました");
                break;

            case InputModePreset.TRANSIT_CONSTRUCTION:
                // 停留所の設置は地形クリックで行う。下地パス(道路・駅パス)は貫通させて
                // 地形ヒットを採用し、TransitPlanManager 側で下地パスへ射影吸着する。
                SetMode(InputObjectType.TERRAIN, InputMode.MODE_EDIT_TRANSIT);
                SetMode(InputObjectType.BUILDING, InputMode.MODE_NONE);
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
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
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
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
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
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
                // 駅舎は単独で削除できない(所属ホームの全喪失で駅ごと自動消滅する)ため対象外。
                SetMode(InputObjectType.STATION, InputMode.MODE_NONE);
                Debug.Log("InputHandler: ブルドーザ(オブジェクト削除)モードプリセットを設定しました");
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
            InputObjectType.ZONE,
            InputObjectType.STATION
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

    public string GetObjectTypeName(int objectType)
    {
        switch (objectType)
        {
            case InputObjectType.BUILDING: return "建物";
            case InputObjectType.PARCEL: return "土地区画";
            case InputObjectType.PATH: return "道路";
            case InputObjectType.NODE: return "ノード";
            case InputObjectType.TERRAIN: return "地面";
            case InputObjectType.ZONE: return "地区";
            case InputObjectType.PROP: return "プロップ";
            case InputObjectType.STATION: return "駅舎";
            default: return "不明";
        }
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
            case InputMode.MODE_ZONE_EDIT: return "地区編集";
            case InputMode.MODE_PARCEL_SHAPE_EDIT: return "土地区画形状編集";
            case InputMode.MODE_SET_GRID: return "グリッド角度設定";
            case InputMode.MODE_EDIT_RESOURCE: return "資源エリア編集";
            case InputMode.MODE_EDIT_TRANSIT: return "公共交通設置";
            case InputMode.MODE_EDIT_PROP: return "プロップ配置";
            case InputMode.MODE_EDIT_CONNECTOR: return "接続点編集";
            case InputMode.MODE_PARCEL_CREATION_POLYGON: return "土地区画作成（任意ポリゴン）";
            default: return "UNKNOWN";
        }
    }

    #endregion
}
