using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using UnityEngine;

/// <summary>地面用途の配置計画。ロード順・描画LOD・確定時のIDから独立した平面幾何。</summary>
public static class OpenSpaceLayout
{
    public const float CellSize = 2.5f;
    const int MaxAxis = 128;
    public const float BOUNDARY_TOLERANCE = 1e-3f; // 敷地の辺上に置いた点を内側と数えるための余裕(m)。QAテストも参照する
    public sealed class Surface
    {
        public string Material;
        public Vector2[] Polygon;
        public Vector2[][] Holes;  // 内側の輪(島を抜いた周回路など)。無ければ null
        public float Lift;         // 地面からの天端の高さ(m)
        public string Side;        // 地面から天端までの側面(縁石)の材質。null なら側面の無い平らな面
    }
    public sealed class Prop
    {
        public string Part;
        public Vector2 Position;
        public Vector2 Forward;
    }
    public sealed class Plan
    {
        public Vector2[] Polygon;
        public readonly List<Surface> Surfaces = new List<Surface>();
        public readonly List<Prop> Props = new List<Prop>();
        public readonly List<Vector2[]> ParkingBays = new List<Vector2[]>();
        public readonly List<Vector2> AccessCells = new List<Vector2>();
        public readonly List<BuildingRoadOpening> Openings = new List<BuildingRoadOpening>();
        public bool HasLoop;
    }
    sealed class Grid
    {
        public Vector2 U, V, Origin;
        public Vector2[] Polygon;
        public int Width, Height, Entry;
        public bool[] Inside;
        public Vector2 Point(float x, float y) => Origin + U * (x * CellSize) + V * (y * CellSize);
        public Vector2 Center(int i) => Point(i % Width + .5f, i / Width + .5f);
        public Vector2[] Box(float x, float y, float w, float h) => new[] {Point(x,y),Point(x+w,y),Point(x+w,y+h),Point(x,y+h)};
        public IEnumerable<int> Neighbors(int i)
        {
            int x=i%Width, y=i/Width;
            if (x>0) yield return i-1;
            if (x+1<Width) yield return i+1;
            if (y>0) yield return i-Width;
            if (y+1<Height) yield return i+Width;
        }
    }
    public static Plan Create(Vector2[] polygon, int modelType, float visualVariant)
    {
        var plan = new Plan { Polygon = polygon };
        if (polygon == null || polygon.Length < 3) return plan;
        var grid = MakeGrid(polygon);
        if (grid.Entry < 0) return plan;
        uint seed = (uint)Mathf.RoundToInt(Mathf.Clamp01(visualVariant)*1000000f);
        if (modelType == Constants.MODEL_PARKING) Parking(plan, grid);
        else if (modelType == Constants.MODEL_ROTARY) Rotary(plan, grid, seed);
        else if (modelType == Constants.MODEL_PARK) Park(plan, grid, seed);
        return plan;
    }
    static Grid MakeGrid(Vector2[] polygon)
    {
        Vector2 u=(polygon[1]-polygon[0]).normalized;
        if (u.sqrMagnitude < .5f) u=Vector2.right;
        Vector2 v=new Vector2(-u.y,u.x), front=(polygon[0]+polygon[1])*.5f;
        float minX=float.MaxValue,minY=float.MaxValue,maxX=float.MinValue,maxY=float.MinValue;
        foreach(var p in polygon)
        {
            float x=Vector2.Dot(p-front,u),y=Vector2.Dot(p-front,v);
            minX=Mathf.Min(minX,x); maxX=Mathf.Max(maxX,x); minY=Mathf.Min(minY,y); maxY=Mathf.Max(maxY,y);
        }
        // 巨大区画では入口側320m四方だけを装飾し、全面の地面描画は維持する。
        minX=Mathf.Max(minX,-MaxAxis*CellSize*.5f);
        maxX=Mathf.Min(maxX,minX+MaxAxis*CellSize);
        maxY=Mathf.Min(maxY,minY+MaxAxis*CellSize);
        var g=new Grid {U=u,V=v,Origin=front+u*minX+v*minY,Polygon=polygon,
            Width=Mathf.Max(1,Mathf.FloorToInt((maxX-minX)/CellSize+.0001f)),Height=Mathf.Max(1,Mathf.FloorToInt((maxY-minY)/CellSize+.0001f)),Entry=-1};
        g.Inside=new bool[g.Width*g.Height];
        float nearest=float.MaxValue;
        for(int i=0;i<g.Inside.Length;i++)
        {
            g.Inside[i]=ContainsPolygon(polygon,g.Box(i%g.Width,i/g.Width,1,1));
            float d=(g.Center(i)-front).sqrMagnitude;
            if(g.Inside[i] && d<nearest-.001f) {nearest=d;g.Entry=i;}
        }
        return g;
    }
    static int[] Flood(Grid g, bool[] allowed, int start)
    {
        int[] parent=new int[allowed.Length];
        for(int i=0;i<parent.Length;i++) parent[i]=-1;
        if(start<0 || !allowed[start]) return parent;
        var queue=new Queue<int>();queue.Enqueue(start);parent[start]=start;
        while(queue.Count>0)
        {
            int i=queue.Dequeue();
            foreach(int n in g.Neighbors(i)) if(allowed[n] && parent[n]<0) {parent[n]=i;queue.Enqueue(n);}
        }
        return parent;
    }
    static void Add(Plan p,string material,Vector2[] polygon,float lift=.012f)
    {
        p.Surfaces.Add(new Surface {Material=material,Polygon=polygon,Lift=lift});
    }
    static void Line(Plan p,Vector2 a,Vector2 b,float width,string material="site_marking",float lift=.025f)
    {
        Vector2 n=new Vector2(-(b-a).y,(b-a).x).normalized*width*.5f;
        var q=new[]{a-n,b-n,b+n,a+n};
        if(ContainsPolygon(p.Polygon,q)) Add(p,material,q,lift);
    }
    static void Parking(Plan p,Grid g)
    {
        int spine=Mathf.Clamp(g.Entry%g.Width-1,0,Mathf.Max(0,g.Width-2));
        var allowed=new bool[g.Inside.Length];
        for(int i=0;i<allowed.Length;i++) allowed[i]=g.Inside[i] && (i%g.Width==spine || i%g.Width==spine+1 || (i/g.Width)%6<2);
        int[] reachable=Flood(g,allowed,g.Entry);
        for(int i=0;i<reachable.Length;i++) if(reachable[i]>=0) p.AccessCells.Add(g.Center(i));
        for(int y=2;y+1<g.Height;y+=2)
        {
            if(y%6==0) continue;
            int aisle=y%6==2?y-1:y+2;
            if(aisle>=g.Height) continue;
            for(int x=0;x<g.Width;x++)
            {
                if(p.ParkingBays.Count>=512)return;
                if(x==spine || x==spine+1 || reachable[aisle*g.Width+x]<0) continue;
                if(!g.Inside[y*g.Width+x] || !g.Inside[(y+1)*g.Width+x]) continue;
                var q=g.Box(x+.06f,y+.06f,.88f,1.88f);
                if(!ContainsPolygon(p.Polygon,q)) continue;
                p.ParkingBays.Add(q);
                Line(p,q[0],q[3],.10f); Line(p,q[1],q[2],.10f);
                bool forward=y%6==2;
                Line(p,forward?q[3]:q[0],forward?q[2]:q[1],.10f);
                p.Props.Add(new Prop {Part="site_stop",Position=g.Point(x+.5f,forward?y+1.7f:y+.3f),Forward=forward?g.V:-g.V});
            }
        }
    }
    static void Park(Plan p,Grid g,uint seed)
    {
        int hub=g.Entry;float hubDistance=float.MaxValue;
        var fromEntry=Flood(g,g.Inside,g.Entry);
        Vector2 middle=g.Point(g.Width*.5f,g.Height*.5f);
        for(int i=0;i<fromEntry.Length;i++) if(fromEntry[i]>=0 && (g.Center(i)-middle).sqrMagnitude<hubDistance)
        {hub=i;hubDistance=(g.Center(i)-middle).sqrMagnitude;}
        int[] parent=Flood(g,g.Inside,hub);
        var paths=new bool[parent.Length];
        // 幅を持つセルの探索木を辿るので、凹区画の外側を園路が横切らない。
        for(int direction=0;direction<5;direction++)
        {
            int best=g.Entry;float score=float.MinValue;
            for(int i=0;i<parent.Length;i++) if(parent[i]>=0)
            {
                float value=direction==0?i%g.Width:direction==1?-(i%g.Width):direction==2?i/g.Width:-(i/g.Width);
                value-=.001f*(g.Center(i)-middle).sqrMagnitude;
                if(value>score) {score=value;best=i;}
            }
            if(direction==4)best=g.Entry;
            while(!paths[best]) {paths[best]=true;if(parent[best]==best)break;best=parent[best];}
        }
        // 広い長方形には主園路と接続した周回園路を加える。凹区画では無理に閉じない。
        if(g.Width>=12 && g.Height>=12)
        {
            var ring=new List<int>();bool valid=true;
            for(int y=2;y<g.Height-2;y++)for(int x=2;x<g.Width-2;x++)
                if(x==2 || y==2 || x==g.Width-3 || y==g.Height-3)
                {int i=y*g.Width+x;ring.Add(i);valid&=parent[i]>=0;}
            if(valid){foreach(int i in ring)paths[i]=true;p.HasLoop=true;}
        }
        for(int i=0;i<paths.Length;i++)
        {
            if(paths[i]) {Add(p,"site_paving",g.Box(i%g.Width,i/g.Width,1,1));p.AccessCells.Add(g.Center(i));continue;}
            if(parent[i]<0) continue;
            int x=i%g.Width,y=i/g.Width;
            uint h=Hash((uint)i ^ seed);
            bool nearPath=false;Vector2 facing=g.V;
            foreach(int n in g.Neighbors(i))if(paths[n]){nearPath=true;facing=(g.Center(n)-g.Center(i)).normalized;break;}
            if(p.Props.Count>=512)continue;
            if(nearPath && h%13==0)
                p.Props.Add(new Prop {Part="site_bench",Position=g.Center(i),Forward=facing});
            else if(!nearPath && x%2==1 && y%2==1 && h%5!=0)
            {
                var crown=g.Box(x-.15f,y-.15f,1.3f,1.3f);
                if(ContainsPolygon(p.Polygon,crown)) p.Props.Add(new Prop {Part="site_tree",Position=g.Center(i),Forward=g.V});
            }
        }
    }
    static uint Hash(uint x) {unchecked {x^=x>>16;x*=0x7feb352du;x^=x>>15;x*=0x846ca68bu;return x^(x>>16);}}
    // ------------------------------------------------------------------ 駅前ロータリー
    // 接道辺を基準にした局所座標(s=接道辺の向き、t=敷地の内側、原点=接道辺の中点)で形を作る。
    // NTSの膨張・収縮・集合演算だけで組み立てるので、敷地を回転・平行移動しても同じ形になる。
    // 左側通行なので周回は上から見て時計回り。バスは後方(駅側)の外周の乗降場に左側の扉を付けて停まる。

