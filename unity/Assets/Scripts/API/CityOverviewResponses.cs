/// <summary>
/// GET /buildings/overview の応答。都市全域の建物を遠景の箱の形だけで表す。
/// 各列は little-endian のバイト列で、整数は int32、座標と高さは float32。
/// 建物 i の底面(中心基準の x, z)は polygon_offset[i] から polygon_offset[i+1] までの頂点。base は海上区画の基準標高で、地上は NaN。
/// </summary>
[System.Serializable]
public class BuildingOverviewResponse
{
    public int count;
    public byte[] agent_id;
    public byte[] variant;
    public byte[] landmark;
    public byte[] floors;
    public byte[] status;
    public byte[] center;
    public byte[] @base;
    public byte[] polygon_offset;
    public byte[] polygon;
}

/// <summary>
/// GET /paths/overview の応答。都市全域の地上のパスを、間引いた中心線だけで表す。
/// 各列は little-endian のバイト列で、整数は int32、座標は float32。パス i の中心線(x, z)は point_offset[i] から point_offset[i+1] までの点。
/// </summary>
[System.Serializable]
public class PathOverviewResponse
{
    public int count;
    public byte[] path_id;
    public byte[] path_variant;
    public byte[] point_offset;
    public byte[] points;
}
