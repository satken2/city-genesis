using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using System;
using System.Globalization;
using GeneratedModels;

/// <summary>
/// BuildingPlanManager:
/// 新規建物の建設、既存建物の編集など建物計画全体を管理。
/// InputHandlerシステムと連携して、マウス操作による建物建設機能を提供。
/// </summary>
public partial class BuildingPlanManager : MonoBehaviour
{
    [Header("Construction Settings")]
    public int bdType = 0;        // 建物タイプ（bd_type）
    public int agentType = 0;        // エージェントタイプ（agent_type）
    public int agentVariant = 0;     // エージェントバリアント（agent_variant）
    public float defaultConstructionRatio = 0.8f; // デフォルトの建蔽率
    public int defaultFloors = 3;           // デフォルトの階数

    /// <summary>
    /// 建設モードで選択中のバリアントが要求する土地区画種類(agent_master.parcel_type)。
    /// 0 は「建設モード外」で、表示レイヤーから直接選ばれた建設可能フィルタがこれに当たる。
    /// 建設可能区画のハイライト(ParcelManager)と区画クリックの検証が同じ条件を見るための持ち回しで、
    /// バリアント単位の条件を bd_type 単位の FILTER_* モードでは表現できないため別に保持する。
    /// </summary>
    public static int SelectedParcelType { get; private set; }

    // 実際に使用する建設値（入力値）
    private float currentConstructionRatio = 0.8f;
    private int currentFloors = 3;
    // 外観バリアント(visual_variant1/2/3)。0.0〜1.0の値で建物の外観を決定する。
    // UIの外観乱数スライダーと連動し、プレビューメッシュの生成時にBuildingSummaryへ設定される。
    // 確定時にバックエンドへ送信される。
    private float currentVisualVariant = 0.5f;
    private float currentVisualVariant2 = 0.5f;
    private float currentVisualVariant3 = 0.5f;
    // 構造種別(structure_master のID。0=構造の区別なし)。UIの構造選択と連動し、
    // プレビューメッシュの生成時に BuildingSummary.structure へ設定される(パーツ prefix の解決に効く)。
    private int currentStructure = 0;
    // 観光名所の種別(landmark_master のID。0=観光名所ではない)。テンプレート作成モードの名所選択と連動し、
    // プレビューメッシュの生成時に BuildingSummary.landmark へ設定される(パーツ prefix の解決に効く)。
    private int currentLandmark = 0;

    // プレビュー形状のキャッシュ。外観バリアント変更時にバックエンドへ再取得せず
    // キャッシュしたポリゴンからメッシュを再生成するために保持する。
    private float[][] cachedPolygon;
    private float[] cachedCenterPoint;
    private float cachedHeight;

    // 選択中の土地区画ID
    private int selectedParcelId = -1;

    [SerializeField] private Material previewMaterial; // プレビュー用マテリアル
    
    // プレビュー用コンポーネント
    [SerializeField] private MeshFilter previewMeshFilter;
    [SerializeField] private MeshRenderer previewMeshRenderer;
    private Mesh previewMesh;

    [Header("Sound Effects")]
    [SerializeField] private AudioClip selectParcelSound;
    [SerializeField] private AudioClip changeFloorsSound;

    // 効果音再生用
    private AudioSource audioSource;

    private Coroutine shapeCoroutine;

    // 現在のモード（新規建設 or 編集）
    private bool isEditMode = false;
    private int editingBuildingId = -1;
    private BuildingDetail editingBuildingData = null;

    // 建物建設モードの状態
    private bool isBuildingModeActive = false;

    // インフラ施設(変電所・下水処理場・電波塔など)の効果範囲を3D表示するオーバーレイ。
    // 効果範囲を持つ施設を選んで敷地を決めたときに表示し、モード終了・クリア指示で消す。
    private EffectRangeOverlay effectRangeOverlay;

    // ===== 直接配置のリアルタイム矩形ゴースト =====
    // マスターで初期化し、キーボードまたはパネルの差分コマンドで変更する直接配置パラメータ。
    // 直接配置の底面は「回転付き矩形」なのでUnity単独で生成でき、バックエンド往復なしに
    // カーソル追従のリアルタイムプレビュー(ゴースト)を描画できる。
    private float directWidth = 0f;
    private float directDepth = 0f;
    // 回転角(度)。負値・360超もそのまま保持する(確定送信時にUI側がmod 360する)。
    // 中ボタンドラッグ中はUnityが角度の権威となる。
    private float directAngle = 0f;
    private bool hasDirectParams = false;    // UIから一度でもパラメータを受信したか
    private bool directCommitted = false;    // 地点が確定(左クリック)済みか。true=フェーズB
    private bool directPreviewVisible = false; // 直接配置のプレビューメッシュが現在表示されているか
    private Vector3 directCenter = Vector3.zero; // 現在の矩形中心(フェーズA:カーソル / フェーズB:確定点)

    // 中ボタンドラッグ回転(プロップ配置と共通の PlacementInput.cs)。ドラッグ中はUnityが角度の権威となる。
    private readonly MiddleDragRotation middleDrag = new MiddleDragRotation();
    private bool isMiddleDragging => middleDrag.IsDragging;

    // ホバーゴースト再生成のスロットル(前回描画した中心と角度)
    private Vector3 lastGhostCenter = new Vector3(float.MaxValue, 0f, float.MaxValue);
    private float lastGhostAngle = float.NaN;
    private const float GhostRebuildDistance = 0.5f; // この距離以上カーソルが動いたら再生成(m)

