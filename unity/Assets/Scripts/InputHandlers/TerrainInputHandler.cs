
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using GeneratedModels;

/// <summary>
/// 地形オブジェクト用のInputHandler
/// 地点選択、マーカー表示、パーセルエディター機能を統合
/// </summary>
public class TerrainInputHandler : MonoBehaviour, IInputHandler
{
    public int ObjectTypeId => InputObjectType.TERRAIN;
    [Header("Marker Settings")]
    public GameObject markerPrefab;
    public Material markerMaterial;
    public float markerSize = 1.0f;
    
    [Header("References")]
    public ParcelEditorManager parcelEditorManager;
    public PathManager pathManager;
    
    [Header("Snapping Settings")]
    public float snapThreshold = 10.0f;     // ノード／パスへの吸着の閾値（半径10m）

    // 吸着機能の有効/無効を制御する静的変数。
    // このフラグ群が吸着設定の唯一の真実源であり、切り替えは3Dビュー側の
    // ラジアルメニュー(RoadEditControlMenu)とグリッド連動(GridOverlay/GridSetManager)が行う。
    // セッション(敷設モードの出入り)を跨いでも値は保持される仕様。
    public static bool EnableNodeSnapping = true;   // ノード吸着のON/OFF
    public static bool EnablePathSnapping = true;   // パス吸着のON/OFF
    public static bool EnableGridSnapping = false;           // グリッド吸着のON/OFF(グリッド表示と連動)
    public static bool EnableZoneVertexSnapping = false; // ゾーン頂点吸着のON/OFF
    public static bool EnableRightAngleSnapping = false; // 90°直角吸着（デフォルトOFF）
    public static bool EnableStraightLineSnapping = false; // 直線延長吸着（デフォルトOFF）
    
    /// <summary>
    /// 海上でマーカーを置く最低ワールドY（メートル）。
    /// 海面板は worldY=0.001 にあるので、マーカーの厚みぶん上へ逃がす。
    /// </summary>
    private const float SeaMarkerMinY = 0.05f;

    // 現在表示中のマーカー
    private GameObject currentMarker;
    private Vector3 lastMarkerPosition;
    
    // RoadPlanManagerへの参照
    private RoadPlanManager roadPlanManager;

    // BuildingPlanManagerへの参照(直接配置のホバーゴースト用。毎フレーム検索を避けキャッシュする)
    private BuildingPlanManager buildingPlanManager;

    private void Start()
    {
        // マーカー用マテリアルのデフォルト設定
        if (markerMaterial == null)
        {
            markerMaterial = Resources.Load<Material>("MarkerMaterial");
        }

        // ParcelEditorManagerの自動検索
        if (parcelEditorManager == null)
        {
            parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        }

        if (parcelEditorManager == null)
        {
            Debug.LogError("ParcelEditorManagerが見つかりません。シーンに配置されているか確認してください。");
        }

        // PathManagerの自動検索
        if (pathManager == null)
        {
            pathManager = FindAnyObjectByType<PathManager>();
        }

        // RoadPlanManagerの自動検索
        roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        if (roadPlanManager == null)
        {
            Debug.LogError("RoadPlanManagerが見つかりません");
        }
    }

    /// <summary>
    /// BuildingPlanManager を(なければ)検索してキャッシュを返す。
    /// 常駐コンポーネントのため一度見つければ再利用する(ホバーは毎フレーム呼ばれるため検索を避ける)。
    /// </summary>
    private BuildingPlanManager GetBuildingPlanManager()
    {
        if (buildingPlanManager == null)
        {
            buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        }
        return buildingPlanManager;
    }

    /// <summary>
    /// ParcelShapeEditManager を動的に検索して返す。
    /// 形状編集開始時に動的生成されるため、Start() ではなく呼び出し時に毎回検索する。
    /// </summary>
    private ParcelShapeEditManager GetParcelShapeEditManager()
    {
        return FindAnyObjectByType<ParcelShapeEditManager>();
    }

    /// <summary>
    /// ResourceAreaEditManager を動的に検索して返す。
    /// STATE_EDIT_RESOURCE への遷移時に動的生成されるため、Start() ではなく呼び出し時に毎回検索する。
    /// </summary>
    private ResourceAreaEditManager GetResourceAreaEditManager()
    {
        return FindAnyObjectByType<ResourceAreaEditManager>();
    }

