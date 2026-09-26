using System;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// LRT停留所の相対式ホーム(各軌道の外側)の範囲の解決と描画。下地パスの生成器(431 は TrackPath、
/// 222/242/303 は LrtRoadParts.Generate)がメッシュ生成の一部として呼ぶので、Far・工事中には現れない。
/// 停止位置は路線サマリの stop_points(StopProvider。DataLoaders/LrtStopPlatforms が設定)で、
/// 割合→距離の写像は車両と同じ「割合×未切断3D点列長」(TransitRouteGeometry)。寸法の正は LrtRoadParts。
/// </summary>
public static class LrtPlatformParts
{
    /// <summary>1路線の1停留所。停止中心(編成の中心)の割合と、編成長に両端の余裕を足したデッキ長(m)。</summary>
    public readonly struct Stop
    {
        public readonly float Fraction, DeckLength;
        public Stop(float fraction, float deckLength) { Fraction = fraction; DeckLength = deckLength; }
    }

    /// <summary>
    /// 描画点列上のデッキ区間。From/To は描画点列の始点からのスプライン距離(m)。
    /// トンネルの描画区間で切られた端はデッキの本当の端ではないので、スロープを置かない。
    /// </summary>
    public struct Span
    {
        public float From, To;
        public bool RampAtFrom, RampAtTo;
        public float Length => To - From;
    }

    /// <summary>パスIDごとの停留所。未設定(テストシーン等)ならホームを描かない。</summary>
    public static Func<int, IReadOnlyList<Stop>> StopProvider;

    /// <summary>パスIDと交差点で切断するかから地形追従点列を返す(PathManager が設定)。未設定なら描画点列を全体とみなす。</summary>
    public static Func<int, bool, List<Vector3>> PolylineProvider;

    private const float SLAB_STEP = 1f;             // 簡易スラブの断面を置く間隔
    private const float SLAB_BOTTOM_BELOW_SPLINE = .3f; // 簡易スラブの下端。地形の起伏を吸収する深さ
    private const float END_TOLERANCE = .01f;           // 描画区間の端とデッキ端を同じ位置とみなす距離

    /// <summary>
    /// 描画点列(交差点で切断済み。トンネルでは描画区間)上のデッキ区間を返す。LRTの下地でない・停留所が無いなら空。
    /// </summary>
    public static List<Span> Resolve(PathSummary path, IReadOnlyList<Vector3> drawn, float splineLength)
    {
        if (path == null || !LrtRoadParts.IsLrt(path.path_variant) || path.status == Constants.STATUS_CONSTRUCTING
            || drawn == null || drawn.Count < 2) return new List<Span>();
        var stops = StopProvider?.Invoke(path.path_id);
        if (stops == null || stops.Count == 0) return new List<Span>();
        var full = PolylineProvider?.Invoke(path.path_id, false);
        var trimmed = PolylineProvider?.Invoke(path.path_id, true);
        return Layout(stops, full != null && full.Count >= 2 ? full : drawn, trimmed != null && trimmed.Count >= 2 ? trimmed : drawn,
            drawn, splineLength, LrtRoadParts.PlatformRampLength(path.path_variant));
    }

    /// <summary>
    /// 停留所を未切断点列上の距離区間にして併合し、両端スロープが交差点へ出ないよう切断済み区間の内側
    /// (両端からスロープ長)へ収め、収めた後に PLATFORM_MIN_LENGTH 未満のデッキは捨てる。
    /// 残ったデッキを描画点列の範囲で切り、描画点列の距離をスプライン長へ比例換算して返す。
    /// </summary>
    public static List<Span> Layout(IReadOnlyList<Stop> stops, IReadOnlyList<Vector3> full, IReadOnlyList<Vector3> trimmed,
        IReadOnlyList<Vector3> drawn, float splineLength, float rampLength)
    {
        var result = new List<Span>();
        var line = new VehiclePolyline(full);
        float trimStart = line.Project(trimmed[0]), trimEnd = line.Project(trimmed[trimmed.Count - 1]);
        float drawnStart = line.Project(drawn[0]), drawnEnd = line.Project(drawn[drawn.Count - 1]);
        if (drawnEnd - drawnStart < END_TOLERANCE || splineLength <= 0f) return result;
        float scale = splineLength / (drawnEnd - drawnStart);
        foreach (var deck in MergeDecks(stops, line.Length))
        {
            float from = Mathf.Max(deck.x, trimStart + rampLength), to = Mathf.Min(deck.y, trimEnd - rampLength);
            if (to - from < LrtRoadParts.PLATFORM_MIN_LENGTH) continue;
            float drawnFrom = Mathf.Max(from, drawnStart), drawnTo = Mathf.Min(to, drawnEnd);
            if (drawnTo - drawnFrom < END_TOLERANCE) continue;
            result.Add(new Span
            {
                From = (drawnFrom - drawnStart) * scale,
                To = (drawnTo - drawnStart) * scale,
                RampAtFrom = from >= drawnStart - END_TOLERANCE,
                RampAtTo = to <= drawnEnd + END_TOLERANCE,
            });
        }
        return result;
    }

