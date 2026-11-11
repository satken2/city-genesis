using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Use the main DLL, the mine's actual FBXs, and the URP in an isolated project to verify and render an open-pit forecourt on sloped ground:
// loose wings (heaps, parked machines) leave the placement grid while structures stay on it, and the textured ground panel follows the terrain.
[InitializeOnLoad]
public static class MineYardQA
{
    const int VARIANT = 1022;
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
        foreach (string path in Directory.GetFiles("Assets/Models/Buildings/a1022", "*.fbx", SearchOption.AllDirectories))
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
        SessionState.SetBool("MineYardQA", false);
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        output = root + "/Temp/MineYardQA";
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

    // The mine's rows of the sub-building master: (id, model_prefix, width, height in cells, height per floor, weight, min count, max count, loose).
    static readonly (int id, string prefix, float width, float height, float story, float weight, int? min, int? max, bool loose)[] Wings =
    {
        (48, "crusher", 3, 2, 16, 1, 1, 3, false),
        (49, "office", 2, 1, 4, 1, 1, 1, false),
        (50, "stockpile", 2, 2, 8, 3, 1, null, true),
        (51, "rom", 2, 2, 6, 1, null, null, true),
        (52, "machinery", 2, 1, 4, 1, null, null, true),
        (57, "workshop", 3, 2, 12, 1, 1, 2, false),
    };