    static readonly GeometryFactory Nts = new GeometryFactory();
    const double LaneWidth = 7.0;      // 周回路の幅(停車帯と走行車線)
    const double AccessWidth = 7.0;    // 対面2車線の出入口
    const double OneWayWidth = 4.8;    // 入口と出口を分けるときの片側1車線
    const double BayDepth = 3.0;       // 後方の外周を広げるバス乗降場の奥行き
    const double ReturnRadius = 3.0;   // 出入口と周回路・前面道路のすみ切り
    const float Curb = .15f;           // 歩道と島の縁石の高さ

    static Geometry Union(Geometry a, Geometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.Union);
    static Geometry Minus(Geometry a, Geometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.Difference);
    static Geometry And(Geometry a, Geometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection);

    static BufferParameters Round() => new BufferParameters { JoinStyle = JoinStyle.Round, QuadrantSegments = 8 };

    sealed class Frame
    {
        public Vector2 O, U, V;
        public Coordinate Local(Vector2 p) => new Coordinate(Vector2.Dot(p - O, U), Vector2.Dot(p - O, V));
        public Vector2 World(double s, double t) => O + U * (float)s + V * (float)t;
        public Vector2 Dir(double s, double t) => (U * (float)s + V * (float)t).normalized;
    }

