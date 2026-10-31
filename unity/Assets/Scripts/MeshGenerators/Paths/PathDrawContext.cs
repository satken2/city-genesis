using System.Collections.Generic;
using UnityEngine;
using Dreamteck.Splines;

/// <summary>
/// Rendering settings for one path mesh generation (LOD, detail intervals, curvature-adaptive tile length).
/// Generation is synchronous on the main thread; caller (PathManager) invokes
/// <see cref="Begin"/> / <see cref="End"/> around generation, and generators read static current values.
/// All part placement enters via RoadPath.CreateSplineMesh; this context applies to
/// every generator (roads/tracks/station houses/intersection connectors) without passing extra arguments.
/// It does not read the cell tier table, respecting the rule against generator-side tier-table lookups.
/// </summary>
public static class PathDrawContext
{
    /// <summary>Rendering LOD of the current path. Default Normal (generation outside context remains unchanged).</summary>
    public static DrawLod CurrentDrawLod { get; private set; } = DrawLod.Normal;

    /// <summary>
    /// Detail-part interval (spline fractions). Callers fill outside with distant models.
    /// Full length is 0–1. Generators with separate splines per segment, such as bent roads,
    /// have incompatible fraction references; disable via <see cref="SuppressDetailRange"/>.
    /// </summary>
    public static double DetailFrom { get; private set; } = 0.0;
    public static double DetailTo { get; private set; } = 1.0;
    public static bool SuppressDetailRange { get; set; }

    /// <summary>
    /// Curvature-adaptive tile length for the current spline (m). Nonpositive means uncomputed; use the existing 1m cap.
    /// Generators set this through <see cref="SetAdaptiveTileLength"/> immediately after BuildSpline.
    /// </summary>
    public static float AdaptiveTileLength { get; private set; }

    /// <summary>
    /// Deformation for the next RoadPath.CreateSplineMesh part (lateral offset/width scale by spline fraction).
    /// Track-end S curves (TrackEndTransition) shift exterior track parts sideways and resize beds/girders.
    /// Generators set this immediately before part calls and reset to null afterward. Deferred section LOD (SplinePartLod) inherits it.
    /// Reflection-based tests require CreateSplineMesh's exact argument count, so pass through context instead.
    /// </summary>
    public static PartWarp Warp { get; set; }

    /// <summary>
    /// Generate objects attached to finalized section parts. Pass the same process through deferred LOD/vertex-budget splitting,
    /// aligning attachment updates/lifetime with the source part. Arguments: generated object, spline, source mesh, clip interval.
    /// </summary>
    public static System.Action<GameObject, SplineComputer, Mesh, double, double> PartCreated { get; set; }

    /// <summary>Current path variant. Fixed road-surface section LOD uses it to select distant models; 0 is unknown and omits distant intervals.</summary>
    public static int PathVariant { get; set; }

    /// <summary>Paint color of the current path (API `color`) for RoadPath.GetOrCreatePartMaterial; null keeps model colors.</summary>
    public static Color? PaintColor { get; set; }

    public static bool HasDetailRange => !SuppressDetailRange && (DetailFrom > 0.0001 || DetailTo < 0.9999);

    /// <summary>Close-up part suffix (e.g. Models/Paths/p411/body_close).</summary>
    public const string CLOSE_SUFFIX = "_close";

    /// <summary>
    /// Allowed chord error for curvature-adaptive tiles (m). Tiles form chords between spline endpoints;
    /// choose L so chord error ≈ κL²/8 for curvature κ and length L remains below this.
    /// </summary>
    private const float CHORD_ERROR_TOLERANCE = 0.02f;
    /// <summary>Adaptive tile length cap (m). Even straight paths remain bounded to capture small terrain variations.</summary>
    private const float ADAPTIVE_TILE_MAX = 8f;
    /// <summary>Curvature-sampling interval (m) and maximum samples.</summary>
    private const float CURVATURE_SAMPLE_STEP = 1f;
    private const int CURVATURE_MAX_SAMPLES = 1024;

    public static void Begin(DrawLod drawLod, double detailFrom = 0.0, double detailTo = 1.0)
    {
        CurrentDrawLod = drawLod;
        DetailFrom = System.Math.Clamp(detailFrom, 0.0, 1.0);
        DetailTo = System.Math.Clamp(detailTo, DetailFrom, 1.0);
        SuppressDetailRange = false;
        AdaptiveTileLength = 0f;
        Warp = null;
        PartCreated = null;
        PathVariant = 0;
        PaintColor = null;
    }

    public static void End()
    {
        CurrentDrawLod = DrawLod.Normal;
        DetailFrom = 0.0;
        DetailTo = 1.0;
        SuppressDetailRange = false;
        AdaptiveTileLength = 0f;
        Warp = null;
        PartCreated = null;
        PathVariant = 0;
        PaintColor = null;
    }

    /// <summary>Set the path being drawn (its variant and paint color) after <see cref="Begin"/>.</summary>
    public static void SetPath(GeneratedModels.PathSummary path)
    {
        PathVariant = path.path_variant;
        PaintColor = RoadPath.PaintColorOf(path);
    }

    public static void SetAdaptiveTileLength(float length)
    {
        AdaptiveTileLength = length;
    }

    /// <summary>
    /// Intersect caller clip interval with detail interval. False if disjoint.
    /// </summary>
    public static bool IntersectDetailRange(double clipFrom, double clipTo, out double from, out double to)
    {
        from = clipFrom;
        to = clipTo;
        if (!HasDetailRange) return clipTo > clipFrom;
        from = System.Math.Max(clipFrom, DetailFrom);
        to = System.Math.Min(clipTo, DetailTo);
        return to - from > 1e-6;
    }

