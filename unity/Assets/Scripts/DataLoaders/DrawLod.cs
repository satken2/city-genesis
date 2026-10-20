using UnityEngine;

/// <summary>
/// Render detail for one object (one building or path).
/// Determined by camera appearance independently of per-cell data tiers (<see cref="LodTier"/>).
/// Data tiers determine which terrain data is held and whether geometry follows terrain; render LOD determines drawing detail.
/// Mixing them would generate close-up meshes even for buildings in Near cells up to 1,100m away.
/// </summary>
public enum DrawLod
{
    /// <summary>Do not render. Used for props beyond a camera distance; not used for paths/buildings, which retain skyline and bands at distance.</summary>
    Hidden = 0,
    /// <summary>Far: stretch one distant model to the building outline or repeat along the path. Use a box/plane if unavailable.</summary>
    Far = 1,
    /// <summary>Normal: assemble using unsuffixed parts. This is the production baseline and must establish the streetscape.</summary>
    Normal = 2,
    /// <summary>Close: assemble with `{part name}_close` where available; otherwise use Normal parts.</summary>
    Close = 3,
}

/// <summary>
/// Render LOD thresholds, primarily based on screen size (projected ratio) rather than distance.
/// Projected ratio = bounding sphere radius / (distance × tan(FOV/2)), approximately the fraction of screen height occupied.
/// Do not rely on distance alone, so rendering effort follows visible detail in overview, low views, and zoom.
/// Separate promotion (approaching) and demotion (departing) thresholds prevent boundary oscillation (flicker).
/// These are initial values intended for adjustment using phase 0 measurements.
/// </summary>
public static class DrawLodConfig
{
    /// <summary>Projected ratio promoting Normal to Close (close-up at or above this value).</summary>
    public const float CLOSE_ENTER_RATIO = 0.25f;
    /// <summary>Projected ratio demoting Close to Normal (exit close-up below this value).</summary>
    public const float CLOSE_EXIT_RATIO = 0.18f;
    /// <summary>
    /// Maximum close-up distance (m). Close assembly is expensive, so limit it to immediately nearby objects.
    /// Large facilities have high projected ratios even far away, so projected ratio alone is insufficient.
    /// </summary>
    public const float CLOSE_MAX_DISTANCE = 50f;
    /// <summary>Projected ratio promoting Far to Normal.</summary>
    public const float NORMAL_ENTER_RATIO = 0.045f;
    /// <summary>Projected ratio demoting Normal to Far.</summary>
    public const float NORMAL_EXIT_RATIO = 0.03f;

    /// <summary>
    /// Paths are elongated, making bounding spheres unsuitable; use nearest distance (m) to the path centerline.
    /// Close/Normal distance thresholds (promotion/demotion).
    /// </summary>
    public const float PATH_CLOSE_ENTER_DISTANCE = 50f;
    public const float PATH_CLOSE_EXIT_DISTANCE = 65f;
    public const float PATH_NORMAL_ENTER_DISTANCE = 450f;
    public const float PATH_NORMAL_EXIT_DISTANCE = 550f;

    /// <summary>
    /// Actual camera distance (m) beyond which props become Hidden. Use actual distance rather than projected ratio
    /// to exclude entire distant forests within the resident range (up to beyond ±2km) without adding low-poly models.
    /// Use actual distance for orthographic views too (hide more as the top-down camera rises). Separate entry/exit thresholds prevent flicker.
    /// </summary>
    public const float PROP_HIDDEN_ENTER_DISTANCE = 1500f;
    public const float PROP_HIDDEN_EXIT_DISTANCE = 1350f;

    /// <summary>Tolerance when comparing clipping ranges in compatibility calls.</summary>
    public const float PATH_DETAIL_RANGE_REBUILD_TOLERANCE = 0.1f;

    /// <summary>Render LOD reevaluation interval (frames). Avoid scanning every object each frame.</summary>
    public const int REEVALUATE_INTERVAL_FRAMES = 12;
    /// <summary>Reevaluate only after the camera moves at least this distance (m).</summary>
    public const float REEVALUATE_CAMERA_MOVE_DISTANCE = 4f;

    /// <summary>Maximum buildings holding close-up meshes simultaneously. Do not promote beyond this count (projected ratio controls demotion).</summary>
    public const int MAX_CLOSE_BUILDINGS = 5;
    /// <summary>Maximum paths holding close-up meshes simultaneously.</summary>
    public const int MAX_CLOSE_PATHS = 5;
}

/// <summary>
/// Render LOD evaluator. The caller obtains camera information once and passes it in
/// (avoids resolving Camera.main thousands of times per evaluation pass).
/// </summary>
public static class DrawLodEvaluator
{
    /// <summary>Tangent of half the camera's vertical FOV. Used in the projected ratio denominator.</summary>
    public static float GetTanHalfFov(Camera cam)
    {
        if (cam == null) return Mathf.Tan(30f * Mathf.Deg2Rad);
        if (cam.orthographic)
        {
            // Orthographic size is distance-independent, so treat orthographic size as half-height at unit distance.
            return Mathf.Max(0.01f, cam.orthographicSize);
        }
        return Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
    }

    /// <summary>Calculate projected ratio from bounding sphere radius and distance.</summary>
    public static float ProjectedRatio(float radius, float distance, float tanHalfFov)
    {
        float d = Mathf.Max(1f, distance);
        return radius / (d * tanHalfFov);
    }

    /// <summary>
    /// Evaluate render LOD for objects whose size is represented by a bounding sphere, such as buildings.
    /// current is the current render LOD (for hysteresis). allowClose=false prevents promotion to Close
    /// when the close-up limit is reached. Never return Hidden (buildings retain the distant skyline).
    /// </summary>
    public static DrawLod EvaluateBySize(float radius, float distance, float tanHalfFov, DrawLod current, bool allowClose, bool orthographic = false)
    {
        float ratio = ProjectedRatio(radius, orthographic ? 1f : distance, tanHalfFov);

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
    /// Evaluate path render LOD using nearest distance to its centerline. Never return Hidden.
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

    /// <summary>
    /// Whether to hide a prop based on distance. If current is Hidden, keep it hidden until below the exit distance (the nearer threshold).
    /// Evaluate before per-object projected LOD (<see cref="EvaluateBySize"/>); if true, skip projected LOD evaluation.
    /// </summary>
    public static bool PropHiddenByDistance(float distance, DrawLod current)
    {
        return current == DrawLod.Hidden
            ? distance >= DrawLodConfig.PROP_HIDDEN_EXIT_DISTANCE
            : distance >= DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE;
    }

    /// <summary>Nearest 3D camera distance to an XZ point sequence. Find the nearest point on each segment.</summary>
    public static float MinDistanceToPolyline(System.Collections.Generic.IList<Vector3> points, Vector3 camPos)
    {
        if (points == null || points.Count == 0) return float.MaxValue;
        if (points.Count == 1) return Vector3.Distance(points[0], camPos);
        float best = float.MaxValue;
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];
            float d = Vector3.Distance(PolylineUtils.ClosestPointOnSegment(camPos, a, b), camPos);
            if (d < best) best = d;
        }
        return best;
    }
}

/// <summary>
/// Store the generated object's render LOD and, for paths, the section rendered in detail.
/// Used to regenerate only differences during reevaluation. Like <see cref="ObjectIdInfo"/>,
/// read this component instead of parsing GameObject names.
/// </summary>
public class DrawLodInfo : MonoBehaviour
{
    public DrawLod drawLod = DrawLod.Normal;
    /// <summary>Detailed path section (spline fraction). 0–1 for full length. Unused by buildings.</summary>
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
