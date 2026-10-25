using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Utilities converting GeneratedModels coordinate data (List&lt;object&gt;) into frontend-friendly types.
/// Backend API responses send coordinates as List&lt;object&gt;,
/// so convert them here to float[] or float[][] before use.
/// Newtonsoft.Json may retain nested arrays and numbers as JArray / JValue when deserializing to List&lt;object&gt;,
/// so handle those representations here as well.
/// </summary>
public static class GeneratedModelUtils
{
    /// <summary>
    /// Convert a GeoJSON ring [[x, z], [x, z], ...] to Vector2 points with x=X and y=Z.
    /// Return an empty list for null or non-array inputs; callers validate point counts.
    /// Shared entry point for polygons received as raw JSON, including water surfaces and effect ranges.
    /// </summary>
    public static List<Vector2> ParseRing(JToken ring)
    {
        if (!(ring is JArray arr)) return new List<Vector2>();
        var pts = new List<Vector2>(arr.Count);
        foreach (JToken p in arr)
        {
            if (p is JArray pa && pa.Count >= 2) pts.Add(new Vector2((float)pa[0], (float)pa[1]));
        }
        return pts;
    }

    /// <summary>
    /// Convert List&lt;object&gt; to float[].
    /// Used for one-dimensional arrays representing a point, such as [x, y] or [x, y, z].
    /// </summary>
    public static float[] ToFloatArray(List<object> list)
    {
        if (list == null) return null;
        float[] result = new float[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            result[i] = ToFloat(list[i]);
        }
        return result;
    }

    /// <summary>
    /// Convert JArray returned by Json.NET for nested arrays to float[].
    /// For one-dimensional arrays containing only numeric tokens, such as center_point.
    /// </summary>
    public static float[] ToFloatArray(JArray arr)
    {
        if (arr == null) return null;
        float[] result = new float[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            result[i] = ToFloat(arr[i]);
        }
        return result;
    }

