using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// 川1本分の判定用データ。
///
/// 川は「範囲ポリゴン(polygon。穴=中州)」と「水面標高のプロファイル線(profile)」で定義される
/// (バックエンド GET /terrain/rivers。仕様の正は D_地形形状.md §D.11)。範囲内の任意地点の
/// 水面標高は「profile 折れ線上の最近点の y」で決まり、この規則は地形エディタ
/// (riverGeometry.ts の RiverProfileSampler)・バックエンド(geo_rivers.py)と共通の契約。
///
/// 水面メッシュ(RiverSurfaceManager)と干渉の占有箱(RoadPlanManager / HighlightManager)が
/// 同じ格子(GridStep 間隔、CornerMask)を共有するため、格子のラスタライズ結果もここで持つ。
/// </summary>
public class RiverRecord
{
    public string Id;
    /// <summary>全リング(全パートの外周+穴)。内外判定は全リングの even-odd。</summary>
    public readonly List<Vector2[]> Rings = new List<Vector2[]>();
    public float MinX, MinZ, MaxX, MaxZ;

    // プロファイル線(x, y, z)と線分の格子バケット(最近線分探索用)
    private float[] px, py, pz;
    // 各線分の符号付き曲率(1/m。正=左カーブ)。両端頂点での方向転換角を平均線分長で割り、
    // 前後数線分で平滑化したもの。川岸の砂地(カーブ内岸)の判定に使う
    private float[] segCurvature;
    private int segCount;
    private float bucketSize;
    private float bx0, bz0;
    private int bw, bh;
    private List<int>[] buckets;

    // 水面メッシュ・占有箱で共有する格子。格子点 (i, j) → (GridOriginX + i*GridStep, GridOriginZ + j*GridStep)
    public float GridOriginX, GridOriginZ, GridStep;
    public int GridW, GridH;
    /// <summary>格子点がポリゴン内側なら1(行優先 GridW×GridH)。</summary>
    public byte[] CornerMask;

    /// <summary>セル (i, j)(左上格子点)が川の占有範囲に含まれるか(4隅のいずれかが内側)。</summary>
    public bool CellIncluded(int i, int j)
    {
        if (i < 0 || j < 0 || i >= GridW - 1 || j >= GridH - 1) return false;
        return (CornerMask[j * GridW + i] | CornerMask[j * GridW + i + 1]
            | CornerMask[(j + 1) * GridW + i] | CornerMask[(j + 1) * GridW + i + 1]) != 0;
    }

    /// <summary>描画専用の岸への埋め込み余白。占有判定や川床マスクは広げない。</summary>
    public bool SurfaceCellIncluded(int i, int j)
    {
        for (int y = j - 1; y <= j + 1; y++)
            for (int x = i - 1; x <= i + 1; x++)
                if (CellIncluded(x, y)) return true;
        return false;
    }

