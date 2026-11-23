using System;
using System.Collections.Generic;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.OverlayNG;
using UnityEngine;

/// <summary>
/// 公園(MODEL_PARK)の競技場。競技場ごとの面の材質参照(site_diamond など)がモデルフォルダにある種類だけを、広い順に1つずつ、敷地に収まる所へ置く。
/// 寸法は少年用の規格の実寸で、敷地に合わせて伸び縮みさせない。置いた競技場の輪郭は公園の敷地から抜き、園路・木・小物はその残りに作る。
/// 面と白線は地形に沿う面、ゴールやフェンスは sizes にある部品だけを置く。
/// </summary>
public static partial class OpenSpaceLayout
{
    const double CourtMargin = 3.0;     // 競技場と敷地の境界の間に残す幅(m)
    const double CourtGap = 5.0;        // 競技場どうしの間に残す幅(m)
    const double CourtGate = 3.0;       // フェンスを開ける出入口の幅(m)
    const double CourtLine = .12;       // 白線の幅(m)
    const float CourtLift = .014f, CourtLineLift = .024f;

    sealed class CourtKind
    {
        public string Surface;          // 面の材質参照。モデルフォルダにあるときだけこの競技場を置く
        public Geometry Outline;        // 輪郭。外接箱の中心が原点
        public int Turns;               // 試す向きの数(90度刻み)
        public bool Fenced;             // 輪郭に沿ってフェンスで囲うか
        public (int x, int y)[] Doors;  // 出入口を開けられる向き
        public Action<CourtPen> Draw;
    }

    /// <summary>置いた競技場。競技場の座標 (x, y) を、90度刻みの向きと位置で敷地の局所座標 (s, t) へ移す。</summary>
    sealed class ParkCourt
    {
        public CourtKind Kind;
        public double S, T;
        public int Turn;
        public Geometry Outline;        // 局所座標での輪郭
        public List<(Coordinate at, double ns, double nt)> Doors;  // 出入口の候補(輪郭の上の点と外向きの単位ベクトル)。接道辺に近い順
        public (Coordinate at, double ns, double nt) Door;
        public Coordinate Anchor;       // 出入口の前の、園路が届く点。園路を通せないときは null
        static readonly int[] Cos = { 1, 0, -1, 0 }, Sin = { 0, 1, 0, -1 };
        public Coordinate At(double x, double y) => new Coordinate(S + x * Cos[Turn] - y * Sin[Turn], T + x * Sin[Turn] + y * Cos[Turn]);
        public Geometry Map(Geometry g) => new AffineTransformation(Cos[Turn], -Sin[Turn], S, Sin[Turn], Cos[Turn], T).Transform(g);
    }

    /// <summary>競技場の座標で描いた面と部品を、配置計画へ書き出す。</summary>
    sealed class CourtPen
    {
        public Plan Plan; public Frame F; public ParkCourt Court;
        public IReadOnlyDictionary<string, Vector2> Sizes;
        public void Fill(Geometry g, string material, float lift = CourtLift) => Emit(Plan, F, Court.Map(g), material, lift);
        public void Lines(List<Geometry> lines) => Fill(OverlayNGRobust.Union(lines), "site_marking", CourtLineLift);
        /// <summary>部品を (x, y) に、正面を (fx, fy) へ向けて置く。モデルフォルダに無い部品は置かない。</summary>
        public void Put(string part, double x, double y, double fx, double fy)
        {
            if (!Sizes.ContainsKey(part)) return;
            Coordinate at = Court.At(x, y), to = Court.At(x + fx, y + fy);
            Plan.Props.Add(new Prop { Part = part, Position = F.World(at.X, at.Y), Forward = F.Dir(to.X - at.X, to.Y - at.Y), Fitted = true });
        }
        public void Light(double x, double y, double towardX, double towardY) => Put("site_floodlight", x, y, towardX - x, towardY - y);
    }

    // 広い順。先に置いた競技場が場所を取るので、この並びが割り当ての優先順になる。
    static readonly Lazy<CourtKind[]> CourtKinds = new Lazy<CourtKind[]>(() => new[] { Baseball(), Track(), Soccer(), Tennis() });

