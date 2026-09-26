using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// 海岸線（陸地の境界ポリゴン）を保持する静的ストア。
///
/// バックエンドの <c>GET /api/v1/terrain/coastline</c>（GeoJSON ライク）を
/// <see cref="TerrainManager.FetchCoastline"/> が取得し、ここに格納する。
/// 座標はバックエンドのワールド座標（マップ中心が原点）の X-Z 平面で、
/// リング（外周・穴を区別せず）の頂点列として保持する。
///
/// 港湾（MODEL_PORT）の岸壁メッシュ生成（<see cref="PortBuildingMeshBuilder"/>）で
/// 「底面ポリゴンのどの辺が海岸線に接しているか」を判定するために使う。
/// 海岸線は far_map ベースで init_game 時に確定し以降不変なので、
/// セッション開始時に一度ロードすればよい。
/// </summary>
public static class CoastlineStore
{
    // 各リング。各リングは閉路の頂点列（始点と終点が一致していてもしていなくてもよい）。
    // Vector2 の x=ワールドX、y=ワールドZ。
    private static readonly List<Vector2[]> rings = new List<Vector2[]>();

    /// <summary>海岸線データの取得を試みた（成功・空を問わず）かどうか。</summary>
    public static bool IsLoaded { get; private set; }

    /// <summary>陸地境界リングが1つ以上存在するかどうか。</summary>
    public static bool HasCoastline => rings.Count > 0;

    /// <summary>
    /// 海岸線データの世代番号。SetFromGeoJson / Clear のたびに増える。
    /// 海岸線から導出したジオメトリ（岸壁クリップ用の陸地領域など）のキャッシュ無効化に使う。
    /// </summary>
    public static int Version { get; private set; }

    /// <summary>保持しているリングの読み取り専用ビュー。Vector2 の x=ワールドX、y=ワールドZ。</summary>
    public static IReadOnlyList<Vector2[]> Rings => rings;

    /// <summary>保持しているデータを破棄する。セッション切り替え時などに呼ぶ。</summary>
    public static void Clear()
    {
        rings.Clear();
        IsLoaded = false;
        Version++;
    }

    /// <summary>
    /// GeoJSON ライクな coordinates（Polygon または MultiPolygon）からリング集合を構築する。
    /// Polygon は <c>[ring, hole, ...]</c>、MultiPolygon は <c>[[ring, hole, ...], ...]</c> という
    /// ネスト構造になるが、ネストの深さに依存せず「点（<c>[x, z]</c>）の配列」を
    /// リングとして再帰的に収集することで両者をまとめて扱う。
    /// coordinates が空（海岸線なし）の場合はリング0件でロード完了扱いとする。
    /// </summary>
    public static void SetFromGeoJson(JToken coordinates)
    {
        rings.Clear();
        if (coordinates != null)
        {
            CollectRings(coordinates, rings);
        }
        IsLoaded = true;
        Version++;
    }

    private static void CollectRings(JToken token, List<Vector2[]> output)
    {
        if (!(token is JArray array) || array.Count == 0)
        {
            return;
        }

        // 先頭要素が「点（数値2つ以上の配列）」なら、この array 自体が1つのリング。
        if (IsPoint(array[0]))
        {
            Vector2[] ring = new Vector2[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                JToken p = array[i];
                ring[i] = new Vector2((float)p[0], (float)p[1]);
            }
            output.Add(ring);
            return;
        }

        // それ以外は1段深く潜る。
        foreach (JToken child in array)
        {
            CollectRings(child, output);
        }
    }

    private static bool IsPoint(JToken token)
    {
        return token is JArray arr && arr.Count >= 2 &&
               (arr[0].Type == JTokenType.Float || arr[0].Type == JTokenType.Integer);
    }

