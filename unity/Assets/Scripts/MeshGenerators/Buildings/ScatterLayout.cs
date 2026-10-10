using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using UnityEngine;

/// <summary>
/// 散布パーツ（scatter1, 2, …）の置き場を決める純データ。敷地のうち建物の底面の外側の空き地へ、
/// パーツの外接箱どうしが重ならないよう乱数で置いていく。向きは建物の正面の辺（頂点0→1）に揃えた4方向のどれか。
/// 乱数の種は建物ごとに固定なので、再生成しても同じ配置になる。
/// 座標はすべて center_point 基準のローカルXZ。仕様は city-genesis/docs/docs/geometries/F_建物メッシュの生成.md。
/// </summary>
public static class ScatterLayout
{
    /// <summary>空き地の面積のうち、パーツの外接箱で埋める割合。超えた時点で置くのをやめる。</summary>
    public const float FILL_RATIO = 0.25f;

    /// <summary>パーツと、敷地の外周・建物の底面・ほかのパーツとの間に空ける距離(m)。</summary>
    public const float CLEARANCE = 0.5f;

    private const int MAX_ITEMS = 16;      // 1棟に置く数の上限（広い敷地で頂点が膨らむのを防ぐ）
    private const int MAX_ATTEMPTS = 400;  // 置き場所を試す回数の上限

    public struct Item
    {
        /// <summary>パーツの番号（kinds の添字）。</summary>
        public int Kind;
        /// <summary>パーツの原点を置く位置。</summary>
        public Vector2 Position;
        public Quaternion Rotation;
        /// <summary>外接箱の4隅（CCW）。地形への接地に使う。</summary>
        public Vector2[] Corners;
    }

    /// <summary>
    /// 置き場を決める。site は敷地（区画）、footprint は建物の底面（どちらもCCW）、kinds は各パーツの通常モデルの外接箱、
    /// seed は建物ごとの乱数の種。空き地が無いかパーツが無ければ空のリストを返す。
    /// </summary>
    public static List<Item> Plan(Vector2[] site, Vector2[] footprint, IReadOnlyList<Bounds> kinds, int seed)
    {
        var items = new List<Item>();
        if (site == null || site.Length < 3 || footprint == null || footprint.Length < 3 || kinds == null || kinds.Count == 0)
        {
            return items;
        }

        var gf = new GeometryFactory();
        IPreparedGeometry allowed;
        Envelope envelope;
        double budget;
        try
        {
            Geometry siteGeom = PortLayout.ToPolygon(gf, site);
            Geometry body = PortLayout.ToPolygon(gf, footprint);
            Geometry free = (siteGeom.IsValid ? siteGeom : siteGeom.Buffer(0)).Buffer(-CLEARANCE)
                .Difference((body.IsValid ? body : body.Buffer(0)).Buffer(CLEARANCE));
            if (free.IsEmpty)
            {
                return items;
            }
            allowed = PreparedGeometryFactory.Prepare(free);
            envelope = free.EnvelopeInternal;
            budget = free.Area * FILL_RATIO;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[ScatterLayout] 空き地の計算に失敗しました: {ex.Message}。散布パーツを置きません。");
            return items;
        }

        Vector2 front = footprint[1] - footprint[0];
        Quaternion facing = front.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(new Vector3(front.x, 0f, front.y), Vector3.up)
            : Quaternion.identity;
        var random = new System.Random(seed);
        var placed = new List<Polygon>();
        double filled = 0.0;
        for (int attempt = 0; attempt < MAX_ATTEMPTS && filled < budget && items.Count < MAX_ITEMS; attempt++)
        {
            int kind = random.Next(kinds.Count);
            Quaternion rotation = facing * Quaternion.Euler(0f, 90f * random.Next(4), 0f);
            var at = new Vector2(
                (float)(envelope.MinX + random.NextDouble() * envelope.Width),
                (float)(envelope.MinY + random.NextDouble() * envelope.Height));
            Vector2[] corners = PortLayout.RotatedRect(at, rotation, kinds[kind]);
            Polygon box = PortLayout.ToPolygon(gf, corners);
            if (!allowed.Covers(box) || placed.Exists(other => other.IsWithinDistance(box, CLEARANCE)))
            {
                continue;
            }
            placed.Add(box);
            items.Add(new Item { Kind = kind, Position = at, Rotation = rotation, Corners = corners });
            filled += kinds[kind].size.x * kinds[kind].size.z;
        }
        return items;
    }
}
