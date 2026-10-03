using UnityEngine;
using GeneratedModels;

public partial class RoadPlanManager
{
    /// <summary>
    /// 全ポイントの制御点を3次元Catmull-Romスプラインの接線ベクトルから計算する。
    ///
    /// 接線ベクトル V_i = P_{i+1} - P_{i-1}、テンション=1/6。
    /// 端点は片側の隣接アンカーとのベクトルを使用する。
    /// これによりX・Y・Zすべての軸で滑らかなベジェ曲線が得られる。
    ///
    /// 手動調整済み(control_*_manual)の制御点はXZを上書きしない。頂点の移動・挿入・削除で
    /// 呼ばれるたびに全制御点を作り直すと、ユーザーが整えた曲線が毎回消えてしまうため。
    /// 手動調整済みの制御点のXZは隣接アンカーの移動には追従しない(ユーザーの意図した方向を保つ)。
    /// 高さは control_height_manual でない限り、自動の制御点と同じ縦断勾配 V_i.y / |V_i.xz| を
    /// アンカーからの水平距離に掛けて決める。ハンドルの高さを固定すると、ノードの高さを
    /// いくら変えてもそのノードの勾配が変わらず、左右のハンドルの長さが違えば縦断が折れるため。
    /// </summary>
    /// <param name="resetManual">true なら手動調整フラグを全て落とし、全制御点を自動計算し直す</param>
    private void RecomputeAllControlPoints(bool resetManual = false)
    {
        if (points.Count < 2) return;

        int n = points.Count;

        for (int i = 0; i < n; i++)
        {
            var pt = points[i];
            if (resetManual)
            {
                pt.control_out_manual = false;
                pt.control_in_manual = false;
                pt.control_height_manual = false;
            }

            Vector3 tangent = AutoTangent(i);
            float grade = HorizontalGrade(tangent);

            // control_out: このポイントから次へ向かう出口制御点
            if (i < n - 1 && pt.role != PathPointRole.End)
            {
                if (!pt.control_out_manual)
                    pt.control_out = pt.position + tangent * (1f / 6f);
                else if (pt.control_out.HasValue && !pt.control_height_manual)
                    pt.control_out = WithGrade(pt.control_out.Value, pt.position, grade);
            }

            // control_in: 前のポイントからこのポイントへ入る入口制御点
            if (pt.role != PathPointRole.Start)
            {
                if (!pt.control_in_manual)
                    pt.control_in = pt.position - tangent * (1f / 6f);
                else if (pt.control_in.HasValue && !pt.control_height_manual)
                    pt.control_in = WithGrade(pt.control_in.Value, pt.position, -grade);
            }

            points[i] = pt;
        }

        // 始点・終点が既存パスの終端に接続している場合、その端点制御点を
        // 既存パスの延長線方向へ吸着させ、接続部で道路が折れないようにする。
        AlignEndpointControlsToExistingPaths();
    }

    /// <summary>アンカー i の自動接線 P_{i+1} - P_{i-1}(端点は片側)。バックエンド Path._auto_fill_controls と同じ形。</summary>
    private Vector3 AutoTangent(int i)
    {
        int n = points.Count;
        return points[Mathf.Min(i + 1, n - 1)].position - points[Mathf.Max(i - 1, 0)].position;
    }

    /// <summary>接線の縦断勾配(水平1mあたりの高さ)。水平成分が無い接線は0。</summary>
    private static float HorizontalGrade(Vector3 tangent)
    {
        float horizontal = Mathf.Sqrt(tangent.x * tangent.x + tangent.z * tangent.z);
        return horizontal > 1e-4f ? tangent.y / horizontal : 0f;
    }

    /// <summary>制御点のXZを保ち、高さをアンカーから水平距離×勾配の位置にする。入口制御点は勾配の符号を反転して渡す。</summary>
    private static Vector3 WithGrade(Vector3 control, Vector3 anchor, float grade)
    {
        control.y = anchor.y + grade * Utils.XZDistance(control, anchor);
        return control;
    }

    /// <summary>
    /// アンカー(頂点)を newPosition へ移動し、制御点を追従させる。
    /// 手動調整済みの制御点はアンカーからの相対位置(XZ)を保ったまま平行移動し、
    /// 高さは RecomputeAllControlPoints が移動後の縦断勾配から決め直す。
    /// 未調整の制御点は移動後の頂点列から RecomputeAllControlPoints で再計算する。
    /// ドラッグ移動・標高変更(キー/IPC)の全経路がここを通る。
    /// </summary>
    private void MoveAnchorPosition(int index, Vector3 newPosition)
    {
        if (index < 0 || index >= points.Count) return;

        var pt = points[index];
        Vector3 oldPosition = pt.position;
        Vector3 deltaXZ = new Vector3(newPosition.x - oldPosition.x, 0f, newPosition.z - oldPosition.z);
        pt.position = newPosition;
        pt.proposalGenerated = false;

        if (pt.control_out_manual && pt.control_out.HasValue) pt.control_out = pt.control_out.Value + deltaXZ;
        if (pt.control_in_manual && pt.control_in.HasValue) pt.control_in = pt.control_in.Value + deltaXZ;
        points[index] = pt;

        // 自動接線が変わるのは両隣。動かしたアンカー自身も、経路提案で固定した勾配より新しい配置の勾配を優先する。
        ReleaseControlHeights(index - 1, index + 1);
        RecomputeAllControlPoints();
    }

