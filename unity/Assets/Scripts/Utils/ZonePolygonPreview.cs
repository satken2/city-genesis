using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地区編集中のポリゴンプレビューを管理するユーティリティ。
/// 選択された道路の中心点からConvexHullを計算し、
/// OverlayMeshRendererを通じて破線でプレビュー描画を行う。
///
/// 新規地区作成時は選択道路のcenter_pointのみからConvexHullを生成し、
/// 既存地区の拡張時は既存ポリゴンの頂点と追加道路のcenter_pointを統合してConvexHullを生成する。
///
/// このプレビューはバックエンド確定前の概算であり、
/// 確定後はバックエンドが計算した正確なポリゴンで上書きされる。
/// </summary>
public static class ZonePolygonPreview
{
    /// <summary>
    /// プレビュー用の破線色。本番のシアン（zoneLineColor）と区別するため黄色を使用する。
    /// </summary>
    private static readonly Color PreviewColor = new Color(1f, 0.85f, 0f, 0.9f);

    private const float PREVIEW_LINE_THICKNESS = 6f;

    /// <summary>
    /// 既存地区の拡張プレビューポリゴンを計算して描画する。
    /// 既存の地区ポリゴン頂点と、ドラッグで追加された道路のcenter_pointを統合し、
    /// ConvexHullを求めて描画する。
    /// </summary>
    /// <param name="zoneId">拡張元の地区ID</param>
    /// <param name="additionalPathIds">ドラッグで追加された道路IDのリスト</param>
    public static void ShowExpandPreview(int zoneId, IReadOnlyList<int> additionalPathIds)
    {
        var zoneManager = ZoneManager.Instance;
        if (zoneManager == null) return;

        List<Vector2> points = new List<Vector2>();

        // 既存地区のポリゴン頂点を収集
        Dictionary<int, GeneratedModels.ZoneData> allZones = zoneManager.GetAllZones();
        if (allZones != null && allZones.TryGetValue(zoneId, out var zoneData))
        {
            float[][] poly = zoneData.GetPolygonArray();
            if (poly != null)
            {
                foreach (float[] vertex in poly)
                {
                    if (vertex != null && vertex.Length >= 2)
                    {
                        points.Add(new Vector2(vertex[0], vertex[1]));
                    }
                }
            }
        }

        // 追加道路のpolygon頂点を収集
        if (additionalPathIds != null)
        {
            List<Vector2> additionalPoints = CollectPathPolygonPoints(additionalPathIds);
            points.AddRange(additionalPoints);
        }

        if (points.Count < 2)
        {
            ClearPreview();
            return;
        }

        List<Vector2> hull = ComputeConvexHull(points);
        DrawPreviewHull(hull);
    }

    /// <summary>
    /// プレビュー描画をクリアする。
    /// キャンセル時や確定後に呼び出す。
    /// 実際の描画クリアはZoneManager側で毎フレーム管理されるため、
    /// ここではプレビュー用データを空にするよう要求する。
    /// </summary>
    public static void ClearPreview()
    {
        var zoneManager = ZoneManager.Instance;
        if (zoneManager != null)
        {
            zoneManager.ClearPreviewLines();
        }
    }

    /// <summary>
    /// 指定した道路IDリストからpolygonの全頂点（XZ平面の2D座標）を収集する。
    /// 道路のpolygonは道路の実際の形状を表すため、center_pointの1点だけではなく
    /// polygon全体の頂点を使うことで、プレビューのConvexHullが道路の実際の範囲を
    /// 正確に覆うようになる。
    /// </summary>
    private static List<Vector2> CollectPathPolygonPoints(IReadOnlyList<int> pathIds)
    {
        List<Vector2> points = new List<Vector2>();
        var pathManager = UnityEngine.Object.FindAnyObjectByType<PathManager>();
        if (pathManager == null) return points;

        foreach (int pathId in pathIds)
        {
            var pathData = pathManager.GetPathData(pathId);
            if (pathData == null) continue;

            float[][] poly = pathData.GetPolygonArray();
            if (poly != null)
            {
                foreach (float[] vertex in poly)
                {
                    if (vertex != null && vertex.Length >= 2)
                    {
                        points.Add(new Vector2(vertex[0], vertex[1]));
                    }
                }
            }
        }

        return points;
    }

