using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

[Serializable]
public class PropScatterItem
{
    public int prop_scatter_id;
    public int prop_type;
    // GeoJSON MultiPolygon の coordinates ([[[外周リング], [穴リング], ...], ...])。
    // ネストが深いため JArray のまま受けて ParseRegion で解釈する。
    public JArray region;
    public List<float> center_point;
    public float density;
    public int seed;
    public int version;
}

[Serializable]
public class PropInstanceItem
{
    public int prop_instance_id;
    public int prop_type;
    public float x;
    public float z;
    public float rotation;
    public float scale;
    public int version;
}

[Serializable]
public class PropListResponse
{
    public List<PropScatterItem> scatters;
    public List<PropInstanceItem> instances;
}

/// <summary>
/// 配置・散布済みのプロップ(森、木、岩など)をバックエンドから取得し、
/// 3D空間にインスタンス化して描画・管理するマネージャー。プロップはNearティア相当の
/// セル範囲(BaseApiDataManager.LoadLodCells のデフォルト = nearCells)にロードされる。
///
/// ロード方式:
/// - Building/Path 等と同じく、必要セル全体のマージBBOXを1リクエストで一括取得する。
///   散布プロップは「散布範囲全体を覆うBBOX」で空間インデックスに登録されているため、
///   セル単位に取得すると同じ散布レコードが複数セルの応答に重複して返る。レコード単位の
///   レジストリ(scatterStates / instanceStates)で管理し、同一レコードの重複生成を防ぐ。
/// - version差分更新の慣習に従い、HTTP取得は毎回全件・オブジェクトの再生成は version が
///   変わったレコードのみ行う。これにより ReloadAllData(編集確定・エポック完了後)で
///   新規配置プロップの出現・重なり除去による region 変更・レコード削除が反映される。
///
/// 散布の決定性(公開ドキュメント M_地形プロップ.md の契約):
/// - 散布点は seed を初期値とする決定的な乱数で生成する。乱数列は散布範囲全体の
///   グリッド走査順に消費するため、一部のセルだけを後から実体化しても同じ点列が得られる
///   (セル単位の差分生成が可能)。
/// - 散布点は「外周リングの内側かつ、どの穴リングにも入らない」場所にのみ生成する。
///   region はドーナツ形(穴つき)や複数塊(MultiPolygon)になり得る。
///
/// 地形追従:
/// - プロップはY座標を持たず常に地表に貼り付く。生成時に CPU高さ配列からYをサンプルし、
///   地形ロード完了後は CellDataLoader.TerrainFollowUpCoroutine から
///   AdjustPropHeightsForCellsCoroutine が呼ばれ、地形が実際に更新されたセルのプロップの
///   Y座標を再設定する(建物・道路と同じ2段階ロード。メッシュ再生成は不要でY再設定のみ)。
/// </summary>
public class PropManager : BaseApiDataManager
{
    public static PropManager Instance { get; private set; }

    /// <summary>1散布レコードあたりの生成数上限(パフォーマンス保護)。超過時は警告を出して打ち切る。</summary>
    private const int MAX_SCATTER_OBJECTS_PER_RECORD = 5000;

    /// <summary>
    /// region の1パート(外周リング+穴リング群)。座標は Vector2(x, z)。
    /// </summary>
    private class RegionPart
    {
        public List<Vector2> outer;
        public List<List<Vector2>> holes;
        public float minX, maxX, minZ, maxZ;
    }

    /// <summary>
    /// 散布レコード1件の生成状態。オブジェクトは所属セルごとに保持し、
    /// セル単位のアンロード・地形追従を可能にする。
    /// </summary>
    private class ScatterState
    {
        public int version;
        /// <summary>セル → そのセル内に実体化済みのオブジェクト一覧。</summary>
        public Dictionary<Vector2Int, List<GameObject>> objectsByCell = new Dictionary<Vector2Int, List<GameObject>>();
        /// <summary>散布生成を最後まで走り切ったセル。途中キャンセル時はマークされず、次回ロードで作り直される。</summary>
        public HashSet<Vector2Int> generatedCells = new HashSet<Vector2Int>();
    }

