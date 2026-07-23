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
/// Catmull-Romスプラインに基づき自動計算されるが、ドラッグで手動編集可能。
/// </summary>
[System.Serializable]
public struct PathBuildPoint
{
    public Vector3 position;
    public PathPointRole role;

    /// <summary>出口制御点。始点とウェイポイントのみ。セグメントのベジエP1に対応。</summary>
    public Vector3? control_out;

    /// <summary>入口制御点。終点とウェイポイントのみ。セグメントのベジエP2に対応。</summary>
    public Vector3? control_in;

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
        control_out = null;
        control_in = null;
        embankmentHeight = 0f;
    }
}