    static BufferParameters Smooth() => new BufferParameters { JoinStyle = JoinStyle.Round, QuadrantSegments = 12 };
    static BufferParameters Mitre() => new BufferParameters { JoinStyle = JoinStyle.Mitre };
    static Geometry Disc(double x, double y, double r) => Nts.CreatePoint(new Coordinate(x, y)).Buffer(r, Smooth());
    /// <summary>図形の輪郭をなぞる白線。</summary>
    static Geometry Stroke(Geometry g) => Minus(g.Buffer(CourtLine * .5, Mitre()), g.Buffer(-CourtLine * .5, Mitre()));
    static Geometry LineX(double x0, double x1, double y) => Rect(x0, y - CourtLine * .5, x1, y + CourtLine * .5);
    static Geometry LineY(double x, double y0, double y1) => Rect(x - CourtLine * .5, y0, x + CourtLine * .5, y1);

    /// <summary>野球場(少年野球の規格: 両翼70m・中堅85m)。本塁を角に置いた扇形で、内野は土、外野は公園の芝のまま。</summary>
    static CourtKind Baseball()
    {
        const double foul = 70, center = 85, apron = 6, bases = 23, infield = 30;
        // 柵までの距離。両翼から中堅へなめらかに伸びる。
        double Reach(double angle) => foul + (center - foul) * Math.Sin(2 * angle);
        // 輪郭は、フェアグラウンドを apron だけ縮めた扇形を apron だけ膨らませた形。本塁の後ろとファウルラインの外にファウルグラウンドができる。
        Geometry Field(double homeX, double homeY)
        {
            const int steps = 24;
            var ring = new Coordinate[steps + 3];
            ring[0] = new Coordinate(homeX, homeY);
            for (int i = 0; i <= steps; i++)
            {
                double a = Math.PI * .5 * i / steps, r = Reach(a) - apron;
                ring[i + 1] = new Coordinate(homeX + r * Math.Cos(a), homeY + r * Math.Sin(a));
            }
            ring[steps + 2] = ring[0].Copy();
            return Nts.CreatePolygon(ring).Buffer(apron, Smooth());
        }
        var box = Field(0, 0).EnvelopeInternal;
        double hx = -(box.MinX + box.MaxX) * .5, hy = -(box.MinY + box.MaxY) * .5;
        var outline = Field(hx, hy);
        return new CourtKind { Surface = "site_diamond", Outline = outline, Turns = 4, Fenced = true, Doors = new[] { (0, -1), (-1, 0) }, Draw = pen =>
        {
            pen.Fill(And(Disc(hx, hy, infield), outline), "site_diamond");
            var lines = new List<Geometry> { LineX(hx, hx + foul - .3, hy), LineY(hx, hy, hy + foul - .3) };
            foreach (var (x, y) in new[] { (0.0, 0.0), (bases, 0.0), (bases, bases), (0.0, bases) })
                lines.Add(Rect(hx + x - .2, hy + y - .2, hx + x + .2, hy + y + .2));
            pen.Lines(lines);
            pen.Put("site_backstop", hx - 2.3, hy - 2.3, 1, 1);
            pen.Light(hx + bases + 7, hy - apron + 1.5, hx + bases, hy + bases);
            pen.Light(hx - apron + 1.5, hy + bases + 7, hx + bases, hy + bases);
            foreach (double degrees in new[] { 20.0, 70.0 })
            {
                double a = degrees * Math.PI / 180, r = Reach(a) - 1.5;
                pen.Light(hx + r * Math.Cos(a), hy + r * Math.Sin(a), hx + bases, hy + bases);
            }
        } };
    }

