using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Use the main DLL, the actual FBXs of the metal mine and the quarry, and the URP in an isolated project to verify and render them
// before and after the backend's digging: an open pit on rolling ground and a quarry on a hillside. Units stay in the forecourt, the
// hoppers face the excavation, the loose machines, rocks, and clutter keep clear of roads and units, the machines at work stand on
// the level being cut, and the ground panel follows the terrain. Also reports where the generation time goes.
[InitializeOnLoad]
public static class MineYardQA
{
    const int MODEL_OPEN_PIT = 13, MODEL_QUARRY = 18;
    static readonly (string folder, int model)[] Sites = { ("a1022", MODEL_OPEN_PIT), ("a1089", MODEL_QUARRY) };
    static Assembly assembly;
    static int checks;
    static string output;
    static readonly List<string> stats = new List<string>();

    static MineYardQA()
    {
        if (SessionState.GetBool("MineYardQA", false)) EditorApplication.delayCall += Start;
    }

    public static void Run()
    {
        SessionState.SetBool("MineYardQA", true);
        GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        foreach (var site in Sites)
            foreach (string path in Directory.GetFiles("Assets/Models/Buildings/" + site.folder, "*.fbx", SearchOption.AllDirectories))
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(path.Replace('\\', '/'));
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
            }
        EditorApplication.EnterPlaymode();
    }

    static void Require(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static object Field(object value, string name) => value.GetType().GetField(name)?.GetValue(value) ?? value.GetType().GetProperty(name)?.GetValue(value);

    static void Start()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.delayCall += Start; return; }
        SessionState.SetBool("MineYardQA", false);
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        output = root + "/Temp/MineYardQA";
        if (Directory.Exists(output + "/Captures")) Directory.Delete(output + "/Captures", true);
        Directory.CreateDirectory(output + "/Captures");
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string file = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            Suite();
            File.WriteAllText(output + "/result.txt", "PASS checks=" + checks + "\n" + string.Join("\n", stats));
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(output + "/result.txt", "FAIL " + e + "\n" + string.Join("\n", stats));
            UnityEngine.Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    [Serializable] public class Ring { public Vector2[] points; }
    [Serializable] public class Surface { public string material; public Vector2[] polygon; public List<Ring> holes = new List<Ring>(); public float lift; public string side; public Vector2[] points; public int[] triangles; }
    [Serializable] public class Prop { public string part; public Vector2 position; public Vector2 forward; public float scale; }
    [Serializable] public class Snapshot { public Vector2[] polygon; public List<Surface> surfaces = new List<Surface>(); public List<Prop> props = new List<Prop>(); }

    // Placement sample for the model gallery, in the format the open-space QA exports. The machines at work are placed from terrain and are not in it.
    static string Export(object plan, Type builder)
    {
        var s = new Snapshot { polygon = (Vector2[])Field(plan, "Polygon") };
        foreach (var item in (IEnumerable)Field(plan, "Surfaces"))
        {
            var surface = new Surface { material = (string)Field(item, "Material"), polygon = (Vector2[])Field(item, "Polygon"), lift = (float)Field(item, "Lift"), side = (string)Field(item, "Side") };
            if (Field(item, "Holes") is Vector2[][] holes) foreach (var hole in holes) surface.holes.Add(new Ring { points = hole });
            var args = new object[] { item, null, null };
            builder.GetMethod("Triangulate").Invoke(null, args);
            surface.points = ((List<Vector2>)args[1]).ToArray(); surface.triangles = ((List<int>)args[2]).ToArray();
            s.surfaces.Add(surface);
        }
        foreach (var item in (IEnumerable)Field(plan, "Props"))
            s.props.Add(new Prop { part = (string)Field(item, "Part"), position = (Vector2)Field(item, "Position"), forward = (Vector2)Field(item, "Forward"), scale = (float)Field(item, "Scale") });
        return JsonUtility.ToJson(s);
    }

    // Part sizes (X width, Z depth about the origin), measured as the main assembly does.
    static Vector2 Size(GameObject model)
    {
        bool any = false; var bounds = new Bounds();
        foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            foreach (var v in filter.sharedMesh.vertices)
            { var w = filter.transform.TransformPoint(v); if (any) bounds.Encapsulate(w); else { bounds = new Bounds(w, Vector3.zero); any = true; } }
        return new Vector2(2 * Mathf.Max(-bounds.min.x, bounds.max.x), 2 * Mathf.Max(-bounds.min.z, bounds.max.z));
    }

    const int RES = 1025;
    const float SPAN = 1000f, TOP = 300f;
    // Rolling ground rising gently away from the road, for the open pit.
    static float Rolling(float wx, float wz) => 70 + .05f * wz + 2.5f * Mathf.Sin(wx / 37f) * Mathf.Cos(wz / 49f);

    // Stand-in for the backend's digging: benches 10m high (3.5m face, 6m berm) stepping in from the rim, cut down to a level.
    // openDepth gives each point's depth from the road-facing edge and the depth where the excavation starts: the edge there is not a rim (a quarry's open side).
    static float[,] Heights(Func<float, float, float> natural, Vector2[] pit, float level, float floor, Func<Vector2, float> depth = null, float openDepth = 0)
    {
        var heights = new float[RES, RES];
        float cell = SPAN / (RES - 1);
        Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
        float rimLevel = float.MinValue;
        if (pit != null)
            for (int i = 0; i < pit.Length; i++)
            {
                min = Vector2.Min(min, pit[i]); max = Vector2.Max(max, pit[i]);
                rimLevel = Mathf.Max(rimLevel, natural(pit[i].x, pit[i].y));
            }
        for (int z = 0; z < RES; z++)
            for (int x = 0; x < RES; x++)
            {
                float wx = x * cell - SPAN / 2, wz = z * cell - SPAN / 2, h = natural(wx, wz);
                var p = new Vector2(wx, wz);
                if (pit != null && wx > min.x && wx < max.x && wz > min.y && wz < max.y && Inside(pit, p))
                {
                    float inward = float.MaxValue;
                    for (int i = 0; i < pit.Length; i++)
                    {
                        Vector2 a = pit[i], b = pit[(i + 1) % pit.Length];
                        if (depth != null && depth(a) < openDepth + .5f && depth(b) < openDepth + .5f) continue;
                        inward = Mathf.Min(inward, SegmentDistance(p, a, b));
                    }
                    float steps = Mathf.Floor(inward / 9.5f), within = inward - steps * 9.5f;
                    float ultimate = Mathf.Max(rimLevel - 10f * (steps + Mathf.Min(within / 3.5f, 1f)), floor);
                    h = Mathf.Min(h, Mathf.Max(ultimate, level));
                }
                heights[z, x] = h / TOP;
            }
        return heights;
    }

    static bool Inside(Vector2[] polygon, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            if ((polygon[i].y > p.y) != (polygon[j].y > p.y) && p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x) inside = !inside;
        return inside;
    }

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-9f));
        return Vector2.Distance(p, a + ab * t);
    }

    static Vector2[] Box(float width, float depth) => new[] { new Vector2(-width / 2, -depth / 2), new Vector2(width / 2, -depth / 2), new Vector2(width / 2, depth / 2), new Vector2(-width / 2, depth / 2) };

    static void Suite()
    {
        // Same depth offsets as the game: buildings use Custom/DepthOffsetLit (-1,-1) and the terrain is pushed back (1,1).
        var lit = Shader.Find("Custom/DepthOffsetLit");
        var terrainShader = Shader.Find("Tests/DepthOffsetTerrainQA");
        Require(lit != null && terrainShader != null, "Missing depth-offset shaders");
        var converted = new Dictionary<Material, Material>();
        Material Offset(Material source)
        {
            if (source == null || source.shader == lit) return source;
            if (!converted.TryGetValue(source, out Material material)) converted[source] = material = new Material(source) { shader = lit };
            return material;
        }
        var layout = assembly.GetType("OpenSpaceLayout");
        var builder = assembly.GetType("OpenSpaceBuildingMeshBuilder");
        var ground = assembly.GetType("Utils").GetMethod("GetGroundHeight");
        float Ground(float x, float z) => (float)ground.Invoke(null, new object[] { new Vector3(x, 1000f, z) });
        float Constant(string name) => (float)assembly.GetType("Constants").GetField(name).GetRawConstantValue();

        var data = new TerrainData { heightmapResolution = RES, size = new Vector3(SPAN, TOP, SPAN) };
        data.SetHeights(0, 0, Heights(Rolling, null, 0, 0));
        var terrainObject = Terrain.CreateTerrainGameObject(data);
        terrainObject.transform.position = new Vector3(-SPAN / 2, 0, -SPAN / 2);
        var terrain = terrainObject.GetComponent<Terrain>();
        terrain.drawInstanced = false;
        terrain.heightmapPixelError = (float)assembly.GetType("LocalConstants").GetField("TERRAIN_HEIGHTMAP_PIXEL_ERROR").GetRawConstantValue();
        var snow = new Material(terrainShader);
        snow.SetColor("_BaseColor", new Color(.86f, .87f, .88f));
        snow.SetFloat("_Smoothness", .1f);
        terrain.materialTemplate = snow;
        void Shape(float[,] heights)
        {
            data.SetHeights(0, 0, heights);
            Physics.SyncTransforms();
            assembly.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
        }
        Capture("warmup", Vector3.zero, 10f, 30f, 0f);

        // (name, parcel with the road-facing edge first, depth of the cut below the rim or 0 for untouched ground, seed)
        var pitCases = new (string name, Vector2[] polygon, float cut, float seed)[]
        {
            ("rectangle", Box(120, 120), 0, .2f),
            ("wedge", new[] { new Vector2(-95, -70), new Vector2(85, -82), new Vector2(92, 40), new Vector2(40, 98) }, 0, .2f),
            ("wide", Box(300, 180), 0, .2f),
            ("wide-mirrored", Box(300, 180), 0, .7f),
            ("wide-dug", Box(300, 180), 24, .2f),
            ("large-dug", Box(600, 440), 45, .2f),
        };
        // A quarry's cut is how far below the hill's top the cutting has reached; a large value cuts the whole hill to the floor.
        var quarryCases = new (string name, Vector2[] polygon, float cut, float seed)[]
        {
            ("smallest", Box(45, 45), 0, .2f),
            ("smallest-cut", Box(45, 45), 100, .2f),
            ("default", Box(100, 100), 0, .2f),
            ("default-cut", Box(100, 100), 100, .2f),
            ("wide-mirrored-half-cut", Box(220, 130), 12, .7f),
            ("wedge-cut", new[] { new Vector2(-95, -70), new Vector2(85, -82), new Vector2(92, 40), new Vector2(40, 98) }, 100, .2f),
            ("large-cut", Box(320, 310), 100, .2f),
        };

        foreach (var site in Sites)
        {
            string folder = site.folder;
            bool quarry = site.model == MODEL_QUARRY;
            float yardDepth = Constant(quarry ? "QUARRY_YARD_DEPTH" : "OPEN_PIT_YARD_DEPTH");
            float pitStart = quarry ? yardDepth : yardDepth + Constant("OPEN_PIT_RIM_MARGIN");
            var parts = new Dictionary<string, GameObject>();
            var sizes = new Dictionary<string, Vector2>();
            foreach (string file in Directory.GetFiles($"Assets/Models/Buildings/{folder}/0", "*.fbx"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                parts[name] = AssetDatabase.LoadAssetAtPath<GameObject>(file.Replace('\\', '/'));
                Require(parts[name] != null, "Cannot load " + file);
                if (parts[name].GetComponentsInChildren<MeshFilter>().Length > 0) sizes[name] = Size(parts[name]);
            }
            string[] needed = quarry
                ? new[] { "site_plant", "site_office", "site_pond", "site_stockpile1" }
                : new[] { "site_crusher", "site_conveyor", "site_stockpile1", "site_rom", "site_office", "site_workshop" };
            foreach (string part in needed.Concat(new[] { "floor", "gate", "site_gravel", "site_excavator", "site_truck", "site_loader",
                "site_pickup", "site_drill", "site_rock1", "site_heap1", "site_clutter1" }))
                Require(parts.ContainsKey(part), folder + ": missing part " + part);
            Material floorSource = parts["floor"].GetComponentInChildren<Renderer>().sharedMaterial;
            Require(floorSource != null && floorSource.GetTexture("_BaseMap") != null, folder + ": the ground material has no texture: " + floorSource?.name);
            Material roadSource = parts["site_gravel"].GetComponentInChildren<Renderer>().sharedMaterial;
            Require(roadSource != null && roadSource.GetTexture("_BaseMap") != null, folder + ": the road material has no texture: " + roadSource?.name);
            if (!quarry)
            {
                // The layout assumes the screen tower (the crusher's highest part) is on Unity local +X and the hopper on the back (-Z).
                var crusherMesh = parts["site_crusher"].GetComponentsInChildren<MeshFilter>();
                float top = crusherMesh.Max(f => f.sharedMesh.vertices.Max(v => f.transform.TransformPoint(v).y));
                var high = crusherMesh.SelectMany(f => f.sharedMesh.vertices.Select(v => f.transform.TransformPoint(v))).Where(v => v.y > top - 1f).ToList();
                Require(high.Average(v => v.x) > 4f, folder + ": the crusher's screen tower is not on local +X: " + high.Average(v => v.x));
                Require(sizes["site_conveyor"].x > 15.9f && sizes["site_conveyor"].x < 16.1f, folder + ": conveyor span " + sizes["site_conveyor"].x);
            }

            foreach (var item in quarry ? quarryCases : pitCases)
            {
                Vector2 a = item.polygon[0], b = item.polygon[1], u = (b - a).normalized, v = new Vector2(-u.y, u.x), front = (a + b) * .5f;
                float Depth(Vector2 p) => Vector2.Dot(p - front, v);
                // The quarry stands at the foot of a hill: level forecourt, then a 28% rise with swells.
                Func<float, float, float> natural = !quarry ? Rolling : (wx, wz) =>
                {
                    float rise = Mathf.Max(Depth(new Vector2(wx, wz)) - yardDepth, 0f);
                    return 60 + .28f * rise + (rise > 0 ? 1.5f * Mathf.Sin(wx / 23f) * Mathf.Min(rise / 20f, 1f) : 0f);
                };
                Shape(Heights(natural, null, 0, 0));
                var watch = Stopwatch.StartNew();
                object plan = layout.GetMethod("Create").Invoke(null, new object[] { item.polygon, site.model, item.seed, .5f, sizes });
                double planMs = watch.Elapsed.TotalMilliseconds;
                string name = folder + "-" + item.name;
                if (item.name == (quarry ? "default" : "rectangle")) File.WriteAllText($"{output}/{folder}.json", Export(plan, builder));
                var pit = (Vector2[])Field(plan, "Pit");
                Require(pit != null && pit.Length >= 3, name + ": no excavation area");
                foreach (Vector2 p in pit) Require(Depth(p) >= pitStart - .01f, name + ": the excavation reaches into the forecourt");
                Require((Field(plan, "PitFloor") != null) == quarry, name + ": floor level reference");
                float rimLevel = pit.Max(p => natural(p.x, p.y));
                if (item.cut > 0)
                    Shape(quarry ? Heights(natural, pit, rimLevel - item.cut, natural(front.x, front.y), Depth, pitStart) : Heights(natural, pit, rimLevel - item.cut, 2f));
                var center = new Vector3(0, Ground(0, 0), 0);
                float radius = item.polygon.Max(p => p.magnitude);

                var props = ((IEnumerable)Field(plan, "Props")).Cast<object>().ToList();
                string Part(object prop) => (string)Field(prop, "Part");
                var units = props.Where(p => (bool)Field(p, "Unit")).ToList();
                var loose = props.Where(p => !(bool)Field(p, "Unit")).ToList();
                Require(Field(plan, "Gate") != null, name + ": no gate");
                foreach (object unit in units)
                {
                    Vector2 position = (Vector2)Field(unit, "Position"), forward = (Vector2)Field(unit, "Forward");
                    // Farthest reach of the unit's rectangle from the road-facing edge.
                    Vector2 right = new Vector2(forward.y, -forward.x), half = sizes[Part(unit)] * .5f;
                    float reach = Depth(position) + Mathf.Abs(Vector2.Dot(right, v)) * half.x + Mathf.Abs(Vector2.Dot(forward, v)) * half.y;
                    Require(reach <= yardDepth + .01f, $"{name}: {Part(unit)} reaches {reach}m in, past the forecourt");
                    if (Part(unit) != "gate") Require(parts.ContainsKey(Part(unit) + "_far"), $"{folder}: {Part(unit)} has no distant model");
                }
                foreach (object prop in loose)
                    Require(Inside(item.polygon, (Vector2)Field(prop, "Position")), $"{name}: {Part(prop)} lies off the site");
                // The loose props are a lottery over the free ground, so only a site with room for many draws is sure to show rocks.
                if (radius > 60f) Require(loose.Any(p => Part(p).StartsWith("site_rock")), name + ": no rocks");
                var crushers = units.Where(p => Part(p) == (quarry ? "site_plant" : "site_crusher")).ToList();
                var piles = units.Where(p => Part(p).StartsWith("site_stockpile")).ToList();
                Require(crushers.Count >= 1, name + ": no crushing plant");
                foreach (object crusher in crushers)
                    Require(Vector2.Dot((Vector2)Field(crusher, "Forward"), v) < -.99f, name + ": a hopper does not face the excavation");
                if (!quarry)
                {
                    var conveyors = units.Where(p => Part(p) == "site_conveyor").ToList();
                    Require(piles.Count >= 1 && conveyors.Count == piles.Count, $"{name}: {conveyors.Count} conveyor spans for {piles.Count} piles");
                    foreach (object crusher in crushers)
                    {
                        Vector2 position = (Vector2)Field(crusher, "Position"), forward = (Vector2)Field(crusher, "Forward");
                        Vector2 right = new Vector2(forward.y, -forward.x);
                        // Its first pile stands one conveyor span from the screen tower, 8m to the tower's side of the crusher's center.
                        Vector2 feed = position + right * (8f + sizes["site_conveyor"].x);
                        Require(piles.Any(p => ((Vector2)Field(p, "Position") - feed).magnitude < .1f), name + ": a crusher has no pile on its screen tower's side");
                    }
                    object workshop = units.FirstOrDefault(p => Part(p) == "site_workshop");
                    if (workshop != null) Require(Vector2.Dot((Vector2)Field(workshop, "Forward"), v) > .99f, name + ": the workshop's doors do not face the yard road");
                }

                object Rebuild(Dictionary<string, GameObject> with) => builder.GetMethod("Build").Invoke(null, new object[] { plan, with, center, true, floorSource, null, null });
                object result = Rebuild(parts);
                var mesh = (Mesh)Field(result, "Mesh");
                var materials = (Material[])Field(result, "Materials");
                Require(mesh != null && mesh.vertexCount > 0 && materials.All(m => m != null), name + ": empty mesh or missing material");
                Require(Field(result, "ColliderMesh") != null, name + ": no collider");

                var go = new GameObject("Mine");
                go.transform.position = center;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = materials.Select(Offset).ToArray();
                Capture("warmup", center, radius * .78f, 40f, 20f);
                Capture(name, center, radius * .78f, 40f, 20f);
                Capture(name + "-yard", center + new Vector3(front.x, 0, front.y) + new Vector3(v.x, 0, v.y) * yardDepth * .5f, Mathf.Min(radius * .45f, 70f), 24f, -25f);
                Vector2 line = (Vector2)Field(crushers[0], "Position");
                Capture(name + "-plant", new Vector3(line.x, Ground(line.x, line.y), line.y), 30f, 20f, 150f);
                Capture(name + "-ground", new Vector3(line.x, Ground(line.x, line.y), line.y) + new Vector3(v.x, 0, v.y) * -12f, 9f, 35f, 200f);
                if (item.cut > 0)
                {
                    Vector2 middle = pit.Aggregate(Vector2.zero, (sum, p) => sum + p) / pit.Length;
                    Capture(name + "-pit", new Vector3(middle.x, Ground(middle.x, middle.y), middle.y), Mathf.Min(radius * .5f, 45f), 26f, 40f);
                }

                // Where the generation time goes: the whole build, then without the units and loose props, then without the road surfaces too (ground panel only).
                double Best(Func<object> build)
                {
                    double best = double.MaxValue;
                    for (int i = 0; i < 3; i++)
                    {
                        watch.Restart();
                        object built = build();
                        best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                        UnityEngine.Object.DestroyImmediate((Mesh)Field(built, "Mesh"));
                        UnityEngine.Object.DestroyImmediate((Mesh)Field(built, "ColliderMesh"));
                    }
                    return best;
                }
                double fullMs = Best(() => Rebuild(parts));
                var propList = (IList)Field(plan, "Props");
                var surfaceList = (IList)Field(plan, "Surfaces");
                var keptSurfaces = surfaceList.Cast<object>().ToList();
                var bare = new Dictionary<string, GameObject>(parts);
                bare.Remove("site_excavator"); bare.Remove("site_truck");
                propList.Clear();
                double groundRoadsMs = Best(() => Rebuild(bare));
                surfaceList.Clear();
                double groundMs = Best(() => Rebuild(bare));
                foreach (object prop in props) propList.Add(prop);
                foreach (object surface in keptSurfaces) surfaceList.Add(surface);
                // A shown building defers the ground (the panel and the roads on it) to low-priority work: what is generated at once, and what later.
                var capture = assembly.GetType("BuildingExteriorRenderer").GetField("Capture", BindingFlags.Static | BindingFlags.NonPublic);
                Type listType = typeof(List<>).MakeGenericType(assembly.GetType("BuildingExteriorRenderer+Build"));
                double nowMs = double.MaxValue, laterMs = double.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    var jobs = (IList)Activator.CreateInstance(listType);
                    object shown;
                    capture.SetValue(null, jobs);
                    try
                    {
                        watch.Restart();
                        shown = Rebuild(parts);
                        nowMs = Math.Min(nowMs, watch.Elapsed.TotalMilliseconds);
                    }
                    finally { capture.SetValue(null, null); }
                    Require(jobs.Count == 1, $"{name}: {jobs.Count} deferred jobs");
                    var shownMesh = (Mesh)Field(shown, "Mesh");
                    Require(shownMesh.vertexCount < mesh.vertexCount && Field(shown, "ColliderMesh") != null, name + ": the shown mesh still holds the ground, or has nothing to click on");
                    var laterMeshes = new List<CombineInstance>(); var laterMaterials = new List<Material>(); var temporary = new List<Mesh>();
                    watch.Restart();
                    ((Delegate)jobs[0]).DynamicInvoke(laterMeshes, laterMaterials, temporary);
                    laterMs = Math.Min(laterMs, watch.Elapsed.TotalMilliseconds);
                    Require(laterMeshes.Count >= 2 && laterMeshes.Sum(m => m.mesh.vertexCount) + shownMesh.vertexCount == mesh.vertexCount,
                        name + ": the deferred ground and the shown mesh do not add up to the whole");
                    Require(laterMaterials.All(m => m != null && m.shader == lit), name + ": a deferred ground material lacks the depth offset");
                    foreach (Mesh m in temporary) UnityEngine.Object.DestroyImmediate(m);
                    UnityEngine.Object.DestroyImmediate(shownMesh);
                    UnityEngine.Object.DestroyImmediate((Mesh)Field(shown, "ColliderMesh"));
                }
                stats.Add($"{name}: {crushers.Count} plants, {piles.Count} piles, {loose.Count} loose props, {mesh.vertexCount} vertices, plan {planMs:F1}ms, " +
                    $"build {fullMs:F1}ms (ground {groundMs:F1}ms, roads {groundRoadsMs - groundMs:F1}ms, units and props {fullMs - groundRoadsMs:F1}ms); " +
                    $"shown: {nowMs:F1}ms at once, ground {laterMs:F1}ms deferred");
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate((Mesh)Field(result, "ColliderMesh"));
            }
        }
        UnityEngine.Object.DestroyImmediate(terrainObject);
    }

    static void Capture(string name, Vector3 target, float radius, float elevation, float azimuth)
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional; sun.intensity = 1.4f; sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(48, -30, 0);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.48f, 0.52f);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.fieldOfView = 40f; camera.nearClipPlane = 0.3f; camera.farClipPlane = 4000f;
        camera.backgroundColor = new Color(0.55f, 0.66f, 0.78f); camera.clearFlags = CameraClearFlags.SolidColor;
        float distance = radius / Mathf.Sin(20f * Mathf.Deg2Rad) * 1.05f;
        camera.transform.position = target + Quaternion.Euler(elevation, azimuth, 0) * Vector3.back * distance;
        camera.transform.LookAt(target);
        var rt = new RenderTexture(1600, 1000, 24);
        camera.targetTexture = rt; camera.Render();
        RenderTexture.active = rt;
        var image = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
        image.ReadPixels(new UnityEngine.Rect(0, 0, 1600, 1000), 0, 0); image.Apply();
        File.WriteAllBytes(output + "/Captures/" + name + ".png", image.EncodeToPNG());
        RenderTexture.active = null; camera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(camera.gameObject); UnityEngine.Object.DestroyImmediate(sun.gameObject);
    }
}
