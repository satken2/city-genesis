using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 線分の最近接点・距離、三次ベジエの評価、ポリライン(点列)の進行方向と角丸めを扱う幾何ユーティリティ。
/// 多角形(閉じたリング)に対する計算は <see cref="PolygonUtils"/> にある。
/// </summary>
public static class PolylineUtils
{
    // 長さ0.0001座標単位以下は始点として扱う。ワールドでは0.1mm、画面では0.0001px。
    private const float DEGENERATE_LENGTH_SQ = 1e-8f;

    private static float ProjectionParameter(float dot, float lengthSq)
    {
        return lengthSq <= DEGENERATE_LENGTH_SQ ? 0f : Mathf.Clamp01(dot / lengthSq);
    }

    /// <summary>線分上の最近接点の位置パラメータ(0=始点、1=終点)。辺上の当たり位置を保持したいヒット判定などで使う。</summary>
    public static float ProjectOnSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 delta = end - start;
        return ProjectionParameter(Vector2.Dot(point - start, delta), delta.sqrMagnitude);
    }

    /// <summary>XZ射影での位置パラメータ(0=始点、1=終点)。高さ差を無視して道路中心線上の進行距離を求めるときに使う。</summary>
    public static float ProjectOnSegmentXZ(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector2 delta = new Vector2(end.x - start.x, end.z - start.z);
        Vector2 offset = new Vector2(point.x - start.x, point.z - start.z);
        return ProjectionParameter(Vector2.Dot(offset, delta), delta.sqrMagnitude);
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

    public static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        return Mathf.Sqrt(DistanceToSegmentSq(point, start, end));
    }

    /// <summary>
    /// 三次ベジエ曲線のパラメータ t における点。
    /// B(t) = (1-t)^3 P0 + 3(1-t)^2 t P1 + 3(1-t) t^2 P2 + t^3 P3
    /// 道路プレビュー(RoadPlanManager)と実道路(PathManager)が同じ式を共有し、曲線形状の食い違いを防ぐ。
    /// </summary>
    public static Vector3 CubicBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        float u2 = u * u;
        float t2 = t * t;
        return u2 * u * p0 + 3f * u2 * t * p1 + 3f * u * t2 * p2 + t2 * t * p3;
    }

    public static Vector2 CubicBezierPoint(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        float u2 = u * u;
        float t2 = t * t;
        return u2 * u * p0 + 3f * u2 * t * p1 + 3f * u * t2 * p2 + t2 * t * p3;
    }

    /// <summary>
    /// 点列の index 番目における進行方向(前後の辺の単位ベクトルの平均)。帯メッシュの断面を
    /// 頂点で折れ線状に捻らず滑らかに配置するために使う。端点は片側の辺だけ、前後が打ち消し合う
    /// 折り返し(180°)は前方の辺を優先し、それも縮退していれば Vector3.forward を返す。
    /// </summary>
    public static Vector3 SmoothedDirection(IReadOnlyList<Vector3> points, int index)
    {
        Vector3 forward = index < points.Count - 1 ? points[index + 1] - points[index] : Vector3.zero;
        Vector3 backward = index > 0 ? points[index] - points[index - 1] : Vector3.zero;
        bool hasForward = forward.sqrMagnitude >= 1e-6f;
        bool hasBackward = backward.sqrMagnitude >= 1e-6f;
        if (!hasForward && !hasBackward) return Vector3.forward;

        Vector3 aggregate = Vector3.zero;
        if (hasForward) aggregate += forward.normalized;
        if (hasBackward) aggregate += backward.normalized;
        if (aggregate.sqrMagnitude < 1e-6f)
        {
            return hasForward ? forward.normalized : backward.normalized;
        }
        return aggregate.normalized;
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