    /// <summary>陸上トラック(1周200m・6レーン)。内側は公園の芝のまま。ホームストレートの外に観覧席を並べる。</summary>
    static CourtKind Track()
    {
        const double half = 21.6, inner = 18, lane = 1.22, apron = 1, stand = 5;
        const int lanes = 6;
        double outer = inner + lane * lanes;
        // 観覧席の分だけ輪郭が手前(-y)へ伸びるので、外接箱の中心が原点に来るようトラックを奥へずらす。
        double oy = stand * .5;
        Geometry Oval(double r) => Nts.CreateLineString(new[] { new Coordinate(-half, oy), new Coordinate(half, oy) }).Buffer(r, Smooth());
        var outline = Union(Oval(outer + apron), Rect(-half, oy - outer - apron - stand, half, oy));
        return new CourtKind { Surface = "site_track", Outline = outline, Turns = 2, Doors = new[] { (0, 1), (1, 0), (-1, 0) }, Draw = pen =>
        {
            pen.Fill(Minus(Oval(outer), Oval(inner)), "site_track");
            var lines = new List<Geometry> { LineY(half, oy - outer, oy - inner) };
            for (int k = 0; k <= lanes; k++) lines.Add(Stroke(Oval(inner + lane * k)));
            pen.Lines(lines);
            if (pen.Sizes.TryGetValue("site_stand", out var size))
            {
                int count = Math.Max(1, (int)(2 * half / (size.x + 1)));
                for (int i = 0; i < count; i++)
                    pen.Put("site_stand", (i + .5) * 2 * half / count - half, oy - outer - apron - .3 - size.y * .5, 0, 1);
            }
            double corner = (outer + .5) * Math.Sqrt(.5);
            foreach (int sx in new[] { -1, 1 })
                foreach (int sy in new[] { -1, 1 })
                    pen.Light(sx * (half + corner), oy + sy * corner, 0, oy);
        } };
    }

    /// <summary>サッカー場(少年用の規格: 68×50m)。</summary>
    static CourtKind Soccer()
    {
        const double halfLength = 34, halfWidth = 25, runoff = 3, penaltyDepth = 12, penaltyHalf = 14.5, circle = 7;
        var outline = Rect(-halfLength - runoff, -halfWidth - runoff, halfLength + runoff, halfWidth + runoff);
        return new CourtKind { Surface = "site_pitch", Outline = outline, Turns = 2, Doors = new[] { (0, -1), (1, 0), (-1, 0), (0, 1) }, Draw = pen =>
        {
            pen.Fill(outline, "site_pitch");
            var lines = new List<Geometry> { Stroke(Rect(-halfLength, -halfWidth, halfLength, halfWidth)), LineY(0, -halfWidth, halfWidth), Stroke(Disc(0, 0, circle)) };
            foreach (int side in new[] { -1, 1 })
            {
                lines.Add(Stroke(Rect(Math.Min(side * halfLength, side * (halfLength - penaltyDepth)), -penaltyHalf,
                    Math.Max(side * halfLength, side * (halfLength - penaltyDepth)), penaltyHalf)));
                if (pen.Sizes.TryGetValue("site_goal", out var size)) pen.Put("site_goal", side * (halfLength + size.y * .5), 0, -side, 0);
                foreach (int sy in new[] { -1, 1 })
                    pen.Light(side * (halfLength + runoff - 1), sy * (halfWidth + runoff - 1), 0, 0);
            }
            pen.Lines(lines);
        } };
    }

    /// <summary>テニスコート2面(1面 23.77×10.97m)。長辺を並べ、まとめてフェンスで囲う。</summary>
    static CourtKind Tennis()
    {
        const double halfLength = 11.885, halfWidth = 5.485, halfSingles = 4.115, service = 6.4, back = 6.4, side = 3.66, between = 4.5;
        double centerY = halfWidth + between * .5, halfX = halfLength + back, halfY = centerY + halfWidth + side;
        var outline = Rect(-halfX, -halfY, halfX, halfY);
        return new CourtKind { Surface = "site_court", Outline = outline, Turns = 2, Fenced = true, Doors = new[] { (0, -1), (1, 0), (-1, 0), (0, 1) }, Draw = pen =>
        {
            pen.Fill(outline, "site_court");
            var lines = new List<Geometry>();
            foreach (int court in new[] { -1, 1 })
            {
                double y = court * centerY;
                lines.Add(Stroke(Rect(-halfLength, y - halfWidth, halfLength, y + halfWidth)));
                lines.Add(LineX(-service, service, y));
                foreach (int k in new[] { -1, 1 })
                {
                    lines.Add(LineX(-halfLength, halfLength, y + k * halfSingles));
                    lines.Add(LineY(k * service, y - halfSingles, y + halfSingles));
                }
                pen.Put("site_net", 0, y, 1, 0);
            }
            pen.Lines(lines);
            foreach (int sx in new[] { -1, 1 })
                foreach (int sy in new[] { -1, 1 })
                    pen.Light(sx * (halfX - 1), sy * (halfY - 1), 0, 0);
        } };
    }

