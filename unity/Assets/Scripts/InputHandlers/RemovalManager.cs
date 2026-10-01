using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using GeneratedModels;

/// <summary>
/// ブルドーザ(オブジェクト削除)モードの中核マネージャー。
///
/// STATE_REMOVE_OBJECTS 中に、削除可能なオブジェクト
/// (空き地区画・計画中の建物/道路・地形プロップ)を左クリックで削除する。
///
/// - 削除可否は Unity 側のキャッシュ(ParcelSummary.usage_id / BuildingSummary.status /
///   PathSummary.status)で事前判定し、最終的な可否はバックエンドの DELETE ガードが権威となる。
/// - テンプレート作成モード(神モード)では UI が instant_remove を立てて開始する(Unity は神モードかを知らない)。
///   このとき建物・道路はステータスを問わず確認なしで即時に削除する(保護対象を除く。IsRemovable 参照)。
///   テナント・市民の道連れ削除や孤立ノードの片付けはバックエンドの DELETE 分岐が担う。
/// - 削除は Unity が直接バックエンドの DELETE エンドポイントを叩く
///   (ZoneEditContext / NodeInputHandler と同じ直接REST方式)。
/// - 同じオブジェクトへ何度も DELETE を出さないよう、(type, id) の組で重複排除する。
/// - 削除クリック時に trash 効果音を鳴らす(クリップは IpcStateCoordinator から渡される)。
/// - 削除成功後は差分リロード(CellDataLoader.ReloadAllData)を短時間デバウンスして
///   一度だけ呼び、3D表現を実データに追随させる。区画は即時にローカル除去する。
/// - 道路(名前付き道路)に属する区間を消したときは、
///   同じデバウンスの終わりに world:roads_changed を UI へ1回送り、道路一覧も取り直す。
///   UI の道路操作は UI 自身が notifyRoadsChanged するが、ブルドーザは UI を通らないため
///   (区間が0本になった道路はバックエンドが削除するので、開いている詳細パネルに知らせる必要がある)。
///
/// 地形プロップは個体ごとの GameObject/Collider を持たない(GPUインスタンシング)ため、
/// InputHandler のレイキャストには掛からない。クリック/ホバー時に InputHandler が本クラスへ
/// 画面レイを渡し、PropManager.TryPick のAABB判定で「手前の建物・地形より近い」プロップを拾う。
/// 個別配置(PropInstance)は1個だけ即削除、範囲配置(PropScatter)は森ごと消える不可逆操作なので
/// UI(コックピット)に remove_object:request_confirm で確認を求め、回答 remove_object:confirm_result を
/// 待ってから DELETE する。ホバー中のプロップは赤い枠(範囲配置は散布範囲の外周も)で示す。
///
/// STATE_EDIT_RESOURCE 等の動的マネージャーと同様に、IpcStateCoordinator が
/// 必要時に GameObject を生成して AddComponent する。
/// </summary>
public class RemovalManager : MonoBehaviour
{
    public static RemovalManager Instance { get; private set; }

    /// <summary>確認付き削除の対象種別(remove_object:request_confirm / confirm_result の object_type)。</summary>
    public const string OBJECT_TYPE_PROP_SCATTER = "prop_scatter";

    // ホバー枠の描画パラメータ。削除操作なので警告色(赤)にする。
    private const float HIGHLIGHT_LINE_THICKNESS = 2.5f;
    private static readonly Color HighlightColor = new Color(1f, 0.35f, 0.3f, 0.95f);
    private static readonly Color ScatterRegionColor = new Color(1f, 0.35f, 0.3f, 0.6f);

    /// <summary>削除処理済み(発行中含む)の (objectType, id)。重複DELETEを防ぐ。</summary>
    private readonly HashSet<(int type, int id)> _processed = new HashSet<(int, int)>();
    /// <summary>削除処理済みの範囲配置プロップID(個別配置とIDが衝突するため別集合)。</summary>
    private readonly HashSet<int> _processedScatters = new HashSet<int>();

    /// <summary>ブルドーザモードが有効か。無効時は一切削除しない。</summary>
    private bool _active = false;
    /// <summary>即時削除モードか。建物・道路の削除可否をステータスで絞らない。</summary>
    private bool _instantRemove = false;

    // マネージャー参照(ParcelManager / PathManager はシングルトンでないため FindAnyObjectByType で取得)
    private ParcelManager _parcelManager;
    private PathManager _pathManager;

    // 削除クリック時の効果音(IpcStateCoordinator の SerializeField から設定される)
    private AudioSource _audioSource;
    private AudioClip _clickSound;

