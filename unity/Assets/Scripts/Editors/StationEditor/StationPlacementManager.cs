using UnityEngine;

/// <summary>
/// StationPlacementManager:
/// 駅舎(Station house)の設置エディタ。PropPlacementManager(個別配置)と同じ
/// 「カーソル追従の半透明ゴースト → クリックで候補確定 → Tauri の確定ボタンで PATCH」
/// という2段階フローで、駅舎を駅パス(プラットフォーム)の近傍に設置する。
///
/// 操作の流れ:
///   1. Tauri の駅舎タブで駅舎モデル(house_variant)を選び、STATE_EDIT_STATION を送る。
///      IpcStateCoordinator が本マネージャを動的生成・初期化する。
///   2. マウスカーソルを駅パス(platform_width を持つバリアント)に近づけると、その位置に
///      駅舎モデルの半透明ゴーストが表示される(最寄りプラットフォームの向きに合わせて回転)。
///      駅パスから離れているとゴーストは消える(駅舎はホーム近傍にのみ置けるため)。
///   3. 左クリックで候補位置を確定し、最寄りプラットフォームの path_id・位置・回転・モデルを
///      edit_station:update_candidate で Tauri へ通知する。
///   4. Tauri 側で所属駅を解決して確定ボタンを有効化し、押下で PATCH /api/v1/stations/{id} を
///      送る。成功後 Tauri は edit_station:finish を送り、本マネージャはゴーストを片付け、
///      CellDataLoader が差分リロードして設置済みの駅舎を描画する。
///
/// 確定送信(PATCH)は Tauri 側が直接行う(プロップ配置・公共交通設置と同じ責務分担)。
/// 本マネージャは駅パスへのスナップ・ゴースト描画・候補通知のみを担う。
/// </summary>
public class StationPlacementManager : MonoBehaviour
{
    // カーソルからこの距離(m)以内に駅パス(プラットフォーム)があればゴーストを表示する。
    // バックエンドは所属ホーム中心線から 100m 以内を許容するが、UI 上は「近づける」操作
    // として妥当な範囲に絞る(遠すぎる位置にゴーストが出続けるのを防ぐ)。
    private const float SNAP_THRESHOLD = 60f;

    private int houseVariant = 0;
    private bool active = false;

    // 直近のホバーで有効な候補があるか(=近傍に駅パスがあるか)。
    private bool hoverValid = false;
    private Vector3 candidatePos;
    private float candidateRotation;
    private int candidatePathId = -1;

    // カーソル追従の半透明ゴースト。
    private GameObject ghost;
    private Transform container;
    private Material previewMaterial;
    private PathManager pathManager;

    private void Awake()
    {
        previewMaterial = Resources.Load<Material>("PreviewBuildingMaterial");
        EnsureContainer();
    }

    private void EnsureContainer()
    {
        if (container != null) return;
        var go = new GameObject("StationPlacementContainer");
        container = go.transform;
        container.SetParent(transform, false);
    }

    // =====================================================================
    // ライフサイクル
    // =====================================================================

    /// <summary>
    /// 駅舎設置モードを初期化する。houseVariant は設置する駅舎モデル
    /// (station_house_master のID)。
    /// </summary>
    public void InitializePlacement(int houseVariant)
    {
        EnsureContainer();
        if (previewMaterial == null) previewMaterial = Resources.Load<Material>("PreviewBuildingMaterial");
        ClearGhost();
        this.houseVariant = houseVariant;
        active = true;
        hoverValid = false;
        Debug.Log($"StationPlacementManager: 駅舎設置モード初期化 house_variant={houseVariant}");
    }

    /// <summary>UI からの駅舎モデル変更(edit_station:change_variant)。ゴーストを作り直す。</summary>
    public void OnChangeVariant(int newVariant)
    {
        if (newVariant == houseVariant) return;
        houseVariant = newVariant;
        ClearGhost();
        // 次のホバーで新しいモデルのゴーストが生成される。
    }

    /// <summary>設置モードを終了し、ゴーストを片付ける。</summary>
    public void FinishPlacement()
    {
        active = false;
        hoverValid = false;
        candidatePathId = -1;
        ClearGhost();
    }

    // =====================================================================
    // 入力ハンドラ(TerrainInputHandler から呼ばれる)
    // =====================================================================

    /// <summary>
    /// カーソル追従。最寄りの駅パス(プラットフォーム)を探し、近ければその位置に
    /// 駅舎ゴーストを最寄りホームの向きに合わせて表示する。離れていればゴーストを隠す。
    /// </summary>
    public void OnTerrainHover(Vector3 worldPos)
    {
        if (!active) return;
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();

        if (!TryFindNearestPlatform(worldPos, out Vector3 snappedPos, out float rotation, out int pathId))
        {
            hoverValid = false;
            HideGhost();
            return;
        }

        hoverValid = true;
        // ゴーストはカーソル位置に置き(ホームの直近に自由配置)、向きだけホームに合わせる。
        candidatePos = new Vector3(worldPos.x, Utils.GetGroundHeight(worldPos), worldPos.z);
        candidateRotation = rotation;
        candidatePathId = pathId;

        if (ghost == null)
        {
            ghost = InstantiateGhost();
        }
        if (ghost != null)
        {
            ghost.SetActive(true);
            ghost.transform.position = candidatePos;
            ghost.transform.rotation = Quaternion.Euler(0f, candidateRotation, 0f);
        }
    }

