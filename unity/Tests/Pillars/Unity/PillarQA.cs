using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public static class PillarQA
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static Assembly assembly;
    static Type pathType;
    static object manager;
    static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
    static object Call(string method, params object[] args) => manager.GetType().GetMethod(method, Private | BindingFlags.Public).Invoke(manager, args);
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static object Path(int id, Vector3 start, Vector3 end, int variant = 2)
    {
        var path = Activator.CreateInstance(pathType);
        Set(path, "path_id", id); Set(path, "path_variant", variant); Set(path, "path_type", 2);
        Set(path, "width", 8f); Set(path, "version", 1);
        Set(path, "start_point", new List<object> { start.x, start.y, start.z });
        Set(path, "end_point", new List<object> { end.x, end.y, end.z });
        return path;
    }
    static int Generate(object path)
    {
        var root = GenerateRoot(path, 2, 10);
        int count = root.GetComponentsInChildren<MeshFilter>().Length;
        UnityEngine.Object.DestroyImmediate(root);
        return count;
    }
    static GameObject GenerateRoot(object path, int variant, float height, bool construction = false, List<Vector3> points = null)
    {
        var root = new GameObject("Pillars");
        var road = assembly.GetType("RoadPath");
        var spline = road.GetMethod("BuildSpline").Invoke(null, new object[] { root,
            points ?? new List<Vector3> { new Vector3(-20, height, 0), new Vector3(20, height, 0) }, false });
        if (construction)
            road.GetMethod("GenerateConstructionPillars").Invoke(null, new object[] { root, spline, 20f, 0f, path,
                "Models/Paths/p" + variant, "Models/Paths/p4", (Action<GameObject, double>)((support, percent) =>
                    Check(support.GetComponentsInChildren<MeshFilter>().Length == 3, "Construction unit contains whole portal")) });
        else
            road.GetMethod("GeneratePillars").Invoke(null, new object[] { root, spline, 20f, 0f, path,
                "Models/Paths/p" + variant, "Models/Paths/p" + (variant == 2 ? 2 : 4), false, false });
        return root;
    }
    public static void Run()
    {
        string result = PathName("result.txt");
        try
        {
            var args = Environment.GetCommandLineArgs();
            string source = args[Array.IndexOf(args, "-pillarSourceRoot") + 1];
            AppDomain.CurrentDomain.AssemblyResolve += (o, e) =>
            {
                foreach (string dir in new[] { "/Temp/bin/Debug", "/Library/ScriptAssemblies", "/Assets/Plugins" })
                {
                    string file = source + dir + "/" + new AssemblyName(e.Name).Name + ".dll";
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            assembly = Assembly.LoadFrom(source + "/Temp/bin/Debug/Assembly-CSharp.dll");
            pathType = assembly.GetType("GeneratedModels.PathSummary");
            var masterType = assembly.GetType("MasterDataStore");
            var master = new GameObject("Master").AddComponent(masterType);
            masterType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, master);
            masterType.GetProperty("IsInitialized").SetValue(master, true);
            var item = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
            Set(item, "width", 8f); Set(item, "height", 5f); Set(item, "pillar_spacing", 20f); Set(item, "height_editable", true);
            ((IDictionary)masterType.GetField("pathMasterById", Private).GetValue(master)).Add(2, item);
            var models = new GameObject("Models").AddComponent(assembly.GetType("ModelManager"));
            models.GetType().GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, models);
            var cache = (IDictionary)models.GetType().GetField("_modelCache", Private).GetValue(models);
            foreach (string part in new[] { "pillar", "pillar_cap" })
            {
                var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                prefab.GetComponent<MeshRenderer>().enabled = false;
                var mesh = UnityEngine.Object.Instantiate(prefab.GetComponent<MeshFilter>().sharedMesh);
                var vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i].y -= 0.5f;
                mesh.vertices = vertices; mesh.RecalculateBounds(); prefab.GetComponent<MeshFilter>().sharedMesh = mesh;
                cache.Add("Models/Paths/p2/" + part, prefab);
            }
            manager = new GameObject("Paths").AddComponent(assembly.GetType("PathManager"));
            Call("Awake");
            var bridge = Path(1, new Vector3(-20, 10, 0), new Vector3(20, 10, 0));
            Call("SavePathToCache", bridge);
            Check(Generate(bridge) == 6, "Three supports before lower road");
            var lower = Path(2, new Vector3(0, 0, -30), new Vector3(0, 0, 30));
            Call("SavePathToCache", lower);
            var pending = (HashSet<int>)manager.GetType().GetField("pendingPillarRebuildIds", Private).GetValue(manager);
            Check(pending.Contains(1), "Adding lower road invalidates bridge");
            Check(Generate(bridge) == 4, "Only crossing support and cap disappear without road mesh");
            var revisions = (IDictionary)manager.GetType().GetField("geometryRevisions", Private).GetValue(manager);
            Check((int)revisions[1] > 0, "LOD cache revision changes");
            pending.Clear();
            var moved = Path(2, new Vector3(2000, 0, -30), new Vector3(2000, 0, 30));
            Set(moved, "version", 2); Call("SavePathToCache", moved);
            Check(pending.Contains(1), "Moving road invalidates old neighborhood");
            Check(Generate(bridge) == 6, "Support returns after move");
            pending.Clear(); Call("SavePathToCache", lower);
            Check(Generate(bridge) == 4, "Support disappears again");
            pending.Clear(); Call("RemovePathFromCache", 2);
            Check(pending.Contains(1), "Removal invalidates bridge");
            Check(Generate(bridge) == 6, "Support returns after removal");
            pending.Clear(); Call("SavePathToCache", bridge);
            Check(pending.Count == 0, "Unchanged data does not enqueue rebuilds");
            var curve = Path(3, new Vector3(2000, 0, -30), new Vector3(2000, 0, 30));
            Set(curve, "curved", true);
            Set(curve, "start_control", new List<object> { -2000f / 3f, 0f, -10f });
            Set(curve, "end_control", new List<object> { -2000f / 3f, 0f, 10f });
            Call("SavePathToCache", curve);
            Check(pending.Contains(1), "Curve bulging out of anchor cells invalidates bridge");
            Check(((IList)Call("GetPillarObstacles", bridge)).Count > 0, "Curve is found outside anchor cells");
            Check(Generate(bridge) < 6, "Curved crossing removes support");
            Call("RemovePathFromCache", 3);
            Call("RemovePathFromCache", 1);
            Call("SavePathToCache", lower); Call("SavePathToCache", bridge);
            Check(Generate(bridge) == 4, "Reversed registration order has same placement");
            Set(lower, "path_type", 4);
            Check(Generate(bridge) == 4, "Railway below bridge also removes support");
            var arcadeMaster = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
            Set(arcadeMaster, "width", 5f); Set(arcadeMaster, "height_editable", true); Set(arcadeMaster, "pillar_spacing", 0f);
            ((IDictionary)masterType.GetField("pathMasterById", Private).GetValue(master)).Add(404, arcadeMaster);
            cache.Add("Models/Paths/p404/arcade", GameObject.CreatePrimitive(PrimitiveType.Cube));
            var arcade = Path(4, new Vector3(-20, 10, 0), new Vector3(20, 10, 0), 404);
            Set(arcade, "path_type", 4); Call("SavePathToCache", arcade);
            pending.Clear(); Call("RemovePathFromCache", 2);
            Check(pending.Contains(4), "Road removal invalidates arcade with zero pillar spacing");
            pending.Clear(); Call("SavePathToCache", lower);
            Check(pending.Contains(4), "Road addition invalidates arcade");
            pending.Clear(); Call("SavePathToCache", moved);
            Check(pending.Contains(4), "Road move invalidates old arcade neighborhood");
            foreach (int id in new[] { 1, 2, 4 }) Call("RemovePathFromCache", id);
            PortalCases(master, masterType, cache);
            File.WriteAllText(result, "PASS: Unity pillar placement, add/move/remove, data-only neighbor, LOD invalidation, curve cells, registration order, railway; real FBX portal, road widths, low beam, sidewalk legs, median piers, span limit, terrain, construction, preview render");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(result, "FAIL: " + ex);
            Debug.LogException(ex); EditorApplication.Exit(1);
        }
    }

    static Bounds WorldBounds(MeshFilter filter)
    {
        var vertices = filter.sharedMesh.vertices;
        var bounds = new Bounds(filter.transform.TransformPoint(vertices[0]), Vector3.zero);
        foreach (var vertex in vertices) bounds.Encapsulate(filter.transform.TransformPoint(vertex));
        return bounds;
    }

    static void PortalCases(object master, Type masterType, IDictionary cache)
    {
        AssetDatabase.Refresh();
        ModelPrefabGenerator.SyncAll();
        foreach (string entry in new[] { "p4/pillar_leg", "p4/pillar_crossbeam", "p421/pillar", "p421/pillar_cap" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/Paths/" + entry + ".prefab");
            Check(prefab != null, "Imported prefab " + entry);
            var filter = prefab.GetComponentInChildren<MeshFilter>();
            Check(filter.sharedMesh.isReadable, "Readable FBX " + entry);
            Check(filter.sharedMesh.vertexCount <= 100, "Lightweight FBX " + entry);
            cache["Models/Paths/" + entry] = prefab;
        }
        var item = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
        Set(item, "width", 10f); Set(item, "height", 6f); Set(item, "pillar_spacing", 20f); Set(item, "height_editable", true);
        Set(item, "model_type", "track");
        ((IDictionary)masterType.GetField("pathMasterById", Private).GetValue(master)).Add(421, item);
        var bridge = Path(10, new Vector3(-20, 12, 0), new Vector3(20, 12, 0), 421);
        Set(bridge, "path_type", 4); Set(bridge, "width", 10f);
        Call("SavePathToCache", bridge);
        var before = GenerateRoot(bridge, 421, 12);
        Check(before.GetComponentsInChildren<MeshFilter>().Length == 6, "Ordinary piers without road");
        UnityEngine.Object.DestroyImmediate(before);
        foreach (float width in new[] { 8f, 24f, 32f })
        {
            var lower = Path(11, new Vector3(-60, 0, 0), new Vector3(60, 0, 0));
            Set(lower, "width", width); Set(lower, "version", (int)width);
            Call("SavePathToCache", lower);
            var root = GenerateRoot(bridge, 421, 12);
            var pieces = root.GetComponentsInChildren<MeshFilter>();
            Check(pieces.Length == 9, "Three portals above parallel road width " + width);
            var legs = CheckLegs(pieces, Call("GetPillarObstacles", bridge));
            Check(legs.Length == 6, "Two independent legs per portal");
            foreach (var leg in legs)
                Check(Mathf.Abs(WorldBounds(leg).size.x - 1.8f) < .005f, "Leg depth is not stretched with span");
            if (width == 24) RenderPortal(root);
            UnityEngine.Object.DestroyImmediate(root);
        }
        var construction = GenerateRoot(bridge, 421, 12, true);
        Check(construction.GetComponentsInChildren<MeshFilter>().Length == 9, "Construction has the same three portals");
        UnityEngine.Object.DestroyImmediate(construction);
        // The crossbeam does not check the clearance over the road below, so erect a portal frame even if the elevated road has low clearance beneath the beam.
        var low = GenerateRoot(bridge, 421, 6);
        var lowPieces = low.GetComponentsInChildren<MeshFilter>();
        Check(lowPieces.Length == 9 && CheckLegs(lowPieces, Call("GetPillarObstacles", bridge)).Length == 6, "Low railway still gets portals");
        UnityEngine.Object.DestroyImmediate(low);
        SidewalkAndMedianCases(bridge, master, masterType);
        var crossing = Path(11, new Vector3(0, 0, -100), new Vector3(0, 0, 100));
        Set(crossing, "version", 40); Call("SavePathToCache", crossing);
        var longSpan = GenerateRoot(bridge, 421, 12);
        Check(longSpan.GetComponentsInChildren<MeshFilter>().Length == 4, "Unbounded transverse corridor must not get a giant beam");
        UnityEngine.Object.DestroyImmediate(longSpan);
        Call("RemovePathFromCache", 11);
        var restored = GenerateRoot(bridge, 421, 12);
        Check(restored.GetComponentsInChildren<MeshFilter>().Length == 6, "Removing road restores ordinary piers");
        UnityEngine.Object.DestroyImmediate(restored);
        CheckUnevenGround(bridge);
        CheckRotatedPortals();
    }

    /// <summary>Collect only the portal-frame pillars and verify ground contact and that they are outside the band where pillars cannot be placed.</summary>
    static MeshFilter[] CheckLegs(MeshFilter[] pieces, object obstacles)
    {
        var legs = pieces.Where(f => f.sharedMesh.name.Contains("pillar_leg")).ToArray();
        foreach (var leg in legs)
        {
            Check(Mathf.Abs(WorldBounds(leg).min.y) < .005f, "Leg contacts ground");
            Check(!(bool)assembly.GetType("PillarClearance").GetMethod("Intersects").Invoke(null,
                new object[] { WorldBounds(leg), obstacles }), "Portal legs must clear every corridor");
        }
        return legs;
    }

    static void SidewalkAndMedianCases(object bridge, object master, Type masterType)
    {
        var masters = (IDictionary)masterType.GetField("pathMasterById", Private).GetValue(master);
        // Above a 12 m-wide road with 2.5 m sidewalks, the pillar stands on the sidewalk beyond the carriageway (3.5 m from the center), without moving outside the road.
        var street = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
        Set(street, "width", 12f); Set(street, "height", 5f); Set(street, "sidewalk_width", 2.5f);
        masters.Add(221, street);
        var lower = Path(11, new Vector3(-60, 0, 0), new Vector3(60, 0, 0), 221);
        Set(lower, "path_type", 1); Set(lower, "width", 12f); Set(lower, "version", 60); Call("SavePathToCache", lower);
        var root = GenerateRoot(bridge, 421, 12);
        var pieces = root.GetComponentsInChildren<MeshFilter>();
        var legs = CheckLegs(pieces, Call("GetPillarObstacles", bridge));
        Check(pieces.Length == 9 && legs.Length == 6, "Three portals above a street with sidewalks");
        foreach (var leg in legs)
        {
            float lateral = Mathf.Abs(WorldBounds(leg).center.z);
            Check(lateral > 3.5f && lateral < 6f, "Portal leg stands on the sidewalk: " + lateral);
        }
        UnityEngine.Object.DestroyImmediate(root);
        // Directly above the center of a road with a 10 m median, a standard pier stands in the median.
        var avenue = Activator.CreateInstance(assembly.GetType("PathMasterItem"));
        Set(avenue, "width", 40f); Set(avenue, "height", 5f); Set(avenue, "sidewalk_width", 3f); Set(avenue, "center_width", 10f);
        masters.Add(241, avenue);
        lower = Path(11, new Vector3(-60, 0, 0), new Vector3(60, 0, 0), 241);
        Set(lower, "path_type", 1); Set(lower, "width", 40f); Set(lower, "version", 61); Call("SavePathToCache", lower);
        root = GenerateRoot(bridge, 421, 12);
        pieces = root.GetComponentsInChildren<MeshFilter>();
        Check(pieces.Length == 6 && !pieces.Any(f => f.sharedMesh.name.Contains("pillar_leg")), "Ordinary piers stand in a wide median");
        UnityEngine.Object.DestroyImmediate(root);
    }

    static void CheckRotatedPortals()
    {
        for (int angle = 0; angle < 180; angle += 15)
        {
            Vector3 direction = Quaternion.Euler(0, angle, 0) * Vector3.right;
            Vector3 start = -direction * 20 + Vector3.up * 12, end = direction * 20 + Vector3.up * 12;
            var bridge = Path(10, start, end, 421);
            Set(bridge, "path_type", 4); Set(bridge, "width", 10f); Set(bridge, "version", 100 + angle);
            Call("SavePathToCache", bridge);
            var lower = Path(11, -direction * 60, direction * 60);
            Set(lower, "width", 24f); Set(lower, "version", 100 + angle); Call("SavePathToCache", lower);
            var root = GenerateRoot(bridge, 421, 12, points: new List<Vector3> { start, end });
            Check(root.GetComponentsInChildren<MeshFilter>().Length == 9, "Rotated railway gets three portals at " + angle);
            var obstacles = Call("GetPillarObstacles", bridge);
            foreach (var part in root.GetComponentsInChildren<MeshFilter>())
                Check(!(bool)assembly.GetType("PillarClearance").GetMethod("Intersects").Invoke(null,
                    new object[] { WorldBounds(part), obstacles }), "Rotated portal clearance at " + angle);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    static void CheckUnevenGround(object bridge)
    {
        var data = new TerrainData { heightmapResolution = 33, size = new Vector3(100, 10, 100) };
        var heights = new float[33, 33];
        for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = z / 160f;
        data.SetHeights(0, 0, heights);
        var terrain = Terrain.CreateTerrainGameObject(data);
        terrain.transform.position = new Vector3(-50, 0, -50);
        var lower = Path(11, new Vector3(-60, 0, 0), new Vector3(60, 0, 0));
        Set(lower, "width", 24f); Set(lower, "version", 50); Call("SavePathToCache", lower);
        assembly.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
        var root = GenerateRoot(bridge, 421, 12);
        var legs = root.GetComponentsInChildren<MeshFilter>().Where(f => f.sharedMesh.name.Contains("pillar_leg")).ToArray();
        Check(legs.Length == 6, "Portals above sloping ground");
        foreach (var leg in legs)
        {
            var bounds = WorldBounds(leg);
            float ground = terrain.GetComponent<Terrain>().SampleHeight(bounds.center);
            Check(Mathf.Abs(bounds.min.y - ground) < .01f, "Each leg independently contacts terrain");
        }
        Check(Mathf.Abs(WorldBounds(legs[0]).size.y - WorldBounds(legs[1]).size.y) > .1f, "Different ground heights give different leg heights");
        UnityEngine.Object.DestroyImmediate(root);
        UnityEngine.Object.DestroyImmediate(terrain);
        UnityEngine.Object.DestroyImmediate(data);
        assembly.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null, null);
    }

    static void RenderPortal(GameObject root)
    {
        var scene = new GameObject("Preview");
        var concrete = new Material(Shader.Find("Standard")) { color = new Color(.65f, .65f, .61f) };
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            renderer.sharedMaterial = concrete;
            renderer.enabled = true;
        }
        Action<Vector3, Vector3, Color> box = (position, scale, color) =>
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
            cube.transform.SetParent(scene.transform); cube.transform.position = position; cube.transform.localScale = scale;
            cube.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = color };
            cube.GetComponent<MeshRenderer>().enabled = true;
        };
        box(new Vector3(0, -.15f, 0), new Vector3(75, .2f, 65), new Color(.38f, .48f, .25f));
        box(new Vector3(0, -.01f, 0), new Vector3(70, .15f, 24), new Color(.17f, .18f, .19f));
        box(new Vector3(0, 11.4f, 0), new Vector3(45, 1.2f, 10), new Color(.61f, .61f, .58f));
        foreach (float z in new[] { -3.22f, -1.78f, 1.78f, 3.22f })
            box(new Vector3(0, 12.15f, z), new Vector3(45, .2f, .13f), new Color(.3f, .31f, .33f));
        for (int x = -32; x < 34; x += 6)
            foreach (float z in new[] { -6f, 0f, 6f })
                box(new Vector3(x, .08f, z), new Vector3(3, .02f, .12f), Color.white);
        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = 1.2f; light.transform.rotation = Quaternion.Euler(45, -35, 0);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.transform.position = new Vector3(48, 38, -48); camera.transform.LookAt(new Vector3(0, 5, 0));
        camera.orthographic = true; camera.orthographicSize = 31; camera.backgroundColor = new Color(.75f, .82f, .89f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        var target = new RenderTexture(1100, 800, 24); camera.targetTexture = target; camera.Render();
        RenderTexture.active = target;
        var image = new Texture2D(1100, 800, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 1100, 800), 0, 0); image.Apply();
        File.WriteAllBytes(PathName("portal-preview.png"), image.EncodeToPNG());
        RenderTexture.active = null; camera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(target);
        UnityEngine.Object.DestroyImmediate(camera.gameObject); UnityEngine.Object.DestroyImmediate(light.gameObject);
        UnityEngine.Object.DestroyImmediate(scene);
    }
    static string PathName(string file) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), file);
}