    /// <summary>
    /// 停留所ごとのデッキを未切断点列上の距離区間(x=始端, y=終端)にし、重なりと PLATFORM_MERGE_GAP 未満の隙間を
    /// 併合して昇順で返す。複数路線が同じ停留所を使う・近くに停まる場合も1本のホームになる。
    /// </summary>
    public static List<Vector2> MergeDecks(IReadOnlyList<Stop> stops, float fullLength)
    {
        var decks = new List<Vector2>(stops.Count);
        foreach (var stop in stops)
        {
            if (stop.DeckLength <= 0f) continue;
            float center = stop.Fraction * fullLength;
            decks.Add(new Vector2(center - stop.DeckLength * .5f, center + stop.DeckLength * .5f));
        }
        decks.Sort((a, b) => a.x.CompareTo(b.x));
        var merged = new List<Vector2>(decks.Count);
        foreach (var deck in decks)
        {
            int last = merged.Count - 1;
            if (last >= 0 && deck.x - merged[last].y < LrtRoadParts.PLATFORM_MERGE_GAP)
                merged[last] = new Vector2(merged[last].x, Mathf.Max(merged[last].y, deck.y));
            else merged.Add(deck);
        }
        return merged;
    }

    /// <summary>
    /// ホーム区間(デッキ+両端スロープ+margin)をスプライン距離の区間(x=始端, y=終端)として昇順・併合済みで返す。
    /// 431 の架線柱の移設・高欄の省略と、303 の沿道設備の抑止が使う。
    /// </summary>
    public static List<Vector2> ZoneRanges(List<Span> spans, float rampLength, float margin)
    {
        var zones = new List<Vector2>();
        if (spans == null) return zones;
        foreach (var span in spans)
            zones.Add(new Vector2(span.From - rampLength - margin, span.To + rampLength + margin));
        zones.Sort((a, b) => a.x.CompareTo(b.x));
        var merged = new List<Vector2>(zones.Count);
        foreach (var zone in zones)
        {
            int last = merged.Count - 1;
            if (last >= 0 && zone.x <= merged[last].y) merged[last] = new Vector2(merged[last].x, Mathf.Max(merged[last].y, zone.y));
            else merged.Add(zone);
        }
        return merged;
    }

    public static bool Contains(IReadOnlyList<Vector2> ranges, float distance)
    {
        if (ranges == null) return false;
        foreach (var range in ranges)
            if (distance >= range.x && distance <= range.y) return true;
        return false;
    }

