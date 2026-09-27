using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// メッシュテストシーン用に、マッシュアップ建物(MODEL_MASHUP)のサブ建物配置計画を
/// ランダム生成する。
///
/// 実ゲームでは配置計画はバックエンド(genesis-core:
/// crud_buildings.generate_sub_building_layout + geo_buildings.generate_mashup_cells)が
/// 建物作成時に生成し、Unityは解決済みの BuildingSummary.sub_buildings を描画するだけ。
/// テストシーンにはバックエンドの配置結果が無いため、同じアルゴリズムをここへ移植して
/// sub_buildings を自前で組み立てる。バックエンド側の配置ロジックを変更した場合は
/// この移植も追従させること(ずれた場合の実害はテスト表示の再現度だけ)。
///
/// バックエンドとの意図的な差分:
/// - テストには接道情報が無いため、前面辺は常に「最長辺」フォールバック
///   (バックエンドの接道なし区画と同じ決定論的挙動)
/// - セルの区画内判定は shapely の within の代わりに「四隅の点内包」で行う
///   (テストの底面形状は凸のみなので判定結果は同値)
/// - model_prefix 未定義の棟は配置しない(バックエンドでは配信時に除外され描画されないため)
/// </summary>
public static class TestSubBuildingLayout
{
    // バックエンド(crud_buildings.MASHUP_FILL_RATIO)と同じ目標セル充填率。
    // 敷地の2割程度を意図的に空地として残し、詰め込みすぎを防ぐ。
    private const float FILL_RATIO = 0.8f;

    /// <summary>
    /// 土地区画のセル分割結果。バックエンド geo_buildings.generate_mashup_cells の戻り値に対応する。
    /// 座標系は区画中心を原点とするローカルXZ(テストの底面ポリゴンと同じ)。
    /// </summary>
    private class MashupGrid
    {
        public HashSet<(int i, int j)> Cells = new HashSet<(int i, int j)>();
        public Vector2 Origin;      // グリッド原点(グリッド軸で見たバウンディングボックスの最小角)
        public Vector2 U;           // 列方向(前面辺方向)の単位ベクトル
        public Vector2 V;           // 行方向の単位ベクトル(Uを反時計回りに90°回した向き)
        public float Angle;         // Uの方向角(度。XZ平面で+X基準の反時計回り)
        public Vector2 FrontA;      // 向きの基準にした前面辺の始点
        public Vector2 FrontB;      // 同終点
    }

