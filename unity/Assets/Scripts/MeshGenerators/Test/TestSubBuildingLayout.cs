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
    /// サブ建物の配置計画をランダム生成する。
    ///
    /// 区画をセルへ決定論的に分割し、優先度順に配置する:
    /// primary の棟を前面(最長辺)へ必ず1棟 → sole の棟を必ず1棟だけ(収まらなければ諦める) →
    /// その他の棟を充填率が FILL_RATIO に達するまでランダムに配置する(同じ棟の複数配置あり)。
    ///
    /// Returns:
    ///     subbuilding_master に棟定義が無いバリアント・マスター未初期化は null(=通常の1棟建物)。
    ///     区画が小さくセルが取れない・primary が収まらない場合は空リスト。
    /// </summary>
    /// <param name="variant">建物バリアント</param>
    /// <param name="parcelPolygon">土地区画とみなすポリゴン(中心原点のローカルXZ座標)</param>
    public static List<SubBuildingPlacement> Generate(int variant, Vector2[] parcelPolygon)
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

        var occupied = new HashSet<(int i, int j)>();
        var placements = new List<SubBuildingPlacement>();

        // primary は施設の顔となる棟なので、前面(前面辺)に最も近い候補へ必ず置く。
        int primaryIndex = subs.FindIndex(kvp => kvp.Value.primary);
        if (primaryIndex < 0)
        {
            primaryIndex = 0;
        }
        if (!TryPlace(subs[primaryIndex], preferFront: true, grid, occupied, placements, agentFloorHeight))
        {
            Debug.LogWarning($"[MeshTest] primaryサブ建物{subs[primaryIndex].Key}が区画に収まりませんでした。");
            return new List<SubBuildingPlacement>();
        }

        // sole の棟は必ず1棟だけ配置する。優先度は primary の次で、primary 配置後の
        // 空きに収まらない場合は配置を諦める(primary と違い全体を失敗させない)。
        // primary と sole は「1棟だけ」の棟なので、以降のランダム充填プールに入れない。
        var pool = new List<KeyValuePair<int, MasterDataStore.SubBuildingMasterItem>>();
        for (int i = 0; i < subs.Count; i++)
        {
            if (i == primaryIndex)
            {
                continue;
            }
            if (subs[i].Value.sole)
            {
                if (!TryPlace(subs[i], preferFront: false, grid, occupied, placements, agentFloorHeight))
                {
                    Debug.LogWarning($"[MeshTest] soleサブ建物{subs[i].Key}が区画に収まりませんでした。");
                }
            }
            else
            {
                pool.Add(subs[i]);
            }
        }

        // 残りの棟は、充填率が目標に達するまでランダムに選んで置いていく。
        // 占有セルは増える一方なので、一度どこにも収まらなくなった棟は候補から外してよい。
        int fillTarget = Mathf.CeilToInt(grid.Cells.Count * FILL_RATIO);
        while (pool.Count > 0 && occupied.Count < fillTarget)
        {
            int pick = Random.Range(0, pool.Count);
            if (!TryPlace(pool[pick], preferFront: false, grid, occupied, placements, agentFloorHeight))
            {
                pool.RemoveAt(pick);
            }
        }

        return placements;
    }

    /// <summary>
    /// 指定の棟を1つ配置する。収まる場所が無ければ false。
    /// preferFront=true では前面辺に最も近い候補(同距離はグリッド座標で決定論的に解決)、
    /// false ではランダムな候補を選ぶ。向きはグリッド沿いと90°回転の両方を試す。
    /// </summary>
    private static bool TryPlace(
        KeyValuePair<int, MasterDataStore.SubBuildingMasterItem> sub, bool preferFront,
        MashupGrid grid, HashSet<(int i, int j)> occupied, List<SubBuildingPlacement> placements,
        float? agentFloorHeight)
    {
        (int cellW, int cellH) = CellSpan(sub.Value);

        var candidates = new List<((int i, int j) anchor, int w, int h, bool rotated)>();
        foreach ((bool rotated, int w, int h) in new[] { (false, cellW, cellH), (true, cellH, cellW) })
        {
            foreach ((int i, int j) anchor in FindBlockAnchors(grid.Cells, occupied, w, h))
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
            chosen = candidates[Random.Range(0, candidates.Count)];
        }

        for (int a = 0; a < chosen.w; a++)
        {
            for (int b = 0; b < chosen.h; b++)
            {
                occupied.Add((chosen.anchor.i + a, chosen.anchor.j + b));
            }
        }

        Vector2 blockCenter = BlockCenter(grid, chosen.anchor.i, chosen.anchor.j, chosen.w, chosen.h);
        int minFloors = Mathf.Max(1, sub.Value.min_floors ?? 1);
        int maxFloors = Mathf.Max(minFloors, sub.Value.max_floors ?? minFloors);
        placements.Add(new SubBuildingPlacement
        {
            subbuilding_id = sub.Key,
            model_prefix = sub.Value.model_prefix,
            // ポリゴンは区画中心原点のローカル座標なので、ブロック中心がそのまま相対positionになる
            position = new[] { blockCenter.x, blockCenter.y },
            rotation = grid.Angle + (chosen.rotated ? 90f : 0f),
            floors = Random.Range(minFloors, maxFloors + 1),
            height_per_floor = sub.Value.height_per_floor ?? agentFloorHeight,
        });
        return true;
    }

    /// <summary>primary の前面寄せ判定キー。(前面辺への距離, j, i, 回転) の昇順比較で決定論的に選ぶ。</summary>
    private static (float d, int j, int i, int r) FrontKey(
        ((int i, int j) anchor, int w, int h, bool rotated) candidate, MashupGrid grid)
    {
        Vector2 center = BlockCenter(grid, candidate.anchor.i, candidate.anchor.j, candidate.w, candidate.h);
        float distance = DistanceToSegment(center, grid.FrontA, grid.FrontB);
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
                    IsPointInPolygon(grid.Origin + grid.U * a + grid.V * c, polygon) &&
                    IsPointInPolygon(grid.Origin + grid.U * b + grid.V * c, polygon) &&
                    IsPointInPolygon(grid.Origin + grid.U * b + grid.V * d, polygon) &&
                    IsPointInPolygon(grid.Origin + grid.U * a + grid.V * d, polygon);
                if (allInside)
                {
                    grid.Cells.Add((i, j));
                }
            }
        }
        return grid;
    }

    /// <summary>点がポリゴン内にあるかを交差回数の偶奇(even-odd)ルールで判定する。</summary>
    private static bool IsPointInPolygon(Vector2 point, Vector2[] polygon)
    {
        bool inside = false;
        int n = polygon.Length;
        int j = n - 1;
        for (int i = 0; i < n; i++)
        {
            Vector2 vi = polygon[i];
            Vector2 vj = polygon[j];
            if (((vi.y > point.y) != (vj.y > point.y)) &&
                (point.x < (vj.x - vi.x) * (point.y - vi.y) / (vj.y - vi.y) + vi.x))
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }

    /// <summary>点から線分 a-b までの距離を返す。primary の前面寄せ判定に使う。</summary>
    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float abLenSq = ab.sqrMagnitude;
        float t = abLenSq > 1e-9f ? Mathf.Clamp01(Vector2.Dot(point - a, ab) / abLenSq) : 0f;
        return Vector2.Distance(point, a + ab * t);
    }
}