    private class InstanceState
    {
        public int version;
        public GameObject go;
        public Vector2Int cell;
    }

    // レコード単位のレジストリ。
    private readonly Dictionary<int, ScatterState> scatterStates = new Dictionary<int, ScatterState>();
    private readonly Dictionary<int, InstanceState> instanceStates = new Dictionary<int, InstanceState>();

    // セル → レコードIDの逆引きインデックス。UnloadCell / 地形追従で全レコード走査を避けるために維持する。
    // 登録・削除は必ず RegisterScatterCell / RegisterInstanceCell / UnloadCell / ClearAllCells を通すこと。
    private readonly Dictionary<Vector2Int, HashSet<int>> scatterIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();
    private readonly Dictionary<Vector2Int, HashSet<int>> instanceIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();

    // 同じ警告を毎リロード繰り返さないためのデデュープ。
    private readonly HashSet<int> missingPrefabWarnedTypes = new HashSet<int>();
    private readonly HashSet<int> scatterCapWarnedIds = new HashSet<int>();
    private readonly HashSet<int> invalidRegionWarnedIds = new HashSet<int>();

    // 進行中のマージロードコルーチン。新しいロード開始時に停止して仕切り直す。
    private Coroutine mergedLoadCoroutine;

    private bool isVisible = true;

    /// <summary>
    /// プロップの表示/非表示を切り替える。
    /// </summary>
    public void SetVisible(bool visible)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[PropManager] SetVisible: current={isVisible} -> next={visible}");
        if (isVisible == visible) return;
        isVisible = visible;

        Transform container = GetContainer("PropContainer");
        if (container != null)
        {
            container.gameObject.SetActive(visible);
        }

