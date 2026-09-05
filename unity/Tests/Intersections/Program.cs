using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using GeneratedModels;
using NetTopologySuite.Geometries;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Main()
    {
        int layouts = 0, constrained = 0;
        var factory = new GeometryFactory();
        foreach (float length in new[] { 4f, 20f, 100f })
        foreach (float width in new[] { 2f, 8f, 24f })
        foreach (float angle in new[] { 15f, 45f, 90f, 150f, 179f, 180f })
        foreach (int count in new[] { 2, 3, 4 })
        {
            var paths = new List<PathSummary>();
            var points = new Dictionary<int, List<Vector3>>();
            for (int i = 0; i < count; i++)
            {
                float a = (i == 0 ? 0 : i == 1 ? angle : i == 2 ? 240 : 300) * MathF.PI / 180;
                paths.Add(new PathSummary { path_id = i + 1, start_node_id = 1, end_node_id = 100 + i, width = i == 0 ? width : 8 });
                points[i + 1] = new List<Vector3> { Vector3.zero, new Vector3(MathF.Cos(a) * length, 3, MathF.Sin(a) * length) };
            }
            var result = IntersectionPolygonUtil.ComputeIntersectionPolygon(1, Vector2.zero, paths, factory, null, p => points[p.path_id]);
            if (result.RequiresMoreSpace)
            {
                Check(length < 100, $"十分な長さがある配置を拒否しました。 width={width} angle={angle} count={count}");
                Check(result.Sections.Count == 0 && result.ConnectionPoints.Count == 0, "空間不足時に切断情報が残っています。");
                constrained++;
                continue;
            }
            Check(result.Geometry.IsValid && result.Geometry.Area > 0, "無効な交差点です。");
            foreach (var section in result.Sections.Values)
            {
                Check(section.Distance <= length * .951f, "両端の切断区間が重なります。");
                foreach (float side in new[] { -1f, 0f, 1f })
                {
                    var p = section.Center + section.Left * section.Width * .5f * side;
                    Check(result.Geometry.Boundary.Distance(factory.CreatePoint(new Coordinate(p.x, p.y))) < .001f, "入口断面が境界から欠落しました。");
                }
            }
            paths.Reverse();
            var reversed = IntersectionPolygonUtil.ComputeIntersectionPolygon(1, Vector2.zero, paths, factory, null, p => points[p.path_id]);
            Check(result.Geometry.EqualsTopologically(reversed.Geometry), "入力順で輪郭が変化しました。");
            layouts++;
        }
        var curve = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(10, 5, 0), new Vector3(15, 8, 8), new Vector3(20, 12, 20) };
        float total = IntersectionPolygonUtil.Length(curve);
        var start = IntersectionPolygonUtil.AtDistance(curve, 3);
        var end = IntersectionPolygonUtil.AtDistance(curve, total - 4);
        var trimmed = IntersectionPolygonUtil.Trim(curve, start.Position, end.Position);
        Check(Vector3.Distance(trimmed[0], start.Position) < .0001f && Vector3.Distance(trimmed[trimmed.Count - 1], end.Position) < .0001f, "切断位置が一致しません。");
        Check(trimmed.Contains(curve[1]) && trimmed.Contains(curve[2]), "切断によって内部の曲線が移動しました。");
        Check(MathF.Abs(IntersectionPolygonUtil.Length(trimmed) - (total - 7)) < .001f, "切断後の経路長が一致しません。");
        var controlled = new PathSummary { path_id=1, start_node_id=1, end_node_id=2, curved=true,
            start_point=new List<object>{0f,0f,0f}, end_point=new List<object>{30f,0f,0f},
            start_control=new List<object>{0f,0f,10f}, end_control=new List<object>{30f,0f,10f} };
        Check(RoadIntersectionMeshGenerator.TryGetPathDirectionFromNode(1,controlled,out var origin,out var reference)
            && Vector2.Dot((reference-origin).normalized,Vector2.up) > .999f, "始点制御点が接線に反映されません。");
        Check(RoadIntersectionMeshGenerator.TryGetPathDirectionFromNode(2,controlled,out origin,out reference)
            && Vector2.Dot((reference-origin).normalized,Vector2.up) > .999f, "終点制御点が接線に反映されません。");
        controlled = Newtonsoft.Json.JsonConvert.DeserializeObject<PathSummary>(Newtonsoft.Json.JsonConvert.SerializeObject(controlled));
        var sampled = IntersectionPolygonUtil.SamplePlan(controlled);
        Check(sampled.Exists(p=>p.z>5f), "保存された制御点が曲線に反映されません。");
        controlled.curved=false;
        Check(RoadIntersectionMeshGenerator.TryGetPathDirectionFromNode(1,controlled,out origin,out reference)
            && Vector2.Dot((reference-origin).normalized,Vector2.right) > .999f, "折れ線が無効な制御点を参照しています。");
        Console.WriteLine($"PASS: {layouts} layouts, {constrained} constrained layouts, original curve preservation, input-order invariance.");
    }
}