    static IDictionary InstallMaster(bool loose)
    {
        var masterType = assembly.GetType("MasterDataStore");
        var master = UnityEngine.Object.FindAnyObjectByType(masterType) ?? new GameObject("Master").AddComponent(masterType);
        masterType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, master);
        masterType.GetProperty("IsInitialized").GetSetMethod(true).Invoke(master, new object[] { true });
        var itemType = masterType.GetNestedType("SubBuildingMasterItem");
        var table = (IDictionary)masterType.GetField("subBuildingMasterById", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(master);
        table.Clear();
        foreach (var wing in Wings)
        {
            object item = Activator.CreateInstance(itemType);
            void Set(string name, object value) => itemType.GetField(name).SetValue(item, value);
            Set("agent_variant", VARIANT); Set("display_name", wing.prefix); Set("model_prefix", wing.prefix);
            Set("width", (float?)wing.width); Set("height", (float?)wing.height);
            Set("max_floors", (int?)1); Set("min_floors", (int?)1); Set("height_per_floor", (float?)wing.story);
            Set("weight", (float?)wing.weight); Set("min_count", wing.min); Set("max_count", wing.max);
            Set("loose", loose && wing.loose);
            table.Add(wing.id, item);
        }
        return table;
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
            if (source == null) return null;
            if (!converted.TryGetValue(source, out Material material)) converted[source] = material = new Material(source) { shader = lit };
            return material;
        }
        var models = new Dictionary<string, GameObject>();
        foreach (var wing in Wings)
        {
            models[wing.prefix] = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Models/Buildings/a1022/0/mashup-{wing.prefix}-mid-prebuilt.fbx");
            Require(models[wing.prefix] != null, "Missing model " + wing.prefix);
        }
        var floorModel = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Buildings/a1022/0/floor.fbx");
        Material floorSource = floorModel.GetComponentInChildren<Renderer>().sharedMaterial;
        Require(floorSource != null && floorSource.name == "BD_GROUND_ORE", "Floor is not remapped to the ground material: " + floorSource?.name);
        Require(floorSource.shader.name == "Universal Render Pipeline/Lit" && floorSource.GetTexture("_BaseMap") != null,
            "Ground material is not a textured URP Lit material");
        // The pad under the parked machines carries model UVs (2m cube projection), so it takes the same texture at a matching scale.
        Material pad = models["machinery"].GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m != null && m.name == "BD_GROUND_ORE_PART");
        float panelDensity = (float)assembly.GetType("ModularBodyBuilder").GetField("GROUND_UV_SCALE").GetRawConstantValue();
        Require(pad != null && pad.GetTexture("_BaseMap") == floorSource.GetTexture("_BaseMap")
            && Mathf.Approximately(pad.GetTextureScale("_BaseMap").x * 0.5f, floorSource.GetTextureScale("_BaseMap").x * panelDensity),
            "Machinery pad does not match the ground panel");

        // Snowy ground rising 8% away from the road, with gentle swells.
        const int res = 513;
        var data = new TerrainData { heightmapResolution = res, size = new Vector3(500, 200, 500) };
        var heights = new float[res, res];
        float cell = 500f / (res - 1);
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                float wx = x * cell, wz = z * cell;
                heights[z, x] = (60 + .08f * wz + 2.5f * Mathf.Sin(wx / 37f) * Mathf.Cos(wz / 49f)) / 200f;
            }
        data.SetHeights(0, 0, heights);
        var terrainObject = Terrain.CreateTerrainGameObject(data);
        terrainObject.transform.position = new Vector3(-250, 0, -250);
        var terrain = terrainObject.GetComponent<Terrain>();
        terrain.drawInstanced = false;
        terrain.heightmapPixelError = (float)assembly.GetType("LocalConstants").GetField("TERRAIN_HEIGHTMAP_PIXEL_ERROR").GetRawConstantValue();
        var snow = new Material(terrainShader);
        snow.SetColor("_BaseColor", new Color(.86f, .87f, .88f));
        snow.SetFloat("_Smoothness", .1f);
        terrain.materialTemplate = snow;
        Physics.SyncTransforms();
        assembly.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
        Capture("warmup", Vector3.zero, 10f, 30f, 0f);

        var parcels = new (string name, Vector2[] polygon)[]
        {
            ("rectangle", new[] { new Vector2(-60, -56), new Vector2(60, -56), new Vector2(60, 56), new Vector2(-60, 56) }),
            ("wedge", new[] { new Vector2(-95, -50), new Vector2(75, -62), new Vector2(82, 20), new Vector2(40, 78) }),
            ("wide", new[] { new Vector2(-110, -70), new Vector2(110, -70), new Vector2(110, 70), new Vector2(-110, 70) }),
        };
        var layout = assembly.GetType("TestSubBuildingLayout").GetMethod("Generate");
        var resolve = assembly.GetType("MashupBuildingMeshBuilder").GetMethod("ResolveDrawnPlacement");
        var stack = assembly.GetType("BuildingMeshUtils").GetMethod("AddStackedModule");
        var floor = assembly.GetType("ModularBodyBuilder").GetMethod("GenerateTerrainConformingFloorMesh");
        float tolerance = (float)assembly.GetType("ModularBodyBuilder").GetField("GROUND_PANEL_TOLERANCE").GetRawConstantValue();
        var ground = assembly.GetType("Utils").GetMethod("GetGroundHeight");
        float Ground(float x, float z) => (float)ground.Invoke(null, new object[] { new Vector3(x, 1000f, z) });

        int agentId = 4100;
        foreach (var parcel in parcels)
        {
            agentId += 17;
            var center = new Vector3(0, Ground(0, 0), 0);
            InstallMaster(false);
            var placements = ((IEnumerable)layout.Invoke(null, new object[] { VARIANT, parcel.polygon, 1, true })).Cast<object>().ToList();
            Require(placements.Count >= 4, parcel.name + ": too few wings " + placements.Count);
            var panel = (Mesh)floor.Invoke(null, new object[] { parcel.polygon, center, tolerance });
            Require(panel != null && panel.uv.Length == panel.vertexCount, parcel.name + ": ground panel without UVs");

            var drawn = new Dictionary<bool, List<(Vector2 position, float rotation)>>();
            foreach (bool loose in new[] { false, true })
            {
                var table = InstallMaster(loose);
                var instances = new List<CombineInstance>();
                var materials = new List<Material>();
                var owned = new List<Mesh>();
                var list = new List<(Vector2, float)>();
                int moved = 0;
                foreach (object placement in placements)
                {
                    Type type = placement.GetType();
                    var planned = (float[])type.GetField("position").GetValue(placement);
                    float plannedRotation = (float)type.GetField("rotation").GetValue(placement);
                    int id = (int)type.GetField("subbuilding_id").GetValue(placement);
                    var call = new object[] { placement, agentId, null, null };
                    resolve.Invoke(null, call);
                    var again = new object[] { placement, agentId, null, null };
                    resolve.Invoke(null, again);
                    Vector2 position = (Vector2)call[2];
                    float rotation = (float)call[3];
                    Require(position == (Vector2)again[2] && rotation == (float)again[3], "Loose placement is not deterministic");
                    bool isLoose = loose && Wings.First(w => w.id == id).loose;
                    Vector2 shift = position - new Vector2(planned[0], planned[1]);
                    if (isLoose)
                    {
                        Require(Mathf.Abs(shift.x) <= 1.001f && Mathf.Abs(shift.y) <= 1.001f, "Loose wing moved too far: " + shift);
                        if (shift.sqrMagnitude > 1e-6f || Mathf.Abs(Mathf.DeltaAngle(rotation, plannedRotation)) > .01f) moved++;
                    }
                    else Require(shift == Vector2.zero && rotation == plannedRotation, "A wing that is not loose left its planned placement");
                    list.Add((position, rotation));

                    float baseY = Ground(center.x + position.x, center.z + position.y) - center.y;
                    string prefix = (string)type.GetField("model_prefix").GetValue(placement);
                    stack.Invoke(null, new object[] { models[prefix], baseY, null, instances, materials, owned, position,
                        (Quaternion?)Quaternion.Euler(0, -rotation + 180f, 0), true });
                }
                drawn[loose] = list;
                int looseCount = placements.Count(p => Wings.First(w => w.id == (int)p.GetType().GetField("subbuilding_id").GetValue(p)).loose);
                if (loose) Require(looseCount > 0 && moved == looseCount, $"{parcel.name}: {moved}/{looseCount} loose wings left the grid");

                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(instances.ToArray(), false, true);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var go = new GameObject("Mine");
                go.transform.position = center;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = materials.Select(Offset).ToArray();
                // Like the deferred exterior of the game: the ground panel is its own object with the part's own material,
                // keeps its own normals, and casts no shadow.
                var exterior = new GameObject("BuildingExterior");
                exterior.transform.SetParent(go.transform, false);
                exterior.AddComponent<MeshFilter>().sharedMesh = panel;
                var exteriorRenderer = exterior.AddComponent<MeshRenderer>();
                exteriorRenderer.sharedMaterial = floorSource;
                exteriorRenderer.shadowCastingMode = ShadowCastingMode.Off;
                string suffix = loose ? "loose" : "grid";
                float radius = panel.bounds.extents.magnitude;
                Capture($"{parcel.name}-{suffix}", center + new Vector3(0, 0, -20), radius * .62f, 38f, 20f);
                Capture($"{parcel.name}-{suffix}-low", center + new Vector3(0, 0, -30), radius * .45f, 14f, -35f);
                if (loose) Capture($"{parcel.name}-{suffix}-near", center + new Vector3(list[list.Count - 1].Item1.x, 0, list[list.Count - 1].Item1.y), 16f, 22f, 60f);
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                foreach (Mesh m in owned) UnityEngine.Object.DestroyImmediate(m);
            }
            stats.Add($"{parcel.name}: {placements.Count} wings, ground panel {panel.vertexCount} vertices");
            UnityEngine.Object.DestroyImmediate(panel);
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
