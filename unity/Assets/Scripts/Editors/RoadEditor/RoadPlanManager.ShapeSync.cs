using System.Collections.Generic;
using Newtonsoft.Json;
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
        public string alignmentJson;
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

    private sealed class ValidatedAlignment
    {
        public float? min_curve_radius;
        public float max_grade;
        public float curve_radius_limit;
        public float grade_limit;
        public float[][] curve_violations;
        public float[][] grade_violations;
        public AlignmentPreview preview;
    }

    private sealed class AlignmentPreview
    {
        public double?[][] samples;
        public AlignmentWarning[] warnings;
    }

    private sealed class AlignmentWarning
    {
        public bool curve;
        public float value;
        public float limit;
        public float[] position;
        public float[][] points;
    }

    private ValidatedAlignment[] authoritativeAlignments;

    /// <summary>全線検証と同じ世代だけ採用する。再通知は形状を変更せず、再検証も要求しない。</summary>
    private void ApplyAuthoritativeAlignments(string revision, string json)
    {
        if (revision != routeRevision) return;
        try { authoritativeAlignments = JsonConvert.DeserializeObject<ValidatedAlignment[]>(json ?? "null"); }
        catch (JsonException) { return; }
        cachedAlignmentViolations = null;
        EmitShapeUpdate();
    }

    private List<List<Vector3>> ApplyAuthoritativeAlignmentCache(List<List<Vector3>> violations)
    {
        foreach (var line in authoritativeAlignments)
        {
            cachedAlignmentMinRadius = Mathf.Min(cachedAlignmentMinRadius, line.min_curve_radius ?? float.PositiveInfinity);
            cachedAlignmentMaxGradePct = Mathf.Max(cachedAlignmentMaxGradePct, line.max_grade);
            cachedCurveViolation |= line.curve_violations?.Length > 0;
            cachedGradeViolation |= line.grade_violations?.Length > 0;
            foreach (var warning in line.preview?.warnings ?? System.Array.Empty<AlignmentWarning>())
            {
                var run = new List<Vector3>();
                foreach (var p in warning.points) run.Add(new Vector3(p[0], p[1], p[2]));
                violations.Add(run);
                alignmentLabels.Add(new AlignmentLabel {
                    curve = warning.curve, value = warning.value, limit = warning.limit,
                    position = new Vector3(warning.position[0], warning.position[1], warning.position[2]),
                    text = warning.curve
                        ? $"カーブ  R {warning.value:0.00}m\n下限 {warning.limit:0.##}m 未満"
                        : $"勾配  G {warning.value:0.00}%\n上限 {warning.limit:0.##}% 超過",
                });
            }
        }
        if (authoritativeAlignments.Length == 0) return violations;
        var first = authoritativeAlignments[0];
        cachedAlignmentRadiusLimit = first.curve_radius_limit;
        cachedAlignmentGradeLimit = first.grade_limit;
        var samples = first.preview?.samples;
        if (samples == null) return violations;
        cachedAlignmentSamplesWorld = new List<Vector3>();
        cachedAlignmentRadius = new float[samples.Length];
        cachedAlignmentGradePct = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            cachedAlignmentSamplesWorld.Add(new Vector3((float)s[0].Value, (float)s[5].Value, (float)s[1].Value));
            cachedAlignmentRadius[i] = s[2].HasValue ? (float)s[2].Value : float.PositiveInfinity;
            cachedAlignmentGradePct[i] = (float)s[3].Value;
        }
        return violations;
    }

    /// <summary>
    /// 2Dパネルからの形状操作を適用する。Editingフェーズ以外、および3Dでドラッグ中は無視する
    /// (3Dと2Dから同時に同じ点を動かす競合を避ける)。適用後は3Dドラッグ終了と同じ通知を行う。
    /// </summary>
    public void ApplyShapeCommand(ShapeCommand cmd)
    {
        if (phase != RoadEditorPhase.Editing) return;
        // 掴んだだけで形状が変わっていない場合も検証応答を受け取る。移動した時点で世代が変わる。
        if (cmd.op == "apply_alignment")
        {
            ApplyAuthoritativeAlignments(cmd.revision, cmd.alignmentJson);
            return;
        }
        if (isDragging) return;
        if (TutorialRuntime.PathRestricted || TutorialRuntime.BlocksWorldInput) return;

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
            ["revision"] = routeRevision,
            ["selected_index"] = selectedPointIndex,
            ["lateral_offsets"] = new List<float>(LateralOffsets ?? System.Array.Empty<float>()),
            // 2Dパネルの縦断面(高さ編集)ストリップの表示可否と高さの下限(マスター min_height)
            ["height_editable"] = IsHeightEditable(),
            ["min_height"] = GetMinHeightFromMaster(),
            ["route_proposal"] = BuildRouteProposalPayload(),
            ["max_span_violations"] = BuildMaxSpanViolationsPayload(),
        };
        var alignment = BuildAlignmentPayload();
        if (alignment != null) extras["alignment"] = alignment;
        return extras;
    }

    /// <summary>赤線と同じノード間距離違反を送る。解消時も空配列でUIの警告を消す。</summary>
    private List<Dictionary<string, object>> BuildMaxSpanViolationsPayload()
    {
        var result = new List<Dictionary<string, object>>();
        var violations = CheckMaxSpanViolations();
        if (violations.Count == 0) return result;
        float limit = MasterDataStore.Instance.GetPathMaster(PathSubtype).max_span.Value;
        foreach (int index in violations)
        {
            Vector3 a = points[index].position, b = points[index + 1].position;
            float dx = b.x - a.x, dz = b.z - a.z;
            result.Add(new Dictionary<string, object>
            {
                ["segment_index"] = index,
                ["distance"] = Mathf.Sqrt(dx * dx + dz * dz),
                ["limit"] = limit,
            });
        }
        return result;
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
        if (authoritativeAlignments != null)
            for (int i = 0; i < n; i++) dists[i] = (float)authoritativeAlignments[0].preview.samples[i][4].Value;

        int stride = authoritativeAlignments != null ? 1
            : Mathf.Max(1, Mathf.RoundToInt(AlignmentSyncStrideM / AlignmentSampleStep));
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

        var warnings = new List<List<float[]>>();
        if (authoritativeAlignments != null)
        {
            foreach (var run in cachedAlignmentViolations)
            {
                var line = new List<float[]>();
                foreach (var p in run) line.Add(new[] { p.x, p.y, p.z });
                warnings.Add(line);
            }
            // 地盤・距離・指標も検証時の値を保ち、Unityでの再サンプリングを挟まない。
            samples.Clear();
            foreach (var s in authoritativeAlignments[0].preview.samples)
            {
                var row = new List<object>();
                foreach (var value in s) row.Add(value.HasValue ? (object)value.Value : null);
                samples.Add(row);
            }
        }

        return new Dictionary<string, object>
        {
            ["radius_limit"] = cachedAlignmentRadiusLimit,
            ["grade_limit"] = cachedAlignmentGradeLimit,
            ["min_curve_radius"] = float.IsInfinity(cachedAlignmentMinRadius) ? null : (object)cachedAlignmentMinRadius,
            ["max_grade"] = cachedAlignmentMaxGradePct,
            ["curve_violated"] = cachedCurveViolation,
            ["grade_violated"] = cachedGradeViolation,
            ["authoritative"] = authoritativeAlignments != null,
            ["warnings"] = authoritativeAlignments == null ? null : warnings,
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
