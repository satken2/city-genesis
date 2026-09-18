using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tauriの2Dパス形状編集パネル(path_shape_editor)との形状同期。
/// 形状の真実源はこのクラス(3Dエディタ)のままで、2Dパネルはノード・制御点の操作を
/// <see cref="ShapeCommand"/> として送り、結果は既存の edit_path:update_shape(全状態)で受け取る。
/// 2Dからの操作はXZだけを動かし、標高・盛土は3D側の値をそのまま維持する。吸着は適用しない。
/// </summary>
public partial class RoadPlanManager
{
    /// <summary>edit_path:shape_command のパラメータ。op ごとに使うフィールドが異なる。</summary>
    public struct ShapeCommand
    {
        /// <summary>select_node / move_node / move_control / insert_node / remove_node</summary>
        public string op;
        /// <summary>対象ノードの添字(select_node / move_node / move_control / remove_node)</summary>
        public int index;
        /// <summary>挿入先の辺の添字。points[edgeIndex] と points[edgeIndex+1] の間(insert_node)</summary>
        public int edgeIndex;
        /// <summary>"in"(control_in) / "out"(control_out)。move_control のみ</summary>
        public string which;
        /// <summary>ワールドX(move_node / move_control / insert_node)</summary>
        public float x;
        /// <summary>ワールドZ(move_node / move_control / insert_node)</summary>
        public float z;
        public string revision;
        public string routeJson;
    }

    /// <summary>2Dへ送る線形サンプルの間隔(m)。1m推定を間引いて edit_path:update_shape の肥大化を防ぐ。</summary>
    private const float AlignmentSyncStrideM = 5f;

    /// <summary>これ以上の曲線半径は「直線」として2Dへ null で送る(JSONに無限大を載せられないため)。</summary>
    private const float AlignmentSyncStraightRadius = 100000f;

    // CheckAlignmentViolations が直近に評価した1m等間隔サンプル(ワールド座標)と各サンプルの半径・勾配。
    // 形状変更で InvalidateShapeCache → cachedAlignmentViolations=null となり、次の評価で作り直される。
    private List<Vector3> cachedAlignmentSamplesWorld;
    private float[] cachedAlignmentRadius;
    private float[] cachedAlignmentGradePct;
    private float cachedAlignmentRadiusLimit;

    /// <summary>
    /// 2Dパネルからの形状操作を適用する。Editingフェーズ以外、および3Dでドラッグ中は無視する
    /// (3Dと2Dから同時に同じ点を動かす競合を避ける)。適用後は3Dドラッグ終了と同じ通知を行う。
    /// </summary>
    public void ApplyShapeCommand(ShapeCommand cmd)
    {
        if (phase != RoadEditorPhase.Editing || isDragging) return;

        switch (cmd.op)
        {
            case "apply_route":
                ApplyRouteProposal(cmd.revision, cmd.routeJson);
                break;
            case "undo_route":
                UndoRouteProposal(cmd.revision);
                break;
            case "select_node":
                SelectPoint(cmd.index >= 0 && cmd.index < points.Count ? cmd.index : -1);
                EmitSelectNode();
                EmitShapeUpdate();
                break;

            case "move_node":
                {
                    if (cmd.index < 0 || cmd.index >= points.Count) return;
                    var pt = points[cmd.index];
                    // 2DはXZだけを動かす。地面相対の高さは3Dドラッグと同じく維持する。
                    float relY = pt.position.y - Utils.GetGroundHeight(pt.position);
                    Vector3 newPos = new Vector3(cmd.x, 0f, cmd.z);
                    newPos.y = Utils.GetGroundHeight(newPos) + relY;
                    SelectPoint(cmd.index);
                    MoveAnchorPosition(cmd.index, newPos);
                    InvalidateShapeCache();
                    Update3DPreview();
                    EmitShapeUpdate();
                    EmitSelectNode();
                }
                break;

            case "move_control":
                {
                    // 直線モードでは制御点を持たない(2Dにも表示しない)
                    if (!Curved || cmd.index < 0 || cmd.index >= points.Count) return;
                    var pt = points[cmd.index];
                    bool moveIn = cmd.which == "in";
                    // 始点は control_in、終点は control_out を持たない
                    if (moveIn && cmd.index == 0) return;
                    if (!moveIn && cmd.index == points.Count - 1) return;
                    Vector3 nc = new Vector3(cmd.x, 0f, cmd.z);
                    if (moveIn)
                    {
                        // 3DのXZドラッグと同じく制御点の絶対標高を維持する
                        nc.y = (pt.control_in ?? pt.position).y;
                        pt.control_in = nc;
                        pt.control_in_manual = true;
                    }
                    else
                    {
                        nc.y = (pt.control_out ?? pt.position).y;
                        pt.control_out = nc;
                        pt.control_out_manual = true;
                    }
                    points[cmd.index] = pt;
                    EnforceCollinearControls(cmd.index, movedControlOut: !moveIn);
                    InvalidateShapeCache();
                    Update3DPreview();
                    EmitShapeUpdate();
                }
                break;

            case "insert_node":
                // 挿入位置のYは InsertPointOnEdge が次回配置高さから解決する(3Dの辺クリックと同じ)
                InsertPointOnEdge(cmd.edgeIndex, new Vector3(cmd.x, 0f, cmd.z));
                break;

            case "remove_node":
                // 最低2点の維持は RemovePoint が保証する
                RemovePoint(cmd.index);
                break;

            default:
                Debug.LogWarning($"RoadPlanManager: 未知の shape_command op={cmd.op}");
                break;
        }
    }

