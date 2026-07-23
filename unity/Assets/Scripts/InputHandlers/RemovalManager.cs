using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using GeneratedModels;

/// <summary>
/// ブルドーザ(オブジェクト削除)モードの中核マネージャー。
///
/// STATE_REMOVE_OBJECTS 中に、削除可能なオブジェクト
/// (空き地区画・計画中の建物/道路)を左クリックで削除する。
///
/// - 削除可否は Unity 側のキャッシュ(ParcelSummary.usage_id / BuildingSummary.status /
///   PathSummary.status)で事前判定し、最終的な可否はバックエンドの DELETE ガードが権威となる。
/// - 削除は Unity が直接バックエンドの DELETE エンドポイントを叩く
///   (ZoneEditContext / NodeInputHandler と同じ直接REST方式)。
/// - 同じオブジェクトへ何度も DELETE を出さないよう、(type, id) の組で重複排除する。
/// - 削除クリック時に trash 効果音を鳴らす(クリップは IpcStateCoordinator から渡される)。
/// - 削除成功後は差分リロード(CellDataLoader.ReloadAllData)を短時間デバウンスして
///   一度だけ呼び、3D表現を実データに追随させる。区画は即時にローカル除去する。
///
/// STATE_EDIT_RESOURCE 等の動的マネージャーと同様に、IpcStateCoordinator が
/// 必要時に GameObject を生成して AddComponent する。
/// </summary>
public class RemovalManager : MonoBehaviour
{
    public static RemovalManager Instance { get; private set; }

    /// <summary>削除処理済み(発行中含む)の (objectType, id)。重複DELETEを防ぐ。</summary>
    private readonly HashSet<(int type, int id)> _processed = new HashSet<(int, int)>();

    /// <summary>ブルドーザモードが有効か。無効時は一切削除しない。</summary>
    private bool _active = false;

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
    }

    /// <summary>削除モード開始。削除済みセットを初期化し、マネージャー参照を取り直す。</summary>
    public void BeginRemovalMode()
    {
        _active = true;
        _processed.Clear();
        _parcelManager = FindAnyObjectByType<ParcelManager>();
        _pathManager = FindAnyObjectByType<PathManager>();
    }

    /// <summary>削除モード終了。次回開始時にクリーンな状態にする。</summary>
    public void EndRemovalMode()
    {
        _active = false;
        _processed.Clear();
    }

    /// <summary>
    /// 指定オブジェクトが削除可能かをUnity側キャッシュで事前判定する。
    /// 空き地区画: usage_id == VALUE_NONE(-1)
    /// 計画中の建物: status ∈ {PENDING, SCHEDULED_CONSTRUCTION}
    /// 計画中の道路: status ∈ {TEMPORARY, SCHEDULED_CONSTRUCTION, PENDING}
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
                return b != null && (b.status == Constants.STATUS_PENDING
                                  || b.status == Constants.STATUS_SCHEDULED_CONSTRUCTION);
            }
            case InputObjectType.PATH:
            {
                if (_pathManager == null) _pathManager = FindAnyObjectByType<PathManager>();
                PathSummary p = _pathManager != null ? _pathManager.GetPathData(id) : null;
                return p != null && (p.status == Constants.STATUS_TEMPORARY
                                  || p.status == Constants.STATUS_SCHEDULED_CONSTRUCTION
                                  || p.status == Constants.STATUS_PENDING);
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

        _processed.Add(key);
        PlayClickSound();
        StartCoroutine(DeleteRequest(objectType, id));
    }

    /// <summary>削除クリック時の効果音を鳴らす(未設定なら何もしない)。</summary>
    private void PlayClickSound()
    {
        if (_clickSound != null && _audioSource != null)
        {
            _audioSource.PlayOneShot(_clickSound);
        }
    }

    /// <summary>
    /// バックエンドの DELETE エンドポイントへ削除リクエストを発行する。
    /// 成功時は区画を即時ローカル除去し、差分リロードをデバウンスして予約する。
    /// 4xx(バックエンド拒否=状態が変わった等)はログして破棄する(処理済みセットには残す)。
    /// </summary>
    private IEnumerator DeleteRequest(int objectType, int id)
    {
        string endpoint;
        switch (objectType)
        {
            case InputObjectType.PARCEL: endpoint = "parcels"; break;
            case InputObjectType.BUILDING: endpoint = "agents"; break;
            case InputObjectType.PATH: endpoint = "paths"; break;
            default:
                yield break;
        }

        string url = $"{ApiClient.BASE_URL}/{endpoint}/{id}";
        using (UnityWebRequest request = UnityWebRequest.Delete(url))
        {
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                // 区画は即時にローカル除去してオーバーレイ・コライダーを消す
                // (以後 GetParcelIdAtPoint でも拾われなくなる)。
                if (objectType == InputObjectType.PARCEL)
                {
                    if (_parcelManager == null) _parcelManager = FindAnyObjectByType<ParcelManager>();
                    if (_parcelManager != null)
                    {
                        _parcelManager.RemoveParcel(id);
                    }
                }
                // 建物/道路の3D表現除去と全体整合は差分リロードに委ねる(デバウンス)。
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
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }
}
