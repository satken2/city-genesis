using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 入港する船の航路を求める純データの計算。海岸線までの距離を格子に取り、港側の終点を根にして
/// 岸に近い水域ほど割高にした最短経路木を張り、外洋の出発点から終点までの陸に触れない航路を作る。
/// Unity の Random・地形の高さを使わないので、バックグラウンドスレッドで動かせる。
/// 仕様は公開 V 文書(V_環境演出の乗り物.md §V.5)。
/// </summary>
public static class SeaRoute
{
    private const float ClearanceEpsilon = 1f;       // 航路の短絡・角の丸めで許す距離の誤差 [m]
    private const float DeficitWeight = 4f;          // 望ましい余裕に足りない水域の割増し(不足の割合あたり)
    private const float RootSearchRadius = 500f;     // 終点の近くで通れる格子点を探す範囲 [m]
    private const int CornerSegments = 8;            // 角の丸め1つあたりの分割数
    private const int StartAttempts = 30;            // 外洋の出発点を引き直す回数

    /// <summary>海岸線までの距離の格子。値は海で正・陸で負(<see cref="CoastlineStore.SignedDistance"/> の符号を反転)。</summary>
    public sealed class Grid
    {
        public readonly float minX, minZ, cell;
        public readonly int nx, nz;
        private readonly float[] clearance;

        /// <summary>矩形を cell 間隔の格子点で覆う。maxDistance より岸から遠い点は maxDistance に丸める。</summary>
        public Grid(float minX, float minZ, float maxX, float maxZ, float cell, float maxDistance)
        {
            this.minX = minX;
            this.minZ = minZ;
            this.cell = cell;
            nx = Mathf.Max(2, Mathf.CeilToInt((maxX - minX) / cell) + 1);
            nz = Mathf.Max(2, Mathf.CeilToInt((maxZ - minZ) / cell) + 1);
            var xs = new float[nx];
            var zs = new float[nz];
            for (int i = 0; i < nx; i++) xs[i] = minX + i * cell;
            for (int j = 0; j < nz; j++) zs[j] = minZ + j * cell;
            float[,] signed = CoastlineStore.SampleSignedDistanceGrid(xs, zs, maxDistance);
            clearance = new float[nx * nz];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                    clearance[i + j * nx] = -signed[i, j];
        }

        public int Count => clearance.Length;
        public float At(int index) => clearance[index];
        public Vector2 Point(int index) => new Vector2(minX + index % nx * cell, minZ + index / nx * cell);

        /// <summary>格子点の間を双線形補間した岸までの距離。格子の外(マップの外)は通れないものとして負の無限大。</summary>
        public float Sample(Vector2 p)
        {
            float fx = (p.x - minX) / cell, fz = (p.y - minZ) / cell;
            if (fx < 0f || fz < 0f || fx > nx - 1 || fz > nz - 1) return float.NegativeInfinity;
            int i = Mathf.Min((int)fx, nx - 2), j = Mathf.Min((int)fz, nz - 2);
            float tx = fx - i, tz = fz - j;
            int k = i + j * nx;
            return Mathf.Lerp(Mathf.Lerp(clearance[k], clearance[k + 1], tx), Mathf.Lerp(clearance[k + nx], clearance[k + nx + 1], tx), tz);
        }
    }

    /// <summary>船体が占める向きのある長方形。Axis は船首尾方向の単位ベクトル。</summary>
    public struct Hull
    {
        public Vector2 Center;
        public Vector2 Axis;
        public float HalfLength;
        public float HalfWidth;

        public bool Contains(Vector2 p, float margin)
        {
            Vector2 d = p - Center;
            return Mathf.Abs(Vector2.Dot(d, Axis)) <= HalfLength + margin
                && Mathf.Abs(d.x * Axis.y - d.y * Axis.x) <= HalfWidth + margin;
        }

