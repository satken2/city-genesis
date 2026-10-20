using System;
using System.Collections.Generic;
using UnityEngine;

public partial class RoadPlanManager
{
    // Centerline sample spacing (m) for local alignment estimates (curve radius/grade). Matches the backend's 1m spacing.
    private const float AlignmentSampleStep = 1f;
    private List<int> cachedMaxSpanViolations;
    // Cached local estimates of point sequences (absolute coordinates) violating alignment constraints (minimum curve radius/maximum grade),
    // and minimum curve radius (m)/maximum grade (%) for the HUD.
    private List<List<Vector3>> cachedAlignmentViolations;
    private float cachedAlignmentMinRadius = float.PositiveInfinity;
    private float cachedAlignmentMaxGradePct;
    private float cachedAlignmentGradeLimit;
    private bool cachedCurveViolation;
    private bool cachedGradeViolation;

    /// <summary>
    /// Detects segments exceeding max_span in the current road geometry.
    /// Uses horizontal distance (XZ only), ignoring curvature.
    /// </summary>
    private List<int> CheckMaxSpanViolations()
    {
        if (cachedMaxSpanViolations != null)
            return cachedMaxSpanViolations;

        List<int> violations = new List<int>();
        if (MasterDataStore.Instance == null)
        {
            cachedMaxSpanViolations = violations;
            return cachedMaxSpanViolations;
        }

        var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
        if (master == null || !master.max_span.HasValue)
        {
            cachedMaxSpanViolations = violations;
            return cachedMaxSpanViolations;
        }

        float maxSpan = master.max_span.Value;
        if (maxSpan <= 0f)
        {
            cachedMaxSpanViolations = violations;
            return cachedMaxSpanViolations;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i].position;
            Vector3 b = points[i + 1].position;
            float dist = Mathf.Sqrt((b.x - a.x) * (b.x - a.x) + (b.z - a.z) * (b.z - a.z));
            if (dist > maxSpan)
            {
                violations.Add(i);
            }
        }