    private void Awake()
    {
        if (previewMeshFilter == null || previewMeshRenderer == null)
        {
            // プレビュー用のオブジェクトを探すか作成する
            GameObject previewObj = GameObject.Find("BuildingPreview");
            if (previewObj == null)
            {
                previewObj = new GameObject("BuildingPreview");
                // 影を落とさないなどの設定が必要な場合はここで行う
            }

            if (previewMeshFilter == null)
            {
                previewMeshFilter = previewObj.GetComponent<MeshFilter>();
                if (previewMeshFilter == null) previewMeshFilter = previewObj.AddComponent<MeshFilter>();
            }

            if (previewMeshRenderer == null)
            {
                previewMeshRenderer = previewObj.GetComponent<MeshRenderer>();
                if (previewMeshRenderer == null) previewMeshRenderer = previewObj.AddComponent<MeshRenderer>();
            }
        }

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        gameObject.AddComponent<BuildingEditControlMenu>().Initialize(this);
    }

    /// <summary>
    /// 建物詳細情報のデータ構造
    /// </summary>
    [Serializable]
    public class BuildingDetail
    {
        public int agent_id;
        public int bd_type;
        public int bd_variant;
        public int status;
        public int floors;
        public float construction_ratio;
        public int parcel_id;
        // 外観バリアント。single_agentスキーマがこれらを含まない場合はnullとなる（JSONにフィールドが存在しないとfloat?はnull）。
        // その場合はBuildingManagerキャッシュ(multi_agentスキーマ)からフォールバック取得する。
        public float? visual_variant;
        public float? visual_variant2;
        public float? visual_variant3;
        // 構造種別(未設定の旧スキーマなら null → 0 扱い)
        public int? structure;
        // 観光名所の種別(観光名所でなければ null → 0 扱い)
        public int? landmark;
    }

    [Serializable]
    private class BuildingShape
    {
        public List<List<float>> polygon;
        public float height;
        public List<float> center_point;
    }

    [Serializable]
    private class AgentResponse
    {
        public BuildingDetail economic_agent;
    }

    public void SetEditMode(int buildingId)
    {
        Debug.Log($"BuildingPlanManager: 編集モードを設定(SetEditMode) - 建物ID: {buildingId}");
        isEditMode = true;
        editingBuildingId = buildingId;
        
        // 編集対象の建物情報を取得
        StartCoroutine(LoadEditingBuildingData());
    }

    public void SetNewBuildingMode()
    {
        Debug.Log("BuildingPlanManager: 新規建設モードを設定(SetNewBuildingMode)");
        isEditMode = false;
        editingBuildingId = -1;
        editingBuildingData = null;
    }

    public void SetBuildingVariant(int variant)
    {
        agentVariant = variant;
        // 構造の既定は許可構造の最上位(UIの既定と同じ)。UI が明示的に送る structure で上書きされる。
        currentStructure = 0;
        // 観光名所はテンプレート作成モードの名所選択でしか決まらないので、バリアント変更では未選択に戻す。
        currentLandmark = 0;
        if (MasterDataStore.Instance != null)
        {
            var masterForStructure = MasterDataStore.Instance.GetAgentMaster(variant);
            if (masterForStructure?.structures != null && masterForStructure.structures.Count > 0)
            {
                currentStructure = masterForStructure.structures.Max();
            }
        }

        // バリアント変更は新規の配置フロー開始とみなし、直接配置のゴースト状態を初期化する
        // (地点未確定=フェーズAへ戻す)。寸法はマスターから初期化し、回転は0度に戻す。
        ResetDirectPlacementState();
        InitializePlacement();
        
        // MasterDataStoreからagent_masterを取得して、エージェントタイプと建物タイプを設定
        if (MasterDataStore.Instance != null)
        {
            var agentMaster = MasterDataStore.Instance.GetAgentMaster(variant);
            if (agentMaster != null)
            {
                // agent_type（TYPE_BUILDING等）を設定
                if (agentMaster.type.HasValue)
                {
                    agentType = agentMaster.type.Value;
                }

                // bd_type（建物サブタイプ）を設定
                if (agentMaster.bd_type.HasValue)
                {
                    bdType = agentMaster.bd_type.Value;
                }

                SelectedParcelType = agentMaster.parcel_type ?? 0;

                Debug.Log($"BuildingPlanManager: 建物バリアントを設定しました: {variant}, agent_type: {agentType}, bd_type: {bdType}, parcel_type: {SelectedParcelType}");
            }
            else
            {
                SelectedParcelType = 0;
                Debug.LogWarning($"BuildingPlanManager: agent_master (variant={variant}) が見つかりません");
            }
        }
        else
        {
            SelectedParcelType = 0;
            Debug.LogWarning("BuildingPlanManager: MasterDataStore.Instanceがnullです");
            Debug.Log($"BuildingPlanManager: 建物バリアントを設定しました: {variant}");
        }
    }

    public void SetBuildingMode(bool mode)
    {
        isBuildingModeActive = mode;
        if (mode)
        {
            ApplyPlacementInputMode();
            EmitPlacementState();
            Debug.Log("建物建設モード: ON");
        }
        else
        {
            Debug.Log("建物建設モード: OFF");
            ResetBuildingConstruction();
        }
    }

