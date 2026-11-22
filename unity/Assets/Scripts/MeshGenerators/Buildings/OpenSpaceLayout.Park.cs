using System;
using System.Collections.Generic;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.OverlayNG;
using UnityEngine;

/// <summary>
/// 公園(MODEL_PARK)の配置計画。接道辺を基準にした局所座標(s=辺に沿う向き、t=敷地の奥)で作るので、敷地を回しても移しても同じ形になる。
/// 入口と広場を点として置き、近い点どうしを曲線の園路で結ぶ。点の数は敷地の広さで決まるので、園路の本数も広さに応じて増える。
/// 木の濃さは density(visual_variant3)、それ以外のゆらぎは seed(visual_variant2)で決まる。
/// 小物は sizes にある部品だけを実寸で置く。番号付きの部品(site_tree1, 2, …)は1から連番である分だけ使う。
/// </summary>
public static partial class OpenSpaceLayout
{
    const int MaxParkGates = 8;
    const int MaxParkNodes = 18;
    const int MaxParkTrees = 3000;
    const int MaxParkShrubs = 2000;
    const double ParkCurbWidth = .14;   // 舗装の縁石の幅(m)

    /// <summary>園路が結ぶ点。入口は敷地の境界上、広場は敷地の中にある。</summary>
    sealed class ParkNode
    {
        public double S, T;
        public double R;            // 広場の半径。入口と、園路が交わるだけの点は0
        public bool Gate;
        public double Ns, Nt;       // 入口から敷地の中へ向かう単位ベクトル
        public double Ws, Wt;       // 入口の境界上の点
        public string Feature;      // 広場の中央に置く部品。無ければ null
        public bool Play;           // 遊び場
        public double Fs, Ft;       // 最初につながった園路の向き(広場の部品の正面)
        public int Cell;
    }

    sealed class ParkEdge
    {
        public int A, B;
        public int Flow;            // この園路を通って最初の広場へ向かう入口の数。0 は枝の園路
        public double Width;
        public List<Coordinate> Line;
    }

