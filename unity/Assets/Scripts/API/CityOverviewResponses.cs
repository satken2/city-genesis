/// <summary>
/// Response from GET /buildings/overview. Represents buildings across the city as distant boxes only.
/// Each column is a little-endian byte array: int32 for integers, float32 for coordinates and heights.
/// Building i's footprint vertices (x, z relative to its center) span polygon_offset[i] to polygon_offset[i+1]. base is the reference elevation for parcels over water, or NaN on land.
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
/// Response from GET /paths/overview. Represents above-ground paths across the city with simplified centerlines only.
/// Columns are little-endian byte arrays: int32 for integers, float32 for coordinates. Path i's centerline (x, z) spans point_offset[i] to point_offset[i+1].
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