    static float Rand(uint seed, int k) => (Hash(seed * 0x9E3779B1u + (uint)k * 0x85EBCA77u) & 0xFFFFFF) / (float)0xFFFFFF;

    static Geometry Rect(double s0, double t0, double s1, double t1) =>
        Nts.CreatePolygon(new[] {new Coordinate(s0,t0),new Coordinate(s1,t0),new Coordinate(s1,t1),new Coordinate(s0,t1),new Coordinate(s0,t0)});

    static Geometry Poly(params double[] st)
    {
        var c = new Coordinate[st.Length / 2 + 1];
        for (int i = 0; i < st.Length / 2; i++) c[i] = new Coordinate(st[2 * i], st[2 * i + 1]);
        c[c.Length - 1] = c[0].Copy();
        return Nts.CreatePolygon(c);
    }

    /// <summary>面積の最も大きい多角形。空・線・点だけなら null。</summary>
    static Polygon Largest(Geometry g)
    {
        Polygon best = null;
        if (g == null) return null;
        for (int i = 0; i < g.NumGeometries; i++)
            if (g.GetGeometryN(i) is Polygon poly && !poly.IsEmpty && (best == null || poly.Area > best.Area)) best = poly;
        return best;
    }

    static IEnumerable<Polygon> Polygons(Geometry g)
    {
        if (g == null) yield break;
        for (int i = 0; i < g.NumGeometries; i++)
        {
            var part = g.GetGeometryN(i);
            if (part is Polygon poly && !poly.IsEmpty) yield return poly;
            else if (part is GeometryCollection && part != g) foreach (var inner in Polygons(part)) yield return inner;
        }
    }

    static bool Fits(Geometry g, double inset) => g != null && !g.Buffer(-inset, Round()).IsEmpty;

    /// <summary>NTSの面(穴を含む)を計画の面にする。側面の材質を与えると、地面から天端まで縁石の側面を張る。</summary>
    static void Emit(Plan p, Frame f, Geometry g, string material, float lift, string side = null)
    {
        foreach (var poly in Polygons(g))
        {
            if (poly.Area < .02) continue;
            var holes = new List<Vector2[]>();
            for (int h = 0; h < poly.NumInteriorRings; h++)
                if (Math.Abs(Area(poly.GetInteriorRingN(h).Coordinates)) > .02) holes.Add(ToWorld(f, poly.GetInteriorRingN(h).Coordinates));
            p.Surfaces.Add(new Surface {Material=material,Polygon=ToWorld(f,poly.ExteriorRing.Coordinates),
                Holes=holes.Count>0?holes.ToArray():null,Lift=lift,Side=side});
        }
    }

    static Vector2[] ToWorld(Frame f, Coordinate[] ring)
    {
        var points = new Vector2[ring.Length - 1];
        for (int i = 0; i < points.Length; i++) points[i] = f.World(ring[i].X, ring[i].Y);
        return points;
    }

    static double Area(Coordinate[] ring)
    {
        double a = 0;
        for (int i = 0; i + 1 < ring.Length; i++) a += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
        return a * .5;
    }

    /// <summary>進行方向 (ds, dt) の細長い四角形(白線・停止線)。</summary>
    static Geometry Strip(double s, double t, double ds, double dt, double length, double width)
    {
        double n = Math.Sqrt(ds * ds + dt * dt); ds /= n; dt /= n;
        double hs = -dt * width * .5, ht = ds * width * .5;
        double s1 = s + ds * length, t1 = t + dt * length;
        return Poly(s - hs, t - ht, s1 - hs, t1 - ht, s1 + hs, t1 + ht, s + hs, t + ht);
    }

