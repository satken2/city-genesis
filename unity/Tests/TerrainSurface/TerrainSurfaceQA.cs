using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public static class TerrainSurfaceQA
{
    static Assembly source;
    static readonly List<string> results = new List<string>();
    static object Call(string type, string method, params object[] args)
        => source.GetType(type).GetMethod(method, BindingFlags.Static | BindingFlags.Public).Invoke(null, args);
    static float Height(float x, float z) => (float)Call("Utils", "GetGroundHeight", new Vector3(x, 0, z));
    static float Constant(string name) => (float)source.GetType("ModularBodyBuilder").GetField(name).GetRawConstantValue();
    static float Exact => Constant("GROUND_PANEL_EXACT");
    static Mesh Floor(Vector2[] polygon, Vector3 center, float tolerance)
        => (Mesh)Call("ModularBodyBuilder", "GenerateTerrainConformingFloorMesh", polygon, center, tolerance);

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-surfaceSourceRoot") + 1];
        string output = root + "/Temp/TerrainSurfaceQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (o, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { "/Temp/bin/Debug", "/Library/ScriptAssemblies", "/Assets/Plugins" })
                    if (File.Exists(root + dir + "/" + name)) return Assembly.LoadFrom(root + dir + "/" + name);
                return null;
            };
            source = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            var data = new TerrainData { heightmapResolution = 65, size = new Vector3(64, 120, 64) };
            var heights = new float[65, 65];
            for (int z = 0; z < 65; z++)
                for (int x = 0; x < 65; x++)
                    heights[z, x] = (30 + .15f * x + .2f * Mathf.Sin(x * .9f) * Mathf.Cos(z * .7f)) / 120f;
            data.SetHeights(0, 0, heights);
            var terrainObject = Terrain.CreateTerrainGameObject(data);
            terrainObject.transform.position = new Vector3(-32, -20, -32);
            var terrain = terrainObject.GetComponent<Terrain>();
            terrain.heightmapPixelError = (float)source.GetType("LocalConstants").GetField("TERRAIN_HEIGHTMAP_PIXEL_ERROR").GetRawConstantValue();
            Physics.SyncTransforms();

            var a = new Vector3(-4.4f, 0, -3.2f);
            var b = new Vector3(-3.6f, 0, -3.2f);
            Call("Utils", "InvalidateHeightCache");
            float firstA = Height(a.x, a.z), firstB = Height(b.x, b.z);
            Call("Utils", "InvalidateHeightCache");
            float secondB = Height(b.x, b.z), secondA = Height(a.x, a.z);
            float cacheError = Mathf.Max(Mathf.Abs(firstA - secondA), Mathf.Abs(firstB - secondB));
            float sampleError = Mathf.Max(Mathf.Abs(firstA - (terrain.SampleHeight(a) - 20)), Mathf.Abs(firstB - (terrain.SampleHeight(b) - 20)));
            results.Add($"Height cache: order error {cacheError:F6}m, sample error {sampleError:F6}m");
            if (cacheError > .0001f || sampleError > .0001f) throw new Exception("Height cache depends on query order");

            var polygon = new[] { new Vector2(-12.3f, -10.2f), new Vector2(11.7f, -8.3f), new Vector2(10.2f, 11.4f), new Vector2(-4.6f, 6.3f), new Vector2(-11.3f, 10.8f) };
            var center = new Vector3(0, Height(0, 0), 0);
            var floor = Floor(polygon, center, Exact);
            CheckFloorArea(floor, polygon);
            var floorObject = new GameObject("Floor");
            floorObject.transform.position = center;
            var collider = floorObject.AddComponent<MeshCollider>();
            collider.sharedMesh = floor;
            Physics.SyncTransforms();
            float maxPenetration = 0, maxGap = 0;
            int count = 0;
            var ground = terrainObject.GetComponent<TerrainCollider>();
            for (float z = -12; z <= 12; z += .25f)
                for (float x = -13; x <= 13; x += .25f)
                {
                    var ray = new Ray(new Vector3(x, 100, z), Vector3.down);
                    if (!collider.Raycast(ray, out var surface, 200) || !ground.Raycast(ray, out var earth, 200)) continue;
                    count++;
                    maxPenetration = Mathf.Max(maxPenetration, earth.point.y - surface.point.y);
                    maxGap = Mathf.Max(maxGap, surface.point.y - earth.point.y);
                }
            results.Add($"Floor: {count} interior samples, {floor.vertexCount} vertices, penetration {maxPenetration:F6}m, gap {maxGap:F6}m");
            if (count < 100) throw new Exception("Floor coverage missing");
            if (maxPenetration > .001f) throw new Exception("Terrain penetrates floor");
            CheckFloorRendering(terrain, floorObject, floor, root);
            CheckCellBoundary(polygon);
            CheckAdaptive(root);
            CheckRoad(data, terrainObject);
            File.WriteAllText(output, "PASS\n" + string.Join("\n", results));
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, "FAIL\n" + string.Join("\n", results) + "\n" + ex);
            EditorApplication.Exit(1);
        }
    }

    static void CheckFloorArea(Mesh mesh, Vector2[] polygon)
    {
        double expected = 0, actual = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
            expected += (double)a.x * b.y - (double)a.y * b.x;
        }
        expected = Math.Abs(expected) * .5;
        var vertices = mesh.vertices;
        var indices = mesh.triangles;
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
            actual += Math.Abs((double)(b.x - a.x) * (c.z - a.z) - (double)(b.z - a.z) * (c.x - a.x)) * .5;
        }
        results.Add($"Floor area: expected {expected:F4}, actual {actual:F4} square metres");
        if (Math.Abs(expected - actual) > .005) throw new Exception("Floor area changed during clipping");
    }

    static void CheckCellBoundary(Vector2[] polygon)
    {
        var data = new TerrainData { heightmapResolution = 65, size = new Vector3(64, 120, 64) };
        var heights = new float[65, 65];
        for (int z = 0; z < 65; z++)
            for (int x = 0; x < 65; x++)
                heights[z, x] = (30 + .15f * (x + 64) + .2f * Mathf.Sin((x + 64) * .9f) * Mathf.Cos(z * .7f)) / 120f;
        data.SetHeights(0, 0, heights);
        var adjacent = Terrain.CreateTerrainGameObject(data);
        adjacent.transform.position = new Vector3(32, -20, -32);
        Call("Utils", "InvalidateHeightCache");
        var center = new Vector3(31.3f, Height(31.3f, 0), 0);
        var mesh = Floor(polygon, center, Exact);
        CheckFloorArea(mesh, polygon);
        var go = new GameObject("Boundary floor");
        go.transform.position = center;
        var collider = go.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;
        Physics.SyncTransforms();
        float penetration = 0;
        int count = 0;
        foreach (var terrain in Terrain.activeTerrains)
        {
            var ground = terrain.GetComponent<TerrainCollider>();
            for (float x = 25; x <= 38; x += .125f)
                for (float z = -7; z <= 6; z += .5f)
                {
                    var ray = new Ray(new Vector3(x, 100, z), Vector3.down);
                    if (!collider.Raycast(ray, out var floor, 200) || !ground.Raycast(ray, out var earth, 200)) continue;
                    count++;
                    penetration = Mathf.Max(penetration, earth.point.y - floor.point.y);
                }
        }
        results.Add($"Cell boundary: {count} samples, penetration {penetration:F6}m");
        if (count < 1000 || penetration > .001f) throw new Exception("Terrain penetrates floor at cell boundary");
        UnityEngine.Object.DestroyImmediate(go);
        UnityEngine.Object.DestroyImmediate(mesh);
        UnityEngine.Object.DestroyImmediate(adjacent);
        UnityEngine.Object.DestroyImmediate(data);
        Call("Utils", "InvalidateHeightCache");
    }

    // Ground panels away from close-up: even ground takes few triangles, benches and cut slopes keep the terrain's own triangles,
    // and the gap to the terrain stays within twice the tolerance without terrain showing through from Normal-LOD distances.
    static void CheckAdaptive(string root)
    {
        const int res = 513;
        float cell = 500f / (res - 1);
        var data = new TerrainData { heightmapResolution = res, size = new Vector3(500, 200, 500) };
        var heights = new float[res, res];
        var pit = new Vector2(330, 250);
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                float wx = x * cell, wz = z * cell;
                // Rolling ground, a leveled yard, and an open pit with 10m benches (6m berm, 3.5m face).
                float h = 120 + .05f * wx + 3f * Mathf.Sin(wx / 40f) * Mathf.Cos(wz / 55f);
                if (wx > 150 && wx < 230 && wz > 190 && wz < 240) h = 128;
                float inward = 70 - Vector2.Distance(new Vector2(wx, wz), pit);
                if (inward > 0)
                {
                    float step = Mathf.Floor(inward / 9.5f), along = inward - step * 9.5f;
                    h -= 10f * (step + Mathf.Clamp01(along / 3.5f));
                }
                heights[z, x] = h / 200f;
            }
        data.SetHeights(0, 0, heights);
        var terrainObject = Terrain.CreateTerrainGameObject(data);
        terrainObject.transform.position = new Vector3(1000, -20, -250);
        var terrain = terrainObject.GetComponent<Terrain>();
        terrain.heightmapPixelError = (float)source.GetType("LocalConstants").GetField("TERRAIN_HEIGHTMAP_PIXEL_ERROR").GetRawConstantValue();
        terrain.drawInstanced = false;
        Physics.SyncTransforms();
        Call("Utils", "InvalidateHeightCache");

        float tolerance = Constant("GROUND_PANEL_TOLERANCE"), offset = Constant("GROUND_OFFSET");
        var sites = new (string name, Vector3 center, Vector2[] polygon)[]
        {
            ("mine 3.4ha with pit", new Vector3(1290, 0, 0), new[] { new Vector2(-150, -58), new Vector2(118, -66), new Vector2(124, 71), new Vector2(-141, 55) }),
            ("rolling field 1.0ha", new Vector3(1090, 0, -120), new[] { new Vector2(-52, -47), new Vector2(49, -51), new Vector2(55, 46), new Vector2(-10, 30), new Vector2(-50, 52) }),
            ("leveled yard 0.3ha", new Vector3(1190, 0, -35), new[] { new Vector2(-36, -22), new Vector2(37, -21), new Vector2(36, 22), new Vector2(-37, 21) }),
            ("house lot 300m2", new Vector3(1100, 0, 60), new[] { new Vector2(-10, -8), new Vector2(10, -7), new Vector2(9, 8), new Vector2(-10, 7) }),
            ("large farm 19ha", new Vector3(1250, 0, 0), new[] { new Vector2(-240, -200), new Vector2(230, -215), new Vector2(240, 190), new Vector2(-235, 205) }),
            // An L-shaped lot whose outline has a reflex corner, vertices millimeters apart, and a nearly collinear run, as parcels cut by roads do.
            ("notched lot 0.2ha", new Vector3(1120, 0, 150), new[] { new Vector2(-30, -22), new Vector2(-4.2f, -22.004f), new Vector2(-4.198f, -22.003f), new Vector2(-4.196f, -22.004f),
                new Vector2(30, -21.6f), new Vector2(30.3f, 2.1f), new Vector2(6.4f, 2.4f), new Vector2(6.1f, 21), new Vector2(-12.3f, 21.2f), new Vector2(-30.2f, 21.5f) }),
        };
        // Clipping must settle every cell without falling back to warnings, which cost more than the geometry.
        int warnings = 0;
        Application.LogCallback onLog = (message, stack, type) => { if (type != LogType.Log) warnings++; };
        Application.logMessageReceived += onLog;
        // The first call pays for JIT compilation.
        UnityEngine.Object.DestroyImmediate(Floor(sites[3].polygon, new Vector3(sites[3].center.x, Height(sites[3].center.x, sites[3].center.z), sites[3].center.z), tolerance));
        var ground = terrainObject.GetComponent<TerrainCollider>();
        foreach (var site in sites)
        {
            var center = new Vector3(site.center.x, Height(site.center.x, site.center.z), site.center.z);
            Mesh[] meshes = new Mesh[2];
            double[] ms = new double[2];
            for (int mode = 0; mode < 2; mode++)
            {
                // Median of five runs: single runs vary with garbage collection.
                var samples = new List<double>();
                for (int run = 0; run < 5; run++)
                {
                    if (meshes[mode] != null) UnityEngine.Object.DestroyImmediate(meshes[mode]);
                    Call("Utils", "InvalidateHeightCache");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    meshes[mode] = Floor(site.polygon, center, mode == 0 ? Exact : tolerance);
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                ms[mode] = samples[2];
                CheckFloorArea(meshes[mode], site.polygon);
            }
            double area = 0;
            for (int i = 0; i < site.polygon.Length; i++)
            {
                Vector2 a = site.polygon[i], b = site.polygon[(i + 1) % site.polygon.Length];
                area += ((double)a.x * b.y - (double)a.y * b.x) * .5;
            }
            int cells = (int)(Math.Abs(area) / (cell * cell));
            results.Add($"Adaptive {site.name}: {cells} terrain cells; close-up {meshes[0].vertexCount} vertices {ms[0]:F1}ms; other LODs {meshes[1].vertexCount} vertices {meshes[1].triangles.Length / 3} triangles {ms[1]:F1}ms");

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in site.polygon) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y); }
            float stride = Mathf.Max(.31f, (maxX - minX) / 300f);
            for (int mode = 0; mode < 2; mode++)
            {
                var go = new GameObject("Adaptive floor");
                go.transform.position = center;
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = meshes[mode];
                Physics.SyncTransforms();
                float below = 0, above = 0;
                int count = 0;
                for (float z = minZ; z <= maxZ; z += stride)
                    for (float x = minX; x <= maxX; x += stride)
                    {
                        var ray = new Ray(new Vector3(center.x + x, 500, center.z + z), Vector3.down);
                        if (!collider.Raycast(ray, out var surface, 1000) || !ground.Raycast(ray, out var earth, 1000)) continue;
                        count++;
                        below = Mathf.Max(below, earth.point.y - surface.point.y);
                        above = Mathf.Max(above, surface.point.y - earth.point.y);
                    }
                float allowed = mode == 0 ? .001f : 2 * tolerance + .001f;
                results.Add($"  {(mode == 0 ? "close-up" : "other LODs")}: {count} samples, terrain above panel {below:F4}m, panel above terrain {above:F4}m");
                if (count < 200) throw new Exception("Adaptive floor coverage missing: " + site.name);
                if (below > allowed || above - offset > allowed) throw new Exception("Adaptive floor leaves the terrain: " + site.name);
                if (mode == 1) CheckAdaptiveRendering(terrain, go, meshes[0], meshes[1], center, Mathf.Max(maxX - minX, maxZ - minZ) * .5f, site.name, root);
                UnityEngine.Object.DestroyImmediate(go);
            }
            if (site.name.StartsWith("leveled") && meshes[1].vertexCount * 3 > cells)
                throw new Exception("Leveled ground is not simplified");
            if (meshes[1].vertexCount > meshes[0].vertexCount) throw new Exception("Tolerance adds vertices: " + site.name);
            UnityEngine.Object.DestroyImmediate(meshes[0]);
            UnityEngine.Object.DestroyImmediate(meshes[1]);
        }
        Application.logMessageReceived -= onLog;
        results.Add($"Adaptive: {warnings} warnings or errors logged");
        if (warnings > 0) throw new Exception("Ground panels logged warnings");
        UnityEngine.Object.DestroyImmediate(terrainObject);
        UnityEngine.Object.DestroyImmediate(data);
        Call("Utils", "InvalidateHeightCache");
    }

    // Perspective views from Normal-LOD distances and angles. Ground panels are drawn as deferred exteriors with the part's own material,
    // so only the terrain's depth offset (1,1) separates them from the terrain.
    // Terrain in front of the panel hides both panels alike, so count the pixels where terrain shows through the tolerant panel only.
    static void CheckAdaptiveRendering(Terrain terrain, GameObject floorObject, Mesh exact, Mesh floor, Vector3 center, float radius, string name, string root)
    {
        var earthMaterial = new Material(Shader.Find("Tests/SurfaceDepthQA")) { color = Color.green };
        earthMaterial.SetFloat("_Factor", 1);
        earthMaterial.SetFloat("_Units", 1);
        terrain.materialTemplate = earthMaterial;
        var filter = floorObject.AddComponent<MeshFilter>();
        var floorMaterial = new Material(Shader.Find("Tests/SurfaceDepthQA")) { color = Color.white };
        floorObject.AddComponent<MeshRenderer>().sharedMaterial = floorMaterial;
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.fieldOfView = 40;
        camera.nearClipPlane = .3f;
        camera.farClipPlane = 20000;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        var target = new RenderTexture(1600, 900, 24);
        camera.targetTexture = target;
        Color32[] Capture(string file)
        {
            camera.Render();
            RenderTexture.active = target;
            var texture = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
            texture.Apply();
            if (file != null) File.WriteAllBytes(root + "/Temp/TerrainSurfaceQA/" + file + ".png", texture.EncodeToPNG());
            var pixels = texture.GetPixels32();
            UnityEngine.Object.DestroyImmediate(texture);
            return pixels;
        }
        string slug = name.Split(' ')[0];
        foreach (float distance in new[] { 25f, 60f, 150f, 400f, 1200f })
            foreach (float elevation in new[] { 12f, 40f })
            {
                var look = center + new Vector3(Mathf.Min(radius * .5f, distance * .3f), 0, 0);
                camera.transform.position = look + Quaternion.Euler(elevation, 200, 0) * Vector3.back * distance;
                camera.transform.LookAt(look);
                filter.sharedMesh = exact;
                var reference = Capture(null);
                filter.sharedMesh = floor;
                var actual = Capture(distance == 150f ? $"adaptive-{slug}-{elevation}" : null);
                int covered = 0, holes = 0;
                for (int i = 0; i < actual.Length; i++)
                    if (reference[i].r > 250)
                    {
                        covered++;
                        if (actual[i].r < 100 && actual[i].g > 150) holes++;
                    }
                results.Add($"  rendering at {distance}m, {elevation} deg: {holes}/{covered} terrain pixels inside panel");
                if (distance >= 60f && holes > covered / 2000 + 5) throw new Exception($"Rendered terrain shows through the panel: {name} at {distance}m");
            }
        RenderTexture.active = null;
        camera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(camera.gameObject);
        UnityEngine.Object.DestroyImmediate(target);
    }

    static void CheckFloorRendering(Terrain terrain, GameObject floorObject, Mesh floor, string root)
    {
        terrain.drawInstanced = false;
        // Render in a solid color with only the same depth offset as the main assembly (terrain 1,1; cover -1,-1), and check that terrain color does not show inside the cover surface.
        var earthMaterial = new Material(Shader.Find("Tests/SurfaceDepthQA")) { color = Color.green };
        earthMaterial.SetFloat("_Factor", 1);
        earthMaterial.SetFloat("_Units", 1);
        terrain.materialTemplate = earthMaterial;
        floorObject.AddComponent<MeshFilter>().sharedMesh = floor;
        var floorMaterial = new Material(Shader.Find("Tests/SurfaceDepthQA")) { color = Color.white };
        floorMaterial.SetFloat("_Factor", -1);
        floorMaterial.SetFloat("_Units", -1);
        floorObject.AddComponent<MeshRenderer>().sharedMaterial = floorMaterial;
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 38, -25);
        camera.transform.LookAt(new Vector3(0, Height(0, 0), 0));
        camera.orthographic = true;
        camera.orthographicSize = 18;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        var target = new RenderTexture(800, 600, 24);
        camera.targetTexture = target;
        Color32[] Capture(string name)
        {
            camera.Render();
            RenderTexture.active = target;
            var texture = new Texture2D(800, 600, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, 800, 600), 0, 0);
            texture.Apply();
            File.WriteAllBytes(root + "/Temp/TerrainSurfaceQA/" + name + ".png", texture.EncodeToPNG());
            var pixels = texture.GetPixels32();
            UnityEngine.Object.DestroyImmediate(texture);
            return pixels;
        }
        foreach (float scale in new[] { 18f, 50f, 100f })
        {
            camera.orthographicSize = scale;
            camera.transform.position = new Vector3(0, Height(0, 0), 0) + new Vector3(0, 27, -25) * (scale / 18);
            camera.transform.LookAt(new Vector3(0, Height(0, 0), 0));
            terrain.drawHeightmap = false;
            var reference = Capture("floor-only-" + scale);
            terrain.drawHeightmap = true;
            var actual = Capture("floor-terrain-" + scale);
            int covered = 0, holes = 0;
            for (int i = 0; i < actual.Length; i++)
                if (reference[i].r > 250)
                {
                    covered++;
                    if (actual[i].r < 100 && actual[i].g > 150) holes++;
                }
            results.Add($"Floor rendering (scale {scale}): {holes}/{covered} terrain pixels inside floor");
            if (covered < 1000 || holes > 5) throw new Exception("Rendered terrain penetrates floor");
        }
        RenderTexture.active = null;
        camera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(camera.gameObject);
        UnityEngine.Object.DestroyImmediate(target);
    }

    static void CheckRoad(TerrainData data, GameObject terrainObject)
    {
        // The cross-section direction is a graded, level surface. Place a small ridge in the longitudinal direction that could be missed between samples.
        var heights = new float[65, 65];
        for (int z = 0; z < 65; z++)
            for (int x = 0; x < 65; x++)
                heights[z, x] = (30 + .03f * x + .35f * Mathf.Sin(x * 1.1f)) / 120f;
        data.SetHeights(0, 0, heights);
        Call("Utils", "InvalidateHeightCache");
        var masterType = source.GetType("MasterDataStore");
        var master = new GameObject("Master").AddComponent(masterType);
        masterType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, master);
        var itemType = source.GetType("PathMasterItem");
        var item = Activator.CreateInstance(itemType);
        itemType.GetField("width").SetValue(item, 8f);
        itemType.GetField("sidewalk_width").SetValue(item, 0f);
        itemType.GetField("side_width").SetValue(item, 0f);
        itemType.GetField("lanes").SetValue(item, 1);
        itemType.GetField("follow_terrain").SetValue(item, true);
        var dict = (IDictionary)masterType.GetField("pathMasterById", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(master);
        dict.Add(100, item);
        var modelType = source.GetType("ModelManager");
        var model = new GameObject("Models").AddComponent(modelType);
        modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, model);
        var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.transform.position = Vector3.down * 1000;
        var bodyMesh = UnityEngine.Object.Instantiate(body.GetComponent<MeshFilter>().sharedMesh);
        var vertices = bodyMesh.vertices;
        for (int i = 0; i < vertices.Length; i++) vertices[i].y = vertices[i].y * .1f - .05f;
        bodyMesh.vertices = vertices;
        bodyMesh.RecalculateBounds();
        body.GetComponent<MeshFilter>().sharedMesh = bodyMesh;
        cache.Add("Models/Paths/p100/body", body);
        var pathType = source.GetType("GeneratedModels.PathSummary");
        var path = Activator.CreateInstance(pathType);
        pathType.GetField("path_id").SetValue(path, 1);
        pathType.GetField("path_variant").SetValue(path, 100);
        pathType.GetField("width").SetValue(path, itemType.GetField("width").GetValue(item)); // Like the core, a path takes its variant's width when created.
        pathType.GetField("path_type").SetValue(path, 1);
        pathType.GetField("curved").SetValue(path, false);
        pathType.GetField("start_point").SetValue(path, new List<object> { -20f, 0f, 0f });
        pathType.GetField("end_point").SetValue(path, new List<object> { 20f, 0f, 0f });
        var points = new List<Vector3> { new Vector3(-20, Height(-20, 0) + .1f, 0), new Vector3(20, Height(20, 0) + .1f, 0) };
        var roadType = source.GetType("RoadPath");
        var setback = Activator.CreateInstance(source.GetType("SidewalkSetback"));
        var result = roadType.GetMethod("Generate").Invoke(Activator.CreateInstance(roadType), new object[] { path, points, setback, setback });
        var road = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        foreach (var comp in road.GetComponentsInChildren<Component>())
        {
            if (comp == null || comp.GetType().Name != "SplineMesh") continue;
            var rebuild = comp.GetType().GetMethod("RebuildImmediate");
            rebuild.Invoke(comp, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
        }
        var colliders = new List<MeshCollider>();
        foreach (var filter in road.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null || !filter.name.EndsWith("/body")) continue;
            var collider = filter.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = filter.sharedMesh;
            colliders.Add(collider);
        }
        Physics.SyncTransforms();
        float penetration = 0;
        int count = 0;
        var ground = terrainObject.GetComponent<TerrainCollider>();
        for (float x = -19.5f; x <= 19.5f; x += .125f)
            for (float z = -3.5f; z <= 3.5f; z += .5f)
            {
                var ray = new Ray(new Vector3(x, 100, z), Vector3.down);
                foreach (var collider in colliders)
                    if (collider.Raycast(ray, out var surface, 200) && ground.Raycast(ray, out var earth, 200))
                    {
                        count++;
                        penetration = Mathf.Max(penetration, earth.point.y - surface.point.y);
                    }
            }
        results.Add($"Road: {count} samples across full width, penetration {penetration:F6}m");
        if (count < 100 || penetration > .001f) throw new Exception("Terrain penetrates road");
    }
}
