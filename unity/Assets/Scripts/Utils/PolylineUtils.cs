using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ポリライン(点列)に対する幾何ユーティリティ。
/// 公共交通オーバーレイが下地パスを連結した経路の角を丸めるために使う。
/// </summary>
public static class PolylineUtils
{
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
