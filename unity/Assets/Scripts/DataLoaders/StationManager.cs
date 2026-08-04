using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;

[Serializable]
public class StationHouseItem
{
    // 駅舎の設置位置 [x, z] と Y軸回転(度)。
    public List<float> position;
    public float rotation;
}

[Serializable]
public class StationItem
{
    public int station_id;
    public string name;
    public int version;
    // 駅の中心点 [x, z](所属ホームの中心点平均)。駅舎未設置の駅では表示位置の代わりにはしない。
    public List<float> center_point;
    // 駅舎の設置情報。未設置(野ざらしの停留場)は null。
    public StationHouseItem house;
    // 駅舎モデルの代表バリアント(所属ホームの path_variant から導出される)。所属ゼロなら null。
    public int? house_variant;
}

[Serializable]
public class StationListResponse
{
    public List<StationItem> stations;
}

/// <summary>
/// 駅(Station)の駅舎をバックエンド(GET /api/v1/stations)から取得し、3D空間に
/// 描画・管理するマネージャー。
///
/// 駅はバックエンド側で駅パス(プラットフォーム)の敷設時に自動作成される概念で、
/// このマネージャーは「駅舎が設置されている駅」だけを実体化する(プラットフォーム自体の
/// 描画は PathManager / TrackPath が担う)。駅舎はプレハブ
/// Resources/Models/Stations/{house_variant} を配置し、無ければ default、それも無ければ
/// プリミティブの仮箱で代替する(アセット整備前でも位置・クリックを確認できるようにするため)。
///
/// ロード方式は PropManager と同じ: 必要セル(nearCells)のマージBBOXで一括取得し、
/// version 差分更新の慣習に従ってレコード単位で再生成する。駅舎はY座標を持たず常に
/// 地表に接地し、地形ロード完了後の追従(AdjustStationHeightsForCellsCoroutine)で
/// Y座標が再設定される。
///
/// クリックすると駅詳細パネル(station_detail)が開く(StationInputHandler)。
/// </summary>
public class StationManager : BaseApiDataManager
{
    public static StationManager Instance { get; private set; }

    private const string STATION_TAG = "StationObject";
    private const string STATION_PREFIX = "Station_";

    private class StationState
    {
        public int version;
        public GameObject go;
        public Vector2Int cell;
    }

    // 駅ID単位のレジストリと、セル→駅IDの逆引きインデックス。
    // 登録・削除は必ず RegisterStationCell / RemoveStation / UnloadCell / ClearAllCells を通すこと。
    private readonly Dictionary<int, StationState> stationStates = new Dictionary<int, StationState>();
    private readonly Dictionary<Vector2Int, HashSet<int>> stationIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();

    // プレハブ欠落の警告を variant ごとに1回だけ出すためのデデュープ。
    private readonly HashSet<int> missingPrefabWarnedVariants = new HashSet<int>();

