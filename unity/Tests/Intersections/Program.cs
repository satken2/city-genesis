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
        foreach (int type in new[] { Constants.PATH_CONVEYOR, Constants.PATH_ELECTRICITY })
        {
            var cables = new List<PathSummary> {
                new PathSummary { path_type = type, path_variant = 601 },
                new PathSummary { path_type = type, path_variant = 602 },
            };
            Check(NodeMeshFactory.GenerateNodeMesh(-1, Vector3.zero, cables) == null,
                "ケーブルの折れ点・駅接続に路面が生成されました。");
            cables.Add(new PathSummary { path_type = Constants.PATH_STREET });
            Check(NodeMeshFactory.GenerateNodeMesh(1, Vector3.zero, cables) == null,
                "ケーブルが道路の交差点形状に含まれました。");
        }
        foreach (double angle in new[] { 0, 4.99, 5, 5.01, -5.01, 90, 180 })
        {
            var next = new Vector3((float)(100 * Math.Cos(angle * Math.PI / 180)), 400,
                (float)(100 * Math.Sin(angle * Math.PI / 180)));
            double actual = RoadPlanManager.RopewayBendAngle(new Vector3(-100, 200, 0), Vector3.zero, next);
            Check(Math.Abs(actual - Math.Abs(angle)) < .0001, "ロープウェイの水平折れ角が不正です。");
            Check((actual <= 5.001) == (Math.Abs(angle) <= 5), "5度境界の許否が不正です。");
        }
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
        int merges = CheckMotorwayMerges();
        int junctions = CheckTrackJunctions();
        string surfaces = CheckSlopedSurfaces(factory);
        Console.WriteLine($"PASS: {layouts} layouts, {constrained} constrained layouts, original curve preservation, input-order invariance, {merges} motorway merge checks, {junctions} track junction checks, sloped surfaces [{surfaces}].");
    }

    /// <summary>
    /// 勾配のある交差点の路面を、本編と同じ三角形分割・高さ・法線で組み立てて検査する。
    /// 隣り合う三角形の折れ角、頂点法線と面の向きの差、口での本線の勾配・法線との差、頂点数を測る。
    /// </summary>
    private static string CheckSlopedSurfaces(GeometryFactory factory)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var generator = typeof(RoadIntersectionMeshGenerator);
        var triangulate = generator.GetMethod("TriangulateSurface", flags);
        var surfacePoint = generator.GetMethod("SectionSurfacePoint", flags);
        var surfaceNormal = generator.GetMethod("SurfaceNormal", flags);
        var isLevel = generator.GetMethod("IsLevel", flags);
        float Grade(float degrees, float slope) => slope * MathF.Cos(degrees * MathF.PI / 180);
        var far = new Vector2(7000f, -3000f);
        var cases = new (string name, float[] degrees, float[] grades, float[] widths, Vector2 origin)[]
        {
            ("steep-y", new[] { 0f, 120f, 240f }, new[] { .2f, -.1f, 0f }, new[] { 16f, 8f, 8f }, Vector2.zero),
            ("plane8-y", new[] { 30f, 150f, 270f }, new[] { Grade(30, .08f), Grade(150, .08f), Grade(270, .08f) }, new[] { 12f, 12f, 8f }, Vector2.zero),
            ("plane8-y-far", new[] { 30f, 150f, 270f }, new[] { Grade(30, .08f), Grade(150, .08f), Grade(270, .08f) }, new[] { 12f, 12f, 8f }, far),
            ("plane6-cross", new[] { 0f, 90f, 180f, 270f }, new[] { .06f, 0f, -.06f, 0f }, new[] { 12f, 12f, 12f, 12f }, Vector2.zero),
            ("crest-y", new[] { 0f, 110f, 230f }, new[] { -.05f, -.05f, -.05f }, new[] { 12f, 8f, 8f }, Vector2.zero),
            ("flat-cross", new[] { 0f, 90f, 180f, 270f }, new[] { 0f, 0f, 0f, 0f }, new[] { 12f, 12f, 12f, 12f }, Vector2.zero),
            ("wide-cross", new[] { 10f, 100f, 190f, 280f }, new[] { Grade(10, .05f), Grade(100, .05f), Grade(190, .05f), Grade(280, .05f) }, new[] { 30f, 30f, 30f, 30f }, Vector2.zero),
        };
        var report = new List<string>();
        foreach (var c in cases)
        {
            var paths = new List<PathSummary>();
            var points = new Dictionary<int, List<Vector3>>();
            for (int i = 0; i < c.degrees.Length; i++)
            {
                paths.Add(new PathSummary { path_id = i + 1, start_node_id = 1, end_node_id = 100 + i, width = c.widths[i] });
                points[i + 1] = Ray(c.degrees[i], 80f, c.grades[i]).Select(p => p + new Vector3(c.origin.x, 0f, c.origin.y)).ToList();
            }
            var result = IntersectionPolygonUtil.ComputeIntersectionPolygon(1, c.origin, paths, factory, null, p => points[p.path_id]);
            Check(!result.RequiresMoreSpace, $"{c.name}: 交差点を組み立てられません。");
            var plan = new List<Vector2>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool refine = !(bool)isLevel.Invoke(null, new object[] { result.Sections });
            var triangles = (List<int>)triangulate.Invoke(null, new object[] { result.Geometry, plan, refine });
            watch.Stop();
            var vertices = plan.Select(p => (Vector3)surfacePoint.Invoke(null, new object[] { new Vector3(p.x, 0f, p.y), result.Sections, 0f, null })).ToList();
            Vector3 NormalAt(int v) => (Vector3)surfaceNormal.Invoke(null, new object[] { vertices[v], result.Sections, null });
            var faces = new List<Vector3>();
            var edges = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], d = triangles[t + 2];
                Vector3 cross = Vector3.Cross(vertices[b] - vertices[a], vertices[d] - vertices[a]);
                faces.Add((cross.y < 0f ? -cross : cross).normalized);
                foreach (var (u, w) in new[] { (a, b), (b, d), (d, a) })
                {
                    var key = (Math.Min(u, w), Math.Max(u, w));
                    if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<int>();
                    list.Add(t / 3);
                }
            }
            Check(edges.Values.All(list => list.Count <= 2), $"{c.name}: 路面の三角形が重なっています。");
            float fold = 0f, shade = 0f, mouthNormal = 0f, mouthGrade = 0f;
            foreach (var list in edges.Values)
                if (list.Count == 2) fold = MathF.Max(fold, Vector3.Angle(faces[list[0]], faces[list[1]]));
            for (int t = 0; t < triangles.Count; t++)
                shade = MathF.Max(shade, Vector3.Angle(NormalAt(triangles[t]), faces[t / 3]));
            foreach (var section in result.Sections.Values)
            {
                for (int v = 0; v < vertices.Count; v++)
                {
                    Vector2 delta = plan[v] - section.Center;
                    if (MathF.Abs(Vector2.Dot(delta, section.Direction)) > 1e-3f || MathF.Abs(Vector2.Dot(delta, section.Left)) > section.Width * .5f + 1e-3f) continue;
                    mouthNormal = MathF.Max(mouthNormal, Vector3.Angle(NormalAt(v), section.Normal));
                }
                Vector2 inner = section.Center - section.Direction * .5f;
                float innerY = ((Vector3)surfacePoint.Invoke(null, new object[] { new Vector3(inner.x, 0f, inner.y), result.Sections, 0f, null })).y;
                float grade = section.Tangent.y / new Vector2(section.Tangent.x, section.Tangent.z).magnitude;
                mouthGrade = MathF.Max(mouthGrade, MathF.Abs((section.Position.y - innerY) / .5f - grade));
            }
            int ring = ((Polygon)result.Geometry).ExteriorRing.NumPoints - 1;
            Check(refine == (c.grades.Any(g => g != 0f)), $"{c.name}: 内部頂点を置くかの判定が路面の勾配と合いません。");
            Check(refine || vertices.Count == ring, $"{c.name}: 平面の交差点に内部頂点が置かれました。");
            float budget = ring * 2 + (float)result.Geometry.Area / (LocalConstants.INTERSECTION_SURFACE_VERTEX_SPACING * LocalConstants.INTERSECTION_SURFACE_VERTEX_SPACING) * 2f;
            Check(fold < 8f, $"{c.name}: 隣り合う三角形が {fold}° 折れています。");
            Check(shade < 8f, $"{c.name}: 頂点法線が三角形の向きから {shade}° 外れています。");
            Check(mouthNormal < .5f, $"{c.name}: 口の法線が本線の断面法線から {mouthNormal}° 外れています。");
            Check(mouthGrade < .01f, $"{c.name}: 口の内側の勾配が本線の縦断勾配から {mouthGrade} 外れています。");
            Check(vertices.Count <= budget, $"{c.name}: 頂点数 {vertices.Count} が面積に対して多すぎます(上限 {budget:0})。");
            report.Add($"{c.name}: vertices={vertices.Count} fold={fold:0.0}° shade={shade:0.0}° {watch.Elapsed.TotalMilliseconds:0.0}ms");
        }
        return string.Join("; ", report);
    }

    private static readonly Dictionary<int, PathMasterItem> masters = new()
    {
        [101] = new PathMasterItem { width = 14f, lanes = 4, sidewalk_width = 0f, side_width = .5f, center_width = 1f, wall_width = .2f },
        [102] = new PathMasterItem { width = 5.5f, lanes = 1, sidewalk_width = 0f, side_width = .5f, wall_width = .2f, ramp = true },
        [221] = new PathMasterItem { width = 12f, lanes = 2, sidewalk_width = 2.5f, side_width = .5f },
    };

    private static PathSummary Arm(int id, int variant, int start, int end, int type = Constants.PATH_MOTORWAY, int status = Constants.STATUS_ACTIVE)
        => new PathSummary { path_id = id, path_variant = variant, path_type = type, status = status, start_node_id = start, end_node_id = end, width = masters[variant].width };

    /// <summary>ノードから外向きに、指定方位(度。0=+X)と縦断勾配で伸びる1m間隔の直線。</summary>
    private static List<Vector3> Ray(float degrees, float length, float grade, float lateral = 0f)
    {
        var direction = new Vector3(MathF.Cos(degrees * MathF.PI / 180), 0, MathF.Sin(degrees * MathF.PI / 180));
        var side = new Vector3(-direction.z, 0, direction.x);
        var points = new List<Vector3>();
        for (float s = 0; s <= length + 1e-3f; s += 1f) points.Add(direction * s + side * lateral + Vector3.up * (10 + grade * s));
        return points;
    }

    /// <summary>始点から終点まで1m間隔の直線。</summary>
    private static List<Vector3> Straight(Vector3 from, Vector3 to)
    {
        int count = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to)));
        var points = new List<Vector3>();
        for (int i = 0; i <= count; i++) points.Add(Vector3.Lerp(from, to, (float)i / count));
        return points;
    }

    /// <summary>
    /// 軌道の位置が違う線路のつなぎ目のS字(TrackEndTransition。N文書§4.7)の式と、完成前の線路(仮の相手)との
    /// 近接線路の結合の担当(N文書§8.5)を検査する。マスターを使わない部分だけで、ノードでの組の選び方・板なしの接続・
    /// メッシュは Unity の PlatformQA が検査する。
    /// </summary>
    private static int CheckTrackJunctions()
    {
        int checks = 0;
        // 421(±2.5m)→472(±5.5m)。前後20.81mずつを受け持つ1本のS字で、ノードで両方とも±4.0mになり、向きもそろう。
        const float own = 20.81f;
        var bundled = new TrackEndTransition.EndShift { Delta = new[] { -3f, 3f }, Own = own, Span = 2 * own };
        var island = new TrackEndTransition.EndShift { Delta = new[] { 3f, -3f }, Own = own, Span = 2 * own };
        float Bundled(float d) => 2.5f + bundled.Shift(1, d);
        float Island(float d) => 5.5f + island.Shift(1, d);
        Check(MathF.Abs(Bundled(0f) - 4f) < 1e-4f && MathF.Abs(Island(0f) - 4f) < 1e-4f, "S字の両側がノードで±4.0mにそろいません。");
        Check(MathF.Abs(Bundled(own) - 2.5f) < 1e-4f && MathF.Abs(Island(own) - 5.5f) < 1e-4f, "S字が受け持ちの外へはみ出します。");
        float slopeIn = (Bundled(0f) - Bundled(.01f)) / .01f, slopeOut = (Island(.01f) - Island(0f)) / .01f;
        Check(MathF.Abs(slopeIn - slopeOut) < .01f, $"ノードで軌道の向きが折れます: {slopeIn} / {slopeOut}");
        Check(MathF.Abs(bundled.Delta[1] * bundled.WeightSlope(0f) + slopeIn) < .01f, "S字の傾きの式が数値微分と一致しません。");
        float radius = float.MaxValue;
        for (float d = .5f; d < own - .5f; d += .25f)
        {
            float second = (Bundled(d - .5f) - 2f * Bundled(d) + Bundled(d + .5f)) / .25f;
            if (MathF.Abs(second) > 1e-6f) radius = MathF.Min(radius, 1f / MathF.Abs(second));
        }
        Check(radius > 99f, $"S字の曲率半径が最小曲線半径を下回ります: {radius}");
        var profile = new TrackLateralProfile(new[] { -2.5f, 2.5f }, null, bundled);
        Check(MathF.Abs(profile.Lateral(0, 64f, 0f) + 4f) < 1e-4f && MathF.Abs(profile.OuterShift(64f, 0f) - 1.5f) < 1e-4f, "横位置の形が終点のS字を読みません。");
        Check(MathF.Abs(profile.Lateral(1, 10f, 54f) - 2.5f) < 1e-4f && !profile.IsUniform, "S字の外の横位置が元の位置ではありません。");
        checks += 7;

        // 完成した線路と仮の相手(計画中・建設中・プレビュー)の組では、間の充填は仮の相手が描き、架線柱は共有しない。
        var own0 = Straight(new Vector3(-50, 0, 0), new Vector3(50, 0, 0));
        var parallel = Straight(new Vector3(-50, 0, 6f), new Vector3(50, 0, 6f));
        int mid = own0.Count / 2;
        TrackCorridorInfo Corridor(bool neighborProvisional, bool ownProvisional) => TrackAdjacency.ComputeWithShifts(9, own0, 5f, .4f, 0f, 0f,
            new List<TrackNeighborInfo> { new() { PathId = 5, Points = parallel, Width = 5f, Provisional = neighborProvisional } }, 0f, null, ownProvisional);
        float Fill(TrackCorridorInfo c) => !float.IsNaN(c.LeftFillTo[mid]) ? c.LeftFillTo[mid] : c.RightFillTo[mid];
        var done = Corridor(false, false);
        var completedSide = Corridor(true, false);
        var provisionalSide = Corridor(false, true);
        Check(new[] { done, completedSide, provisionalSide }.All(c => c != null && (c.LeftCovered[mid] || c.RightCovered[mid])), "完成した線路と仮の相手の間の壁が残ります。");
        Check(float.IsNaN(Fill(done)) && float.IsNaN(Fill(completedSide)) && !float.IsNaN(Fill(provisionalSide)), "道床の隙間の充填の担当が完成側と仮の側で入れ替わっています。");
        Check(done.SuppressPoleSet[mid] && !completedSide.SuppressPoleSet[mid] && !provisionalSide.SuppressPoleSet[mid], "完成した線路と仮の相手が架線柱を共有しています。");
        Check(MathF.Abs(MathF.Max(completedSide.LeftExtent[mid], completedSide.RightExtent[mid]) - 2.5f) < 1e-3f, "共有しない架線柱が相手の幅まで張り出します。");
        checks += 4;

        // 島式の結合: 完成側は単独のときのデッキの外縁(5.5m)まで、仮の側が残りを張って島をつなぐ。
        var partner = Straight(new Vector3(-50, 0, 9f), new Vector3(50, 0, 9f));
        TrackCorridorInfo IslandCorridor(bool partnerProvisional, bool ownProvisional) => TrackAdjacency.ComputeWithShifts(9, own0, 5f, 0f, 3f, 0f,
            new List<TrackNeighborInfo> { new() { PathId = 1, Points = partner, Width = 5f, PlatformRight = 3f, PlatformEdge = 1.5f, Provisional = partnerProvisional } }, 0f, null, ownProvisional);
        var completedStation = IslandCorridor(true, false);
        var provisionalStation = IslandCorridor(false, true);
        var bothDone = IslandCorridor(false, false);
        Check(completedStation != null && MathF.Abs(completedStation.LeftDeckFar[mid] - 5.5f) < 1e-3f && !completedStation.LeftDeckJoined[mid], "完成側のデッキが単独の外縁から変わりました。");
        Check(provisionalStation != null && MathF.Abs(provisionalStation.LeftDeckFar[mid] - 3.5f) < 1e-3f && provisionalStation.LeftDeckJoined[mid], "仮の側のデッキが完成側の外縁まで届きません。");
        Check(bothDone != null && float.IsNaN(bothDone.LeftDeckFar[mid]), "完成どうしの島のデッキを path_id の大きい側が張りました。");
        Check(completedStation.LeftPlatformWallOmit[mid] && provisionalStation.LeftPlatformWallOmit[mid], "島式の結合で外縁の柵が残ります。");
        checks += 4;
        return checks;
    }

    private static int CheckMotorwayMerges()
    {
        int checks = 0;
        const float sink = LocalConstants.ROAD_MERGE_SINK;

        // 並走: 隙間1mのランプは本線の右側で結合し、隙間を本線が舗装し、ランプは本線の高さへ寄る。
        var main = Ray(0, 200, .03f);
        var parallel = Ray(0, 120, 0f, -(7f + 1f + 2.75f));
        for (int i = 0; i < parallel.Count; i++) parallel[i] += new Vector3(40f, .03f * (parallel[i].x + 40f) + .3f, 0f);
        var mainCorridor = RoadAdjacency.Compute(main, 14f, .2f, false, new List<RoadNeighborInfo> { new() { PathId = 2, Points = parallel, Width = 5.5f } });
        Check(mainCorridor != null, "並走するランプが結合されません。");
        bool rightSide = mainCorridor.RightMerged[80];
        Check(rightSide != mainCorridor.LeftMerged[80] && !mainCorridor.RightMerged[10] && !mainCorridor.LeftMerged[10], "結合区間が並走区間と一致しません。");
        float fill = rightSide ? mainCorridor.RightFillTo[80] : mainCorridor.LeftFillTo[80];
        Check(MathF.Abs(fill - 8f) < .01f, "隙間の舗装がランプの路面の縁まで届きません。");
        var walls = mainCorridor.GetWallPercentIntervals(!rightSide, 1f);
        Check(walls.Count == 2 && walls[0].To * 200 < 42 && walls[1].From * 200 > 158, "ランプ側の壁が並走区間で省かれません。");
        Check(mainCorridor.GetWallPercentIntervals(rightSide, 1f).Count == 1, "反対側の壁が欠けました。");
        var rampCorridor = RoadAdjacency.Compute(parallel, 5.5f, .2f, true, new List<RoadNeighborInfo> { new() { PathId = 1, Points = main, Width = 14f } });
        Check(rampCorridor != null && !float.IsNaN(rampCorridor.TargetY[60]) && MathF.Abs(rampCorridor.TargetY[60] - main[100].y) < .01f, "ランプの寄せ先が本線の路面の高さになりません。");
        var lifted = RoadAdjacency.ApplyRampHeights(parallel, rampCorridor);
        Check(MathF.Abs(lifted[60].y - (main[100].y - sink)) < .001f, "結合区間のランプが本線より少し下の高さになりません。");
        checks += 6;

        // 隙間が広い・高さが離れて隣り合うだけの道路は結合しない。路面が重なる区間は高さが離れていても結合する。
        var far = Ray(0, 120, 0f, -(7f + 3f + 2.75f));
        Check(RoadAdjacency.Compute(main, 14f, .2f, false, new List<RoadNeighborInfo> { new() { PathId = 2, Points = far, Width = 5.5f } }) == null, "離れた並走が結合されました。");
        var low = Ray(0, 120, 0f, -(7f + 1f + 2.75f));
        for (int i = 0; i < low.Count; i++) low[i] += Vector3.down * 2f;
        Check(RoadAdjacency.Compute(main, 14f, .2f, false, new List<RoadNeighborInfo> { new() { PathId = 2, Points = low, Width = 5.5f } }) == null, "高さの異なる隣接路が結合されました。");
        var overlapping = Ray(0, 120, 0f, -(7f - 1f + 2.75f));
        for (int i = 0; i < overlapping.Count; i++) overlapping[i] += Vector3.down * 2f;
        Check(RoadAdjacency.Compute(overlapping, 5.5f, .2f, true, new List<RoadNeighborInfo> { new() { PathId = 1, Points = main, Width = 14f } }) != null, "路面の重なるランプが結合されません。");
        checks += 3;

        // 浅い角度で本線から分かれて下るランプ: 本線の路面に重なる区間は本線へ寄せ、外側は勾配の上限内で元の縦断へ戻す。
        var mainArm = Ray(0, 200, 0f);
        var ramp = Ray(20, 150, -.06f);
        var diverge = RoadAdjacency.Compute(ramp, 5.5f, .2f, true, new List<RoadNeighborInfo> { new() { PathId = 1, Points = mainArm, Width = 14f } });
        Check(diverge != null && !float.IsNaN(diverge.TargetY[5]) && float.IsNaN(diverge.TargetY[60]), "取り付け部の結合区間が不正です。");
        var shaped = RoadAdjacency.ApplyRampHeights(ramp, diverge);
        Check(MathF.Abs(shaped[5].y - (10 - sink)) < .001f, "本線と重なるランプの路面が本線の高さへ寄りません。");
        Check(MathF.Abs(shaped[shaped.Count - 1].y - ramp[ramp.Count - 1].y) < .001f, "ランプの遠い端の高さが変わりました。");
        float steepest = 0;
        for (int i = 1; i < shaped.Count; i++)
            if (float.IsNaN(diverge.TargetY[i - 1]))
                steepest = MathF.Max(steepest, MathF.Abs((shaped[i].y - ramp[i].y) - (shaped[i - 1].y - ramp[i - 1].y)));
        Check(steepest <= LocalConstants.ROAD_MERGE_MAX_ADDED_GRADE + .005f, "元の縦断へ戻す区間の勾配が急すぎます。");
        checks += 4;

        // ノード: 一直線の本線と浅い角度のランプは板を作らず、ランプを本線の路面へ入りきる位置で切る。
        foreach (float angle in new[] { 10f, 20f })
        foreach (bool reversed in new[] { false, true })
        {
            var points = new Dictionary<int, List<Vector3>> { [1] = Ray(180, 150, 0f), [2] = Ray(0, 150, 0f), [3] = Ray(angle, 150, 0f), [4] = Ray(-angle, 150, 0f) };
            var paths = new List<PathSummary> { Arm(1, 101, 10, 1), Arm(2, 101, 1, 11), Arm(3, 102, reversed ? 12 : 1, reversed ? 1 : 12), Arm(4, 102, 1, 13) };
            List<Vector3> Resolve(PathSummary p) { var list = new List<Vector3>(points[p.path_id]); if (p.end_node_id == 1) list.Reverse(); return list; }
            var layout = MotorwayMergeLayout.Create(1, paths, Resolve, p => 100f, v => masters[v]);
            Check(layout != null && !layout.NeedsSlab && layout.MainA == 1 && layout.MainB == 2, "一直線の本線とランプで板が作られました。");
            float expected = (7f - 2.75f) / MathF.Sin(angle * MathF.PI / 180);
            foreach (int rampId in new[] { 3, 4 })
            {
                float cut = IntersectionPolygonUtil.XZ(layout.ConnectionPoints[rampId]).magnitude;
                Check(MathF.Abs(cut - expected) < .05f, $"ランプの切断位置が本線の縁と一致しません: {cut} / {expected}");
            }
            Check(layout.ConnectionPoints[1] == points[1][0] && layout.ConnectionPoints[2] == points[2][0], "本線がノードで切られました。");
            checks += 4;
        }

        {
            var points = new Dictionary<int, List<Vector3>> { [1] = Ray(180, 150, 0f), [2] = Ray(0, 150, 0f), [3] = Ray(45, 150, 0f) };
            var paths = new List<PathSummary> { Arm(1, 101, 10, 1), Arm(2, 101, 1, 11), Arm(3, 102, 1, 12) };
            List<Vector3> Resolve(PathSummary p) { var list = new List<Vector3>(points[p.path_id]); if (p.end_node_id == 1) list.Reverse(); return list; }
            var steep = MotorwayMergeLayout.Create(1, paths, Resolve, p => 100f, v => masters[v]);
            Check(steep != null && steep.NeedsSlab && steep.Surface != null, "角度の大きいランプを本線へ取り込みました。");
            paths[2] = Arm(3, 102, 1, 12, status: Constants.STATUS_CONSTRUCTING);
            points[3] = Ray(20, 150, 0f);
            Check(MotorwayMergeLayout.Create(1, paths, Resolve, p => 100f, v => masters[v]).NeedsSlab, "工事中のランプを本線へ取り込みました。");
            points[2] = Ray(10, 150, 0f);
            paths[2] = Arm(3, 102, 1, 12);
            Check(MotorwayMergeLayout.Create(1, paths, Resolve, p => 100f, v => masters[v]).NeedsSlab, "折れた本線を板なしでつなぎました。");
            var streets = new List<PathSummary> { Arm(1, 221, 10, 1, Constants.PATH_STREET), Arm(2, 221, 1, 11, Constants.PATH_STREET), Arm(3, 221, 1, 12, Constants.PATH_STREET) };
            Check(MotorwayMergeLayout.Create(1, streets, Resolve, p => 100f, v => masters[v]) == null, "一般道路に本線の扱いを適用しました。");
            Check(MotorwayMergeLayout.Create(-1, paths, Resolve, p => 100f, v => masters[v]) == null, "折れ線の仮ノードに本線の扱いを適用しました。");
            checks += 5;
        }

        // 板を作る交差点でも本線の上は本線の縦断そのもの。口で水平になって折れない。
        {
            var sloped = new Dictionary<int, List<Vector3>> { [1] = Ray(180, 150, -.05f), [2] = Ray(0, 150, .05f), [3] = Ray(60, 150, 0f) };
            var surface = new MainLineSurface(1, sloped[1], 2, sloped[2], 14f);
            var sections = new Dictionary<int, PathEndSection>();
            foreach (var pair in sloped)
            {
                var section = IntersectionPolygonUtil.AtDistance(pair.Value, 12f);
                section.Width = pair.Key == 3 ? 5.5f : 14f;
                sections[pair.Key] = section;
            }
            var method = typeof(RoadIntersectionMeshGenerator).GetMethod("SectionSurfacePoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            float SurfaceAt(Vector3 p, MainLineSurface main) => ((Vector3)method.Invoke(null, new object[] { p, sections, 0f, main })).y;
            float worst = 0;
            for (float x = -11.5f; x <= 11.5f; x += .5f)
                worst = MathF.Max(worst, MathF.Abs(SurfaceAt(new Vector3(x, 0, 2f), surface) - (10 + .05f * x)));
            Check(worst < .001f, $"本線の上の高さが本線の縦断から外れます: {worst}");
            float plain = 0;
            for (float x = -11.5f; x <= 11.5f; x += .5f)
                plain = MathF.Max(plain, MathF.Abs(SurfaceAt(new Vector3(x, 0, 2f), null) - (10 + .05f * x)));
            Check(plain > .01f, $"本線を使わない補間でも本線の縦断と一致しています(検査条件の誤り): {plain}");
            checks += 2;
        }
        return checks;
    }
}
