using UnityEngine;

/// <summary>
/// 描画実体を持たない歩行者1人の状態。パス幾何(始点→終点)に対する横位置を保持し、
/// 進行方向を反転しても同じ歩道に留まる。交差点・建物との出入りは接続点列で表す。
/// </summary>
public sealed class Pedestrian
{
    public int pathId, variant;
    public bool forward;
    public float s, speed, age;
    public int side;                 // パス幾何に対する左右(+1=右 / -1=左)。中心帯を歩く歩行者専用道路は0
    public float lateral;            // 中心線からの符号付き横位置(m)
    public float surface;            // 点列(スプライン基準面)からの高さ
    public float walked;             // 歩行姿勢の位相に使う累積歩行距離(m)。初期値は個体ごとの乱数で、全員が揃って足を出さないようにする
    public bool moving;              // 直近の移動処理で前進したか。立ち止まっている間は揃い姿勢で描く
    public VehiclePolyline road, connector;
    public float connectorS;
    public bool arrivalPending, fading;
    public float fadeElapsed;
    public float arrivalDistance = -1;
    public int destinationParcel, destinationBuilding;
    public int searchedPath = -1, accessGeneration = -1;
    public Vector3 destinationPoint;
    public bool Entering { get; private set; }
    public bool InConnector => connector != null;
    public bool ConnectorComplete => InConnector && connectorS >= connector.Length - .001f;
    public float TotalLength => road.Length;
    public float Remaining => forward ? TotalLength - s : s;

    public void SetPath(int id, VehiclePolyline points, bool travelForward, float startS = -1)
    {
        pathId = id; road = points; forward = travelForward;
        s = startS < 0 ? (forward ? 0 : TotalLength) : Mathf.Clamp(startS, 0, TotalLength);
    }

    public void Sample(out Vector3 position, out Vector3 tangent)
    {
        if (InConnector) { connector.Sample(connectorS, true, out position, out tangent); return; }
        road.Sample(s, true, out position, out Vector3 geometry);
        position += Vector3.Cross(Vector3.up, geometry).normalized * lateral + Vector3.up * surface;
        tangent = forward ? geometry : -geometry;
    }

    /// <summary>状態の端まで進め、未消費時間を返す。短いパスでも移動時間を捨てない。</summary>
    public float Advance(float seconds)
    {
        float velocity = Mathf.Max(0, speed);
        if (velocity <= .00001f) { age += seconds; return 0; }
        float remaining = InConnector ? connector.Length - connectorS : Remaining;
        if (!InConnector && arrivalDistance >= 0) remaining = Mathf.Min(remaining, arrivalDistance);
        float used = Mathf.Min(seconds, Mathf.Max(0, remaining) / velocity);
        float distance = used * velocity;
        age += used;
        walked += distance;
        if (InConnector) connectorS += distance;
        else
        {
            s = Mathf.Clamp(s + (forward ? distance : -distance), 0, TotalLength);
            if (arrivalDistance >= 0) arrivalDistance = Mathf.Max(0, arrivalDistance - distance);
        }
        return Mathf.Max(0, seconds - used);
    }

    /// <summary>現在位置から敷地内の点へ直線で入る。到達後に縮小して消える。</summary>
    public void BeginEntering(Vector3 destination)
    {
        Sample(out Vector3 start, out _);
        connector = new VehiclePolyline(new[] { start, destination });
        connectorS = 0; Entering = true; arrivalDistance = -1;
    }

    /// <summary>敷地内の点から歩道上の現在位置へ直線で出てくる。到達後は通常の歩行に戻る。</summary>
    public void BeginLeaving(Vector3 origin)
    {
        Sample(out Vector3 target, out _);
        if (Vector3.Distance(origin, target) < .01f) return;
        connector = new VehiclePolyline(new[] { origin, target });
        connectorS = 0;
    }

    public void EndConnector() { connector = null; }
}
