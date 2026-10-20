using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Generate near decorations from per-road equipment selection. Counts are rendering-only, unused by economic calculations.
/// </summary>
public static class RoadEquipmentGenerator
{
    // Separate shape-sampling density from equipment spacing, preventing clustered posts after road subdivision.
    private const float EndClearance = 3f;
    private const float LampSpacing = 24f;
    private const float PoleSpacing = 32f;
    private const float BoxSpacing = 64f;
    private static readonly Dictionary<string, Material> Materials = new();

    // Street-equipment materials resolve by name and may come from asset packs. Reload on city switches.
    static RoadEquipmentGenerator() => AssetPackStore.CityChanging += Materials.Clear;

    /// <summary>Nearby path passage spaces that may pass overhead, set by PathManager.</summary>
    public static System.Func<PathSummary, List<PillarClearance.Corridor>> OverheadProvider;

    /// <param name="keepClear">
    /// Spline-distance intervals excluding point equipment (poles/lights/trees/benches, etc.), x=start/y=end. Pass 303 LRT
    /// platform intervals. Fences at pavement edges do not overlap platforms and are excluded from this rule.
    /// </param>
    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path,
        float width, float sidewalkWidth, float? treeSpacing, IReadOnlyList<Vector2> keepClear = null, RoadAccessPlan access = null)
    {
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        if (master == null || (path.streetlight_type == 0 && path.fence_type == 0 && path.street_tree_type == 0 && path.utility_mode == 0
            && path.bench_type == 0 && path.bin_type == 0 && path.planter_type == 0 && path.advert_type == 0)) return;
        var prefabs = new Dictionary<string, GameObject>();
        GameObject Prefab(string part)
        {
            if (!prefabs.TryGetValue(part, out var prefab)) prefabs[part] = prefab = Resolve(path, part);
            return prefab;
        }
        float length = spline.CalculateLength();
        if (length <= EndClearance * 2f) return;
        // Omit outdoor lighting/planting/wiring in tunnels and beneath other roads/tracks. Use saved equipment choices elsewhere.
        var outdoorClear = keepClear != null ? new List<Vector2>(keepClear) : new List<Vector2>();
        foreach (var tunnel in PathTunnelParts.Ranges(spline, path))
            outdoorClear.Add(new Vector2((float)tunnel.From * length, (float)tunnel.To * length));
        var above = OverheadProvider?.Invoke(path);
        // Grade-separated overhead paths exceed own occupancy height; use half that as threshold to distinguish neighboring roads at terrain-induced height differences.
        float rise = master.height.GetValueOrDefault() * .5f;
        var radii = new Dictionary<string, float>();
        bool Covered(string part, Pose pose)
        {
            if (above == null || above.Count == 0) return false;
            if (!radii.TryGetValue(part, out float radius)) radii[part] = radius = HorizontalRadius(Prefab(part));
            return PillarClearance.PassesAbove(pose.position, radius, rise, above);
        }
        var occupied = new Dictionary<Vector2Int, List<Vector3>>();
        float edge = width * 0.5f;
        float offset = sidewalkWidth > 0f ? edge - sidewalkWidth * 0.5f : Mathf.Max(0f, edge - 0.3f);
        bool centralMall = path.path_variant == 304;
        bool twinMall = path.path_variant == 305;
        bool lrtMall = path.path_variant == 303;
        if (centralMall) offset = 0f;
        else if (twinMall) offset = width * .2f;
        else if (lrtMall) offset = edge - .9f;
        float surface = SurfaceHeight(path, sidewalkWidth > 0f ? "sidewalk" : "body");
        if (lrtMall && master.wire_spacing.GetValueOrDefault() > 0f)
        {
            // Double-track poles stand on both sides. Equipment rows (0.9m inside road edge) lie outside vehicle envelopes; avoid only pole bases.
            foreach (float d in LrtRoadParts.PoleDistances(length, master.wire_spacing.Value))
                for (int side = -1; side <= 1; side += 2)
                    Reserve(occupied, Sample(spline, d, side * LrtRoadParts.MALL_POLE_OFFSET, surface).position, 2f);
        }
        var metal = new Batch(parent, "EquipmentMetal", MaterialFor("PATH_EQUIPMENT_METAL", new Color(0.32f, 0.35f, 0.37f)));
        var concrete = new Batch(parent, "EquipmentConcrete", MaterialFor("PATH_EQUIPMENT_CONCRETE", new Color(0.62f, 0.61f, 0.57f)));
        var lamp = new Batch(parent, "EquipmentLamp", MaterialFor("PATH_EQUIPMENT_LAMP", new Color(1f, 0.91f, 0.67f)));
        var leaves = new Batch(parent, "EquipmentLeaves", MaterialFor("PATH_EQUIPMENT_LEAVES", new Color(0.24f, 0.43f, 0.19f)));

        // Fix poles first, then connect only their attachment points with wires. Boxes have independent spacing.
        // Unpaved roads may have overhead wiring, but omit aboveground boxes for underground wiring.
        if (master.utility_mode_allowed && path.utility_mode != Constants.PATH_UTILITY_NONE
            && !(path.path_variant == 201 && path.utility_mode == Constants.PATH_UTILITY_UNDERGROUND))
        {
            bool overhead = path.utility_mode == Constants.PATH_UTILITY_OVERHEAD;
            string utilityPart = overhead ? "utility_pole" : "utility_box";
            Vector3? previous = null;
            foreach (float distance in Distances(length, overhead ? PoleSpacing : BoxSpacing, 0f, outdoorClear))
            {
                if (access?.Blocks(1, distance, 1f) == true) continue;
                Pose pose = Sample(spline, distance, offset, surface);
                // Do not connect across omitted poles, preventing wires from piercing overhead paths.
                if (Covered(utilityPart, pose)) { previous = null; continue; }
                if (!Reserve(occupied, pose.position, 2f)) continue;
                if (!PlacePrefab(parent, Prefab(utilityPart), pose))
                {
                    if (overhead)
                    {
                        concrete.Box(pose, new Vector3(0f, 4f, 0f), new Vector3(0.22f, 8f, 0.22f));
                        metal.Box(pose, new Vector3(0f, 7.6f, 0f), new Vector3(1.6f, 0.12f, 0.12f));
                    }
                    else concrete.Box(pose, new Vector3(0f, 0.65f, 0f), new Vector3(0.65f, 1.3f, 0.9f));
                }
                if (overhead)
                {
                    Vector3 top = pose.position + Vector3.up * 7.6f;
                    if (previous.HasValue) Wire(metal, previous.Value, top);
                    previous = top;
                }
            }
        }
        if (master.streetlight_type_allowed && path.streetlight_type == Constants.PATH_EQUIPMENT_STANDARD)
        {
            // Narrow residential roads use lighting only on the right along start→end.
            int firstSide = centralMall || path.path_variant == 211 || path.path_variant == 212 ? 1 : -1;
            foreach (float distance in Distances(length, LampSpacing, LampSpacing * 0.25f, outdoorClear))
                for (int side = firstSide; side <= 1; side += 2)
                {
                    if (access?.Blocks(side, distance, 1f) == true) continue;
                    Pose pose = Sample(spline, distance, offset * side, surface);
                    if (Covered("streetlight", pose) || !Reserve(occupied, pose.position, 2f)) continue;
                    if (PlacePrefab(parent, Prefab("streetlight"), pose, faceRoad: true)) continue;
                    metal.Box(pose, new Vector3(0f, 3f, 0f), new Vector3(0.12f, 6f, 0.12f));
                    metal.Box(pose, new Vector3(-0.65f, 6f, 0f), new Vector3(1.4f, 0.12f, 0.12f));
                    lamp.Box(pose, new Vector3(-1.15f, 5.9f, 0f), new Vector3(0.65f, 0.12f, 0.35f));
                }
        }
        if (master.street_tree_type_allowed && path.street_tree_type == Constants.PATH_EQUIPMENT_STANDARD)
        {
            float spacing = treeSpacing.GetValueOrDefault() > 0f ? treeSpacing.Value : 20f;
            var centerTree = Prefab("center_tree");
            float centerWidth = master.center_width.GetValueOrDefault();
            if (centerTree != null && centerWidth > 0f && length > centerWidth + EndClearance * 2f)
            {
                float centerSurface = SurfaceHeight(path, "center");
                foreach (float distance in Distances(length, spacing, spacing * .5f, outdoorClear))
                {
                    if (distance < centerWidth * .5f + EndClearance || distance > length - centerWidth * .5f - EndClearance) continue;
                    Pose pose = Sample(spline, distance, 0f, centerSurface);
                    if (!Covered("center_tree", pose) && Reserve(occupied, pose.position, 3f)) PlacePrefab(parent, centerTree, pose);
                }
            }
            foreach (float distance in Distances(length, spacing, spacing * 0.5f, outdoorClear))
                for (int side = centralMall ? 1 : -1; side <= 1; side += 2)
                {
                    if (access?.Blocks(side, distance, 1.5f) == true) continue;
                    Pose pose = Sample(spline, distance, offset * side, surface);
                    if (Covered("tree", pose) || !Reserve(occupied, pose.position, 3f)) continue;
                    if (PlacePrefab(parent, Prefab("tree"), pose)) continue;
                    concrete.Box(pose, new Vector3(0f, 1f, 0f), new Vector3(0.2f, 2f, 0.2f));
                    leaves.Box(pose, new Vector3(0f, 3f, 0f), new Vector3(1.8f, 2.5f, 1.8f));
                }
        }
        Furniture("bench", master.bench_type_allowed, path.bench_type, 24f, 0f, 2f);
        Furniture("bin", master.bin_type_allowed, path.bin_type, 24f, 3f, 1.2f);
        Furniture("planter", master.planter_type_allowed, path.planter_type, 24f, 17f, 2.5f);
        Furniture("advert", master.advert_type_allowed, path.advert_type, 48f, 21f, 2f);

        void Furniture(string part, bool allowed, int selected, float spacing, float phase, float clearance)
        {
            if (!allowed || selected != Constants.PATH_EQUIPMENT_STANDARD) return;
            var prefab = Prefab(part);
            if (prefab == null) return;
            bool pair = !centralMall || part == "bench";
            float lateral = centralMall ? (pair ? .95f : 0f) : offset;
            foreach (float distance in Distances(length, spacing, phase, keepClear))
                for (int side = pair ? -1 : 1; side <= 1; side += 2)
                {
                    if (access?.Blocks(side, distance, clearance) == true) continue;
                    Pose pose = Sample(spline, distance, lateral * side, surface);
                    // Center-island benches face outward. Two-row layouts face the central aisle, keeping shop fronts clear.
                    if (centralMall && part == "bench") pose.rotation *= Quaternion.Euler(0f, 180f, 0f);
                    if (Reserve(occupied, pose.position, part == "bench" && centralMall ? 1.8f : clearance))
                        PlacePrefab(parent, prefab, pose);
                }
        }
        if (master.fence_type_allowed && path.fence_type == Constants.PATH_EQUIPMENT_STANDARD)
        {
            // Pedestrian paths without separate sidewalks place fences inside pavement edges.
            float fenceOffset = sidewalkWidth > 0f ? edge - sidewalkWidth + 0.12f : Mathf.Max(0f, edge - 0.12f);
            // Posts (0.07m square) rest on sidewalk tops at their center's lateral position. After width scaling, a curb/walking-surface step
            // at 0.085–0.155m from the road boundary would leave half a post floating; author sidewalk steps outside that range.
            float fenceSurface = sidewalkWidth > 0f
                ? SurfaceHeight(path, "sidewalk", 0.5f - 0.12f / sidewalkWidth)
                : SurfaceHeight(path, "body");
            for (int side = -1; side <= 1; side += 2)
            {
                Pose previous = Sample(spline, EndClearance, side * fenceOffset, fenceSurface);
                if (access?.Blocks(side, EndClearance, .05f) != true)
                    metal.Box(previous, new Vector3(0f, 0.5f, 0f), new Vector3(0.07f, 1f, 0.07f));
                // Close fence ends independently of sidewalks. No automatic connection across intersections, preserving access.
                for (float d = EndClearance; d < length - EndClearance;)
                {
                    float previousDistance = d;
                    d = Mathf.Min(d + 2f, length - EndClearance);
                    Pose next = Sample(spline, d, side * fenceOffset, fenceSurface);
                    if (access?.Blocks(side, d, .05f) != true)
                        metal.Box(next, new Vector3(0f, 0.5f, 0f), new Vector3(0.07f, 1f, 0.07f));
                    if (access?.Blocks(side, (d + previousDistance) * .5f, (d - previousDistance) * .5f + .05f) == true)
                    { previous = next; continue; }
                    if (!PlaceFencePanel(parent, Prefab("fence"), previous.position, next.position))
                    {
                        metal.Bar(previous.position + Vector3.up * 0.5f, next.position + Vector3.up * 0.5f, 0.05f);
                        metal.Bar(previous.position + Vector3.up * 0.95f, next.position + Vector3.up * 0.95f, 0.05f);
                    }
                    previous = next;
                }
            }
        }
        metal.Finish(); concrete.Finish(); lamp.Finish(); leaves.Finish();
    }

    private static IEnumerable<float> Distances(float length, float spacing, float phase, IReadOnlyList<Vector2> keepClear)
    {
        for (float d = EndClearance + phase; d <= length - EndClearance; d += spacing)
            if (!LrtPlatformParts.Contains(keepClear, d)) yield return d;
    }

    private static Pose Sample(SplineComputer spline, float distance, float offset, float surface)
    {
        var sample = spline.Evaluate(RoadPath.ClampSplinePercent(spline.Travel(0, distance, Spline.Direction.Forward)));
        Vector3 forward = Vector3.ProjectOnPlane(sample.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3 normal = Vector3.Cross(sample.forward, right).normalized;
        Vector3 position = sample.position + right * offset
            + Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE
            + normal * (surface - RoadPartHeights.BODY_ABOVE_SPLINE);
        // Inward is local -X. Align imported lamp orientation during placement.
        return new Pose(position, Quaternion.LookRotation(offset < 0f ? -forward : forward, Vector3.up));
    }

    /// <summary>
    /// Road-part (body/sidewalk/center) top height above spline. widthFraction is lateral position relative to part width
    /// (-0.5–0.5, positive toward sidewalk curb). Intersection signal posts also use this height.
    /// </summary>
    internal static float SurfaceHeight(PathSummary path, string part, float widthFraction = 0f)
    {
        // Generation aligns body tops to Y=0. Sidewalks also subtract body height from their selected model family.
        if (part == "body") return RoadPartHeights.BODY_ABOVE_SPLINE;
        string resourcePath = $"Models/Paths/p{path.path_variant}/{part}";
        var prefab = ModelManager.Instance?.GetModel(resourcePath);
        if (prefab == null)
        {
            resourcePath = $"Models/Paths/p{path.path_type}/{part}";
            prefab = ModelManager.Instance?.GetModel(resourcePath);
        }
        var mesh = prefab != null ? prefab.GetComponent<MeshFilter>()?.sharedMesh : null;
        if (mesh == null) return RoadPartHeights.BODY_ABOVE_SPLINE;
        mesh = RoadPath.GetRoadPartMesh(mesh, resourcePath);
        // Curb/walking-surface heights differ. Use top height at placement lateral position, not full bounds maximum.
        Vector3 point = mesh.bounds.center;
        point.x += widthFraction * mesh.bounds.size.x;
        float top = float.NegativeInfinity;
        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
            float det = (b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
            if (Mathf.Abs(det) < 1e-8f) continue;
            float u = ((b.z-c.z)*(point.x-c.x)+(c.x-b.x)*(point.z-c.z))/det;
            float v = ((c.z-a.z)*(point.x-c.x)+(a.x-c.x)*(point.z-c.z))/det;
            if (u >= -0.0001f && v >= -0.0001f && u+v <= 1.0001f)
                top = Mathf.Max(top, u*a.y+v*b.y+(1f-u-v)*c.y);
        }
        return RoadPartHeights.SIDEWALK_ABOVE_SPLINE + (float.IsNegativeInfinity(top) ? 0f : top);
    }

    /// <summary>Whether outdoor point equipment (poles/boxes/streetlights/trees) is selected. Rebuild when overhead paths change.</summary>
    public static bool HasOutdoorEquipment(PathSummary path) =>
        path.utility_mode != Constants.PATH_UTILITY_NONE || path.streetlight_type != Constants.PATH_EQUIPMENT_NONE
        || path.street_tree_type != Constants.PATH_EQUIPMENT_NONE;

    /// <summary>Maximum horizontal distance from origin (foot) to outline, independent of placement orientation.</summary>
    private static float HorizontalRadius(GameObject prefab)
    {
        var bounds = PillarClearance.GetPrefabBounds(prefab);
        if (!bounds.HasValue) return 0f;
        Vector3 min = bounds.Value.min, max = bounds.Value.max, scale = prefab.transform.localScale;
        return new Vector2(Mathf.Max(-min.x, max.x) * Mathf.Abs(scale.x), Mathf.Max(-min.z, max.z) * Mathf.Abs(scale.z)).magnitude;
    }

    private static GameObject Resolve(PathSummary path, string part) =>
        ModelManager.Instance?.GetModel($"Models/Paths/p{path.path_variant}/{part}")
        ?? ModelManager.Instance?.GetModel($"Models/Paths/p{path.path_type}/{part}");

    private static bool PlacePrefab(GameObject parent, GameObject prefab, Pose pose, bool faceRoad = false)
    {
        if (prefab == null) return false;
        // FBX axis conversion can flip X; use the imported lamp's projection side as reference.
        var mesh = prefab.GetComponent<MeshFilter>()?.sharedMesh;
        if (faceRoad && mesh != null && mesh.bounds.center.x > 0f)
            pose.rotation *= Quaternion.Euler(0f, 180f, 0f);
        // Legacy common models lacking ground-contact origins also align mesh bottoms to the floor.
        if (mesh != null) pose.position -= Vector3.up * (mesh.bounds.min.y * prefab.transform.localScale.y);
        var go = Object.Instantiate(prefab, pose.position, pose.rotation, parent.transform);
        PreparePrefab(go);
        return true;
    }

    private static void PreparePrefab(GameObject go)
    {
        foreach (var collider in go.GetComponentsInChildren<Collider>()) Object.Destroy(collider);
        foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>())
        {
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = RoadPath.GetOrCreateRoadMaterial(mats[i]);
            renderer.sharedMaterials = mats;
        }
    }

    private static bool PlaceFencePanel(GameObject parent, GameObject prefab, Vector3 a, Vector3 b)
    {
        var mesh = prefab != null ? prefab.GetComponent<MeshFilter>()?.sharedMesh : null;
        if (mesh == null || mesh.bounds.size.z < 0.001f || (b-a).sqrMagnitude < 0.00001f) return false;
        // Preserve panel width/height; fit only length between posts. Posts remain independently upright.
        var go = Object.Instantiate(prefab, (a+b)*0.5f, Quaternion.LookRotation(b-a), parent.transform);
        float scale = Vector3.Distance(a,b) / mesh.bounds.size.z;
        go.transform.localScale = new Vector3(1f, 1f, scale);
        go.transform.position -= go.transform.forward * (mesh.bounds.center.z * scale);
        PreparePrefab(go);
        return true;
    }

    private static bool Reserve(Dictionary<Vector2Int, List<Vector3>> occupied, Vector3 p, float clearance)
    {
        const float cellSize = 3f;
        var cell = new Vector2Int(Mathf.FloorToInt(p.x/cellSize), Mathf.FloorToInt(p.z/cellSize));
        for (int x=-1;x<=1;x++) for (int z=-1;z<=1;z++)
            if (occupied.TryGetValue(cell + new Vector2Int(x,z), out var neighbors))
                foreach (var other in neighbors)
                    if ((other-p).sqrMagnitude < clearance*clearance) return false;
        if (!occupied.TryGetValue(cell, out var entries)) occupied[cell] = entries = new List<Vector3>();
        entries.Add(p);
        return true;
    }

    private static void Wire(Batch batch, Vector3 a, Vector3 b)
    {
        Vector3 prev = a;
        for (int i = 1; i <= 12; i++)
        {
            float t = i / 12f;
            Vector3 next = Vector3.Lerp(a, b, t) - Vector3.up * (0.4f * 4f * t * (1f - t));
            batch.Bar(prev, next, 0.025f);
            prev = next;
        }
    }

    private static Material MaterialFor(string name, Color color)
    {
        var existing = RoadPath.GetOrCreateRoadMaterialByName(name);
        if (existing != null) return existing;
        if (Materials.TryGetValue(name, out var cached)) return cached;
        var source = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = name, color = color };
        cached = RoadPath.GetOrCreateRoadMaterial(source);
        Object.Destroy(source);
        Materials[name] = cached;
        return cached;
    }

    // Combine simple geometry per road/material; do not create a GameObject/Collider for every post.
    private sealed class Batch
    {
        private readonly GameObject parent;
        private readonly string name;
        private readonly Material material;
        private readonly List<Vector3> vertices = new();
        private readonly List<int> triangles = new();
        public Batch(GameObject parent, string name, Material material) { this.parent = parent; this.name = name; this.material = material; }
        public void Bar(Vector3 a, Vector3 b, float thickness)
        {
            if ((b-a).sqrMagnitude < 0.00001f) return;
            Box(new Pose((a+b)*0.5f, Quaternion.LookRotation(b-a)), Vector3.zero, new Vector3(thickness, thickness, Vector3.Distance(a,b)));
        }
        public void Box(Pose pose, Vector3 center, Vector3 size)
        {
            Vector3[] p = new Vector3[8];
            for (int i = 0; i < 8; i++)
                p[i] = parent.transform.InverseTransformPoint(pose.position + pose.rotation * (center + Vector3.Scale(size, new Vector3((i&1)==0 ? -0.5f:0.5f, (i&2)==0 ? -0.5f:0.5f, (i&4)==0 ? -0.5f:0.5f))));
            int[] faces = {0,2,3,1, 4,5,7,6, 0,4,6,2, 1,3,7,5, 0,1,5,4, 2,6,7,3};
            for (int i = 0; i < faces.Length; i += 4)
            {
                int n = vertices.Count;
                for (int j=0;j<4;j++) vertices.Add(p[faces[i+j]]);
                triangles.AddRange(new[] {n,n+1,n+2,n,n+2,n+3});
            }
        }
        public void Finish()
        {
            if (vertices.Count == 0) return;
            var mesh = new Mesh {name=name, indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles,0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var go = new GameObject(name); go.transform.SetParent(parent.transform,false);
            go.AddComponent<MeshFilter>().sharedMesh=mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial=material;
            go.AddComponent<IntersectionMeshOwner>().OwnedMesh=mesh;
        }
    }
}