    /// <summary>リングを弧長で歩き、間隔 step ごとに点と接線(リングの向き)を返す。</summary>
    static IEnumerable<(double s, double t, double ds, double dt)> Walk(Coordinate[] ring, double start, double step)
    {
        double next = start, walked = 0;
        for (int i = 0; i + 1 < ring.Length; i++)
        {
            double ds = ring[i + 1].X - ring[i].X, dt = ring[i + 1].Y - ring[i].Y, len = Math.Sqrt(ds * ds + dt * dt);
            if (len < 1e-9) continue;
            while (next <= walked + len)
            {
                double k = (next - walked) / len;
                yield return (ring[i].X + ds * k, ring[i].Y + dt * k, ds / len, dt / len);
                next += step;
            }
            walked += len;
        }
    }

    /// <summary>向きをそろえたリング。clockwise=true で上から見て時計回り。</summary>
    static Coordinate[] Oriented(Coordinate[] ring, bool clockwise)
    {
        var copy = (Coordinate[])ring.Clone();
        if ((Area(copy) < 0) != clockwise) Array.Reverse(copy);
        return copy;
    }

    sealed class Placer
    {
        public Plan Plan; public Frame F;
        public readonly List<Geometry> Taken = new List<Geometry>();
        /// <summary>中心(s,t)・正面(fs,ft)・幅w(ローカルX)・奥行きd(正面方向)の占有が region に収まり、既存と重ならなければ置く。</summary>
        public bool Put(string part, double s, double t, double fs, double ft, double w, double d, Geometry region)
        {
            if (Plan.Props.Count >= 160) return false;
            double n = Math.Sqrt(fs * fs + ft * ft); fs /= n; ft /= n;
            double xs = ft, xt = -fs;
            var box = Poly(s - xs * w / 2 - fs * d / 2, t - xt * w / 2 - ft * d / 2, s + xs * w / 2 - fs * d / 2, t + xt * w / 2 - ft * d / 2,
                s + xs * w / 2 + fs * d / 2, t + xt * w / 2 + ft * d / 2, s - xs * w / 2 + fs * d / 2, t - xt * w / 2 + ft * d / 2);
            if (region == null || !region.Contains(box)) return false;
            foreach (var other in Taken) if (other.Intersects(box)) return false;
            Taken.Add(box);
            Plan.Props.Add(new Prop {Part = part, Position = F.World(s, t), Forward = F.Dir(fs, ft)});
            return true;
        }
    }

    static void Rotary(Plan p, Grid g, uint seed)
    {
        var f = new Frame {O = (p.Polygon[0] + p.Polygon[1]) * .5f, U = g.U, V = g.V};
        var ring = new Coordinate[p.Polygon.Length + 1];
        // 0.1mm単位にそろえ、接道辺をちょうど t=0 に載せる(浮動小数点のずれで重なり判定が破綻しないように)。
        for (int i = 0; i < p.Polygon.Length; i++)
        {
            var c = f.Local(p.Polygon[i]);
            ring[i] = new Coordinate(Math.Round(c.X * 1e4) / 1e4, Math.Round(c.Y * 1e4) / 1e4);
        }
        ring[ring.Length - 1] = ring[0].Copy();
        Geometry site = Largest(Nts.CreatePolygon(ring).Buffer(0));
        if (site == null || site.Area < 60) return;
        var env = site.EnvelopeInternal;
        double halfFront = Vector2.Distance(p.Polygon[0], p.Polygon[1]) * .5;
        // 形の揺らぎはvisual_variant2から決める。左右の歩道の幅・前面と駅側の広場の奥行き・外周の四隅の丸み・出入口の位置。
        double[][] tries = {
            new[]{3.5 + 1.5 * Rand(seed, 1), 4.5 + 1.5 * Rand(seed, 2), 7.5 + 2.5 * Rand(seed, 3), 6 + 7 * Rand(seed, 4), 2.5 * Rand(seed, 8), 2.5 * Rand(seed, 9)},
            new[]{2.5, 3.5, 5.0, 5.0, 0, 0}};
        foreach (var setting in tries)
        {
            double side = setting[0], front = setting[1], rear = setting[2], radius = setting[3];
            var region = Largest(And(site.Buffer(-side, Round()),
                Rect(env.MinX + setting[4], front, env.MaxX - setting[5], env.MaxY - rear)));
            if (!Fits(region, LaneWidth + 1.6)) continue;
            // 島を置けない細い張り出し(L字の腕など)は周回路に含めず、広場として残す。
            region = Largest(region.Buffer(-(LaneWidth + 1.6), Round()).Buffer(LaneWidth + 1.6, Round()));
            if (region == null) continue;
            Polygon outer = null;
            for (double scale = 1; scale > .1 && outer == null; scale *= .7)
            {
                var opened = Largest(RoundCorners(region, radius * scale, seed));
                if (opened != null && opened.Area > region.Area * .7 && Fits(opened, LaneWidth + 1.6)) outer = opened;
            }
            if (outer == null) continue;
            // 島は周回路の幅だけ内側。細い継ぎ目で切れた部分も、ある程度の広さなら島として残す(L字の敷地の周回路)。
            double islandRound = 1.5 + 2.5 * Rand(seed, 7);
            var pieces = new List<Geometry>();
            foreach (var piece in Polygons(outer.Buffer(-LaneWidth, Round()).Buffer(-islandRound, Round()).Buffer(islandRound, Round())))
                if (piece.Area >= 8) pieces.Add(piece);
            if (pieces.Count == 0 || Largest(Nts.BuildGeometry(pieces)).Area < 10) continue;
            if (BuildRotary(p, f, site, outer, Nts.BuildGeometry(pieces), halfFront, seed)) return;
        }
        RotaryFallback(p, f, site, halfFront);
    }