    private IEnumerator LoadEditingBuildingData()
    {
        yield return ApiClient.Get<AgentResponse>(
            $"/agents/{editingBuildingId}",
            response =>
            {
                if (response == null || response.economic_agent == null)
                {
                    Debug.LogError($"建物 {editingBuildingId} の詳細データが取得できませんでした");
                    return;
                }

                editingBuildingData = response.economic_agent;

                // 編集可能かチェック
                if (editingBuildingData.status != Constants.STATUS_TEMPORARY && editingBuildingData.status != Constants.STATUS_SCHEDULED_CONSTRUCTION)
                {
                    Debug.LogWarning($"建物 {editingBuildingId} は編集できません。ステータス: {editingBuildingData.status}");
                    return;
                }

                // 現在の設定を建物データから読み込み
                agentType = editingBuildingData.bd_type;
                agentVariant = editingBuildingData.bd_variant;
                defaultConstructionRatio = editingBuildingData.construction_ratio;
                defaultFloors = editingBuildingData.floors;

                // 現在の建設値も更新
                currentConstructionRatio = editingBuildingData.construction_ratio;
                currentFloors = editingBuildingData.floors;
                currentStructure = editingBuildingData.structure ?? 0;
                currentLandmark = editingBuildingData.landmark ?? 0;

                // 外観バリアントを読み込む。
                // single_agentスキーマがvisual_variantを含まない場合、フィールドはnullとなるため、
                // BuildingManagerキャッシュ(multi_agentスキーマはvisual_variantを含む)からフォールバック取得する。
                if (editingBuildingData.visual_variant.HasValue || editingBuildingData.visual_variant2.HasValue || editingBuildingData.visual_variant3.HasValue)
                {
                    currentVisualVariant = editingBuildingData.visual_variant ?? 0.5f;
                    currentVisualVariant2 = editingBuildingData.visual_variant2 ?? 0.5f;
                    currentVisualVariant3 = editingBuildingData.visual_variant3 ?? 0.5f;
                }
                else if (BuildingManager.Instance != null)
                {
                    BuildingSummary cached = BuildingManager.Instance.GetBuildingSummary(editingBuildingId);
                    if (cached != null)
                    {
                        currentVisualVariant = cached.visual_variant;
                        currentVisualVariant2 = cached.visual_variant2;
                        currentVisualVariant3 = cached.visual_variant3;
                        Debug.Log($"編集対象建物のvisual_variantをキャッシュから取得: vv1={currentVisualVariant}, vv2={currentVisualVariant2}, vv3={currentVisualVariant3}");
                    }
                }

                Debug.Log($"編集対象建物データを読み込みました: Type={agentType}, Variant={agentVariant}, Floors={currentFloors}");
            },
            error => Debug.LogError($"建物詳細の取得に失敗: {error}")
        );
    }

    public void OnParcelClick(int parcelId, Vector3 position, ParcelSummary parcelData = null)
    {
        if (!isEditMode && PlacementMode != BuildingPlacementMode.Parcel) return;
        if (!isBuildingModeActive)
        {
            Debug.Log("建物建設モードが無効のため、クリックを無視します");
            return;
        }

        // 未選択はバリアントだけで判定する。国境検問所は type=TYPE_DEFAULT(0) なので type では判定できない。
        if (agentVariant == 0)
        {
            Debug.LogWarning("バリアントが設定されていません");
            return;
        }

        // 建設可能区画のハイライトと同じ条件で立地を弾き、UIへ警告を出す。
        // parcelData が無い呼び出しは判定できないので、バックエンドの検証に委ねて通す。
        if (parcelData != null && !IsParcelTypeAllowed(parcelData.parcel_type, bdType))
        {
            Debug.Log($"BuildingPlanManager: parcel_type={parcelData.parcel_type} はバリアント {agentVariant} の立地条件({ResolveRequiredParcelType(bdType)})を満たしません。クリックを無視します。");
            // UIに通知して警告を表示
            if (IpcClient.Instance != null)
            {
                IpcClient.Instance.EmitEditBuildingInvalidParcel(parcelId);
            }
            return;
        }

        // 選択された土地区画を記録（プレビュー生成と確定時に使用）
        selectedParcelId = parcelId;
        PlaySound(selectParcelSound);

        // 区画を選択したら直接配置のゴースト/プレビューは消す(区画ベースの配置フローへ移行)。
        // directCommitted を先に落とさないと HideDirectGhost が確定済みプレビューを消せない。
        directCommitted = false;
        HideDirectGhost();

        if (isEditMode)
        {
            // 編集モードの場合はIPC経由で移動確認をUIに通知
            if (IpcClient.Instance != null)
            {
                IpcClient.Instance.EmitEditBuildingSelectParcel(
                    parcelId,
                    new float[] { position.x, position.y, position.z },
                    true,
                    editingBuildingId,
                    parcelData?.GetPolygonArray()
                );
            }
        }
        else
        {
            // 新規建設の場合はIPC経由でパーセル選択をUIに通知
            if (IpcClient.Instance != null)
            {
                IpcClient.Instance.EmitEditBuildingSelectParcel(
                    parcelId,
                    new float[] { position.x, position.y, position.z },
                    false,
                    -1,
                    parcelData?.GetPolygonArray()
                );
            }
        }
    }