        /// <summary>分離軸で判定する。gap は両者の間に要る隙間 [m]。</summary>
        public bool Overlaps(Hull other, float gap)
        {
            Vector2[] axes = { Axis, new Vector2(-Axis.y, Axis.x), other.Axis, new Vector2(-other.Axis.y, other.Axis.x) };
            foreach (Vector2 axis in axes)
            {
                float distance = Mathf.Abs(Vector2.Dot(other.Center - Center, axis));
                if (distance > Extent(axis) + other.Extent(axis) + gap) return false;
            }
            return true;
        }

        /// <summary>外周上の点を step 間隔で列挙する。</summary>
        public IEnumerable<Vector2> Outline(float step)
        {
            var side = new Vector2(-Axis.y, Axis.x);
            Vector2[] corners =
            {
                Center - Axis * HalfLength - side * HalfWidth, Center + Axis * HalfLength - side * HalfWidth,
                Center + Axis * HalfLength + side * HalfWidth, Center - Axis * HalfLength + side * HalfWidth,
            };
            for (int c = 0; c < 4; c++)
            {
                Vector2 a = corners[c], b = corners[(c + 1) % 4];
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / step));
                for (int s = 0; s < steps; s++) yield return Vector2.Lerp(a, b, (float)s / steps);
            }
        }

        private float Extent(Vector2 axis) =>
            Mathf.Abs(Vector2.Dot(Axis, axis)) * HalfLength + Mathf.Abs(Axis.y * axis.x - Axis.x * axis.y) * HalfWidth;
    }

    public sealed class Request
    {
        public Vector2[] approach;          // 航路の最後に通る点列。先頭が最短経路木の根
        public float minClearance;          // これより岸に近い水域は通らない [m]
        public float preferredClearance;    // これより岸に近い水域は距離を割増す [m]
        public float startClearance;        // 出発点に要る岸からの距離 [m]
        public float cornerTrim;            // 角を丸め始める距離の上限 [m]
        public Hull[] blockers;             // 他の船の接岸位置
        public float blockerMargin;         // blockers を広げる幅 [m]
        public Vector2[][] rings;           // 海岸線のリング(出発点が湖でないかの判定)
        public int seed;
    }

    /// <summary>
    /// 外洋の無作為な出発点から approach の末尾までの航路(点列)。出発点が見つからなければ null。
    /// 出発点は終点から水路でつながり、岸から startClearance 以上離れた外洋(どの海岸線リングにも含まれない)の格子点から選ぶ。
    /// </summary>
    public static Vector2[] Plan(Grid grid, Request request)
    {
        int n = grid.Count;
        var passable = new bool[n];
        for (int k = 0; k < n; k++)
            passable[k] = grid.At(k) >= request.minClearance && !Blocked(request, grid.Point(k));

        Vector2 target = request.approach[0];
        int root = NearestPassable(grid, passable, target);
        if (root < 0) return null;
        int[] parent = ShortestPathTree(grid, passable, root, request.preferredClearance, out float[] cost);

        int start = PickStart(grid, cost, request);
        if (start < 0) return null;

        var raw = new List<Vector2>();
        for (int k = start; k >= 0; k = parent[k]) raw.Add(grid.Point(k));
        raw.Add(target);
        List<Vector2> route = Pull(grid, request, raw);
        for (int i = 1; i < request.approach.Length; i++) route.Add(request.approach[i]);
        return Round(grid, request, route).ToArray();
    }

    private static bool Blocked(Request request, Vector2 p)
    {
        if (request.blockers == null) return false;
        foreach (Hull hull in request.blockers)
            if (hull.Contains(p, request.blockerMargin)) return true;
        return false;
    }

    /// <summary>岸までの距離。他の船の接岸位置(blockerMargin だけ広げる)の中は通れないものとして負の無限大。</summary>
    private static float Clearance(Grid grid, Request request, Vector2 p) =>
        Blocked(request, p) ? float.NegativeInfinity : grid.Sample(p);

    /// <summary>線分上の <see cref="Clearance"/> の最小値(格子の半分の間隔で標本を取る)。</summary>
    private static float SegmentMin(Grid grid, Request request, Vector2 a, Vector2 b)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / (grid.cell * .5f)));
        float min = float.PositiveInfinity;
        for (int s = 0; s <= steps; s++) min = Mathf.Min(min, Clearance(grid, request, Vector2.Lerp(a, b, (float)s / steps)));
        return min;
    }

    private static int NearestPassable(Grid grid, bool[] passable, Vector2 target)
    {
        int ci = Mathf.RoundToInt((target.x - grid.minX) / grid.cell);
        int cj = Mathf.RoundToInt((target.y - grid.minZ) / grid.cell);
        int reach = Mathf.CeilToInt(RootSearchRadius / grid.cell);
        int best = -1;
        float bestSq = float.PositiveInfinity;
        for (int j = Mathf.Max(0, cj - reach); j <= Mathf.Min(grid.nz - 1, cj + reach); j++)
        {
            for (int i = Mathf.Max(0, ci - reach); i <= Mathf.Min(grid.nx - 1, ci + reach); i++)
            {
                int k = i + j * grid.nx;
                if (!passable[k]) continue;
                float sq = (grid.Point(k) - target).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = k; }
            }
        }
        return best;
    }

    /// <summary>
    /// 根からの最短経路木(8近傍のダイクストラ)。辺の長さは、両端の岸までの距離の平均が望ましい余裕に足りない割合だけ割増す。
    /// 斜めの辺は両脇の格子点も通れるときだけ使う(岬の角をかすめない)。
    /// </summary>
    private static int[] ShortestPathTree(Grid grid, bool[] passable, int root, float preferred, out float[] cost)
    {
        int n = grid.Count;
        cost = new float[n];
        var parent = new int[n];
        for (int k = 0; k < n; k++) { cost[k] = float.PositiveInfinity; parent[k] = -1; }
        cost[root] = 0f;
        var heap = new MinHeap(1024);
        heap.Push(0f, root);
        while (heap.Count > 0)
        {
            heap.Pop(out float c, out int k);
            if (c > cost[k]) continue;
            int i = k % grid.nx, j = k / grid.nx;
            for (int dj = -1; dj <= 1; dj++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int ni = i + di, nj = j + dj;
                    if (ni < 0 || nj < 0 || ni >= grid.nx || nj >= grid.nz) continue;
                    int nk = ni + nj * grid.nx;
                    if (!passable[nk]) continue;
                    bool diagonal = di != 0 && dj != 0;
                    if (diagonal && (!passable[ni + j * grid.nx] || !passable[i + nj * grid.nx])) continue;
                    float mean = (grid.At(k) + grid.At(nk)) * .5f;
                    float penalty = 1f + DeficitWeight * Mathf.Max(0f, preferred - mean) / preferred;
                    float next = c + grid.cell * (diagonal ? 1.41421356f : 1f) * penalty;
                    if (next >= cost[nk]) continue;
                    cost[nk] = next;
                    parent[nk] = k;
                    heap.Push(next, nk);
                }
            }
        }
        return parent;
    }

    private static int PickStart(Grid grid, float[] cost, Request request)
    {
        var candidates = new List<int>();
        for (int k = 0; k < cost.Length; k++)
            if (!float.IsPositiveInfinity(cost[k]) && grid.At(k) >= request.startClearance) candidates.Add(k);
        var random = new System.Random(request.seed);
        for (int attempt = 0; attempt < StartAttempts && candidates.Count > 0; attempt++)
        {
            int k = candidates[random.Next(candidates.Count)];
            if (IsOpenSea(request.rings, grid.Point(k))) return k;
        }
        return -1;
    }

    /// <summary>どの海岸線リングにも含まれない点だけを外洋とする(陸地リングの内側の穴は湖)。</summary>
    private static bool IsOpenSea(Vector2[][] rings, Vector2 point)
    {
        foreach (Vector2[] ring in rings)
            if (ring.Length >= 3 && PolygonUtils.Contains(ring, point)) return false;
        return true;
    }

    /// <summary>
    /// 見通せる点まで一直線にする。短絡した線分は、置き換える区間の点の岸までの距離の最小値
    /// (ただし望ましい余裕が上限)より岸へ寄らないものだけ採る。狭い水路では格子の経路がそのまま残る。
    /// </summary>
    private static List<Vector2> Pull(Grid grid, Request request, List<Vector2> raw)
    {
        var result = new List<Vector2> { raw[0] };
        int i = 0;
        while (i < raw.Count - 1)
        {
            int best = i + 1;
            float floor = Mathf.Min(grid.Sample(raw[i]), grid.Sample(raw[i + 1]));
            for (int j = i + 2; j < raw.Count; j++)
            {
                floor = Mathf.Min(floor, grid.Sample(raw[j]));
                if (SegmentMin(grid, request, raw[i], raw[j]) < Mathf.Min(request.preferredClearance, floor) - ClearanceEpsilon) break;
                best = j;
            }
            result.Add(raw[best]);
            i = best;
        }
        return result;
    }

    /// <summary>
    /// 曲がり角を二次ベジエで丸める。丸めた曲線が両隣の線分より岸へ寄るなら、丸める距離を半分ずつ縮めて試し、
    /// だめなら角のまま残す。丸める距離は両隣の線分の半分までなので、隣の角の丸めと重ならない。
    /// </summary>
    private static List<Vector2> Round(Grid grid, Request request, List<Vector2> points)
    {
        var result = new List<Vector2> { points[0] };
        for (int k = 1; k < points.Count - 1; k++)
        {
            Vector2 prev = points[k - 1], cur = points[k], next = points[k + 1];
            float lenIn = Vector2.Distance(prev, cur), lenOut = Vector2.Distance(cur, next);
            if (lenIn < 1e-3f || lenOut < 1e-3f || Vector2.Angle(cur - prev, next - cur) < 2f)
            {
                result.Add(cur);
                continue;
            }
            Vector2 dirIn = (cur - prev) / lenIn, dirOut = (next - cur) / lenOut;
            float floor = Mathf.Min(request.preferredClearance, SegmentMin(grid, request, prev, cur), SegmentMin(grid, request, cur, next)) - ClearanceEpsilon;
            float trim = Mathf.Min(request.cornerTrim, lenIn * .5f, lenOut * .5f);
            bool rounded = false;
            for (int attempt = 0; attempt < 3 && !rounded; attempt++, trim *= .5f)
            {
                Vector2 a = cur - dirIn * trim, b = cur + dirOut * trim;
                rounded = true;
                for (int s = 1; s < CornerSegments && rounded; s++)
                    rounded = Clearance(grid, request, Bezier(a, cur, b, (float)s / CornerSegments)) >= floor;
                if (!rounded) continue;
                for (int s = 0; s <= CornerSegments; s++) result.Add(Bezier(a, cur, b, (float)s / CornerSegments));
            }
            if (!rounded) result.Add(cur);
        }
        result.Add(points[points.Count - 1]);
        return result;
    }

    private static Vector2 Bezier(Vector2 a, Vector2 control, Vector2 b, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * control + t * t * b;
    }

    /// <summary>ダイクストラ用の最小ヒープ(.NET Standard 2.1 に PriorityQueue が無いため)。</summary>
    private sealed class MinHeap
    {
        private float[] keys;
        private int[] values;
        public int Count { get; private set; }

        public MinHeap(int capacity) { keys = new float[capacity]; values = new int[capacity]; }

        public void Push(float key, int value)
        {
            if (Count == keys.Length)
            {
                System.Array.Resize(ref keys, Count * 2);
                System.Array.Resize(ref values, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int up = (i - 1) / 2;
                if (keys[up] <= key) break;
                keys[i] = keys[up];
                values[i] = values[up];
                i = up;
            }
            keys[i] = key;
            values[i] = value;
        }

        public void Pop(out float key, out int value)
        {
            key = keys[0];
            value = values[0];
            float lastKey = keys[--Count];
            int lastValue = values[Count];
            int i = 0;
            while (true)
            {
                int child = i * 2 + 1;
                if (child >= Count) break;
                if (child + 1 < Count && keys[child + 1] < keys[child]) child++;
                if (keys[child] >= lastKey) break;
                keys[i] = keys[child];
                values[i] = values[child];
                i = child;
            }
            keys[i] = lastKey;
            values[i] = lastValue;
        }
    }
}