    /// <summary>
    /// 四隅を別々の半径で丸める(外周の形を敷地ごとに少しずつ崩す)。象限ごとに丸めた形を切り出してつなぐ。
    /// 直線の辺ではどの半径で丸めても同じ位置になるので、半径を各辺の半分未満に抑えれば継ぎ目は一致する。
    /// </summary>
    static Geometry RoundCorners(Geometry region, double radius, uint seed)
    {
        var env = region.EnvelopeInternal;
        double cs = (env.MinX + env.MaxX) * .5, ct = (env.MinY + env.MaxY) * .5;
        double limit = Math.Min(env.Width, env.Height) * .5 - .5;
        var pieces = new List<Geometry>();
        for (int q = 0; q < 4; q++)
        {
            double r = Math.Max(1.5, Math.Min(limit, radius * (.55 + .9 * Rand(seed, 20 + q))));
            var opened = region.Buffer(-r, Round()).Buffer(r, Round());
            var quadrant = Rect(q % 2 == 0 ? env.MinX - 1 : cs, q < 2 ? env.MinY - 1 : ct, q % 2 == 0 ? cs : env.MaxX + 1, q < 2 ? ct : env.MaxY + 1);
            pieces.Add(And(opened, quadrant));
        }
        return OverlayNGRobust.Union(pieces).Buffer(0);
    }

