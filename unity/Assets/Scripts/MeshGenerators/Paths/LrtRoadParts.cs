using UnityEngine;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>道路上の併用軌道。車線中心を共有し、軌間と架線柱を実寸のまま配置する。</summary>
public static class LrtRoadParts
{
    public static bool IsEmbedded(int variant) => variant == 222 || variant == 242 || variant == 303;
    public static bool IsLrt(int variant) => IsEmbedded(variant) || variant == 431;
    public const float RailTop = .025f;

    public static float Offset(PathSummary path)
    {
        if (path.path_variant == 303) return 0;
        RoadPath.GetPathMasterParams(path, out float width, out float sidewalk, out float side, out _,
            out int lanes, out _, out float center, out _, out _, out _, out _);
        // 併用軌道は一方通行への変更でも位置を変えず、双方向時の内側車線を使う。
        var layout = new RoadLaneLayout(width, sidewalk, side, center, lanes, Constants.DIR_BOTH);
        return Mathf.Abs(layout.Center(layout.Count - 1, Constants.TRAFFIC_HAND_LEFT));
    }

    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path)
    {
        if (!IsEmbedded(path.path_variant)) return;
        string prefix = $"Models/Paths/p{path.path_variant}";
        float offset = Offset(path);
        float spacing = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.wire_spacing ?? 0;
        int count = path.path_variant == 303 ? 1 : 2;
        for (int i = 0; i < count; i++)
        {
            float x = count == 1 ? 0 : (i == 0 ? -offset : offset);
            RoadPath.CreateSplineMesh(parent, spline, prefix + "/lrt_track", 3, x,
                RoadPartHeights.BODY_ABOVE_SPLINE, stretchToWidth: false);
            if (spacing > 0)
                RoadPath.CreateSplineMesh(parent, spline, prefix + "/lrt_wire", 1, x,
                    RoadPartHeights.BODY_ABOVE_SPLINE, stretchToWidth: false);
        }
        if (spacing <= 0) return;
        var pole = ModelManager.Instance?.GetModel(prefix + "/lrt_pole", true);
        if (pole == null) return;
        float length = spline.CalculateLength();
        float margin = Mathf.Min(spacing * .5f, 5f);
        for (float distance = margin; distance <= length - margin; distance += spacing)
        {
            double percent = spline.Travel(0, distance, Spline.Direction.Forward);
            if (PathDrawContext.HasDetailRange && (percent < PathDrawContext.DetailFrom || percent > PathDrawContext.DetailTo)) continue;
            var sample = spline.Evaluate(percent);
            Vector3 forward = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) continue;
            var placed = Object.Instantiate(pole, parent.transform);
            placed.name = "LrtPole";
            placed.transform.position = sample.position + Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE;
            placed.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            foreach (var renderer in placed.GetComponentsInChildren<MeshRenderer>())
            {
                var materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++) materials[m] = RoadPath.GetOrCreateRoadMaterial(materials[m]);
                renderer.sharedMaterials = materials;
            }
        }
    }
}
