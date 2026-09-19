using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>路線専有ノードのバス停を、色帯・車両調達の状態から独立して描く。</summary>
public sealed class BusStopManager : MonoBehaviour
{
    private const float Radius = 600f;
    private PathManager paths;
    private PropRenderModel model;
    private float nextScan;
    private int session = -1;
    private readonly List<Matrix4x4[]> batches = new();
    private readonly List<Bounds> bounds = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<BusStopManager>() == null)
            new GameObject("BusStopManager").AddComponent<BusStopManager>();
    }

    private void Update()
    {
        var store = TransitDataStore.Ensure();
        if (session != store.Session)
        {
            session = store.Session;
            batches.Clear(); bounds.Clear(); nextScan = 0;
        }
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted())
        { batches.Clear(); bounds.Clear(); nextScan = 0; return; }
        var camera = Camera.main;
        if (camera == null || CameraCellChecker.Instance == null) return;
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null) return;
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + .5f;
            if (model == null || model.normal.Count == 0)
            {
                model?.Dispose(); model = new PropRenderModel("bus_stop");
                foreach (var part in model.normal)
                    if (part.material.name.StartsWith("PROP_SHELTER_GLASS")) ConfigureGlass(part.material);
            }
            Rebuild(CameraCellChecker.Instance.CurrentWorldPosition);
        }
        for (int i = 0; i < batches.Count; i++)
        {
            var part = model.normal[i % model.normal.Count];
            var parameters = new RenderParams(part.material) { camera = camera, worldBounds = bounds[i],
                shadowCastingMode = part.material.renderQueue >= 3000 ? ShadowCastingMode.Off : ShadowCastingMode.On,
                receiveShadows = true };
            Graphics.RenderMeshInstanced(parameters, part.mesh, part.submesh, batches[i]);
        }
    }

    private void Rebuild(Vector3 center)
    {
        batches.Clear(); bounds.Clear();
        if (model.normal.Count == 0) return;
        var placements = new List<Matrix4x4>();
        var seen = new HashSet<int>();
        var masters = MasterDataStore.Instance?.GetPathMasterDictionary();
        if (masters == null) return;
        foreach (var node in paths.QueryNearbyNodes(center, Radius))
        {
            if (!seen.Add(node.node_id) || !node.station_path_id.HasValue) continue;
            bool bus = false;
            foreach (var link in paths.GetPathsConnectedToNode(node.node_id))
                if (link.path_type == Constants.PATH_TRANSPORT &&
                    VehicleModelConfig.GetTransitVehicleType(link.path_variant) == Constants.VEHICLE_BUS)
                { bus = true; break; }
            if (!bus) continue;
            var road = paths.GetPathData(node.station_path_id.Value);
            if (road == null || road.path_type != Constants.PATH_STREET ||
                paths.GetPathObject(road.path_id) == null ||
                !masters.TryGetValue(road.path_variant, out var master) ||
                !paths.TryGetPathPolyline(road.path_id, out var points, false)) continue;
            if (!TryPlace(node.GetAbsolutePosition(), points, master.width, master.sidewalk_width ?? 0,
                out var position, out var rotation)) continue;
            if ((position - center).sqrMagnitude > Radius * Radius) continue;
            // 上屋は水平に保ち、四隅の高い地表に基準面を合わせる。脚の地中延長が低い側を覆う。
            float height = position.y;
            for (int x = -1; x <= 1; x += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = position + rotation * new Vector3(x * 2.7f, 0, z * 1.1f);
                    height = Mathf.Max(height, Utils.GetGroundHeight(corner));
                }
            position.y = height;
            placements.Add(Matrix4x4.TRS(position, rotation, Vector3.one));
        }
        for (int start = 0; start < placements.Count; start += 511)
            foreach (var part in model.normal)
            {
                var matrices = new Matrix4x4[Mathf.Min(511, placements.Count - start)];
                var batchBounds = new Bounds();
                for (int i = 0; i < matrices.Length; i++)
                {
                    matrices[i] = placements[start + i] * part.local;
                    var b = PropRenderModel.TransformBounds(part.mesh.bounds, matrices[i]);
                    if (i == 0) batchBounds = b; else batchBounds.Encapsulate(b);
                }
                batches.Add(matrices); bounds.Add(batchBounds);
            }
    }

    /// <summary>実中心線に射影し、打点側の歩道へ寄せる。狭い歩道では屋根が車道へ張り出さない路外側に置く。</summary>
    public static bool TryPlace(Vector3 stop, List<Vector3> points, float width, float sidewalk,
        out Vector3 position, out Quaternion rotation)
    {
        position = default; rotation = Quaternion.identity;
        float best = float.PositiveInfinity;
        Vector3 tangent = default;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 delta = points[i] - points[i - 1];
            Vector3 flat = new Vector3(delta.x, 0, delta.z);
            if (flat.sqrMagnitude < .000001f) continue;
            float t = Mathf.Clamp01(Vector3.Dot(stop - points[i - 1], flat) / flat.sqrMagnitude);
            Vector3 candidate = Vector3.Lerp(points[i - 1], points[i], t);
            float distance = new Vector2(stop.x - candidate.x, stop.z - candidate.z).sqrMagnitude;
            if (distance >= best) continue;
            best = distance; position = candidate; tangent = flat.normalized;
        }
        if (float.IsPositiveInfinity(best)) return false;
        Vector3 side = Vector3.Cross(Vector3.up, tangent);
        if (Vector3.Dot(stop - position, side) < 0) side = -side;
        float offset = sidewalk >= 2.4f ? width * .5f - 1.2f : width * .5f + 1.2f;
        position += side * offset;
        position.y += RoadPartHeights.BODY_ABOVE_SPLINE + (sidewalk >= 2.4f ? .2f : 0);
        rotation = Quaternion.LookRotation(-side, Vector3.up);
        return true;
    }

    private void OnDestroy() => model?.Dispose();

    /// <summary>FBXの透明度だけではURPの描画状態が揃わないため、所有する複製材質に指定する。</summary>
    public static void ConfigureGlass(Material material)
    {
        material.shader = Shader.Find("Universal Render Pipeline/Lit");
        material.SetColor("_BaseColor", new Color(.38f, .68f, .75f, .23f));
        material.SetFloat("_Surface", 1);
        material.SetFloat("_Blend", 0);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0);
        material.SetFloat("_Smoothness", .84f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = (int)RenderQueue.Transparent;
        material.enableInstancing = true;
    }
}