    static bool BuildRotary(Plan p, Frame f, Geometry site, Polygon outer, Geometry island, double halfFront, uint seed)
    {
        var env = outer.EnvelopeInternal;
        double tRear = env.MaxY, tFront = env.MinY;
        // 後方の外周を奥行き3mだけ外へ広げてバス乗降場にする。奥に上屋と通路(4m)を取れる長さだけ。
        Geometry roadCore = outer;
        double bayS = 0, bayLength = 0;
        for (double length = Math.Min(26, env.Width * .75); length >= 12; length -= 2)
        {
            double sc = (env.MinX + env.MaxX) * .5;
            var bay = And(outer.Buffer(BayDepth, Round()), Rect(sc - length / 2, tRear - LaneWidth, sc + length / 2, tRear + BayDepth + 1));
            if (!site.Contains(bay.Buffer(4.2, Round()))) continue;
            roadCore = Union(outer, bay);
            bayS = sc; bayLength = length;
            break;
        }
        // 出入口は接道辺と周回路の前面が重なる範囲に置く。広ければ入口(右)と出口(左)を分け、狭ければ対面2車線を1本にする。
        double lo = Math.Max(env.MinX, -halfFront), hi = Math.Min(env.MaxX, halfFront);
        var accesses = new List<(double s, double width, bool entry, bool exit)>();
        double splitLo = lo + OneWayWidth / 2 + ReturnRadius + .5, splitHi = hi - OneWayWidth / 2 - ReturnRadius - .5;
        if (splitHi - splitLo >= 16 && Rand(seed, 6) > .45)
        {
            accesses.Add((splitHi - (splitHi - splitLo) * .1, OneWayWidth, true, false));
            accesses.Add((splitLo + (splitHi - splitLo) * .1, OneWayWidth, false, true));
        }
        else
        {
            double a0 = lo + AccessWidth / 2 + ReturnRadius + .5, a1 = hi - AccessWidth / 2 - ReturnRadius - .5;
            if (a1 < a0) return false;
            accesses.Add(((a0 + a1) * .5 + (Rand(seed, 5) - .5) * (a1 - a0) * .6, AccessWidth, true, true));
        }
        foreach (var a in accesses)
        {
            var strip = And(outer, Rect(a.s - a.width / 2, env.MinY - 1, a.s + a.width / 2, env.MaxY + 1));
            if (strip.IsEmpty) return false;
            double reach = strip.EnvelopeInternal.MinY + LaneWidth * .5;
            roadCore = Union(roadCore, Rect(a.s - a.width / 2, -2, a.s + a.width / 2, reach));
        }
        // 周回路と出入口の角はすみ切りで丸め、前面道路との角は四分円のすみ切りを足す。
        Geometry road = roadCore.Buffer(ReturnRadius, Round()).Buffer(-ReturnRadius, Round());
        foreach (var a in accesses)
            foreach (int side in new[]{-1, 1})
            {
                double edge = a.s + side * a.width / 2, r = ReturnRadius;
                var square = Rect(Math.Min(edge, edge + side * r), 0, Math.Max(edge, edge + side * r), r);
                var disk = Nts.CreatePoint(new Coordinate(edge + side * r, r)).Buffer(r, Round());
                road = Union(road, Minus(square, disk));
            }
        var roadFull = Largest(And(road, site));
        if (roadFull == null || !roadFull.Contains(island)) return false;
        var plaza = Minus(site, roadFull);
        AddRoadOpenings(p, f, roadFull, halfFront, accesses);
        p.HasLoop = true;

        // 面: 周回路・歩道(縁石で上げる)・島(芝と縁石)・縁石の天端の帯
        Emit(p, f, roadFull, "site_asphalt", .012f);
        Emit(p, f, plaza, "site_paving", Curb, "site_curb");
        Emit(p, f, island, "site_grass", Curb, "site_curb");
        Emit(p, f, And(plaza, roadFull.Buffer(.3, Round())), "site_curb", Curb + .003f);
        Emit(p, f, Minus(island, island.Buffer(-.3, Round())), "site_curb", Curb + .003f);

        // 路面標示: 車線境界の破線、時計回りの矢印、横断歩道、停止線、バス乗降場の黄色い枠
        var lanes = Largest(outer.Buffer(-LaneWidth * .5, Round()));
        var islandHalo = island.Buffer(.5, Round());
        if (lanes != null)
            foreach (var d in Walk(Oriented(lanes.ExteriorRing.Coordinates, true), 1.5, 6))
            {
                var dash = Strip(d.s, d.t, d.ds, d.dt, 3, .15);
                if (roadFull.Contains(dash) && !islandHalo.Intersects(dash)) Emit(p, f, dash, "site_marking", .022f);
            }
        var inner = Largest(outer.Buffer(-LaneWidth * .75, Round()));
        if (inner != null)
        {
            var cw = Oriented(inner.ExteriorRing.Coordinates, true);
            double perimeter = inner.Length;
            foreach (double k in new[]{.2, .45, .7, .95})
                foreach (var d in Walk(cw, perimeter * k, perimeter))
                {
                    double ns = -d.dt, nt = d.ds;
                    double s0 = d.s - d.ds * 1.4, t0 = d.t - d.dt * 1.4, s1 = d.s + d.ds * .2, t1 = d.t + d.dt * .2, s2 = d.s + d.ds * 1.4, t2 = d.t + d.dt * 1.4;
                    var arrow = Poly(s0 + ns * .12, t0 + nt * .12, s1 + ns * .12, t1 + nt * .12, s1 + ns * .45, t1 + nt * .45, s2, t2,
                        s1 - ns * .45, t1 - nt * .45, s1 - ns * .12, t1 - nt * .12, s0 - ns * .12, t0 - nt * .12);
                    if (roadFull.Contains(arrow) && !island.Intersects(arrow)) Emit(p, f, arrow, "site_marking", .022f);
                }
        }
        double crossing = Math.Min(2.4, tFront * .45);
        var yellow = new List<Geometry>();
        foreach (var a in accesses)
        {
            for (double x = a.s - a.width / 2 + .4; x + .45 <= a.s + a.width / 2 - .35; x += .9)
                Emit(p, f, And(Rect(x, crossing - 1.5, x + .45, crossing + 1.5), roadFull), "site_marking", .022f);
            if (a.exit)
            {
                double s0 = a.entry ? a.s : a.s - a.width / 2 + .3;
                Emit(p, f, And(Rect(s0, crossing + 1.9, a.s + a.width / 2 - .3, crossing + 2.2), roadFull), "site_marking", .022f);
            }
            foreach (int side in new[]{-1, 1})
            {
                double edge = a.s + side * (a.width / 2 + .2);
                yellow.Add(Rect(Math.Min(edge, edge + side * .3), crossing - 1.5, Math.Max(edge, edge + side * .3), crossing + 1.5));
            }
        }
        if (bayLength > 0)
        {
            double s0 = bayS - bayLength / 2 + 1.5, s1 = bayS + bayLength / 2 - 1.5, t0 = tRear + .4, t1 = tRear + BayDepth - .2;
            foreach (var line in new[]{Rect(s0, t0, s1, t0 + .15), Rect(s0, t1 - .15, s1, t1), Rect(s0, t0, s0 + .15, t1), Rect(s1 - .15, t0, s1, t1)})
                Emit(p, f, And(line, roadFull), "site_yellow", .022f);
            // 乗降場の縁石から0.6mの誘導ブロック
            yellow.Add(And(Minus(roadFull.Buffer(.9, Round()), roadFull.Buffer(.6, Round())),
                Rect(bayS - bayLength / 2, tRear, bayS + bayLength / 2, tRear + BayDepth + 2)));
        }
        foreach (var tactile in yellow) Emit(p, f, And(tactile, plaza), "site_tactile", Curb + .006f);

        // 小物: 島の時計塔と樹木・街灯、乗降場の上屋・自販機、タクシー乗り場、外周の街灯・ベンチ、広場の樹木・植栽、車止め
        var placer = new Placer {Plan = p, F = f};
        var islandSpace = island.Buffer(-.35, Round());
        var plazaSpace = plaza.Buffer(-.3, Round());
        var mainIsland = Largest(island);
        Coordinate clock = null;
        if (Fits(mainIsland, 2.6))
        {
            var c = mainIsland.Centroid.Coordinate;
            var core = mainIsland.Buffer(-2.4, Round());
            if (!core.Contains(Nts.CreatePoint(c))) c = core.InteriorPoint.Coordinate;
            if (placer.Put("site_clock", c.X, c.Y, 0, -1, 1.4, 1.4, islandSpace))
            {
                clock = c;
                Emit(p, f, And(Nts.CreatePoint(c).Buffer(2.3, Round()), mainIsland.Buffer(-.6, Round())), "site_paving", Curb + .004f);
            }
        }
        var islandEnv = island.EnvelopeInternal;
        var treeIsland = island.Buffer(-1.1, Round());
        var lampIsland = mainIsland.Buffer(-.8, Round());
        double off = 1.5 + Rand(seed, 11) * 2;
        int trees = 0;
        for (double t = islandEnv.MinY + off; t < islandEnv.MaxY && trees < 10; t += 4.6)
            for (double s = islandEnv.MinX + off; s < islandEnv.MaxX && trees < 10; s += 4.6)
            {
                if (clock != null && Math.Abs(s - clock.X) < 3.4 && Math.Abs(t - clock.Y) < 3.4) continue;
                if (Rand(seed, 100 + (int)(s * 7 + t * 131)) < .2) continue;
                if (placer.Put("site_tree", s, t, 0, -1, 2.6, 2.6, treeIsland)) trees++;
            }
        foreach (int side in new[]{-1, 1})
        {
            var edge = lampIsland.EnvelopeInternal;
            double s = side < 0 ? edge.MinX : edge.MaxX, t = (edge.MinY + edge.MaxY) * .5;
            var hit = And(lampIsland, Rect(s - side * 3, t - .3, s + side * .01, t + .3)).EnvelopeInternal;
            if (!hit.IsNull) placer.Put("site_light", side < 0 ? hit.MinX + .3 : hit.MaxX - .3, t, side, 0, 1.4, .5, islandSpace);
        }
        double rowEnd = double.NaN;
        if (bayLength > 0)
        {
            int count = Math.Max(1, (int)((bayLength - 2) / 6.6));
            double start = bayS - (count - 1) * 3.3;
            for (int i = 0; i < count; i++)
                if (placer.Put("site_shelter", start + i * 6.6, tRear + BayDepth + 1.7, 0, -1, 6.2, 2.6, plazaSpace)) rowEnd = start + i * 6.6 + 3.1;
            if (!double.IsNaN(rowEnd)) placer.Put("site_vending", rowEnd + 1.6, tRear + BayDepth + 1.9, 0, -1, 2.2, .9, plazaSpace);
            placer.Put("site_taxi", bayS - bayLength / 2 - 3.5, tRear + .9, 0, -1, .6, .5, plazaSpace);
        }
        else
        {
            placer.Put("site_shelter", (env.MinX + env.MaxX) * .5, tRear + 1.7, 0, -1, 6.2, 2.6, plazaSpace);
        }
        // 外周の縁石に沿った街灯とベンチ。敷地境界上の区間(前面道路側の出入口)は飛ばす。
        var curb = Oriented(roadFull.ExteriorRing.Coordinates, false);
        var boundary = site.Boundary;
        foreach (var d in Walk(curb, 4 + Rand(seed, 12) * 6, 14))
        {
            if (boundary.Distance(Nts.CreatePoint(new Coordinate(d.s, d.t))) < .5) continue;
            double ns = d.dt, nt = -d.ds;
            placer.Put("site_light", d.s + ns * .8, d.t + nt * .8, -ns, -nt, 1.4, .5, plazaSpace);
        }
        int benches = 0;
        foreach (var d in Walk(curb, 9 + Rand(seed, 13) * 4, 11))
        {
            if (benches >= 6 || boundary.Distance(Nts.CreatePoint(new Coordinate(d.s, d.t))) < 3) continue;
            if (Rand(seed, 300 + benches * 17 + (int)d.s) < .4) continue;
            double ns = d.dt, nt = -d.ds;
            if (placer.Put("site_bench", d.s + ns * 2.4, d.t + nt * 2.4, -ns, -nt, 2.0, .7, plazaSpace)) benches++;
        }
        // 広場の余白: 前面道路と周回路から離れた所に樹木、残りに植栽
        var treeSpace = Minus(plaza, roadFull.Buffer(2.2, Round())).Buffer(-1.3, Round());
        var plazaEnv = plaza.EnvelopeInternal;
        int plazaTrees = 0, planters = 0;
        for (double t = plazaEnv.MinY + 3; t < plazaEnv.MaxY && plazaTrees < 12; t += 6.5)
            for (double s = plazaEnv.MinX + 3; s < plazaEnv.MaxX && plazaTrees < 12; s += 6.5)
                if (Rand(seed, 500 + (int)(s * 13 + t * 71)) > .35 && placer.Put("site_tree", s, t, 0, -1, 2.6, 2.6, treeSpace)) plazaTrees++;
        var planterSpace = Minus(plaza, roadFull.Buffer(1.8, Round())).Buffer(-.8, Round());
        for (double t = plazaEnv.MinY + 1.5; t < plazaEnv.MaxY && planters < 8; t += 4.5)
            for (double s = plazaEnv.MinX + 2; s < plazaEnv.MaxX && planters < 8; s += 5)
                if (Rand(seed, 700 + (int)(s * 17 + t * 29)) > .65 && placer.Put("site_planter", s, t, 0, -1, 2.4, 1.0, planterSpace)) planters++;
        // 出入口の両脇の車止め(歩道への乗り入れを防ぐ)
        foreach (var a in accesses)
            foreach (int side in new[]{-1, 1})
                for (int k = 0; k < 2; k++)
                    placer.Put("site_bollard", a.s + side * (a.width / 2 + ReturnRadius + .6 + k * 1.3), .7, 0, -1, .25, .25, plazaSpace);
        var roadCenter = roadFull.InteriorPoint.Coordinate;
        p.AccessCells.Add(f.World(roadCenter.X, roadCenter.Y));
        return true;
    }

