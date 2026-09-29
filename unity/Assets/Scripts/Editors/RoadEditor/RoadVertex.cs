using UnityEngine;

/// <summary>
/// 道路エディタで配置するポイントのロール。
/// デバッグ用HTML(path_build.html)の build.points[].role に対応する。
/// Start は先頭に常に1つ、End は末尾に常に1つ存在する。
/// </summary>
public enum PathPointRole
{
    Start,
    Waypoint,
    End
}

/// <summary>
/// 道路エディタの配置ポイントデータ。
///
/// 各ポイントは三次ベジエの制御点を保持する:
///   control_out ... このポイントから次のポイントへ向かう出口制御点（ベジエのP1）
///   control_in  ... 前のポイントからこのポイントへ入る入口制御点（ベジエのP2）
///
/// 始点は control_out のみ、終点は control_in のみを持つ。
/// ウェイポイントは両方を持つ。
/// Catmull-Romスプラインに基づき自動計算されるが、ドラッグで平面位置(XZ)を手動編集できる。
/// 高さ(Y)は control_height_manual でない限り、アンカーの自動の縦断勾配から決める。
/// </summary>
[System.Serializable]
public struct PathBuildPoint
{
    public Vector3 position;
    public PathPointRole role;

    /// <summary>経路提案が補った点。プレイヤーが移動した点は指定点へ昇格する。</summary>
    public bool proposalGenerated;

    /// <summary>出口制御点。始点とウェイポイントのみ。セグメントのベジエP1に対応。</summary>
    public Vector3? control_out;

    /// <summary>入口制御点。終点とウェイポイントのみ。セグメントのベジエP2に対応。</summary>
    public Vector3? control_in;

    /// <summary>
    /// control_out の平面位置(XZ)がユーザーの手動調整済みなら true。
    /// 自動計算(RecomputeAllControlPoints)はこのフラグが立った制御点のXZを上書きしない。
    /// アンカーを動かしたときはXZを自動計算し直さず、アンカーと同じ量だけ平行移動する。
    /// 高さは control_height_manual でない限り自動計算が決め直す。
    /// </summary>
    public bool control_out_manual;

    /// <summary>control_in の手動調整フラグ。意味は control_out_manual と同じ。</summary>
    public bool control_in_manual;

    /// <summary>
    /// 手動調整済みの制御点の高さ(Y)を固定するなら true。経路提案の結果と、既存パスの再開で
    /// 保存値が自動の縦断勾配と食い違った制御点だけに立て、ハンドルのドラッグでは立てない。
    /// このアンカーか両隣のアンカーを動かすと解除する(自動の縦断勾配が変わるため)。
    /// </summary>
    public bool control_height_manual;

    /// <summary>
    /// 盛土オフセット(m)。-5〜+5。
    /// 道路のY座標自体は0のままだが、着工時にこの高さだけDynamic地形を変形させる。
    /// プレビュー時はこの高さ分だけ道路メッシュを浮かせて表示する。
    /// 切土(マイナス)も可。
    /// </summary>
    public float embankmentHeight;

    public PathBuildPoint(Vector3 pos, PathPointRole r)
    {
        position = pos;
        role = r;
        proposalGenerated = false;
        control_out = null;
        control_in = null;
        control_out_manual = false;
        control_in_manual = false;
        control_height_manual = false;
        embankmentHeight = 0f;
    }
}