    /// <summary>
    /// 競技場の割り当て。境界から CourtMargin 内側・ほかの競技場から CourtGap 離れた所に輪郭が収まる種類だけを置く。
    /// 置き場は接道辺からいちばん奥に寄せ、手前に公園の部分をまとめて残す。左右のどちらへ寄せるかは seed で決まる。
    /// </summary>
    static List<ParkCourt> ParkCourts(Geometry site, uint seed, IReadOnlyDictionary<string, Vector2> sizes)
    {
        var courts = new List<ParkCourt>();
        if (sizes == null) return courts;
        Geometry room = null;
        IPreparedGeometry covers = null;
        IndexedPointInAreaLocator inside = null;
        var taken = new List<Geometry>();
        var probe = new Coordinate();
        double step = Math.Max(1.5, Math.Sqrt(site.Area) / 50);
        for (int index = 0; index < CourtKinds.Value.Length; index++)
        {
            var kind = CourtKinds.Value[index];
            if (!sizes.ContainsKey(kind.Surface)) continue;
            if (room == null)
            {
                room = site.Buffer(-CourtMargin);
                if (room.IsEmpty) break;
                covers = PreparedGeometryFactory.Prepare(room);
                inside = new IndexedPointInAreaLocator(room);
            }
            var env = room.EnvelopeInternal;
            bool fromRight = Rand(seed, 80 + index) < .5;
            ParkCourt best = null;
            double bestFront = double.MinValue;
            for (int turn = 0; turn < kind.Turns; turn++)
            {
                var court = new ParkCourt { Kind = kind, Turn = turn };
                var shape = court.Map(kind.Outline);
                var box = shape.EnvelopeInternal;
                if (box.Width > env.Width || box.Height > env.Height) continue;
                // 候補の大半は輪郭の数点が敷地から出るので、それを先に見てから輪郭全体を判定する。
                var ring = shape.Coordinates;
                int stride = Math.Max(1, ring.Length / 12);
                bool found = false;
                for (double front = env.MaxY - box.Height; front >= env.MinY - 1e-9 && front > bestFront && !found; front -= step)
                    for (double shift = 0; shift + box.Width <= env.Width + 1e-9; shift += step)
                    {
                        double s = (fromRight ? env.MaxX - shift - box.Width : env.MinX + shift) - box.MinX, t = front - box.MinY;
                        bool outside = false;
                        for (int k = 0; k < ring.Length && !outside; k += stride)
                        {
                            probe.X = ring[k].X + s; probe.Y = ring[k].Y + t;
                            outside = inside.Locate(probe) == Location.Exterior;
                        }
                        if (outside) continue;
                        court.S = s; court.T = t;
                        var placed = court.Map(kind.Outline);
                        if (!covers.Covers(placed)) continue;
                        bool clash = false;
                        foreach (var other in taken) clash |= other.Intersects(placed);
                        if (clash) continue;
                        court.Outline = placed;
                        found = true;
                        break;
                    }
                if (found) { best = court; bestFront = court.T + box.MinY; }
            }
            if (best == null) continue;
            best.Doors = CourtDoors(best);
            if (best.Doors.Count == 0) continue;
            best.Door = best.Doors[0];
            courts.Add(best);
            taken.Add(best.Outline.Buffer(CourtGap));
        }
        return courts;
    }

    /// <summary>出入口の候補。競技場の中心から向きごとに輪郭まで進んだ点。</summary>
    static List<(Coordinate at, double ns, double nt)> CourtDoors(ParkCourt court)
    {
        var doors = new List<(Coordinate at, double ns, double nt)>();
        var center = court.At(0, 0);
        var box = court.Outline.EnvelopeInternal;
        double reach = box.Width + box.Height;
        foreach (var (x, y) in court.Kind.Doors)
        {
            var toward = court.At(x, y);
            double ns = toward.X - center.X, nt = toward.Y - center.Y;
            var ray = Nts.CreateLineString(new[] { center, new Coordinate(center.X + ns * reach, center.Y + nt * reach) });
            Coordinate far = null;
            foreach (var hit in court.Outline.Boundary.Intersection(ray).Coordinates)
                if (far == null || hit.Distance(center) > far.Distance(center)) far = hit;
            if (far != null) doors.Add((far, ns, nt));
        }
        doors.Sort((a, b) => a.at.Y.CompareTo(b.at.Y));
        return doors;
    }