    /// <summary>区間の外側をスプライン percent 区間(昇順)で返す。区間が無ければ全長1区間。</summary>
    public static List<TrackCorridorInfo.PercentRange> OutsidePercentRanges(SplineComputer spline, IReadOnlyList<Vector2> ranges)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        double cursor = 0.0;
        if (ranges != null)
            foreach (var range in ranges)
            {
                double from = Percent(spline, range.x), to = Percent(spline, range.y);
                if (from > cursor) result.Add(new TrackCorridorInfo.PercentRange { From = cursor, To = from });
                cursor = Math.Max(cursor, to);
            }
        if (cursor < 1.0) result.Add(new TrackCorridorInfo.PercentRange { From = cursor, To = 1.0 });
        return result;
    }

    /// <summary>
    /// ホームを両側に描く。部品はバリアント→タイプの順で解決し、無ければスキップする。
    /// lrt_platform だけは無ければ手続き生成の簡易スラブで代替する(停留所の位置が最低限見えるように)。
    /// </summary>
    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path, List<Span> spans)
    {
        if (spans == null || spans.Count == 0 || spline == null) return;
        int variant = path.path_variant;
        string variantBase = $"Models/Paths/p{variant}", typeBase = $"Models/Paths/p{path.path_type}";
        float track = LrtRoadParts.Offset(path);
        float inner = LrtRoadParts.PlatformInnerOffset(path), width = LrtRoadParts.PlatformWidth(variant), outer = inner + width;
        float railTop = LrtRoadParts.RailTopAboveSpline(variant), surface = LrtRoadParts.PlatformSurfaceAboveSpline(variant);
        bool modeled = LoadOptional(variantBase, typeBase, "lrt_platform") != null;
        GameObject end = LoadOptional(variantBase, typeBase, "lrt_platform_end");
        GameObject shelter = LoadOptional(variantBase, typeBase, "lrt_shelter");
        // 上屋は背面(+X)を外縁に付ける。奥行きはモデルの実寸から取る。
        float shelterBack = 0f;
        MeshFilter shelterFilter = shelter != null ? shelter.GetComponent<MeshFilter>() : null;
        if (shelterFilter != null && shelterFilter.sharedMesh != null) shelterBack = shelterFilter.sharedMesh.bounds.max.x;
        foreach (var span in spans)
        {
            double from = Percent(spline, span.From), to = Percent(spline, span.To);
            if (to - from < 1e-6) continue;
            double middle = Percent(spline, (span.From + span.To) * .5f);
            for (int side = -1; side <= 1; side += 2)
            {
                // 右側用のモデルを軌道中心へ原点で置き、左側はXミラーで外向きにする。著作長4mの造形周期を保つ。
                if (modeled)
                    RoadPath.CreateSplineMesh(parent, spline, variantBase + "/lrt_platform", width, side * track, railTop, side < 0,
                        typeBase + "/lrt_platform", stretchToWidth: false, clipFrom: from, clipTo: to, compensatePivot: false,
                        maxTileLength: 0f, normalizeBodySurface: false);
                else BuildSlab(parent, spline, from, to, side, inner, outer, surface);
                float rampLateral = side * (inner + width * .5f);
                if (span.RampAtFrom) PlaceRamp(parent, end, spline, from, false, rampLateral, railTop);
                if (span.RampAtTo) PlaceRamp(parent, end, spline, to, true, rampLateral, railTop);
                if (shelter != null && span.Length >= LrtRoadParts.SHELTER_MIN_DECK_LENGTH)
                    PlaceShelter(parent, shelter, spline, middle, side, outer - shelterBack, surface);
            }
        }
    }

    private static double Percent(SplineComputer spline, float distance) =>
        RoadPath.ClampSplinePercent(spline.Travel(0.0, Mathf.Max(0f, distance), Spline.Direction.Forward));

    private static bool InDetailRange(double percent) =>
        !PathDrawContext.HasDetailRange || (percent >= PathDrawContext.DetailFrom && percent <= PathDrawContext.DetailTo);

    private static GameObject LoadOptional(string variantBase, string typeBase, string part) =>
        ModelManager.Instance?.GetModel($"{variantBase}/{part}", true) ?? ModelManager.Instance?.GetModel($"{typeBase}/{part}", true);

    /// <summary>スロープは +Z がデッキから外向きで、勾配に沿わせて傾ける(下端を前後の路面に合わせるため)。</summary>
    private static void PlaceRamp(GameObject parent, GameObject prefab, SplineComputer spline, double percent, bool outwardForward, float lateral, float railTop)
    {
        if (prefab == null || !InDetailRange(percent)) return;
        SplineSample sample = spline.Evaluate(percent);
        Vector3 flat = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) return;
        Vector3 right = Vector3.Cross(Vector3.up, flat).normalized;
        Vector3 outward = (outwardForward ? sample.forward : -sample.forward).normalized;
        Place(parent, prefab, "LrtPlatformEnd", sample.position + right * lateral + Vector3.up * railTop, Quaternion.LookRotation(outward, Vector3.up));
    }

    /// <summary>上屋は直立させ、開放面(-X)を軌道へ向ける。左側はY軸180°回転。</summary>
    private static void PlaceShelter(GameObject parent, GameObject prefab, SplineComputer spline, double percent, int side, float lateral, float surface)
    {
        if (!InDetailRange(percent)) return;
        SplineSample sample = spline.Evaluate(percent);
        Vector3 flat = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) return;
        flat.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, flat);
        Quaternion rotation = Quaternion.LookRotation(flat, Vector3.up) * (side < 0 ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity);
        Place(parent, prefab, "LrtShelter", sample.position + right * (side * lateral) + Vector3.up * surface, rotation);
    }

    private static void Place(GameObject parent, GameObject prefab, string name, Vector3 position, Quaternion rotation)
    {
        var placed = UnityEngine.Object.Instantiate(prefab, position, rotation, parent.transform);
        placed.name = name;
        foreach (var renderer in placed.GetComponentsInChildren<MeshRenderer>())
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) materials[i] = RoadPath.GetOrCreateRoadMaterial(materials[i]);
            renderer.sharedMaterials = materials;
        }
    }

    /// <summary>
    /// lrt_platform が無いときの代替。内縁〜外縁・上面〜スプライン下 SLAB_BOTTOM_BELOW_SPLINE の箱断面を
    /// スプラインに沿って押し出す(上面・両側面・底面・両端の小口)。材質は共通コンクリート。
    /// </summary>
    private static void BuildSlab(GameObject parent, SplineComputer spline, double from, double to, int side, float inner, float outer, float surface)
    {
        if (!PathDrawContext.IntersectDetailRange(from, to, out from, out to)) return;
        int steps = Mathf.Max(1, Mathf.CeilToInt(spline.CalculateLength() * (float)(to - from) / SLAB_STEP));
        var rings = new Vector3[steps + 1][];
        var normals = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            SplineSample sample = spline.Evaluate(RoadPath.ClampSplinePercent(from + (to - from) * i / steps));
            Vector3 flat = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, flat).normalized * side;
            Vector3 p = sample.position;
            rings[i] = new[]
            {
                parent.transform.InverseTransformPoint(p + right * inner + Vector3.up * surface),
                parent.transform.InverseTransformPoint(p + right * outer + Vector3.up * surface),
                parent.transform.InverseTransformPoint(p + right * outer - Vector3.up * SLAB_BOTTOM_BELOW_SPLINE),
                parent.transform.InverseTransformPoint(p + right * inner - Vector3.up * SLAB_BOTTOM_BELOW_SPLINE),
            };
            normals[i] = parent.transform.InverseTransformDirection(flat.normalized);
        }
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i < steps; i++)
            for (int k = 0; k < 4; k++)
            {
                Vector3 a = rings[i][k], b = rings[i][(k + 1) % 4], c = rings[i + 1][(k + 1) % 4], d = rings[i + 1][k];
                Vector3 center = (rings[i][0] + rings[i][2]) * .5f;
                AddQuad(vertices, triangles, a, b, c, d, (a + b) * .5f - center);
            }
        AddQuad(vertices, triangles, rings[0][0], rings[0][1], rings[0][2], rings[0][3], -normals[0]);
        AddQuad(vertices, triangles, rings[steps][0], rings[steps][1], rings[steps][2], rings[steps][3], normals[steps]);
        var mesh = new Mesh { name = "LrtPlatformSlab" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        var go = new GameObject("LrtPlatformSlab");
        go.transform.SetParent(parent.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = BuildingMeshBuilder.GetCommonConcreteMaterial();
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
    }

    /// <summary>面ごとに頂点を分けて法線を割らない。巻き順は外向きの目安 outward に合わせる。</summary>
    private static void AddQuad(List<Vector3> vertices, List<int> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
    {
        int n = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        bool forward = Vector3.Dot(Vector3.Cross(b - a, c - a), outward) >= 0f;
        if (forward) triangles.AddRange(new[] { n, n + 1, n + 2, n, n + 2, n + 3 });
        else triangles.AddRange(new[] { n, n + 2, n + 1, n, n + 3, n + 2 });
    }
}