    /// <summary>プロファイル線を設定し、最近線分探索用のバケットを構築する。</summary>
    public void SetProfile(List<Vector3> profile, float bucket = 64f)
    {
        int n = profile.Count;
        px = new float[n]; py = new float[n]; pz = new float[n];
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            px[i] = profile[i].x; py[i] = profile[i].y; pz[i] = profile[i].z;
            minX = Mathf.Min(minX, px[i]); maxX = Mathf.Max(maxX, px[i]);
            minZ = Mathf.Min(minZ, pz[i]); maxZ = Mathf.Max(maxZ, pz[i]);
        }
        segCount = Mathf.Max(0, n - 1);
        bucketSize = Mathf.Max(1f, bucket);
        if (n == 0) { minX = maxX = minZ = maxZ = 0f; }
        bx0 = minX; bz0 = minZ;
        bw = Mathf.Max(1, Mathf.FloorToInt((maxX - minX) / bucketSize) + 1);
        bh = Mathf.Max(1, Mathf.FloorToInt((maxZ - minZ) / bucketSize) + 1);
        // 線分の符号付き曲率: 頂点 i の転換角(前後の線分方向の外積の atan2)を、頂点ごとに
        // 隣接線分の平均長で割って曲率にし、線分の両端頂点の平均を前後3線分の窓で平滑化する
        var vertexCurv = new float[n];
        for (int i = 1; i < n - 1; i++)
        {
            float d0x = px[i] - px[i - 1], d0z = pz[i] - pz[i - 1];
            float d1x = px[i + 1] - px[i], d1z = pz[i + 1] - pz[i];
            float l0 = Mathf.Sqrt(d0x * d0x + d0z * d0z), l1 = Mathf.Sqrt(d1x * d1x + d1z * d1z);
            if (l0 < 1e-6f || l1 < 1e-6f) continue;
            float cross = d0x * d1z - d0z * d1x;
            float dot = d0x * d1x + d0z * d1z;
            vertexCurv[i] = Mathf.Atan2(cross, dot) / (0.5f * (l0 + l1));
        }
        var raw = new float[segCount];
        for (int s = 0; s < segCount; s++) raw[s] = 0.5f * (vertexCurv[s] + vertexCurv[s + 1]);
        segCurvature = new float[segCount];
        for (int s = 0; s < segCount; s++)
        {
            float sum = 0f; int count = 0;
            for (int k = -3; k <= 3; k++)
            {
                int j = s + k;
                if (j < 0 || j >= segCount) continue;
                sum += raw[j]; count++;
            }
            segCurvature[s] = count > 0 ? sum / count : 0f;
        }

