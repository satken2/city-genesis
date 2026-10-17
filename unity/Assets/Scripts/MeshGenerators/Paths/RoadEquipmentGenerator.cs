using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// 道路単位の設備選択から近景の装飾を生成する。設置数は描画専用で経済計算には使わない。
/// </summary>
public static class RoadEquipmentGenerator
{
    // 形状のサンプリング密度と設備の間隔を分け、道路の再分割で支柱が密集するのを防ぐ。
    private const float EndClearance = 3f;
    private const float LampSpacing = 24f;
    private const float PoleSpacing = 32f;
    private const float BoxSpacing = 64f;
    private static readonly Dictionary<string, Material> Materials = new();

    /// <summary>上を通り得る周辺パスの通行空間。PathManager が設定する。</summary>
    public static System.Func<PathSummary, List<PillarClearance.Corridor>> OverheadProvider;

    /// <param name="keepClear">
    /// 点設備(電柱・街灯・樹木・ベンチ等)を置かないスプライン距離の区間(x=始端, y=終端)。303 のLRT停留所の
    /// ホーム区間を渡す。柵は舗装端にありホームと重ならないので対象外。
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
        // 屋外の照明・植栽・配線はトンネル内と、他の道路・線路が上を通る地点には置かない。保存された設備選択は残りの区間で使う。
        var outdoorClear = keepClear != null ? new List<Vector2>(keepClear) : new List<Vector2>();
        foreach (var tunnel in PathTunnelParts.Ranges(spline, path))
            outdoorClear.Add(new Vector2((float)tunnel.From * length, (float)tunnel.To * length));
        var above = OverheadProvider?.Invoke(path);
        // 立体交差で上を通る相手は自分の占有高さ以上高いので、その半分をしきい値にして、地形の傾きで高低差のある隣の道路と分ける。
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
            // 複線の架線柱は両側に立つ。設備の列(道路端から0.9m内側)は車体の通る範囲の外にあり、柱の足元だけを避ける。
            foreach (float d in LrtRoadParts.PoleDistances(length, master.wire_spacing.Value))
                for (int side = -1; side <= 1; side += 2)
                    Reserve(occupied, Sample(spline, d, side * LrtRoadParts.MALL_POLE_OFFSET, surface).position, 2f);
        }
        var metal = new Batch(parent, "EquipmentMetal", MaterialFor("PATH_EQUIPMENT_METAL", new Color(0.32f, 0.35f, 0.37f)));
        var concrete = new Batch(parent, "EquipmentConcrete", MaterialFor("PATH_EQUIPMENT_CONCRETE", new Color(0.62f, 0.61f, 0.57f)));
        var lamp = new Batch(parent, "EquipmentLamp", MaterialFor("PATH_EQUIPMENT_LAMP", new Color(1f, 0.91f, 0.67f)));
        var leaves = new Batch(parent, "EquipmentLeaves", MaterialFor("PATH_EQUIPMENT_LEAVES", new Color(0.24f, 0.43f, 0.19f)));

        // 電柱を先に確定し、その接続点だけを電線で結ぶ。ボックスは電柱と別の間隔で置く。
        // 未舗装路も架空配線を持てるが、地中配線の地上ボックスは置かない。
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
                // 電線が上のパスを貫かないよう、省いた電柱の前後は結ばない。
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
            // 狭い生活道路は始点から終点へ向かう右側だけで照明をまかなう。
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
                    // 中央島のベンチは外向き。2列型は中央通路へ向け、店舗前を空ける。
                    if (centralMall && part == "bench") pose.rotation *= Quaternion.Euler(0f, 180f, 0f);
                    if (Reserve(occupied, pose.position, part == "bench" && centralMall ? 1.8f : clearance))
                        PlacePrefab(parent, prefab, pose);
                }
        }
        if (master.fence_type_allowed && path.fence_type == Constants.PATH_EQUIPMENT_STANDARD)
        {
            // 独立した歩道を持たない歩行者道では、舗装端の内側に柵を置く。
            float fenceOffset = sidewalkWidth > 0f ? edge - sidewalkWidth + 0.12f : Mathf.Max(0f, edge - 0.12f);
            // 支柱(0.07m角)は中心の横位置の歩道上面に接地させる。歩道幅へ伸縮した後の縁石と歩行面の段差が
            // 歩車道境界から0.085〜0.155mにあると支柱の片側が浮くので、歩道モデルの段差はこの範囲を避けて著作する。
            float fenceSurface = sidewalkWidth > 0f
                ? SurfaceHeight(path, "sidewalk", 0.5f - 0.12f / sidewalkWidth)
                : SurfaceHeight(path, "body");
            for (int side = -1; side <= 1; side += 2)
            {
                Pose previous = Sample(spline, EndClearance, side * fenceOffset, fenceSurface);
                if (access?.Blocks(side, EndClearance, .05f) != true)
                    metal.Box(previous, new Vector3(0f, 0.5f, 0f), new Vector3(0.07f, 1f, 0.07f));
                // 柵は歩道と独立して終端を閉じる。交差点内部に自動接続しないため通行口を塞がない。
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
        // 内向きはローカル-X。読み込み後の灯具の向きは配置時に合わせる。
        return new Pose(position, Quaternion.LookRotation(offset < 0f ? -forward : forward, Vector3.up));
    }

    /// <summary>
    /// 道路パーツ(body/sidewalk/center)の上面のスプラインからの高さ。widthFraction はパーツ幅に対する横位置
    /// (-0.5〜0.5。歩道は+側が縁石)。交差点の信号機の柱もこの高さに立てる。
    /// </summary>
    internal static float SurfaceHeight(PathSummary path, string part, float widthFraction = 0f)
    {
        // bodyは生成時に上面をY=0へ揃える。歩道も採用したモデル系列のbody高を除く。
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
        // 縁石と歩行面の高さは異なる。全体boundsの最大値ではなく設置する横位置の上面を使う。
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

    /// <summary>屋外の点設備(電柱・地上ボックス・街灯・街路樹)を選んでいるか。上を通るパスの変化で作り直す対象。</summary>
    public static bool HasOutdoorEquipment(PathSummary path) =>
        path.utility_mode != Constants.PATH_UTILITY_NONE || path.streetlight_type != Constants.PATH_EQUIPMENT_NONE
        || path.street_tree_type != Constants.PATH_EQUIPMENT_NONE;

    /// <summary>原点(足元)から外形までの水平の最大距離。配置の向きによらない。</summary>
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
        // FBXの軸変換でXの符号が変わるため、Unityで読んだ灯具の張り出し側を基準にする。
        var mesh = prefab.GetComponent<MeshFilter>()?.sharedMesh;
        if (faceRoad && mesh != null && mesh.bounds.center.x > 0f)
            pose.rotation *= Quaternion.Euler(0f, 180f, 0f);
        // 接地原点を持たない旧共通モデルも、メッシュ下端を床へ合わせる。
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
        // パネルは横幅・高さを保持し、支柱間の長さだけ合わせる。支柱は独立した直立形状。
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

    // 簡易形状は道路・材質単位で結合し、支柱一本ごとのGameObjectやColliderを作らない。
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
