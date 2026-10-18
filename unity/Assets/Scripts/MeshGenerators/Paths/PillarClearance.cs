using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>他の道路・線路と干渉する橋脚・沿道設備を、描画順に依存せず省くための幾何判定。</summary>
public static class PillarClearance
{
    /// <summary>
    /// 通行空間。Points は PathManager の点列キャッシュと共有することがあるので読み取り専用として扱い、
    /// 高さの持ち上げは YOffset で表す(判定時に各点のYへ加える)。
    /// 橋脚の足元は、中心線から MedianHalfWidth までの中央分離帯と、両端から SidewalkWidth までの歩道に置ける(既定の0は全幅が車道)。
    /// </summary>
    public sealed class Corridor
    {
        public List<Vector3> Points;
        public float HalfWidth;
        public float Height;
        public float YOffset;
        public float MedianHalfWidth;
        public float SidewalkWidth;

        /// <summary>柱を置けない帯の数。0番は全幅の床版、1番以降は車道(中央分離帯があれば左右の2本)。</summary>
        internal int FootBandCount
        {
            get
            {
                float inner = Mathf.Max(0f, MedianHalfWidth), outer = HalfWidth - Mathf.Max(0f, SidewalkWidth);
                return outer <= inner ? 1 : inner > 0f ? 3 : 2;
            }
        }

        /// <summary>
        /// 帯の中心線からの横ずれ・半幅と、路面から上で柱を妨げる高さ。
        /// 床版の高さは -FootTolerance で、路面が足元より FootTolerance 以上高い(柱が貫く)ときだけ妨げる。
        /// </summary>
        internal void GetFootBand(int index, out float offset, out float halfWidth, out float height)
        {
            offset = 0f;
            if (index == 0) { halfWidth = HalfWidth; height = -FootTolerance; return; }
            float inner = Mathf.Max(0f, MedianHalfWidth), outer = HalfWidth - Mathf.Max(0f, SidewalkWidth);
            height = Height;
            if (inner <= 0f) { halfWidth = outer; return; }
            halfWidth = (outer - inner) * 0.5f;
            offset = (index == 1 ? 1f : -1f) * (inner + halfWidth);
        }

        // テストは Points と HalfWidth を外から差し替えるので、XZ範囲は Points の参照と点数で検証し、HalfWidth は判定時に毎回使う。
        private List<Vector3> boundsSource;
        private int boundsCount = -1;
        private float minX, maxX, minZ, maxZ;

        /// <summary>中心点が点列のXZ範囲から radius より外にあれば真(どの線分とも交差し得ない)。</summary>
        internal bool IsOutsideXZ(Vector3 center, float radius)
        {
            if (!ReferenceEquals(boundsSource, Points) || boundsCount != Points.Count)
            {
                boundsSource = Points;
                boundsCount = Points.Count;
                minX = minZ = float.PositiveInfinity;
                maxX = maxZ = float.NegativeInfinity;
                for (int i = 0; i < Points.Count; i++)
                {
                    Vector3 p = Points[i];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
            }
            // 距離判定の float/double 丸め差で交差を取りこぼさないよう、わずかに広げて棄却する。
            double r = radius + 1e-3;
            return center.x < minX - r || center.x > maxX + r || center.z < minZ - r || center.z > maxZ + r;
        }
    }

    public static Func<PathSummary, List<Corridor>> NeighborProvider;
    public const float Margin = 0.25f;
    // 地表の道路の路面(地形+0.2m)と歩道の段差を、柱が立つ地面とみなす高さ。
    public const float FootTolerance = 0.5f;

