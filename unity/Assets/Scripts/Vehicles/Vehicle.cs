using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 道路上を走る車1台の走行状態を保持する軽量クラス（MonoBehaviour ではない）。
/// パスの中心線(polyline)上を距離パラメータ s で進み、交差点(ノード)で次のパスへ乗り換える。
/// 実体の描画は go(GameObject) が担い、生成/破棄/プールは <see cref="VehicleManager"/> が行う。
/// </summary>
public class Vehicle
{
    // 現在走行中のパスID。
    public int pathId;
    // ジオメトリ方向(start_point→end_point)に沿って進むなら true。
    public bool forward;
    // 中心線上の始点(start)からの距離[m]。0..TotalLength。進行は forward で増減する。
    public float s;
    // 走行速度[m/s]。
    public float speed;
    // 車種(Constants.VEHICLE_*)。将来の車種別モデル/交通量重み付けに使う。
    public int vehicleType;
    // 描画に使っているモデルのバリアントID(プール返却時のキーに使う)。
    public int variantId;
    // 描画実体。
    public GameObject go;

    // 現在パスの地形追従済み中心線(ジオメトリ順: start→end)。
    private List<Vector3> polyline;
    // polyline[0] からの累積距離。cumulative[i] は点 i までの距離。長さは polyline.Count。
    private readonly List<float> cumulative = new List<float>();

    public float TotalLength => cumulative.Count > 0 ? cumulative[cumulative.Count - 1] : 0f;

    /// <summary>
    /// 走行対象のパスを設定する。polyline はジオメトリ順(start→end)の点列。
    /// startS に負値を渡すと「進行向きの入口端」(forward なら 0 / backward なら TotalLength)に置く。
    /// 0以上を渡すとその始点距離[m](範囲境界からのスポーン等)に置く。
    /// </summary>
    public void SetPath(int newPathId, List<Vector3> newPolyline, bool travelForward, float startS = -1f)
    {
        pathId = newPathId;
        polyline = newPolyline;
        forward = travelForward;

        cumulative.Clear();
        float acc = 0f;
        cumulative.Add(0f);
        for (int i = 1; i < polyline.Count; i++)
        {
            acc += Vector3.Distance(polyline[i - 1], polyline[i]);
            cumulative.Add(acc);
        }

        s = startS < 0f ? (forward ? 0f : TotalLength) : Mathf.Clamp(startS, 0f, TotalLength);
    }

    /// <summary>現在の s に応じてワールド位置と「進行方向」の接線を返す。</summary>
    public void Sample(out Vector3 position, out Vector3 travelTangent)
    {
        float clamped = Mathf.Clamp(s, 0f, TotalLength);

        // clamped が入る区間 [i-1, i] を線形探索(点数は数十程度)。
        int seg = 1;
        while (seg < cumulative.Count && cumulative[seg] < clamped)
        {
            seg++;
        }
        if (seg >= cumulative.Count) seg = cumulative.Count - 1;

        Vector3 a = polyline[seg - 1];
        Vector3 b = polyline[seg];
        float segLen = cumulative[seg] - cumulative[seg - 1];
        float t = segLen > 1e-4f ? (clamped - cumulative[seg - 1]) / segLen : 0f;

        position = Vector3.Lerp(a, b, t);
        // ジオメトリ方向の接線に、進行向き(forward)を掛けたものが「進行方向」。
        Vector3 geoTangent = (b - a);
        travelTangent = (forward ? geoTangent : -geoTangent);
        if (travelTangent.sqrMagnitude < 1e-6f)
        {
            travelTangent = forward ? Vector3.forward : Vector3.back;
        }
        travelTangent.Normalize();
    }

    /// <summary>進行方向に s を進める。パス端を越えたら true(=ノード到達)を返す。</summary>
    public bool Advance(float dt)
    {
        s += (forward ? 1f : -1f) * speed * dt;
        return forward ? (s >= TotalLength) : (s <= 0f);
    }

    /// <summary>進行方向の先にあるノード(到達したノード)。forward なら end、backward なら start。</summary>
    public int AheadNodeId(int startNodeId, int endNodeId) => forward ? endNodeId : startNodeId;
}
