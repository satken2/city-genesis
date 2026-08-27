using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// 施設の接続端子ノード（インフラ接続点 = infra_node_id、走行網接続点 = depot_node_id）の
/// ハイライト表示とドラッグ編集を司るシングルトンマネージャー。
///
/// - <see cref="ShowConnectors"/>: AgentDetailPanel が施設を表示している間、接続端子ノードの
///   上空に色付きマーカー（回転＋ボビング）を表示する。読み取り専用（ドラッグ不可）。
/// - <see cref="EnterEditMode"/>: STATE_EDIT_CONNECTOR で呼ばれる。マーカーに加えて対象ノードへ
///   <see cref="NodeInputHandler"/> と端子ロールを付与し、MODE_EDIT でのドラッグ→PATCH を可能にする。
///   端子は施設の敷地内の設備のため、ドラッグ移動は施設が立地する土地区画の範囲内へ
///   クランプされ、編集中は対象区画がハイライト表示される（区画未ロード時はクランプ無効で、
///   区画外への移動はバックエンドの PATCH バリデーションが拒否する）。
/// - <see cref="Hide"/> / <see cref="ExitEditMode"/>: マーカーを消してノードの表示状態を元に戻す。
///
/// インフラと走行網の接続点が同一ノードを指す共用接続ノード（電化した鉄道車両基地など）では
/// マーカーは1つだけ表示する。
/// </summary>
public class ConnectorEditManager : MonoBehaviour
{
    public static ConnectorEditManager Instance { get; private set; }

    // 走行網接続点（引き込み線路など）の色。インフラ接続点（赤）と区別するために使用。
    private static readonly Color DepotColor = new Color(1f, 0.6f, 0.1f);

    // 編集時、平たいノード実体(scale 0.5,0.01,0.5)だけでは接続点の位置が地表で分かりにくいため、
    // 目印の台座として少し大きいサイズに一時的に拡大する(掴み判定はマーカーのスクリーン座標で
    // 行うため、このスケールは当たり判定には影響しない)。
    private static readonly Vector3 EditClickableScale = new Vector3(2f, 0.5f, 2f);

    // マーカー(逆四角錐)を掴めるスクリーン座標上の最低ヒット半径(ピクセル)。
    // マーカーはノード実体の上空に浮くオーバーレイのため、コライダーへのレイキャストではなく
    // 「ノード(地表)〜マーカー天面を結ぶスクリーン上の線分」との距離で掴み判定を行う。
    // 実際の半径はズーム(マーカーの見かけサイズ)に応じて拡大され、この値は下限となる。
    private const float ConnectorHitRadius = 45f;

    // マーカー天面のノードからの高さ(m)。ヒット判定用の線分の上端に使う。
    private const float MarkerTopHeight =
        InfrastructureMarkerUtility.APEX_OFFSET + InfrastructureMarkerUtility.HEIGHT;

    private class MarkerEntry
    {
        public GameObject node;
        public bool addedHandler; // このマネージャーが NodeInputHandler を後付けしたか
        public Vector3 originalScale; // 編集用に拡大する前のノードスケール
        public bool scaled;          // ノードスケールを拡大したか
    }

    private readonly Dictionary<int, MarkerEntry> entries = new Dictionary<int, MarkerEntry>();
    private int infraNodeId;
    private int depotNodeId;
    private bool editMode;

    // 編集対象施設の agent_id と、その施設が立地する土地区画ID(-1=未解決)。
    // 接続端子は施設の敷地内にある設備という位置づけのため、ドラッグ移動を
    // この区画の範囲内へクランプする(バックエンドも区画外への PATCH を
    // バリデーションで拒否する権威チェックを持つ)。区画が未ロード等で解決
    // できない場合はクランプせず、バックエンドの拒否＋既存ロールバックに委ねる。
    private int ownerAgentId;
    private int ownerParcelId = -1;

    private ParcelManager parcelManager;

    // アクティブにドラッグ中の端子ノードID(0=なし)。ドラッグ終了(ProcessMouseDragEnd)でクリアされる。
    private int draggingNodeId;