    /// <summary>
    /// GradingAreaEditManager を動的に検索して返す。
    /// STATE_EDIT_GRADING への遷移時に動的生成されるため、Start() ではなく呼び出し時に毎回検索する。
    /// </summary>
    private GradingAreaEditManager GetGradingAreaEditManager()
    {
        return FindAnyObjectByType<GradingAreaEditManager>();
    }


    /// <summary>
    /// TransitPlanManager を動的に検索して返す。
    /// STATE_EDIT_TRANSIT への遷移時に動的生成されるため、Start() ではなく呼び出し時に毎回検索する。
    /// </summary>
    private TransitPlanManager GetTransitPlanManager()
    {
        return FindAnyObjectByType<TransitPlanManager>();
    }

    /// <summary>
    /// PropPlacementManager を動的に検索して返す。
    /// STATE_EDIT_PROP への遷移時に動的生成されるため、Start() ではなく呼び出し時に毎回検索する。
    /// </summary>
    private PropPlacementManager GetPropPlacementManager()
    {
        return FindAnyObjectByType<PropPlacementManager>();
    }

    public void onClick(int mode)
    {
        Vector3 clickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
        clickPosition.y = Utils.GetGroundHeight(clickPosition);
        // 道路/鉄道建設モードは RoadPlanManager が自前の吸着半径(snapThreshold)とバリアント
        // フィルタで一度だけ吸着する。ここで共通吸着(インスタンス既定半径・フィルタなし)を
        // 掛けると二重吸着になり、RoadPlanManager 側の半径設定が一切効かなくなる。
        // パス分割の分割点選択も共通吸着を掛けない(端点ノードへ引き寄せられると常に
        // 端点近傍の分割になって拒否されるため。onHover 側と条件を揃える)。
        // 造成範囲の作図も共通吸着を掛けない。頂点が道路の中心線に乗ると範囲がパスに掛かり、発注で必ず拒否されるため。
        if (mode != InputMode.MODE_ROAD_CONSTRUCTION && mode != InputMode.MODE_PATH_SPLIT
            && mode != InputMode.MODE_BUILDING_CONSTRUCTION && mode != InputMode.MODE_EDIT_PROP
            && mode != InputMode.MODE_PARCEL_SHAPE_EDIT
            && mode != InputMode.MODE_EDIT_GRADING)
        {
            clickPosition = ApplySnapping(clickPosition);
        }

        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                HandleNormalClick(clickPosition, mode);
                break;
            case InputMode.MODE_ROAD_CONSTRUCTION:
                HandleRoadConstructionClick(clickPosition);
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                {
                    // 直接配置中は区画を貫通して地形を選ぶ。区画配置中の地形クリックは
                    // BuildingPlanManagerが無視する。吸着は同マネージャで一度だけ行う。
                    var bpm = GetBuildingPlanManager();
                    if (bpm != null)
                    {
                        bpm.OnTerrainClickDirect(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_PARCEL_SHAPE_EDIT:
                {
                    var shapeEditManager = GetParcelShapeEditManager();
                    Debug.Log($"TerrainInputHandler.onClick: MODE_PARCEL_SHAPE_EDIT shapeEditManager={shapeEditManager != null}");
                    if (shapeEditManager != null)
                    {
                        shapeEditManager.OnTerrainLeftClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_SET_GRID:
                {
                    // ホバーと同じく、グリッド吸着・グローバルフラグに依存しない専用吸着を使う。
                    // clickPosition は冒頭で通常の ApplySnapping を通っているため、生の地形座標から再吸着する。
                    Vector3 raw = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
                    raw.y = Utils.GetGroundHeight(raw);
                    GridSetManager.Instance.OnTerrainClicked(ApplySnappingForGridSet(raw));
                }
                break;
            case InputMode.MODE_EDIT_RESOURCE:
                {
                    var resourceEditManager = GetResourceAreaEditManager();
                    if (resourceEditManager != null)
                    {
                        resourceEditManager.OnTerrainLeftClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_GRADING:
                GetGradingAreaEditManager()?.OnTerrainLeftClick(clickPosition);
                break;
            case InputMode.MODE_EDIT_TRANSIT:
                {
                    var transitPlanManager = GetTransitPlanManager();
                    if (transitPlanManager != null)
                    {
                        transitPlanManager.OnTerrainLeftClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_PROP:
                {
                    var propPlacementManager = GetPropPlacementManager();
                    if (propPlacementManager != null)
                    {
                        propPlacementManager.OnTerrainLeftClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_CONNECTOR:
                // 接続点編集モードでは地形クリックは何もしない(端子の移動はドラッグでのみ行う)
                break;
            case InputMode.MODE_NONE:
                // 何もしない
                break;
            case InputMode.MODE_REMOVE:
                // ブルドーザモードでの素の地形クリックは何もしない。
                // 地形に埋もれた空き区画は InputHandler の地形→区画置換で
                // ParcelInputHandler.onClick(MODE_REMOVE) に差し替えられて処理される。
                break;
            case InputMode.MODE_PATH_SPLIT:
                // パス脇の地形クリックも分割点の指定として受ける(中心線への垂線の足を採る)。
                PathTopologyEditManager.Instance?.OnClickForSplit();
                break;
            default:
                HandleNormalClick(clickPosition, mode);
                break;
        }
    }

    public void onRightClick(int mode)
    {
        Vector3 clickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
        clickPosition.y = Utils.GetGroundHeight(clickPosition);
        // 道路/鉄道建設モードは RoadPlanManager が自前の吸着半径で吸着するため共通吸着はしない
        // (onClick と同じ理由。二重吸着で RoadPlanManager 側の半径が効かなくなるのを防ぐ)。
        if (mode != InputMode.MODE_ROAD_CONSTRUCTION && mode != InputMode.MODE_PARCEL_SHAPE_EDIT
            && mode != InputMode.MODE_EDIT_GRADING)
        {
            clickPosition = ApplySnapping(clickPosition);
        }
        switch (mode)
        {
            case InputMode.MODE_ROAD_CONSTRUCTION:
                if (roadPlanManager != null)
                {
                    // 道路敷設モードでの右クリック：道路確定
                    roadPlanManager.OnTerrainRightClick(clickPosition);
                }
                break;
            case InputMode.MODE_PARCEL_SHAPE_EDIT:
                {
                    var shapeEditManager = GetParcelShapeEditManager();
                    Debug.Log($"TerrainInputHandler.onRightClick: MODE_PARCEL_SHAPE_EDIT shapeEditManager={shapeEditManager != null}");
                    if (shapeEditManager != null)
                    {
                        shapeEditManager.OnTerrainRightClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_RESOURCE:
                {
                    var resourceEditManager = GetResourceAreaEditManager();
                    if (resourceEditManager != null)
                    {
                        resourceEditManager.OnTerrainRightClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_GRADING:
                GetGradingAreaEditManager()?.OnTerrainRightClick(clickPosition);
                break;
            case InputMode.MODE_EDIT_TRANSIT:
                {
                    var transitPlanManager = GetTransitPlanManager();
                    if (transitPlanManager != null)
                    {
                        transitPlanManager.OnTerrainRightClick(clickPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_PROP:
                {
                    var propPlacementManager = GetPropPlacementManager();
                    if (propPlacementManager != null)
                    {
                        propPlacementManager.OnTerrainRightClick(clickPosition);
                    }
                }
                break;
            default:
                break;
        }
    }

    public void onHoverStart(int mode)
    {
        // マーカー表示は onHover で継続的に更新するため、ここでは何もしない
    }

    public void onHoverEnd(int mode)
    {
        HideMarker();
        if (mode == InputMode.MODE_SET_GRID && GridSetManager.Instance != null)
        {
            GridSetManager.Instance.OnTerrainHoverEnd();
        }
        if (mode == InputMode.MODE_BUILDING_CONSTRUCTION)
        {
            // 空き地形から外れた(区画上へ移動した等)ら直接配置のホバーゴーストを消す。
            GetBuildingPlanManager()?.HideDirectGhost();
        }
        // ポリゴン編集のPlacingマーカーは、UI上へ移った・地形から外れたときに消す。
        if (mode == InputMode.MODE_PARCEL_SHAPE_EDIT)
        {
            GetParcelShapeEditManager()?.OnTerrainHoverEnd();
        }
        else if (mode == InputMode.MODE_EDIT_RESOURCE)
        {
            GetResourceAreaEditManager()?.OnTerrainHoverEnd();
        }
        else if (mode == InputMode.MODE_EDIT_GRADING)
        {
            GetGradingAreaEditManager()?.OnTerrainHoverEnd();
        }
    }

    public void onHover(int mode)
    {
        Vector3 basePosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
        basePosition.y = Utils.GetGroundHeight(basePosition);

        // ホバーで吸着した位置はカーソル座標へ書き戻され、後続のクリック・ドラッグにも混入する。
        // そのため吸着しないモードは、ドラッグ側だけでなくホバーでも吸着を省く。
        // 接続点編集モードでは吸着を適用しない。ノード吸着(半径10m)が有効なままだと、
        // ドラッグ中の端子ノードが「自分自身の移動前のキャッシュ位置」(nodeGridはPATCH完了まで
        // 旧位置を保持)へ吸着し続け、10m以内では動かず境界で飛ぶ壊れた挙動になるため。
        // 道路/鉄道建設モードも共通吸着を掛けない。RoadPlanManager.OnTerrainHover が自前の
        // 吸着半径(snapThreshold)で吸着しプレビューを描くため、ここで二重吸着すると
        // RoadPlanManager 側の半径設定が効かなくなる。
        // パス分割の分割点選択も共通吸着を掛けない。ノード吸着(半径10m)が効くと、対象パスの
        // 端点ノード付近でカーソルが端点へ引き寄せられ、常に「端点に近すぎる」分割になって
        // バックエンドに拒否されるため。分割点は中心線への射影(PathTopologyEditManager)で決める。
        bool skipSnap = mode == InputMode.MODE_EDIT_CONNECTOR
                     || mode == InputMode.MODE_PARCEL_SHAPE_EDIT
                     || mode == InputMode.MODE_EDIT_GRADING
                     || mode == InputMode.MODE_EDIT_PROP
                     || mode == InputMode.MODE_BUILDING_CONSTRUCTION
                     || mode == InputMode.MODE_ROAD_CONSTRUCTION
                     || mode == InputMode.MODE_PATH_SPLIT;
        Vector3 finalPosition = skipSnap
            ? basePosition
            : ApplySnapping(basePosition);
        if (Vector3.Distance(basePosition, finalPosition) > 0.01f)
        {
            InputHandler.Instance.OverrideMousePosition(finalPosition.x, finalPosition.z);
        }

        switch (mode)
        {
            case InputMode.MODE_ROAD_CONSTRUCTION:
                if (roadPlanManager != null)
                {
                    roadPlanManager.OnTerrainHover(finalPosition);
                }
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                // 自由配置・道路沿い配置のゴースト。共通の道路中心線吸着は適用しない。
                // 区画配置中も生の座標を保持し、Ctrlメニューを閉じた直後の再評価に使う。
                GetBuildingPlanManager()?.OnTerrainHoverDirect(finalPosition);
                break;
            case InputMode.MODE_PARCEL_CREATION_POINT:
                // 前回の位置から一定距離以上移動した場合のみ更新（パフォーマンス対策）
                if (Vector3.Distance(finalPosition, lastMarkerPosition) > 0.1f)
                {
                    ShowMarker(finalPosition);
                    lastMarkerPosition = finalPosition;
                }
                break;
            case InputMode.MODE_PARCEL_SHAPE_EDIT:
                {
                    var shapeEditManager = GetParcelShapeEditManager();
                    if (shapeEditManager != null)
                    {
                        shapeEditManager.OnTerrainHover(finalPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_RESOURCE:
                {
                    var resourceEditManager = GetResourceAreaEditManager();
                    if (resourceEditManager != null)
                    {
                        resourceEditManager.OnTerrainHover(finalPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_GRADING:
                GetGradingAreaEditManager()?.OnTerrainHover(finalPosition);
                break;
            case InputMode.MODE_EDIT_TRANSIT:
                {
                    var transitPlanManager = GetTransitPlanManager();
                    if (transitPlanManager != null)
                    {
                        transitPlanManager.OnTerrainHover(finalPosition);
                    }
                }
                break;
            case InputMode.MODE_EDIT_PROP:
                {
                    var propPlacementManager = GetPropPlacementManager();
                    if (propPlacementManager != null)
                    {
                        propPlacementManager.OnTerrainHover(finalPosition);
                    }
                }
                break;
            case InputMode.MODE_SET_GRID:
                {
                    // グリッド角度設定では「設定対象であるグリッド」への吸着は無意味なので除外し、
                    // グローバルな吸着フラグ(UIから変更されうる)にも依存せず、既存ノード/パスへ直接吸着する。
                    Vector3 gridSnap = ApplySnappingForGridSet(basePosition);
                    if (Vector3.Distance(basePosition, gridSnap) > 0.01f)
                    {
                        InputHandler.Instance.OverrideMousePosition(gridSnap.x, gridSnap.z);
                    }
                    GridSetManager.Instance.OnTerrainHover(gridSnap);
                }
                break;
            default:
                HideMarker();
                break;
        }
    }

    private void HandleNormalClick(Vector3 position, int mode)
    {
        Debug.Log($"TerrainInputHandler: 地形がクリックされました: {position}");
        
        if (mode == InputMode.MODE_PARCEL_CREATION_POINT)
        {
            HandleParcelEditorPointSelection(position);
            return;
        }
    }

    private void HandleParcelEditorPointSelection(Vector3 position)
    {
        // ParcelEditorManagerに地点選択を通知
        if (parcelEditorManager != null)
        {
            parcelEditorManager.OnPointSelected(position);
        }
        else
        {
            Debug.LogError("parcelEditorManagerが見つかりません");
        }
    }

    private void HandleRoadConstructionClick(Vector3 position)
    {
        if (roadPlanManager == null)
        {
            Debug.LogError("RoadPlanManagerが見つかりません");
            return;
        }

        // スナップ済みのpositionが既に渡されているのでそのままRoadPlanManagerに委譲
        roadPlanManager.OnTerrainLeftClick(position);
    }

    private void ShowMarker(Vector3 position)
    {
        // 海上では地形標高が海底(worldY<0)なので、そのまま置くと海面板に隠れて
        // どこを指しているか見えなくなる。
        position.y = Mathf.Max(position.y, SeaMarkerMinY);

        // 既存のマーカーがある場合は位置を更新
        if (currentMarker != null)
        {
            currentMarker.transform.position = position;
            return;
        }
        
        // 新しいマーカーを作成
        if (markerPrefab != null)
        {
            currentMarker = Instantiate(markerPrefab, position, Quaternion.identity);
        }
        else
        {
            // プリミティブでマーカーを作成
            currentMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            currentMarker.name = "TerrainMarker";
            
            // マーカーのスケールを設定
            currentMarker.transform.localScale = new Vector3(markerSize, 0.1f, markerSize);
            currentMarker.transform.position = position;
            
            // マテリアルを適用
            if (markerMaterial != null)
            {
                Renderer renderer = currentMarker.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material = markerMaterial;
                }
            }
            
            // コライダーを削除（クリック対象にしない）
            Collider markerCollider = currentMarker.GetComponent<Collider>();
            if (markerCollider != null)
            {
                Destroy(markerCollider);
            }
        }
    }

    private void HideMarker()
    {
        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
        }
    }

    private void OnDestroy()
    {
        if (roadPlanManager != null)
        {
        }
        HideMarker();
    }


    #region パーセルエディター統合機能

    private void Update()
    {
        // Escキーによる選択のリセット自体は ParcelEditorManager.Update が行う
        // (作成モードからは抜けず、同じモードで選び直せる)。
        // ここは地形ホバーで出しているマーカーを消すだけ。このコンポーネントは
        // 地形タイルごとに付くため、条件は軽く保つこと。
        if (parcelEditorManager != null
            && parcelEditorManager.IsActive
            && InputUtil.WorldEscapeWasPressed)
        {
            HideMarker();
        }
    }

    #endregion
    
    #region Snapping Functions

    /// <summary>
    /// グリッド角度設定モード専用の吸着。
    /// グリッド自身への吸着（設定対象なので無意味）と、UIから変更されうるグローバル吸着フラグ
    /// (EnableNodeSnapping / EnablePathSnapping 等)に依存せず、既存ノード→パスの順に直接吸着する。
    /// どちらも閾値内に無ければ basePosition をそのまま返す。
    /// </summary>
    public Vector3 ApplySnappingForGridSet(Vector3 basePosition)
    {
        if (pathManager == null)
        {
            pathManager = FindAnyObjectByType<PathManager>();
        }
        if (pathManager == null) return basePosition;

        float actualThreshold = (snapThreshold > 0.01f) ? snapThreshold : 10.0f;

        // ノードを優先（交差点に正確に合わせたいケースを優先する）
        Vector3 snappedPos;
        if (FindNearestNode(basePosition, actualThreshold, pathManager, out snappedPos) != null)
        {
            return snappedPos;
        }

        // 次にパス（道路）上へ吸着
        GeneratedModels.PathSummary snappedPath;
        int segmentIndex;
        if (FindNearestPathSegment(basePosition, actualThreshold, pathManager, out snappedPos, out snappedPath, out segmentIndex))
        {
            return snappedPos;
        }

        return basePosition;
    }

    public Vector3 ApplySnapping(Vector3 basePosition)
    {
        return ApplySnapping(basePosition, -1);
    }

    public Vector3 ApplySnapping(Vector3 basePosition, int drawingPathVariant)
    {
        return ApplySnapping(basePosition, drawingPathVariant, out _, out _);
    }

    /// <summary>
    /// 共通スナップ（グリッド/ゾーン頂点/ノード/建設中端点/パス）を適用する。
    ///
    /// drawingPathVariant には建設中パスのバリアントを渡す（-1 = 制限なし）。
    /// 指定時はインフラ網の互換性フィルタが働き、道路はインフラノード/インフラパスに
    /// 吸着せず、インフラパスは同一網（infra_products が重なる）のノード・パス・
    /// 施設の接続端子ノードにのみ吸着する（InfraNetworkUtil 参照）。
    ///
    /// snappedToElevatable / snapRelHeight は吸着対象の高さ引き継ぎ用のメタ情報。
    /// ノード/パスセグメント/建設中端点へ吸着したとき true と対象の相対高さ（標高）を返し、
    /// 呼び出し側は高架への接続で配置ノードの高さとして採用できる。
    /// グリッド/ゾーン頂点/未吸着では false, 0。
    ///
    /// thresholdOverride に正値を渡すとインスタンスの snapThreshold の代わりに使う。
    /// 道路/鉄道敷設のように編集側が独自の吸着半径を持つ場合に、共有ハンドラの既定半径から
    /// 切り離して吸着範囲を制御するために用いる（未指定=負値ならインスタンス値を使用）。
    /// skipGrid は道路の並行補助で接続→並行→グリッドの順にするための省略指定。
    /// </summary>
    public Vector3 ApplySnapping(Vector3 basePosition, int drawingPathVariant, out bool snappedToElevatable, out float snapRelHeight, float thresholdOverride = -1f, bool skipGrid = false)
    {
        snappedToElevatable = false;
        snapRelHeight = 0f;

        if (pathManager == null)
        {
            pathManager = FindAnyObjectByType<PathManager>();
        }

        float actualThreshold = (thresholdOverride > 0.01f) ? thresholdOverride : ((snapThreshold > 0.01f) ? snapThreshold : 10.0f);

        // グリッドへの吸着を最優先（可視状態に依存しない）
        if (!skipGrid && EnableGridSnapping && GridOverlay.Instance != null)
        {
            return GridOverlay.Instance.SnapPosition(basePosition);
        }

        // ゾーン頂点への吸着を試行
        if (EnableZoneVertexSnapping)
        {
            Vector3 snappedPos;
            if (FindNearestZoneVertex(basePosition, actualThreshold, out snappedPos))
            {
                return snappedPos;
            }
        }

        // ノードへの吸着を試行（建設中/計画中パスの端点も含む）
        if (EnableNodeSnapping)
        {
            Vector3 snappedPos;
            NodeData snappedNode = FindNearestNode(basePosition, actualThreshold, pathManager, out snappedPos, drawingPathVariant);
            if (snappedNode != null)
            {
                // 吸着位置は設計標高またはノード相対Yから組み立て、描画Yを含まない。
                snappedToElevatable = true;
                snapRelHeight = snappedPos.y - Utils.GetGroundHeight(snappedPos);
                return snappedPos;
            }

            // 既存ノードに吸着しなかった場合、建設中/計画中パスの端点への吸着を試行する。
            // 建設が完了してアクティブになるまでは端点にノードが作られないため、
            // 計画中・建設中のパスの start_point / end_point を直接判定対象とする。
            Vector3 endpointPos;
            float endpointRelHeight;
            if (FindNearestConstructionEndpoint(basePosition, actualThreshold, pathManager, out endpointPos, out endpointRelHeight, drawingPathVariant))
            {
                snappedToElevatable = true;
                snapRelHeight = endpointRelHeight;
                return endpointPos;
            }
        }

        // パスへの吸着を試行
        if (EnablePathSnapping)
        {
            Vector3 snappedPos;
            GeneratedModels.PathSummary snappedPath;
            int segmentIndex;
            if (FindNearestPathSegment(basePosition, actualThreshold, pathManager, out snappedPos, out snappedPath, out segmentIndex, drawingPathVariant))
            {
                // 標高の引き継ぎは吸着先パスのアンカー相対Yを吸着点で補間して求める。
                // 描画ポリラインの絶対Y−地形サンプルの逆算は禁止
                // (GetRelativeHeightAtPoint のコメント参照。地面のパスへの吸着でも
                //  端点が地下/空中扱いになり、インフラのT字タップが無言で不成立になる)
                snappedToElevatable = true;
                snapRelHeight = snappedPath != null
                    ? snappedPath.GetRelativeHeightAtPoint(snappedPos.x, snappedPos.z)
                    : 0f;
                return snappedPos;
            }
        }

        return basePosition;
    }

    /// <summary>地下表示では、半透明の地上区間を敷設時の表示・吸着候補から除く。</summary>
    public static bool IsPathSnapPointVisible(GeneratedModels.PathSummary path, Vector3 position)
    {
        return !UndergroundView.IsActive || (path != null && TunnelSectionUtil.IsTunnelPoint(position,
            TunnelSectionUtil.GetClearanceHeight(path.path_variant)));
    }

    public static bool IsNodeSnapVisible(NodeData node, PathManager pathManager)
    {
        if (!UndergroundView.IsActive) return true;
        var paths = pathManager.GetPathsConnectedToNode(node.node_id);
        // 交差点の材質分類と同じ接続パスを使う。孤立した端子は深さだけで判定する。
        float clearance = paths.Count > 0 ? TunnelSectionUtil.GetClearanceHeight(paths[0].path_variant) : 0f;
        return TunnelSectionUtil.IsTunnelPoint(pathManager.GetNodeSnapPosition(node), clearance);
    }

    /// <summary>混在パスも描画と同じ境界で切り分け、地下区間だけを候補として返す。</summary>
    public static IEnumerable<PathSegment> GetVisiblePathSnapSegments(IEnumerable<PathSegment> segments)
    {
        if (segments == null) yield break;
        foreach (var segment in segments)
        {
            if (!UndergroundView.IsActive)
            {
                yield return segment;
                continue;
            }
            if (segment.path == null) continue;
            var runs = TunnelSectionUtil.CollectTunnelRuns(new List<Vector3> { segment.a, segment.b },
                TunnelSectionUtil.GetClearanceHeight(segment.path.path_variant),
                PathManager.GetPathFollowTerrain(segment.path));
            foreach (var run in runs)
                for (int i = 1; i < run.Count; i++)
                    yield return new PathSegment { path = segment.path, segmentIndex = segment.segmentIndex,
                        a = run[i - 1], b = run[i] };
        }
    }

    public static NodeData FindNearestNode(Vector3 basePos, float threshold, PathManager pathManager, out Vector3 snappedPos, int drawingPathVariant = -1)
    {
        if (pathManager == null)
        {
            snappedPos = basePos;
            return null;
        }

        // === 近傍ノードをグリッド検索 ===
        var nearby = pathManager.QueryNearbyNodes(basePos, threshold);
        snappedPos = basePos;
        if (nearby == null || nearby.Count == 0) return null;

        float minDist = float.MaxValue;
        NodeData resultNode = null;

        foreach (var node in nearby)
        {
            // インフラ網の互換性フィルタ（道路×インフラ、別網インフラ同士は吸着しない）
            if (!InfraNetworkUtil.IsNodeSnappable(node, drawingPathVariant, pathManager)) continue;
            if (drawingPathVariant >= 0 && !IsNodeSnapVisible(node, pathManager)) continue;

            Vector3 nodePos = pathManager.GetNodeSnapPosition(node);

            float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(nodePos.x, nodePos.z));
            if (dist < minDist && dist <= threshold)
            {
                minDist = dist;
                resultNode = node;
                snappedPos = nodePos;
            }
        }
        return resultNode;
    }

    public static bool FindNearestPathSegment(Vector3 basePos, float threshold, PathManager pathManager, out Vector3 snappedPos, out GeneratedModels.PathSummary snappedPath, out int snappedSegmentIndex, int drawingPathVariant = -1)
    {
        // 初期化
        snappedPos = basePos;
        snappedPath = null;
        snappedSegmentIndex = -1;

        if (pathManager == null)
        {
            return false;
        }

        IEnumerable<PathSegment> segments = pathManager.QueryNearbySegments(basePos, threshold);
        if (drawingPathVariant >= 0) segments = GetVisiblePathSnapSegments(segments);
        float minDist = float.MaxValue;
        bool found = false;

        foreach (var seg in segments)
        {
            // インフラ網の互換性フィルタ（道路×インフラ、別網インフラ同士は吸着しない）
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, drawingPathVariant)) continue;

            Vector3 closest = PolylineUtils.ClosestPointOnSegmentXZ(basePos, seg.a, seg.b);
            float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(closest.x, closest.z));
            if (dist < minDist && dist <= threshold)
            {
                minDist = dist;
                snappedPos = closest;
                snappedPath = seg.path;
                snappedSegmentIndex = seg.segmentIndex;
                found = true;
            }
        }

        return found;
    }

    public static bool FindNearestZoneVertex(Vector3 basePos, float threshold, out Vector3 snappedPos)
    {
        snappedPos = basePos;
        if (ZoneManager.Instance == null) return false;

        var zones = ZoneManager.Instance.GetAllZones();
        if (zones == null || zones.Count == 0) return false;

        float minDist = float.MaxValue;
        bool found = false;

        foreach (var kv in zones)
        {
            int zoneId = kv.Key;
            var zone = kv.Value;
            float[][] polygonArray = zone.GetPolygonArray();
            if (polygonArray == null) continue;

            foreach (float[] point in polygonArray)
            {
                if (point == null || point.Length < 2) continue;
                float x = point[0];
                float z = point[1];
                // 高さは地形に合わせる
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
                Vector3 vertexPos = new Vector3(x, y, z);

                float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(vertexPos.x, vertexPos.z));
                if (dist < minDist && dist <= threshold)
                {
                    minDist = dist;
                    snappedPos = vertexPos;
                    found = true;
                }
            }
        }

        return found;
    }
    
    /// <summary>
    /// 建設中(STATUS_CONSTRUCTING)または計画中(STATUS_SCHEDULED_CONSTRUCTION)のパスの
    /// 端点(start_point / end_point)のうち、basePos に最も近いものを探す。
    /// 建設が完了してアクティブになるまでは端点にノードが作られないが、
    /// パスの端点位置には吸着できるようにするためのメソッド。
    /// </summary>
    public static bool FindNearestConstructionEndpoint(Vector3 basePos, float threshold, PathManager pathManager, out Vector3 snappedPos, out float snapRelHeight, int drawingPathVariant = -1)
    {
        snappedPos = basePos;
        snapRelHeight = 0f;
        if (pathManager == null) return false;

        var allPaths = pathManager.GetAllPaths();
        if (allPaths == null) return false;

        float minDist = float.MaxValue;
        bool found = false;

        foreach (var path in allPaths)
        {
            if (path.status != Constants.STATUS_SCHEDULED_CONSTRUCTION &&
                path.status != Constants.STATUS_PENDING &&
                path.status != Constants.STATUS_CONSTRUCTING)
            {
                continue;
            }

            // インフラ網の互換性フィルタ（道路×インフラ、別網インフラ同士は吸着しない）
            if (!InfraNetworkUtil.IsPathSnappable(path.path_variant, drawingPathVariant))
            {
                continue;
            }

            // start_point / end_point は [x, 相対高さ, z] 形式。高架の計画中/建設中パスに
            // 吸着したとき高さも引き継げるよう、中央要素を相対高さとして絶対Yを組み立てる。
            var startList = GeneratedModelUtils.ToFloatList(path.start_point);
            if (startList != null && startList.Count >= 2)
            {
                float sx = startList[0];
                float sz = startList[startList.Count - 1];
                float sRel = path.GetRelativeHeightAtPoint(sx, sz);
                float sy = Utils.GetGroundHeight(new Vector3(sx, 0f, sz)) + sRel;
                Vector3 startPos = new Vector3(sx, sy, sz);
                float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(sx, sz));
                if (dist < minDist && dist <= threshold
                    && (drawingPathVariant < 0 || IsPathSnapPointVisible(path, startPos)))
                {
                    minDist = dist;
                    snappedPos = startPos;
                    snapRelHeight = sRel;
                    found = true;
                }
            }

            var endList = GeneratedModelUtils.ToFloatList(path.end_point);
            if (endList != null && endList.Count >= 2)
            {
                float ex = endList[0];
                float ez = endList[endList.Count - 1];
                float eRel = path.GetRelativeHeightAtPoint(ex, ez);
                float ey = Utils.GetGroundHeight(new Vector3(ex, 0f, ez)) + eRel;
                Vector3 endPos = new Vector3(ex, ey, ez);
                float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(ex, ez));
                if (dist < minDist && dist <= threshold
                    && (drawingPathVariant < 0 || IsPathSnapPointVisible(path, endPos)))
                {
                    minDist = dist;
                    snappedPos = endPos;
                    snapRelHeight = eRel;
                    found = true;
                }
            }
        }

        return found;
    }

    
    #endregion
}
