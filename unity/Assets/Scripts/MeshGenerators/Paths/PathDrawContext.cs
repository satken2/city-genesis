using System.Collections.Generic;
using UnityEngine;
using Dreamteck.Splines;

/// <summary>
/// パスのメッシュ生成1回分の描画設定(描画LOD・詳細描画区間・曲率適応タイル長)。
/// 生成は同期的にメインスレッドで行われるため、呼び出し側(PathManager)が生成の前後で
/// <see cref="Begin"/> / <see cref="End"/> を呼び、生成器は静的な現在値を参照する。
/// パーツ配置の入口は RoadPath.CreateSplineMesh に集約されており、そこへ引数を通すより
/// 全生成器(道路・線路・駅舎・交差点コネクタ)に一括で効かせられる。
/// セルのティア表を参照するわけではないので「生成器側でティア表を見ない」規約とは矛盾しない。
/// </summary>
public static class PathDrawContext
{
    /// <summary>現在生成中のパスの描画LOD。既定 Normal(コンテキスト外の生成は従来どおり)。</summary>
    public static DrawLod CurrentDrawLod { get; private set; } = DrawLod.Normal;

    /// <summary>
    /// 詳細パーツを敷く区間(スプライン割合)。区間外は呼び出し側が遠景の帯で埋める。
    /// 全長なら 0〜1。折れ角付き道路のようにセグメントごとに別スプラインを持つ生成では、
    /// 割合の基準が一致しないため <see cref="SuppressDetailRange"/> で無効化する。
    /// </summary>
    public static double DetailFrom { get; private set; } = 0.0;
    public static double DetailTo { get; private set; } = 1.0;
    public static bool SuppressDetailRange { get; set; }

    /// <summary>
    /// 現在のスプラインに対する曲率適応のタイル長(m)。0以下なら未計算で、従来の上限(1m)を使う。
    /// 生成器が BuildSpline 直後に <see cref="SetAdaptiveTileLength"/> で設定する。
    /// </summary>
    public static float AdaptiveTileLength { get; private set; }

    public static bool HasDetailRange => !SuppressDetailRange && (DetailFrom > 0.0001 || DetailTo < 0.9999);

    /// <summary>接写用パーツ名のサフィックス(例: Models/Paths/p411/body_close)。</summary>
    public const string CLOSE_SUFFIX = "_close";

    /// <summary>
    /// 曲率適応タイルの弦誤差の許容(m)。タイルはスプライン上の両端を結ぶ弦になるため、
    /// 曲率κ・タイル長Lの弦誤差 ≈ κL²/8 がこれ以下になるLを選ぶ。
    /// </summary>
    private const float CHORD_ERROR_TOLERANCE = 0.02f;
    /// <summary>曲率適応タイルの上限(m)。直線でも無限に長くはせず、地形の微小起伏の取りこぼしを抑える。</summary>
    private const float ADAPTIVE_TILE_MAX = 8f;
    /// <summary>曲率推定のサンプル間隔(m)と最大サンプル数。</summary>
    private const float CURVATURE_SAMPLE_STEP = 1f;
    private const int CURVATURE_MAX_SAMPLES = 1024;

    public static void Begin(DrawLod drawLod, double detailFrom = 0.0, double detailTo = 1.0)
    {
        CurrentDrawLod = drawLod;
        DetailFrom = System.Math.Clamp(detailFrom, 0.0, 1.0);
        DetailTo = System.Math.Clamp(detailTo, DetailFrom, 1.0);
        SuppressDetailRange = false;
        AdaptiveTileLength = 0f;
    }

    public static void End()
    {
        CurrentDrawLod = DrawLod.Normal;
        DetailFrom = 0.0;
        DetailTo = 1.0;
        SuppressDetailRange = false;
        AdaptiveTileLength = 0f;
    }

    public static void SetAdaptiveTileLength(float length)
    {
        AdaptiveTileLength = length;
    }

    /// <summary>
    /// 呼び出し側のクリップ区間と詳細描画区間の共通部分を返す。共通部分が無ければ false。
    /// </summary>
    public static bool IntersectDetailRange(double clipFrom, double clipTo, out double from, out double to)
    {
        from = clipFrom;
        to = clipTo;
        if (!HasDetailRange) return clipTo > clipFrom;
        from = System.Math.Max(clipFrom, DetailFrom);
        to = System.Math.Min(clipTo, DetailTo);
        return to - from > 1e-6;
    }