    // マーカーの描画基準にノード実体の transform.position を使う端子ノードID(0=なし)。
    // ドラッグ中〜ドロップ直後の PATCH 完了までは PathManager のキャッシュ座標が旧位置の
    // ままのため(GetNodeData はキャッシュを返す)、掴んだノードは transform に追従させる。
    // ドロップ後もマーカー再構築(ClearMarkers)まで保持し、PATCH 往復中に旧位置へ一瞬戻る
    // ちらつきを防ぐ(PATCH 完了後は transform とキャッシュの絶対座標が一致するため無害)。
    private int pinnedNodeId;

    // このドラッグジェスチャで掴み判定を試行済みか。
    // InputHandler の地形ドラッグは掴み対象を決めずに開始され、ProcessMouseDrag の初回呼び出しで
    // 遅延的に掴み判定を行う(RoadPlanManager.ProcessMouseDrag と同じ方式)。判定に失敗した場合、
    // ジェスチャ終了まで再試行しない(ドラッグ途中でマーカー上を横切った際に誤って掴まないため)。
    private bool grabAttemptedThisGesture;

    /// <summary>接続点編集モード中(ドラッグ移動を受け付ける)かどうか。</summary>
    public bool IsEditMode => editMode;

    private PathManager pathManager;

    // オーバーレイ描画用コンポーネント（RoadPlanManagerと同様にOverlayLineマテリアルを使用）
    private OverlayMeshRenderer overlayRenderer;
    private GameObject overlayMeshObj;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            ClearMarkers();
            if (overlayMeshObj != null)
            {
                Destroy(overlayMeshObj);
            }
            Instance = null;
        }
    }

    private PathManager PathMgr => pathManager != null
        ? pathManager
        : (pathManager = FindAnyObjectByType<PathManager>());

    private void EnsureOverlayRenderer()
    {
        if (overlayMeshObj == null)
        {
            overlayMeshObj = new GameObject("ConnectorOverlayMesh");
            overlayRenderer = overlayMeshObj.AddComponent<OverlayMeshRenderer>();
            Material mat = new Material(Shader.Find("Custom/OverlayLine"));
            // 地形に埋もれず常に手前に出す。
            mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            overlayRenderer.Initialize(mat);
        }
    }

    /// <summary>
    /// 接続端子ノードを読み取り専用でハイライト表示する。
    /// infraNodeId / depotNodeId が同一（共用接続ノード）の場合はマーカーを1つだけ表示する。
    /// いずれも 0 以下の場合は何も表示しない。
    /// </summary>
    public void ShowConnectors(int infraNodeId, int depotNodeId)
    {
        ClearMarkers();
        editMode = false;
        this.infraNodeId = infraNodeId;
        this.depotNodeId = depotNodeId;

        bool shared = infraNodeId > 0 && infraNodeId == depotNodeId;
        if (shared)
        {
            HighlightNode(infraNodeId);
        }
        else
        {
            if (infraNodeId > 0) HighlightNode(infraNodeId);
            if (depotNodeId > 0) HighlightNode(depotNodeId);
        }
    }

    /// <summary>
    /// 接続点編集モードに入る。ハイライト表示に加え、対象ノードをドラッグ移動可能にする。
    /// agentId は編集対象の施設。施設が立地する土地区画を解決してハイライト表示し、
    /// 端子のドラッグ移動をその区画の範囲内へ制限する。
    /// </summary>
    public void EnterEditMode(int infraNodeId, int depotNodeId, int agentId)
    {
        ShowConnectors(infraNodeId, depotNodeId);
        editMode = true;
        ResolveOwnerParcel(agentId, infraNodeId);
        foreach (var kv in entries)
        {
            EnsureDraggable(kv.Key, kv.Value);
        }
        Debug.Log($"ConnectorEditManager: 接続点編集モード開始 -> infra={infraNodeId}, depot={depotNodeId}, agent={agentId}, parcel={ownerParcelId}");
    }

    /// <summary>
    /// 編集モードを終了して読み取り専用のハイライト表示に戻す（パネルは開いたまま）。
    /// </summary>
    public void ExitEditMode()
    {
        if (editMode)
        {
            ClearOwnerParcel();
            // 再ハイライト（NodeInputHandler の後付けは残すが、editMode=false により
            // IsMouseOverConnector が false を返すようになり、ドラッグ掴みは無効化される）。
            ShowConnectors(infraNodeId, depotNodeId);
        }
    }

    /// <summary>
    /// マーカーを全て消し、ノードの表示状態を元に戻す。パネルを閉じた際に呼ばれる。
    /// </summary>
    public void Hide()
    {
        ClearMarkers();
        ClearOwnerParcel();
        editMode = false;
        infraNodeId = 0;
        depotNodeId = 0;
    }

    /// <summary>
    /// 編集対象施設の立地区画を解決し、可動範囲として区画ハイライトを表示する。
    /// agentId が未指定(0以下)の場合は端子ノードIDからの逆引き
    /// (BuildingManager.TryGetInfraTerminalOwner)にフォールバックする。
    /// 施設・区画が未ロード等で解決できなければ ownerParcelId = -1 のまま
    /// (クランプ無効。移動可否はバックエンドの権威チェックに委ねる)。
    /// </summary>
    private void ResolveOwnerParcel(int agentId, int infraNodeId)
    {
        ownerAgentId = agentId;
        ownerParcelId = -1;

        var bm = BuildingManager.Instance;
        BuildingSummary summary = null;
        if (bm != null)
        {
            if (agentId > 0)
            {
                summary = bm.GetBuildingSummary(agentId);
            }
            if (summary == null && infraNodeId > 0)
            {
                bm.TryGetInfraTerminalOwner(infraNodeId, out summary);
            }
        }
        if (summary == null)
        {
            Debug.LogWarning($"ConnectorEditManager: 施設 {agentId} が未ロードのため可動範囲(区画)を解決できません(クランプ無効)");
            return;
        }
        if (summary.parcel_id <= 0)
        {
            // 区画を持たない施設(生成時に center_point フォールバックした端子)は制限なし。
            return;
        }

        ownerParcelId = summary.parcel_id;
        ownerAgentId = summary.agent_id;

        // 可動範囲をプレイヤーへ見せるため、対象区画をハイライト表示する。
        var pm = ParcelMgr;
        if (pm != null)
        {
            pm.SetHighlightedParcel(ownerParcelId);
        }
    }

    /// <summary>区画ハイライトを解除し、可動範囲の解決状態をリセットする。</summary>
    private void ClearOwnerParcel()
    {
        if (ownerParcelId > 0)
        {
            var pm = ParcelMgr;
            if (pm != null)
            {
                pm.ClearHighlightedParcel();
            }
        }
        ownerAgentId = 0;
        ownerParcelId = -1;
    }

    /// <summary>
    /// ドラッグ座標を施設の立地区画の範囲内へクランプする。
    /// 区画が未解決・未ロードの場合は入力座標をそのまま返す(バックエンドが区画外への
    /// PATCH を拒否し、NodeInputHandler.SaveNodePosition のロールバックで元位置へ戻る)。
    /// </summary>
    private Vector3 ClampToOwnerParcel(Vector3 worldPos)
    {
        if (ownerParcelId <= 0) return worldPos;
        var pm = ParcelMgr;
        if (pm == null) return worldPos;
        if (pm.TryClampPointToParcel(ownerParcelId, worldPos, out Vector3 clamped))
        {
            return clamped;
        }
        return worldPos;
    }

    private ParcelManager ParcelMgr => parcelManager != null
        ? parcelManager
        : (parcelManager = FindAnyObjectByType<ParcelManager>());

    /// <summary>
    /// 指定スクリーン座標が接続端子マーカー(逆四角錐)またはその直下(ノードまでの垂直の柱)の
    /// 上にあるか判定する。CameraController がカメラドラッグを開始しないための判定と、
    /// ドラッグの掴み判定の両方で同じ領域を使う(編集モード中のみ有効)。
    /// </summary>
    public bool IsMouseOverConnector(Vector2 screenPos, Camera cam)
    {
        if (!editMode) return false;
        return HitTestConnector(screenPos, cam, "IsMouseOverConnector") > 0;
    }

    /// <summary>ドラッグを開始する。カーソル下のマーカーの端子ノードを掴む。</summary>
    public void StartDrag(Vector2 screenPos)
    {
        TryBeginDrag(screenPos);
    }

    /// <summary>
    /// 指定スクリーン座標のマーカーを掴んでドラッグを開始する。掴めなければ false。
    /// 実際のノード移動と PATCH 送信は NodeInputHandler に委譲する(端子ロールで /nodes/{id} を更新)。
    /// </summary>
    private bool TryBeginDrag(Vector2 screenPos)
    {
        if (!editMode)
        {
            Debug.Log("ConnectorEditManager: TryBeginDrag 編集モードではないため掴めません");
            return false;
        }
        int nodeId = HitTestConnector(screenPos, Camera.main, "TryBeginDrag");
        if (nodeId <= 0) return false;
        if (!entries.TryGetValue(nodeId, out var e) || e.node == null) return false;

        draggingNodeId = nodeId;
        pinnedNodeId = nodeId;
        var handler = e.node.GetComponent<NodeInputHandler>();
        handler?.onDragStart(InputMode.MODE_EDIT);
        Debug.Log($"ConnectorEditManager: 端子ノード {nodeId} のドラッグを開始しました");
        return true;
    }

    /// <summary>
    /// ドラッグ中。掴んでいる端子ノードをマウス位置(地形XZ)へ移動する。
    ///
    /// InputHandler の地形ドラッグは「掴み対象を決めずに」開始されることに注意。
    /// レイキャストが地形コライダーにヒットした場合、ドラッグは StartTerrainDrag ではなく
    /// pendingClick→StartDrag(TERRAIN) 経由で始まり、マネージャ側の StartDrag は呼ばれない。
    /// そのため初回呼び出しで遅延的に掴み判定を行う(RoadPlanManager.ProcessMouseDrag と同じ方式)。
    /// </summary>
    public void ProcessMouseDrag(Vector3 worldPos)
    {
        if (draggingNodeId <= 0)
        {
            if (grabAttemptedThisGesture) return;
            grabAttemptedThisGesture = true;
            if (!TryBeginDrag(InputUtil.MousePosition)) return;
        }
        if (!entries.TryGetValue(draggingNodeId, out var e) || e.node == null) return;

        // 端子は施設の敷地内の設備のため、ドラッグ位置を立地区画の範囲内へクランプする。
        worldPos = ClampToOwnerParcel(worldPos);

        var handler = e.node.GetComponent<NodeInputHandler>();
        handler?.onDragUpdate(worldPos);

        // InputHandler から渡される座標は Y=0 で、onDragUpdate も Y を変更しないため、
        // ここで「現在XZ位置の地形高さ + 相対高さ」へ補正して起伏地形に追従させる
        // (相対高さの考え方はルートAGENTS.md参照)。
        ApplyTerrainFollowY(e.node, draggingNodeId);
    }

    /// <summary>ドラッグ終了。移動が確定していれば NodeInputHandler が PATCH を送信する。</summary>
    public void ProcessMouseDragEnd(Vector3 worldPos)
    {
        grabAttemptedThisGesture = false;
        if (draggingNodeId <= 0) return;
        if (entries.TryGetValue(draggingNodeId, out var e) && e.node != null)
        {
            // 確定位置も立地区画の範囲内へクランプする(ドラッグ中と同じ制限)。
            worldPos = ClampToOwnerParcel(worldPos);

            // onDragEnd は transform.position.y を最終Yとして採用するため、
            // 最終XZ位置での「地形高さ + 相対高さ」を先に transform へ確定させておく。
            Vector3 p = e.node.transform.position;
            p.x = worldPos.x;
            p.z = worldPos.z;
            e.node.transform.position = p;
            ApplyTerrainFollowY(e.node, draggingNodeId);

            var handler = e.node.GetComponent<NodeInputHandler>();
            handler?.onDragEnd(worldPos);
            Debug.Log($"ConnectorEditManager: 端子ノード {draggingNodeId} のドラッグを終了 -> {e.node.transform.position}");
        }
        // pinnedNodeId は保持し(PATCH往復中のちらつき防止)、アクティブなドラッグだけを解除する。
        draggingNodeId = 0;
    }

    /// <summary>
    /// ノードのY座標を「現在XZ位置の地形高さ + バックエンド上の相対高さ」へ補正する。
    /// ドラッグで起伏を横切った際に、端子が地形に埋まったり浮いたりしないようにする。
    /// </summary>
    private void ApplyTerrainFollowY(GameObject nodeGO, int nodeId)
    {
        float rel = 0f;
        var pm = PathMgr;
        var nd = pm != null ? pm.GetNodeData(nodeId) : null;
        if (nd != null && nd.position != null && nd.position.Length >= 3)
        {
            rel = nd.position[1];
        }
        Vector3 p = nodeGO.transform.position;
        p.y = HeightmapTextureManager.GetTerrainHeight(new Vector3(p.x, 1000f, p.z)) + rel;
        nodeGO.transform.position = p;
    }

    /// <summary>
    /// スクリーン座標に最も近いマーカーの端子ノードIDを返す(ヒット領域内。無ければ0)。
    /// ヒット領域は「ノード(地表)からマーカー天面までを結ぶスクリーン上の線分」の周囲で、
    /// 半径はズーム(マーカーの見かけサイズ)に応じて拡大される(最低 ConnectorHitRadius px)。
    /// マーカー中心1点との距離判定だと、ズームインでマーカーが大きく映るほど四角錐の上でも
    /// ヒットしなくなるため、線分+可変半径で「四角錐そのもの/その真下の空間」全体を掴めるようにする。
    /// </summary>
    private int HitTestConnector(Vector2 screenPos, Camera cam, string debugContext)
    {
        if (cam == null)
        {
            Debug.LogWarning($"ConnectorEditManager: HitTest({debugContext}) カメラが取得できません");
            return 0;
        }
        if (entries.Count == 0)
        {
            Debug.Log($"ConnectorEditManager: HitTest({debugContext}) マーカーエントリが0件です(ノード未ロード?)");
            return 0;
        }

        int best = 0;
        float bestDist = float.MaxValue;
        foreach (var kv in entries)
        {
            if (kv.Value == null || kv.Value.node == null) continue;

            Vector3 nodePos = GetNodeWorldPos(kv.Key, kv.Value);
            Vector3 spNode = cam.WorldToScreenPoint(nodePos);
            Vector3 spTop = cam.WorldToScreenPoint(nodePos + Vector3.up * MarkerTopHeight);
            if (spNode.z < 0f || spTop.z < 0f) continue; // カメラ背面

            Vector2 a = new Vector2(spNode.x, spNode.y);
            Vector2 b = new Vector2(spTop.x, spTop.y);
            float d = DistancePointToSegment(screenPos, a, b);

            // 見かけの大きさに応じたヒット半径: 柱の画面投影長からピクセル/メートルを概算し、
            // 四角錐の底面半径ぶんの横幅(+余裕1.5倍)を確保する。遠距離では最低半径にフォールバック。
            float pxPerMeter = Vector2.Distance(a, b) / MarkerTopHeight;
            float radius = Mathf.Max(ConnectorHitRadius, pxPerMeter * InfrastructureMarkerUtility.BASE_HALF * 1.5f);

            // 掴み判定の失敗原因を特定するためのデバッグログ(クリック時のみ呼ばれるため常時出力)
            Debug.Log($"ConnectorEditManager: HitTest({debugContext}) node={kv.Key} 距離={d:F1}px 半径={radius:F1}px 線分={a}→{b} マウス={screenPos}");

            if (d <= radius && d < bestDist)
            {
                bestDist = d;
                best = kv.Key;
            }
        }
        return best;
    }

    /// <summary>点 p からスクリーン上の線分 [a,b] までの距離(ピクセル)。</summary>
    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 1e-6f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>
    /// 端子ノードのマーカーを描く基準となるワールド座標を返す。
    /// 通常は PathManager のノードデータ(地形高さを考慮した絶対座標)を使うが、
    /// ドラッグ中〜PATCH 完了までキャッシュが旧位置のままのノード(pinnedNodeId)は transform を使う。
    /// </summary>
    private Vector3 GetNodeWorldPos(int nodeId, MarkerEntry entry)
    {
        if (nodeId == pinnedNodeId && entry.node != null)
        {
            return entry.node.transform.position;
        }
        var pm = PathMgr;
        if (pm != null)
        {
            var nd = pm.GetNodeData(nodeId);
            if (nd != null)
            {
                return nd.GetAbsolutePosition();
            }
        }
        return entry.node != null ? entry.node.transform.position : Vector3.zero;
    }

    private void HighlightNode(int nodeId)
    {
        if (nodeId <= 0 || entries.ContainsKey(nodeId)) return;
        var pm = PathMgr;
        if (pm == null) return;

        GameObject nodeGO = pm.GetNodeObject(nodeId);
        if (nodeGO == null)
        {
            Debug.LogWarning($"ConnectorEditManager: ノード {nodeId} の GameObject が見つかりません（未ロード）");
            return;
        }

        // 通常は非表示（ShowNodes=false）のノードでも、接続点として見せるため強制表示する。
        nodeGO.SetActive(true);

        entries[nodeId] = new MarkerEntry
        {
            node = nodeGO,
            addedHandler = false,
            originalScale = nodeGO.transform.localScale,
            scaled = false,
        };
    }

    private void EnsureDraggable(int nodeId, MarkerEntry entry)
    {
        if (entry == null || entry.node == null) return;

        // 端子ロールを設定（NodeInputHandler.SaveNodePosition が PATCH /nodes/{id} を通すために必要）。
        var idInfo = entry.node.GetComponent<ObjectIdInfo>();
        if (idInfo != null && !NodeInputHandler.IsTerminalRole(idInfo.role))
        {
            idInfo.role = "connector_terminal";
        }

        // NodeInputHandler が無ければ後付けする（非電化の路線専用ノードなど）。
        var handler = entry.node.GetComponent<NodeInputHandler>();
        if (handler == null)
        {
            handler = entry.node.AddComponent<NodeInputHandler>();
            handler.nodeId = nodeId;
            entry.addedHandler = true;
        }

        // 掴み判定自体はマーカー(逆四角錐)のスクリーン座標で行う(IsMouseOverConnector)ため
        // ノードのコライダーサイズには依存しないが、平たいノード実体だけでは接続点の位置が
        // 分かりにくいため、編集中は地表側の目印として少し大きい台座サイズに拡大しておく。
        if (!entry.scaled)
        {
            entry.node.transform.localScale = EditClickableScale;
            entry.scaled = true;
        }
    }

    private void ClearMarkers()
    {
        draggingNodeId = 0;
        pinnedNodeId = 0;
        grabAttemptedThisGesture = false;
        var pm = PathMgr;
        bool showNodes = pm != null && pm.ShowNodes;
        foreach (var kv in entries)
        {
            if (kv.Value.node != null)
            {
                // 編集用に拡大したスケールを元に戻す。
                if (kv.Value.scaled)
                {
                    kv.Value.node.transform.localScale = kv.Value.originalScale;
                }
                // ノードの表示状態を ShowNodes 設定に戻す。
                if (kv.Value.node.activeSelf != showNodes)
                {
                    kv.Value.node.SetActive(showNodes);
                }
            }
        }
        entries.Clear();

        if (overlayRenderer != null)
        {
            overlayRenderer.ClearMesh();
        }
    }

    private void LateUpdate()
    {
        if (entries.Count == 0)
        {
            if (overlayRenderer != null)
            {
                overlayRenderer.ClearMesh();
            }
            return;
        }

        EnsureOverlayRenderer();
        overlayRenderer.BeginFrame();

        foreach (var kv in entries)
        {
            var e = kv.Value;
            if (e.node == null) continue;

            // マーカーの色を決定する。
            // インフラノード、および共用ノードは RoadPlanManager と同じ赤色。
            // 走行網単体 (depot_node_id) はオレンジ色にする。
            Color color = InfrastructureMarkerUtility.COLOR_BRIGHT;
            if (kv.Key == depotNodeId && kv.Key != infraNodeId)
            {
                color = DepotColor;
            }

            // ノードの絶対座標（地形高さを考慮したワールド座標）を動的に再計算して描画する。
            // 初期ロード時のタイミング（地形未ロードなど）により nodeGO.transform.position.y が
            // 0 に平坦化されてしまっている問題を回避するため、GetAbsolutePosition() を通す。
            // ドラッグ中〜PATCH完了までのノード(pinnedNodeId)だけは transform に追従させ、
            // 掴んだマーカーがカーソルに付いてくるようにする。
            Vector3 worldPos = GetNodeWorldPos(kv.Key, e);

            InfrastructureMarkerUtility.DrawPyramid(overlayRenderer, worldPos, color);
        }

        overlayRenderer.EndFrame(null);
    }
}

