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

// Use the main DLL, the actual FBXs of the gas station, and the URP in an isolated project to verify and render it on lots from the
// smallest to a large one: the units and loose props stay on the lot, the canopy's bays follow the lot's area and join edge to edge,
// the shop gives way to the office and then to pumps alone as the lot shrinks, and the plan's site starts with the parcel edge the
// building's front looks onto. Also reports the generation time.
[InitializeOnLoad]
public static class GasStationQA
{
    const int MODEL_GAS_STATION = 19;
    const string Folder = "a1019";
    static Assembly assembly;
    static int checks;
    static string output;
    static readonly List<string> stats = new List<string>();

    static GasStationQA()
    {
        if (SessionState.GetBool("GasStationQA", false)) EditorApplication.delayCall += Start;
    }

    public static void Run()
    {
        SessionState.SetBool("GasStationQA", true);
        GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        foreach (string path in Directory.GetFiles("Assets/Models/Buildings/" + Folder, "*.fbx", SearchOption.AllDirectories))
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
        SessionState.SetBool("GasStationQA", false);
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        output = root + "/Temp/GasStationQA";
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

    // Placement sample for the model gallery, in the format the open-space QA exports.
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
    // Ground rising gently away from the road with a slight roll, as a town lot has.
    static float Rolling(float wx, float wz) => 70 + .02f * wz + .6f * Mathf.Sin(wx / 37f) * Mathf.Cos(wz / 49f);
    // A lot on a slope across the frontage.
    static float Sloping(float wx, float wz) => 70 + .06f * wx + .03f * wz;

    static bool Inside(Vector2[] polygon, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            if ((polygon[i].y > p.y) != (polygon[j].y > p.y) && p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x) inside = !inside;
        return inside;
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
            // The leaves of the planting keep their own shader.
            if (source == null || source.shader == lit || source.shader.name.Contains("Foliage")) return source;
            if (!converted.TryGetValue(source, out Material material)) converted[source] = material = new Material(source) { shader = lit };
            return material;
        }
        var layout = assembly.GetType("OpenSpaceLayout");
        var builder = assembly.GetType("OpenSpaceBuildingMeshBuilder");
        var ground = assembly.GetType("Utils").GetMethod("GetGroundHeight");
        float Ground(float x, float z) => (float)ground.Invoke(null, new object[] { new Vector3(x, 1000f, z) });

        var data = new TerrainData { heightmapResolution = RES, size = new Vector3(SPAN, TOP, SPAN) };
        var terrainObject = Terrain.CreateTerrainGameObject(data);
        terrainObject.transform.position = new Vector3(-SPAN / 2, 0, -SPAN / 2);
        var terrain = terrainObject.GetComponent<Terrain>();
        terrain.drawInstanced = false;
        terrain.heightmapPixelError = (float)assembly.GetType("LocalConstants").GetField("TERRAIN_HEIGHTMAP_PIXEL_ERROR").GetRawConstantValue();
        var soil = new Material(terrainShader);
        soil.SetColor("_BaseColor", new Color(.42f, .5f, .3f));
        soil.SetFloat("_Smoothness", .1f);
        terrain.materialTemplate = soil;
        void Shape(Func<float, float, float> natural)
        {
            var heights = new float[RES, RES];
            float cell = SPAN / (RES - 1);
            for (int z = 0; z < RES; z++)
                for (int x = 0; x < RES; x++) heights[z, x] = natural(x * cell - SPAN / 2, z * cell - SPAN / 2) / TOP;
            data.SetHeights(0, 0, heights);
            Physics.SyncTransforms();
            assembly.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
        }
        Shape(Rolling);
        Capture("warmup", Vector3.zero, 10f, 30f, 0f);

        // The plan's site: the parcel, starting with the edge the building's front looks onto.
        var facing = layout.GetMethod("LotFacingRoad");
        Vector2[] parcel = { new Vector2(20, 15), new Vector2(-20, 15), new Vector2(-20, -15), new Vector2(20, -15) };
        Vector2[] footprint = { new Vector2(-8, -6), new Vector2(8, -6), new Vector2(8, 6), new Vector2(-8, 6) };
        var lot = (Vector2[])facing.Invoke(null, new object[] { parcel, footprint });
        Require(lot.Length == 4 && lot[0] == new Vector2(-20, -15) && lot[1] == new Vector2(20, -15), "The lot does not start with the edge the front looks onto: " + string.Join(" ", lot));
        Vector2[] turned = { new Vector2(8, -6), new Vector2(8, 6), new Vector2(-8, 6), new Vector2(-8, -6) };
        lot = (Vector2[])facing.Invoke(null, new object[] { parcel, turned });
        Require(lot[0] == new Vector2(20, -15) && lot[1] == new Vector2(20, 15), "The lot does not follow a front turned to the side: " + string.Join(" ", lot));
        Require((Vector2[])facing.Invoke(null, new object[] { null, footprint }) == footprint, "Without a parcel the footprint is the lot");

        var parts = new Dictionary<string, GameObject>();
        var sizes = new Dictionary<string, Vector2>();
        foreach (string file in Directory.GetFiles($"Assets/Models/Buildings/{Folder}/0", "*.fbx"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            parts[name] = AssetDatabase.LoadAssetAtPath<GameObject>(file.Replace('\\', '/'));
            Require(parts[name] != null, "Cannot load " + file);
            if (parts[name].GetComponentsInChildren<MeshFilter>().Length > 0) sizes[name] = Size(parts[name]);
        }
        foreach (string part in new[] { "floor", "site_pad", "site_joint", "site_paving", "site_curb", "site_grass", "site_marking", "site_canopy", "site_store", "site_office",
            "site_sign", "site_fascia", "site_fill", "site_vent", "site_air", "site_dumpster", "site_bollard", "site_stop", "site_light", "site_clutter1", "site_car1", "site_tanker",
            "site_shrub1", "site_tree1" })
            Require(parts.ContainsKey(part), "Missing part " + part);
        foreach (string unit in new[] { "site_canopy", "site_store", "site_office", "site_sign" })
            Require(parts.ContainsKey(unit + "_far"), unit + " has no distant model");
        // The layout assumes bays join edge to edge and cars are authored nose-forward.
        Require(Mathf.Abs(sizes["site_canopy"].x - 8f) < .05f && Mathf.Abs(sizes["site_canopy"].y - 8f) < .05f, "Canopy bay " + sizes["site_canopy"]);
        Require(sizes["site_car1"].y > sizes["site_car1"].x, "A car is not authored nose-forward");
        Material floorSource = parts["floor"].GetComponentInChildren<Renderer>().sharedMaterial;

        // (name, lot with the road-facing edge first, seed, terrain, shop expected, least bays expected)
        var cases = new (string name, Vector2[] polygon, float seed, Func<float, float, float> natural, string shop, int bays)[]
        {
            ("smallest", Box(10, 10), .2f, Rolling, null, 1),
            ("small", Box(15, 20), .2f, Rolling, "site_office", 1),
            ("corner-shop", Box(20, 25), .2f, Rolling, "site_store", 2),
            ("medium", Box(30, 30), .2f, Rolling, "site_store", 2),
            ("medium-mirrored", Box(30, 30), .7f, Rolling, "site_store", 2),
            ("wide", Box(48, 20), .2f, Rolling, "site_store", 2),
            ("deep", Box(18, 44), .2f, Rolling, "site_store", 2),
            ("town", Box(40, 36), .2f, Sloping, "site_store", 4),
            ("wedge", new[] { new Vector2(-28, -22), new Vector2(26, -26), new Vector2(30, 14), new Vector2(8, 30), new Vector2(-24, 18) }, .2f, Rolling, "site_store", 3),
            ("default", Box(40, 30), .2f, Rolling, "site_store", 4),
            ("block", Box(75, 75), .2f, Rolling, "site_store", 12),
            ("block-mirrored-slope", Box(75, 75), .7f, Sloping, "site_store", 12),
            ("large", Box(130, 90), .2f, Rolling, "site_store", 24),
        };
        Func<float, float, float> shaped = Rolling;
        var unexpected = new List<string>();
        foreach (var item in cases)
        {
            if (item.natural != shaped) Shape(shaped = item.natural);
            var watch = Stopwatch.StartNew();
            object plan = layout.GetMethod("Create").Invoke(null, new object[] { item.polygon, MODEL_GAS_STATION, item.seed, .5f, sizes });
            double planMs = watch.Elapsed.TotalMilliseconds;
            for (int i = 0; i < 2; i++)
            {
                watch.Restart();
                layout.GetMethod("Create").Invoke(null, new object[] { item.polygon, MODEL_GAS_STATION, item.seed, .5f, sizes });
                planMs = Math.Min(planMs, watch.Elapsed.TotalMilliseconds);
            }
            string name = Folder + "-" + item.name;
            if (item.name == "default") File.WriteAllText($"{output}/{Folder}.json", Export(plan, builder));
            Vector2 a = item.polygon[0], b = item.polygon[1], u = (b - a).normalized, v = new Vector2(-u.y, u.x);
            var center = new Vector3(0, Ground(0, 0), 0);
            float radius = item.polygon.Max(p => p.magnitude);

            var props = ((IEnumerable)Field(plan, "Props")).Cast<object>().ToList();
            string Part(object prop) => (string)Field(prop, "Part");
            var units = props.Where(p => (bool)Field(p, "Unit")).ToList();
            var loose = props.Where(p => !(bool)Field(p, "Unit")).ToList();
            Require(Field(plan, "Gate") == null, name + ": a gas station has no fence or gate");
            foreach (object prop in props)
            {
                Require(sizes.ContainsKey(Part(prop)), name + ": unknown part " + Part(prop));
                Vector2 position = (Vector2)Field(prop, "Position"), forward = (Vector2)Field(prop, "Forward");
                Require(Inside(item.polygon, position), $"{name}: {Part(prop)} lies off the lot");
                if (!(bool)Field(prop, "Unit") || Part(prop) == "site_sign" || Part(prop) == "site_fascia") continue;
                // A unit's whole rectangle stays on the lot.
                Vector2 right = new Vector2(forward.y, -forward.x), half = sizes[Part(prop)] * .5f;
                foreach (var corner in new[] { new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1) })
                    Require(Inside(item.polygon, position + right * (corner.x * (half.x - .05f)) + forward * (corner.y * (half.y - .05f))), $"{name}: {Part(prop)} reaches off the lot");
            }
            var bays = units.Where(p => Part(p) == "site_canopy").ToList();
            string shop = units.Select(Part).FirstOrDefault(p => p == "site_store" || p == "site_office");
            if (bays.Count < item.bays) unexpected.Add($"{name}: {bays.Count} bays, expected at least {item.bays}");
            if (shop != item.shop) unexpected.Add($"{name}: shop {shop ?? "none"}, expected {item.shop ?? "none"}");
            Require(bays.Count >= 1, name + ": no fueling bay");
            Require(units.Count(p => Part(p) == "site_store" || Part(p) == "site_office") <= 1, name + ": more than one shop");
            Require(units.Count(p => Part(p) == "site_sign") == 1, name + ": no price sign");
            // Bays join edge to edge: each stands a whole number of bay sizes from the first.
            foreach (object bay in bays)
            {
                Vector2 offset = (Vector2)Field(bay, "Position") - (Vector2)Field(bays[0], "Position");
                float along = Vector2.Dot(offset, u) / sizes["site_canopy"].x, across = Vector2.Dot(offset, v) / sizes["site_canopy"].y;
                Require(Mathf.Abs(along - Mathf.Round(along)) < .01f && Mathf.Abs(across - Mathf.Round(across)) < .01f, name + ": a bay is off the canopy's grid");
            }

            object Rebuild() => builder.GetMethod("Build").Invoke(null, new object[] { plan, parts, center, true, floorSource, null, null });
            object result = Rebuild();
            var mesh = (Mesh)Field(result, "Mesh");
            var materials = (Material[])Field(result, "Materials");
            Require(mesh != null && mesh.vertexCount > 0 && materials.All(m => m != null), name + ": empty mesh or missing material");
            Require(Field(result, "ColliderMesh") != null, name + ": no collider");

            var go = new GameObject("GasStation");
            go.transform.position = center;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = materials.Select(Offset).ToArray();
            // The camera's azimuth 0 looks along +Z; turn it to look inward across the road-facing edge.
            float inward = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;
            Capture("warmup", center, radius * .8f, 40f, inward + 25f);
            Capture(name, center, radius * .8f, 42f, inward + 25f);
            Capture(name + "-street", center, Mathf.Min(radius * .6f, 28f), 14f, inward - 30f);
            Vector2 bayAt = (Vector2)Field(bays[0], "Position");
            Capture(name + "-pumps", new Vector3(bayAt.x, Ground(bayAt.x, bayAt.y) + 1.5f, bayAt.y), 9f, 12f, inward + 40f);
            object shopUnit = units.FirstOrDefault(p => Part(p) == shop);
            if (shopUnit != null)
            {
                Vector2 at = (Vector2)Field(shopUnit, "Position") + (Vector2)Field(shopUnit, "Forward") * 6f;
                Capture(name + "-shop", new Vector3(at.x, Ground(at.x, at.y) + 1.5f, at.y), 12f, 16f, inward + 20f);
            }

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
            double fullMs = Best(Rebuild);
            // The game draws the loose props by instancing instead of combining them; without them, only the ground, the surfaces, and the units remain.
            var unitParts = parts.Where(pair => loose.All(prop => Part(prop) != pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            double unitsMs = Best(() => builder.GetMethod("Build").Invoke(null, new object[] { plan, unitParts, center, true, floorSource, null, null }));
            // A shown building defers the ground (the panel and the flat surfaces on it) to low-priority work: what is generated at once, and what later.
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
                    shown = Rebuild();
                    nowMs = Math.Min(nowMs, watch.Elapsed.TotalMilliseconds);
                }
                finally { capture.SetValue(null, null); }
                Require(jobs.Count == 1, $"{name}: {jobs.Count} deferred jobs");
                var laterMeshes = new List<CombineInstance>(); var laterMaterials = new List<Material>(); var temporary = new List<Mesh>();
                watch.Restart();
                ((Delegate)jobs[0]).DynamicInvoke(laterMeshes, laterMaterials, temporary);
                laterMs = Math.Min(laterMs, watch.Elapsed.TotalMilliseconds);
                foreach (Mesh m in temporary) UnityEngine.Object.DestroyImmediate(m);
                UnityEngine.Object.DestroyImmediate((Mesh)Field(shown, "Mesh"));
                UnityEngine.Object.DestroyImmediate((Mesh)Field(shown, "ColliderMesh"));
            }
            int stalls = loose.Count(p => Part(p) == "site_stop"), cars = loose.Count(p => Part(p).StartsWith("site_car"));
            stats.Add($"{name}: {bays.Count} bays, shop {shop ?? "none"}, {stalls} stalls, {cars} cars, {loose.Count} loose props, {((IList)Field(plan, "Surfaces")).Count} surfaces, " +
                $"{mesh.vertexCount} vertices, plan {planMs:F1}ms, build {fullMs:F1}ms (without the loose props {unitsMs:F1}ms); shown: {nowMs:F1}ms at once, ground {laterMs:F1}ms deferred");
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate((Mesh)Field(result, "ColliderMesh"));
        }
        UnityEngine.Object.DestroyImmediate(terrainObject);
        // Reported after every lot is rendered, so one run shows them all.
        Require(unexpected.Count == 0, string.Join("; ", unexpected));
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
