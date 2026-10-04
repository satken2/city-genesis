using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

internal static class StationGradeQA
{
    public static int Run()
    {
        int checks = 0;
        var profiles = new List<List<List<float>>>
        {
            new(),
            new() { new() { 0f, 12f, 0f } },
            new() { new() { 0f, 12f, 0f }, new() { 0f, 24f, 0f } },
            new() { new() { -20f, 0f, 0f }, new() { 20f, 40f, 0f } },
        };
        var crossing = new List<List<float>>();
        for (int i = 0; i < 12; i++) crossing.Add(new() { -20f + 4f * i, 10f, 0f });
        for (int i = 0; i < 12; i++) crossing.Add(new() { 0f, 80f, -20f + 4f * i });
        profiles.Add(crossing);
        var reverse = new List<List<float>>(crossing);
        reverse.Reverse();
        profiles.Add(reverse);
        var parallel = new List<List<float>>();
        for (int i = 0; i < 20; i++) parallel.Add(new() { -20f + 2f * i, 10f, -1f });
        for (int i = 0; i < 20; i++) parallel.Add(new() { 18f - 2f * i, 80f, 1f });
        profiles.Add(parallel);

        var random = new System.Random(8421);
        foreach (float scale in new[] { .00001f, 1f, 5000f })
        {
            var loop = new List<List<float>>();
            for (int i = 0; i < 513; i++)
            {
                float angle = i * .023f;
                loop.Add(new() { scale * MathF.Cos(angle), i * .5f, scale * MathF.Sin(angle) });
                if (i % 37 == 0) loop.Add(new(loop[loop.Count - 1]));
            }
            profiles.Add(loop);
            var shuffled = new List<List<float>>(loop);
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            profiles.Add(shuffled);
        }

        foreach (var profile in profiles)
        {
            Check(profile, Vector3.zero);
            checks++;
            foreach (var vertex in profile)
            {
                Check(profile, new Vector3(vertex[0], -999f, vertex[2]));
                checks++;
            }
            for (int i = 0; i < 300; i++)
            {
                float scale = i % 3 == 0 ? .00001f : i % 3 == 1 ? 30f : 20000f;
                var point = new Vector3((float)(random.NextDouble() - .5) * scale, 999f,
                    (float)(random.NextDouble() - .5) * scale);
                Check(profile, point);
                checks++;
            }
        }

        var path = new PathSummary { station_grade_profile = new List<object>
            { new List<float> { 0f, 10f, 0f }, new List<float> { 10f, 20f, 0f } } };
        var original = path.GetStationGradeProfile();
        if (!ReferenceEquals(original, path.GetStationGradeProfile()))
            throw new InvalidOperationException("同じ受信点列の型変換が共有されません。");
        path.station_grade_profile = new List<object>
            { new List<float> { 0f, 30f, 0f }, new List<float> { 10f, 40f, 0f } };
        var replacement = path.GetStationGradeProfile();
        if (ReferenceEquals(original, replacement) || PathManager.EvaluateStationGrade(replacement, new Vector3(5f, 0f, 0f)) != 35f)
            throw new InvalidOperationException("受信点列の置き換え後も古い標高が使われます。");
        return checks + 2;
    }

    private static void Check(List<List<float>> profile, Vector3 point)
    {
        float expected = BruteForce(profile, point);
        float actual = PathManager.EvaluateStationGrade(profile, point);
        if (actual != expected)
            throw new InvalidOperationException($"保存標高が総当たりの結果と一致しません: count={profile.Count}, XZ=({point.x},{point.z}), expected={expected}, actual={actual}");
    }

    private static float BruteForce(List<List<float>> profile, Vector3 point)
    {
        float best = float.PositiveInfinity, height = 0f;
        for (int i = 1; i < profile.Count; i++)
        {
            var a = profile[i - 1];
            var b = profile[i];
            var start = new Vector2(a[0], a[2]);
            var end = new Vector2(b[0], b[2]);
            var xz = new Vector2(point.x, point.z);
            float t = PolylineUtils.ProjectOnSegment(xz, start, end);
            float distance = (xz - Vector2.Lerp(start, end, t)).sqrMagnitude;
            if (distance < best) { best = distance; height = Mathf.Lerp(a[1], b[1], t); }
        }
        return height;
    }
}