    /// <summary>
    /// Convert List&lt;object&gt; to List&lt;float&gt;.
    /// Used where List&lt;float&gt; is expected, such as start_point and end_point.
    /// </summary>
    public static List<float> ToFloatList(List<object> list)
    {
        if (list == null) return null;
        List<float> result = new List<float>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            result.Add(ToFloat(list[i]));
        }
        return result;
    }

    /// <summary>
    /// Convert polygon data, a List&lt;object&gt; of List&lt;object&gt; elements, to float[][].
    /// Used for two-dimensional arrays such as polygon = [[x1,y1], [x2,y2], ...].
    /// </summary>
    public static float[][] ToFloatJaggedArray(List<object> list)
    {
        if (list == null) return null;
        float[][] result = new float[list.Count][];
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is JArray jArr)
            {
                result[i] = ToFloatArray(jArr);
            }
            else if (list[i] is List<object> inner)
            {
                result[i] = ToFloatArray(inner);
            }
            else if (list[i] is IEnumerable<object> enumerable)
            {
                var tempList = new List<float>();
                foreach (var item in enumerable)
                {
                    tempList.Add(ToFloat(item));
                }
                result[i] = tempList.ToArray();
            }
            else
            {
                result[i] = new float[0];
            }
        }
        return result;
    }

    /// <summary>
    /// Convert the waypoint list to PathWaypoint entries.
    /// The backend returns only control-point dictionaries normalized by coordinate_utils.normalize_waypoints
    ///   [{"position":[x,y,z], "control_in":[x,y,z], "control_out":[x,y,z]}, ...]
    /// whose elements are JObject via JSON or Dictionary&lt;string, object&gt; via msgpack,
    /// so accept only those two representations.
    /// </summary>
    public static List<PathWaypoint> ToWaypointList(List<object> list)
    {
        if (list == null) return null;
        List<PathWaypoint> result = new List<PathWaypoint>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is JObject wpObj)
            {
                var wp = new PathWaypoint();

                JToken posToken = wpObj["position"];
                if (posToken is JArray posArr)
                    wp.position = ToFloatArray(posArr);
                else if (posToken != null)
                    wp.position = ToFloatArray(posToken.ToObject<List<object>>());

                JToken ciToken = wpObj["control_in"];
                if (ciToken is JArray ciArr && ciArr.Count >= 2)
                    wp.control_in = ToFloatArray(ciArr);

                JToken coToken = wpObj["control_out"];
                if (coToken is JArray coArr && coArr.Count >= 2)
                    wp.control_out = ToFloatArray(coArr);

                result.Add(wp);
            }
            else if (list[i] is Dictionary<string, object> dict)
            {
                // When JsonConvert.DeserializeObject produces Dictionary<string, object>.
                var wp = new PathWaypoint();

                if (dict.TryGetValue("position", out object posObj))
                {
                    wp.position = ToFloatArrayFromObject(posObj);
                }
                if (dict.TryGetValue("control_in", out object ciObj) && ciObj != null)
                {
                    wp.control_in = ToFloatArrayFromObject(ciObj);
                }
                if (dict.TryGetValue("control_out", out object coObj) && coObj != null)
                {
                    wp.control_out = ToFloatArrayFromObject(coObj);
                }

                result.Add(wp);
            }
        }
        return result;
    }

    /// <summary>
    /// Convert object to float[].
    /// Accept List&lt;object&gt;, JArray, and other IEnumerable values.
    /// </summary>
    private static float[] ToFloatArrayFromObject(object obj)
    {
        if (obj == null) return null;
        if (obj is List<object> list) return ToFloatArray(list);
        if (obj is JArray jarr) return ToFloatArray(jarr);
        if (obj is IEnumerable<object> enumerable)
        {
            List<float> result = new List<float>();
            foreach (var item in enumerable)
            {
                result.Add(ToFloat(item));
            }
            return result.ToArray();
        }
        return null;
    }

    /// <summary>
    /// Convert object to float. After JSON deserialization, numbers may be
    /// long, double, int, float, or other numeric types, so handle all cases.
    /// Also interpret Json.NET JValue / JToken as their contained numeric values.
    /// </summary>
    public static float ToFloat(object obj)
    {
        if (obj == null) return 0f;
        if (obj is JToken jt && jt.Type != JTokenType.Null && jt.Type != JTokenType.Undefined)
        {
            switch (jt.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return jt.Value<float>();
                case JTokenType.String:
                    if (float.TryParse(jt.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out float jParsed))
                        return jParsed;
                    break;
            }
        }
        if (obj is long l) return (float)l;
        if (obj is double d) return (float)d;
        if (obj is float f) return f;
        if (obj is int i) return (float)i;
        if (obj is string s && float.TryParse(s, out float parsed)) return parsed;
        Debug.LogWarning($"GeneratedModelUtils: floatへの変換に失敗: {obj} ({obj.GetType().Name})");
        return 0f;
    }

    /// <summary>
    /// Extract enabled building type IDs from the allowed_bd_mask bit flags.
    /// District building permissions use a bitmask,
    /// so return the indices of set bits as a list.
    /// </summary>
    /// <summary>
    /// Extract enabled building type IDs from the allowed_bd_mask bit flags.
    /// District building permissions use a bitmask,
    /// so return the indices of set bits as a list.
    /// Building type IDs range from 1 to 512 (bits 0 through 9),
    /// so ignore higher bits to prevent unnecessary icon lookups.
    /// </summary>
    public static List<int> ExtractBuildingTypes(int mask)
    {
        List<int> types = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                types.Add(1 << i);
            }
        }
        return types;
    }
}

/// <summary>
/// Extension methods for GeneratedModels.PathSummary.
/// Add coordinate conversions without changing generated files.
/// </summary>
public static class PathSummaryExtensions
{
    // Converted saved design elevations, cached by source list because each response creates a new list.
    // Height lookups run per sample in grade-separation checks and snapping; converting on every lookup would make them take seconds.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<object>, List<List<float>>> stationGradeProfiles = new();

    /// <summary>Share conversion of a received point list between rendering and snapping. New data converts from a new list.</summary>
    public static List<List<float>> GetStationGradeProfile(this GeneratedModels.PathSummary p)
    {
        return stationGradeProfiles.GetValue(p.station_grade_profile,
            source => Newtonsoft.Json.Linq.JArray.FromObject(source).ToObject<List<List<float>>>());
    }

    /// <summary>
    /// Convert start_point to List&lt;float&gt;.
    /// </summary>
    public static List<float> GetStartPointList(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToFloatList(p.start_point);
    }

    /// <summary>
    /// Convert end_point to List&lt;float&gt;.
    /// </summary>
    public static List<float> GetEndPointList(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToFloatList(p.end_point);
    }

