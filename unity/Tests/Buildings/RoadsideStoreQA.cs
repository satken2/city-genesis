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

// Use the main DLL, the actual FBXs of the roadside store, and the URP in an isolated project to verify and render the store with
// its parking lot on parcels from the smallest to a large one, with the footprints the core's setback shape gives them: the lot's
// site starts with the road-facing parcel edge, everything stays on the parcel and off the building, stalls are laid on every
// parcel, and the parked cars follow the building's operation rate (none at 0, all at 1). Also reports the generation time.
[InitializeOnLoad]
public static class RoadsideStoreQA
{
    const string Prefix = "a2020";
    const float FloorHeight = 4f;
    static Assembly assembly;
    static int checks;
    static string output;
    static readonly List<string> stats = new List<string>();

    static RoadsideStoreQA()
    {
        if (SessionState.GetBool("RoadsideStoreQA", false)) EditorApplication.delayCall += Start;
    }

    public static void Run()
    {
        SessionState.SetBool("RoadsideStoreQA", true);
        GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        AssetDatabase.Refresh();
        // Leaves embedded in the FBXs are assigned to the shared foliage materials, as the main project's import settings do.
        var shared = Directory.GetFiles("Assets/Models/Materials", "*.mat").ToDictionary(Path.GetFileNameWithoutExtension, f => AssetDatabase.LoadAssetAtPath<Material>(f.Replace('\\', '/')));
        foreach (string file in Directory.GetFiles("Assets/Models/Buildings/" + Prefix, "*.fbx", SearchOption.AllDirectories))
        {
            string path = file.Replace('\\', '/');
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            var map = importer.GetExternalObjectMap();
            bool changed = false;
            foreach (var material in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
            {
                var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), material.name);
                if (!shared.TryGetValue(material.name, out Material target) || map.ContainsKey(id)) continue;
                importer.AddRemap(id, target);
                changed = true;
            }
            if (changed) importer.SaveAndReimport();
        }
        // The game resolves parts through the mirror prefabs under Resources.
        ModelPrefabGenerator.SyncAll();
        EditorApplication.EnterPlaymode();
    }

    static void Require(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static object Field(object value, string name) => value.GetType().GetField(name)?.GetValue(value) ?? value.GetType().GetProperty(name)?.GetValue(value);

    static void Start()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.delayCall += Start; return; }
        SessionState.SetBool("RoadsideStoreQA", false);
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        output = root + "/Temp/RoadsideStoreQA";
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
    static float Sloping(float wx, float wz) => 70 + .05f * wx + .03f * wz;

    static bool Inside(Vector2[] polygon, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            if ((polygon[i].y > p.y) != (polygon[j].y > p.y) && p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x) inside = !inside;
        return inside;
    }

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
        var lotBuilder = assembly.GetType("OpenSpaceBuildingMeshBuilder");
        var bodyBuilder = assembly.GetType("ModularBuildingMeshBuilder");
        var utils = assembly.GetType("BuildingMeshUtils");
        var constants = assembly.GetType("Constants");
        int flat = (int)constants.GetField("MODEL_MODULAR_FLAT").GetRawConstantValue(), setback = (int)constants.GetField("SHAPE_SETBACK").GetRawConstantValue();
        var ground = assembly.GetType("Utils").GetMethod("GetGroundHeight");
        float Ground(float x, float z) => (float)ground.Invoke(null, new object[] { new Vector3(x, 1000f, z) });
        new GameObject("ModelManager").AddComponent(assembly.GetType("ModelManager"));
        utils.GetProperty("CurrentDrawLod").SetValue(null, Enum.Parse(assembly.GetType("DrawLod"), "Normal"));

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

        // The parts as the game resolves them: the model folder picked by the weights table, through the mirror prefabs.
        var parts = new Dictionary<string, GameObject>();
        lotBuilder.GetMethod("CollectParts", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { Prefix, .5f, true, parts });
        foreach (string part in new[] { "site_paving", "site_curb", "site_grass", "site_marking", "site_stop", "site_bollard", "site_sign", "site_light",
            "site_clutter1", "site_car1", "site_shrub1", "site_tree1" })
            Require(parts.ContainsKey(part), "Missing part " + part);
        var sizes = new Dictionary<string, Vector2>();
        foreach (var pair in parts) if (pair.Value.GetComponentsInChildren<MeshFilter>().Length > 0) sizes[pair.Key] = Size(pair.Value);
        Require(sizes["site_car1"].y > sizes["site_car1"].x, "A car is not authored nose-forward");
        var floor = (GameObject)utils.GetMethod("GetPartNoPosition").Invoke(null, new object[] { Prefix, "floor", 0, true });
        Require(floor != null, "Missing part floor");
        Material floorSource = floor.GetComponentInChildren<Renderer>().sharedMaterial;

        // (name, parcel with the road-facing edge first, footprint from the core's setback shape with a floor of 300 m2, least stalls expected)
        var cases = new (string name, Vector2[] parcel, Vector2[] footprint, int stalls)[]
        {
            ("smallest", new[] { new Vector2(-10.000f, -15.000f), new Vector2(10.000f, -15.000f), new Vector2(10.000f, 15.000f), new Vector2(-10.000f, 15.000f) },
                new[] { new Vector2(-9.000f, -2.667f), new Vector2(9.000f, -2.667f), new Vector2(9.000f, 14.000f), new Vector2(-9.000f, 14.000f) }, 4),
            ("small", new[] { new Vector2(-12.500f, -18.000f), new Vector2(12.500f, -18.000f), new Vector2(12.500f, 18.000f), new Vector2(-12.500f, 18.000f) },
                new[] { new Vector2(-11.500f, 3.957f), new Vector2(11.500f, 3.957f), new Vector2(11.500f, 17.000f), new Vector2(-11.500f, 17.000f) }, 6),
            ("medium", new[] { new Vector2(-20.000f, -22.500f), new Vector2(20.000f, -22.500f), new Vector2(20.000f, 22.500f), new Vector2(-20.000f, 22.500f) },
                new[] { new Vector2(-15.000f, 11.500f), new Vector2(15.000f, 11.500f), new Vector2(15.000f, 21.500f), new Vector2(-15.000f, 21.500f) }, 12),
            ("wide", new[] { new Vector2(-35.000f, -16.000f), new Vector2(35.000f, -16.000f), new Vector2(35.000f, 16.000f), new Vector2(-35.000f, 16.000f) },
                new[] { new Vector2(-15.000f, 5.000f), new Vector2(15.000f, 5.000f), new Vector2(15.000f, 15.000f), new Vector2(-15.000f, 15.000f) }, 12),
            ("deep", new[] { new Vector2(-13.000f, -35.000f), new Vector2(13.000f, -35.000f), new Vector2(13.000f, 35.000f), new Vector2(-13.000f, 35.000f) },
                new[] { new Vector2(-12.000f, 21.500f), new Vector2(12.000f, 21.500f), new Vector2(12.000f, 34.000f), new Vector2(-12.000f, 34.000f) }, 12),
            ("corner", new[] { new Vector2(-22.500f, -22.500f), new Vector2(22.500f, -22.500f), new Vector2(22.500f, 22.500f), new Vector2(-22.500f, 22.500f) },
                new[] { new Vector2(-15.000f, 0.627f), new Vector2(-0.627f, 0.627f), new Vector2(-0.627f, 21.500f), new Vector2(-15.000f, 21.500f) }, 12),
            ("wedge", new[] { new Vector2(-28.566f, -22.035f), new Vector2(23.434f, -26.035f), new Vector2(29.434f, 13.965f), new Vector2(5.434f, 29.965f), new Vector2(-26.566f, 17.965f) },
                new[] { new Vector2(-13.589f, 16.408f), new Vector2(16.322f, 14.107f), new Vector2(16.863f, 21.143f), new Vector2(5.305f, 28.849f), new Vector2(-13.165f, 21.922f) }, 12),
            ("large", new[] { new Vector2(-45.000f, -35.000f), new Vector2(45.000f, -35.000f), new Vector2(45.000f, 35.000f), new Vector2(-45.000f, 35.000f) },
                new[] { new Vector2(-15.000f, 24.000f), new Vector2(15.000f, 24.000f), new Vector2(15.000f, 34.000f), new Vector2(-15.000f, 34.000f) }, 40),
            ("turned", new[] { new Vector2(-8.000f, -30.000f), new Vector2(30.000f, -8.000f), new Vector2(8.000f, 30.000f), new Vector2(-30.000f, 8.000f) },
                new[] { new Vector2(-18.470f, 1.965f), new Vector2(7.493f, 16.996f), new Vector2(2.482f, 25.650f), new Vector2(-23.480f, 10.619f) }, 12),
        };
        var generate = bodyBuilder.GetMethod("GenerateInternal", BindingFlags.Static | BindingFlags.NonPublic);
        var unexpected = new List<string>();
        foreach (var item in cases)
        {
            bool slope = item.name == "wide" || item.name == "turned";
            Shape(slope ? Sloping : (Func<float, float, float>)Rolling);
            string name = Prefix + "-" + item.name;
            // The parcel is handed over starting somewhere else, as the game's parcel is.
            var parcel = item.parcel.Skip(2).Concat(item.parcel.Take(2)).ToArray();
            var lot = (Vector2[])layout.GetMethod("LotFacingRoad").Invoke(null, new object[] { parcel, item.footprint });
            Require(lot.Length == item.parcel.Length && lot[0] == item.parcel[0] && lot[1] == item.parcel[1], name + ": the lot does not start with the road-facing edge: " + string.Join(" ", lot));

            var watch = Stopwatch.StartNew();
            object plan = layout.GetMethod("CreateLot").Invoke(null, new object[] { lot, item.footprint, .2f, sizes });
            double planMs = watch.Elapsed.TotalMilliseconds;
            for (int i = 0; i < 2; i++)
            {
                watch.Restart();
                layout.GetMethod("CreateLot").Invoke(null, new object[] { lot, item.footprint, .2f, sizes });
                planMs = Math.Min(planMs, watch.Elapsed.TotalMilliseconds);
            }
            var all = (IList)Field(plan, "Props");
            var props = all.Cast<object>().ToList();
            string Part(object prop) => (string)Field(prop, "Part");
            float Use(object prop) => (float)Field(prop, "Use");
            foreach (object prop in props)
            {
                Require(sizes.ContainsKey(Part(prop)), name + ": unknown part " + Part(prop));
                Vector2 position = (Vector2)Field(prop, "Position");
                Require(Inside(item.parcel, position), $"{name}: {Part(prop)} lies off the parcel");
                Require(!Inside(item.footprint, position), $"{name}: {Part(prop)} stands inside the building");
                Require(Part(prop).StartsWith("site_car") == Use(prop) > 0f, $"{name}: {Part(prop)} has use {Use(prop)}");
            }
            int stalls = props.Count(p => Part(p) == "site_stop"), cars = props.Count(p => Part(p).StartsWith("site_car"));
            if (stalls < item.stalls) unexpected.Add($"{name}: {stalls} stalls, expected at least {item.stalls}");
            if (props.Count(p => Part(p) == "site_sign") != 1) unexpected.Add(name + ": no sign, or more than one");
            if (!props.Any(p => Part(p).StartsWith("site_shrub"))) unexpected.Add(name + ": no shrub in the planted strip");
            if (cars == 0) unexpected.Add(name + ": no stall ever takes a car");
            var surfaces = ((IEnumerable)Field(plan, "Surfaces")).Cast<object>().ToList();
            Require(surfaces.Count(s => (string)Field(s, "Material") == "site_marking") == stalls * 2, name + ": a stall does not have its two lines");

            // Parcels lie at any angle: the same lot turned keeps its stalls, and every surface triangulates to its own area
            // (a triangulation that gives up on a thin band would cover the lot instead).
            foreach (float angle in new[] { 0f, 17f, 30f, 73f, 121f, 200f })
            {
                Vector2 Turn(Vector2 p) { Vector3 q = Quaternion.Euler(0, angle, 0) * new Vector3(p.x, 0, p.y); return new Vector2(q.x + 130f, q.z - 45f); }
                object turned = layout.GetMethod("CreateLot").Invoke(null, new object[] { lot.Select(Turn).ToArray(), item.footprint.Select(Turn).ToArray(), .2f, sizes });
                int turnedStalls = ((IEnumerable)Field(turned, "Props")).Cast<object>().Count(p => Part(p) == "site_stop");
                Require(Math.Abs(turnedStalls - stalls) <= 2, $"{name}: {turnedStalls} stalls when turned by {angle}, {stalls} unturned");
                foreach (object surface in (IEnumerable)Field(turned, "Surfaces"))
                {
                    float Area(Vector2[] ring) { float sum = 0; for (int i = 0; i < ring.Length; i++) sum += ring[i].x * ring[(i + 1) % ring.Length].y - ring[(i + 1) % ring.Length].x * ring[i].y; return Mathf.Abs(sum) * .5f; }
                    float expected = Area((Vector2[])Field(surface, "Polygon"));
                    if (Field(surface, "Holes") is Vector2[][] holes) foreach (var hole in holes) expected -= Area(hole);
                    var call = new object[] { surface, null, null };
                    lotBuilder.GetMethod("Triangulate").Invoke(null, call);
                    var points = (List<Vector2>)call[1]; var triangles = (List<int>)call[2];
                    float covered = 0;
                    for (int k = 0; k + 2 < triangles.Count; k += 3) covered += Area(new[] { points[triangles[k]], points[triangles[k + 1]], points[triangles[k + 2]] });
                    Require(Mathf.Abs(covered - expected) <= .02f * Mathf.Max(1f, expected), $"{name}: a {Field(surface, "Material")} surface of {expected:F1} m2 triangulates to {covered:F1} m2 when turned by {angle}");
                }
            }

            var center = new Vector3(0, Ground(0, 0), 0);
            // The building with its lot at an operation rate: cars whose threshold the rate does not exceed are left out, as the game's instancing does.
            object Building(float rate)
            {
                all.Clear();
                foreach (object prop in props) if (Use(prop) <= 0f || rate > Use(prop)) all.Add(prop);
                object ground = lotBuilder.GetMethod("Build").Invoke(null, new object[] { plan, parts, center, true, floorSource, null, null });
                all.Clear();
                foreach (object prop in props) all.Add(prop);
                return generate.Invoke(null, new object[] { item.footprint, 1, Prefix, FloorHeight, .5f, .2f, flat, setback, null, center, false, true, null, null, ground });
            }
            int Parked(float rate) => props.Count(p => Use(p) > 0f && rate > Use(p));
            Require(Parked(0f) == 0 && Parked(1f) == cars, $"{name}: {Parked(0f)} cars at rate 0, {Parked(1f)} of {cars} at rate 1");
            if (cars >= 4) Require(Parked(.5f) > 0 && Parked(.5f) < cars, $"{name}: {Parked(.5f)} of {cars} cars at rate 0.5");

            // The body alone: one floor of walls ending in the parapet.
            object body = generate.Invoke(null, new object[] { item.footprint, 1, Prefix, FloorHeight, .5f, .2f, flat, setback, null, center, false, true, null, null, null });
            float top = ((Mesh)Field(body, "Mesh")).bounds.max.y;
            Require(top > FloorHeight + .3f && top < FloorHeight + .8f, name + ": the store's walls do not end in the parapet: " + top);
            UnityEngine.Object.DestroyImmediate((Mesh)Field(body, "Mesh"));
            UnityEngine.Object.DestroyImmediate((Mesh)Field(body, "ColliderMesh"));

            float radius = item.parcel.Max(p => p.magnitude);
            Vector2 u = (lot[1] - lot[0]).normalized, v = new Vector2(-u.y, u.x);
            // The camera's azimuth 0 looks along +Z; turn it to look inward across the road-facing edge.
            float inward = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;
            Vector2 door = (item.footprint[0] + item.footprint[1]) * .5f - v * 5f;
            int vertices = 0;
            foreach (float rate in new[] { 1f, .5f, 0f })
            {
                object result = Building(rate);
                var mesh = (Mesh)Field(result, "Mesh");
                var materials = (Material[])Field(result, "Materials");
                Require(mesh != null && mesh.vertexCount > 0 && materials.All(m => m != null), name + ": empty mesh or missing material");
                Require(Field(result, "ColliderMesh") != null, name + ": no collider");
                if (rate == 1f) vertices = mesh.vertexCount;
                var go = new GameObject("RoadsideStore");
                go.transform.position = center;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = materials.Select(Offset).ToArray();
                string tag = rate == 1f ? "" : rate == 0f ? "-closed" : "-half";
                if (rate == 1f) Capture("warmup", center, radius * .8f, 40f, inward + 25f);
                Capture(name + tag, center, radius * .8f, 42f, inward + 25f);
                if (rate == 1f)
                {
                    Capture(name + "-street", center, Mathf.Min(radius * .6f, 28f), 12f, inward - 30f);
                    Capture(name + "-front", new Vector3(door.x, Ground(door.x, door.y) + 1.5f, door.y), 10f, 14f, inward + 20f);
                    Capture(name + "-rear", center, radius * .8f, 35f, inward + 200f);
                }
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate((Mesh)Field(result, "ColliderMesh"));
            }

            double buildMs = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                watch.Restart();
                object built = Building(1f);
                buildMs = Math.Min(buildMs, watch.Elapsed.TotalMilliseconds);
                UnityEngine.Object.DestroyImmediate((Mesh)Field(built, "Mesh"));
                UnityEngine.Object.DestroyImmediate((Mesh)Field(built, "ColliderMesh"));
            }
            stats.Add($"{name}: {stalls} stalls, cars {Parked(0f)}/{Parked(.5f)}/{cars} at rate 0/0.5/1, {props.Count(p => Part(p).StartsWith("site_shrub"))} shrubs, " +
                $"{props.Count(p => Part(p).StartsWith("site_tree"))} trees, {props.Count(p => Part(p) == "site_light")} lights, {props.Count} props, {surfaces.Count} surfaces, " +
                $"{vertices} vertices, plan {planMs:F1}ms, build {buildMs:F1}ms");
        }
        UnityEngine.Object.DestroyImmediate(terrainObject);
        // Reported after every parcel is rendered, so one run shows them all.
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