    /// <summary>
    /// 競技場ごとに、前の園路まで出られる出入口を接道辺に近い順に選び、園路が届く点を返す。lane は園路の中心線を通せる範囲。
    /// どの出入口も園路に届かない競技場は、接道辺にいちばん近い出入口を開けるだけにする。
    /// </summary>
    static List<Coordinate> CourtEntrances(List<ParkCourt> courts, Polygon lane, double laneInset)
    {
        if (courts.Count == 0) return null;
        var anchors = new List<Coordinate>();
        var locator = new IndexedPointInAreaLocator(lane);
        foreach (var court in courts)
        {
            court.Door = court.Doors[0];
            court.Anchor = null;
            foreach (var door in court.Doors)
            {
                var point = new Coordinate(door.at.X + door.ns * (laneInset + .3), door.at.Y + door.nt * (laneInset + .3));
                if (locator.Locate(point) != Location.Interior) continue;
                court.Door = door;
                court.Anchor = point;
                anchors.Add(point);
                break;
            }
        }
        return anchors;
    }

    /// <summary>競技場の面・白線・備品と、囲いのフェンス。フェンスは出入口の幅だけ開ける。</summary>
    static void DrawCourts(Plan p, Frame f, List<ParkCourt> courts, IReadOnlyDictionary<string, Vector2> sizes)
    {
        foreach (var court in courts)
        {
            court.Kind.Draw(new CourtPen { Plan = p, F = f, Court = court, Sizes = sizes });
            if (!court.Kind.Fenced || !sizes.TryGetValue("site_fence", out var panel)) continue;
            var line = Largest(court.Outline.Buffer(-(panel.y * .5 + .05), Mitre()));
            if (line == null) continue;
            foreach (var run in FenceRuns(Oriented(line.ExteriorRing.Coordinates, false)))
            {
                double length = 0;
                for (int i = 0; i + 1 < run.Length; i++) length += run[i].Distance(run[i + 1]);
                // 区間ごとに枚数を決めて等間隔に並べる(端の半端は重なりか隙間に均す)。
                double spacing = length / Math.Max(1, Math.Round(length / panel.x));
                foreach (var d in Walk(run, spacing * .5, spacing))
                {
                    if (court.Door.at.Distance(new Coordinate(d.s, d.t)) < (CourtGate + panel.x) * .5) continue;
                    p.Props.Add(new Prop { Part = "site_fence", Position = f.World(d.s, d.t), Forward = f.Dir(-d.dt, d.ds), Fitted = true });
                }
            }
        }
    }

    /// <summary>輪を角(向きが30度より大きく変わる頂点)で区切った折れ線に分ける。フェンスが角をまたがないようにする。</summary>
    static List<Coordinate[]> FenceRuns(Coordinate[] ring)
    {
        int n = ring.Length - 1;
        bool Corner(int i)
        {
            Coordinate a = ring[(i - 1 + n) % n], b = ring[i % n], c = ring[(i + 1) % n];
            double ux = b.X - a.X, uy = b.Y - a.Y, vx = c.X - b.X, vy = c.Y - b.Y;
            return ux * vx + uy * vy < .866 * Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        }
        int start = 0;
        while (start < n && !Corner(start)) start++;
        var runs = new List<Coordinate[]>();
        var run = new List<Coordinate> { ring[start % n] };
        for (int k = 1; k <= n; k++)
        {
            int i = (start + k) % n;
            run.Add(ring[i]);
            if (k < n && Corner(i))
            {
                runs.Add(run.ToArray());
                run = new List<Coordinate> { ring[i] };
            }
        }
        runs.Add(run.ToArray());
        return runs;
    }
}