    /// <summary>
    /// Convert waypoints to a list of PathWaypoint.
    /// </summary>
    public static List<PathWaypoint> GetWaypointList(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToWaypointList(p.waypoints);
    }

    /// <summary>
    /// Return relative height above terrain at the specified XZ position along the path centerline.
    /// Linearly interpolate relative Y between the nearest XZ anchor segment (start -> waypoints -> end).
    /// If saved design elevations exist, prefer that reference line and return its difference from Utils terrain elevation.
    /// Always use this method when inheriting path elevation through snapping.
    /// Do not reconstruct it as rendered-polyline absolute Y minus a terrain sample: terrain variation within polyline chords,
    /// render offsets, and differing height sources (CPU array / Terrain.SampleHeight) all introduce elevation noise,
    /// which can classify endpoints as underground or elevated merely by snapping to a ground-level path (relative Y=0).
    /// Even variants with base_height, such as power lines, return the backend's relative Y instead of rendered Y.
    /// </summary>
    public static float GetRelativeHeightAtPoint(this GeneratedModels.PathSummary p, float x, float z)
    {
        if (p.station_grade_profile != null && p.station_grade_profile.Count >= 2)
        {
            var profile = p.GetStationGradeProfile();
            return PathManager.EvaluateStationGrade(profile, new Vector3(x, 0f, z)) - Utils.GetGroundHeight(new Vector3(x, 0f, z));
        }

        var anchorX = new List<float>();
        var anchorZ = new List<float>();
        var anchorRel = new List<float>();

        void AddAnchor(List<float> pt)
        {
            if (pt == null || pt.Count < 2) return;
            anchorX.Add(pt[0]);
            anchorZ.Add(pt[pt.Count - 1]);
            anchorRel.Add(pt.Count >= 3 ? pt[1] : 0f);
        }

        AddAnchor(p.GetStartPointList());
        var waypoints = p.GetWaypointList();
        if (waypoints != null)
        {
            foreach (var wp in waypoints)
            {
                if (wp?.position == null || wp.position.Length < 2) continue;
                anchorX.Add(wp.position[0]);
                anchorZ.Add(wp.position[wp.position.Length - 1]);
                anchorRel.Add(wp.GetRelativeHeight());
            }
        }
        AddAnchor(p.GetEndPointList());

        if (anchorRel.Count == 0) return 0f;
        if (anchorRel.Count == 1) return anchorRel[0];

        var pXZ = new Vector2(x, z);
        float bestDistSqr = float.MaxValue;
        float bestRel = anchorRel[0];
        for (int i = 0; i < anchorRel.Count - 1; i++)
        {
            var a = new Vector2(anchorX[i], anchorZ[i]);
            var b = new Vector2(anchorX[i + 1], anchorZ[i + 1]);
            float t = PolylineUtils.ProjectOnSegment(pXZ, a, b);
            Vector2 closest = Vector2.Lerp(a, b, t);
            float distSqr = (pXZ - closest).sqrMagnitude;
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                bestRel = Mathf.Lerp(anchorRel[i], anchorRel[i + 1], t);
            }
        }
        return bestRel;
    }

    /// <summary>
    /// Convert center_point to float[].
    /// </summary>
    public static float[] GetCenterPointArray(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToFloatArray(p.center_point);
    }

    /// <summary>
    /// Convert polygon to float[][].
    /// </summary>
    public static float[][] GetPolygonArray(this GeneratedModels.PathSummary p)
    {
        return GeneratedModelUtils.ToFloatJaggedArray(p.polygon);
    }

    /// <summary>
    /// Read platform_side (station-path platform placement; Constants.PLATFORM_SIDE_*)
    /// through reflection so compilation and execution still work when endpoint_schemas
    /// has not been updated and GeneratedModels.cs lacks the field,
    /// following VehicleManager's path_traffic pattern. While the field is absent,
    /// return the default PLATFORM_SIDE_OUTSIDE (outside).
    /// Regenerating the schema automatically enables real data; direct field access can then replace reflection.
    /// </summary>
    private static readonly System.Reflection.FieldInfo PlatformSideField =
        typeof(GeneratedModels.PathSummary).GetField("platform_side");

    public static int GetPlatformSide(this GeneratedModels.PathSummary p)
    {
        if (p == null || PlatformSideField == null) return Constants.PLATFORM_SIDE_OUTSIDE;
        object value = PlatformSideField.GetValue(p);
        if (value is int i) return i;
        if (value is long l) return (int)l;
        return Constants.PLATFORM_SIDE_OUTSIDE;
    }

    /// <summary>
    /// Read start_control / end_control (endpoint Bezier control points [x, relative height, z])
    /// through reflection. As with GetPlatformSide, this keeps compilation and execution working
    /// when endpoint_schemas has not been updated and GeneratedModels.cs lacks the fields.
    /// Return null while a field is absent or its value is null;
    /// callers such as PathManager.CalculatePathPoints retain the existing fallback
    /// that computes controls using the same formula as backend _auto_fill_controls.
    /// </summary>
    private static readonly System.Reflection.FieldInfo StartControlField =
        typeof(GeneratedModels.PathSummary).GetField("start_control");
    private static readonly System.Reflection.FieldInfo EndControlField =
        typeof(GeneratedModels.PathSummary).GetField("end_control");

    public static List<float> GetStartControlList(this GeneratedModels.PathSummary p)
    {
        if (p == null || StartControlField == null) return null;
        return GeneratedModelUtils.ToFloatList(StartControlField.GetValue(p) as List<object>);
    }

    public static List<float> GetEndControlList(this GeneratedModels.PathSummary p)
    {
        if (p == null || EndControlField == null) return null;
        return GeneratedModelUtils.ToFloatList(EndControlField.GetValue(p) as List<object>);
    }

    /// <summary>
    /// Return follow_terrain: per-path terrain following (-1=unset, variant default; 0=off; 1=on).
    /// PathManager.GetPathFollowTerrain(PathSummary) resolves the effective value, applying the variant default when unset.
    /// Reflection would silently treat a generated-model type mismatch as unset,
    /// so access the field directly to make type mismatches compilation errors.
    /// </summary>
    public static int GetFollowTerrain(this GeneratedModels.PathSummary p)
    {
        return p != null ? p.follow_terrain : Constants.VALUE_NONE;
    }
}