/// <summary>
/// インフラ接続点および走行網接続点のマーカー（反転した赤い四角錐 / 赤い矢印）を
/// 描画する処理を一元管理するユーティリティクラス。
/// </summary>
public static class InfrastructureMarkerUtility
{
    /// <summary>四角錐の底面の半径（中心から角までの距離、メートル）。</summary>
    public const float BASE_HALF = 2.0f;
    /// <summary>四角錐の高さ（メートル）。</summary>
    public const float HEIGHT = 4.0f;
    /// <summary>下向きの先端（apex）をノードからどれだけ上空に浮かせるか（メートル）。</summary>
    public const float APEX_OFFSET = 2.5f;
    /// <summary>Y軸回転の角速度（度/秒）。</summary>
    public const float ROTATE_SPEED = 40f;
    /// <summary>上下ボビングの振幅（メートル）。</summary>
    public const float BOB_AMPLITUDE = 0.3f;
    /// <summary>上下ボビングの角速度（ラジアン/秒）。</summary>
    public const float BOB_SPEED = 2f;

    /// <summary>側面の明色（赤色ベースの標準輝度）。</summary>
    public static readonly Color COLOR_BRIGHT = new Color(0.95f, 0.10f, 0.10f);
    /// <summary>側面の暗色および天面のフタの色（赤色ベースの低輝度）。</summary>
    public static readonly Color COLOR_DARK = new Color(0.75f, 0.05f, 0.05f);