    /// <summary>
    /// 自由配置・道路沿い配置の地点を固定する。表示中の矩形と同じ吸着計算を使い、
    /// 中心・寸法・角度をplacement_stateで一括通知する。建設の確定はパネルが行う。
    /// </summary>
    public void OnTerrainClickDirect(Vector3 position)
    {
        if (!IsDirectPlacement || directCommitted || isMiddleDragging) return;
        OnTerrainHoverDirect(position);
        if (!directPreviewVisible) return;
        if (!isBuildingModeActive)
        {
            Debug.Log("建物建設モードが無効のため、区画外クリックを無視します");
            return;
        }

        if (isEditMode)
        {
            // 既存建物の編集(移設)中は直接配置を行わない。
            return;
        }

        // 国境検問所は type=TYPE_DEFAULT(0) なので、未選択はバリアントだけで判定する。
        if (agentVariant == 0)
        {
            Debug.LogWarning("バリアントが設定されていません");
            return;
        }

        // 直接配置へ移行するため、区画選択は解除する(区画選択中はゴースト非表示のガードがあるほか、
        // プレビューの BuildingSummary に古い parcel_id が残るのを防ぐ)。
        selectedParcelId = -1;

        // 地点を確定(フェーズB)。以降ホバーで中心は動かさず、確定点周りで回転する。
        directCommitted = true;
        directPreviewVisible = true;

        PlaySound(selectParcelSound);

        if (IpcClient.Instance != null)
        {
            // 表示中の中心・寸法・角度を同じ世代として通知する。
            EmitPlacementState();
        }
    }

    private void ResetBuildingConstruction()
    {
        // プレビュー形状取得コルーチンが実行中ならキャンセルする
        if (shapeCoroutine != null)
        {
            StopCoroutine(shapeCoroutine);
            shapeCoroutine = null;
        }

        isEditMode = false;
        editingBuildingId = -1;
        editingBuildingData = null;
        // 建設モードを抜けたら、表示レイヤーから選ぶ建設可能フィルタが
        // 直前のバリアントの区画種別に引きずられないよう未選択へ戻す。
        SelectedParcelType = 0;

        // 現在の建設値をデフォルト値にリセット
        currentConstructionRatio = defaultConstructionRatio;
        currentFloors = defaultFloors;
        // 外観バリアントもデフォルト(0.5)にリセット
        currentVisualVariant = 0.5f;
        currentVisualVariant2 = 0.5f;
        currentVisualVariant3 = 0.5f;
        // プレビュー形状キャッシュをクリア
        cachedPolygon = null;
        cachedCenterPoint = null;
        cachedHeight = 0f;
        selectedParcelId = -1;

        // 直接配置のゴースト状態をリセット
        ResetDirectPlacementState();

        ClearPreview();

        // インフラ施設の効果範囲オーバーレイも消す。
        if (effectRangeOverlay != null)
        {
            effectRangeOverlay.Clear();
        }

        Debug.Log("建物建設の状態をリセットしました");
    }

    /// <summary>
    /// IPC経由で建物形状変更を受信したときの処理。
    /// UI側がバックエンドのPOST /api/v1/buildings/previewから取得した形状データを
    /// 受け取り、直接3Dプレビューメッシュを生成する。
    /// FetchBuildingShape()は使わず、提供されたデータから直接メッシュを構築する。
    /// </summary>
    public void OnUpdateShapeFromIpc(float[][] polygon, int floors, float[] centerPoint, float height, int revision = -1)
    {
        if (!isBuildingModeActive) return;
        // 非同期応答で取消し済みの位置や古い寸法へ戻らないよう、直接配置は世代を照合する。
        if (revision >= 0 && (!IsDirectPlacement || !directCommitted || revision != placementRevision || floors != currentFloors)) return;
        if (revision < 0 && (IsDirectPlacement || (!isEditMode && selectedParcelId < 0))) return;
        if (isMiddleDragging) return;
        Debug.Log($"BuildingPlanManager: IPC経由で形状変更を受信 floors={floors}, height={height}");
        currentFloors = floors;

        if (polygon == null || polygon.Length < 3)
        {
            Debug.LogWarning("BuildingPlanManager: ポリゴンデータが不正です（頂点数 < 3）");
            return;
        }

        // プレビュー形状をキャッシュ。外観バリアント変更時にこのポリゴンから再生成する。
        cachedPolygon = polygon;
        cachedCenterPoint = centerPoint;
        cachedHeight = height;

        // 直接配置(フェーズB)の権威プレビュー(preview-direct由来)を受信した場合、
        // 中ボタン回転がこの中心点周りで矩形をローカル回転できるよう中心を保持する。
        if (directCommitted && centerPoint != null && centerPoint.Length >= 2)
        {
            directCenter = new Vector3(centerPoint[0], 0f, centerPoint[1]);
            directPreviewVisible = true;
        }

        BuildAndShowPreviewMesh(polygon, floors, centerPoint, height);
    }

    /// <summary>
    /// 与えられた底面ポリゴン・階数・中心・高さからプレビューメッシュを生成して表示する。
    /// IPC受信(OnUpdateShapeFromIpc)、キャッシュ再生成(RebuildPreviewFromCache)、
    /// 直接配置のローカル矩形ゴースト(BuildDirectRectanglePreview)で共有する。
    /// ポリゴンはバックエンドの実建物と同じ「center_point 相対座標」、中心はワールド座標で
    /// 受け取る(メッシュはローカル座標で生成され、center_point 位置に配置される)。
    /// キャッシュ更新や currentFloors の設定は呼び出し側の責務とする。
    /// </summary>
    private void BuildAndShowPreviewMesh(float[][] polygon, int floors, float[] centerPoint, float height)
    {
        if (polygon == null || polygon.Length < 3)
        {
            Debug.LogWarning("BuildingPlanManager: ポリゴンデータが不正です（頂点数 < 3）");
            return;
        }

        // ポリゴン座標をBuildingSummaryのpolygon形式(List<object>)で構築
        var polygonObjList = new List<object>();
        for (int i = 0; i < polygon.Length; i++)
        {
            if (polygon[i] != null && polygon[i].Length >= 2)
            {
                polygonObjList.Add(new List<object> { polygon[i][0], polygon[i][1] });
            }
        }

        BuildingSummary buildingData = new BuildingSummary
        {
            agent_id = -1,
            parcel_id = selectedParcelId > 0 ? selectedParcelId : 0,
            type = agentType,
            variant = agentVariant,
            bd_type = bdType,
            floors = floors,
            height = height,
            polygon = polygonObjList,
            structure = currentStructure,
            landmark = currentLandmark,
            visual_variant = currentVisualVariant,
            visual_variant2 = currentVisualVariant2,
            visual_variant3 = currentVisualVariant3,
            center_point = centerPoint != null && centerPoint.Length >= 2
                ? new List<object> { centerPoint[0], centerPoint[1] }
                : null
        };

        // BuildingMeshBuilderを使用してメッシュを生成
        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData);

