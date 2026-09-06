using UnityEngine;

/// <summary>
/// オブジェクト1つ(建物1棟・パス1本)の描画詳細度。
/// セル単位のデータティア(<see cref="LodTier"/>)とは独立に、カメラからの見え方で決まる。
/// データティアは「どの地形データを持ち、地形追従するか」を、描画LODは「どの細かさで描くか」を
/// 決める。両者を混ぜると、Nearセル(最大1,100m先)の建物にまで接写品質のメッシュが作られる。
/// </summary>
public enum DrawLod
{
    /// <summary>描画しない(画面上で判別できない大きさ。現状はパス・建物では使わず将来のプロップ用)。</summary>
    Hidden = 0,
    /// <summary>遠景: フットプリント押し出しの箱 / 曲線を保った帯。コード生成でモデル不要。</summary>
    Far = 1,
    /// <summary>通常: 無印のパーツで組み立てる。制作の基準であり街並みはこれで成立させる。</summary>
    Normal = 2,
    /// <summary>接写: `{パーツ名}_close` があればそれで組み立てる。無ければ Normal と同じ。</summary>
    Close = 3,
}

/// <summary>
/// 描画LODの判定しきい値。距離ではなく「画面上の大きさ(投影比)」を主基準にする。
/// 投影比 = 境界球半径 ÷ (距離 × tan(FOV/2)) で、画面の縦の高さに対するおおよその占有率。
/// 俯瞰・低視点・ズームのどれでも「見える細部」に描画量を使うため、距離だけでは判定しない。
/// 接近時(昇格)と離脱時(降格)で別のしきい値を持たせ、境界での往復(チラつき)を防ぐ。
/// 値は初期案で、第0期の計測結果で調整する前提。
/// </summary>
public static class DrawLodConfig
{
    /// <summary>Normal→Close に昇格する投影比(これ以上で接写)。</summary>
    public const float CLOSE_ENTER_RATIO = 0.25f;
    /// <summary>Close→Normal に降格する投影比(これ未満で接写解除)。</summary>
    public const float CLOSE_EXIT_RATIO = 0.18f;
    /// <summary>
    /// 接写の距離上限(m)。大きな施設は遠くからでも投影比が大きくなるため、
    /// 投影比だけだと数百m先の工場が接写扱いになる。
    /// </summary>
    public const float CLOSE_MAX_DISTANCE = 150f;
    /// <summary>Far→Normal に昇格する投影比。</summary>
    public const float NORMAL_ENTER_RATIO = 0.045f;
    /// <summary>Normal→Far に降格する投影比。</summary>
    public const float NORMAL_EXIT_RATIO = 0.03f;

    /// <summary>
    /// パスは細長く境界球が意味を持たないため、パス中心線までの最近接距離(m)で判定する。
    /// 接写・通常の距離しきい値(昇格/降格)。
    /// </summary>
    public const float PATH_CLOSE_ENTER_DISTANCE = 120f;
    public const float PATH_CLOSE_EXIT_DISTANCE = 160f;
    public const float PATH_NORMAL_ENTER_DISTANCE = 450f;
    public const float PATH_NORMAL_EXIT_DISTANCE = 550f;

    /// <summary>
    /// 長いパスの詳細描画区間。カメラからこの距離以内の部分だけ詳細パーツを敷き、
    /// 残りは遠景の帯にする(描画区間の分割)。降格距離より少し外側まで詳細にして、
    /// カメラの小さな移動で区間境界が動き続けないようにする。
    /// </summary>
    public const float PATH_DETAIL_RANGE_DISTANCE = 650f;
    /// <summary>この長さ(m)未満のパスは区間分割せず全長を同じ詳細度で描く。</summary>
    public const float PATH_DETAIL_RANGE_MIN_LENGTH = 300f;
    /// <summary>詳細区間の端が、この割合以上ずれたときだけ再生成する(再生成の抑制)。</summary>
    public const float PATH_DETAIL_RANGE_REBUILD_TOLERANCE = 0.1f;

    /// <summary>描画LODの再判定間隔(フレーム)。毎フレーム全件を走査しない。</summary>
    public const int REEVALUATE_INTERVAL_FRAMES = 12;
    /// <summary>カメラがこの距離(m)以上動いたときだけ再判定する。</summary>
    public const float REEVALUATE_CAMERA_MOVE_DISTANCE = 4f;

    /// <summary>接写メッシュを同時に保持する建物の上限。超過分は昇格しない(降格は投影比が決める)。</summary>
    public const int MAX_CLOSE_BUILDINGS = 300;
    /// <summary>接写メッシュを同時に保持するパスの上限。</summary>
    public const int MAX_CLOSE_PATHS = 120;
}