    // 差分リロードのデバウンス制御
    private bool _reloadScheduled = false;
    private float _reloadDeadline = 0f;
    private const float RELOAD_DEBOUNCE_SECONDS = 0.35f;
    /// <summary>次のデバウンス明けに world:roads_changed を送るか(道路に属する区間の削除が成功した)。</summary>
    private bool _roadsChangedPending = false;

    // UIの確認待ち中の範囲配置プロップID(0=待ちなし)。待ち中は別の範囲配置クリックを受けない。
    private int _pendingScatterId = 0;

    // ホバー中のプロップ(枠描画用)
    private bool _hasHover = false;
    private PropManager.PickResult _hover;
    private OverlayMeshRenderer _overlayRenderer;
    private GameObject _overlayObj;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            _audioSource = gameObject.AddComponent<AudioSource>();
        }
        _audioSource.playOnAwake = false;
    }

    /// <summary>削除クリック時に鳴らす効果音を設定する。</summary>
    public void SetClickSound(AudioClip clip)
    {
        _clickSound = clip;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        if (_overlayObj != null) Destroy(_overlayObj);
    }

    /// <summary>
    /// 削除モード開始。削除済みセットを初期化し、マネージャー参照を取り直す。
    /// instantRemove=true は神モードの即時削除(建物・道路をステータスを問わず消す)。
    /// </summary>
    public void BeginRemovalMode(bool instantRemove = false)
    {
        _active = true;
        _instantRemove = instantRemove;
        _processed.Clear();
        _processedScatters.Clear();
        _pendingScatterId = 0;
        _hasHover = false;
        _parcelManager = FindAnyObjectByType<ParcelManager>();
        _pathManager = FindAnyObjectByType<PathManager>();
    }

    /// <summary>削除モード終了。次回開始時にクリーンな状態にする。</summary>
    public void EndRemovalMode()
    {
        _active = false;
        _instantRemove = false;
        _processed.Clear();
        _processedScatters.Clear();
        _pendingScatterId = 0;
        _hasHover = false;
        _overlayRenderer?.ClearMesh();
    }

    /// <summary>
    /// 指定オブジェクトが削除可能かをUnity側キャッシュで事前判定する。
    /// 空き地区画: usage_id == VALUE_NONE(-1)
    /// 計画中の建物: status ∈ {PENDING, SCHEDULED_CONSTRUCTION}
    /// 計画中の道路: status ∈ {TEMPORARY, SCHEDULED_CONSTRUCTION, PENDING}
    /// 即時削除モードでは建物・道路をステータスで絞らない。ただし建物は市政府・中央銀行(TYPE_CITY)と
    /// 代理主体・国境検問所(TYPE_DEFAULT)を除き、道路は公共交通の区間パス(PATH_TRANSPORT。路線の持ち物)を除く。
    /// </summary>
    public bool IsRemovable(int objectType, int id)
    {
        switch (objectType)
        {
            case InputObjectType.PARCEL:
            {
                if (_parcelManager == null) _parcelManager = FindAnyObjectByType<ParcelManager>();
                ParcelSummary s = _parcelManager != null ? _parcelManager.GetParcelSummary(id) : null;
                return s != null && s.usage_id == Constants.VALUE_NONE;
            }
            case InputObjectType.BUILDING:
            {
                BuildingSummary b = BuildingManager.Instance != null ? BuildingManager.Instance.GetBuildingSummary(id) : null;
                if (b == null) return false;
                if (_instantRemove) return b.type != Constants.TYPE_CITY && b.type != Constants.TYPE_DEFAULT;
                return b.status == Constants.STATUS_PENDING || b.status == Constants.STATUS_SCHEDULED_CONSTRUCTION;
            }
            case InputObjectType.PATH:
            {
                if (_pathManager == null) _pathManager = FindAnyObjectByType<PathManager>();
                PathSummary p = _pathManager != null ? _pathManager.GetPathData(id) : null;
                if (p == null) return false;
                if (_instantRemove) return p.path_type != Constants.PATH_TRANSPORT;
                return p.status == Constants.STATUS_TEMPORARY
                    || p.status == Constants.STATUS_SCHEDULED_CONSTRUCTION
                    || p.status == Constants.STATUS_PENDING;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// 単一オブジェクトの削除を試みる(左クリックの入口)。
    /// 可否判定を通り、まだ処理していない対象のみ DELETE を発行し、trash 効果音を鳴らす。
    /// </summary>
    public void TryRemove(int objectType, int id)
    {
        if (!_active) return;
        if (id < 0) return;

        var key = (objectType, id);
        if (_processed.Contains(key)) return;
        if (!IsRemovable(objectType, id)) return;

        string endpoint;
        switch (objectType)
        {
            case InputObjectType.PARCEL: endpoint = "parcels"; break;
            case InputObjectType.BUILDING: endpoint = "agents"; break;
            case InputObjectType.PATH: endpoint = "paths"; break;
            default: return;
        }

        // 所属は削除前に引く(削除後に取り直した一覧からは消えている)。一覧の取り直し前に所属した区間も
        // 拾えるようパスの road_id も見る(古くて余計に送っても、UI が取り直すだけで害はない)。
        bool isRoadMember = objectType == InputObjectType.PATH && IsRoadMemberPath(id);

        _processed.Add(key);
        PlayClickSound();
        StartCoroutine(DeleteRequest(endpoint, id, () =>
        {
            if (isRoadMember) _roadsChangedPending = true;
            // 区画は即時にローカル除去してオーバーレイ・コライダーを消す
            // (以後 GetParcelIdAtPoint でも拾われなくなる)。建物/道路の3D表現除去は差分リロードに委ねる。
            if (objectType == InputObjectType.PARCEL)
            {
                if (_parcelManager == null) _parcelManager = FindAnyObjectByType<ParcelManager>();
                _parcelManager?.RemoveParcel(id);
            }
        }));
    }

    private bool IsRoadMemberPath(int pathId)
    {
        if (RoadManager.Instance != null && RoadManager.Instance.GetRoadIdOfPath(pathId) > 0) return true;
        if (_pathManager == null) _pathManager = FindAnyObjectByType<PathManager>();
        PathSummary p = _pathManager != null ? _pathManager.GetPathData(pathId) : null;
        return p != null && p.road_id.HasValue && p.road_id.Value > 0;
    }

    // ======================================================================
    // 地形プロップ
    // ======================================================================

    /// <summary>
    /// 画面レイ上の最も近いプロップを削除対象として処理する(左クリックの入口)。
    /// maxDistance は同じレイで拾った建物・区画・地形までの距離で、それより遠いプロップは手前の
    /// オブジェクトに隠れているため拾わない。プロップを拾えたら true を返し、呼び出し側は
    /// 通常の(建物・区画等への)クリック配送を行わない。
    /// 個別配置は即 DELETE、範囲配置は UI へ確認を求めて回答待ちにする。
    /// </summary>
    public bool TryRemovePropAt(Ray ray, float maxDistance)
    {
        if (!_active || PropManager.Instance == null) return false;
        if (!PropManager.Instance.TryPick(ray, maxDistance, out var hit)) return false;

        if (!hit.scatter)
        {
            var key = (InputObjectType.PROP, hit.id);
            if (_processed.Contains(key)) return true;
            _processed.Add(key);
            PlayClickSound();
            StartCoroutine(DeleteRequest("props/instances", hit.id, () => PropManager.Instance?.HideSource(false, hit.id)));
            return true;
        }

        // 範囲配置: 確認待ち中・処理済みなら何もしない(連打で確認ダイアログが重ならないように)。
        if (_pendingScatterId != 0 || _processedScatters.Contains(hit.id)) return true;
        _pendingScatterId = hit.id;
        IpcClient.Instance?.EmitRemoveObjectRequestConfirm(OBJECT_TYPE_PROP_SCATTER, hit.id, hit.type);
        return true;
    }

    /// <summary>
    /// UI の確認ダイアログの回答(remove_object:confirm_result)。確認を求めた対象と一致し、承諾された
    /// 場合だけ DELETE を発行する。モード終了後に遅れて届いた回答は無視する。
    /// </summary>
    public void OnConfirmResult(string objectType, int id, bool accepted)
    {
        if (objectType != OBJECT_TYPE_PROP_SCATTER || id != _pendingScatterId) return;
        _pendingScatterId = 0;
        if (!_active || !accepted) return;
        if (!_processedScatters.Add(id)) return;
        PlayClickSound();
        StartCoroutine(DeleteRequest("props/scatters", id, () => PropManager.Instance?.HideSource(true, id)));
    }

    /// <summary>
    /// ホバー中のプロップを更新する(InputHandler がレイの変化ごとに呼ぶ)。枠の描画は LateUpdate。
    /// </summary>
    public void UpdatePropHover(Ray ray, float maxDistance)
    {
        if (!_active || PropManager.Instance == null) { _hasHover = false; return; }
        _hasHover = PropManager.Instance.TryPick(ray, maxDistance, out _hover);
    }

    private void LateUpdate()
    {
        if (!_active || !_hasHover) { _overlayRenderer?.ClearMesh(); return; }
        Camera cam = Camera.main;
        if (cam == null) return;
        EnsureOverlayRenderer();
        _overlayRenderer.BeginFrame();
        DrawBoundsEdges(_hover.bounds);
        if (_hover.scatter && PropManager.Instance != null
            && PropManager.Instance.TryGetScatterRegions(_hover.id, out var regions))
        {
            // 範囲配置は森ごと消えることを伝えるため、散布範囲の外周を地表に沿って描く。
            Vector3 offset = PropManager.Instance.SceneOffset;
            foreach (var region in regions)
            {
                if (region.rings.Count == 0) continue;
                var ring = region.rings[0];
                for (int i = 0; i < ring.Length; i++)
                {
                    Vector3 a = GroundPoint(ring[i], offset);
                    Vector3 b = GroundPoint(ring[(i + 1) % ring.Length], offset);
                    _overlayRenderer.AddSolidLine(a, b, ScatterRegionColor, HIGHLIGHT_LINE_THICKNESS);
                }
            }
        }
        _overlayRenderer.EndFrame(cam);
    }

    private static Vector3 GroundPoint(Vector2 saved, Vector3 offset)
    {
        var p = new Vector3(saved.x, 0f, saved.y) + offset;
        p.y = Utils.GetGroundHeight(p) + 0.3f;
        return p;
    }

    /// <summary>AABBの12辺を線で描く。</summary>
    private void DrawBoundsEdges(Bounds b)
    {
        Vector3 min = b.min, max = b.max;
        Vector3[] c =
        {
            new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z),
        };
        int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        for (int i = 0; i < edges.GetLength(0); i++)
            _overlayRenderer.AddSolidLine(c[edges[i, 0]], c[edges[i, 1]], HighlightColor, HIGHLIGHT_LINE_THICKNESS);
    }

    private void EnsureOverlayRenderer()
    {
        if (_overlayObj != null) return;
        // 樹冠や建物に埋もれず常に手前に描く(他エディタのギズモと同じ)。
        _overlayRenderer = OverlayMeshRenderer.Create("RemovalOverlayMesh", alwaysOnTop: true);
        _overlayObj = _overlayRenderer.gameObject;
    }

    // ======================================================================
    // 共通
    // ======================================================================

    /// <summary>削除クリック時の効果音を鳴らす(未設定なら何もしない)。</summary>
    private void PlayClickSound()
    {
        if (_clickSound != null && _audioSource != null)
        {
            _audioSource.PlayOneShot(_clickSound);
        }
    }

    /// <summary>
    /// バックエンドの DELETE エンドポイント({endpoint}/{id})へ削除リクエストを発行する。
    /// 成功時は onSuccess(即時のローカル反映)を呼び、差分リロードをデバウンスして予約する。
    /// 4xx(バックエンド拒否=状態が変わった等)はログして破棄する(処理済みセットには残す)。
    /// </summary>
    private IEnumerator DeleteRequest(string endpoint, int id, System.Action onSuccess)
    {
        using (ApiRequest call = ApiClient.Request("DELETE", $"/{endpoint}/{id}"))
        {
            yield return call.Send();
            UnityWebRequest request = call.Web;

            if (request.result == UnityWebRequest.Result.Success)
            {
                onSuccess?.Invoke();
                RequestReload();
            }
            else
            {
                Debug.LogWarning($"RemovalManager: 削除に失敗しました ({endpoint}/{id}): "
                    + $"{request.responseCode} {request.error}");
            }
        }
    }

    /// <summary>差分リロードを短時間デバウンスして予約する。連続削除を1回のリロードに集約する。</summary>
    private void RequestReload()
    {
        _reloadDeadline = Time.unscaledTime + RELOAD_DEBOUNCE_SECONDS;
        if (!_reloadScheduled)
        {
            _reloadScheduled = true;
            StartCoroutine(ReloadAfterIdle());
        }
    }

    private IEnumerator ReloadAfterIdle()
    {
        while (Time.unscaledTime < _reloadDeadline)
        {
            yield return null;
        }
        _reloadScheduled = false;
        bool roadsChanged = _roadsChangedPending;
        _roadsChangedPending = false;
        if (roadsChanged) IpcClient.Instance?.EmitRoadsChanged();
        if (CellDataLoader.Instance != null)
        {
            // 道路一覧の取り直し(RoadManager.RequestRefresh)も ReloadAllData に含まれる。
            CellDataLoader.Instance.ReloadAllData();
        }
        else if (roadsChanged)
        {
            RoadManager.Instance?.RequestRefresh();
        }
    }
}