        Vector3 centerPosition = ComputePreviewCenterPosition(polygon, centerPoint);
        SetBuildingPreviewMesh(meshResult, centerPosition, previewMaterial);
    }

    /// <summary>
    /// プレビューメッシュの配置位置(中心のワールド座標)を計算する。
    /// center_point があればそれを、なければ polygon の重心を用い、地面高さに合わせて
    /// Zファイティング回避のため少し浮かせる。
    /// </summary>
    private Vector3 ComputePreviewCenterPosition(float[][] polygon, float[] centerPoint)
    {
        if (centerPoint != null && centerPoint.Length >= 2)
        {
            float cx = centerPoint[0];
            float cz = centerPoint[1];
            float groundHeight = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
            return new Vector3(cx, groundHeight + 0.01f, cz);
        }

        List<Vector3> points = new List<Vector3>();
        for (int i = 0; i < polygon.Length; i++)
        {
            if (polygon[i] != null && polygon[i].Length >= 2)
            {
                points.Add(new Vector3(polygon[i][0], 0, polygon[i][1]));
            }
        }
        if (points.Count == 0) return Vector3.zero;

        Vector3 sum = Vector3.zero;
        foreach (var p in points) sum += p;
        Vector3 c = sum / points.Count;
        float gh = Utils.GetGroundHeight(new Vector3(c.x, 1000f, c.z));
        return new Vector3(c.x, gh + 0.01f, c.z);
    }

    /// <summary>
    /// IPC経由でインフラ施設の効果範囲を受信したときの処理。
    /// UI側が POST /api/v1/buildings/preview-effect-range から取得した領域データを
    /// 3Dオーバーレイ(EffectRangeOverlay)に反映する。payload がクリア指示なら表示を消す。
    /// </summary>
    public void OnUpdateEffectRangeFromIpc(EffectRangePayload payload)
    {
        EnsureEffectRangeOverlay();
        effectRangeOverlay.SetData(payload);
    }

    /// <summary>効果範囲オーバーレイの実体を(なければ)生成する。</summary>
    private void EnsureEffectRangeOverlay()
    {
        if (effectRangeOverlay != null) return;
        var overlayObj = new GameObject("EffectRangeOverlay");
        effectRangeOverlay = overlayObj.AddComponent<EffectRangeOverlay>();
    }

    public void SetFloors(int floors)
    {
        bool changed = currentFloors != floors;
        currentFloors = floors;
        if (changed) PlaySound(changeFloorsSound);
        if (IsDirectPlacement && directPreviewVisible) BuildDirectRectanglePreview();
        Debug.Log($"BuildingPlanManager: 階数を設定しました: {floors}");
        UpdateBuildingShape();
    }

    /// <summary>
    /// 外観バリアント(visual_variant1/2/3)を設定し、プレビューメッシュを再生成する。
    /// UIの外観乱数スライダー変更時に呼ばれる。バックエンドへは再取得せず、
    /// キャッシュしている底面ポリゴンから現在のvisual_variantでメッシュを再構築する。
    /// </summary>
    public void SetVisualVariant(float vv1, float vv2, float vv3)
    {
        currentVisualVariant = vv1;
        currentVisualVariant2 = vv2;
        currentVisualVariant3 = vv3;
        Debug.Log($"BuildingPlanManager: 外観バリアントを設定しました: vv1={vv1}, vv2={vv2}, vv3={vv3}");
        RebuildPreviewFromCache();
    }

    /// <summary>
    /// 構造種別を設定し、プレビューメッシュを再生成する。UIの構造選択変更時に呼ばれる。
    /// 構造はパーツ prefix の解決(a{variant}s{structure} など)に効くだけで底面形状は変わらないため、
    /// バックエンドへは再取得せずキャッシュした底面ポリゴンから再構築する。
    /// </summary>
    public void SetStructure(int structure)
    {
        currentStructure = structure;
        Debug.Log($"BuildingPlanManager: 構造種別を設定しました: structure={structure}");
        RebuildPreviewFromCache();
    }

    /// <summary>
    /// 観光名所の種別を設定し、プレビューメッシュを再生成する。テンプレート作成モードの名所選択で呼ばれる。
    /// 名所はパーツ prefix の解決(landmark-{model_prefix})に加えて敷地寸法・階数もマスターが正なので、
    /// 直接配置のゴーストと placement_state を名所の実寸で作り直す。
    /// 底面ポリゴン自体はバックエンドの権威プレビューが返すため、ここでは再取得しない。
    /// </summary>
    public void SetLandmark(int landmark)
    {
        currentLandmark = landmark;
        if (landmark > 0)
        {
            ApplyFootprintDefaults();
            RebuildAfterSizeChange();
            EmitPlacementState(false);
        }
        Debug.Log($"BuildingPlanManager: 観光名所を設定しました: landmark={landmark}");
        RebuildPreviewFromCache();
    }

    /// <summary>
    /// キャッシュした底面ポリゴンと現在のvisual_variantでプレビューメッシュを再生成する。
    /// 外観バリアント変更時に呼ばれる。バックエンド往復不要。
    /// キャッシュがない場合は何もしない（まだパーセル選択前など）。
    /// </summary>
    private void RebuildPreviewFromCache()
    {
        if (cachedPolygon == null || cachedPolygon.Length < 3)
        {
            // キャッシュがない場合はプレビュー表示できない
            return;
        }

        // キャッシュした底面ポリゴンと現在のvisual_variant/floorsでメッシュを再生成する。
        BuildAndShowPreviewMesh(cachedPolygon, currentFloors, cachedCenterPoint, cachedHeight);
    }

    // ======================================================================
    // 直接配置のリアルタイム矩形ゴースト
    // ======================================================================

    /// <summary>
    /// 直接配置のゴースト状態(パラメータ・確定フラグ・ドラッグ状態・スロットル)を初期化する。
    /// メッシュ自体はここでは消さない(全体リセットは ResetBuildingConstruction が ClearPreview で行う)。
    /// </summary>
    private void ResetDirectPlacementState()
    {
        ResetRoadAnchor();
        hasRawPlacementPosition = false;
        placementRevision++;
        directWidth = 0f;
        directDepth = 0f;
        directAngle = 0f;
        hasDirectParams = false;
        directCommitted = false;
        directPreviewVisible = false;
        directCenter = Vector3.zero;
        middleDrag.Cancel();
        lastGhostCenter = new Vector3(float.MaxValue, 0f, float.MaxValue);
        lastGhostAngle = float.NaN;
    }

    /// <summary>
    /// UI(パネル)から直接配置の矩形パラメータ(横幅・縦幅・回転角・階数)を受信する。
    /// edit_building:update_direct_params 経由。カーソル追従ゴースト/確定後プレビューの
    /// 寸法・回転をUnityに同期する。直接プレビュー表示中なら即再描画する。
    /// </summary>
    public void SetDirectParams(float width, float depth, float angle, int floors)
    {
        directWidth = width;
        directDepth = depth;
        currentFloors = floors;
        hasDirectParams = true;

        // 中ボタンドラッグ中はUnityが角度の権威。UIからのエコーで角度を上書きしない
        // (無駄な再描画とエコー往復を防ぐ)。
        if (!isMiddleDragging)
        {
            directAngle = angle;
        }

        // 直接プレビューが表示中(フェーズA:ゴースト or フェーズB:確定済み)なら即再描画。
        if (directPreviewVisible && isBuildingModeActive && !isEditMode)
        {
            BuildDirectRectanglePreview();
        }
    }

    /// <summary>
    /// 直接配置モードで空き地形をホバーしたときに呼ばれる(TerrainInputHandler.onHover)。
    /// 地点確定前(フェーズA)に限り、カーソル位置を中心とする矩形ゴーストを描画する。
    /// 確定後(フェーズB)は権威プレビューを動かさないため何もしない。
    /// </summary>
    public void OnTerrainHoverDirect(Vector3 pos)
    {
        lastRawPlacementPosition = pos;
        hasRawPlacementPosition = true;
        if (!IsDirectPlacement) return;
        if (isEditMode || !hasDirectParams || directCommitted) return;


        // 中ボタン回転中はゴーストの中心を動かさない(回転操作でカーソルが動いても
        // ゴーストが滑らないようアンカーする)。回転の再描画は Update() が行う。
        if (isMiddleDragging) return;

        directCenter = new Vector3(pos.x, 0f, pos.z);
        ResolveRoadPlacement(pos);

        // スロットル: 前回描画から一定距離動くか角度が変わった場合のみ再生成する
        // (毎フレームのメッシュ再生成によるGC/カクつきを避ける)。
        if (Vector3.Distance(directCenter, lastGhostCenter) < GhostRebuildDistance &&
            directAngle == lastGhostAngle && directPreviewVisible)
        {
            return;
        }

        BuildDirectRectanglePreview();
    }

    /// <summary>
    /// 直接配置のホバーゴーストを消す(TerrainInputHandler.onHoverEnd)。
    /// 地点確定後(フェーズB)は権威プレビューを消さないため何もしない。
    /// </summary>
    public void HideDirectGhost()
    {
        if (directCommitted) return;
        if (!directPreviewVisible) return;

        if (previewMeshFilter != null)
        {
            previewMeshFilter.mesh = null;
        }
        directPreviewVisible = false;
        lastGhostCenter = new Vector3(float.MaxValue, 0f, float.MaxValue);
        lastGhostAngle = float.NaN;
    }

    /// <summary>
    /// directCenter を中心に、directWidth×directDepth の矩形を directAngle だけ回転させた
    /// 底面ポリゴンをローカル計算し、プレビューメッシュを生成・表示する(IPC送信なし)。
    /// ポリゴンはバックエンドの権威プレビュー(preview-direct)と同じ「center_point 相対座標」で、
    /// 中心はワールド座標で渡す(既存の描画経路 BuildAndShowPreviewMesh の規約と一致させる)。
    /// </summary>
    private void BuildDirectRectanglePreview()
    {
        if (directWidth <= 0f || directDepth <= 0f) return;

        float cx = directCenter.x;
        float cz = directCenter.z;
        float halfW = directWidth * 0.5f;
        float halfD = directDepth * 0.5f;
        float rad = directAngle * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);

        // 中心周りの回転矩形の4隅(center_point 相対座標)。ローカル(±halfW, ±halfD)を回転する。
        Vector2[] local =
        {
            new Vector2(-halfW, -halfD),
            new Vector2( halfW, -halfD),
            new Vector2( halfW,  halfD),
            new Vector2(-halfW,  halfD),
        };
        float[][] polygon = new float[4][];
        for (int i = 0; i < 4; i++)
        {
            float rx = local[i].x * cos - local[i].y * sin;
            float rz = local[i].x * sin + local[i].y * cos;
            polygon[i] = new float[] { rx, rz };
        }

        // BuildingMeshBuilder は floors から高さを再算出するため、height は参考値の概算でよい。
        float heightEstimate = Mathf.Max(1, currentFloors) * 3.0f;
        cachedPolygon = polygon;
        cachedCenterPoint = new float[] { cx, cz };
        cachedHeight = heightEstimate;
        BuildAndShowPreviewMesh(polygon, currentFloors, new float[] { cx, cz }, heightEstimate);

        directPreviewVisible = true;
        lastGhostCenter = directCenter;
        lastGhostAngle = directAngle;
    }

    /// <summary>
    /// 中ボタンドラッグによる直接配置の回転を毎フレーム処理する。
    /// フェーズA/B いずれでもローカル即時回転し、ボタンリリース時に確定角度を
    /// edit_building:placement_state でUIへ1回だけ通知する(ドラッグ中は送信しない)。
    /// </summary>
    private void Update()
    {
        if (!UpdatePlacementInput()) return;
        if (PlacementMode == BuildingPlacementMode.Roadside) return;
        if (!isBuildingModeActive || isEditMode || !hasDirectParams || !directPreviewVisible)
        {
            // ドラッグ中にモードが外れた等の場合は状態を落とす。
            middleDrag.Cancel();
            return;
        }

        switch (middleDrag.Update(out int deltaDegrees))
        {
            case MiddleDragRotation.Result.Started:
                placementRevision++;
                break;
            case MiddleDragRotation.Result.Rotated:
                directAngle += deltaDegrees;
                BuildDirectRectanglePreview();
                break;
            case MiddleDragRotation.Result.Released:
                if (IpcClient.Instance != null)
                {
                    EmitPlacementState();
                }
                break;
        }
    }

    private void UpdateBuildingShape()
    {
        if (selectedParcelId <= 0) return;
        if (shapeCoroutine != null)
        {
            StopCoroutine(shapeCoroutine);
        }
        shapeCoroutine = StartCoroutine(FetchBuildingShape());
    }

    private IEnumerator FetchBuildingShape()
    {
        string url = $"{ApiClient.BASE_URL}/buildings/shape?parcel_id={selectedParcelId}&floors={currentFloors}&building_ratio={currentConstructionRatio.ToString(CultureInfo.InvariantCulture)}&building_variant={agentVariant}";
        Debug.Log($"BuildingPlanManager: 建物形状取得URL: {url}");
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                string json = request.downloadHandler.text;
                BuildingShape shape = JsonConvert.DeserializeObject<BuildingShape>(json);
                if (shape != null && shape.polygon != null)
                {
                    // ポリゴン座標を BuildingSummary の polygon (List<object>) 形式で構築
                    var polygonObjList = new List<object>();
                    // 同時にfloat[][]形式も構築し、外観バリアント変更時の再生成用にキャッシュする
                    var polygonFloatArray = new float[shape.polygon.Count][];
                    for (int i = 0; i < shape.polygon.Count; i++)
                    {
                        var p = shape.polygon[i];
                        if (p.Count >= 2)
                        {
                            polygonObjList.Add(new List<object> { p[0], p[1] });
                            polygonFloatArray[i] = new float[] { p[0], p[1] };
                        }
                    }

                    // プレビュー形状をキャッシュ。外観バリアント変更時にこのポリゴンから再生成する。
                    cachedPolygon = polygonFloatArray;
                    cachedHeight = shape.height;
                    if (shape.center_point != null && shape.center_point.Count >= 2)
                    {
                        cachedCenterPoint = new float[] { shape.center_point[0], shape.center_point[1] };
                    }

                    BuildingSummary buildingData = new BuildingSummary
                    {
                        agent_id = -1,
                        parcel_id = selectedParcelId,
                        type = agentType,
                        variant = agentVariant,
                        bd_type = bdType,
                        floors = currentFloors,
                        height = shape.height,
                        polygon = polygonObjList,
                        structure = currentStructure,
                        landmark = currentLandmark,
                        visual_variant = currentVisualVariant,
                        visual_variant2 = currentVisualVariant2,
                        visual_variant3 = currentVisualVariant3,
                        center_point = shape.center_point != null && shape.center_point.Count >= 2 
                            ? new List<object> { shape.center_point[0], shape.center_point[1] }
                            : null
                    };

                    // BuildingMeshBuilderを使用してメッシュを生成（プレビューモード）
                    BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(
                        buildingData
                    );

                    // 中心位置を計算
                    Vector3 centerPosition = Vector3.zero;
                    if (shape.center_point != null && shape.center_point.Count >= 2)
                    {
                        float cx = shape.center_point[0];
                        float cz = shape.center_point[1];
                        // 地面の高さを取得して設定（Zファイティングを防ぐため少し浮かせる）
                        float groundHeight = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
                        float buildingBaseHeight = groundHeight + 0.01f;
                        centerPosition = new Vector3(cx, buildingBaseHeight, cz);
                    }
                    else
                    {
                        // center_pointがない場合はpolygonから重心を計算
                        List<Vector3> points = new List<Vector3>();
                        foreach (var p in shape.polygon)
                        {
                            if (p.Count >= 2)
                            {
                                float x = p[0];
                                float z = p[1];
                                points.Add(new Vector3(x, 0, z));
                            }
                        }
                        if (points.Count > 0)
                        {
                            Vector3 sum = Vector3.zero;
                            foreach (var p in points)
                            {
                                sum += p;
                            }
                            centerPosition = sum / points.Count;
                            // 地面の高さを取得して設定（Zファイティングを防ぐため少し浮かせる）
                            float groundHeight = Utils.GetGroundHeight(new Vector3(centerPosition.x, 1000f, centerPosition.z));
                            float buildingBaseHeight = groundHeight + 0.01f;
                            centerPosition = new Vector3(centerPosition.x, buildingBaseHeight, centerPosition.z);
                        }
                    }

                    SetBuildingPreviewMesh(meshResult, centerPosition, previewMaterial);
                }
            }
            else
            {
                Debug.LogError($"建物形状取得に失敗: {request.error}");
            }
        }
    }

    /// <summary>
    /// BuildingMeshResultからプレビュー用メッシュを設定する。
    /// </summary>
    /// <param name="meshResult">建物メッシュ生成結果</param>
    /// <param name="centerPosition">建物の中心位置（ワールド座標）</param>
    private void SetBuildingPreviewMesh(BuildingMeshResult meshResult, Vector3 centerPosition, Material previewMaterial = null)
    {
        if (meshResult == null || meshResult.Mesh == null)
        {
            Debug.LogWarning("BuildingPlanManager: meshResult が null または Mesh が null です。");
            return;
        }

        if (previewMeshFilter == null || previewMeshRenderer == null)
        {
            Debug.LogWarning("BuildingPlanManager: Preview Mesh Components are missing.");
            return;
        }

        if (previewMesh != null && previewMesh != meshResult.Mesh) Destroy(previewMesh);
        previewMesh = meshResult.Mesh;
        previewMeshFilter.mesh = previewMesh;

        // マテリアルを設定
        if (previewMaterial != null)
        {
            // プレビュー用マテリアルが指定されている場合はそれを全サブメッシュに適用
            int subMeshCount = previewMesh.subMeshCount;
            Material[] mats = new Material[subMeshCount];
            for (int i = 0; i < subMeshCount; i++)
            {
                mats[i] = previewMaterial;
            }
            previewMeshRenderer.sharedMaterials = mats;
        }
        else if (meshResult.Materials != null && meshResult.Materials.Length > 0)
        {
            previewMeshRenderer.sharedMaterials = meshResult.Materials;
        }

        // 位置を設定
        previewMeshFilter.transform.position = centerPosition;
    }

    /// <summary>
    /// 指定された AudioClip を再生する。
    /// クリップまたは AudioSource が未設定の場合は何もしない。
    /// </summary>
    private void PlaySound(AudioClip clip)
    {
        if (clip == null || audioSource == null) return;
        audioSource.PlayOneShot(clip);
    }

    /// <summary>
    /// プレビューをクリア(メッシュを消去)
    /// </summary>
    private void ClearPreview()
    {
        if (previewMesh != null)
        {
            Destroy(previewMesh);
            previewMesh = null;
        }
        if (previewMeshFilter != null)
        {
            previewMeshFilter.mesh = null;
        }
        // プレビュー形状キャッシュもクリア
        cachedPolygon = null;
        cachedCenterPoint = null;
        cachedHeight = 0f;
    }

    // ======================================================================
    // 建物種類と土地区画種類の互換性ユーティリティ
    // ======================================================================

    /// <summary>
    /// 建物が立地できる土地区画種類（PARCEL_*）を返す。
    /// 建設モードで選択中のバリアントがあれば agent_master.parcel_type をそのまま使い、
    /// 無い場合（表示レイヤーから bd_type だけでフィルタを選んだ場合）は
    /// 港湾=沿岸 / それ以外=陸地 という既定へ倒す。
    /// </summary>
    public static int ResolveRequiredParcelType(int bdType)
    {
        if (SelectedParcelType > 0) return SelectedParcelType;
        return (bdType & Constants.BUILDING_PORT) != 0 ? Constants.PARCEL_SHORE : Constants.PARCEL_LAND;
    }

    /// <summary>
    /// 土地区画種類（parcel_type）がその建物の立地条件を満たすかを判定する。
    /// </summary>
    public static bool IsParcelTypeAllowed(int parcelType, int bdType)
    {
        return parcelType == ResolveRequiredParcelType(bdType);
    }

    /// <summary>
    /// 建物種類（bd_type）に対応するパーセル表示フィルタのsubMode文字列を返す。
    /// IpcViewCoordinator の _layerSubModes に設定するために使用する。
    /// </summary>
    public static string BdTypeToParcelFilterSubMode(int bdType)
    {
        if (bdType == Constants.BUILDING_APARTMENT) return "BUILDING_APARTMENT";
        if (bdType == Constants.BUILDING_HOUSE) return "BUILDING_HOUSE";
        if (bdType == Constants.BUILDING_RETAIL) return "BUILDING_RETAIL";
        if (bdType == Constants.BUILDING_FACTORY) return "BUILDING_FACTORY";
        if (bdType == Constants.BUILDING_OFFICE) return "BUILDING_OFFICE";
        if (bdType == Constants.BUILDING_FARM) return "BUILDING_FARM";
        if (bdType == Constants.BUILDING_MINE) return "BUILDING_MINE";
        if (bdType == Constants.BUILDING_PORT) return "BUILDING_PORT";
        if (bdType == Constants.BUILDING_OTHER) return "BUILDING_OTHER";
        if (bdType == Constants.BUILDING_PUBLIC) return "BUILDING_PUBLIC";
        return null;
    }
}