    /// <summary>
    /// 指定された OverlayMeshRenderer を使って、ワールド座標の位置に
    /// アニメーション（回転・ボビング）付きの逆四角錐（ピラミッド）を描画します。
    /// </summary>
    public static void DrawPyramid(OverlayMeshRenderer renderer, Vector3 worldPos, Color color, float alpha = 1.0f)
    {
        float angle = Time.time * ROTATE_SPEED * Mathf.Deg2Rad;
        float bob = Mathf.Sin(Time.time * BOB_SPEED) * BOB_AMPLITUDE;

        // 底面4隅のオフセット。先頭の角だけ三角関数で求め、残りはXZ平面での90°回転で導出する
        Vector3 first = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * BASE_HALF;
        Vector3[] offsets = new Vector3[4];
        offsets[0] = first;
        offsets[1] = new Vector3(-first.z, 0f, first.x);
        offsets[2] = new Vector3(-first.x, 0f, -first.z);
        offsets[3] = new Vector3(first.z, 0f, -first.x);

        // 逆四角錐: 下向きの先端（apex）がノードの上空に浮き、底面はさらにその上
        Vector3 apex = worldPos + Vector3.up * (APEX_OFFSET + bob);
        Vector3 baseCenter = apex + Vector3.up * HEIGHT;

        var brightTris = new List<Vector3>(6);
        var darkTris = new List<Vector3>(12);

        for (int k = 0; k < 4; k++)
        {
            Vector3 c0 = baseCenter + offsets[k];
            Vector3 c1 = baseCenter + offsets[(k + 1) % 4];
            // 側面は明暗2色を交互に貼り、立体感を表現する
            var target = (k % 2 == 0) ? brightTris : darkTris;
            target.Add(apex); target.Add(c0); target.Add(c1);
        }

        // 天面（底面の正方形）を暗い色で塞ぐ
        darkTris.Add(baseCenter + offsets[0]);
        darkTris.Add(baseCenter + offsets[1]);
        darkTris.Add(baseCenter + offsets[2]);
        darkTris.Add(baseCenter + offsets[0]);
        darkTris.Add(baseCenter + offsets[2]);
        darkTris.Add(baseCenter + offsets[3]);

        // 色とアルファ値の適用
        Color brightColor = color;
        Color darkColor = color * 0.8f; // 暗い方は 80% の明るさにする
        brightColor.a = alpha;
        darkColor.a = alpha;

        renderer.AddPreTriangulated(brightTris, brightColor);
        renderer.AddPreTriangulated(darkTris, darkColor);
    }
}