    /// <summary>first〜last(範囲外は無視)のアンカーの制御点の高さ固定を解除する。</summary>
    private void ReleaseControlHeights(int first, int last)
    {
        for (int i = Mathf.Max(0, first); i <= Mathf.Min(points.Count - 1, last); i++)
        {
            var pt = points[i];
            pt.control_height_manual = false;
            points[i] = pt;
        }
    }

    /// <summary>
    /// 制御点(ハンドル)のXZを動かす。3Dのドラッグと2Dパネルの共通処理。
    /// 手動になるのはXZだけで、高さはアンカーの縦断勾配から左右同じ勾配で決め直す
    /// (経路提案などで固定した高さも解除する)。ハンドルは平面の曲がりを調整する道具で、
    /// 高さを固定するとノードの高さを変えても勾配が変わらなくなるため。
    /// 線路の接続端では本線の接線上へ射影し、接続方向を保って長さだけ調整する。
    /// </summary>
    private void MoveControlXZ(int index, bool controlIn, float x, float z)
    {
        var pt = points[index];
        Vector3 control = new Vector3(x, pt.position.y, z);
        if (IsRailJunctionTarget && (index == 0 || index == points.Count - 1)
            && TryGetRailHandleDirection(pt.position, control - pt.position, out Vector3 direction))
        {
            control = pt.position + direction * Vector3.Dot(control - pt.position, direction);
        }
        if (controlIn)
        {
            pt.control_in = control;
            pt.control_in_manual = true;
        }
        else
        {
            pt.control_out = control;
            pt.control_out_manual = true;
        }
        pt.control_height_manual = false;
        points[index] = pt;
        // 反対側(control_in / control_out)を頂点を挟んで一直線に保ち、道路が折れないようにする
        EnforceCollinearControls(index, movedControlOut: !controlIn);
        RecomputeAllControlPoints();
    }

    /// <summary>
    /// 指定ポイントの2本のベジエ制御点(control_in / control_out)を常に一直線（180度）に保つ。
    /// 制御点をドラッグしたとき反対側の制御点を頂点を挟んだ反対方向へ再配置し、
    /// 頂点で道路が折れないようにする（フロント側の親切設計。バックエンド検証は不要）。
    /// 反対側ハンドルの長さ（アンカーからの距離）は維持し、向きだけを反転させる。
    /// 両方の制御点を持つポイント（ウェイポイント）でのみ機能する。
    /// 始点/終点は片側しか持たないため何もしない。
    /// XZ平面上で一直線にするだけで、高さは変えない(呼び出し側の RecomputeAllControlPoints が
    /// 左右同じ縦断勾配で決め直す)。
    /// 追従させた反対側も手動調整済み扱いにする。片方だけ自動計算のままだと、
    /// 次の頂点移動で再計算されて一直線が崩れ、頂点で道路が折れるため。
    /// </summary>
    /// <param name="index">対象ポイントのインデックス</param>
    /// <param name="movedControlOut">true なら control_out を動かした（control_in を追従させる）、false ならその逆</param>
    private void EnforceCollinearControls(int index, bool movedControlOut)
    {
        if (index < 0 || index >= points.Count) return;
        var pt = points[index];
        if (!pt.control_in.HasValue || !pt.control_out.HasValue) return;

        Vector3 anchor = pt.position;


        if (movedControlOut)
        {
            Vector3 outDir = pt.control_out.Value - anchor;
            outDir.y = 0f;
            if (outDir.sqrMagnitude < 1e-6f) return;
            Vector3 inVec = pt.control_in.Value - anchor;
            inVec.y = 0f;
            float inLen = inVec.magnitude;
            Vector3 newIn = anchor - outDir.normalized * inLen;
            newIn.y = pt.control_in.Value.y;
            pt.control_in = newIn;
            pt.control_in_manual = true;
        }
        else
        {
            Vector3 inDir = pt.control_in.Value - anchor;
            inDir.y = 0f;
            if (inDir.sqrMagnitude < 1e-6f) return;
            Vector3 outVec = pt.control_out.Value - anchor;
            outVec.y = 0f;
            float outLen = outVec.magnitude;
            Vector3 newOut = anchor - inDir.normalized * outLen;
            newOut.y = pt.control_out.Value.y;
            pt.control_out = newOut;
            pt.control_out_manual = true;
        }

        points[index] = pt;
    }