        if (isVisible)
        {
            // 表示ONに切り替わったら最新データをリロードする
            if (CellDataLoader.Instance != null)
            {
                CellDataLoader.Instance.ReloadAllData();
            }
        }
        else
        {
            // 非表示に切り替わったら全オブジェクトを破棄する
            ClearAllCells();
        }
    }

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
    /// HTTP取得は毎回全件(マージBBOX)行い、再生成の要否はレコードごとの version 比較で決める
    /// (他マネージャーと同じ version 差分更新の慣習)。
    /// </summary>
    public override void LoadCells(List<Vector2Int> neededCells, bool isForce)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[PropManager] LoadCells invoked. count={neededCells.Count}, isForce={isForce}, isVisible={isVisible}");
        if (!isVisible)
        {
            ClearAllCells();
            return;
        }

        // 不要になったセルを特定してアンロード
        var neededSet = new HashSet<Vector2Int>(neededCells);
        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!neededSet.Contains(cell))
            {
                toUnload.Add(cell);
            }
        }
        foreach (var cell in toUnload)
        {
            UnloadCell(cell);
        }

        // ロード開始時点で必要セルをすべて「ロード済み(=内容物を持ちうるセル)」として登録する。
        // ロードが途中でキャンセルされても、次回の LoadCells がこの集合を基準にアンロードを
        // 行うため、中途半端に生成されたオブジェクトが残留しない。
        loadedCells.Clear();
        loadedCells.UnionWith(neededSet);

        // 進行中のロードがあれば停止して最新のセル集合で仕切り直す。
        // (yield 境界はレコード単位なので、停止してもレジストリの整合性は壊れない)
        if (mergedLoadCoroutine != null)
        {
            StopCoroutine(mergedLoadCoroutine);
        }
        mergedLoadCoroutine = StartCoroutine(LoadMergedProps(new List<Vector2Int>(neededCells), isForce));
    }

    /// <summary>
    /// 必要セル全体のマージBBOXでプロップを一括取得し、レコード単位の差分同期を行う。
    /// 生成・破棄はフレーム予算(PerformanceConfig.FrameBudgetMs)を超えるたびに次フレームへ譲る。
    /// </summary>
    private IEnumerator LoadMergedProps(List<Vector2Int> neededCells, bool isForce)
    {
        if (neededCells.Count == 0)
        {
            mergedLoadCoroutine = null;
            yield break;
        }

        CalculateMergedBBox(neededCells, out float minX, out float minZ, out float maxX, out float maxZ);
        string path = $"/props?bbox={minX},{minZ},{maxX},{maxZ}";

        PropListResponse propData = null;
        string errorMsg = null;
        yield return ApiClient.Get<PropListResponse>(path,
            (res) => { propData = res; },
            (err) => { errorMsg = err; }
        );

        if (errorMsg != null || propData == null)
        {
            Debug.LogError($"[PropManager] プロップ一覧の取得に失敗しました: {errorMsg}");
            mergedLoadCoroutine = null;
            yield break;
        }

        // 通信中に非表示へ切り替わった場合は何もしない(ClearAllCells 済み)。
        if (!isVisible)
        {
            mergedLoadCoroutine = null;
            yield break;
        }

        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () =>
            $"[PropManager] マージロード: scatters={propData.scatters?.Count ?? 0}, instances={propData.instances?.Count ?? 0}, cells={neededCells.Count}");

        var neededSet = new HashSet<Vector2Int>(neededCells);
        Transform container = GetContainer("PropContainer");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ---- 個別配置(instances)の同期 ----
        var returnedInstanceIds = new HashSet<int>();
        if (propData.instances != null)
        {
            foreach (var inst in propData.instances)
            {
                Vector2Int cell = PositionToCell(new Vector3(inst.x, 0f, inst.z));
                // マージBBOXは必要セル集合の外接矩形のため、必要セル外のレコードも混ざる。
                if (!neededSet.Contains(cell)) continue;
                returnedInstanceIds.Add(inst.prop_instance_id);

                if (instanceStates.TryGetValue(inst.prop_instance_id, out InstanceState st)
                    && !isForce && st.version == inst.version && st.go != null)
                {
                    continue; // version 不変なら再生成しない
                }
                if (st != null)
                {
                    RemoveInstance(inst.prop_instance_id, st);
                }

                GameObject prefab = GetPrefab(inst.prop_type);
                if (prefab == null)
                {
                    WarnMissingPrefabOnce(inst.prop_type);
                    continue;
                }

                GameObject go = Instantiate(prefab, container);
                go.transform.position = new Vector3(inst.x, SampleGroundY(inst.x, inst.z), inst.z);
                go.transform.rotation = Quaternion.Euler(0f, inst.rotation, 0f);
                go.transform.localScale = Vector3.one * inst.scale;
                go.SetActive(true);
                instanceStates[inst.prop_instance_id] = new InstanceState { version = inst.version, go = go, cell = cell };
                RegisterInstanceCell(inst.prop_instance_id, cell);

                if (sw.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                {
                    yield return null;
                    sw.Restart();
                }
            }
        }

        // 応答に含まれなかったキャッシュ済みレコードはバックエンドで削除されたものとして破棄する。
        // (ロード済みセルに実体を持つレコードのBBOXは必ずマージBBOXと重なるため、
        //  削除以外の理由で応答から漏れることはない)
        List<int> staleInstanceIds = null;
        foreach (var kvp in instanceStates)
        {
            if (!returnedInstanceIds.Contains(kvp.Key))
            {
                (staleInstanceIds ??= new List<int>()).Add(kvp.Key);
            }
        }
        if (staleInstanceIds != null)
        {
            foreach (int id in staleInstanceIds)
            {
                if (instanceStates.TryGetValue(id, out var st)) RemoveInstance(id, st);
            }
        }

        // ---- 範囲散布(scatters)の同期 ----
        var returnedScatterIds = new HashSet<int>();
        if (propData.scatters != null)
        {
            foreach (var scatter in propData.scatters)
            {
                returnedScatterIds.Add(scatter.prop_scatter_id);

                List<RegionPart> parts = ParseRegion(scatter.region);
                if (parts.Count == 0)
                {
                    if (invalidRegionWarnedIds.Add(scatter.prop_scatter_id))
                    {
                        Debug.LogWarning($"[PropManager] scatter {scatter.prop_scatter_id} の region が解釈できません");
                    }
                    continue;
                }

                bool hasState = scatterStates.TryGetValue(scatter.prop_scatter_id, out ScatterState state);
                if (hasState && (isForce || state.version != scatter.version))
                {
                    // region / density / seed / prop_type が変わった(version が上がった)ため全面作り直し。
                    DestroyScatterRecord(scatter.prop_scatter_id, state);
                    hasState = false;
                }
                if (!hasState)
                {
                    state = new ScatterState { version = scatter.version };
                    scatterStates[scatter.prop_scatter_id] = state;
                }

                // 実体化すべきセル = 散布範囲BBOXが重なるセル ∩ 必要セル。
                // そのうち未生成のセルだけを差分生成する(生成済みセルのオブジェクトは維持)。
                var cellsToGenerate = CollectScatterTargetCells(parts, neededSet);
                cellsToGenerate.ExceptWith(state.generatedCells);
                if (cellsToGenerate.Count == 0) continue;

                if (scatter.density <= 0f) continue;
                GameObject prefab = GetPrefab(scatter.prop_type);
                if (prefab == null)
                {
                    WarnMissingPrefabOnce(scatter.prop_type);
                    continue;
                }

                // StartCoroutine ではなく直接 yield return し、親の StopCoroutine で確実に停止させる。
                yield return GenerateScatterObjects(scatter, state, parts, cellsToGenerate, prefab, container);
            }
        }

        // 応答に含まれなかった散布レコードは削除済みとして全オブジェクトを破棄する。
        List<int> staleScatterIds = null;
        foreach (var kvp in scatterStates)
        {
            if (!returnedScatterIds.Contains(kvp.Key))
            {
                (staleScatterIds ??= new List<int>()).Add(kvp.Key);
            }
        }
        if (staleScatterIds != null)
        {
            foreach (int id in staleScatterIds)
            {
                if (scatterStates.TryGetValue(id, out var state)) DestroyScatterRecord(id, state);
            }
        }

        mergedLoadCoroutine = null;
    }

    /// <summary>
    /// 散布レコードのオブジェクトを対象セルへ差分生成する。
    /// 乱数列(ジッタ・回転・スケール)は散布範囲全体のグリッド走査順に、実体化するセルに
    /// かかわらず必ず消費する。これにより「どのセルを後から実体化しても同じ点列」となり、
    /// セル単位の差分生成とアンロード→再ロードで散布結果がずれない。
    /// </summary>
    private IEnumerator GenerateScatterObjects(
        PropScatterItem scatter, ScatterState state, List<RegionPart> parts,
        HashSet<Vector2Int> cellsToGenerate, GameObject prefab, Transform container)
    {
        // 前回の生成がキャンセルされて中途半端に残ったオブジェクトがあれば先に破棄する
        // (generatedCells にマークされていないセルには不完全な実体が残っている可能性がある)。
        foreach (var cell in cellsToGenerate)
        {
            if (state.objectsByCell.TryGetValue(cell, out var stale))
            {
                foreach (var go in stale)
                {
                    if (go != null) Destroy(go);
                }
                state.objectsByCell.Remove(cell);
            }
            // 途中キャンセルされてもアンロード時の後始末(UnloadCell)が拾えるよう、
            // 生成開始時点で逆引きインデックスに登録しておく。
            RegisterScatterCell(scatter.prop_scatter_id, cell);
        }

        // 密度(本/ha = 本/10000m²)から配置グリッド間隔を求める。過密による生成爆発を防ぐ下限つき。
        float side = Mathf.Sqrt(10000f / scatter.density);
        if (side < 0.5f) side = 0.5f;

        var rng = new System.Random(scatter.seed);
        int passed = 0;       // ポリゴン内に入った点の通算数(セルの実体化有無に依存しない決定的なカウント)
        int candidates = 0;
        bool capped = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var part in parts)
        {
            if (capped) break;
            for (float gx = part.minX; gx <= part.maxX && !capped; gx += side)
            {
                for (float gz = part.minZ; gz <= part.maxZ && !capped; gz += side)
                {
                    // グリッド中心への決定的なジッタ。乱数は候補点ごとに必ず消費する。
                    float jx = gx + (float)(rng.NextDouble() - 0.5) * side;
                    float jz = gz + (float)(rng.NextDouble() - 0.5) * side;
                    candidates++;

                    if (PointInPart(jx, jz, part))
                    {
                        // 回転・スケールも「ポリゴン内の点」ごとに必ず消費する。
                        float rot = (float)(rng.NextDouble() * 360.0);
                        // スケールは基準1.0に対し±20%の揺らぎ
                        float scl = 0.8f + (float)rng.NextDouble() * 0.4f;

                        passed++;
                        if (passed > MAX_SCATTER_OBJECTS_PER_RECORD)
                        {
                            capped = true;
                            break;
                        }

                        Vector2Int cellIdx = PositionToCell(new Vector3(jx, 0f, jz));
                        if (cellsToGenerate.Contains(cellIdx))
                        {
                            GameObject go = Instantiate(prefab, container);
                            go.transform.position = new Vector3(jx, SampleGroundY(jx, jz), jz);
                            go.transform.rotation = Quaternion.Euler(0f, rot, 0f);
                            go.transform.localScale = Vector3.one * scl;
                            go.SetActive(true);

                            if (!state.objectsByCell.TryGetValue(cellIdx, out var list))
                            {
                                list = new List<GameObject>();
                                state.objectsByCell[cellIdx] = list;
                            }
                            list.Add(go);

                            if (sw.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                            {
                                yield return null;
                                sw.Restart();
                            }
                        }
                    }

                    // 実体化ゼロの広域スキャンでもフレームを占有しないよう定期的に予算を確認する。
                    if ((candidates & 2047) == 0 && sw.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                    {
                        yield return null;
                        sw.Restart();
                    }
                }
            }
        }

        if (capped && scatterCapWarnedIds.Add(scatter.prop_scatter_id))
        {
            Debug.LogWarning($"[PropManager] scatter {scatter.prop_scatter_id} の散布数が上限 {MAX_SCATTER_OBJECTS_PER_RECORD} に達したため打ち切りました (density={scatter.density})");
        }

        // 最後まで走り切った場合のみ生成済みとしてマークする。
        // 途中で StopCoroutine された場合はマークされず、次回ロード時に該当セルが作り直される。
        foreach (var cell in cellsToGenerate)
        {
            state.generatedCells.Add(cell);
        }

        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () =>
            $"[PropManager] scatter {scatter.prop_scatter_id} (type {scatter.prop_type}): parts={parts.Count}, density={scatter.density}, grid={side:F2}m, cells={cellsToGenerate.Count}, passed={passed}");
    }

    /// <summary>
    /// 地形が更新されたセルのプロップのY座標を地表に合わせ直す。
    /// CellDataLoader.TerrainFollowUpCoroutine から「実際に地形が更新されたセル ∩ nearCells」を
    /// 対象に呼ばれる(建物・道路と同じ地形追従フロー)。プロップはメッシュ再生成が不要なため、
    /// 全ティア共通で transform.position.y の再設定のみ行う。
    /// フレーム予算(PerformanceConfig.FrameBudgetMs)を超えるたびに次フレームへ譲る。
    /// </summary>
    public IEnumerator AdjustPropHeightsForCellsCoroutine(List<Vector2Int> cells)
    {
        if (!isVisible || cells == null || cells.Count == 0) yield break;

        // Terrainタイル差し替え直後のフレームキャッシュ(古い高さ)を無効化する。
        // 高さ取得はCPU高さ配列が主経路だが、未構築時のフォールバックが GetGroundHeight を通るため。
        Utils.InvalidateHeightCache();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int adjusted = 0;

        foreach (var cell in cells)
        {
            // 併走するマージロードコルーチンが辞書を書き換える可能性があるため、
            // スナップショットを取ってから走査する(yield をまたぐ列挙の破壊防止)。
            if (instanceIdsByCell.TryGetValue(cell, out var instIds))
            {
                foreach (int id in new List<int>(instIds))
                {
                    // 本コルーチンは CellDataLoader.TerrainFollowUpCoroutine から直接 yield されるため、
                    // 1プロップの位置調整例外を漏らすと残りのプロップ・後続の建物/道路追従・
                    // FinishTerrainFollowUp(deferred抑制とカメラロックの解除)まで巻き込んで中断し、
                    // 都市全体の地形追従が恒久的に止まる。1個の失敗はそのプロップだけに留めて続行する
                    // (yield をまたがない位置調整のみを包む)。
                    try
                    {
                        if (!instanceStates.TryGetValue(id, out var st) || st.go == null) continue;
                        Vector3 pos = st.go.transform.position;
                        pos.y = SampleGroundY(pos.x, pos.z);
                        st.go.transform.position = pos;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"PropManager: 地形追従の高さ調整に失敗 instance_id={id}: {e}");
                    }
                    adjusted++;
                    if ((adjusted & 63) == 0 && sw.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                    {
                        yield return null;
                        sw.Restart();
                    }
                }
            }

            if (scatterIdsByCell.TryGetValue(cell, out var scatterIds))
            {
                foreach (int id in new List<int>(scatterIds))
                {
                    if (!scatterStates.TryGetValue(id, out var state)) continue;
                    if (!state.objectsByCell.TryGetValue(cell, out var list)) continue;
                    foreach (var go in new List<GameObject>(list))
                    {
                        // 上記インスタンスと同じ理由で、1個の位置調整の失敗が追従全体を
                        // 中断しないよう捕捉して続行する(yield をまたがない位置調整のみを包む)。
                        try
                        {
                            if (go == null) continue;
                            Vector3 pos = go.transform.position;
                            pos.y = SampleGroundY(pos.x, pos.z);
                            go.transform.position = pos;
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"PropManager: 地形追従の高さ調整に失敗 scatter_id={id}: {e}");
                        }
                        adjusted++;
                        if ((adjusted & 63) == 0 && sw.ElapsedMilliseconds > PerformanceConfig.FrameBudgetMs)
                        {
                            yield return null;
                            sw.Restart();
                        }
                    }
                }
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[PropManager] 地形追従: {adjusted}個のプロップの高さを再設定 ({cells.Count}セル)");
    }

    /// <summary>
    /// 全セルのプロップオブジェクトと内部状態をクリアする。
    /// </summary>
    public override void ClearAllCells()
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, "[PropManager] ClearAllCells invoked.");
        if (mergedLoadCoroutine != null)
        {
            StopCoroutine(mergedLoadCoroutine);
            mergedLoadCoroutine = null;
        }

        foreach (var kvp in instanceStates)
        {
            if (kvp.Value.go != null) Destroy(kvp.Value.go);
        }
        instanceStates.Clear();
        instanceIdsByCell.Clear();

        foreach (var kvp in scatterStates)
        {
            foreach (var cellKvp in kvp.Value.objectsByCell)
            {
                foreach (var go in cellKvp.Value)
                {
                    if (go != null) Destroy(go);
                }
            }
        }
        scatterStates.Clear();
        scatterIdsByCell.Clear();

        loadedCells.Clear();
    }

    /// <summary>
    /// 指定されたセルのプロップオブジェクトを破棄する。
    /// 散布レコードは該当セル分のみ破棄し、他セルの実体は維持する。
    /// </summary>
    protected override void UnloadCell(Vector2Int cellIndex)
    {
        int destroyed = 0;

        if (instanceIdsByCell.TryGetValue(cellIndex, out var instIds))
        {
            foreach (int id in instIds)
            {
                if (instanceStates.TryGetValue(id, out var st))
                {
                    if (st.go != null) { Destroy(st.go); destroyed++; }
                    instanceStates.Remove(id);
                }
            }
            instanceIdsByCell.Remove(cellIndex);
        }

        if (scatterIdsByCell.TryGetValue(cellIndex, out var scatterIds))
        {
            foreach (int id in scatterIds)
            {
                if (!scatterStates.TryGetValue(id, out var state)) continue;
                if (state.objectsByCell.TryGetValue(cellIndex, out var list))
                {
                    foreach (var go in list)
                    {
                        if (go != null) { Destroy(go); destroyed++; }
                    }
                    state.objectsByCell.Remove(cellIndex);
                }
                state.generatedCells.Remove(cellIndex);
                // どのセルにも実体・生成マークが残っていなければレコードごと忘れる
                // (次に必要になったときのマージロードで再生成される)。
                if (state.objectsByCell.Count == 0 && state.generatedCells.Count == 0)
                {
                    scatterStates.Remove(id);
                }
            }
            scatterIdsByCell.Remove(cellIndex);
        }

        loadedCells.Remove(cellIndex);
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => $"[PropManager] Unloaded cell {cellIndex}: destroyed {destroyed} objects");
    }

    // =====================================================================
    // レジストリ操作ヘルパー(逆引きインデックスの同期は必ずここを通す)
    // =====================================================================

    private void RegisterScatterCell(int scatterId, Vector2Int cell)
    {
        if (!scatterIdsByCell.TryGetValue(cell, out var set))
        {
            set = new HashSet<int>();
            scatterIdsByCell[cell] = set;
        }
        set.Add(scatterId);
    }

    private void RegisterInstanceCell(int instanceId, Vector2Int cell)
    {
        if (!instanceIdsByCell.TryGetValue(cell, out var set))
        {
            set = new HashSet<int>();
            instanceIdsByCell[cell] = set;
        }
        set.Add(instanceId);
    }

    private void RemoveInstance(int instanceId, InstanceState st)
    {
        if (st.go != null) Destroy(st.go);
        instanceStates.Remove(instanceId);
        if (instanceIdsByCell.TryGetValue(st.cell, out var set))
        {
            set.Remove(instanceId);
            if (set.Count == 0) instanceIdsByCell.Remove(st.cell);
        }
    }

    /// <summary>散布レコードの全オブジェクト・逆引き・状態を破棄する。</summary>
    private void DestroyScatterRecord(int scatterId, ScatterState state)
    {
        var cells = new HashSet<Vector2Int>(state.generatedCells);
        foreach (var kvp in state.objectsByCell)
        {
            cells.Add(kvp.Key);
            foreach (var go in kvp.Value)
            {
                if (go != null) Destroy(go);
            }
        }
        foreach (var cell in cells)
        {
            if (scatterIdsByCell.TryGetValue(cell, out var set))
            {
                set.Remove(scatterId);
                if (set.Count == 0) scatterIdsByCell.Remove(cell);
            }
        }
        scatterStates.Remove(scatterId);
    }

    // =====================================================================
    // ジオメトリ・ユーティリティ
    // =====================================================================

    /// <summary>
    /// プロップの接地高さを取得する。CPU高さ配列のO(1)ルックアップ
    /// (未構築時は Utils.GetGroundHeight にフォールバック)。
    /// </summary>
    private static float SampleGroundY(float x, float z)
    {
        return HeightmapTextureManager.GetTerrainHeight(new Vector3(x, 0f, z));
    }

    private GameObject GetPrefab(int propType)
    {
        if (ModelManager.Instance == null) return null;
        return ModelManager.Instance.GetModel($"Models/Props/{propType}");
    }

    private void WarnMissingPrefabOnce(int propType)
    {
        if (missingPrefabWarnedTypes.Add(propType))
        {
            Debug.LogWarning($"[PropManager] Prefab not found: Models/Props/{propType}");
        }
    }

    /// <summary>
    /// 散布範囲のBBOXが重なるセル ∩ 必要セル を収集する。
    /// </summary>
    private HashSet<Vector2Int> CollectScatterTargetCells(List<RegionPart> parts, HashSet<Vector2Int> neededSet)
    {
        var result = new HashSet<Vector2Int>();
        float cellSize = TerrainManager.CellSize;
        foreach (var part in parts)
        {
            int cx0 = Mathf.FloorToInt(part.minX / cellSize);
            int cx1 = Mathf.FloorToInt(part.maxX / cellSize);
            int cz0 = Mathf.FloorToInt(part.minZ / cellSize);
            int cz1 = Mathf.FloorToInt(part.maxZ / cellSize);
            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    var cell = new Vector2Int(cx, cz);
                    if (neededSet.Contains(cell)) result.Add(cell);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// GeoJSON MultiPolygon の coordinates ([[[外周リング], [穴リング], ...], ...]) を
    /// パートの一覧(外周+穴)にパースする。各Polygonの先頭リングが外周、以降が穴(GeoJSONの規約)。
    /// 外周が3頂点未満のパートは無効として読み飛ばす。
    /// </summary>
    private static List<RegionPart> ParseRegion(JArray region)
    {
        var parts = new List<RegionPart>();
        if (region == null) return parts;

        try
        {
            foreach (var polyToken in region)
            {
                var rings = polyToken as JArray;
                if (rings == null || rings.Count == 0) continue;

                var outer = ParseRing(rings[0] as JArray);
                if (outer == null || outer.Count < 3) continue;

                var part = new RegionPart
                {
                    outer = outer,
                    holes = new List<List<Vector2>>(),
                    minX = float.MaxValue,
                    maxX = float.MinValue,
                    minZ = float.MaxValue,
                    maxZ = float.MinValue,
                };
                for (int i = 1; i < rings.Count; i++)
                {
                    var hole = ParseRing(rings[i] as JArray);
                    if (hole != null && hole.Count >= 3) part.holes.Add(hole);
                }
                foreach (var v in outer)
                {
                    if (v.x < part.minX) part.minX = v.x;
                    if (v.x > part.maxX) part.maxX = v.x;
                    if (v.y < part.minZ) part.minZ = v.y;
                    if (v.y > part.maxZ) part.maxZ = v.y;
                }
                parts.Add(part);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[PropManager] regionのパースに失敗しました: {e.Message}\n{e.StackTrace}");
        }

        return parts;
    }

    /// <summary>リング([[x, z], ...])を Vector2(x, z) のリストにパースする。</summary>
    private static List<Vector2> ParseRing(JArray ringArray)
    {
        if (ringArray == null) return null;
        var ring = new List<Vector2>(ringArray.Count);
        foreach (var pt in ringArray)
        {
            var coords = pt as JArray;
            if (coords != null && coords.Count >= 2)
            {
                ring.Add(new Vector2((float)coords[0], (float)coords[1]));
            }
        }
        return ring;
    }

    /// <summary>
    /// 点がパート内(外周リングの内側、かつどの穴リングにも入らない)にあるかを判定する。
    /// </summary>
    private static bool PointInPart(float x, float z, RegionPart part)
    {
        if (x < part.minX || x > part.maxX || z < part.minZ || z > part.maxZ) return false;
        if (!PointInRing(x, z, part.outer)) return false;
        foreach (var hole in part.holes)
        {
            if (PointInRing(x, z, hole)) return false;
        }
        return true;
    }

    /// <summary>XZ平面上での点内包判定 (Even-Oddアルゴリズム)。Vector2 は (x, z)。</summary>
    private static bool PointInRing(float x, float z, List<Vector2> ring)
    {
        bool inside = false;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = ring[i].x, zi = ring[i].y;
            float xj = ring[j].x, zj = ring[j].y;
            bool intersect = ((zi > z) != (zj > z)) &&
                             (x < (xj - xi) * (z - zi) / (zj - zi) + xi);
            if (intersect) inside = !inside;
        }
        return inside;
    }
}