        buckets = new List<int>[bw * bh];
        for (int s = 0; s < segCount; s++)
        {
            int cMin = Mathf.FloorToInt((Mathf.Min(px[s], px[s + 1]) - bx0) / bucketSize);
            int cMax = Mathf.FloorToInt((Mathf.Max(px[s], px[s + 1]) - bx0) / bucketSize);
            int rMin = Mathf.FloorToInt((Mathf.Min(pz[s], pz[s + 1]) - bz0) / bucketSize);
            int rMax = Mathf.FloorToInt((Mathf.Max(pz[s], pz[s + 1]) - bz0) / bucketSize);
            for (int r = rMin; r <= rMax; r++)
                for (int c = cMin; c <= cMax; c++)
                {
                    int bi = r * bw + c;
                    (buckets[bi] ??= new List<int>()).Add(s);
                }
        }
    }

    /// <summary>(x, z) に最も近いプロファイル線分と射影パラメータ・距離を返す。</summary>
    public void Nearest(float x, float z, out int seg, out float t, out float dist)
    {
        seg = 0; t = 0f; dist = float.PositiveInfinity;
        if (segCount == 0) return;
        int cq = Mathf.FloorToInt((x - bx0) / bucketSize);
        int rq = Mathf.FloorToInt((z - bz0) / bucketSize);
        int maxRing = Mathf.Max(bw, bh) + Mathf.Abs(cq) + Mathf.Abs(rq) + 2;
        for (int ring = 0; ring <= maxRing; ring++)
        {
            // このリングより外の線分は少なくとも (ring-1)*bucketSize 離れているので打ち切れる
            if (dist <= (ring - 1) * bucketSize) break;
            int rMin = rq - ring, rMax = rq + ring, cMin = cq - ring, cMax = cq + ring;
            for (int r = rMin; r <= rMax; r++)
            {
                if (r < 0 || r >= bh) continue;
                bool edgeRow = r == rMin || r == rMax;
                for (int c = cMin; c <= cMax; c++)
                {
                    if (c < 0 || c >= bw) continue;
                    if (!edgeRow && c != cMin && c != cMax) continue;
                    var list = buckets[r * bw + c];
                    if (list == null) continue;
                    foreach (int s in list)
                    {
                        float ax = px[s], az = pz[s];
                        float dx = px[s + 1] - ax, dz = pz[s + 1] - az;
                        float len2 = dx * dx + dz * dz;
                        float tt = len2 > 1e-12f ? ((x - ax) * dx + (z - az) * dz) / len2 : 0f;
                        tt = Mathf.Clamp01(tt);
                        float ex = ax + dx * tt - x, ez = az + dz * tt - z;
                        float d = Mathf.Sqrt(ex * ex + ez * ez);
                        if (d < dist) { dist = d; seg = s; t = tt; }
                    }
                }
            }
        }
    }

    /// <summary>(x, z) の水面標高(最近線分上で y を線形補間)。</summary>
    public float WaterLevelAt(float x, float z)
    {
        if (segCount == 0) return py != null && py.Length > 0 ? py[0] : 0f;
        Nearest(x, z, out int seg, out float t, out _);
        return py[seg] + (py[seg + 1] - py[seg]) * t;
    }

    /// <summary>
    /// (x, z) の「内岸度」(0..1)。最近線分の符号付き曲率と、点が線分のどちら側にあるかの符号が
    /// 一致する(=カーブの内側)とき、曲率の大きさを curvRef で正規化した値を返す。外岸・直線区間は0。
    /// </summary>
    public float InnerBankFactorAt(float x, float z, float curvRef)
    {
        if (segCount == 0) return 0f;
        Nearest(x, z, out int seg, out float t, out _);
        float curv = segCurvature[seg];
        if (Mathf.Abs(curv) < 1e-9f) return 0f;
        float dx = px[seg + 1] - px[seg], dz = pz[seg + 1] - pz[seg];
        float nx = px[seg] + dx * t, nz = pz[seg] + dz * t;
        // 流下方向に対して左側が正(左カーブの内岸は左側)
        float side = dx * (z - nz) - dz * (x - nx);
        if (side * curv <= 0f) return 0f;
        return Mathf.Clamp01(Mathf.Abs(curv) / Mathf.Max(curvRef, 1e-6f));
    }

    /// <summary>(x, z) の流下方向(最近線分の単位接線、XZ)。</summary>
    public Vector2 FlowDirAt(float x, float z)
    {
        if (segCount == 0) return new Vector2(1f, 0f);
        Nearest(x, z, out int seg, out _, out _);
        var d = new Vector2(px[seg + 1] - px[seg], pz[seg + 1] - pz[seg]);
        return d.sqrMagnitude > 1e-12f ? d.normalized : new Vector2(1f, 0f);
    }

    /// <summary>(x, z) が範囲内か(外接矩形で前置判定してから全リングの even-odd)。</summary>
    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
        bool inside = false;
        foreach (var ring in Rings)
        {
            int n = ring.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((ring[i].y > z) != (ring[j].y > z))
                {
                    float xCross = ring[i].x + (z - ring[i].y) * (ring[j].x - ring[i].x) / (ring[j].y - ring[i].y);
                    if (x < xCross) inside = !inside;
                }
            }
        }
        return inside;
    }

    /// <summary>
    /// ポリゴンを格子点マスクへラスタライズする(スキャンライン + even-odd)。
    /// 地形エディタ riverGeometry.ts の rasterizePolygonMask と同じ規則。
    /// </summary>
    public void BuildGrid(float step)
    {
        GridStep = step;
        GridOriginX = Mathf.Floor(MinX / step) * step - 2 * step;
        GridOriginZ = Mathf.Floor(MinZ / step) * step - 2 * step;
        GridW = Mathf.CeilToInt((MaxX - GridOriginX) / step) + 3;
        GridH = Mathf.CeilToInt((MaxZ - GridOriginZ) / step) + 3;
        CornerMask = new byte[GridW * GridH];

        var edges = new List<float>();
        foreach (var ring in Rings)
        {
            int n = ring.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (ring[j].y == ring[i].y) continue;
                edges.Add(ring[j].x); edges.Add(ring[j].y); edges.Add(ring[i].x); edges.Add(ring[i].y);
            }
        }
        int edgeCount = edges.Count / 4;
        var crossings = new List<float>();
        for (int j = 0; j < GridH; j++)
        {
            float z = GridOriginZ + j * step;
            crossings.Clear();
            for (int e = 0; e < edgeCount; e++)
            {
                float x1 = edges[e * 4], z1 = edges[e * 4 + 1], x2 = edges[e * 4 + 2], z2 = edges[e * 4 + 3];
                if ((z1 > z) == (z2 > z)) continue;
                crossings.Add(x1 + (z - z1) * (x2 - x1) / (z2 - z1));
            }
            if (crossings.Count < 2) continue;
            crossings.Sort();
            for (int k = 0; k + 1 < crossings.Count; k += 2)
            {
                int iStart = Mathf.Max(0, Mathf.CeilToInt((crossings[k] - GridOriginX) / step));
                int iEnd = Mathf.Min(GridW - 1, Mathf.FloorToInt((crossings[k + 1] - GridOriginX) / step));
                for (int i = iStart; i <= iEnd; i++) CornerMask[j * GridW + i] = 1;
            }
        }
    }
}