    /// <summary>
    /// 左クリックで候補位置を確定し、Tauri へ通知する。近傍に駅パスが無いクリックは無視する。
    /// </summary>
    public void OnTerrainLeftClick(Vector3 worldPos)
    {
        if (!active || !hoverValid) return;
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditStationUpdateCandidate(
                candidatePathId, candidatePos.x, candidatePos.z, candidateRotation, houseVariant
            );
        }
        Debug.Log($"StationPlacementManager: 候補確定 path_id={candidatePathId} " +
                  $"pos=({candidatePos.x:F1},{candidatePos.z:F1}) rot={candidateRotation:F1}");
    }

    // =====================================================================
    // スナップ・ゴースト生成
    // =====================================================================

    /// <summary>
    /// カーソル近傍の駅パス(platform_width を持つバリアント)を探す。見つかれば
    /// そのプラットフォームの向き(セグメント方向)から回転角を求めて返す。
    /// </summary>
    private bool TryFindNearestPlatform(Vector3 basePos, out Vector3 snappedPos, out float rotation, out int pathId)
    {
        snappedPos = basePos;
        rotation = 0f;
        pathId = -1;
        if (pathManager == null) return false;

        // 近傍セグメントを走査し、駅パス(platform_width>0)のうち最も近いものを選ぶ。
        var segments = pathManager.QueryNearbySegments(basePos, SNAP_THRESHOLD);
        float minDist = float.MaxValue;
        bool found = false;
        Vector3 segDir = Vector3.forward;
        foreach (var seg in segments)
        {
            if (seg.path == null) continue;
            if (!IsPlatformVariant(seg.path.path_variant)) continue;

            Vector3 closest = TerrainInputHandler.GetClosestPointOnSegment(seg.a, seg.b, basePos);
            float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(closest.x, closest.z));
            if (dist < minDist && dist <= SNAP_THRESHOLD)
            {
                minDist = dist;
                snappedPos = closest;
                pathId = seg.path.path_id;
                segDir = seg.b - seg.a;
                found = true;
            }
        }

        if (found)
        {
            segDir.y = 0f;
            if (segDir.sqrMagnitude > 1e-4f)
            {
                // 駅舎の奥行き軸をプラットフォームと平行に向ける(ホームに沿って建つ)。
                rotation = Mathf.Atan2(segDir.x, segDir.z) * Mathf.Rad2Deg;
            }
        }
        return found;
    }

    /// <summary>path_variant が駅パス(platform_width>0)かどうかをマスターから判定する。</summary>
    private static bool IsPlatformVariant(int pathVariant)
    {
        if (MasterDataStore.Instance == null) return false;
        var master = MasterDataStore.Instance.GetPathMaster(pathVariant);
        return master != null && master.platform_width.HasValue && master.platform_width.Value > 0f;
    }

    /// <summary>
    /// 駅舎モデル(Resources/Models/Stations/{variant})を読み込み、半透明ゴーストとして生成する。
    /// モデルが無ければ既定寸法のプリミティブ箱で代替する(StationManager と同じ方針)。
    /// </summary>
    private GameObject InstantiateGhost()
    {
        GameObject prefab = null;
        if (ModelManager.Instance != null)
        {
            prefab = ModelManager.Instance.GetModel($"Models/Stations/{houseVariant}", suppressWarning: true)
                     ?? ModelManager.Instance.GetModel("Models/Stations/default", suppressWarning: true);
        }

        GameObject go;
        if (prefab != null)
        {
            go = Instantiate(prefab, container);
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(container, false);
            go.transform.localScale = new Vector3(
                Constants.STATION_HOUSE_DEFAULT_WIDTH, 6f, Constants.STATION_HOUSE_DEFAULT_DEPTH
            );
            // プリミティブのコライダーはゴースト表示に不要(レイキャストを邪魔しない)。
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }
        go.name = "StationHouseGhost";
        go.SetActive(true);
        if (previewMaterial != null) ApplyGhostMaterial(go);
        return go;
    }

    /// <summary>配置前のゴースト表示のため、全子 Renderer を半透明マテリアルで上書きする。</summary>
    private void ApplyGhostMaterial(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = previewMaterial;
            r.sharedMaterials = mats;
        }
    }

    private void HideGhost()
    {
        if (ghost != null) ghost.SetActive(false);
    }

    private void ClearGhost()
    {
        if (ghost != null) { Destroy(ghost); ghost = null; }
    }

    private void OnDestroy()
    {
        ClearGhost();
    }
}
