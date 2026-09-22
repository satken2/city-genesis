using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 2D多角形(閉じたリング。終点に始点を繰り返さない頂点列)の幾何ユーティリティ。
/// Vector2 は x=ワールドX, y=ワールドZ の水平射影として扱う。
/// 内外判定はレイキャスティング(even-odd)で、非凸リングでも偶奇則で一貫した結果になる。
/// 線分に対する計算は <see cref="PolylineUtils"/> にある。
/// </summary>
public static class PolygonUtils
{
    /// <summary>
    /// 点が多角形の内部にあるか。境界上の点はどちらに倒れるか未定義なので、
    /// 境界を含めたい場合は boundaryTolerance 付きのオーバーロードを使う。
    /// </summary>
    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 vi = polygon[i];
            Vector2 vj = polygon[j];
            if (CrossesRay(vi.x, vi.y, vj.x, vj.y, point.x, point.y)) inside = !inside;
        }
        return inside;
    }

    /// <summary>境界から boundaryTolerance 以内の点も内部として扱う(辺上に置いた点を確実に含めたいレイアウト用)。</summary>
    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point, float boundaryTolerance)
    {
        float toleranceSq = boundaryTolerance * boundaryTolerance;
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 vi = polygon[i];
            Vector2 vj = polygon[j];
            if (PolylineUtils.DistanceToSegmentSq(point, vj, vi) < toleranceSq) return true;
            if (CrossesRay(vi.x, vi.y, vj.x, vj.y, point.x, point.y)) inside = !inside;
        }
        return inside;
    }

    /// <summary>頂点が [i][0]=X, [i][1]=Z の配列(区画・地区のポリゴン応答をそのまま保持する形式)向け。</summary>
    public static bool Contains(float[][] polygon, float x, float z)
    {
        bool inside = false;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (CrossesRay(polygon[i][0], polygon[i][1], polygon[j][0], polygon[j][1], x, z)) inside = !inside;
        }
        return inside;
    }

    /// <summary>頂点が List&lt;List&lt;float&gt;&gt; 形式(資源ポリゴン応答)向け。</summary>
    public static bool Contains(List<List<float>> polygon, float x, float z)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (CrossesRay(polygon[i][0], polygon[i][1], polygon[j][0], polygon[j][1], x, z)) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 点(x, z)から+X方向へ伸ばした半直線が辺 i-j と交わるか。
    /// 高さ比較を片側開区間にすることで、半直線が頂点をちょうど通る場合の二重カウントを避ける。
    /// </summary>
    private static bool CrossesRay(float xi, float zi, float xj, float zj, float x, float z)
    {
        return ((zi > z) != (zj > z)) && (x < (xj - xi) * (z - zi) / (zj - zi) + xi);
    }

    /// <summary>符号付き面積(靴紐公式)。正=反時計回り(CCW)。巻き順の正規化判定に使う。</summary>
    public static float SignedArea(IReadOnlyList<Vector2> polygon)
    {
        float area = 0f;
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % n];
            area += a.x * b.y - b.x * a.y;
        }
        return area * 0.5f;
    }

    /// <summary>点から多角形の境界(全辺)までの最短距離。内外は問わない(符号なし)。</summary>
    public static float DistanceToBoundary(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        float minDistanceSq = float.MaxValue;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float distanceSq = PolylineUtils.DistanceToSegmentSq(point, polygon[j], polygon[i]);
            if (distanceSq < minDistanceSq) minDistanceSq = distanceSq;
        }
        return Mathf.Sqrt(minDistanceSq);
    }
}