    /// <summary>
    /// Graham ScanアルゴリズムでConvexHullを計算する。
    /// 入力点群から最も外側の凸包を求める。計算量はO(n log n)。
    /// </summary>
    private static List<Vector2> ComputeConvexHull(List<Vector2> points)
    {
        if (points.Count <= 2) return new List<Vector2>(points);

        // 重複を除去
        HashSet<Vector2> unique = new HashSet<Vector2>();
        List<Vector2> distinct = new List<Vector2>();
        foreach (var p in points)
        {
            if (unique.Add(p))
            {
                distinct.Add(p);
            }
        }

        if (distinct.Count <= 2) return distinct;

        // 最もYが小さい（同じならXが小さい）点を基準点として選ぶ
        int pivotIdx = 0;
        for (int i = 1; i < distinct.Count; i++)
        {
            if (distinct[i].y < distinct[pivotIdx].y ||
                (distinct[i].y == distinct[pivotIdx].y && distinct[i].x < distinct[pivotIdx].x))
            {
                pivotIdx = i;
            }
        }

        Vector2 pivot = distinct[pivotIdx];
        distinct.RemoveAt(pivotIdx);

        // 偏角ソート
        distinct.Sort((a, b) =>
        {
            float angleA = Mathf.Atan2(a.y - pivot.y, a.x - pivot.x);
            float angleB = Mathf.Atan2(b.y - pivot.y, b.x - pivot.x);
            if (Mathf.Abs(angleA - angleB) < 1e-6f)
            {
                float distA = (a - pivot).sqrMagnitude;
                float distB = (b - pivot).sqrMagnitude;
                return distA.CompareTo(distB);
            }
            return angleA.CompareTo(angleB);
        });

        // Graham Scan
        List<Vector2> hull = new List<Vector2> { pivot };
        foreach (var point in distinct)
        {
            while (hull.Count > 1 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(point);
        }

        return hull;
    }

    /// <summary>
    /// 3点OA→ABの外積（Z成分）を計算する。
    /// 正なら反時計回り、負なら時計回り、0なら共線。
    /// </summary>
    private static float Cross(Vector2 o, Vector2 a, Vector2 b)
    {
        return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }

    /// <summary>
    /// ConvexHullの頂点列をZoneManagerのプレビューラインとして登録する。
    /// </summary>
    private static void DrawPreviewHull(List<Vector2> hull)
    {
        if (hull.Count < 2) return;

        var zoneManager = ZoneManager.Instance;
        if (zoneManager == null) return;

        // ワールド座標のラインエントリに変換してZoneManagerに渡す
        List<ZoneManager.PreviewLineEntry> lines = new List<ZoneManager.PreviewLineEntry>();
        for (int i = 0; i < hull.Count; i++)
        {
            int next = (i + 1) % hull.Count;

            float x1 = hull[i].x;
            float z1 = hull[i].y;
            float y1 = Utils.GetGroundHeight(new Vector3(x1, 1000f, z1)) + 0.1f;

            float x2 = hull[next].x;
            float z2 = hull[next].y;
            float y2 = Utils.GetGroundHeight(new Vector3(x2, 1000f, z2)) + 0.1f;

            lines.Add(new ZoneManager.PreviewLineEntry
            {
                worldA = new Vector3(x1, y1, z1),
                worldB = new Vector3(x2, y2, z2)
            });
        }

        zoneManager.SetPreviewLines(lines, PreviewColor, PREVIEW_LINE_THICKNESS);
    }
}