/// <summary>
/// 描画LODの判定器。判定に必要なカメラ情報は呼び出し側が1回だけ取って渡す
/// (数千件の判定で毎回 Camera.main を引かないため)。
/// </summary>
public static class DrawLodEvaluator
{
    /// <summary>カメラの縦FOVの半分の正接。投影比の分母に使う。</summary>
    public static float GetTanHalfFov(Camera cam)
    {
        if (cam == null) return Mathf.Tan(30f * Mathf.Deg2Rad);
        if (cam.orthographic)
        {
            // 正射影は距離で大きさが変わらないので、正射影サイズを「距離1あたりの半高」として扱う。
            return Mathf.Max(0.01f, cam.orthographicSize);
        }
        return Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
    }

    /// <summary>境界球半径と距離から投影比を求める。</summary>
    public static float ProjectedRatio(float radius, float distance, float tanHalfFov)
    {
        float d = Mathf.Max(1f, distance);
        return radius / (d * tanHalfFov);
    }

    /// <summary>
    /// 建物など「境界球で大きさが表せる」オブジェクトの描画LODを判定する。
    /// current は現在の描画LOD(ヒステリシス用)。allowClose=false なら接写へ昇格しない
    /// (接写の保持上限に達しているとき)。Hidden は返さない(建物は遠景でもスカイラインを保つ)。
    /// </summary>
    public static DrawLod EvaluateBySize(float radius, float distance, float tanHalfFov, DrawLod current, bool allowClose)
    {
        float ratio = ProjectedRatio(radius, distance, tanHalfFov);

        bool closeNow;
        if (current == DrawLod.Close)
        {
            closeNow = ratio >= DrawLodConfig.CLOSE_EXIT_RATIO && distance <= DrawLodConfig.CLOSE_MAX_DISTANCE * 1.15f;
        }
        else
        {
            closeNow = allowClose && ratio >= DrawLodConfig.CLOSE_ENTER_RATIO && distance <= DrawLodConfig.CLOSE_MAX_DISTANCE;
        }
        if (closeNow) return DrawLod.Close;

        bool normalNow = current >= DrawLod.Normal
            ? ratio >= DrawLodConfig.NORMAL_EXIT_RATIO
            : ratio >= DrawLodConfig.NORMAL_ENTER_RATIO;
        return normalNow ? DrawLod.Normal : DrawLod.Far;
    }

    /// <summary>
    /// パスの描画LODを、パス中心線までの最近接距離で判定する。Hidden は返さない。
    /// </summary>
    public static DrawLod EvaluatePathByDistance(float distance, DrawLod current, bool allowClose)
    {
        bool closeNow = current == DrawLod.Close
            ? distance <= DrawLodConfig.PATH_CLOSE_EXIT_DISTANCE
            : allowClose && distance <= DrawLodConfig.PATH_CLOSE_ENTER_DISTANCE;
        if (closeNow) return DrawLod.Close;

        bool normalNow = current >= DrawLod.Normal
            ? distance <= DrawLodConfig.PATH_NORMAL_EXIT_DISTANCE
            : distance <= DrawLodConfig.PATH_NORMAL_ENTER_DISTANCE;
        return normalNow ? DrawLod.Normal : DrawLod.Far;
    }

    /// <summary>点列(XZ)へのカメラの最近接距離(3D)。線分ごとに最近接点を取る。</summary>
    public static float MinDistanceToPolyline(System.Collections.Generic.IList<Vector3> points, Vector3 camPos)
    {
        if (points == null || points.Count == 0) return float.MaxValue;
        if (points.Count == 1) return Vector3.Distance(points[0], camPos);
        float best = float.MaxValue;
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-6f ? Mathf.Clamp01(Vector3.Dot(camPos - a, ab) / len2) : 0f;
            float d = Vector3.Distance(a + ab * t, camPos);
            if (d < best) best = d;
        }
        return best;
    }
}

/// <summary>
/// 生成済みオブジェクトが「どの描画LODで、パスならどの区間を詳細に描いたか」を保持する。
/// 再判定時に現在値との差分だけを再生成するために使う。<see cref="ObjectIdInfo"/> と同じく
/// GameObject名をパースせず、このコンポーネントから読む。
/// </summary>
public class DrawLodInfo : MonoBehaviour
{
    public DrawLod drawLod = DrawLod.Normal;
    /// <summary>パスの詳細描画区間(スプライン割合)。全長なら 0〜1。建物では未使用。</summary>
    public float detailFrom = 0f;
    public float detailTo = 1f;

    public bool HasPartialDetailRange => detailFrom > 0.0001f || detailTo < 0.9999f;

    public static DrawLod GetDrawLod(GameObject go, DrawLod fallback)
    {
        if (go == null) return fallback;
        var info = go.GetComponent<DrawLodInfo>();
        return info != null ? info.drawLod : fallback;
    }

    public static DrawLodInfo Ensure(GameObject go)
    {
        var info = go.GetComponent<DrawLodInfo>();
        if (info == null) info = go.AddComponent<DrawLodInfo>();
        return info;
    }
}