    // 進行中のマージロードコルーチン。新しいロード開始時に停止して仕切り直す。
    private Coroutine mergedLoadCoroutine;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        ClearAllCells();
    }

    /// <summary>
    /// 指定されたセル一覧に基づき、アンロードと一括ロードを実行する。
    /// HTTP取得は毎回全件(マージBBOX)行い、再生成の要否はレコードごとの version 比較で決める。
    /// </summary>
    public override void LoadCells(List<Vector2Int> neededCells, bool isForce)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[StationManager] LoadCells invoked. count={neededCells.Count}, isForce={isForce}");

        var neededSet = new HashSet<Vector2Int>(neededCells);
        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!neededSet.Contains(cell)) toUnload.Add(cell);
        }
        foreach (var cell in toUnload)
        {
            UnloadCell(cell);
        }

        // ロード開始時点で必要セルをすべて登録する(途中キャンセル時の残留防止。PropManagerと同じ)。
        loadedCells.Clear();
        loadedCells.UnionWith(neededSet);

        if (mergedLoadCoroutine != null)
        {
            StopCoroutine(mergedLoadCoroutine);
        }
        mergedLoadCoroutine = StartCoroutine(LoadMergedStations(new List<Vector2Int>(neededCells), isForce));
    }

    /// <summary>
    /// 必要セル全体のマージBBOXで駅を一括取得し、駅舎オブジェクトの差分同期を行う。
    /// </summary>
    private IEnumerator LoadMergedStations(List<Vector2Int> neededCells, bool isForce)
    {
        if (neededCells.Count == 0)
        {
            mergedLoadCoroutine = null;
            yield break;
        }

        CalculateMergedBBox(neededCells, out float minX, out float minZ, out float maxX, out float maxZ);
        string path = $"/stations?bbox={minX},{minZ},{maxX},{maxZ}";

        StationListResponse data = null;
        string errorMsg = null;
        yield return ApiClient.Get<StationListResponse>(path,
            (res) => { data = res; },
            (err) => { errorMsg = err; }
        );

        if (errorMsg != null || data == null)
        {
            Debug.LogError($"[StationManager] 駅一覧の取得に失敗しました: {errorMsg}");
            mergedLoadCoroutine = null;
            yield break;
        }

        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () =>
            $"[StationManager] マージロード: stations={data.stations?.Count ?? 0}, cells={neededCells.Count}");

        var neededSet = new HashSet<Vector2Int>(neededCells);
        Transform container = GetContainer("StationContainer");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var returnedIds = new HashSet<int>();
        if (data.stations != null)
        {
            foreach (var st in data.stations)
            {
                // 駅舎が未設置の駅は実体化しない(プラットフォームはPathManagerが描画している)。
                if (st.house == null || st.house.position == null || st.house.position.Count < 2)
                {
                    continue;
                }
                float hx = st.house.position[0];
                float hz = st.house.position[1];
                Vector2Int cell = PositionToCell(new Vector3(hx, 0f, hz));
                // マージBBOXは必要セル集合の外接矩形のため、必要セル外のレコードも混ざる。
                if (!neededSet.Contains(cell)) continue;
                returnedIds.Add(st.station_id);

                if (stationStates.TryGetValue(st.station_id, out StationState state)
                    && !isForce && state.version == st.version && state.go != null)
                {
                    continue; // version 不変なら再生成しない
                }
                if (state != null)
                {
                    RemoveStation(st.station_id, state);
                }

                GameObject go = CreateStationHouseObject(st, container, hx, hz);
                stationStates[st.station_id] = new StationState { version = st.version, go = go, cell = cell };
                RegisterStationCell(st.station_id, cell);

                if (sw.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                {
                    yield return null;
                    sw.Restart();
                }
            }
        }

        // 応答に含まれなかった(または駅舎が撤去された)キャッシュ済みレコードは破棄する。
        List<int> staleIds = null;
        foreach (var kvp in stationStates)
        {
            if (!returnedIds.Contains(kvp.Key))
            {
                (staleIds ??= new List<int>()).Add(kvp.Key);
            }
        }
        if (staleIds != null)
        {
            foreach (int id in staleIds)
            {
                if (stationStates.TryGetValue(id, out var state)) RemoveStation(id, state);
            }
        }

        mergedLoadCoroutine = null;
    }

    /// <summary>
    /// 駅舎のGameObjectを生成し、クリック用のタグ・コライダー・ハンドラを付与する。
    /// </summary>
    private GameObject CreateStationHouseObject(StationItem st, Transform container, float hx, float hz)
    {
        int variant = st.house_variant ?? 0;
        GameObject prefab = GetPrefab(variant);

        GameObject go;
        if (prefab != null)
        {
            go = Instantiate(prefab, container);
        }
        else
        {
            // アセット整備前でも位置とクリックを確認できるよう、プリミティブの仮箱で代替する。
            WarnMissingPrefabOnce(variant);
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(container, false);
            go.transform.localScale = new Vector3(10f, 6f, 8f);
        }

        go.name = $"{STATION_PREFIX}{st.station_id}";
        go.transform.position = new Vector3(hx, SampleGroundY(hx, hz), hz);
        go.transform.rotation = Quaternion.Euler(0f, st.house.rotation, 0f);
        go.SetActive(true);

        // クリック判定に必要な三点セット: タグ・コライダー・IInputHandler。
        // IDの参照は名前パースではなく ObjectIdInfo を使う(プロジェクト規約)。
        go.tag = STATION_TAG;
        var idInfo = go.AddComponent<ObjectIdInfo>();
        idInfo.id = st.station_id;
        var handler = go.AddComponent<StationInputHandler>();
        handler.stationId = st.station_id;
        EnsureCollider(go);

        return go;
    }

    /// <summary>
    /// クリック判定用のコライダーを保証する。プレハブ側にコライダーが無い場合、
    /// レンダラーの合成バウンズからBoxColliderを付与する(Y軸回転のみの前提の近似で十分)。
    /// </summary>
    private static void EnsureCollider(GameObject go)
    {
        if (go.GetComponentInChildren<Collider>() != null) return;

        var renderers = go.GetComponentsInChildren<Renderer>();
        var box = go.AddComponent<BoxCollider>();
        if (renderers.Length == 0) return;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }
        box.center = go.transform.InverseTransformPoint(bounds.center);
        box.size = go.transform.InverseTransformVector(bounds.size);
    }

    /// <summary>
    /// 地形が更新されたセルの駅舎のY座標を地表に合わせ直す。
    /// CellDataLoader.TerrainFollowUpCoroutine から呼ばれる(プロップと同じ2段階ロード。
    /// メッシュ再生成は不要でY再設定のみ)。IEnumerator を直接 yield return される前提のため、
    /// 1個体の失敗は捕捉して続行する(yield をまたがない位置調整のみを包む)。
    /// </summary>
    public IEnumerator AdjustStationHeightsForCellsCoroutine(List<Vector2Int> cells)
    {
        if (cells == null || cells.Count == 0) yield break;

        Utils.InvalidateHeightCache();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int adjusted = 0;

        foreach (var cell in cells)
        {
            if (!stationIdsByCell.TryGetValue(cell, out var ids)) continue;
            foreach (int id in new List<int>(ids))
            {
                try
                {
                    if (!stationStates.TryGetValue(id, out var st) || st.go == null) continue;
                    Vector3 pos = st.go.transform.position;
                    pos.y = SampleGroundY(pos.x, pos.z);
                    st.go.transform.position = pos;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"StationManager: 地形追従の高さ調整に失敗 station_id={id}: {e}");
                }
                adjusted++;
                if ((adjusted & 63) == 0 && sw.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                {
                    yield return null;
                    sw.Restart();
                }
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[StationManager] 地形追従: {adjusted}個の駅舎の高さを再設定 ({cells.Count}セル)");
    }

    /// <summary>
    /// 全セルの駅舎オブジェクトと内部状態をクリアする。
    /// </summary>
    public override void ClearAllCells()
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "[StationManager] ClearAllCells invoked.");
        if (mergedLoadCoroutine != null)
        {
            StopCoroutine(mergedLoadCoroutine);
            mergedLoadCoroutine = null;
        }

        foreach (var kvp in stationStates)
        {
            if (kvp.Value.go != null) Destroy(kvp.Value.go);
        }
        stationStates.Clear();
        stationIdsByCell.Clear();
        loadedCells.Clear();
    }

    /// <summary>
    /// 指定されたセルの駅舎オブジェクトを破棄する。
    /// </summary>
    protected override void UnloadCell(Vector2Int cellIndex)
    {
        if (stationIdsByCell.TryGetValue(cellIndex, out var ids))
        {
            foreach (int id in ids)
            {
                if (stationStates.TryGetValue(id, out var st))
                {
                    if (st.go != null) Destroy(st.go);
                    stationStates.Remove(id);
                }
            }
            stationIdsByCell.Remove(cellIndex);
        }
        loadedCells.Remove(cellIndex);
    }

    // =====================================================================
    // レジストリ操作ヘルパー
    // =====================================================================

    private void RegisterStationCell(int stationId, Vector2Int cell)
    {
        if (!stationIdsByCell.TryGetValue(cell, out var set))
        {
            set = new HashSet<int>();
            stationIdsByCell[cell] = set;
        }
        set.Add(stationId);
    }

    private void RemoveStation(int stationId, StationState st)
    {
        if (st.go != null) Destroy(st.go);
        stationStates.Remove(stationId);
        if (stationIdsByCell.TryGetValue(st.cell, out var set))
        {
            set.Remove(stationId);
            if (set.Count == 0) stationIdsByCell.Remove(st.cell);
        }
    }

    // =====================================================================
    // ユーティリティ
    // =====================================================================

    /// <summary>駅舎の接地高さ。CPU高さ配列のO(1)ルックアップ。</summary>
    private static float SampleGroundY(float x, float z)
    {
        return HeightmapTextureManager.GetTerrainHeight(new Vector3(x, 0f, z));
    }

    /// <summary>
    /// 駅舎プレハブを解決する。バリアント → default の順にフォールバックする
    /// (プロジェクト規約の「バリアント → タイプ → デフォルト」に従う)。
    /// </summary>
    private GameObject GetPrefab(int houseVariant)
    {
        if (ModelManager.Instance == null) return null;
        return ModelManager.Instance.GetModel($"Models/Stations/{houseVariant}", suppressWarning: true)
            ?? ModelManager.Instance.GetModel("Models/Stations/default", suppressWarning: true);
    }

    private void WarnMissingPrefabOnce(int houseVariant)
    {
        if (missingPrefabWarnedVariants.Add(houseVariant))
        {
            Debug.LogWarning($"[StationManager] Prefab not found: Models/Stations/{houseVariant} (仮箱で代替表示します)");
        }
    }
}