/// <summary>
/// 川の静的ストア。RiverSurfaceManager がセッション初期化時に GET /terrain/rivers から構築し、
/// 水面メッシュの生成、敷設中パスの川横断判定(RoadPlanManager)、権威干渉ハイライト
/// (HighlightManager)が参照する。海岸線の CoastlineStore と同じ位置付け。
/// </summary>
public static class RiverStore
{
    /// <summary>水面メッシュ・占有箱の格子間隔(m)。地形格子(10m)より細かく岸線へ追従させる。</summary>
    public const float GRID_STEP = 4f;
    /// <summary>占有箱の底面を水面から下げる量(m)。川床の深さは川ごとに違うため固定値で示す。</summary>
    public const float OCCUPANCY_BOX_DEPTH = 4f;

    private static readonly List<RiverRecord> rivers = new List<RiverRecord>();

    public static IReadOnlyList<RiverRecord> Rivers => rivers;
    public static bool HasRivers => rivers.Count > 0;

    public static void Clear()
    {
        rivers.Clear();
    }

    /// <summary>GET /terrain/rivers の rivers 配列(JToken)からストアを構築する。</summary>
    public static void SetFromJson(JToken riversToken)
    {
        rivers.Clear();
        if (!(riversToken is JArray arr)) return;
        foreach (JToken item in arr)
        {
            var rec = Parse(item);
            if (rec != null) rivers.Add(rec);
        }
    }

    private static RiverRecord Parse(JToken item)
    {
        if (item == null) return null;
        var rec = new RiverRecord { Id = item["id"]?.ToString() ?? "" };
        JToken polygon = item["polygon"];
        string gtype = polygon?["type"]?.ToString();
        JToken coords = polygon?["coordinates"];
        if (coords == null) return null;
        if (gtype == "Polygon") AddRings(rec, coords);
        else if (gtype == "MultiPolygon") foreach (JToken part in coords) AddRings(rec, part);
        if (rec.Rings.Count == 0) return null;

        rec.MinX = rec.MinZ = float.PositiveInfinity;
        rec.MaxX = rec.MaxZ = float.NegativeInfinity;
        foreach (var ring in rec.Rings)
            foreach (var p in ring)
            {
                rec.MinX = Mathf.Min(rec.MinX, p.x); rec.MaxX = Mathf.Max(rec.MaxX, p.x);
                rec.MinZ = Mathf.Min(rec.MinZ, p.y); rec.MaxZ = Mathf.Max(rec.MaxZ, p.y);
            }

        var profile = new List<Vector3>();
        if (item["profile"] is JArray prof)
            foreach (JToken p in prof)
                if (p is JArray pa && pa.Count >= 3)
                    profile.Add(new Vector3((float)pa[0], (float)pa[1], (float)pa[2]));
        if (profile.Count < 2) return null;
        rec.SetProfile(profile);
        rec.BuildGrid(GRID_STEP);
        return rec;
    }

    private static void AddRings(RiverRecord rec, JToken rings)
    {
        if (!(rings is JArray arr)) return;
        foreach (JToken ringToken in arr)
        {
            if (!(ringToken is JArray ring)) continue;
            var pts = new List<Vector2>(ring.Count);
            foreach (JToken p in ring)
                if (p is JArray pa && pa.Count >= 2) pts.Add(new Vector2((float)pa[0], (float)pa[1]));
            // GeoJSON の閉じ点(始点=終点)は除く
            if (pts.Count >= 2 && (pts[0] - pts[pts.Count - 1]).sqrMagnitude < 1e-6f) pts.RemoveAt(pts.Count - 1);
            if (pts.Count >= 3) rec.Rings.Add(pts.ToArray());
        }
    }