    /// <summary>
    /// 指定したワールド座標(x, z; バックエンド座標)が、海岸線の境界線分から
    /// <paramref name="threshold"/> メートル以内にあるかを判定する。
    /// 閾値内の線分が見つかった時点で早期 return する。
    /// 海岸線が未ロード、または陸地が存在しない場合は false を返す。
    /// </summary>
    public static bool IsNearCoastline(float x, float z, float threshold)
    {
        if (rings.Count == 0)
        {
            return false;
        }

        float thresholdSq = threshold * threshold;
        for (int r = 0; r < rings.Count; r++)
        {
            Vector2[] ring = rings[r];
            int count = ring.Length;
            if (count < 2)
            {
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % count];
                if (PolylineUtils.DistanceToSegmentSq(new Vector2(x, z), a, b) <= thresholdSq)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 指定したワールド座標(x, z; バックエンド座標)が陸地の内側にあるかを判定する。
    ///
    /// 全リング(外周・穴を区別せず)に対する even-odd(交差回数の偶奇)ルールで判定する。
    /// バックエンドの海岸線は外周リングと湖などの穴リングをまとめて持つが、
    /// すべてのリングを通して交差回数を数えると、外周のみの内側=奇数=陸地、
    /// 外周かつ穴の内側=偶数=海(湖)、と穴が自然に扱われる。
    /// 海岸線が未ロード、または陸地が存在しない場合は false を返す(呼び出し側でフォールバック)。
    /// </summary>
    public static bool IsLand(float x, float z)
    {
        if (rings.Count == 0)
        {
            return false;
        }

        // 複数リングの偶奇合成: 外周で内側、その中の穴(湖)で再び外側になる。
        Vector2 point = new Vector2(x, z);
        bool inside = false;
        for (int r = 0; r < rings.Count; r++)
        {
            if (rings[r].Length < 3) continue;
            if (PolygonUtils.Contains(rings[r], point)) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 指定したワールド座標(x, z; バックエンド座標)から海岸線までの符号付き距離を返す。
    /// 陸地内が正、海(陸地外)が負。絶対値は全リングの全辺への最短距離。
    /// 1点ごとに全辺を走査するので、格子全体を求めるときは <see cref="SampleSignedDistanceGrid"/> を使う。
    /// 海岸線が未ロード/陸地なしの場合は負の無限大を返す(=全域を海とみなす)。
    /// </summary>
    public static float SignedDistance(float x, float z)
    {
        if (rings.Count == 0)
        {
            return float.NegativeInfinity;
        }

        float minDistSq = float.PositiveInfinity;
        for (int r = 0; r < rings.Count; r++)
        {
            Vector2[] ring = rings[r];
            int count = ring.Length;
            if (count < 2)
            {
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % count];
                float dSq = PolylineUtils.DistanceToSegmentSq(new Vector2(x, z), a, b);
                if (dSq < minDistSq)
                {
                    minDistSq = dSq;
                }
            }
        }

        float dist = Mathf.Sqrt(minDistSq);
        return IsLand(x, z) ? dist : -dist;
    }

    /// <summary>
    /// 格子点 (xs[i], zs[j]) ごとの符号付き距離を [i, j] に並べて返す。xs・zs は昇順で渡す。
    /// 符号は <see cref="IsLand"/>、絶対値は <see cref="SignedDistance"/> と同じ値で、maxDistance より遠い点だけ
    /// ±maxDistance に丸める。格子点ごとに SignedDistance を呼ぶと格子点数×海岸線の頂点数に比例して遅いため、
    /// 内外は行ごとの半直線の交差、距離は近傍の辺だけから求める。
    /// </summary>
    public static float[,] SampleSignedDistanceGrid(float[] xs, float[] zs, float maxDistance)
    {
        var result = new float[xs.Length, zs.Length];
        // 内外と距離で2回走査するので、途中で差し替わっても同じリング集合を使う。
        Vector2[][] snapshot = rings.ToArray();

        var crossings = new List<float>();
        for (int j = 0; j < zs.Length; j++)
        {
            crossings.Clear();
            foreach (Vector2[] ring in snapshot)
            {
                if (ring.Length >= 3) PolygonUtils.AppendRayCrossings(ring, zs[j], crossings);
            }
            crossings.Sort();
            int passed = 0;
            for (int i = 0; i < xs.Length; i++)
            {
                while (passed < crossings.Count && crossings[passed] <= xs[i]) passed++;
                result[i, j] = ((crossings.Count - passed) & 1) == 1 ? maxDistance : -maxDistance;
            }
        }

        // 辺を maxDistance 以下の間隔の点で登録する。辺上のどの点も最寄りの登録点から maxDistance/2 以内なので、
        // 半径 reach の候補に maxDistance 以内の全辺が入る。
        float reach = maxDistance * 1.5f;
        var segments = new List<(Vector2 start, Vector2 end)>();
        var hash = new SpatialHash<int>(reach);
        foreach (Vector2[] ring in snapshot)
        {
            int count = ring.Length;
            if (count < 2) continue;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % count];
                int samples = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / maxDistance));
                for (int k = 0; k < samples; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, (k + 0.5f) / samples);
                    hash.Add(segments.Count, new Vector3(p.x, 0f, p.y));
                }
                segments.Add((a, b));
            }
        }

        float maxDistanceSq = maxDistance * maxDistance;
        for (int i = 0; i < xs.Length; i++)
        {
            for (int j = 0; j < zs.Length; j++)
            {
                var point = new Vector2(xs[i], zs[j]);
                float minDistanceSq = maxDistanceSq;
                foreach (int index in hash.Query(new Vector3(point.x, 0f, point.y), reach))
                {
                    float distanceSq = PolylineUtils.DistanceToSegmentSq(point, segments[index].start, segments[index].end);
                    if (distanceSq < minDistanceSq) minDistanceSq = distanceSq;
                }
                float distance = Mathf.Sqrt(minDistanceSq);
                result[i, j] = result[i, j] > 0f ? distance : -distance;
            }
        }
        return result;
    }

    /// <summary>
    /// 全リング頂点のワールド座標(バックエンド座標)のAABBを返す。
    /// 陸地メッシュ生成のグリッド走査範囲を陸地の存在範囲に限定するために使う。
    /// リングが1つも無い場合は false。
    /// </summary>
    public static bool TryGetBounds(out float minX, out float minZ, out float maxX, out float maxZ)
    {
        minX = float.PositiveInfinity;
        minZ = float.PositiveInfinity;
        maxX = float.NegativeInfinity;
        maxZ = float.NegativeInfinity;
        bool any = false;
        for (int r = 0; r < rings.Count; r++)
        {
            Vector2[] ring = rings[r];
            for (int i = 0; i < ring.Length; i++)
            {
                Vector2 p = ring[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
                any = true;
            }
        }
        return any;
    }
}