    /// <summary>
    /// 柱を置けない帯(車道・線路と床版)を橋脚の横断方向へ投影し、左右それぞれの柱を最小限だけ外へ逃がす。
    /// 歩道・中央分離帯に柱が収まれば、そこで止まる。
    /// 柱の外接円で帯を広げ、線分を高さと前後方向で切ってから横方向へ投影する保守的な近似。
    /// </summary>
    public static void FindPortalOffsets(Vector3 origin, Vector3 right, float minY, float maxY,
        float legRadius, float minimumHalfSpan, List<Corridor> corridors, out float left, out float rightOffset)
    {
        left = -minimumHalfSpan;
        rightOffset = minimumHalfSpan;
        if (corridors == null) return;
        var intervals = new List<Vector2>();
        Vector3 forward = Vector3.Cross(right, Vector3.up);
        foreach (var corridor in corridors)
        {
            if (corridor.Points == null) continue;
            for (int band = 0; band < corridor.FootBandCount; band++)
            {
                corridor.GetFootBand(band, out float offset, out float halfWidth, out float height);
                float radius = legRadius + halfWidth + Margin;
                for (int i = 1; i < corridor.Points.Count; i++)
                {
                    Vector3 a = corridor.Points[i - 1] + Vector3.up * corridor.YOffset;
                    Vector3 b = corridor.Points[i] + Vector3.up * corridor.YOffset;
                    if (offset != 0f)
                    {
                        Vector3 shift = RightOf(a, b) * offset;
                        a += shift;
                        b += shift;
                    }
                    float lo = 0f, hi = 1f;
                    if (!ClipRange(a.y, b.y, minY - height, maxY, ref lo, ref hi)) continue;
                    if (!ClipRange(Vector3.Dot(a - origin, forward), Vector3.Dot(b - origin, forward),
                            -radius, radius, ref lo, ref hi)) continue;
                    float x0 = Vector3.Dot(Vector3.Lerp(a, b, lo) - origin, right);
                    float x1 = Vector3.Dot(Vector3.Lerp(a, b, hi) - origin, right);
                    intervals.Add(new Vector2(Mathf.Min(x0, x1) - radius, Mathf.Max(x0, x1) + radius));
                }
            }
        }
        // 重なる帯を順に抜ける。離れた道路までまとめてまたぐ必要はない。
        intervals.Sort((a, b) => a.x.CompareTo(b.x));
        for (int i = 0; i < intervals.Count; i++)
            if (rightOffset >= intervals[i].x && rightOffset <= intervals[i].y)
                rightOffset = intervals[i].y + 0.01f;
        intervals.Sort((a, b) => b.y.CompareTo(a.y));
        for (int i = 0; i < intervals.Count; i++)
            if (left >= intervals[i].x && left <= intervals[i].y)
                left = intervals[i].x - 0.01f;
    }

    private static bool ClipRange(float a, float b, float min, float max, ref float lo, ref float hi)
    {
        float delta = b - a;
        if (Mathf.Abs(delta) < 1e-6f) return a >= min && a <= max;
        float t0 = (min - a) / delta, t1 = (max - a) / delta;
        lo = Mathf.Max(lo, Mathf.Min(t0, t1));
        hi = Mathf.Min(hi, Mathf.Max(t0, t1));
        return lo <= hi;
    }

    /// <summary>線分 a→b の平面での右向き単位ベクトル。長さ0の線分では0。</summary>
    private static Vector3 RightOf(Vector3 a, Vector3 b) => new Vector3(b.z - a.z, 0f, a.x - b.x).normalized;

    /// <summary>子メッシュの変換を含む、プレハブルート座標での範囲。ルートのscaleは配置時に適用する。</summary>
    public static Bounds? GetPrefabBounds(GameObject prefab)
    {
        if (prefab == null) return null;
        Bounds? result = null;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;
            var matrix = prefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var bounds = Utils.TransformBounds(filter.sharedMesh.bounds, matrix);
            if (result.HasValue)
            {
                var combined = result.Value;
                combined.Encapsulate(bounds);
                result = combined;
            }
            else result = bounds;
        }
        return result;
    }