    /// <summary>
    /// For Close LOD, prefer `{prefab}_close`, falling back to the standard part.
    /// Missing standard underground walls/ceilings use default geometry; other parts return null. Close bounds/origins must match standard parts.
    /// </summary>
    public static GameObject ResolvePrefab(string prefabName, string fallbackPrefabName, out string resolvedName)
    {
        resolvedName = prefabName;
        if (ModelManager.Instance == null || string.IsNullOrEmpty(prefabName)) return null;

        if (CurrentDrawLod == DrawLod.Close)
        {
            GameObject close = ModelManager.Instance.GetModel(prefabName + CLOSE_SUFFIX, true);
            if (close != null)
            {
                resolvedName = prefabName + CLOSE_SUFFIX;
                return close;
            }
        }
        GameObject prefab = ModelManager.Instance.GetModel(prefabName);
        if (prefab != null) return prefab;

        if (!string.IsNullOrEmpty(fallbackPrefabName))
        {
            if (CurrentDrawLod == DrawLod.Close)
            {
                GameObject close = ModelManager.Instance.GetModel(fallbackPrefabName + CLOSE_SUFFIX, true);
                if (close != null)
                {
                    resolvedName = fallbackPrefabName + CLOSE_SUFFIX;
                    return close;
                }
            }
            prefab = ModelManager.Instance.GetModel(fallbackPrefabName);
            if (prefab != null)
            {
                resolvedName = fallbackPrefabName;
                return prefab;
            }
        }
        return PathTunnelParts.ResolveDefaultPart(prefabName);
    }

    /// <summary>
    /// Derive tile length within chord-error tolerance from maximum horizontal/vertical spline curvature.
    /// Straight/flat uses the cap; sharp curves or strongly varying terrain conformity shrink to 1m.
    /// </summary>
    public static float ComputeAdaptiveTileLength(SplineComputer spline, float minTile)
    {
        if (spline == null) return minTile;
        float length = spline.CalculateLength();
        if (length <= CURVATURE_SAMPLE_STEP * 2f) return minTile;

        int samples = Mathf.Clamp(Mathf.CeilToInt(length / CURVATURE_SAMPLE_STEP), 3, CURVATURE_MAX_SAMPLES);
        float step = length / (samples - 1);
        Vector3 prev = spline.EvaluatePosition(0.0);
        Vector3 cur = spline.EvaluatePosition(1.0 / (samples - 1));
        float maxCurvature = 0f;
        for (int i = 2; i < samples; i++)
        {
            Vector3 next = spline.EvaluatePosition((double)i / (samples - 1));
            Vector3 d1 = cur - prev;
            Vector3 d2 = next - cur;
            float l1 = d1.magnitude, l2 = d2.magnitude;
            if (l1 > 1e-4f && l2 > 1e-4f)
            {
                // Turn angle ÷ mean section length ≈ curvature.
                float angle = Vector3.Angle(d1, d2) * Mathf.Deg2Rad;
                float curvature = angle / (0.5f * (l1 + l2));
                if (curvature > maxCurvature) maxCurvature = curvature;
            }
            prev = cur;
            cur = next;
        }

        if (maxCurvature < 1e-5f) return ADAPTIVE_TILE_MAX;
        float tile = Mathf.Sqrt(8f * CHORD_ERROR_TOLERANCE / maxCurvature);
        return Mathf.Clamp(tile, minTile, ADAPTIVE_TILE_MAX);
    }

    /// <summary>
    /// Return point-list subsection whose cumulative-distance fractions lie in [from, to] (interpolate endpoints).
    /// Used to place distant models only outside detail intervals.
    /// </summary>
    public static List<Vector3> SlicePolylineByFraction(List<Vector3> points, double from, double to)
    {
        var result = new List<Vector3>();
        if (points == null || points.Count < 2 || to <= from) return result;
        float total = 0f;
        var cum = new float[points.Count];
        for (int i = 1; i < points.Count; i++)
        {
            total += Vector3.Distance(points[i - 1], points[i]);
            cum[i] = total;
        }
        if (total <= 1e-4f) return result;
        float a = (float)(from * total), b = (float)(to * total);
        void AddPoint(Vector3 point)
        {
            if (result.Count > 0 && (result[result.Count - 1] - point).sqrMagnitude < 1e-8f)
                result[result.Count - 1] = point;
            else result.Add(point);
        }
        for (int i = 0; i < points.Count - 1; i++)
        {
            float s0 = cum[i], s1 = cum[i + 1];
            float segLen = s1 - s0;
            // Adding boundary-coincident vertices twice from neighboring segments makes endpoint tangents zero.
            // Exclude them to prevent SplineMesh cross-sections rotating 90° and road surfaces protruding at construction fronts.
            if (s1 <= a || s0 >= b || segLen <= 1e-6f) continue;
            if (result.Count == 0)
            {
                float t0 = segLen > 1e-6f ? Mathf.Clamp01((a - s0) / segLen) : 0f;
                AddPoint(Vector3.Lerp(points[i], points[i + 1], t0));
            }
            if (s1 <= b)
            {
                AddPoint(points[i + 1]);
                if (s1 >= b) break;
            }
            else
            {
                float t1 = segLen > 1e-6f ? Mathf.Clamp01((b - s0) / segLen) : 1f;
                AddPoint(Vector3.Lerp(points[i], points[i + 1], t1));
                break;
            }
        }
        return result;
    }
}

/// <summary>
/// Part deformation: lateral offset by spline fraction (0–1, m, positive rightward) and width scale (1=original).
/// SourceMesh selects a rendering mesh while preserving the shared original. Scale about part origin; origin corrections move with the scale.
/// </summary>
public sealed class PartWarp
{
    public System.Func<double, float> Shift;
    public System.Func<double, float> Scale;
    public System.Func<Mesh, Mesh> SourceMesh;
}