    /// <summary>
    /// サブ建物の配置計画を生成する。
    ///
    /// 区画をセルへ決定論的に分割し、優先度順に配置する:
    /// 先頭(ID最小)の棟=施設の顔の1棟目を前面(最長辺)へ必ず配置 →
    /// 各棟の最低配置数(min_count)を充足 →
    /// 充填率が FILL_RATIO に達するまで weight(棟数比)÷(配置済み棟数+1) が最大の棟を
    /// 1棟ずつ追加する(D'Hondt方式。max_count 超過・収まらない棟は候補から外れる)。
    /// 棟数の構成は決定論で、位置だけがランダム。同じ棟種の2棟目以降は
    /// 既存の同種棟に隣接する位置を優先する(クラスタリング)。
    /// 棟の階数は親建物の floors を棟定義の min_floors〜max_floors でクランプして導出する
    /// (バックエンドでは配信時の _attach_sub_buildings が同じ導出を行う)。
    ///
    /// Returns:
    ///     subbuilding_master に棟定義が無いバリアント・マスター未初期化は null(=通常の1棟建物)。
    ///     区画が小さくセルが取れない・顔の棟が収まらない場合は空リスト。
    /// </summary>
    /// <param name="variant">建物バリアント</param>
    /// <param name="parcelPolygon">土地区画とみなすポリゴン(中心原点のローカルXZ座標)</param>
    /// <param name="parentFloors">親建物の階数(棟ごとの階数の導出元)</param>
    /// <param name="openPit">露天掘り(MODEL_OPEN_PIT)。前庭のセルだけに配置する</param>
    public static List<SubBuildingPlacement> Generate(int variant, Vector2[] parcelPolygon, int parentFloors, bool openPit = false)
    {
        if (MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized)
        {
            return null;
        }
        if (parcelPolygon == null || parcelPolygon.Length < 3)
        {
            return null;
        }

        List<KeyValuePair<int, MasterDataStore.SubBuildingMasterItem>> subs =
            MasterDataStore.Instance.GetSubBuildingsForVariant(variant);
        subs.RemoveAll(kvp => string.IsNullOrEmpty(kvp.Value.model_prefix));
        if (subs.Count == 0)
        {
            return null;
        }

        MashupGrid grid = BuildMashupCells(parcelPolygon, Constants.MASHUP_CELL_SIZE);
        if (openPit)
        {
            KeepYardCells(grid, parcelPolygon, Constants.MASHUP_CELL_SIZE);
        }
        if (grid.Cells.Count == 0)
        {
            Debug.LogWarning("[MeshTest] 区画が小さすぎるため、サブ建物を配置できませんでした。");
            return new List<SubBuildingPlacement>();
        }

        // 配信時の height_per_floor 解決(サブ建物優先→本体マスター)に使うフォールバック値。
        float? agentFloorHeight = null;
        try
        {
            var agentMaster = MasterDataStore.Instance.GetAgentMaster(variant);
            agentFloorHeight = agentMaster?.height_per_floor;
        }
        catch (KeyNotFoundException) { }

        var state = new LayoutState
        {
            Grid = grid,
            AgentFloorHeight = agentFloorHeight,
            ParentFloors = Mathf.Max(1, parentFloors),
        };
        foreach (var kvp in subs)
        {
            state.Counts[kvp.Key] = 0;
            state.CellsBySub[kvp.Key] = new HashSet<(int i, int j)>();
        }

        // 先頭(ID最小)の棟が施設の顔で、1棟目を前面(前面辺)に最も近い候補へ必ず置く。
        // 収まらない区画では配置計画全体を諦める(敷地装飾のみの描画になる)。
        if (!TryPlace(subs[0], preferFront: true, state))
        {
            Debug.LogWarning($"[MeshTest] 先頭のサブ建物{subs[0].Key}が区画に収まりませんでした。");
            return new List<SubBuildingPlacement>();
        }

        // 各棟の最低配置数を満たす(顔として配置済みの1棟も数に入る)。空きに収まらない棟は
        // 警告して諦める(顔の棟と違い全体を失敗させない)。
        foreach (var kvp in subs)
        {
            (int minCount, _) = CountBounds(kvp.Value);
            while (state.Counts[kvp.Key] < minCount)
            {
                if (!TryPlace(kvp, preferFront: false, state))
                {
                    Debug.LogWarning($"[MeshTest] サブ建物{kvp.Key}が区画に収まりませんでした" +
                        $"(最低配置数{minCount}のうち{state.Counts[kvp.Key]}棟で打ち切り)。");
                    break;
                }
            }
        }

        // 充填率が目標に達するまで、weight(棟数比)の比率を保つ棟を1棟ずつ追加する。
        // 「weight ÷ (配置済み棟数 + 1) が最大の棟」を選ぶ D'Hondt 方式で、同値は
        // subs の並び(ID昇順)の先頭を採る。占有セルは増える一方なので、
        // 一度どこにも収まらなくなった棟は候補から外してよい。
        var pool = subs.FindAll(kvp => Weight(kvp.Value) > 0f);
        int fillTarget = Mathf.CeilToInt(grid.Cells.Count * FILL_RATIO);
        while (state.Occupied.Count < fillTarget)
        {
            int bestIndex = -1;
            float bestQuotient = 0f;
            for (int i = 0; i < pool.Count; i++)
            {
                (_, int? maxCount) = CountBounds(pool[i].Value);
                if (maxCount.HasValue && state.Counts[pool[i].Key] >= maxCount.Value)
                {
                    continue;
                }
                float quotient = Weight(pool[i].Value) / (state.Counts[pool[i].Key] + 1);
                if (quotient > bestQuotient)
                {
                    bestQuotient = quotient;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0)
            {
                break;
            }
            if (!TryPlace(pool[bestIndex], preferFront: false, state))
            {
                pool.RemoveAt(bestIndex);
            }
        }

        return state.Placements;
    }

    /// <summary>
    /// 配置処理の途中経過。バックエンド generate_sub_building_layout のローカル変数群に対応する。
    /// </summary>
    private class LayoutState
    {
        public MashupGrid Grid;
        public float? AgentFloorHeight;
        public int ParentFloors;    // 親建物の階数(棟ごとの floors 導出元)
        public HashSet<(int i, int j)> Occupied = new HashSet<(int i, int j)>();
        public List<SubBuildingPlacement> Placements = new List<SubBuildingPlacement>();
        public Dictionary<int, int> Counts = new Dictionary<int, int>();                                  // 棟種→配置済み棟数
        public Dictionary<int, HashSet<(int i, int j)>> CellsBySub = new Dictionary<int, HashSet<(int i, int j)>>();  // 棟種→占有セル(クラスタリング用)
        public Dictionary<int, bool> FirstRotated = new Dictionary<int, bool>();                          // 棟種→1棟目の向き(整列用)
    }

    /// <summary>配置ウェイト(棟数比)。未定義は1.0、負値は0扱い。</summary>
    private static float Weight(MasterDataStore.SubBuildingMasterItem sub)
    {
        return Mathf.Max(0f, sub.weight ?? 1f);
    }

    /// <summary>
    /// 棟の (最低配置数, 最大配置数) を返す。
    /// min_count / max_count 未定義は下限0・上限無制限(null)。矛盾した定義(max &lt; min)は
    /// 下限を優先して上限を引き上げる。
    /// </summary>
    private static (int minCount, int? maxCount) CountBounds(MasterDataStore.SubBuildingMasterItem sub)
    {
        int minCount = sub.min_count ?? 0;
        int? maxCount = sub.max_count;
        if (maxCount.HasValue)
        {
            maxCount = Mathf.Max(maxCount.Value, minCount);
        }
        return (minCount, maxCount);
    }

    /// <summary>
    /// 指定の棟を1つ配置する。収まる場所が無ければ false。
    /// preferFront=true では前面辺に最も近い候補(同距離はグリッド座標で決定論的に解決)、
    /// false では同種棟への隣接候補(クラスタリング)を優先した上でランダムな候補を選ぶ。
    /// 向きはグリッド沿いと90°回転の両方を試す。
    /// </summary>
    private static bool TryPlace(
        KeyValuePair<int, MasterDataStore.SubBuildingMasterItem> sub, bool preferFront, LayoutState state)
    {
        MashupGrid grid = state.Grid;
        (int cellW, int cellH) = CellSpan(sub.Value);

        var candidates = new List<((int i, int j) anchor, int w, int h, bool rotated)>();
        foreach ((bool rotated, int w, int h) in new[] { (false, cellW, cellH), (true, cellH, cellW) })
        {
            foreach ((int i, int j) anchor in FindBlockAnchors(grid.Cells, state.Occupied, w, h))
            {
                candidates.Add((anchor, w, h, rotated));
            }
        }
        if (candidates.Count == 0)
        {
            return false;
        }

        ((int i, int j) anchor, int w, int h, bool rotated) chosen;
        if (preferFront)
        {
            chosen = candidates[0];
            (float d, int j, int i, int r) bestKey = FrontKey(chosen, grid);
            for (int c = 1; c < candidates.Count; c++)
            {
                (float d, int j, int i, int r) key = FrontKey(candidates[c], grid);
                if (key.CompareTo(bestKey) < 0)
                {
                    bestKey = key;
                    chosen = candidates[c];
                }
            }
        }
        else
        {
            var narrowed = ClusterCandidates(sub.Key, candidates, state);
            chosen = narrowed[Random.Range(0, narrowed.Count)];
        }

        for (int a = 0; a < chosen.w; a++)
        {
            for (int b = 0; b < chosen.h; b++)
            {
                state.Occupied.Add((chosen.anchor.i + a, chosen.anchor.j + b));
                state.CellsBySub[sub.Key].Add((chosen.anchor.i + a, chosen.anchor.j + b));
            }
        }
        state.Counts[sub.Key]++;
        if (!state.FirstRotated.ContainsKey(sub.Key))
        {
            state.FirstRotated[sub.Key] = chosen.rotated;
        }

        Vector2 blockCenter = BlockCenter(grid, chosen.anchor.i, chosen.anchor.j, chosen.w, chosen.h);
        // 棟の階数 = 親建物の floors を棟定義の階数範囲でクランプ(配信時導出と同じ規約)
        int minFloors = Mathf.Max(1, sub.Value.min_floors ?? 1);
        int maxFloors = Mathf.Max(minFloors, sub.Value.max_floors ?? minFloors);
        state.Placements.Add(new SubBuildingPlacement
        {
            subbuilding_id = sub.Key,
            model_prefix = sub.Value.model_prefix,
            // ポリゴンは区画中心原点のローカル座標なので、ブロック中心がそのまま相対positionになる
            position = new[] { blockCenter.x, blockCenter.y },
            rotation = grid.Angle + (chosen.rotated ? 90f : 0f),
            floors = Mathf.Clamp(state.ParentFloors, minFloors, maxFloors),
            height_per_floor = sub.Value.height_per_floor ?? state.AgentFloorHeight,
        });
        return true;
    }

    /// <summary>
    /// 同種棟の2棟目以降の候補を、既存の同種棟に隣接する位置へ絞り込む(クラスタリング)。
    /// 既存棟のセルに4近傍で接する候補だけを残し、さらに向き(90°回転の有無)が1棟目と
    /// 揃う候補があればそちらを優先する。該当が無ければ絞り込まない
    /// (孤立した空きにしか置けない場合でも配置自体は成立させる)。
    /// </summary>
    private static List<((int i, int j) anchor, int w, int h, bool rotated)> ClusterCandidates(
        int subbuildingId, List<((int i, int j) anchor, int w, int h, bool rotated)> candidates,
        LayoutState state)
    {
        HashSet<(int i, int j)> anchorCells = state.CellsBySub[subbuildingId];
        if (anchorCells.Count == 0)
        {
            return candidates;
        }

        var adjacent = new List<((int i, int j) anchor, int w, int h, bool rotated)>();
        foreach (var candidate in candidates)
        {
            bool touches = false;
            for (int a = 0; a < candidate.w && !touches; a++)
            {
                for (int b = 0; b < candidate.h && !touches; b++)
                {
                    int ci = candidate.anchor.i + a;
                    int cj = candidate.anchor.j + b;
                    touches = anchorCells.Contains((ci + 1, cj)) || anchorCells.Contains((ci - 1, cj))
                        || anchorCells.Contains((ci, cj + 1)) || anchorCells.Contains((ci, cj - 1));
                }
            }
            if (touches)
            {
                adjacent.Add(candidate);
            }
        }
        if (adjacent.Count == 0)
        {
            return candidates;
        }

        var aligned = adjacent.FindAll(c => c.rotated == state.FirstRotated[subbuildingId]);
        return aligned.Count > 0 ? aligned : adjacent;
    }

    /// <summary>顔の棟の前面寄せ判定キー。(前面辺への距離, j, i, 回転) の昇順比較で決定論的に選ぶ。</summary>
    private static (float d, int j, int i, int r) FrontKey(
        ((int i, int j) anchor, int w, int h, bool rotated) candidate, MashupGrid grid)
    {
        Vector2 center = BlockCenter(grid, candidate.anchor.i, candidate.anchor.j, candidate.w, candidate.h);
        float distance = PolylineUtils.DistanceToSegment(center, grid.FrontA, grid.FrontB);
        return (distance, candidate.anchor.j, candidate.anchor.i, candidate.rotated ? 1 : 0);
    }

    /// <summary>
    /// サブ建物の底面が専有するセル数 (前面辺方向, 奥行き方向) を返す。
    /// マスターの width / height はセル数単位(1単位=MASHUP_CELL_SIZE m)。未定義は1セル、端数は切り上げ。
    /// </summary>
    private static (int cellW, int cellH) CellSpan(MasterDataStore.SubBuildingMasterItem sub)
    {
        float width = (sub.width.HasValue && sub.width.Value > 0f) ? sub.width.Value : 1f;
        float height = (sub.height.HasValue && sub.height.Value > 0f) ? sub.height.Value : 1f;
        int cellW = Mathf.Max(1, Mathf.CeilToInt(width - 1e-6f));
        int cellH = Mathf.Max(1, Mathf.CeilToInt(height - 1e-6f));
        return (cellW, cellH);
    }

    /// <summary>cellW×cellH のブロックを空きセルだけで確保できる最小角 (i, j) の一覧を返す。</summary>
    private static List<(int i, int j)> FindBlockAnchors(
        HashSet<(int i, int j)> cells, HashSet<(int i, int j)> occupied, int cellW, int cellH)
    {
        var anchors = new List<(int i, int j)>();
        foreach ((int i, int j) in cells)
        {
            bool blockOk = true;
            for (int a = 0; a < cellW && blockOk; a++)
            {
                for (int b = 0; b < cellH && blockOk; b++)
                {
                    (int, int) cell = (i + a, j + b);
                    if (!cells.Contains(cell) || occupied.Contains(cell))
                    {
                        blockOk = false;
                    }
                }
            }
            if (blockOk)
            {
                anchors.Add((i, j));
            }
        }
        return anchors;
    }

    /// <summary>セル(i, j)を最小角とする cellW×cellH セルのブロック中心のローカル座標を返す。</summary>
    private static Vector2 BlockCenter(MashupGrid grid, int i, int j, int cellW, int cellH)
    {
        float su = (i + cellW * 0.5f) * Constants.MASHUP_CELL_SIZE;
        float sv = (j + cellH * 0.5f) * Constants.MASHUP_CELL_SIZE;
        return grid.Origin + grid.U * su + grid.V * sv;
    }

    /// <summary>
    /// 露天掘りの前庭(前面辺から OPEN_PIT_YARD_DEPTH 以内)に全体が入るセルだけを残す。
    /// 前面辺から敷地の奥へ向かう向きは、前面辺の中点から V 方向へわずかに進んだ点が区画に入るかで決める。
    /// </summary>
    private static void KeepYardCells(MashupGrid grid, Vector2[] polygon, float cellSize)
    {
        Vector2 middle = (grid.FrontA + grid.FrontB) * 0.5f;
        Vector2 inward = PolygonUtils.Contains(polygon, middle + grid.V * 0.05f) ? grid.V : -grid.V;
        float baseDepth = Vector2.Dot(grid.Origin - grid.FrontA, inward);
        float step = Vector2.Dot(grid.V, inward);
        grid.Cells.RemoveWhere(c => baseDepth + Mathf.Max(c.j * cellSize * step, (c.j + 1) * cellSize * step)
            > Constants.OPEN_PIT_YARD_DEPTH + 1e-6f);
    }

    /// <summary>
    /// ポリゴンをセルサイズ四方の正方形セルへ決定論的に分割する。
    /// グリッドの向きは最長辺に、原点はグリッド軸で見たバウンディングボックスの最小角に揃える。
    /// セル全体がポリゴン内に収まるものだけを採用し、縁の端数は隙間として残す。
    /// </summary>
    private static MashupGrid BuildMashupCells(Vector2[] polygon, float cellSize)
    {
        var grid = new MashupGrid();
        int n = polygon.Length;

        // 前面辺 = 最長辺(接道情報の無い区画のバックエンドフォールバックと同じ決定論)
        int bestIdx = 0;
        float bestSq = -1f;
        for (int i = 0; i < n; i++)
        {
            float sq = (polygon[(i + 1) % n] - polygon[i]).sqrMagnitude;
            if (sq > bestSq)
            {
                bestSq = sq;
                bestIdx = i;
            }
        }
        grid.FrontA = polygon[bestIdx];
        grid.FrontB = polygon[(bestIdx + 1) % n];

        Vector2 edge = grid.FrontB - grid.FrontA;
        grid.U = edge.sqrMagnitude > 1e-12f ? edge.normalized : Vector2.right;
        grid.V = new Vector2(-grid.U.y, grid.U.x);
        grid.Angle = Mathf.Atan2(grid.U.y, grid.U.x) * Mathf.Rad2Deg;

        // 全頂点をグリッド軸(U, V)へ射影し、バウンディングボックスの最小角を原点とする
        float minU = float.PositiveInfinity, minV = float.PositiveInfinity;
        float maxU = float.NegativeInfinity, maxV = float.NegativeInfinity;
        foreach (Vector2 p in polygon)
        {
            float su = Vector2.Dot(p, grid.U);
            float sv = Vector2.Dot(p, grid.V);
            minU = Mathf.Min(minU, su);
            maxU = Mathf.Max(maxU, su);
            minV = Mathf.Min(minV, sv);
            maxV = Mathf.Max(maxV, sv);
        }
        grid.Origin = grid.U * minU + grid.V * minV;

        int cols = Mathf.Max(0, Mathf.CeilToInt((maxU - minU) / cellSize - 1e-9f));
        int rows = Mathf.Max(0, Mathf.CeilToInt((maxV - minV) / cellSize - 1e-9f));

        // 境界上のセルが浮動小数点誤差で落ちないよう、判定はわずかに縮めた正方形で行う。
        // テストの底面形状は凸のみなので、四隅の点内包チェックで「セル全体が区画内」と同値になる。
        const float inset = 0.01f;
        for (int i = 0; i < cols; i++)
        {
            for (int j = 0; j < rows; j++)
            {
                float a = i * cellSize + inset;
                float b = (i + 1) * cellSize - inset;
                float c = j * cellSize + inset;
                float d = (j + 1) * cellSize - inset;
                bool allInside =
                    PolygonUtils.Contains(polygon, grid.Origin + grid.U * a + grid.V * c) &&
                    PolygonUtils.Contains(polygon, grid.Origin + grid.U * b + grid.V * c) &&
                    PolygonUtils.Contains(polygon, grid.Origin + grid.U * b + grid.V * d) &&
                    PolygonUtils.Contains(polygon, grid.Origin + grid.U * a + grid.V * d);
                if (allInside)
                {
                    grid.Cells.Add((i, j));
                }
            }
        }
        return grid;
    }
}