    /// <summary>
    /// 地面に接する部材(下端=足元)を外接円と高さ区間で近似し、柱を置けない帯に掛かるかを調べる。
    /// 帯は車道・線路の通行空間と、足元より高い路面(床版)の全幅。地上の歩道・中央分離帯には掛からない。
    /// 道路の線分上で水平の重複区間と高さの重複区間を交差させ、斜路も判定する。
    /// </summary>
    public static bool Intersects(Bounds part, List<Corridor> corridors)
    {
        if (corridors == null) return false;
        float partRadius = new Vector2(part.extents.x, part.extents.z).magnitude;
        foreach (var corridor in corridors)
            for (int band = 0; band < corridor.FootBandCount; band++)
            {
                corridor.GetFootBand(band, out float offset, out float halfWidth, out float height);
                if (Reaches(corridor, part.center, partRadius, part.min.y - height, part.max.y, offset, halfWidth)) return true;
            }
        return false;
    }

    /// <summary>
    /// 足元 foot から rise 以上高い路面が、平面で foot から radius(設備の水平半径)+半幅以内を通るか。
    /// 上を通る相手の下の設備を、相手の高さに関わらず省くために使う。
    /// </summary>
    public static bool PassesAbove(Vector3 foot, float radius, float rise, List<Corridor> corridors)
    {
        if (corridors == null) return false;
        foreach (var corridor in corridors)
            if (Reaches(corridor, foot, radius, foot.y + rise, double.PositiveInfinity, 0f, corridor.HalfWidth)) return true;
        return false;
    }

    /// <summary>平面で points のいずれかの点から radius+半幅以内を通るか(高さは問わない)。points は未切断の中心線のように細かく標本化した点列とする。</summary>
    public static bool NearXZ(IReadOnlyList<Vector3> points, float radius, Corridor corridor)
    {
        if (points == null) return false;
        for (int i = 0; i < points.Count; i++)
            if (Reaches(corridor, points[i], radius, double.NegativeInfinity, double.PositiveInfinity, 0f, corridor.HalfWidth)) return true;
        return false;
    }

    /// <summary>中心線を右へ offset ずらした線のうち高さが [minY, maxY] の部分が、平面で center から radius+halfWidth 以内を通るか。</summary>
    private static bool Reaches(Corridor corridor, Vector3 center, float radius, double minY, double maxY, float offset, float halfWidth)
    {
        if (corridor.Points == null || corridor.Points.Count < 2) return false;
        float reach = radius + halfWidth + Margin;
        if (corridor.IsOutsideXZ(center, reach + Mathf.Abs(offset))) return false;
        Vector3 lift = new Vector3(0f, corridor.YOffset, 0f);
        for (int i = 1; i < corridor.Points.Count; i++)
        {
            Vector3 a = corridor.Points[i - 1] + lift, b = corridor.Points[i] + lift;
            if (offset != 0f)
            {
                Vector3 shift = RightOf(a, b) * offset;
                a += shift;
                b += shift;
            }
            double lo = 0, hi = 1;
            double dy = b.y - a.y;
            if (Math.Abs(dy) < 1e-8)
            {
                if (a.y < minY || a.y > maxY) continue;
            }
            else
            {
                double t0 = (minY - a.y) / dy, t1 = (maxY - a.y) / dy;
                lo = Math.Max(lo, Math.Min(t0, t1));
                hi = Math.Min(hi, Math.Max(t0, t1));
                if (lo > hi) continue;
            }
            double dx = b.x - a.x, dz = b.z - a.z;
            double px = a.x - center.x, pz = a.z - center.z;
            double lengthSquared = dx * dx + dz * dz;
            double t = lengthSquared > 1e-8 ? -(px * dx + pz * dz) / lengthSquared : lo;
            t = Math.Max(lo, Math.Min(hi, t));
            double x = px + dx * t, z = pz + dz * t;
            if (x * x + z * z <= reach * reach) return true;
        }
        return false;
    }
}