    /// <summary>
    /// edit_path:query_shape への応答。2Dパネルは開くたびに再マウントされ、変化時にしか送られない
    /// update_shape を受け取れないため、現在形状を1回送り直す。セッション外でも phase=inactive を返す。
    /// </summary>
    public void EmitShapeUpdateOnRequest()
    {
        EmitShapeUpdate();
    }

    /// <summary>
    /// edit_path:update_shape に載せる2D同期用の追加フィールド。
    /// phase(placing/editing/inactive)、selected_index、lateral_offsets、alignment(線形サンプルとラベル)。
    /// </summary>
    private Dictionary<string, object> BuildShapeSyncExtras()
    {
        string phaseName = phase == RoadEditorPhase.Placing ? "placing"
            : phase == RoadEditorPhase.Editing ? "editing" : "inactive";
        var extras = new Dictionary<string, object>
        {
            ["phase"] = phaseName,
            ["selected_index"] = selectedPointIndex,
            ["lateral_offsets"] = new List<float>(LateralOffsets ?? System.Array.Empty<float>()),
            // 2Dパネルの縦断面(高さ編集)ストリップの表示可否と高さの下限(マスター min_height)
            ["height_editable"] = IsHeightEditable(),
            ["min_height"] = GetMinHeightFromMaster(),
            ["route_proposal"] = BuildRouteProposalPayload(),
        };
        var alignment = BuildAlignmentPayload();
        if (alignment != null) extras["alignment"] = alignment;
        return extras;
    }

    /// <summary>
    /// 3Dの区間ラベルと同じローカル推定(1m等間隔)を約5m間隔へ間引いた線形サンプルと、
    /// 原因別違反区間の最悪値ラベル、各アンカーの縦断面上の位置を返す。
    /// 線形評価の対象外(インフラ等)や点が足りないときは null。
    /// samples の各要素は [x, z, radius(直線は null), grade_pct, 始点からの距離m, 路面の絶対標高, 地盤の絶対標高]。
    /// nodes の各要素はアンカーと同順の [始点からの距離m, 地盤の絶対標高](縦断面ストリップの横位置と基準高さ)。
    /// </summary>
    private Dictionary<string, object> BuildAlignmentPayload()
    {
        CheckAlignmentViolations();
        var world = cachedAlignmentSamplesWorld;
        if (world == null || cachedAlignmentRadius == null || cachedAlignmentGradePct == null) return null;
        int n = world.Count;
        if (n < 2) return null;

        // 1mサンプルの累積距離(縦断面の横軸)
        var dists = new float[n];
        for (int i = 1; i < n; i++)
        {
            float dx = world[i].x - world[i - 1].x, dz = world[i].z - world[i - 1].z;
            dists[i] = dists[i - 1] + Mathf.Sqrt(dx * dx + dz * dz);
        }

        int stride = Mathf.Max(1, Mathf.RoundToInt(AlignmentSyncStrideM / AlignmentSampleStep));
        var samples = new List<List<object>>();
        for (int i = 0; i < n; i += stride)
        {
            samples.Add(BuildAlignmentSample(world[i], cachedAlignmentRadius[i], cachedAlignmentGradePct[i], dists[i]));
        }
        if ((n - 1) % stride != 0)
        {
            samples.Add(BuildAlignmentSample(world[n - 1], cachedAlignmentRadius[n - 1], cachedAlignmentGradePct[n - 1], dists[n - 1]));
        }

        // 各アンカーの縦断面上の位置 = XZで最も近い1mサンプルの距離。地盤高はアンカー直下の値。
        var nodes = new List<List<float>>(points.Count);
        foreach (var pt in points)
        {
            int best = 0;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                float dx = world[i].x - pt.position.x, dz = world[i].z - pt.position.z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            nodes.Add(new List<float> { dists[best], Utils.GetGroundHeight(pt.position) });
        }

        var labels = new List<Dictionary<string, object>>();
        foreach (var label in alignmentLabels)
        {
            labels.Add(new Dictionary<string, object>
            {
                ["curve"] = label.curve,
                ["x"] = label.position.x,
                ["z"] = label.position.z,
                ["value"] = label.value,
                ["limit"] = label.limit,
            });
        }

        return new Dictionary<string, object>
        {
            ["radius_limit"] = cachedAlignmentRadiusLimit,
            ["grade_limit"] = cachedAlignmentGradeLimit,
            ["min_curve_radius"] = float.IsInfinity(cachedAlignmentMinRadius) ? null : (object)cachedAlignmentMinRadius,
            ["max_grade"] = cachedAlignmentMaxGradePct,
            ["samples"] = samples,
            ["labels"] = labels,
            ["nodes"] = nodes,
        };
    }

    private static List<object> BuildAlignmentSample(Vector3 p, float radius, float gradePct, float dist)
    {
        object r = (float.IsInfinity(radius) || float.IsNaN(radius) || radius >= AlignmentSyncStraightRadius) ? null : (object)radius;
        return new List<object> { p.x, p.z, r, gradePct, dist, p.y, Utils.GetGroundHeight(p) };
    }
}
