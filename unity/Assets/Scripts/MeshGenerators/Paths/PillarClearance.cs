using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>道路・線路の通行空間に入る橋脚を、描画順に依存せず省くための幾何判定。</summary>
public static class PillarClearance
{
    public sealed class Corridor
    {
        public List<Vector3> Points;
        public float HalfWidth;
        public float Height;
    }

    public static Func<PathSummary, List<Corridor>> NeighborProvider;
    public const float Margin = 0.25f;

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
            float radius = partRadius + corridor.HalfWidth + Margin;
            for (int i = 1; i < corridor.Points.Count; i++)
            {
                Vector3 a = corridor.Points[i - 1], b = corridor.Points[i];
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
