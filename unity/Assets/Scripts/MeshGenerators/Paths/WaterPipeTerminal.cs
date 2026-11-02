using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>施設端点を、導水管が壁に入り込むコンクリート製の接続設備で覆う。</summary>
public static class WaterPipeTerminal
{
    public static Func<int, Vector3?> FacilityTerminalPosition;

    public static void Generate(GameObject root, PathSummary path, List<Vector3> points)
    {
        if (root == null || path.path_type != Constants.PATH_WATER || points == null || points.Count < 2
            || PathDrawContext.CurrentDrawLod == DrawLod.Far) return;

        RoadPath.GetPathMasterParams(path, out float width, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        var body = PathDrawContext.ResolvePrefab($"Models/Paths/p{path.path_variant}/body", $"Models/Paths/p{path.path_type}/body", out _);
        var source = body != null ? body.GetComponent<MeshFilter>()?.sharedMesh : null;
        if (source == null) return;
        var metal = body.GetComponent<MeshRenderer>()?.sharedMaterial;
        metal = RoadPath.GetOrCreateRoadMaterial(metal) ?? RoadPath.GetOrCreateRoadMaterialByName("BuildingMaterial");
        var concrete = RoadPath.GetOrCreateRoadMaterialByName("COMMON_CONCRETE") ?? metal;

        // body の横幅は上面の頂点で合わせるため、管全体の幅はマスターの幅より大きくなる。
        float renderedWidth = width * source.bounds.size.x / Mathf.Max(.001f, RoadPath.GetBodySurfaceBounds(source).size.x);
        float centerY = RoadPartHeights.BODY_ABOVE_SPLINE - source.bounds.extents.y;
        for (int end = 0; end < 2; end++)
        {
            bool start = end == 0;
            int node = start ? path.start_node_id : path.end_node_id;
            if (node <= 0 || FacilityTerminalPosition?.Invoke(node) == null) continue;
            var anchor = start ? path.GetStartPointList() : path.GetEndPointList();
            int index = start ? 0 : points.Count - 1;
            Vector3 point = points[index];
            // トンネルや長いパスの部分生成では、本来の端点を含む区間だけに置く。
            if (anchor == null || anchor.Count < 2 || Vector2.Distance(new Vector2(point.x, point.z),
                new Vector2(anchor[0], anchor[anchor.Count - 1])) > .1f) continue;
            if (PathDrawContext.HasDetailRange && (start ? PathDrawContext.DetailFrom > .0001 : PathDrawContext.DetailTo < .9999)) continue;
            Vector3 forward = point - points[start ? 1 : points.Count - 2];
            forward.y = 0f;
            if (forward.sqrMagnitude < .0001f) continue;
            Build(root, point + Vector3.up * centerY, forward.normalized, renderedWidth, source.bounds.extents.y, metal, concrete);
        }
    }

    static void Build(GameObject root, Vector3 position, Vector3 forward, float pipeWidth, float pipeHalfHeight, Material metal, Material concrete)
    {
        var mesh = new PathStructureMesh(root, "WaterPipeTerminal", new[] { metal, concrete });
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Quaternion rotation = Quaternion.LookRotation(forward);
        const float depth = 3.6f;
        Vector3 center = position + forward * (depth * .5f - .4f);
        float ground = Utils.GetGroundHeight(center);
        float bottom = Mathf.Min(ground - .4f, position.y - pipeHalfHeight - .2f);
        float top = Mathf.Max(ground + 2.4f, position.y + pipeHalfHeight + .8f);
        float width = Mathf.Max(4.8f, pipeWidth + 1.8f);

        // 管を壁面より40cm内側まで飲み込み、切断面や地形との隙間を箱の内部へ隠す。
        center.y = (bottom + top) * .5f;
        mesh.Box(center, new Vector3(width, top - bottom, depth), rotation, 1);
        Vector3 roof = new Vector3(center.x, top + .24f, center.z);
        mesh.Box(roof - Vector3.up * .12f, new Vector3(width + .3f, .24f, depth + .3f), rotation, 1);

        // 通常の俯瞰距離でも読める太い配管と大きな操作輪だけを載せる。
        Vector3 left = roof - right * 1.15f - forward * .45f;
        Vector3 rightEnd = roof + right * 1.15f - forward * .45f;
        RoofPipe(mesh, left, rightEnd, right);
        Vector3 valve = roof - forward * .45f + Vector3.up * 1.1f;
        mesh.Bar(valve, valve + Vector3.up * .42f, .3f, sides: 8);
        Vector3 wheel = valve + Vector3.up * .8f;
        mesh.Bar(valve, wheel, .08f, sides: 6);
        Ring(mesh, wheel, right, forward, .6f, .065f, 12);
        mesh.Bar(wheel - right * .6f, wheel + right * .6f, .05f, sides: 6);
        mesh.Bar(wheel - forward * .6f, wheel + forward * .6f, .05f, sides: 6);
        mesh.Box(roof + forward * .7f + right * .95f + Vector3.up * .5f, new Vector3(1f, 1f, .8f), rotation);
        mesh.Finish();
    }

    static void RoofPipe(PathStructureMesh mesh, Vector3 left, Vector3 right, Vector3 across)
    {
        const float radius = .25f, bend = .35f, stem = .75f;
        Vector3 previous = left + Vector3.up * stem;
        mesh.Bar(left - Vector3.up * .1f, previous, radius, sides: 8);
        for (int i = 1; i <= 4; i++)
        {
            float angle = i * Mathf.PI / 8f;
            Vector3 next = left + Vector3.up * (stem + bend * Mathf.Sin(angle)) + across * (bend * (1f - Mathf.Cos(angle)));
            mesh.Bar(previous, next, radius, sides: 8);
            previous = next;
        }
        Vector3 end = right + Vector3.up * (stem + bend) - across * bend;
        mesh.Bar(previous, end, radius, sides: 8);
        previous = end;
        for (int i = 1; i <= 4; i++)
        {
            float angle = i * Mathf.PI / 8f;
            Vector3 next = right + Vector3.up * (stem + bend * Mathf.Cos(angle)) + across * (bend * (Mathf.Sin(angle) - 1f));
            mesh.Bar(previous, next, radius, sides: 8);
            previous = next;
        }
        mesh.Bar(previous, right - Vector3.up * .1f, radius, sides: 8);
    }

    static void Ring(PathStructureMesh mesh, Vector3 center, Vector3 x, Vector3 y, float radius, float thickness, int steps)
    {
        Vector3 previous = center + x * radius;
        for (int i = 1; i <= steps; i++)
        {
            float angle = i * Mathf.PI * 2f / steps;
            Vector3 next = center + (x * Mathf.Cos(angle) + y * Mathf.Sin(angle)) * radius;
            mesh.Bar(previous, next, thickness, sides: 6);
            previous = next;
        }
    }
}
