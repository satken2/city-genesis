using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 碁盤状の敷設(Constants.PATH_BUILD_METHOD_GRID)の入力とプレビュー。
/// 1点目・2点目で矩形の1辺を決め、3点目は2点目を通って1辺に直交する線の上に拘束して矩形を決める。
/// 置いた3点は単一の敷設と同じく edit_path:update_shape で UI へ送り、UI がコアの展開(POST /paths/layout)で
/// 作るパスを求めて apply_layout で返す。配置中は辺ごとの分割数で等分した仮の格子(コアと同じ割り付け)を
/// オーバーレイで描き、結果が届いたら作るパスを帯で描く(既存道路と重なる区間や敷けない区間はコアが除く)。
/// 点の移動・挿入・削除は受け付けず、やり直しはパネルのキャンセルで行う。
/// </summary>
public partial class RoadPlanManager
{
    /// <summary>敷設方法(Constants.PATH_BUILD_METHOD_*)。STATE_EDIT_PATH の build_method で受け取る。</summary>
    public int BuildMethod = Constants.PATH_BUILD_METHOD_SINGLE;

    /// <summary>碁盤状の分割数 [1辺目(1点目→2点目), 2辺目(2点目→3点目)]。</summary>
    public int[] GridDivisions = { 3, 3 };

    public bool IsGridBuild => BuildMethod == Constants.PATH_BUILD_METHOD_GRID;

    // コアが返した作るパスを、地表に沿わせた点列にしたもの。点を置き直すか分割数を変えると捨てる。
    private List<List<Vector3>> gridLayoutLines;

    // 仮の格子を地表に沿わせた点列と、それを作ったときの入力。毎フレームの描画で地形を引き直さない。
    private List<List<Vector3>> gridGuideLines;
    private Vector3 gridGuideP1, gridGuideP2, gridGuideP3;
    private int gridGuideDiv1, gridGuideDiv2;

    private const float GridLineSampleStep = 5f;
    private static readonly Color GridGuideColor = new Color(0.3f, 0.6f, 1f, 0.6f);

    /// <summary>edit_path:change_grid_divisions。仮の格子を描き直し、3点目の後は UI が返す展開を待つ。</summary>
    public void SetGridDivisions(int[] divisions)
    {
        if (divisions == null || divisions.Length != 2) return;
        GridDivisions = new[] { Mathf.Max(1, divisions[0]), Mathf.Max(1, divisions[1]) };
        gridLayoutLines = null;
        if (IsGridBuild) UpdateGridPreview();
    }

    private void OnGridLeftClick(Vector3 worldPos)
    {
        if (phase != RoadEditorPhase.Placing) return;
        Vector3 position = ApplySnapping(worldPos);
        if (points.Count == 2) position = ConstrainGridCorner(position);
        position.y = Utils.GetGroundHeight(position);
        var role = points.Count == 0 ? PathPointRole.Start
            : points.Count == 1 ? PathPointRole.Waypoint : PathPointRole.End;
        points.Add(new PathBuildPoint(position, role));
        gridLayoutLines = null;
        if (points.Count < 3)
        {
            CommitShapeChange();
            PlaySound(placePointSound);
            return;
        }
        phase = RoadEditorPhase.Editing;
        CommitShapeChange(finalized: true);
        PlaySound(finalizeShapeSound);
    }

    /// <summary>3点目の候補を、2点目を通って1点目→2点目に直交する線の上へ射影する。</summary>
    private Vector3 ConstrainGridCorner(Vector3 position)
    {
        Vector3 p1 = points[0].position, p2 = points[1].position;
        var edge = new Vector2(p2.x - p1.x, p2.z - p1.z);
        if (edge.sqrMagnitude < 1e-6f) return position;
        var normal = new Vector2(-edge.y, edge.x).normalized;
        float width = Vector2.Dot(new Vector2(position.x - p2.x, position.z - p2.z), normal);
        return new Vector3(p2.x + normal.x * width, position.y, p2.z + normal.y * width);
    }

