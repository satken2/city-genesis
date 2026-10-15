using System;
using System.Collections.Generic;
using UnityEngine;

public partial class RoadPlanManager
{
    // 線形(曲線半径・勾配)のローカル推定に使う中心線の標本間隔(m)。バックエンドの1m間隔と同じ
    private const float AlignmentSampleStep = 1f;
    private List<int> cachedMaxSpanViolations;
    // 線形制約(最小曲線半径・最大勾配)を超える区間の点列(絶対座標)のローカル推定キャッシュと、
    // HUD表示用の最小曲線半径(m)・最大勾配(%)。
    private List<List<Vector3>> cachedAlignmentViolations;
    private float cachedAlignmentMinRadius = float.PositiveInfinity;
    private float cachedAlignmentMaxGradePct;
    private float cachedAlignmentGradeLimit;
    private bool cachedCurveViolation;
    private bool cachedGradeViolation;

    /// <summary>
    /// 現在の道路形状で max_span を超えるセグメントを検出する。
    /// 水平距離（XZ平面のみ）で計算し、曲線は考慮しない。
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
    /// max_span を超えるセグメントを赤色の太線でハイライトする。
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
    // 線形制約(最小曲線半径・最大勾配)のローカル推定
    // =====================================================================

    /// <summary>検証用: 敷設中パスの最小曲線半径(m)。直線・点不足は +∞。</summary>
    public float AlignmentMinRadius { get { CheckAlignmentViolations(); return cachedAlignmentMinRadius; } }

    /// <summary>検証用: 敷設中パスの最大縦断勾配(%)。</summary>
    public float AlignmentMaxGradePct { get { CheckAlignmentViolations(); return cachedAlignmentMaxGradePct; } }

    /// <summary>規格(min_curve_radius / max_grade)を超える区間があるか。</summary>
    public bool HasAlignmentViolation => CheckAlignmentViolations().Count > 0;
    public bool HasCurveViolation { get { CheckAlignmentViolations(); return cachedCurveViolation; } }
    public float AlignmentGradeLimit { get { CheckAlignmentViolations(); return cachedAlignmentGradeLimit; } }
    public bool HasGradeViolation { get { CheckAlignmentViolations(); return cachedGradeViolation; } }

    // 約1mの標高差分に含まれるfloatの丸め誤差を許容する（単位はパーセントポイント）。
    private const float AlignmentGradeTolerancePct = 0.001f;

    /// <summary>水平折れ角。バックエンド ropeway_bend_violations と同じく重複点は無効とする。</summary>
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
            // 度単位の許容誤差はバックエンドと共通。高さによる傾斜は含めない。
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

    /// <summary>線形指標を表示できる状態か(配置中は1点+ホバー、編集中は2点以上)。</summary>
    public bool HasAlignmentMetrics =>
        IsAlignmentTarget() && points.Count >= (phase == RoadEditorPhase.Placing ? 1 : 2);

    /// <summary>線形の評価対象の種別か(バックエンド geo_path_alignment.ALIGNMENT_PATH_TYPES と同じ)。</summary>
    private bool IsAlignmentTarget()
    {
        return PathType == Constants.PATH_MOTORWAY || PathType == Constants.PATH_STREET
            || PathType == Constants.PATH_WALKWAY || PathType == Constants.PATH_TRACK
            || PathType == Constants.PATH_MONORAIL;
    }

    /// <summary>
    /// 敷設中パスの線形(曲線半径・縦断勾配)を1m間隔の点列からローカル推定し、規格
    /// (path_master の min_curve_radius / max_grade。0=制限なし)を超える区間の点列を返す。
    ///
    /// バックエンド geo_path_alignment と同じ規則: 前後 PATH_CURVATURE_WINDOW_M の3点を通る
    /// 円の半径を局所曲線半径(折れ線の角は小さな半径として現れる)、smooth_factor(最小10m)の
    /// 移動平均は地形追従区間だけに適用し、非追従区間は線分の標高差を勾配とする。権威判定はバックエンド
    /// (construction-cost / validate / 登録時)で、ここはドラッグ追従の赤表示・区間ラベルに使う。
    /// 保存設計標高や複線展開後の最終判定はバックエンドに委ね、
    /// ローカル推定だけでは確定を禁止しない。形状変更でキャッシュを無効化する。
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

        // 勾配: 標高を移動平均で均してから(float16地形の量子化段差を消す)、前後 w 点の標高差を距離で割る
        int half = Mathf.Max(1, Mathf.RoundToInt(smoothDistance / AlignmentSampleStep)) / 2;
        var smoothed = new double[n];
        // 累積和で長い平滑化窓も線形時間で処理し、絶対標高が高い都市での桁落ちを抑える。
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
                // 非追従区間は造成されないため、隣の坂を平滑化で持ち込まず実際の線分の勾配を測る。
                int a = Mathf.Min(i, n - 2), b = a + 1;
                double dx = (double)samples[b].x - samples[a].x, dz = (double)samples[b].z - samples[a].z;
                double distance = System.Math.Sqrt(dx * dx + dz * dz);
                gradePct[i] = distance > 1e-9 ? (float)(System.Math.Abs((double)samples[b].y - samples[a].y) / distance * 100) : 0f;
            }
            if (gradePct[i] > cachedAlignmentMaxGradePct) cachedAlignmentMaxGradePct = gradePct[i];
        }

        CollectAlignmentLabels(samples, sampleOrigin, radius, radiusLimit, true, alignmentLabels);
        CollectAlignmentLabels(samples, sampleOrigin, gradePct, gradeLimit, false, alignmentLabels);

        // 2Dパネルへ送る線形サンプル(RoadPlanManager.ShapeSync)のために評価結果を保持する
        cachedAlignmentSamplesWorld = new List<Vector3>(n);
        for (int i = 0; i < n; i++) cachedAlignmentSamplesWorld.Add(samples[i] + sampleOrigin);
        cachedAlignmentRadius = radius;
        cachedAlignmentGradePct = gradePct;
        cachedAlignmentRadiusLimit = radiusLimit;

        // 違反サンプルの連続区間を点列にまとめる
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
    /// 描画と同じ制御点・仮終点から密な曲線を作り、全長にわたる約1m等間隔へ再標本化する。
    /// 固定分割の描画用折れ線を測ると、その折れ角を急カーブとして検出するため使用しない。
    /// 区間ごとに端数を切り上げず、バックエンドと同じ全長基準で標本間隔を揃える。
    /// </summary>
    private List<Vector3> BuildAlignmentSamples()
    {
        var samples = BuildLocalAlignmentSamples(out Vector3 origin);
        for (int i = 0; i < samples.Count; i++) samples[i] += origin;
        return samples;
    }

    /// <summary>
    /// 始点を原点とする相対座標で標本を作る。大きなワールド座標のまま float で補間して差を取ると、
    /// 端部の約1mの窓で丸め誤差が増幅され、緩い曲線を急曲線と誤認するため。
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

    /// <summary>XZ平面で3点を通る円の半径。ほぼ一直線なら +∞。</summary>
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
    /// 線形制約を超える区間を赤の太線でハイライトする(max_span 警告と同じ表現)。
    /// 1m間隔の点列を2点おきに結び、路面より少し浮かせて描く。
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