    /// <summary>決定論の乱数列(xorshift32)。</summary>
    struct ParkRandom
    {
        uint state;
        public ParkRandom(uint seed, uint salt) { state = Hash(seed * 0x9E3779B1u + salt * 0x85EBCA77u + 1u) | 1u; }
        public double Next()
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0xFFFFFF) / (double)0x1000000;
        }
        public double Range(double lo, double hi) => lo + (hi - lo) * Next();
    }

    /// <summary>なめらかな値ノイズ(0〜1)。木立と開けた芝生、樹種のまとまりに使う。</summary>
    static double ParkNoise(uint seed, double x, double y)
    {
        double fx = Math.Floor(x), fy = Math.Floor(y);
        int xi = (int)fx, yi = (int)fy;
        double tx = x - fx, ty = y - fy;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        double Corner(int cx, int cy) => (Hash(seed ^ (uint)(cx * 73856093) ^ (uint)(cy * 19349663)) & 0xFFFFFF) / (double)0xFFFFFF;
        double a = Corner(xi, yi), b = Corner(xi + 1, yi), c = Corner(xi, yi + 1), d = Corner(xi + 1, yi + 1);
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }

    /// <summary>
    /// 面の輪郭の線分を格子に入れ、点から輪郭までの距離を引く。木や低木の候補を数万点ためすので、
    /// NTS の距離計算(点ごとにオブジェクトを作る)を使わない。
    /// </summary>
    sealed class ParkEdgeIndex
    {
        readonly double cell, minX, minY;
        readonly int width, height;
        readonly List<int>[] buckets;
        readonly List<double> segments = new List<double>();
        public ParkEdgeIndex(Geometry g, double cellSize)
        {
            var env = g.EnvelopeInternal;
            cell = cellSize; minX = env.MinX - cell; minY = env.MinY - cell;
            width = Math.Max(1, (int)Math.Ceiling((env.Width + 2 * cell) / cell));
            height = Math.Max(1, (int)Math.Ceiling((env.Height + 2 * cell) / cell));
            buckets = new List<int>[width * height];
            foreach (var poly in Polygons(g))
            {
                Add(poly.ExteriorRing.Coordinates);
                for (int i = 0; i < poly.NumInteriorRings; i++) Add(poly.GetInteriorRingN(i).Coordinates);
            }
        }
        void Add(Coordinate[] ring)
        {
            for (int i = 0; i + 1 < ring.Length; i++)
            {
                int index = segments.Count / 4;
                segments.Add(ring[i].X); segments.Add(ring[i].Y); segments.Add(ring[i + 1].X); segments.Add(ring[i + 1].Y);
                int x0 = Clamp((int)((Math.Min(ring[i].X, ring[i + 1].X) - minX) / cell), width), x1 = Clamp((int)((Math.Max(ring[i].X, ring[i + 1].X) - minX) / cell), width);
                int y0 = Clamp((int)((Math.Min(ring[i].Y, ring[i + 1].Y) - minY) / cell), height), y1 = Clamp((int)((Math.Max(ring[i].Y, ring[i + 1].Y) - minY) / cell), height);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        (buckets[y * width + x] ??= new List<int>()).Add(index);
            }
        }
        static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;
        /// <summary>輪郭までの距離。max より遠ければ max を返す。</summary>
        public double Distance(double x, double y, double max)
        {
            double best = max * max;
            int x0 = Clamp((int)((x - max - minX) / cell), width), x1 = Clamp((int)((x + max - minX) / cell), width);
            int y0 = Clamp((int)((y - max - minY) / cell), height), y1 = Clamp((int)((y + max - minY) / cell), height);
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    var bucket = buckets[cy * width + cx];
                    if (bucket == null) continue;
                    foreach (int i in bucket)
                    {
                        double ax = segments[4 * i], ay = segments[4 * i + 1], bx = segments[4 * i + 2], by = segments[4 * i + 3];
                        double dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
                        double k = len < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len));
                        double px = ax + dx * k - x, py = ay + dy * k - y, d = px * px + py * py;
                        if (d < best) best = d;
                    }
                }
            return Math.Sqrt(best);
        }
    }

    /// <summary>置いた円(木・低木・小物)どうしの間隔を調べる格子。</summary>
    sealed class ParkDiscs
    {
        readonly double cell;
        readonly Dictionary<long, List<int>> buckets = new Dictionary<long, List<int>>();
        readonly List<double> discs = new List<double>();
        public ParkDiscs(double cellSize) { cell = cellSize; }
        long Key(double x, double y) => ((long)(int)Math.Floor(x / cell) << 32) ^ (uint)(int)Math.Floor(y / cell);
        public void Add(double x, double y, double r)
        {
            long key = Key(x, y);
            if (!buckets.TryGetValue(key, out var list)) buckets.Add(key, list = new List<int>());
            list.Add(discs.Count / 3);
            discs.Add(x); discs.Add(y); discs.Add(r);
        }
        /// <summary>中心間の距離が (r + 相手の半径) × factor より近い円があるか。reach は相手の半径の上限。</summary>
        public bool Near(double x, double y, double r, double factor, double reach)
        {
            double range = (r + reach) * factor;
            int x0 = (int)Math.Floor((x - range) / cell), x1 = (int)Math.Floor((x + range) / cell);
            int y0 = (int)Math.Floor((y - range) / cell), y1 = (int)Math.Floor((y + range) / cell);
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!buckets.TryGetValue(((long)cx << 32) ^ (uint)cy, out var list)) continue;
                    foreach (int i in list)
                    {
                        double dx = discs[3 * i] - x, dy = discs[3 * i + 1] - y, limit = (r + discs[3 * i + 2]) * factor;
                        if (dx * dx + dy * dy < limit * limit) return true;
                    }
                }
            return false;
        }
    }

    /// <summary>番号付きの部品を1から連番である分だけ集める。</summary>
    static List<string> ParkSeries(IReadOnlyDictionary<string, Vector2> sizes, string name)
    {
        var parts = new List<string>();
        if (sizes == null) return parts;
        for (int n = 1; sizes.ContainsKey(name + n); n++) parts.Add(name + n);
        return parts;
    }

    /// <summary>接道辺の中点を原点にした局所座標の敷地。接道辺が t=0 に厳密に乗るよう 0.1mm に丸める(重なり判定の浮動小数点の失敗を防ぐ)。</summary>
    static Geometry LocalSite(Plan p, Frame f)
    {
        var ring = new Coordinate[p.Polygon.Length + 1];
        for (int i = 0; i < p.Polygon.Length; i++)
        {
            var c = f.Local(p.Polygon[i]);
            ring[i] = new Coordinate(Math.Round(c.X * 1e4) / 1e4, Math.Round(c.Y * 1e4) / 1e4);
        }
        ring[ring.Length - 1] = ring[0].Copy();
        return Largest(Nts.CreatePolygon(ring).Buffer(0));
    }

    static void Park(Plan p, uint seed, float density, IReadOnlyDictionary<string, Vector2> sizes)
    {
        Vector2 u = (p.Polygon[1] - p.Polygon[0]).normalized;
        if (u.sqrMagnitude < .5f) u = Vector2.right;
        var f = new Frame { O = (p.Polygon[0] + p.Polygon[1]) * .5f, U = u, V = new Vector2(-u.y, u.x) };
        Geometry site = LocalSite(p, f);
        if (site == null || site.Area < 12) return;
        double scale = Math.Sqrt(site.Area);
        double halfFront = Vector2.Distance(p.Polygon[0], p.Polygon[1]) * .5;

        // 園路の幅と、園路を敷地の境界から離す幅。細い敷地では幅を詰めてもう一度ためす。
        double mainWidth = Math.Max(1.8, Math.Min(4.5, 1.6 + scale * .022));
        var nodes = new List<ParkNode>();
        var edges = new List<ParkEdge>();
        foreach (var setting in new[] { new[] { 1.2, mainWidth }, new[] { .4, Math.Min(mainWidth, 1.6) } })
        {
            nodes.Clear(); edges.Clear();
            mainWidth = setting[1];
            var lane = Largest(site.Buffer(-(setting[0] + mainWidth * .5), Round()));
            if (lane == null || lane.Area < 4) continue;
            ParkNetwork(site, lane, setting[0] + mainWidth * .5, scale, halfFront, mainWidth, seed, sizes, nodes, edges);
            if (edges.Count > 0) break;
        }

        // 面: 入口から最初の広場へ向かう園路と広場は舗装、枝の園路は土。交わる所の内側の角は膨らませて縮める操作で丸める。
        var pavedParts = new List<Geometry>(); var softParts = new List<Geometry>(); var sandParts = new List<Geometry>();
        foreach (var e in edges)
            (e.Flow > 0 ? pavedParts : softParts).Add(Nts.CreateLineString(e.Line.ToArray()).Buffer(e.Width * .5, Round()));
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (!n.Gate && n.R > 0) (n.Play ? sandParts : pavedParts).Add(ParkBlob(n, seed, i));
        }
        Geometry paved = pavedParts.Count > 0 ? OverlayNGRobust.Union(pavedParts) : null;
        Geometry soft = softParts.Count > 0 ? OverlayNGRobust.Union(softParts) : null;
        Geometry sand = sandParts.Count > 0 ? OverlayNGRobust.Union(sandParts) : null;
        double fillet = Math.Min(2.5, mainWidth);
        if (paved != null) paved = And(paved.Buffer(fillet, Round()).Buffer(-fillet, Round()), site);
        if (soft != null) soft = And(soft.Buffer(fillet * .6, Round()).Buffer(-fillet * .6, Round()), site);
        // 遊び場の砂地は園路で切らない(園路は砂地のへりで途切れ、向こう側から続く)。
        if (sand != null && paved != null) paved = Minus(paved, sand);
        if (soft != null && paved != null) soft = Minus(soft, paved);
        if (soft != null && sand != null) soft = Minus(soft, sand);
        Geometry hard = null;
        foreach (var g in new[] { paved, soft, sand })
            if (g != null && !g.IsEmpty) hard = hard == null ? g : Union(hard, g);
        if (soft != null) Emit(p, f, soft, "site_gravel", .012f);
        if (sand != null)
        {
            Emit(p, f, sand, "site_sand", .014f);
            Emit(p, f, Minus(sand, sand.Buffer(-ParkCurbWidth, Round())), "site_curb", .02f);
        }
        if (paved != null)
        {
            Emit(p, f, paved, "site_paving", .014f);
            Emit(p, f, And(Minus(paved, paved.Buffer(-ParkCurbWidth, Round())), site.Buffer(-.05)), "site_curb", .02f);
        }
        foreach (var e in edges)
            for (int i = 0; i < e.Line.Count; i += 4) p.AccessCells.Add(f.World(e.Line[i].X, e.Line[i].Y));
        p.HasLoop = edges.Count >= nodes.Count && nodes.Count > 1;

        if (sizes == null || sizes.Count == 0) return;
        var placer = new ParkPlacer { Plan = p, F = f, Sizes = sizes, Site = PreparedGeometryFactory.Prepare(site.Buffer(-.15)),
            Hard = hard != null ? PreparedGeometryFactory.Prepare(hard) : null, HardArea = hard != null ? new IndexedPointInAreaLocator(hard) : null,
            HardEdges = hard != null ? new ParkEdgeIndex(hard, 4) : null, SiteEdges = new ParkEdgeIndex(site, Math.Max(8, scale)), Crowns = new ParkDiscs(6), Objects = new ParkDiscs(4) };
        ParkFurniture(placer, nodes, edges, mainWidth, seed, sand);
        ParkPlanting(placer, site, nodes, scale, seed, Mathf.Clamp01(density));
    }

    /// <summary>広場の輪郭。真円にせず、ゆるい凹凸を付けた閉曲線にする。</summary>
    static Geometry ParkBlob(ParkNode n, uint seed, int index)
    {
        const int count = 40;
        var ring = new Coordinate[count + 1];
        double p1 = Rand(seed, 900 + index) * Math.PI * 2, p2 = Rand(seed, 930 + index) * Math.PI * 2;
        // 凹凸は半径の内側だけに付ける(半径は広場が敷地とほかの広場に収まる上限)。
        for (int i = 0; i < count; i++)
        {
            double a = Math.PI * 2 * i / count;
            double r = n.R * (.9 + .07 * Math.Sin(2 * a + p1) + .03 * Math.Sin(3 * a + p2));
            ring[i] = new Coordinate(n.S + Math.Cos(a) * r, n.T + Math.Sin(a) * r);
        }
        ring[count] = ring[0].Copy();
        return Nts.CreatePolygon(ring);
    }

    /// <summary>入口と広場を決め、近い点どうしを園路で結ぶ。lane は園路の中心線を通せる範囲で、敷地を laneInset だけ内側へ引いた形。</summary>
    static void ParkNetwork(Geometry site, Polygon lane, double laneInset, double scale, double halfFront, double mainWidth, uint seed,
        IReadOnlyDictionary<string, Vector2> sizes, List<ParkNode> nodes, List<ParkEdge> edges)
    {
        var env = lane.EnvelopeInternal;
        double h = Math.Max(1.0, Math.Min(6.0, scale / 45));
        int width = Math.Max(1, (int)Math.Ceiling(env.Width / h)), height = Math.Max(1, (int)Math.Ceiling(env.Height / h));
        var inside = new bool[width * height];
        var clear = new double[width * height];
        var locator = new IndexedPointInAreaLocator(lane);
        var siteEdges = new ParkEdgeIndex(site, Math.Max(8, scale));
        var probe = new Coordinate();
        double reach = scale * .5 + 4;
        double X(int i) => env.MinX + (i % width + .5) * h;
        double Y(int i) => env.MinY + (i / width + .5) * h;
        bool any = false;
        for (int i = 0; i < inside.Length; i++)
        {
            probe.X = X(i); probe.Y = Y(i);
            inside[i] = locator.Locate(probe) == Location.Interior;
            if (!inside[i]) continue;
            any = true;
            clear[i] = siteEdges.Distance(probe.X, probe.Y, reach);
        }
        if (!any) return;
        int Nearest(double s, double t)
        {
            int best = -1; double bestD = double.MaxValue;
            for (int i = 0; i < inside.Length; i++)
            {
                if (!inside[i]) continue;
                double d = (X(i) - s) * (X(i) - s) + (Y(i) - t) * (Y(i) - t);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ---- 入口: 接道辺に1つ、残りは外周に沿ってほぼ等間隔。角のそばは避ける。
        var ring = Oriented(((Polygon)site).ExteriorRing.Coordinates, false);
        double perimeter = 0;
        for (int i = 0; i + 1 < ring.Length; i++) perimeter += ring[i].Distance(ring[i + 1]);
        int gateCount = Math.Max(1, Math.Min(MaxParkGates, (int)Math.Round(perimeter / 75)));
        double front = (Rand(seed, 40) - .5) * halfFront * .8;
        double frontArc = 0, walked = 0, bestFront = double.MaxValue;
        for (int i = 0; i + 1 < ring.Length; i++)
        {
            double len = ring[i].Distance(ring[i + 1]);
            if (len > 1e-9)
            {
                double k = Math.Max(0, Math.Min(1, ((front - ring[i].X) * (ring[i + 1].X - ring[i].X) + (0 - ring[i].Y) * (ring[i + 1].Y - ring[i].Y)) / (len * len)));
                double dx = ring[i].X + (ring[i + 1].X - ring[i].X) * k - front, dy = ring[i].Y + (ring[i + 1].Y - ring[i].Y) * k;
                if (dx * dx + dy * dy < bestFront) { bestFront = dx * dx + dy * dy; frontArc = walked + len * k; }
            }
            walked += len;
        }
        for (int g = 0; g < gateCount; g++)
        {
            double arc = frontArc + perimeter * (g + (g == 0 ? 0 : (Rand(seed, 50 + g) - .5) * .35)) / gateCount;
            arc = (arc % perimeter + perimeter) % perimeter;
            walked = 0;
            for (int i = 0; i + 1 < ring.Length; i++)
            {
                double len = ring[i].Distance(ring[i + 1]);
                if (arc > walked + len && i + 2 < ring.Length) { walked += len; continue; }
                double margin = mainWidth * .5 + 1.0;
                if (len < margin * 2 + .2) break;
                double along = Math.Max(margin, Math.Min(len - margin, arc - walked));
                double ds = (ring[i + 1].X - ring[i].X) / len, dt = (ring[i + 1].Y - ring[i].Y) / len;
                var node = new ParkNode { Gate = true, Ws = ring[i].X + ds * along, Wt = ring[i].Y + dt * along, Ns = -dt, Nt = ds };
                node.S = node.Ws + node.Ns * (laneInset + .05); node.T = node.Wt + node.Nt * (laneInset + .05);
                probe.X = node.S; probe.Y = node.T;
                if (locator.Locate(probe) != Location.Interior) break;
                bool crowded = false;
                foreach (var other in nodes) crowded |= Math.Sqrt((other.Ws - node.Ws) * (other.Ws - node.Ws) + (other.Wt - node.Wt) * (other.Wt - node.Wt)) < mainWidth * 3 + 4;
                if (crowded) break;
                node.Cell = Nearest(node.S, node.T);
                nodes.Add(node);
                break;
            }
        }
        if (nodes.Count == 0) return;
        int gates = nodes.Count;

        // ---- 広場: 境界から最も離れた所から順に、すでに置いた点から遠い所へ置く。中身は収まる物を番号順に割り当てる。
        var features = ParkSeries(sizes, "site_feature");
        bool playAvailable = ParkSeries(sizes, "site_play").Count > 0;
        var used = new HashSet<string>();
        int want = Math.Max(1, Math.Min(MaxParkNodes, (int)(scale / 14 + .5)));
        double spacing = Math.Max(5, scale * .09);
        double edge = laneInset - mainWidth * .5;
        for (int k = 0; k < want; k++)
        {
            int best = -1; double bestScore = 0;
            for (int i = 0; i < inside.Length; i++)
            {
                if (!inside[i]) continue;
                double x = X(i), y = Y(i), near = double.MaxValue;
                foreach (var n in nodes)
                {
                    double d = Math.Sqrt((n.S - x) * (n.S - x) + (n.T - y) * (n.T - y)) - n.R;
                    near = Math.Min(near, d);
                }
                if (near < spacing) continue;
                // 最初の広場は境界から最も離れた所(敷地のまん中)。2つ目からは、置いた点から遠く、境界に寄りすぎない所。
                double score = (k == 0 ? clear[i] : Math.Min(near, clear[i] * 2.2)) * (.85 + .3 * ParkNoise(seed + 11, x / (scale * .2) + k, y / (scale * .2)));
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) break;
            var node = new ParkNode { S = X(best), T = Y(best), Cell = best };
            double room = clear[best] - edge - .2;
            foreach (var n in nodes)
                if (!n.Gate) room = Math.Min(room, Math.Sqrt((n.S - node.S) * (n.S - node.S) + (n.T - node.T) * (n.T - node.T)) - n.R - spacing * .6);
            double cap = scale * .17;
            if (k == 1 && playAvailable && room >= 4)
            {
                node.Play = true;
                node.R = Math.Min(room, Math.Max(4, Math.Min(10, cap)));
            }
            else
            {
                foreach (string feature in features)
                {
                    if (used.Contains(feature)) continue;
                    // 部品の外接円に、まわりを歩く幅を足した広さが要る。広場は部品に対して広げすぎない。
                    double need = sizes[feature].magnitude * .5 + 2.2;
                    if (need > Math.Min(room, cap)) continue;
                    node.Feature = feature; used.Add(feature);
                    node.R = Math.Max(need, Math.Min(Math.Min(room, need + (k == 0 ? 5 : 2.5)), scale * (k == 0 ? .09 : .07)));
                    break;
                }
                // 置く物の無い点は、ベンチのある小さな休憩所か、園路が交わるだけの点(最初の広場は必ず休憩所にする)。
                if (node.Feature == null && room >= 1.2 && (k == 0 || Rand(seed, 70 + k) < .7)) node.R = Math.Min(room, Math.Max(mainWidth * .5 + 1.1, Math.Min(3.2, scale * .035)));
            }
            nodes.Add(node);
        }
        if (nodes.Count == gates)
        {
            // 広場を置けない細い敷地は、入口から最も遠い所まで園路を1本だけ通す。
            int far = -1; double farD = -1;
            var fromGate = ParkDijkstra(inside, width, height, h, nodes[0].Cell, out _);
            for (int i = 0; i < inside.Length; i++) if (inside[i] && fromGate[i] < double.MaxValue && fromGate[i] > farD) { farD = fromGate[i]; far = i; }
            if (far < 0 || farD < 3) return;
            nodes.Add(new ParkNode { S = X(far), T = Y(far), Cell = far });
        }

        // ---- 結び方: 敷地の中を通る距離で測り、広場どうしは「2点の間に、どちらにもより近い第三の点が無い」組を結ぶ
        // (相対近傍グラフ。全体がつながり、点が増えると自然に輪ができる)。入口は最も近い広場へ結ぶ。
        int count = nodes.Count;
        var dist = new double[count][];
        var parents = new int[count][];
        for (int i = 0; i < count; i++) dist[i] = ParkDijkstra(inside, width, height, h, nodes[i].Cell, out parents[i]);
        double D(int a, int b) => dist[a][nodes[b].Cell];
        var linked = new List<ParkEdge>();
        for (int a = gates; a < count; a++)
            for (int b = a + 1; b < count; b++)
            {
                if (D(a, b) == double.MaxValue) continue;
                bool blocked = false;
                for (int c = gates; c < count && !blocked; c++)
                    blocked = c != a && c != b && Math.Max(D(a, c), D(c, b)) < D(a, b);
                if (!blocked) linked.Add(new ParkEdge { A = a, B = b });
            }
        for (int g = 0; g < gates; g++)
        {
            int best = -1;
            for (int c = gates; c < count; c++) if (D(g, c) < double.MaxValue && (best < 0 || D(g, c) < D(g, best))) best = c;
            if (best >= 0) linked.Add(new ParkEdge { A = g, B = best });
        }
        // 園路が交わるだけの点で行き止まりになる園路は外す。
        for (bool pruned = true; pruned;)
        {
            pruned = false;
            for (int c = gates + 1; c < count && !pruned; c++)
            {
                if (nodes[c].R > 0) continue;
                int degree = 0, only = -1;
                for (int i = 0; i < linked.Count; i++) if (linked[i].A == c || linked[i].B == c) { degree++; only = i; }
                if (degree == 1) { linked.RemoveAt(only); pruned = true; }
            }
        }
        // 園路の太さ: それぞれの入口から最初の広場までの最短の道筋をたどり、通る入口の数を数える。合流するほど太くなり、どの入口の道筋でもない園路は細い土の道になる。
        var graph = new double[count];
        var via = new int[count];
        for (int i = 0; i < count; i++) { graph[i] = double.MaxValue; via[i] = -1; }
        graph[Math.Min(gates, count - 1)] = 0;
        for (int pass = 0; pass < count; pass++)
            for (int i = 0; i < linked.Count; i++)
            {
                var e = linked[i];
                double w = D(e.A, e.B);
                if (graph[e.A] < double.MaxValue && graph[e.A] + w < graph[e.B]) { graph[e.B] = graph[e.A] + w; via[e.B] = i; }
                if (graph[e.B] < double.MaxValue && graph[e.B] + w < graph[e.A]) { graph[e.A] = graph[e.B] + w; via[e.A] = i; }
            }
        int most = 1;
        for (int g = 0; g < gates; g++)
            for (int at = g, guard = 0; via[at] >= 0 && guard < count; guard++)
            {
                var e = linked[via[at]];
                most = Math.Max(most, ++e.Flow);
                at = e.A == at ? e.B : e.A;
            }
        double softWidth = Math.Max(1.2, Math.Min(2.0, mainWidth * .55));
        foreach (var e in linked) e.Width = e.Flow == 0 ? softWidth : Math.Max(softWidth, mainWidth * (.62 + .38 * e.Flow / most));

        // ---- 園路の形: 両端の向きを振り、長い園路は途中の点を左右へずらして蛇行させた曲線。
        // 敷地からはみ出すなら振りを弱め、それでも出るなら敷地の中の最短の道筋をなめらかにして使う。
        var prepared = PreparedGeometryFactory.Prepare(lane.Buffer(.02));
        for (int index = 0; index < linked.Count; index++)
        {
            var e = linked[index];
            ParkNode a = nodes[e.A], b = nodes[e.B];
            double ds = b.S - a.S, dt = b.T - a.T, length = Math.Sqrt(ds * ds + dt * dt);
            if (length < .5) continue;
            ds /= length; dt /= length;
            var random = new ParkRandom(seed, 200u + (uint)index);
            double swingA = random.Range(.3, .75) * (random.Next() < .5 ? -1 : 1), swingB = random.Range(.3, .75) * (random.Next() < .5 ? -1 : 1);
            int bends = (int)(length / 38);
            var sway = new double[bends];
            double side = random.Next() < .5 ? -1 : 1;
            for (int i = 0; i < bends; i++) { sway[i] = side * random.Range(.1, .26) * length / (bends + 1); side = -side; }
            List<Coordinate> line = null;
            foreach (double k in new[] { 1.0, .5, 0 })
            {
                (double s, double t) Dir(ParkNode n, double sign, double swing)
                {
                    if (n.Gate) return (n.Ns * (.65 + .35 * k) + ds * sign * .35 * (1 - k), n.Nt * (.65 + .35 * k) + dt * sign * .35 * (1 - k));
                    double c = Math.Cos(swing * k), sn = Math.Sin(swing * k);
                    return ((ds * c - dt * sn) * sign, (ds * sn + dt * c) * sign);
                }
                // 通る点と、そこでの進む向き(長さは区間の長さに比例)。途中の点の向きは前後の点を結ぶ向き。
                var points = new List<(double s, double t)> { (a.S, a.T) };
                for (int i = 0; i < bends; i++)
                {
                    double along = length * (i + 1) / (bends + 1);
                    points.Add((a.S + ds * along - dt * sway[i] * k, a.T + dt * along + ds * sway[i] * k));
                }
                points.Add((b.S, b.T));
                var candidate = new List<Coordinate>();
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    double span = Math.Sqrt((points[i + 1].s - points[i].s) * (points[i + 1].s - points[i].s) + (points[i + 1].t - points[i].t) * (points[i + 1].t - points[i].t));
                    (double s, double t) Tangent(int at)
                    {
                        if (at == 0) { var d = Dir(a, 1, swingA); return (d.s, d.t); }
                        if (at == points.Count - 1) { var d = Dir(b, -1, swingB); return (-d.s, -d.t); }
                        double ts = points[at + 1].s - points[at - 1].s, tt = points[at + 1].t - points[at - 1].t, tn = Math.Sqrt(ts * ts + tt * tt);
                        return (ts / tn, tt / tn);
                    }
                    var t0 = Tangent(i); var t1 = Tangent(i + 1);
                    double pull = span * .38;
                    var piece = ParkBezier(points[i].s, points[i].t, points[i].s + t0.s * pull, points[i].t + t0.t * pull,
                        points[i + 1].s - t1.s * pull, points[i + 1].t - t1.t * pull, points[i + 1].s, points[i + 1].t, Math.Max(6, (int)(span / 1.2)));
                    if (i > 0) piece.RemoveAt(0);
                    candidate.AddRange(piece);
                }
                if (prepared.Covers(Nts.CreateLineString(candidate.ToArray()))) { line = candidate; break; }
            }
            if (line == null) line = ParkRoute(prepared, parents[e.A], nodes[e.B].Cell, width, env, h, a, b);
            if (line == null) continue;
            if (a.Gate) line.Insert(0, new Coordinate(a.Ws, a.Wt));
            if (b.Gate) line.Add(new Coordinate(b.Ws, b.Wt));
            e.Line = line;
            if (!a.Gate && a.Fs == 0 && a.Ft == 0) { a.Fs = line[1].X - line[0].X; a.Ft = line[1].Y - line[0].Y; }
            if (!b.Gate && b.Fs == 0 && b.Ft == 0) { b.Fs = line[line.Count - 2].X - line[line.Count - 1].X; b.Ft = line[line.Count - 2].Y - line[line.Count - 1].Y; }
            edges.Add(e);
        }
    }

    static List<Coordinate> ParkBezier(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, int steps)
    {
        var line = new List<Coordinate>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps, m = 1 - t;
            double a = m * m * m, b = 3 * m * m * t, c = 3 * m * t * t, d = t * t * t;
            line.Add(new Coordinate(a * x0 + b * x1 + c * x2 + d * x3, a * y0 + b * y1 + c * y2 + d * y3));
        }
        return line;
    }

    /// <summary>格子の上の最短距離(8近傍)。届かない升は double.MaxValue。</summary>
    static double[] ParkDijkstra(bool[] inside, int width, int height, double h, int start, out int[] parent)
    {
        var dist = new double[inside.Length];
        parent = new int[inside.Length];
        for (int i = 0; i < dist.Length; i++) { dist[i] = double.MaxValue; parent[i] = -1; }
        if (start < 0) return dist;
        // 升の数は多くても数千なので、二分ヒープを配列で持つ。
        var heap = new List<(double d, int i)> { (0, start) };
        dist[start] = 0;
        while (heap.Count > 0)
        {
            var top = heap[0];
            var last = heap[heap.Count - 1]; heap.RemoveAt(heap.Count - 1);
            if (heap.Count > 0)
            {
                int at = 0;
                while (true)
                {
                    int l = at * 2 + 1, r = l + 1, small = at;
                    var value = last;
                    if (l < heap.Count && heap[l].d < value.d) { small = l; value = heap[l]; }
                    if (r < heap.Count && heap[r].d < value.d) { small = r; }
                    if (small == at) break;
                    heap[at] = heap[small]; at = small;
                }
                heap[at] = last;
            }
            if (top.d > dist[top.i]) continue;
            int x = top.i % width, y = top.i / width;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int n = ny * width + nx;
                    if (!inside[n]) continue;
                    // 斜めは両隣の升も中にあるときだけ通す(敷地の角をかすめない)。
                    if (dx != 0 && dy != 0 && (!inside[y * width + nx] || !inside[ny * width + x])) continue;
                    double d = top.d + h * (dx != 0 && dy != 0 ? 1.41421356 : 1);
                    if (d >= dist[n]) continue;
                    dist[n] = d; parent[n] = top.i;
                    int at = heap.Count; heap.Add((d, n));
                    while (at > 0 && heap[(at - 1) / 2].d > d) { heap[at] = heap[(at - 1) / 2]; at = (at - 1) / 2; }
                    heap[at] = (d, n);
                }
        }
        return dist;
    }

    /// <summary>格子の最短の道筋を、見通せる点まで間引いてから角を落としてなめらかにする。</summary>
    static List<Coordinate> ParkRoute(IPreparedGeometry lane, int[] parent, int target, int width, Envelope env, double h, ParkNode a, ParkNode b)
    {
        var cells = new List<Coordinate>();
        for (int at = target, guard = 0; at >= 0 && guard < parent.Length; at = parent[at], guard++)
            cells.Add(new Coordinate(env.MinX + (at % width + .5) * h, env.MinY + (at / width + .5) * h));
        if (cells.Count < 2) return null;
        cells.Reverse();
        cells[0] = new Coordinate(a.S, a.T); cells[cells.Count - 1] = new Coordinate(b.S, b.T);
        var pulled = new List<Coordinate> { cells[0] };
        for (int i = 0; i < cells.Count - 1;)
        {
            int next = i + 1;
            for (int j = cells.Count - 1; j > i + 1; j--)
                if (lane.Covers(Nts.CreateLineString(new[] { cells[i], cells[j] }))) { next = j; break; }
            pulled.Add(cells[next]); i = next;
        }
        var line = pulled;
        for (int pass = 0; pass < 3; pass++)
        {
            var cut = new List<Coordinate> { line[0] };
            for (int i = 0; i + 1 < line.Count; i++)
            {
                Coordinate p0 = line[i], p1 = line[i + 1];
                if (i > 0) cut.Add(new Coordinate(p0.X * .75 + p1.X * .25, p0.Y * .75 + p1.Y * .25));
                if (i + 2 < line.Count) cut.Add(new Coordinate(p0.X * .25 + p1.X * .75, p0.Y * .25 + p1.Y * .75));
            }
            cut.Add(line[line.Count - 1]);
            if (!lane.Covers(Nts.CreateLineString(cut.ToArray()))) break;
            line = cut;
        }
        return line;
    }

    sealed class ParkPlacer
    {
        public Plan Plan; public Frame F;
        public IReadOnlyDictionary<string, Vector2> Sizes;
        public IPreparedGeometry Site, Hard;
        public IndexedPointInAreaLocator HardArea;
        public ParkEdgeIndex HardEdges, SiteEdges;
        public ParkDiscs Crowns, Objects;  // 樹冠どうしの間隔と、幹・低木が避ける小物
        const double TakenCell = 8;
        readonly Dictionary<long, List<Geometry>> taken = new Dictionary<long, List<Geometry>>();
        readonly Coordinate probe = new Coordinate();
        public bool Has(string part) => Sizes.ContainsKey(part);
        public bool OnHard(double s, double t)
        {
            if (HardArea == null) return false;
            probe.X = s; probe.Y = t;
            return HardArea.Locate(probe) != Location.Exterior;
        }
        /// <summary>
        /// 部品の実寸の矩形を (s,t) に正面 (fs,ft) で置く。敷地の外・ほかの小物との重なりがあれば置かない。
        /// onHard は下地の指定で、true は舗装・砂地の上だけ、false は芝生の上だけ、null は問わない。gap は矩形に足す余白(負なら縮める)。
        /// </summary>
        public bool Put(string part, double s, double t, double fs, double ft, bool? onHard = null, double gap = .15)
        {
            if (!Sizes.TryGetValue(part, out var size)) return false;
            double n = Math.Sqrt(fs * fs + ft * ft);
            if (n < 1e-9) { fs = 0; ft = -1; n = 1; }
            fs /= n; ft /= n;
            var box = ParkBox(s, t, fs, ft, size.x + gap * 2, size.y + gap * 2);
            if (!Site.Covers(box)) return false;
            if (onHard.HasValue && (Hard == null ? onHard.Value : onHard.Value ? !Hard.Covers(box) : Hard.Intersects(box))) return false;
            int cx = (int)Math.Floor(s / TakenCell), cy = (int)Math.Floor(t / TakenCell);
            for (int y = cy - 1; y <= cy + 1; y++)
                for (int x = cx - 1; x <= cx + 1; x++)
                    if (taken.TryGetValue(((long)x << 32) ^ (uint)y, out var near))
                        foreach (var other in near) if (other.Intersects(box)) return false;
            long key = ((long)cx << 32) ^ (uint)cy;
            if (!taken.TryGetValue(key, out var list)) taken.Add(key, list = new List<Geometry>());
            list.Add(box);
            Objects.Add(s, t, Math.Max(size.x, size.y) * .5);
            Plan.Props.Add(new Prop { Part = part, Position = F.World(s, t), Forward = F.Dir(fs, ft), Fitted = true });
            return true;
        }
    }

    /// <summary>入口の車止め、広場の部品と遊具、園路沿いのベンチ・街灯・ゴミ箱、広場のまわりの水飲み場と花壇。</summary>
    static void ParkFurniture(ParkPlacer placer, List<ParkNode> nodes, List<ParkEdge> edges, double mainWidth, uint seed, Geometry sand)
    {
        var flowers = ParkSeries(placer.Sizes, "site_flower");
        int flower = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (!n.Gate) continue;
            // 車止めは園路をふさがないよう、入口の少し内側に園路の幅へ等間隔に並べる。
            if (placer.Has("site_bollard"))
            {
                int posts = Math.Max(1, (int)(mainWidth / 1.4));
                for (int k = 0; k < posts; k++)
                {
                    double off = (k + .5) / posts * mainWidth - mainWidth * .5;
                    placer.Put("site_bollard", n.Ws + n.Ns * .9 - n.Nt * off, n.Wt + n.Nt * .9 + n.Ns * off, n.Ns, n.Nt, null, .02);
                }
            }
            if (flowers.Count == 0) continue;
            foreach (int side in new[] { -1, 1 })
            {
                string part = flowers[flower++ % flowers.Count];
                double off = side * (mainWidth * .5 + .7 + placer.Sizes[part].x * .5), depth = .7 + placer.Sizes[part].y * .5;
                placer.Put(part, n.Ws + n.Ns * depth - n.Nt * off, n.Wt + n.Nt * depth + n.Ns * off, -n.Ns, -n.Nt, false);
            }
        }
        var plays = ParkSeries(placer.Sizes, "site_play");
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n.Gate || n.R <= 0) continue;
            double fs = n.Fs, ft = n.Ft;
            if (fs == 0 && ft == 0) ft = -1;
            double norm = Math.Sqrt(fs * fs + ft * ft); fs /= norm; ft /= norm;
            if (n.Feature != null) placer.Put(n.Feature, n.S, n.T, fs, ft, null, .3);
            if (n.Play)
            {
                // 遊具は番号順に1つずつ、砂地の中の空いている所へ置く。
                var random = new ParkRandom(seed, 300u + (uint)i);
                var area = sand != null ? PreparedGeometryFactory.Prepare(sand.Buffer(-.3)) : null;
                foreach (string part in plays)
                {
                    var size = placer.Sizes[part];
                    for (int attempt = 0; attempt < 40; attempt++)
                    {
                        double angle = random.Next() * Math.PI * 2, radius = Math.Sqrt(random.Next()) * Math.Max(0, n.R - size.magnitude * .5);
                        double s = n.S + Math.Cos(angle) * radius, t = n.T + Math.Sin(angle) * radius;
                        double turn = Math.Floor(random.Next() * 4) * Math.PI * .5 + (random.Next() - .5) * .5;
                        double ps = Math.Cos(turn), pt = Math.Sin(turn);
                        if (area == null || !area.Covers(ParkBox(s, t, ps, pt, size.x + 1.2, size.y + 1.2))) continue;
                        if (placer.Put(part, s, t, ps, pt, null, .6)) break;
                    }
                }
            }
            // 広場のへり: 中央を向いたベンチ、合間に街灯・水飲み場。外側に花壇。
            int slots = Math.Max(3, (int)(Math.PI * 2 * n.R / 4.2));
            double phase = Rand(seed, 400 + i) * Math.PI * 2;
            bool drink = i == FirstPlaza(nodes);
            for (int k = 0; k < slots; k++)
            {
                double angle = phase + Math.PI * 2 * k / slots, cs = Math.Cos(angle), sn = Math.Sin(angle);
                string part = k % 3 == 1 ? "site_light" : drink && k % 3 == 2 ? "site_drink" : "site_bench";
                if (part == "site_drink") drink = false;
                if (!placer.Has(part)) continue;
                double r = n.R * .86 - placer.Sizes[part].y * .5 - .25;
                if (n.Play) r = n.R + .5 + placer.Sizes[part].y * .5;
                if (r < 1 || !placer.Put(part, n.S + cs * r, n.T + sn * r, -cs, -sn, n.Play ? false : (bool?)true)) continue;
                if (part == "site_bench" && flowers.Count > 0 && !n.Play && k % 2 == 0)
                {
                    string bed = flowers[flower++ % flowers.Count];
                    double outer = n.R + .7 + placer.Sizes[bed].y * .5, a2 = angle + Math.PI / slots;
                    placer.Put(bed, n.S + Math.Cos(a2) * outer, n.T + Math.Sin(a2) * outer, -Math.Cos(a2), -Math.Sin(a2), false);
                }
            }
        }
        // 園路沿い: 道の外側に、道を向いたベンチと街灯を交互の側へ置く。ゴミ箱はベンチの隣。
        for (int index = 0; index < edges.Count; index++)
        {
            var line = edges[index].Line;
            double w = edges[index].Width;
            var random = new ParkRandom(seed, 500u + (uint)index);
            double next = random.Range(5, 11), walked = 0;
            int placed = 0;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                double ds = line[i + 1].X - line[i].X, dt = line[i + 1].Y - line[i].Y, len = Math.Sqrt(ds * ds + dt * dt);
                if (len < 1e-9) continue;
                ds /= len; dt /= len;
                while (next <= walked + len)
                {
                    double k = next - walked, s = line[i].X + ds * k, t = line[i].Y + dt * k;
                    int side = placed % 2 == 0 ? 1 : -1;
                    bool light = placed % 2 == 1;
                    string part = light ? "site_light" : "site_bench";
                    if (placer.Has(part) && (edges[index].Flow > 0 || !light))
                    {
                        double off = w * .5 + .35 + placer.Sizes[part].y * .5;
                        double ps = s - dt * side * off, pt = t + ds * side * off;
                        if (placer.Put(part, ps, pt, dt * side, -ds * side, false) && !light && placer.Has("site_bin") && random.Next() < .4)
                        {
                            double shift = placer.Sizes[part].x * .5 + .5 + placer.Sizes["site_bin"].x * .5;
                            placer.Put("site_bin", ps + ds * shift, pt + dt * shift, dt * side, -ds * side, false);
                        }
                    }
                    placed++;
                    next += random.Range(9, 15);
                }
                walked += len;
            }
        }
    }

    static int FirstPlaza(List<ParkNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++) if (!nodes[i].Gate && !nodes[i].Play && nodes[i].R > 0) return i;
        return -1;
    }

    static Geometry ParkBox(double s, double t, double fs, double ft, double w, double d)
    {
        double xs = ft, xt = -fs; w *= .5; d *= .5;
        return Poly(s - xs * w - fs * d, t - xt * w - ft * d, s + xs * w - fs * d, t + xt * w - ft * d,
            s + xs * w + fs * d, t + xt * w + ft * d, s - xs * w + fs * d, t - xt * w + ft * d);
    }

    /// <summary>
    /// 生垣・木・低木。木は間隔を保ちながらばらまき、置く確率を「境界に近いほど高い」「ノイズの高い所(木立)ほど高い」「density が高いほど高い」で決める。
    /// 樹冠は園路の上へ張り出してよいが、幹は舗装と小物を避け、樹冠は敷地から出さない。
    /// </summary>
    static void ParkPlanting(ParkPlacer placer, Geometry site, List<ParkNode> nodes, double scale, uint seed, double density)
    {
        var env = site.EnvelopeInternal;
        var siteArea = new IndexedPointInAreaLocator(site);
        var probe = new Coordinate();
        double grove = Math.Max(9, scale * .22);

        // 生垣: 境界の少し内側を一周し、入口と、ノイズの低い区間では切る。
        if (placer.Has("site_hedge"))
        {
            var size = placer.Sizes["site_hedge"];
            double back = size.y * .5 + .2;
            var line = Largest(site.Buffer(-back, new NetTopologySuite.Operation.Buffer.BufferParameters { JoinStyle = NetTopologySuite.Operation.Buffer.JoinStyle.Mitre }));
            if (line != null)
                foreach (var d in Walk(Oriented(line.ExteriorRing.Coordinates, false), size.x * .5, size.x))
                {
                    if (ParkNoise(seed + 5, d.s / 14, d.t / 14) < .38) continue;
                    bool gate = false;
                    foreach (var n in nodes) gate |= n.Gate && Math.Sqrt((n.Ws - d.s) * (n.Ws - d.s) + (n.Wt - d.t) * (n.Wt - d.t)) < 3.2 + size.x;
                    if (!gate) placer.Put("site_hedge", d.s, d.t, -d.dt, d.ds, false, -.03);
                }
        }

        var trees = ParkSeries(placer.Sizes, "site_tree");
        if (trees.Count > 0)
        {
            double mean = 0, largest = 0;
            foreach (string part in trees) { mean += placer.Sizes[part].x * .5; largest = Math.Max(largest, placer.Sizes[part].x * .5); }
            mean /= trees.Count; largest *= 1.2;
            double fringeWidth = Math.Max(6, scale * .16);
            // 樹冠どうしの重なり: 疎なら離し、密なら枝を交わらせる。上限の本数を超える広さでは間隔を広げる。
            double factor = .95 - .4 * density;
            factor = Math.Max(factor, Math.Sqrt(site.Area / MaxParkTrees) / (2 * mean));
            int attempts = (int)Math.Min(90000, 14 * site.Area / (4 * mean * mean * factor * factor) + 60);
            var random = new ParkRandom(seed, 600u);
            int placed = 0;
            for (int i = 0; i < attempts && placed < MaxParkTrees; i++)
            {
                double s = random.Range(env.MinX, env.MaxX), t = random.Range(env.MinY, env.MaxY);
                double pick = random.Next(), size = random.Range(.82, 1.2), chance = random.Next(), turn = random.Next() * Math.PI * 2;
                // 候補の大半は確率で落ちるので、境界のそばとして見積もった上限で先に落としてから、重い判定をする。
                double clump = ParkNoise(seed + 3, s / grove, t / grove);
                if (chance > density * 1.15 + .2 + .9 * (clump - .5)) continue;
                probe.X = s; probe.Y = t;
                if (siteArea.Locate(probe) != Location.Interior) continue;
                // 樹種は場所ごとにまとまり、ところどころ混ざる。
                // ノイズは中ほどの値に寄るので、広げてから樹種へ割り振る。
                double kind = Math.Max(0, Math.Min(.999, (ParkNoise(seed + 7, s / (grove * 1.3), t / (grove * 1.3)) - .5) * 2.4 + .5 + (pick - .5) * .5));
                string part = trees[(int)(kind * trees.Count)];
                double radius = placer.Sizes[part].x * .5 * size;
                double border = placer.SiteEdges.Distance(s, t, fringeWidth + largest);
                if (border < radius * 1.02 + .1) continue;
                double fringe = Math.Max(0, 1 - (border - radius) / fringeWidth);
                if (chance > density * 1.15 - .35 + .55 * fringe + .9 * (clump - .5)) continue;
                if (placer.OnHard(s, t) || (placer.HardEdges != null && placer.HardEdges.Distance(s, t, 1.2) < Math.Max(.7, radius * .22))) continue;
                if (placer.Crowns.Near(s, t, radius, factor, largest) || placer.Objects.Near(s, t, .5, 1, 5)) continue;
                placer.Crowns.Add(s, t, radius);
                placer.Plan.Props.Add(new Prop { Part = part, Position = placer.F.World(s, t), Forward = placer.F.Dir(Math.Cos(turn), Math.Sin(turn)), Scale = (float)size, Fitted = true });
                placed++;
            }
        }

        var shrubs = ParkSeries(placer.Sizes, "site_shrub");
        if (shrubs.Count > 0)
        {
            double mean = 0;
            foreach (string part in shrubs) mean += placer.Sizes[part].x * .5;
            mean /= shrubs.Count;
            var random = new ParkRandom(seed, 700u);
            int attempts = (int)Math.Min(60000, site.Area * (.5 + density) / (mean * mean * 2) + 40), placed = 0;
            for (int i = 0; i < attempts && placed < MaxParkShrubs; i++)
            {
                double s = random.Range(env.MinX, env.MaxX), t = random.Range(env.MinY, env.MaxY);
                double pick = random.Next(), size = random.Range(.8, 1.25), chance = random.Next(), turn = random.Next() * Math.PI * 2;
                double clump = ParkNoise(seed + 3, s / grove, t / grove), weight = .45 + .75 * density;
                if (chance > Math.Max(.75, (clump - .55) * 1.6) * weight) continue;
                probe.X = s; probe.Y = t;
                if (siteArea.Locate(probe) != Location.Interior) continue;
                string part = shrubs[Math.Min(shrubs.Count - 1, (int)(pick * shrubs.Count))];
                double radius = Math.Max(placer.Sizes[part].x, placer.Sizes[part].y) * .5 * size;
                double border = placer.SiteEdges.Distance(s, t, 12);
                if (border < radius + .9) continue;
                if (placer.OnHard(s, t)) continue;
                double path = placer.HardEdges != null ? placer.HardEdges.Distance(s, t, 6) : 6;
                if (path < radius + .25) continue;
                // 低木は境界のそば・園路や広場のへり・木立の中に寄せ、開けた芝生には置かない。
                if (chance > Math.Max(Math.Max(.75 - border / 5, .6 - path / 3.5), (clump - .55) * 1.6) * weight) continue;
                if (placer.Objects.Near(s, t, radius, .9, 5)) continue;
                placer.Objects.Add(s, t, radius);
                placer.Plan.Props.Add(new Prop { Part = part, Position = placer.F.World(s, t), Forward = placer.F.Dir(Math.Cos(turn), Math.Sin(turn)), Scale = (float)size, Fitted = true });
                placed++;
            }
        }
    }
}