    /// <summary>周回路が入らない敷地は、接道辺沿いの乗降場(車寄せ)と縁石で上げた歩道にする。</summary>
    static void RotaryFallback(Plan p, Frame f, Geometry site, double halfFront)
    {
        var env = site.EnvelopeInternal;
        if (halfFront < 7 || env.Height < 9) return;
        double half = Math.Min(halfFront - 3, 12);
        var bay = And(site, Rect(-half, -1, half, 5));
        foreach (int side in new[]{-1, 1})
        {
            double edge = side * half, r = ReturnRadius;
            bay = Union(bay, And(Minus(Rect(Math.Min(edge, edge + side * r), 0, Math.Max(edge, edge + side * r), r),
                Nts.CreatePoint(new Coordinate(edge + side * r, r)).Buffer(r, Round())), site));
        }
        var road = Largest(bay);
        if (road == null) return;
        var plaza = Minus(site, road);
        AddRoadOpenings(p, f, road, halfFront, null);
        Emit(p, f, road, "site_asphalt", .012f);
        Emit(p, f, plaza, "site_paving", Curb, "site_curb");
        Emit(p, f, And(plaza, road.Buffer(.3, Round())), "site_curb", Curb + .003f);
        var placer = new Placer {Plan = p, F = f};
        var space = plaza.Buffer(-.3, Round());
        placer.Put("site_shelter", 0, 5 + 1.7, 0, -1, 6.2, 2.6, space);
        foreach (int side in new[]{-1, 1})
            placer.Put("site_light", side * (half - 1), 5.8, 0, -1, 1.4, .5, space);
        placer.Put("site_bench", -half * .5 - 2.5, 7.2, 0, -1, 2.0, .7, space);
        placer.Put("site_vending", half * .5 + 2.5, 7.2, 0, -1, 2.2, .9, space);
    }
    /// <summary>すみ切り後の車路と接道辺の交線が実際の間口。構内セルや車線の公称幅とは区別する。</summary>
    static void AddRoadOpenings(Plan p, Frame f, Geometry road, double halfFront,
        List<(double s, double width, bool entry, bool exit)> accesses)
    {
        var boundary = Nts.CreateLineString(new[] { new Coordinate(-halfFront, 0), new Coordinate(halfFront, 0) });
        var crossing = And(road, boundary);
        var lines = new List<LineString>();
        void Collect(Geometry geometry)
        {
            if (geometry is LineString line && line.Length > .000001) lines.Add(line);
            else if (geometry is GeometryCollection group)
                for (int i = 0; i < group.NumGeometries; i++) Collect(group.GetGeometryN(i));
        }
        Collect(crossing);
        lines.Sort((a, b) => a.EnvelopeInternal.MinX.CompareTo(b.EnvelopeInternal.MinX));
        var intervals = new List<(double lo, double hi)>();
        foreach (var line in lines)
        {
            double lo = line.EnvelopeInternal.MinX, hi = line.EnvelopeInternal.MaxX;
            if (intervals.Count > 0 && lo <= intervals[intervals.Count - 1].hi + .0001)
                intervals[intervals.Count - 1] = (intervals[intervals.Count - 1].lo, Math.Max(hi, intervals[intervals.Count - 1].hi));
            else intervals.Add((lo, hi));
        }
        foreach (var (lo, hi) in intervals)
        {
            if (hi - lo < .2) continue;
            Vector2 a = f.World(lo, 0), b = f.World(hi, 0), outward = f.World(0, 0) - f.World(0, 1);
            var apron = Largest(And(road, Rect(lo, 0, hi, BuildingRoadAccess.ApronDepth)));
            if (apron == null) continue;
            var ring = apron.ExteriorRing.Coordinates;
            var points = new Vector2[ring.Length - 1];
            for (int i = 0; i < points.Length; i++) points[i] = f.World(ring[i].X, ring[i].Y);
            string flow = "both";
            if (accesses != null)
                foreach (var access in accesses)
                    if (access.s >= lo && access.s <= hi) flow = access.entry && access.exit ? "both" : access.entry ? "entry" : "exit";
            float pavement = ModularBodyBuilder.GROUND_OFFSET + .012f;
            p.Openings.Add(new BuildingRoadOpening { A = new Vector3(a.x, pavement, a.y), B = new Vector3(b.x, pavement, b.y),
                Outward = new Vector3(outward.x, 0, outward.y).normalized, Apron = points, FollowsTerrain = true, Flow = flow });
        }
    }

