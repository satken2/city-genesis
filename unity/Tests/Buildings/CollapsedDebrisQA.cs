using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Use the main DLL and the production materials in an isolated URP project to verify and render the debris of collapsed buildings.
[InitializeOnLoad]
public static class CollapsedDebrisQA
{
    const float FLOOR = 4f;
    static MethodInfo build, buildFar;
    static int checks;
    static string output;
    static readonly List<string> stats = new List<string>();

    static CollapsedDebrisQA()
    {
        if (SessionState.GetBool("CollapsedDebrisQA", false)) EditorApplication.delayCall += Start;
    }

    public static void Run()
    {
        SessionState.SetBool("CollapsedDebrisQA", true);
        GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        EditorApplication.EnterPlaymode();
    }

    static void Require(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }

    static void Start()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.delayCall += Start; return; }
        SessionState.SetBool("CollapsedDebrisQA", false);
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        output = root + "/Temp/CollapsedDebrisQA";
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
            var assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            build = assembly.GetType("CollapsedBuildingMeshBuilder").GetMethod("BuildDebrisMesh", BindingFlags.NonPublic | BindingFlags.Static);
            buildFar = assembly.GetType("CollapsedBuildingMeshBuilder").GetMethod("BuildFarPlate", BindingFlags.NonPublic | BindingFlags.Static);
            var invalidate = assembly.GetType("Utils").GetMethod("InvalidateHeightCache");
            Suite(invalidate);
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

    static Vector2[] Box(float w, float d) => new[] { new Vector2(-w / 2, -d / 2), new Vector2(w / 2, -d / 2), new Vector2(w / 2, d / 2), new Vector2(-w / 2, d / 2) };

    static Mesh Build(Vector2[] footprint, float height, int seed, Vector3 center, bool followTerrain, out float heap, out double ms, bool columns = false)
    {
        var args = new object[] { footprint, height, FLOOR, seed, center, followTerrain, columns, 0f };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var mesh = (Mesh)build.Invoke(null, args);
        ms = watch.Elapsed.TotalMilliseconds;
        heap = (float)args[7];
        return mesh;
    }

    // Horizontal reach of the debris above ground beyond the footprint's bounding box (m).
    static float Overhang(Mesh mesh, Vector2[] footprint)
    {
        float minX = footprint.Min(p => p.x), maxX = footprint.Max(p => p.x), minZ = footprint.Min(p => p.y), maxZ = footprint.Max(p => p.y);
        float reach = 0f;
        foreach (var v in mesh.vertices)
        {
            if (v.y <= 0.05f) continue;
            reach = Mathf.Max(reach, minX - v.x, v.x - maxX, minZ - v.z, v.z - maxZ);
        }
        return reach;
    }

    static void Suite(MethodInfo invalidate)
    {
        var rubble = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/CollapsedRubble.mat");
        var sand = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/CollapsedSand.mat");
        var rubbleBurned = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/CollapsedRubbleBurned.mat");
        var sandBurned = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/CollapsedSandBurned.mat");
        Require(rubble != null && sand != null && rubbleBurned != null && sandBurned != null, "Missing debris materials");
        Require(rubble.GetTexture("_BaseMap") != null && rubble.GetTexture("_BumpMap") != null && rubble.IsKeywordEnabled("_NORMALMAP"), "Rubble textures not bound");
        Require(sand.GetTexture("_BaseMap") != null && sand.GetTexture("_BumpMap") != null && sand.IsKeywordEnabled("_NORMALMAP"), "Sand textures not bound");
        var rebar = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/CollapsedRebar.mat");
        Require(rebar != null, "Missing rebar material");

        var lit = Shader.Find("Universal Render Pipeline/Lit");
        Material Flat(Color c) { var m = new Material(lit); m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", 0.1f); return m; }
        var grass = Flat(new Color(0.30f, 0.36f, 0.25f));
        var asphalt = Flat(new Color(0.16f, 0.16f, 0.17f));
        var walls = Flat(new Color(0.72f, 0.70f, 0.66f));
        // The first render of the session can draw with materials that are not ready yet.
        Capture("warmup", Vector3.zero, 10f, 30f, 0f);

        // Same footprint, rising height: the heap must grow higher and wider.
        var block = Box(20, 20);
        float lastHeap = 0f, lastReach = 0f;
        foreach (int floors in new[] { 1, 3, 10, 30 })
        {
            var mesh = Build(block, floors * FLOOR, 7, Vector3.zero, false, out float heap, out double ms);
            float reach = Overhang(mesh, block);
            stats.Add($"20x20m {floors}F: heap={heap:F2}m reach={reach:F2}m verts={mesh.vertexCount} tris={mesh.triangles.Length / 3} {ms:F2}ms");
            Require(heap > lastHeap && reach > lastReach, $"Debris does not grow with height at {floors}F");
            Require(mesh.vertexCount <= 2 * 65 * 65, "Vertex budget exceeded");
            foreach (var v in mesh.vertices) Require(!float.IsNaN(v.y) && !float.IsInfinity(v.y), "Non finite vertex");
            lastHeap = heap; lastReach = reach;
            UnityEngine.Object.DestroyImmediate(mesh);
        }
        var a = Build(block, 40, 11, Vector3.zero, false, out _, out _);
        var b = Build(block, 40, 11, Vector3.zero, false, out _, out _);
        Require(a.vertices.SequenceEqual(b.vertices) && a.triangles.SequenceEqual(b.triangles), "Non deterministic layout");
        Require(a.subMeshCount == 3 && a.GetTriangles(0).Length > 0 && a.GetTriangles(1).Length > 0, "Rubble and sand submeshes");
        Require(a.GetTriangles(2).Length == 0, "Broken columns outside the close-up LOD");
        Require(a.tangents.Length == a.vertexCount, "Missing tangents for the normal map");

        // Close-up: broken columns stand inside the footprint, below the building's height, and add little geometry.
        var close = Build(block, 40, 11, Vector3.zero, false, out _, out double closeMs, columns: true);
        var closeAgain = Build(block, 40, 11, Vector3.zero, false, out _, out _, columns: true);
        Require(close.vertices.SequenceEqual(closeAgain.vertices), "Non deterministic columns");
        Require(close.GetTriangles(2).Length > 0, "No broken columns in the close-up LOD");
        int added = close.vertexCount - a.vertexCount;
        Require(added > 0 && added < 3000, $"Broken columns add {added} vertices");
        var closeVertices = close.vertices;
        var columnIndices = close.GetTriangles(1).Concat(close.GetTriangles(2)).Where(i => i >= a.vertexCount).Distinct().ToArray();
        foreach (int i in columnIndices)
        {
            var v = closeVertices[i];
            Require(Mathf.Abs(v.x) < 10f + 2.5f && Mathf.Abs(v.z) < 10f + 2.5f && v.y < 40f + 1.5f && v.y > -0.5f, $"Column vertex out of place ({v.x:F1},{v.y:F1},{v.z:F1})");
        }
        stats.Add($"close-up 20x20m 10F: +{added} verts for broken columns, {closeMs:F2}ms");
        UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
        UnityEngine.Object.DestroyImmediate(close); UnityEngine.Object.DestroyImmediate(closeAgain);

        // Scenes: (name, footprint, parcel, floors, burned)
        var lshape = new[] { new Vector2(-14, -12), new Vector2(14, -12), new Vector2(14, 0), new Vector2(0, 0), new Vector2(0, 12), new Vector2(-14, 12) };
        var scenes = new (string name, Vector2[] footprint, Vector2[] parcel, int floors, bool burned)[]
        {
            ("house-2F", Box(10, 12), Box(16, 20), 2, false),
            ("full-parcel-3F", Box(20, 20), Box(20, 20), 3, false),
            ("full-parcel-10F", Box(20, 20), Box(20, 20), 10, false),
            ("full-parcel-10F-burned", Box(20, 20), Box(20, 20), 10, true),
            ("tower-30F", Box(30, 30), Box(36, 36), 30, false),
            ("warehouse-1F", Box(50, 40), Box(54, 44), 1, false),
            ("l-shape-5F", lshape, Box(30, 26), 5, false),
        };
        foreach (var s in scenes)
        {
            var mesh = Build(s.footprint, s.floors * FLOOR, s.name.GetHashCode(), Vector3.zero, false, out float heap, out double ms);
            stats.Add($"{s.name}: heap={heap:F2}m reach={Overhang(mesh, s.footprint):F2}m verts={mesh.vertexCount} {ms:F2}ms");
            var objects = new List<GameObject>();
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane); ground.transform.localScale = Vector3.one * 40;
            ground.GetComponent<Renderer>().sharedMaterial = grass; objects.Add(ground);
            float pMinX = s.parcel.Min(p => p.x), pMaxX = s.parcel.Max(p => p.x), pMinZ = s.parcel.Min(p => p.y), pMaxZ = s.parcel.Max(p => p.y);
            var road = GameObject.CreatePrimitive(PrimitiveType.Cube);
            road.transform.position = new Vector3(0, 0.1f, pMinZ - 6); road.transform.localScale = new Vector3(200, 0.2f, 12);
            road.GetComponent<Renderer>().sharedMaterial = asphalt; objects.Add(road);
            foreach (float side in new[] { -1f, 1f })
            {
                var neighbor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                float x = side < 0 ? pMinX - 2 - 7 : pMaxX + 2 + 7;
                neighbor.transform.position = new Vector3(x, 6, (pMinZ + pMaxZ) / 2); neighbor.transform.localScale = new Vector3(14, 12, pMaxZ - pMinZ);
                neighbor.GetComponent<Renderer>().sharedMaterial = walls; objects.Add(neighbor);
            }
            var debris = new GameObject("Debris");
            debris.AddComponent<MeshFilter>().sharedMesh = mesh;
            debris.AddComponent<MeshRenderer>().sharedMaterials = s.burned ? new[] { rubbleBurned, sandBurned, rebar } : new[] { rubble, sand, rebar };
            objects.Add(debris);
            float radius = mesh.bounds.extents.magnitude;
            Capture(s.name, mesh.bounds.center, radius, 38f, -35f);
            // Far: one plate over the debris area, compared with the heap from the same distant viewpoint.
            var plate = (Mesh)buildFar.Invoke(null, new object[] { s.footprint, s.floors * FLOOR, Vector3.zero, false });
            Require(plate.vertexCount == 4, "Far plate is not one quad");
            float plateReach = Overhang(plate, s.footprint);
            Require(plateReach > 0f && Mathf.Abs(plateReach - heap / Mathf.Tan(38f * Mathf.Deg2Rad)) < 0.01f, $"Far plate does not cover the spread: {plateReach:F2}m");
            if (s.name == "tower-30F" || s.name == "full-parcel-10F")
            {
                Capture(s.name + "-far-heap", mesh.bounds.center, radius * 8f, 30f, -35f);
                var heapMaterials = debris.GetComponent<MeshRenderer>().sharedMaterials;
                debris.GetComponent<MeshFilter>().sharedMesh = plate;
                debris.GetComponent<MeshRenderer>().sharedMaterials = new[] { heapMaterials[0] };
                Capture(s.name + "-far-plate", mesh.bounds.center, radius * 8f, 30f, -35f);
                debris.GetComponent<MeshFilter>().sharedMesh = mesh;
                debris.GetComponent<MeshRenderer>().sharedMaterials = heapMaterials;
            }
            UnityEngine.Object.DestroyImmediate(plate);
            var closeMesh = Build(s.footprint, s.floors * FLOOR, s.name.GetHashCode(), Vector3.zero, false, out _, out _, columns: true);
            debris.GetComponent<MeshFilter>().sharedMesh = closeMesh;
            Capture(s.name + "-close", mesh.bounds.center, Mathf.Min(radius, 14f) * 0.6f, 30f, 20f);
            foreach (var o in objects) UnityEngine.Object.DestroyImmediate(o);
            UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(closeMesh);
        }

        // Slope: debris sits on the terrain at every vertex rather than on the center's height.
        var terrainData = new TerrainData { heightmapResolution = 129, size = new Vector3(200, 40, 200) };
        var heights = new float[129, 129];
        for (int y = 0; y < 129; y++) for (int x = 0; x < 129; x++) heights[y, x] = 0.2f + 0.5f * x / 128f;
        terrainData.SetHeights(0, 0, heights);
        var terrainObject = Terrain.CreateTerrainGameObject(terrainData); terrainObject.transform.position = new Vector3(-100, 0, -100);
        var terrain = terrainObject.GetComponent<Terrain>();
        invalidate.Invoke(null, null);
        Vector3 center = new Vector3(0, terrain.SampleHeight(Vector3.zero), 0);
        var sloped = Build(Box(30, 30), 30 * FLOOR, 5, center, true, out float slopeHeap, out double slopeMs);
        stats.Add($"tower-30F on 10% slope: heap={slopeHeap:F2}m verts={sloped.vertexCount} {slopeMs:F2}ms (with terrain sampling)");
        foreach (var v in sloped.vertices)
        {
            float above = v.y + center.y - terrain.SampleHeight(new Vector3(v.x, 0, v.z));
            Require(above >= -0.31f && above <= slopeHeap * 1.2f + 3f, $"Vertex off the terrain: {above:F2}m at ({v.x:F1},{v.z:F1})");
        }
        var slopeObject = new GameObject("Debris");
        slopeObject.transform.position = center;
        slopeObject.AddComponent<MeshFilter>().sharedMesh = sloped;
        slopeObject.AddComponent<MeshRenderer>().sharedMaterials = new[] { rubble, sand, rebar };
        Capture("tower-30F-slope", center + sloped.bounds.center, sloped.bounds.extents.magnitude, 25f, 70f);
        UnityEngine.Object.DestroyImmediate(slopeObject); UnityEngine.Object.DestroyImmediate(sloped);
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
        var rt = new RenderTexture(1280, 800, 24);
        camera.targetTexture = rt; camera.Render();
        RenderTexture.active = rt;
        var image = new Texture2D(1280, 800, TextureFormat.RGB24, false);
        image.ReadPixels(new UnityEngine.Rect(0, 0, 1280, 800), 0, 0); image.Apply();
        File.WriteAllBytes(output + "/Captures/" + name + ".png", image.EncodeToPNG());
        RenderTexture.active = null; camera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(camera.gameObject); UnityEngine.Object.DestroyImmediate(sun.gameObject);
    }
}