    /// <summary>(x, z) を範囲に含む川を返す(無ければ null)。</summary>
    public static RiverRecord FindAt(float x, float z)
    {
        foreach (var rec in rivers)
            if (rec.Contains(x, z)) return rec;
        return null;
    }

    public static RiverRecord FindById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var rec in rivers)
            if (rec.Id == id) return rec;
        return null;
    }

    /// <summary>
    /// 川の範囲を示す半透明の占有箱(上面=水面+ε、底面=水面-OCCUPANCY_BOX_DEPTH、外周に側壁)を
    /// 三角形リストへ追加する。nearPoints を渡すと、そのいずれかから radius 以内のセルだけを
    /// 箱にする(敷設中パスが横切っている付近だけを示す)。null なら川全体。
    /// 道路の占有箱(OccupancyVolumeUtil.AddSegment)と同じ警告色で描く前提。
    /// </summary>
    public static void AddOccupancyBox(List<Vector3> triangles, RiverRecord river, List<Vector3> nearPoints, float radius)
    {
        if (river == null || river.CornerMask == null) return;
        int w = river.GridW, h = river.GridH;
        float step = river.GridStep;
        float r2 = radius * radius;
        var cells = new HashSet<int>();
        for (int j = 0; j < h - 1; j++)
        {
            for (int i = 0; i < w - 1; i++)
            {
                if (!river.CellIncluded(i, j)) continue;
                if (nearPoints != null)
                {
                    float cx = river.GridOriginX + (i + 0.5f) * step;
                    float cz = river.GridOriginZ + (j + 0.5f) * step;
                    bool near = false;
                    foreach (var p in nearPoints)
                    {
                        float dx = p.x - cx, dz = p.z - cz;
                        if (dx * dx + dz * dz <= r2) { near = true; break; }
                    }
                    if (!near) continue;
                }
                cells.Add(j * w + i);
            }
        }
        if (cells.Count == 0) return;

        Vector3 Corner(int i, int j, float dy)
        {
            float x = river.GridOriginX + i * step;
            float z = river.GridOriginZ + j * step;
            return new Vector3(x, river.WaterLevelAt(x, z) + dy, z);
        }
        const float top = 0.1f;
        float bottom = -OCCUPANCY_BOX_DEPTH;
        foreach (int cell in cells)
        {
            int i = cell % w, j = cell / w;
            Vector3 tl = Corner(i, j, top), tr = Corner(i + 1, j, top), bl = Corner(i, j + 1, top), br = Corner(i + 1, j + 1, top);
            Vector3 tl2 = Corner(i, j, bottom), tr2 = Corner(i + 1, j, bottom), bl2 = Corner(i, j + 1, bottom), br2 = Corner(i + 1, j + 1, bottom);
            AddQuad(triangles, tl, tr, br, bl);      // 上面
            AddQuad(triangles, tl2, bl2, br2, tr2);  // 底面
            if (!cells.Contains(j * w + i - 1) || i == 0) AddQuad(triangles, tl, bl, bl2, tl2);           // 左壁
            if (!cells.Contains(j * w + i + 1)) AddQuad(triangles, tr, tr2, br2, br);                     // 右壁
            if (!cells.Contains((j - 1) * w + i) || j == 0) AddQuad(triangles, tl, tl2, tr2, tr);        // 手前壁
            if (!cells.Contains((j + 1) * w + i)) AddQuad(triangles, bl, br, br2, bl2);                   // 奥壁
        }
    }

    private static void AddQuad(List<Vector3> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        triangles.Add(a); triangles.Add(b); triangles.Add(c);
        triangles.Add(a); triangles.Add(c); triangles.Add(d);
    }
}