    /// <summary>
    /// apply_layout。今の編集世代の結果だけを採用し、作るパスを地表に沿った点列にして帯で描く。
    /// json は各パスの [始点, 終点]([x, 相対Y, z])の列。
    /// </summary>
    private void ApplyGridLayout(string revision, string json)
    {
        if (!IsGridBuild || revision != routeRevision) return;
        float[][][] segments;
        try { segments = JsonConvert.DeserializeObject<float[][][]>(json ?? "null"); }
        catch (JsonException) { return; }
        var lines = new List<List<Vector3>>();
        foreach (var segment in segments ?? System.Array.Empty<float[][]>())
        {
            if (segment == null || segment.Length < 2 || segment[0] == null || segment[1] == null
                || segment[0].Length < 3 || segment[1].Length < 3) continue;
            lines.Add(SampleGroundLine(new Vector2(segment[0][0], segment[0][2]), new Vector2(segment[1][0], segment[1][2]),
                RoadPartHeights.ROAD_SPLINE_OFFSET));
        }
        gridLayoutLines = lines;
        UpdateGridPreview();
    }

    /// <summary>碁盤状の3Dプレビュー。3点目の後にコアの結果が届いていればそのパスを帯で描き、それ以外は描かない。</summary>
    private void UpdateGridPreview()
    {
        if (roadRenderer == null) return;
        roadRenderer.UpdateLineStrips(phase == RoadEditorPhase.Editing ? gridLayoutLines : null, PathSubtype);
    }

    /// <summary>碁盤状のオーバーレイ。配置中と、3点目の後にコアの結果が届くまでは仮の格子を描く。</summary>
    private void DrawGridOverlay(Camera cam)
    {
        bool placing = phase == RoadEditorPhase.Placing;
        if (placing && points.Count == 1) DrawPreviewLine(cam);
        if (gridLayoutLines == null && points.Count >= 2 && (points.Count == 3 || placing))
        {
            Vector3 third = points.Count == 3 ? points[2].position : hoverWorldPosition;
            foreach (var line in GetGridGuideLines(points[0].position, points[1].position, third))
                for (int i = 0; i < line.Count - 1; i++)
                    overlayRenderer.AddSolidLine(line[i], line[i + 1], GridGuideColor, solidLineThickness);
        }
        if (placing) DrawPlacingMarker(cam);
        DrawPoints(cam);
    }

    /// <summary>
    /// 3点で決まる矩形を分割数で等分した格子線(外周を含む)を、地表に沿った点列で返す。
    /// 割り付けはコアの generate_rect_grid_segments と同じ。
    /// </summary>
    private List<List<Vector3>> GetGridGuideLines(Vector3 p1, Vector3 p2, Vector3 p3)
    {
        int div1 = GridDivisions[0], div2 = GridDivisions[1];
        if (gridGuideLines != null && p1 == gridGuideP1 && p2 == gridGuideP2 && p3 == gridGuideP3
            && div1 == gridGuideDiv1 && div2 == gridGuideDiv2) return gridGuideLines;
        gridGuideP1 = p1; gridGuideP2 = p2; gridGuideP3 = p3;
        gridGuideDiv1 = div1; gridGuideDiv2 = div2;
        gridGuideLines = new List<List<Vector3>>();

        var a = new Vector2(p1.x, p1.z);
        var edge = new Vector2(p2.x, p2.z) - a;
        if (edge.sqrMagnitude < 1e-6f) return gridGuideLines;
        var normal = new Vector2(-edge.y, edge.x).normalized;
        var side = normal * Vector2.Dot(new Vector2(p3.x - p2.x, p3.z - p2.z), normal);
        for (int k = 0; k <= div2; k++)
        {
            Vector2 start = a + side * k / div2;
            gridGuideLines.Add(SampleGroundLine(start, start + edge, 0f));
        }
        for (int k = 0; k <= div1; k++)
        {
            Vector2 start = a + edge * k / div1;
            gridGuideLines.Add(SampleGroundLine(start, start + side, 0f));
        }
        return gridGuideLines;
    }

    private static List<Vector3> SampleGroundLine(Vector2 a, Vector2 b, float lift)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / GridLineSampleStep));
        var line = new List<Vector3>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, (float)i / steps);
            var position = new Vector3(p.x, 0f, p.y);
            position.y = Utils.GetGroundHeight(position) + lift;
            line.Add(position);
        }
        return line;
    }
}
