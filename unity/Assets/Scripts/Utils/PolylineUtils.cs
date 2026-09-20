using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 線分の最近接点・距離と、ポリライン(点列)の角丸めを扱う幾何ユーティリティ。
/// </summary>
public static class PolylineUtils
{
    // 長さ0.0001座標単位以下は始点として扱う。ワールドでは0.1mm、画面では0.0001px。
    private const float DEGENERATE_LENGTH_SQ = 1e-8f;

    private static float ProjectionParameter(float dot, float lengthSq)
    {
        return lengthSq <= DEGENERATE_LENGTH_SQ ? 0f : Mathf.Clamp01(dot / lengthSq);
    }

    /// <summary>同じ2D座標系の点と線分を渡す。スクリーン座標の場合は単位がピクセルになる。</summary>
    public static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 delta = end - start;
        float t = ProjectionParameter(Vector2.Dot(point - start, delta), delta.sqrMagnitude);
        return start + delta * t;
    }

    /// <summary>高さも含む3D距離で射影する。地形上のカーソルの水平吸着にはXZ版を使う。</summary>
    public static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 delta = end - start;
        float t = ProjectionParameter(Vector3.Dot(point - start, delta), delta.sqrMagnitude);
        return start + delta * t;
    }

    /// <summary>XZだけで射影位置を決め、Yは両端から補間する。XZが縮退した場合は始点を返す。</summary>
    public static Vector3 ClosestPointOnSegmentXZ(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector2 delta = new Vector2(end.x - start.x, end.z - start.z);
        Vector2 offset = new Vector2(point.x - start.x, point.z - start.z);
        float t = ProjectionParameter(Vector2.Dot(offset, delta), delta.sqrMagnitude);
        return Vector3.Lerp(start, end, t);
    }

    public static float DistanceToSegmentSq(Vector2 point, Vector2 start, Vector2 end)
    {
        return (point - ClosestPointOnSegment(point, start, end)).sqrMagnitude;
    }

    // これ以下の曲がり角は「ほぼ直線」とみなして丸めず素通しする(度)。
    private const float STRAIGHT_ANGLE_DEG = 4f;

    /// <summary>
    /// ポリラインの曲がり角だけを丸める。
    /// 実道路ジオメトリ(既にサンプル済みの点列)はそのまま活かし、方向が変わる頂点のみ
    /// 両隣セグメント長の半分でクランプした距離 radius でチャンファし、その区間を
    /// 二次ベジエ(制御点=元頂点)で補間して滑らかにつなぐ。
    /// segsPerCorner は 1 角あたりの補間分割数。
    /// </summary>
    public static List<Vector3> RoundCorners(List<Vector3> pts, float radius, int segsPerCorner)
    {
        if (pts == null || pts.Count < 3 || radius <= 0f)
            return pts;
        if (segsPerCorner < 1) segsPerCorner = 1;

        var result = new List<Vector3>(pts.Count + pts.Count / 2);
        result.Add(pts[0]);

        for (int i = 1; i < pts.Count - 1; i++)
        {
            Vector3 prev = pts[i - 1];
            Vector3 cur = pts[i];
            Vector3 next = pts[i + 1];

            Vector3 vIn = cur - prev;
            Vector3 vOut = next - cur;
            float lenIn = vIn.magnitude;
            float lenOut = vOut.magnitude;
            if (lenIn < 1e-4f || lenOut < 1e-4f)
            {
                // 退化(重複点)。そのまま通す。
                result.Add(cur);
                continue;
            }

            Vector3 dirIn = vIn / lenIn;
            Vector3 dirOut = vOut / lenOut;
            float angle = Vector3.Angle(dirIn, dirOut);
            if (angle < STRAIGHT_ANGLE_DEG)
            {
                // ほぼ直線なので丸めない。
                result.Add(cur);
                continue;
            }

            // チャンファ距離: radius を両隣セグメント長の半分でクランプ(角が潰れない/重ならない)。
            float trim = Mathf.Min(radius, lenIn * 0.5f, lenOut * 0.5f);
            Vector3 pA = cur - dirIn * trim;   // 入口側の接点
            Vector3 pB = cur + dirOut * trim;  // 出口側の接点

            // pA→(制御点 cur)→pB の二次ベジエで丸める。両セグメントに接する。
            result.Add(pA);
            for (int s = 1; s < segsPerCorner; s++)
            {
                float t = (float)s / segsPerCorner;
                float u = 1f - t;
                Vector3 b = u * u * pA + 2f * u * t * cur + t * t * pB;
                result.Add(b);
            }
            result.Add(pB);
        }

        result.Add(pts[pts.Count - 1]);
        return result;
    }
}