    /// <summary>
    /// 道路の端点延長は「直線延長」吸着がONで、角度差が許容範囲内のときに整列する。
    /// 線路はこのフラグによらず、端点が接する本線の解析接線へ自動ハンドルを整列する。
    /// 線路の手動ハンドルも接線へ整列し、アンカーを移動しても接続方向を保つ。
    /// RecomputeAllControlPoints の自動計算結果を上書きする形で適用し、
    /// 制御点の長さ（アンカーからの距離）は整列前の値を維持して向きだけを揃える。
    /// 確定送信される制御点にもこの結果が載るため、バックエンドに保存される形状と一致する。
    /// </summary>
    private void AlignEndpointControlsToExistingPaths()
    {
        if ((!TerrainInputHandler.EnableStraightLineSnapping && !IsRailJunctionTarget) || SnappingSuppressed || points.Count < 2) return;
        AlignEndpointControl(0, false);
        AlignEndpointControl(points.Count - 1, true);
    }

    private void AlignEndpointControl(int index, bool incoming)
    {
        var point = points[index];
        Vector3? control = incoming ? point.control_in : point.control_out;
        bool manual = incoming ? point.control_in_manual : point.control_out_manual;
        if (!control.HasValue || (manual && !IsRailJunctionTarget)) return;
        Vector3 direction = control.Value - point.position;
        direction.y = 0f;
        float length = direction.magnitude;
        if (length <= 1e-4f || !TryGetEndpointAlignmentDirection(point.position, direction, out Vector3 aligned)) return;
        // 平面方向の整列では縦断形状を変えない。
        Vector3 position = point.position + aligned * length;
        position.y = control.Value.y;
        if (incoming) point.control_in = position;
        else point.control_out = position;
        points[index] = point;
    }

    /// <summary>
    /// 指定ワールド位置(XZ)に端点を持つ既存パスセグメントを探し、handleDir（自動計算された
    /// 端点ハンドル方向）に最も角度が近い延長方向（XZ平面・正規化・±の向き解決済み）を返す。
    /// 新規パスの端点が既存パスの終端に接続するとき、接続部を滑らかにするために
    /// 端点の制御点をこの方向へ吸着させる用途で使う。
    ///
    /// - pos が端点にほぼ一致するセグメントのみ候補とする（中間で交差しているだけのパスは無視）。
    /// - 網互換でない相手・編集中パス自身のセグメントは除外する（自分の方向に吸着して折り返すのを防ぐ）。
    /// - 候補が複数ある場合（交差点への接続など）は handleDir との角度差が最小のものを採用する。
    /// - 最小角度差が PATH_TANGENT_ALIGN_MAX_ANGLE_DEG を超える場合は false
    ///   （ユーザーが明らかに別方向へ引いており、整列すると描いた曲線を壊すため）。
    /// - 延長線の向き(±)は handleDir と同じ側（内積が正の側）を採用し、意図せぬ折り返しを防ぐ。
    /// </summary>
    private bool TryGetExistingPathExtensionDir(Vector3 pos, Vector3 handleDir, out Vector3 extensionDir)
    {
        extensionDir = Vector3.zero;
        if (pathManager == null) return false;

        Vector3 handleFlat = new Vector3(handleDir.x, 0f, handleDir.z);
        if (handleFlat.sqrMagnitude < 1e-8f) return false;
        handleFlat.Normalize();

        var segs = pathManager.QueryNearbySegments(pos, snapThreshold);
        if (segs == null) return false;

        float bestAngle = float.MaxValue;
        bool found = false;
        foreach (var seg in segs)
        {
            // 網互換でない相手（別タイプ・別網のインフラパス等）は接続対象ではないため無視
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
            // 編集中のパス自身のセグメントは除外（自分の方向へ吸着すると折り返してしまう）
            if (seg.path != null && pathId != 0 && seg.path.path_id == pathId) continue;

            float dA = Vector2.Distance(new Vector2(seg.a.x, seg.a.z), new Vector2(pos.x, pos.z));
            float dB = Vector2.Distance(new Vector2(seg.b.x, seg.b.z), new Vector2(pos.x, pos.z));

            Vector3 near, far;
            float endpointDist;
            if (dA <= dB) { near = seg.a; far = seg.b; endpointDist = dA; }
            else { near = seg.b; far = seg.a; endpointDist = dB; }

            // pos がセグメントの端点にほぼ一致する場合のみ延長方向の候補とする
            if (endpointDist >= snapThreshold) continue;

            Vector3 dir = new Vector3(near.x - far.x, 0f, near.z - far.z);
            if (dir.sqrMagnitude < 1e-6f) continue;
            dir.Normalize();

            Vector3 candidate = Vector3.Dot(handleFlat, dir) >= 0f ? dir : -dir;
            float angle = Vector3.Angle(handleFlat, candidate);
            if (angle <= Constants.PATH_TANGENT_ALIGN_MAX_ANGLE_DEG && angle < bestAngle)
            {
                bestAngle = angle;
                extensionDir = candidate;
                found = true;
            }
        }
        return found;
    }

    private bool TryGetEndpointAlignmentDirection(Vector3 position, Vector3 current, out Vector3 aligned)
    {
        return IsRailJunctionTarget
            ? TryGetRailHandleDirection(position, current, out aligned)
            : TryGetExistingPathExtensionDir(position, current, out aligned);
    }
}