/// <summary>
/// Extension methods for GeneratedModels.ParcelSummary.
/// Add coordinate conversions without changing generated files.
/// </summary>
public static class ParcelSummaryExtensions
{
    /// <summary>
    /// Convert center_point to float[].
    /// </summary>
    public static float[] GetCenterPointArray(this GeneratedModels.ParcelSummary p)
    {
        return GeneratedModelUtils.ToFloatArray(p.center_point);
    }

    /// <summary>
    /// Convert polygon to float[][].
    /// </summary>
    public static float[][] GetPolygonArray(this GeneratedModels.ParcelSummary p)
    {
        return GeneratedModelUtils.ToFloatJaggedArray(p.polygon);
    }

    /// <summary>
    /// Read front_path_id (fronting street ID; 0 means no road access) through reflection.
    /// As with PathSummaryExtensions.GetPlatformSide, this keeps compilation and execution working
    /// when multi_parcel in endpoint_schemas has not been updated and GeneratedModels.cs ParcelSummary lacks the field.
    /// Return 0 while the field or its value is absent,
    /// so callers looking up area parcels by fronting street behave as if no area parcel exists.
    /// Regenerating the schema automatically enables real data; direct field access can then replace reflection.
    /// </summary>
    private static readonly System.Reflection.FieldInfo FrontPathIdField =
        typeof(GeneratedModels.ParcelSummary).GetField("front_path_id");

    public static int GetFrontPathId(this GeneratedModels.ParcelSummary p)
    {
        if (p == null || FrontPathIdField == null) return 0;
        object value = FrontPathIdField.GetValue(p);
        if (value is int i) return i;
        if (value is long l) return (int)l;
        return 0;
    }
}

/// <summary>
/// Extension methods for GeneratedModels.ZoneData.
/// Add coordinate conversions without changing generated files.
/// </summary>
public static class ZoneDataExtensions
{
    /// <summary>
    /// Convert polygon to float[][].
    /// </summary>
    public static float[][] GetPolygonArray(this GeneratedModels.ZoneData z)
    {
        return GeneratedModelUtils.ToFloatJaggedArray(z.polygon);
    }

    /// <summary>
    /// Convert center_point to float[].
    /// </summary>
    public static float[] GetCenterPointArray(this GeneratedModels.ZoneData z)
    {
        return GeneratedModelUtils.ToFloatArray(z.center_point);
    }
}