    static float Cross(Vector2 a,Vector2 b) => a.x*b.y-a.y*b.x;
    static bool Intersects(Vector2 a,Vector2 b,Vector2 c,Vector2 d)
    {
        float ab1=Cross(b-a,c-a),ab2=Cross(b-a,d-a),cd1=Cross(d-c,a-c),cd2=Cross(d-c,b-c);
        float abTolerance=.001f*(b-a).magnitude,cdTolerance=.001f*(d-c).magnitude;
        return ((ab1>abTolerance && ab2<-abTolerance)||(ab2>abTolerance && ab1<-abTolerance))
            && ((cd1>cdTolerance && cd2<-cdTolerance)||(cd2>cdTolerance && cd1<-cdTolerance));
    }
    public static bool ContainsPolygon(Vector2[] polygon,Vector2[] candidate)
    {
        for(int i=0;i<candidate.Length;i++)
        {
            Vector2 a=candidate[i],b=candidate[(i+1)%candidate.Length];
            if(!PolygonUtils.Contains(polygon,a,BOUNDARY_TOLERANCE) || !PolygonUtils.Contains(polygon,(a+b)*.5f,BOUNDARY_TOLERANCE))return false;
            for(int j=0;j<polygon.Length;j++) if(Intersects(a,b,polygon[j],polygon[(j+1)%polygon.Length]))return false;
        }
        return true;
    }
    static bool PolygonsOverlap(Vector2[] a,Vector2[] b)
    {
        foreach(var p in a)if(PolygonUtils.Contains(b,p,BOUNDARY_TOLERANCE))return true;
        foreach(var p in b)if(PolygonUtils.Contains(a,p,BOUNDARY_TOLERANCE))return true;
        for(int i=0;i<a.Length;i++)for(int j=0;j<b.Length;j++)if(Intersects(a[i],a[(i+1)%a.Length],b[j],b[(j+1)%b.Length]))return true;
        return false;
    }
}
