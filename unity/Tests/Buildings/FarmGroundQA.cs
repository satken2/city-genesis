using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Use the main DLL, the farms' actual FBXs, and the URP in an isolated project to verify and render farm ground on sloped terrain:
// the ground panel and the soil of the tiles share one soil texture at one density, and its color matches the plain materials it replaces.
[InitializeOnLoad]
public static class FarmGroundQA
{
    const int MODEL_MODULAR_GROUND = 4, SHAPE_FULL = 4;
    static Assembly assembly;
    static int checks;
    static string output;
    static readonly List<string> stats = new List<string>();

    static FarmGroundQA()
    {
        if (SessionState.GetBool("FarmGroundQA", false)) EditorApplication.delayCall += Start;
    }

    public static void Run()
    {
        SessionState.SetBool("FarmGroundQA", true);
        GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        foreach (string path in Directory.GetFiles("Assets/Models/Buildings", "*.fbx", SearchOption.AllDirectories))
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(path.Replace('\\', '/'));
            if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
        }
        EditorApplication.EnterPlaymode();
    }

    static void Require(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }

    static void Start()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.delayCall += Start; return; }
        SessionState.SetBool("FarmGroundQA", false);
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        output = root + "/Temp/FarmGroundQA";
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
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static Material Find(string fbx, string name)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Buildings/" + fbx);
        return prefab.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m != null && m.name == name);
    }

    // Mean color of a 10m square of the material seen from above. uvPerMeter is the UV density of the surface it is used on.
    static Color Mean(Material material, float uvPerMeter)
    {
        var mesh = new Mesh();
        mesh.vertices = new[] { new Vector3(-5, 0, -5), new Vector3(-5, 0, 5), new Vector3(5, 0, 5), new Vector3(5, 0, -5) };
        mesh.uv = mesh.vertices.Select(v => new Vector2(v.x, v.z) * uvPerMeter).ToArray();
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateNormals();
        var go = new GameObject("Swatch");
        go.transform.position = new Vector3(0, 2000, 0);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional; sun.intensity = 1.2f; sun.shadows = LightShadows.None;
        sun.transform.rotation = Quaternion.Euler(60, 20, 0);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.4f, 0.4f, 0.4f);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.orthographic = true; camera.orthographicSize = 4.5f;
        camera.transform.position = go.transform.position + Vector3.up * 20; camera.transform.rotation = Quaternion.Euler(90, 0, 0);
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.magenta;
        var rt = new RenderTexture(256, 256, 24);
        camera.targetTexture = rt; camera.Render();
        RenderTexture.active = rt;
        var image = new Texture2D(256, 256, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); image.Apply();
        Color32[] pixels = image.GetPixels32();
        double r = 0, g = 0, b = 0;
        foreach (Color32 p in pixels) { r += p.r; g += p.g; b += p.b; }
        RenderTexture.active = null; camera.targetTexture = null;
        foreach (UnityEngine.Object o in new UnityEngine.Object[] { image, rt, camera.gameObject, sun.gameObject, go, mesh }) UnityEngine.Object.DestroyImmediate(o);
        return new Color((float)(r / pixels.Length / 255), (float)(g / pixels.Length / 255), (float)(b / pixels.Length / 255));
    }

    static void SameColor(string label, Material plain, float plainDensity, Material textured, float texturedDensity)
    {
        Require(plain != null && textured != null, label + ": material missing");
        Color a = Mean(plain, plainDensity), b = Mean(textured, texturedDensity);
        stats.Add($"{label}: plain {plain.name} ({a.r * 255:F0},{a.g * 255:F0},{a.b * 255:F0}) textured {textured.name} ({b.r * 255:F0},{b.g * 255:F0},{b.b * 255:F0})");
        for (int c = 0; c < 3; c++) Require(Mathf.Abs(a[c] - b[c]) <= 0.06f * a[c] + 3f / 255f, label + ": mean color differs");
    }

    static void Suite()
    {
        var lit = Shader.Find("Custom/DepthOffsetLit");
        var terrainShader = Shader.Find("Tests/DepthOffsetTerrainQA");
        Require(lit != null && terrainShader != null, "Missing depth-offset shaders");
        var converted = new Dictionary<Material, Material>();
        Material Offset(Material source)
        {
            if (source == null) return null;
            if (!converted.TryGetValue(source, out Material material)) converted[source] = material = new Material(source) { shader = lit };
            return material;
        }

        // The textured materials replace plain ones of the same color: the floor's and path's BD_DIRT and the furrows' MAT_SOIL_TILLED.
        // The distant models keep the plain materials, which gives the reference colors.
        Material soil = Find("a1002/0/floor.fbx", "BD_GROUND_SOIL");
        Require(soil != null && soil.GetTexture("_BaseMap") != null && Find("a1000/0/floor.fbx", "BD_GROUND_SOIL") == soil, "Farm floors are not remapped to the soil material");
        Material soilPart = Find("a1000/0/tile.fbx", "BD_GROUND_SOIL_PART");
        Material tilledPart = Find("a1002/0/tile.fbx", "BD_GROUND_SOIL_TILLED_PART");
        foreach (string tile in new[] { "a1000/300/tile.fbx", "a1000/600/tile.fbx", "a1002/500/tile.fbx" })
            Require(Find(tile, "BD_GROUND_SOIL_PART") == soilPart && soilPart != null, tile + " soil is not remapped");
        float panelDensity = (float)assembly.GetType("ModularBodyBuilder").GetField("GROUND_UV_SCALE").GetRawConstantValue();
        const float partDensity = 0.5f;  // Model faces carry UVs from a 2m cube projection.
        Require(Mathf.Approximately(soilPart.GetTextureScale("_BaseMap").x * partDensity, soil.GetTextureScale("_BaseMap").x * panelDensity),
            "Soil on tiles and on the ground panel repeats at different sizes");
        Capture("warmup", Vector3.zero, 10f, 30f, 0f);
        SameColor("floor soil", Find("a1000/0/far.fbx", "BD_DIRT"), partDensity, soil, panelDensity);
        SameColor("tile soil", Find("a1000/0/far.fbx", "BD_DIRT"), partDensity, soilPart, partDensity);
        SameColor("furrow soil", Find("a1002/0/far.fbx", "MAT_SOIL_TILLED"), partDensity, tilledPart, partDensity);

        // Grassland rising 6% away from the road, with gentle swells.
        const int res = 513;
        var data = new TerrainData { heightmapResolution = res, size = new Vector3(500, 200, 500) };
        var heights = new float[res, res];
        float cell = 500f / (res - 1);
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                float wx = x * cell, wz = z * cell;
                heights[z, x] = (60 + .06f * wz + 2f * Mathf.Sin(wx / 31f) * Mathf.Cos(wz / 43f)) / 200f;
            }
        data.SetHeights(0, 0, heights);
        var terrainObject = Terrain.CreateTerrainGameObject(data);
        terrainObject.transform.position = new Vector3(-250, 0, -250);
        var terrain = terrainObject.GetComponent<Terrain>();
        terrain.drawInstanced = false;
        terrain.heightmapPixelError = (float)assembly.GetType("LocalConstants").GetField("TERRAIN_HEIGHTMAP_PIXEL_ERROR").GetRawConstantValue();
        var grass = new Material(terrainShader);
        grass.SetColor("_BaseColor", new Color(.42f, .5f, .3f));
        grass.SetFloat("_Smoothness", .1f);
        terrain.materialTemplate = grass;
        Physics.SyncTransforms();
        assembly.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);

        // The game resolves parts through ModelManager; give it the actual FBXs.
        var managerType = assembly.GetType("ModelManager");
        var manager = new GameObject("Models").AddComponent(managerType);
        var cache = (IDictionary)managerType.GetField("_modelCache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
        foreach (string path in Directory.GetFiles("Assets/Models/Buildings", "*.fbx", SearchOption.AllDirectories))
        {
            string asset = path.Replace('\\', '/');
            if (asset.Contains("/Materials/") || asset.Contains("/Textures/")) continue;
            cache[asset.Substring("Assets/".Length, asset.Length - "Assets/".Length - 4)] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
        }

        var builder = assembly.GetType("ModularBodyBuilder");
        var utils = assembly.GetType("BuildingMeshUtils");
        var exterior = assembly.GetType("BuildingExteriorRenderer");
        var capture = exterior.GetField("Capture", BindingFlags.Static | BindingFlags.NonPublic);
        Type jobType = exterior.GetNestedType("Build");
        utils.GetProperty("CurrentDrawLod").SetValue(null, Enum.Parse(assembly.GetType("DrawLod"), "Normal"));
        var ground = assembly.GetType("Utils").GetMethod("GetGroundHeight");

        // (name, part folder, exterior model fraction, parcel)
        var farms = new (string name, string prefix, float variant, Vector2[] parcel)[]
        {
            ("vegetable-field", "a1002", .2f, new[] { new Vector2(-48, -36), new Vector2(46, -40), new Vector2(52, 34), new Vector2(-44, 38) }),
            ("greenhouses", "a1002", .8f, new[] { new Vector2(-50, -40), new Vector2(50, -40), new Vector2(50, 40), new Vector2(-50, 40) }),
            ("wheat", "a1000", .1f, new[] { new Vector2(-70, -45), new Vector2(60, -52), new Vector2(72, 30), new Vector2(10, 58), new Vector2(-66, 44) }),
            ("stubble", "a1000", .9f, new[] { new Vector2(-55, -40), new Vector2(55, -40), new Vector2(55, 40), new Vector2(-55, 40) }),
        };
        foreach (var farm in farms)
        {
            var center = new Vector3(0, (float)ground.Invoke(null, new object[] { new Vector3(0, 1000f, 0) }), 0);
            // As for a shown building in the game: the ground panel is captured as a deferred job and built as its own exterior mesh.
            var jobs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(jobType));
            capture.SetValue(null, jobs);
            object body;
            try
            {
                body = builder.GetMethod("GenerateBody").Invoke(null, new object[] {
                    farm.parcel, 1, farm.prefix, 4f, farm.variant, SHAPE_FULL, MODEL_MODULAR_GROUND, farm.parcel, center, true, soil, null, null, .37f, null });
            }
            finally { capture.SetValue(null, null); }
            Type bodyType = body.GetType();
            var instances = (List<CombineInstance>)bodyType.GetField("CombineInstances").GetValue(body);
            var materials = (List<Material>)bodyType.GetField("Materials").GetValue(body);
            var owned = (List<Mesh>)bodyType.GetField("TemporaryMeshes").GetValue(body);
            Require(instances.Count > 0 && jobs.Count == 1, $"{farm.name}: {instances.Count} parts and {jobs.Count} deferred jobs");
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.CombineMeshes(instances.ToArray(), false, true);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var panelParts = new List<CombineInstance>();
            var panelMaterials = new List<Material>();
            ((Delegate)jobs[0]).DynamicInvoke(panelParts, panelMaterials, owned);
            Require(panelParts.Count == 1 && panelMaterials[0] == soil, farm.name + ": ground panel missing");
            Mesh panel = panelParts[0].mesh;
            Require(panel.uv.Length == panel.vertexCount && panel.normals.Length == panel.vertexCount, farm.name + ": ground panel without UVs or normals");

            var go = new GameObject("Farm");
            go.transform.position = center;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = materials.Select(Offset).ToArray();
            var exteriorObject = new GameObject("BuildingExterior");
            exteriorObject.transform.SetParent(go.transform, false);
            exteriorObject.AddComponent<MeshFilter>().sharedMesh = panel;
            var exteriorRenderer = exteriorObject.AddComponent<MeshRenderer>();
            exteriorRenderer.sharedMaterial = soil;
            exteriorRenderer.shadowCastingMode = ShadowCastingMode.Off;

            string used = string.Join(",", materials.Where(m => m != null).Select(m => m.name).Distinct().OrderBy(n => n));
            stats.Add($"{farm.name}: {instances.Count} parts, {mesh.vertexCount} vertices, ground panel {panel.vertexCount} vertices, materials {used}");
            Require(!materials.Any(m => m != null && (m.name == "BD_DIRT" || m.name == "MAT_SOIL_TILLED")), farm.name + ": plain soil material left on the tiles");
            float radius = panel.bounds.extents.magnitude;
            Capture(farm.name, center, radius * .7f, 40f, 25f);
            Capture(farm.name + "-low", center + new Vector3(0, 0, -15), radius * .4f, 13f, -30f);
            Capture(farm.name + "-edge", center + new Vector3(farm.parcel[1].x - 8, 0, farm.parcel[1].y + 8), 9f, 30f, 150f);
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(mesh);
            foreach (Mesh m in owned) UnityEngine.Object.DestroyImmediate(m);
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
        camera.fieldOfView = 40f; camera.nearClipPlane = 0.3f; camera.farClipPlane = 3000f;
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
