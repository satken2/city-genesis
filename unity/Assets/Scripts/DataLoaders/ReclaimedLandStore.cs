using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 埋立地の外周の区間(岸壁・壁)。座標はワールドXZ(x=X, y=Z)、Normal は埋立地の外向きの単位ベクトル。
/// </summary>
public readonly struct ReclaimedLandSegment
{
    public readonly Vector2 A;
    public readonly Vector2 B;
    public readonly Vector2 Normal;

    public ReclaimedLandSegment(Vector2 a, Vector2 b, Vector2 normal)
    {
        A = a;
        B = b;
        Normal = normal;
    }
}

/// <summary>岸壁区間1つと、それが属する埋立地の天端の絶対標高(港のクレーン配置用)。</summary>
public readonly struct ReclaimedQuay
{
    public readonly int LandId;
    public readonly ReclaimedLandSegment Segment;
    public readonly float TopHeight;

    public ReclaimedQuay(int landId, ReclaimedLandSegment segment, float topHeight)
    {
        LandId = landId;
        Segment = segment;
        TopHeight = topHeight;
    }
}

/// <summary>
/// 埋立地1件(GET /terrain/reclaimed-lands の1要素)。Id・Version・Ring は保存値、
/// 天端の高さ・岸壁・壁・帯はバックエンド(geo_reclaimed_land)が海岸線と地形から求めた派生値で、
/// Unity はこれを描いて判定に使うだけにする(岸壁の判定を二重に実装すると端点や天端がずれる)。
/// </summary>
public sealed class ReclaimedLandRecord
{
    public int Id;
    public int Version;
    public Vector2[] Ring;                  // 外周(閉じ点を除く)
    public float? TopHeight;                // 天端の絶対標高。岸壁が無ければ null
    public ReclaimedLandSegment[] Quays;    // 海に面する岸壁区間
    public ReclaimedLandSegment[] Walls;    // 垂直の面を張る区間(岸壁と、帯が陸側の辺に沿って出る袖)
    public Vector2[][][] Band;              // 天端で覆う帯の多角形の列。各多角形は [外周, 穴...]
    public Rect[] BandBounds;               // Band の多角形ごとの外接矩形
    public Rect Bounds;                     // Ring・Band・Walls をすべて含む外接矩形
}

/// <summary>
/// 埋立地の静的ストア。<see cref="ReclaimedLandManager"/> が GET /terrain/reclaimed-lands から構築し、
/// 接地(<see cref="Utils.GetGroundHeight"/> の帯の中の天端)と港の岸壁判定が参照する。
/// 海岸線の CoastlineStore・湖の WaterSurfaceStore と同じ位置付け。
/// </summary>
public static class ReclaimedLandStore
{
    private static readonly List<ReclaimedLandRecord> lands = new List<ReclaimedLandRecord>();
    // 全埋立地の帯の外接矩形。接地はホットパスなので、帯が無い・外側の点は1回の比較で返す。
    private static bool hasBand;
    private static float bandMinX, bandMinZ, bandMaxX, bandMaxZ;

    /// <summary>Id の昇順の埋立地。</summary>
    public static IReadOnlyList<ReclaimedLandRecord> Lands => lands;

    /// <summary>
    /// 中身の世代番号。埋立地の集合か、いずれかの中身(形・天端・岸壁・壁・帯)が変わったときだけ進む。
    /// 取り直しのたびに進めると、これを描画キーに混ぜた港がエポックごとに全部作り直されるため。
    /// </summary>
    public static int ContentVersion { get; private set; }

    public static ReclaimedLandRecord Find(int id)
    {
        for (int i = 0; i < lands.Count; i++)
        {
            if (lands[i].Id == id) return lands[i];
        }
        return null;
    }

    /// <summary>
    /// 中身を records に置き換える。中身が同じなら何もせず false を返す。
    /// 変わったときは <see cref="ContentVersion"/> を進め、接地のフレームキャッシュを捨てる。
    /// </summary>
    public static bool Replace(IReadOnlyList<ReclaimedLandRecord> records)
    {
        var sorted = new List<ReclaimedLandRecord>();
        if (records != null)
        {
            foreach (var record in records)
            {
                if (record != null) sorted.Add(record);
            }
        }
        sorted.Sort((a, b) => a.Id.CompareTo(b.Id));

        if (SameContent(lands, sorted)) return false;

        lands.Clear();
        lands.AddRange(sorted);
        RebuildBandBounds();
        ContentVersion++;
        Utils.InvalidateHeightCache();
        return true;
    }

    /// <summary>保持している埋立地を破棄する。セッション切り替え時に呼ぶ。</summary>
    public static void Clear()
    {
        if (lands.Count == 0) return;
        lands.Clear();
        RebuildBandBounds();
        ContentVersion++;
        Utils.InvalidateHeightCache();
    }

