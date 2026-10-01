using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>道路・線路の通行空間に入る橋脚を、描画順に依存せず省くための幾何判定。</summary>
public static class PillarClearance
{
    /// <summary>
    /// 通行空間。Points は PathManager の点列キャッシュと共有することがあるので読み取り専用として扱い、
    /// 高さの持ち上げは YOffset で表す(判定時に各点のYへ加える)。
    /// </summary>
    public sealed class Corridor
    {
        public List<Vector3> Points;
        public float HalfWidth;
        public float Height;
        public float YOffset;

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

    /// <summary>
    /// 橋脚の横断面で通行空間の左右端を求め、左右それぞれの柱を最小限だけ外へ逃がす。
    /// 柱の外接円で通行帯を広げ、線分を高さと前後方向で切ってから横方向へ投影する保守的な近似。
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
            float radius = legRadius + corridor.HalfWidth + Margin;
            for (int i = 1; i < corridor.Points.Count; i++)
            {
                Vector3 a = corridor.Points[i - 1] + Vector3.up * corridor.YOffset;
                Vector3 b = corridor.Points[i] + Vector3.up * corridor.YOffset;
                float lo = 0f, hi = 1f;
                if (!ClipRange(a.y, b.y, minY - corridor.Height, maxY, ref lo, ref hi)) continue;
                if (!ClipRange(Vector3.Dot(a - origin, forward), Vector3.Dot(b - origin, forward),
                        -radius, radius, ref lo, ref hi)) continue;
                float x0 = Vector3.Dot(Vector3.Lerp(a, b, lo) - origin, right);
                float x1 = Vector3.Dot(Vector3.Lerp(a, b, hi) - origin, right);
                intervals.Add(new Vector2(Mathf.Min(x0, x1) - radius, Mathf.Max(x0, x1) + radius));
            }
        }
        // 重なる通行帯を順に抜ける。離れた道路までまとめてまたぐ必要はない。
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
    /// 柱・キャップをそれぞれ外接円と高さ区間で近似する。
    /// 道路の線分上で水平の重複区間と高さの重複区間を交差させ、斜路も判定する。
    /// </summary>
    public static bool Intersects(Bounds part, List<Corridor> corridors)
    {
        if (corridors == null) return false;
        float partRadius = new Vector2(part.extents.x, part.extents.z).magnitude;
        foreach (var corridor in corridors)
        {
            if (corridor.Points == null || corridor.Points.Count < 2) continue;
            float radius = partRadius + corridor.HalfWidth + Margin;
            if (corridor.IsOutsideXZ(part.center, radius)) continue;
            Vector3 lift = new Vector3(0f, corridor.YOffset, 0f);
            for (int i = 1; i < corridor.Points.Count; i++)
            {
                Vector3 a = corridor.Points[i - 1] + lift, b = corridor.Points[i] + lift;
                double lo = 0, hi = 1;
                double dy = b.y - a.y;
                double minY = part.min.y - corridor.Height, maxY = part.max.y;
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
                double px = a.x - part.center.x, pz = a.z - part.center.z;
                double lengthSquared = dx * dx + dz * dz;
                double t = lengthSquared > 1e-8 ? -(px * dx + pz * dz) / lengthSquared : lo;
                t = Math.Max(lo, Math.Min(hi, t));
                double x = px + dx * t, z = pz + dz * t;
                if (x * x + z * z <= radius * radius) return true;
            }
        }
        return false;
    }
}