    /// <summary>
    /// 接写LODなら `{prefab}_close` を優先して解決し、無ければ無印を返す。
    /// 無印すら無ければ null。Close のパーツは bbox・原点が無印と一致する規約なので差し替えても配置は変わらない。
    /// </summary>
    public static GameObject ResolvePrefab(string prefabName, string fallbackPrefabName, out string resolvedName)
    {
        resolvedName = prefabName;
        if (ModelManager.Instance == null || string.IsNullOrEmpty(prefabName)) return null;

        if (CurrentDrawLod == DrawLod.Close)
        {
            GameObject close = ModelManager.Instance.GetModel(prefabName + CLOSE_SUFFIX, true);
            if (close != null)
            {
                resolvedName = prefabName + CLOSE_SUFFIX;
                return close;
            }
        }
        GameObject prefab = ModelManager.Instance.GetModel(prefabName);
        if (prefab != null) return prefab;

        if (!string.IsNullOrEmpty(fallbackPrefabName))
        {
            if (CurrentDrawLod == DrawLod.Close)
            {
                GameObject close = ModelManager.Instance.GetModel(fallbackPrefabName + CLOSE_SUFFIX, true);
                if (close != null)
                {
                    resolvedName = fallbackPrefabName + CLOSE_SUFFIX;
                    return close;
                }
            }
            prefab = ModelManager.Instance.GetModel(fallbackPrefabName);
            if (prefab != null)
            {
                resolvedName = fallbackPrefabName;
                return prefab;
            }
        }
        return null;
    }

    /// <summary>
    /// スプラインの最大曲率(水平・縦断とも)から、弦誤差が許容内に収まるタイル長を求める。
    /// 直線かつ平坦なら上限、急カーブや起伏の大きい地形追従では1mまで詰まる。
    /// </summary>
    public static float ComputeAdaptiveTileLength(SplineComputer spline, float minTile)
    {
        if (spline == null) return minTile;
        float length = spline.CalculateLength();
        if (length <= CURVATURE_SAMPLE_STEP * 2f) return minTile;

        int samples = Mathf.Clamp(Mathf.CeilToInt(length / CURVATURE_SAMPLE_STEP), 3, CURVATURE_MAX_SAMPLES);
        float step = length / (samples - 1);
        Vector3 prev = spline.EvaluatePosition(0.0);
        Vector3 cur = spline.EvaluatePosition(1.0 / (samples - 1));
        float maxCurvature = 0f;
        for (int i = 2; i < samples; i++)
        {
            Vector3 next = spline.EvaluatePosition((double)i / (samples - 1));
            Vector3 d1 = cur - prev;
            Vector3 d2 = next - cur;
            float l1 = d1.magnitude, l2 = d2.magnitude;
            if (l1 > 1e-4f && l2 > 1e-4f)
            {
                // 折れ角 ÷ 平均区間長 ≈ 曲率
                float angle = Vector3.Angle(d1, d2) * Mathf.Deg2Rad;
                float curvature = angle / (0.5f * (l1 + l2));
                if (curvature > maxCurvature) maxCurvature = curvature;
            }
            prev = cur;
            cur = next;
        }

        if (maxCurvature < 1e-5f) return ADAPTIVE_TILE_MAX;
        float tile = Mathf.Sqrt(8f * CHORD_ERROR_TOLERANCE / maxCurvature);
        return Mathf.Clamp(tile, minTile, ADAPTIVE_TILE_MAX);
    }

    /// <summary>
    /// 点列の累積距離割合が [from, to] に入る部分点列を返す(区間端は補間して切る)。
    /// 遠景の帯を詳細区間の外側にだけ敷くために使う。
    /// </summary>
    public static List<Vector3> SlicePolylineByFraction(List<Vector3> points, double from, double to)
    {
        var result = new List<Vector3>();
        if (points == null || points.Count < 2 || to <= from) return result;
        float total = 0f;
        var cum = new float[points.Count];
        for (int i = 1; i < points.Count; i++)
        {
            total += Vector3.Distance(points[i - 1], points[i]);
            cum[i] = total;
        }
        if (total <= 1e-4f) return result;
        float a = (float)(from * total), b = (float)(to * total);
        void AddPoint(Vector3 point)
        {
            if (result.Count > 0 && (result[result.Count - 1] - point).sqrMagnitude < 1e-8f)
                result[result.Count - 1] = point;
            else result.Add(point);
        }
        for (int i = 0; i < points.Count - 1; i++)
        {
            float s0 = cum[i], s1 = cum[i + 1];
            float segLen = s1 - s0;
            // 境界と一致した頂点を隣接セグメントから二重追加すると、末端の接線がゼロになる。
            // SplineMeshの断面がそこで90度回り、施工先端から路面が突き出すため除外する。
            if (s1 <= a || s0 >= b || segLen <= 1e-6f) continue;
            if (result.Count == 0)
            {
                float t0 = segLen > 1e-6f ? Mathf.Clamp01((a - s0) / segLen) : 0f;
                AddPoint(Vector3.Lerp(points[i], points[i + 1], t0));
            }
            if (s1 <= b)
            {
                AddPoint(points[i + 1]);
                if (s1 >= b) break;
            }
            else
            {
                float t1 = segLen > 1e-6f ? Mathf.Clamp01((b - s0) / segLen) : 1f;
                AddPoint(Vector3.Lerp(points[i], points[i + 1], t1));
                break;
            }
        }
        return result;
    }
}