        cachedMaxSpanViolations = violations;
        return cachedMaxSpanViolations;
    }

    /// <summary>
    /// Highlights segments exceeding max_span with thick red lines.
    /// </summary>
    private void DrawMaxSpanWarnings(Camera cam)
    {
        if (overlayRenderer == null) return;

        List<int> violations = CheckMaxSpanViolations();
        if (violations.Count == 0) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        Color warningColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        foreach (int i in violations)
        {
            Vector3 a = points[i].position;
            Vector3 b = points[i + 1].position;
            if (Vector3.Dot(a - camPos, camFwd) < 0f && Vector3.Dot(b - camPos, camFwd) < 0f) continue;
            overlayRenderer.AddSolidLine(a, b, warningColor, dashedLineThickness * 2.5f);
        }
    }

    // =====================================================================
    // Local alignment constraint estimates (minimum curve radius/maximum grade)
    // =====================================================================

    /// <summary>For validation: minimum curve radius of the path being laid (m). +∞ for straight lines/insufficient points.</summary>
    public float AlignmentMinRadius { get { CheckAlignmentViolations(); return cachedAlignmentMinRadius; } }

    /// <summary>For validation: maximum longitudinal grade of the path being laid (%).</summary>
    public float AlignmentMaxGradePct { get { CheckAlignmentViolations(); return cachedAlignmentMaxGradePct; } }

    /// <summary>Whether any section exceeds min_curve_radius / max_grade specifications.</summary>
    public bool HasAlignmentViolation => CheckAlignmentViolations().Count > 0;
    public bool HasCurveViolation { get { CheckAlignmentViolations(); return cachedCurveViolation; } }
    public float AlignmentGradeLimit { get { CheckAlignmentViolations(); return cachedAlignmentGradeLimit; } }
    public bool HasGradeViolation { get { CheckAlignmentViolations(); return cachedGradeViolation; } }

    // Allow float rounding errors in elevation differences over roughly 1m (in percentage points).
    private const float AlignmentGradeTolerancePct = 0.001f;

    /// <summary>Horizontal deflection angle. Duplicate points are invalid, matching backend ropeway_bend_violations.</summary>
    public static double RopewayBendAngle(Vector3 previous, Vector3 anchor, Vector3 next)
    {
        double ax = (double)anchor.x - previous.x, az = (double)anchor.z - previous.z;
        double bx = (double)next.x - anchor.x, bz = (double)next.z - anchor.z;
        if (ax * ax + az * az <= 1e-12 || bx * bx + bz * bz <= 1e-12) return 180.0;
        return System.Math.Atan2(System.Math.Abs(ax * bz - az * bx), ax * bx + az * bz)
            * 180.0 / System.Math.PI;
    }

    private void CollectRopewayBendWarnings(List<List<Vector3>> violations)
    {
        var anchors = new List<Vector3>();
        foreach (var point in points) anchors.Add(point.position);
        if (phase == RoadEditorPhase.Placing) anchors.Add(hoverWorldPosition);
        for (int i = 1; i < anchors.Count - 1; i++)
        {
            double angle = RopewayBendAngle(anchors[i - 1], anchors[i], anchors[i + 1]);
            // Degree tolerance matches the backend. Excludes vertical slope.
            if (angle <= 5.0 + 0.001) continue;
            cachedCurveViolation = true;
            violations.Add(new List<Vector3> { anchors[i - 1], anchors[i] });
            violations.Add(new List<Vector3> { anchors[i], anchors[i + 1] });
            alignmentLabels.Add(new AlignmentLabel {
                curve = true, value = (float)angle, limit = 5f, position = anchors[i],
                text = $"ロープウェイ 折れ角 {angle:0.00}°\n上限 5° 超過",
            });
        }
    }

    /// <summary>Whether alignment metrics can be shown (1 point + hover while placing; at least 2 points while editing).</summary>
    public bool HasAlignmentMetrics =>
        IsAlignmentTarget() && points.Count >= (phase == RoadEditorPhase.Placing ? 1 : 2);

    /// <summary>Whether the path type is evaluated for alignment (matches backend geo_path_alignment.ALIGNMENT_PATH_TYPES).</summary>
    private bool IsAlignmentTarget()
    {
        return PathType == Constants.PATH_MOTORWAY || PathType == Constants.PATH_STREET
            || PathType == Constants.PATH_WALKWAY || PathType == Constants.PATH_TRACK
            || PathType == Constants.PATH_MONORAIL;
    }

    /// <summary>
    /// Locally estimates path alignment (curve radius/longitudinal grade) from 1m samples and returns point sequences
    /// exceeding specifications (path_master min_curve_radius / max_grade; 0=unlimited).
    ///
    /// Same rules as backend geo_path_alignment: local radius is the circle through three points separated by
    /// PATH_CURVATURE_WINDOW_M (polyline corners appear as small radii); apply a smooth_factor moving average
    /// (minimum 10m) only to terrain-following sections, using segment elevation grade for other sections. The backend
    /// is authoritative (construction-cost / validate / registration); this supplies responsive red highlights and section labels.
    /// Leave saved design elevations and final validation after parallel track expansion to the backend;
    /// local estimates alone do not prohibit confirmation. Geometry changes invalidate the cache.
    /// </summary>
    private List<List<Vector3>> CheckAlignmentViolations()
    {
        if (cachedAlignmentViolations != null)
            return cachedAlignmentViolations;

        var violations = new List<List<Vector3>>();
        cachedAlignmentViolations = violations;
        alignmentLabels.Clear();
        cachedAlignmentSamplesWorld = null;
        cachedAlignmentRadius = null;
        cachedAlignmentGradePct = null;
        cachedAlignmentRadiusLimit = 0f;
        cachedAlignmentMinRadius = float.PositiveInfinity;
        cachedAlignmentMaxGradePct = 0f;
        cachedAlignmentGradeLimit = 0f;
        cachedCurveViolation = cachedGradeViolation = false;
        if (RoadPath.IsRopewayVariant(PathSubtype))
        {
            CollectRopewayBendWarnings(violations);
            return violations;
        }
        if (authoritativeAlignments != null)
            return ApplyAuthoritativeAlignmentCache(violations);
        if (!IsAlignmentTarget()) return violations;

        float radiusLimit = 0f, gradeLimit = 0f;
        float smoothDistance = LocalConstants.PATH_GRADE_SMOOTH_MIN_M;
        if (MasterDataStore.Instance != null)
        {
            var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
            if (master != null)
            {
                radiusLimit = master.min_curve_radius ?? 0f;
                gradeLimit = master.max_grade ?? 0f;
                smoothDistance = Mathf.Max(smoothDistance, master.smooth_factor ?? 0f);
            }
        }

        cachedAlignmentGradeLimit = gradeLimit;
        var follows = new List<bool>();
        var samples = BuildLocalAlignmentSamples(out Vector3 sampleOrigin, follows);
        int n = samples.Count;
        if (n < 3) return violations;

        int w = Mathf.Max(1, Mathf.RoundToInt(LocalConstants.PATH_CURVATURE_WINDOW_M / AlignmentSampleStep));
        var radius = new float[n];
        for (int i = 0; i < n; i++)
        {
            int span = Mathf.Min(w, Mathf.Min(i, n - 1 - i));
            radius[i] = span >= 1 ? Circumradius(samples[i - span], samples[i], samples[i + span]) : float.PositiveInfinity;
            if (radius[i] < cachedAlignmentMinRadius) cachedAlignmentMinRadius = radius[i];
        }

        // Grade: smooth elevations with a moving average (removes float16 terrain quantization steps), then divide the elevation difference across w samples by distance.
        int half = Mathf.Max(1, Mathf.RoundToInt(smoothDistance / AlignmentSampleStep)) / 2;
        var smoothed = new double[n];
        // Use prefix sums for linear-time processing even with long smoothing windows, limiting cancellation in high-elevation cities.
        var sums = new double[n + 1];
        for (int i = 0; i < n; i++) sums[i + 1] = sums[i] + samples[i].y;
        for (int i = 0; i < n; i++)
        {
            int left = Mathf.Max(0, i - half), right = Mathf.Min(n - 1, i + half);
            double sum = sums[right + 1] - sums[left];
            sum += (double)Mathf.Max(0, half - i) * samples[0].y;
            sum += (double)Mathf.Max(0, i + half - n + 1) * samples[n - 1].y;
            smoothed[i] = sum / (2 * half + 1);
        }
        var gradePct = new float[n];
        for (int i = 0; i < n; i++)
        {
            double ya = smoothed[Mathf.Clamp(i - w, 0, n - 1)];
            double yb = smoothed[Mathf.Clamp(i + w, 0, n - 1)];
            if (follows[i])
                gradePct[i] = (float)(System.Math.Abs(yb - ya) / (2.0 * w * AlignmentSampleStep) * 100.0);
            else
            {
                // Non-following sections have no earthworks; measure actual segment grades without smoothing adjacent slopes into them.
                int a = Mathf.Min(i, n - 2), b = a + 1;
                double dx = (double)samples[b].x - samples[a].x, dz = (double)samples[b].z - samples[a].z;
                double distance = System.Math.Sqrt(dx * dx + dz * dz);
                gradePct[i] = distance > 1e-9 ? (float)(System.Math.Abs((double)samples[b].y - samples[a].y) / distance * 100) : 0f;
            }
            if (gradePct[i] > cachedAlignmentMaxGradePct) cachedAlignmentMaxGradePct = gradePct[i];
        }

        CollectAlignmentLabels(samples, sampleOrigin, radius, radiusLimit, true, alignmentLabels);
        CollectAlignmentLabels(samples, sampleOrigin, gradePct, gradeLimit, false, alignmentLabels);

        // Retain evaluation results for alignment samples sent to the 2D panel (RoadPlanManager.ShapeSync).
        cachedAlignmentSamplesWorld = new List<Vector3>(n);
        for (int i = 0; i < n; i++) cachedAlignmentSamplesWorld.Add(samples[i] + sampleOrigin);
        cachedAlignmentRadius = radius;
        cachedAlignmentGradePct = gradePct;
        cachedAlignmentRadiusLimit = radiusLimit;

        // Group consecutive violation samples into point sequences.
        List<Vector3> run = null;
        for (int i = 0; i < n; i++)
        {
            bool badCurve = radiusLimit > 0f && radius[i] < radiusLimit;
            bool badGrade = gradeLimit > 0f && gradePct[i] > gradeLimit + AlignmentGradeTolerancePct;
            cachedCurveViolation |= badCurve;
            cachedGradeViolation |= badGrade;
            bool bad = badCurve || badGrade;
            if (bad)
            {
                if (run == null) { run = new List<Vector3>(); violations.Add(run); }
                run.Add(samples[i] + sampleOrigin);
            }
            else
            {
                run = null;
            }
        }
        return violations;
    }

    /// <summary>
    /// Builds a dense curve using the same controls and provisional endpoint as rendering, then resamples at roughly 1m intervals over its full length.
    /// Do not measure the fixed-subdivision render polyline: its corners would be detected as sharp curves.
    /// Match backend spacing over total length rather than rounding up each section independently.
    /// </summary>
    private List<Vector3> BuildAlignmentSamples()
    {
        var samples = BuildLocalAlignmentSamples(out Vector3 origin);
        for (int i = 0; i < samples.Count; i++) samples[i] += origin;
        return samples;
    }

    /// <summary>
    /// Build samples relative to the start point. Interpolating and differencing large world coordinates as floats
    /// amplifies rounding errors in the roughly 1m end windows, misclassifying gentle curves as sharp ones.
    /// </summary>
    private List<Vector3> BuildLocalAlignmentSamples(out Vector3 origin, List<bool> follows = null)
    {
        origin = points.Count > 0 ? points[0].position : Vector3.zero;
        var samples = new List<Vector3>();
        if (!HasAlignmentMetrics) return samples;
        bool placing = phase == RoadEditorPhase.Placing;
        var sourceFollows = new List<bool>();
        var source = SamplePreviewSpans(GetRenderPoints(), 0, points.Count - 1, placing, false, origin, sourceFollows);
        var profiles = earthworkPreview?.PathProfiles;
        if (!placing && !isDragging && profiles != null && profiles.Count > 0 && profiles[0].Count >= 2)
        {
            source = new List<Vector3>(profiles[0].Count);
            sourceFollows.Clear();
            foreach (var point in profiles[0])
            {
                source.Add(point - origin);
                sourceFollows.Add(false);
            }
        }
        if (source.Count < 2) return samples;

        var distances = new double[source.Count];
        for (int i = 1; i < source.Count; i++)
        {
            double dx = (double)source[i].x - source[i - 1].x;
            double dz = (double)source[i].z - source[i - 1].z;
            distances[i] = distances[i - 1] + System.Math.Sqrt(dx * dx + dz * dz);
        }
        double total = distances[distances.Length - 1];
        if (total < 1e-3) return samples;
        int steps = (int)System.Math.Ceiling(total / AlignmentSampleStep);
        int segment = 0;
        for (int i = 0; i <= steps; i++)
        {
            double distance = total * i / steps;
            while (segment < source.Count - 2 && distances[segment + 1] <= distance) segment++;
            double length = distances[segment + 1] - distances[segment];
            float t = length > 0 ? (float)((distance - distances[segment]) / length) : 0f;
            samples.Add(Vector3.Lerp(source[segment], source[segment + 1], t));
            follows?.Add(sourceFollows[segment]);
        }
        return samples;
    }

    /// <summary>Radius of the circle through 3 XZ points. +∞ if nearly collinear.</summary>
    private static float Circumradius(Vector3 a, Vector3 b, Vector3 c)
    {
        double abx = (double)b.x - a.x, abz = (double)b.z - a.z;
        double acx = (double)c.x - a.x, acz = (double)c.z - a.z;
        double bcx = (double)c.x - b.x, bcz = (double)c.z - b.z;
        double cross = System.Math.Abs(abx * acz - abz * acx);
        if (cross < 1e-9) return float.PositiveInfinity;
        double ab = System.Math.Sqrt(abx * abx + abz * abz);
        double ac = System.Math.Sqrt(acx * acx + acz * acz);
        double bc = System.Math.Sqrt(bcx * bcx + bcz * bcz);
        return (float)(ab * bc * ac / (2.0 * cross));
    }

    /// <summary>
    /// Highlights sections violating alignment constraints with thick red lines (same as max_span warnings).
    /// Connects every other 1m sample and draws slightly above the road surface.
    /// </summary>
    private void DrawAlignmentWarnings(Camera cam)
    {
        if (overlayRenderer == null) return;

        var violations = CheckAlignmentViolations();
        if (violations.Count == 0) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        Color warningColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        Vector3 lift = Vector3.up * 0.3f;
        foreach (var run in violations)
        {
            for (int i = 2; i < run.Count + 1; i += 2)
            {
                Vector3 a = run[i - 2] + lift;
                Vector3 b = run[Mathf.Min(i, run.Count - 1)] + lift;
                if (Vector3.Dot(a - camPos, camFwd) < 0f && Vector3.Dot(b - camPos, camFwd) < 0f) continue;
                overlayRenderer.AddSolidLine(a, b, warningColor, dashedLineThickness * 2.5f);
            }
        }
    }
}