    /// <summary>
    /// (x, z) がいずれかの埋立地の帯の中なら、その天端の絶対標高を返す。
    /// 帯の多角形は穴を持ちうるので、外周と穴の全リングの偶奇で判定する。
    /// </summary>
    public static bool TryGetBandTop(float x, float z, out float top)
    {
        top = 0f;
        if (!hasBand || x < bandMinX || x > bandMaxX || z < bandMinZ || z > bandMaxZ) return false;

        bool found = false;
        var point = new Vector2(x, z);
        for (int l = 0; l < lands.Count; l++)
        {
            ReclaimedLandRecord land = lands[l];
            if (!land.TopHeight.HasValue || !Contains(land.Bounds, x, z)) continue;
            Vector2[][][] band = land.Band;
            for (int p = 0; p < band.Length; p++)
            {
                if (!Contains(land.BandBounds[p], x, z)) continue;
                bool inside = false;
                Vector2[][] rings = band[p];
                for (int r = 0; r < rings.Length; r++)
                {
                    if (PolygonUtils.Contains(rings[r], point)) inside = !inside;
                }
                if (!inside) continue;
                float landTop = land.TopHeight.Value;
                if (!found || landTop > top) top = landTop;
                found = true;
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// 外接矩形が area に重なる岸壁区間を result に入れる(result は先に空にする)。
    /// 港が「埋立地の岸壁に面しているか」を判定する入口で、岸壁の規則を港側で再実装しないためのもの。
    /// </summary>
    public static void GetQuaySegments(Rect area, List<ReclaimedQuay> result)
    {
        result.Clear();
        for (int l = 0; l < lands.Count; l++)
        {
            ReclaimedLandRecord land = lands[l];
            if (!land.TopHeight.HasValue || !Overlaps(land.Bounds, area)) continue;
            foreach (var quay in land.Quays)
            {
                float minX = Mathf.Min(quay.A.x, quay.B.x), maxX = Mathf.Max(quay.A.x, quay.B.x);
                float minZ = Mathf.Min(quay.A.y, quay.B.y), maxZ = Mathf.Max(quay.A.y, quay.B.y);
                if (maxX < area.xMin || minX > area.xMax || maxZ < area.yMin || minZ > area.yMax) continue;
                result.Add(new ReclaimedQuay(land.Id, quay, land.TopHeight.Value));
            }
        }
    }

    /// <summary>2つの埋立地の中身(保存値と派生値のすべて)が同じか。</summary>
    public static bool SameContent(ReclaimedLandRecord a, ReclaimedLandRecord b)
    {
        if (a == null || b == null) return a == b;
        return a.Id == b.Id && a.Version == b.Version && a.TopHeight == b.TopHeight &&
               SamePoints(a.Ring, b.Ring) && SameSegments(a.Quays, b.Quays) && SameSegments(a.Walls, b.Walls) &&
               SameBand(a.Band, b.Band);
    }

    private static bool SameContent(IReadOnlyList<ReclaimedLandRecord> a, IReadOnlyList<ReclaimedLandRecord> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!SameContent(a[i], b[i])) return false;
        }
        return true;
    }

    private static bool SamePoints(Vector2[] a, Vector2[] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static bool SameSegments(ReclaimedLandSegment[] a, ReclaimedLandSegment[] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].A != b[i].A || a[i].B != b[i].B || a[i].Normal != b[i].Normal) return false;
        }
        return true;
    }

    private static bool SameBand(Vector2[][][] a, Vector2[][][] b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int p = 0; p < a.Length; p++)
        {
            if (a[p] == null || b[p] == null)
            {
                if (a[p] != b[p]) return false;
                continue;
            }
            if (a[p].Length != b[p].Length) return false;
            for (int r = 0; r < a[p].Length; r++)
            {
                if (!SamePoints(a[p][r], b[p][r])) return false;
            }
        }
        return true;
    }

    private static void RebuildBandBounds()
    {
        hasBand = false;
        bandMinX = bandMinZ = float.PositiveInfinity;
        bandMaxX = bandMaxZ = float.NegativeInfinity;
        foreach (var land in lands)
        {
            if (!land.TopHeight.HasValue || land.Band == null) continue;
            foreach (Rect rect in land.BandBounds)
            {
                hasBand = true;
                bandMinX = Mathf.Min(bandMinX, rect.xMin);
                bandMinZ = Mathf.Min(bandMinZ, rect.yMin);
                bandMaxX = Mathf.Max(bandMaxX, rect.xMax);
                bandMaxZ = Mathf.Max(bandMaxZ, rect.yMax);
            }
        }
    }

    // Rect.Contains は上端を含まないので、境界上の点も内側と数える比較を使う。
    private static bool Contains(Rect rect, float x, float z) =>
        x >= rect.xMin && x <= rect.xMax && z >= rect.yMin && z <= rect.yMax;

    private static bool Overlaps(Rect a, Rect b) =>
        a.xMin <= b.xMax && a.xMax >= b.xMin && a.yMin <= b.yMax && a.yMax >= b.yMin;
}
