using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 湖など海面以外の水面1枚分の判定用データ(GET /terrain/water の1要素)。
/// 水面は footprint(polygon)全体に水位 Level の水平面として描かれるが、footprint の内側でも
/// 水位より高い地形は陸地であり、実際に水があるのは「footprint 内で地形が水位を下回る所」である
/// (バックエンドはこの範囲を海岸線ポリゴンの穴にする。D_地形形状.md §D.7)。
/// </summary>
public class WaterSurfaceRecord
{
    public string Id;
    public float Level;
    /// <summary>全パートの外周と穴。内外判定は全リングの even-odd。</summary>
    public readonly List<Vector2[]> Rings = new List<Vector2[]>();
    public float MinX = float.PositiveInfinity, MinZ = float.PositiveInfinity;
    public float MaxX = float.NegativeInfinity, MaxZ = float.NegativeInfinity;

    public void AddRing(List<Vector2> ring)
    {
        if (ring == null) return;
        var pts = new List<Vector2>(ring);
        // GeoJSON の閉じ点(始点=終点)は除く
        if (pts.Count >= 2 && (pts[0] - pts[pts.Count - 1]).sqrMagnitude < 1e-6f) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return;
        foreach (var p in pts)
        {
            MinX = Mathf.Min(MinX, p.x); MaxX = Mathf.Max(MaxX, p.x);
            MinZ = Mathf.Min(MinZ, p.y); MaxZ = Mathf.Max(MaxZ, p.y);
        }
        Rings.Add(pts.ToArray());
    }

    /// <summary>(x, z) が footprint 内か(外接矩形で前置判定してから全リングの even-odd)。</summary>
    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
        var point = new Vector2(x, z);
        bool inside = false;
        foreach (var ring in Rings)
        {
            if (PolygonUtils.Contains(ring, point)) inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// 湖の水面の静的ストア。WaterSurfaceManager がセッション初期化時に GET /terrain/water から構築し、
/// 敷設中パスの海・湖の横断判定(RoadPlanManager)が参照する。海岸線の CoastlineStore、
/// 川の RiverStore と同じ位置付け。
/// </summary>
public static class WaterSurfaceStore
{
    private static readonly List<WaterSurfaceRecord> surfaces = new List<WaterSurfaceRecord>();

    public static IReadOnlyList<WaterSurfaceRecord> Surfaces => surfaces;

    public static void Clear()
    {
        surfaces.Clear();
    }

    public static void Add(WaterSurfaceRecord record)
    {
        if (record != null && record.Rings.Count > 0) surfaces.Add(record);
    }

    /// <summary>
    /// (x, z) を footprint に含む水面を返す(無ければ null)。footprint が重なる場合は水位の高い方を返す
    /// (バックエンド geo_sea_lake の水面標高の決め方と同じ)。
    /// </summary>
    public static WaterSurfaceRecord FindAt(float x, float z)
    {
        WaterSurfaceRecord found = null;
        foreach (var rec in surfaces)
        {
            if (rec.Contains(x, z) && (found == null || rec.Level > found.Level)) found = rec;
        }
        return found;
    }
}
