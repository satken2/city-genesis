using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Use the main assembly and actual FBXs to check intersection signal placement eligibility, positioning, ground contact, and combined meshes, then output images.
public static class TrafficSignalsQA
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const int Node = 1;
    const string Style = "Models/Props/traffic_signal_jp_";
    static Assembly asm;
    static Type pathType, itemType;
    static IDictionary masters, models;
    static string root, output;
    static int checks;
    static readonly List<string> report = new List<string>();
    static readonly Dictionary<int, List<Vector3>> points = new Dictionary<int, List<Vector3>>();
    static readonly Dictionary<string, int> partVertices = new Dictionary<string, int>();
    static float headBottom;

    // Cross-sections from the actual master (path_master): {width, number of lanes, sidewalk width, edge-line width, median width, wall width, type}
    static readonly Dictionary<int, float[]> sections = new Dictionary<int, float[]>
    {
        [102] = new[] { 5.5f, 1, 0, .5f, 0, .2f, 2 },
        [211] = new[] { 3f, 1, 0, .5f, 0, 0, 1 },
        [212] = new[] { 6f, 1, 0, .5f, 0, 0, 1 },
        [221] = new[] { 12f, 2, 2.5f, .5f, 0, 0, 1 },
        [231] = new[] { 24f, 4, 3, .5f, 0, 0, 1 },
        [241] = new[] { 32f, 6, 3, .5f, 3, 0, 1 },
    };

    // Expected lateral distance from the outgoing road centerline to the signal head (for two-way roads, the center of the outgoing lane group; for a one-lane two-way road, the center of the half used by traffic).
    static readonly Dictionary<int, float> headOffsets = new Dictionary<int, float>
    {
        [212] = (3f - .25f) * .5f,
        [221] = (6f - 2.5f - .25f) * .5f,
        [231] = (12f - 3f - .25f) * .5f,
        [241] = (16f - 3f - .25f + 1.5f) * .5f,
    };

    sealed class Arm
    {
        public int Id, Variant, Direction, Status = 1;
        public float Angle;
        public bool EndsAtNode;
        public Vector3 Outward => new Vector3(Mathf.Cos(Angle * Mathf.Deg2Rad), 0f, Mathf.Sin(Angle * Mathf.Deg2Rad));
    }

    static Arm A(int id, int variant, float angle, int direction = 0, bool endsAtNode = false, int status = 1)
        => new Arm { Id = id, Variant = variant, Angle = angle, Direction = direction, EndsAtNode = endsAtNode, Status = status };

    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Any).SetValue(target, value);
    static object Get(object target, string name)
    {
        var type = target.GetType();
        var field = type.GetField(name, Any);
        return field != null ? field.GetValue(target) : type.GetProperty(name, Any).GetValue(target);
    }
    static List<Vector3> Samples<T>(T p) => points[(int)p.GetType().GetField("path_id").GetValue(p)];

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        root = args[Array.IndexOf(args, "-signalsSourceRoot") + 1];
        output = root + "/Temp/TrafficSignalsQA";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string file = Path.Combine(root, dir, name);
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            asm = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            pathType = asm.GetType("GeneratedModels.PathSummary");
            itemType = asm.GetType("PathMasterItem");
            masters = CreateManager("MasterDataStore", "pathMasterById");
            models = CreateManager("ModelManager", "_modelCache");
            ModelImportSettings.EnableReadWriteAll();
            ModelPrefabGenerator.SyncAll();
            CheckModels();
            foreach (string file in Directory.GetFiles("Assets/Paths", "*.fbx", SearchOption.AllDirectories))
            {
                string asset = file.Replace('\\', '/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                models["Models/Paths/" + asset.Substring("Assets/Paths/".Length).Replace(".fbx", "")] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            }
            foreach (var pair in sections)
            {
                var item = Activator.CreateInstance(itemType);
                Set(item, "width", pair.Value[0]); Set(item, "lanes", (int?)(int)pair.Value[1]); Set(item, "sidewalk_width", (float?)pair.Value[2]);
                Set(item, "side_width", (float?)pair.Value[3]); Set(item, "center_width", (float?)pair.Value[4]); Set(item, "wall_width", (float?)pair.Value[5]);
                Set(item, "type", (int)pair.Value[6]); Set(item, "ramp", pair.Key == 102);
                masters[pair.Key] = item;
            }
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground"; ground.transform.localScale = new Vector3(30f, 1f, 30f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = Flat(new Color(.33f, .36f, .3f));

            Scenario("cross-221", new[] { A(1, 221, 0), A(2, 221, 90), A(3, 221, 180), A(4, 221, 270) }, 4, 4, "cross");
            Scenario("tee-221", new[] { A(1, 221, 0), A(2, 221, 180), A(3, 221, 270) }, 3, 3, "tee");
            Scenario("cross-221x212", new[] { A(1, 221, 0), A(2, 212, 90), A(3, 221, 180), A(4, 212, 270) }, 4, 4, "alley-cross");
            Scenario("tee-221x211", new[] { A(1, 221, 0), A(2, 221, 180), A(3, 211, 90) }, 3, 3);
            Scenario("wide-231x241", new[] { A(1, 231, 0), A(2, 241, 90), A(3, 231, 180), A(4, 241, 270) }, 4, 4, "wide");
            Scenario("skew-221", new[] { A(1, 221, 10), A(2, 221, 75), A(3, 221, 190), A(4, 221, 290) }, 4, 4, "skew");
            Scenario("ramp-221", new[] { A(1, 221, 0), A(2, 221, 180), A(3, 102, 270, 1, true) }, 3, 3);
            Scenario("oneway-exit", new[] { A(1, 221, 0), A(2, 221, 90, 1), A(3, 221, 180), A(4, 221, 270, 1, true) }, 3, 3);
            Scenario("under-construction", new[] { A(1, 221, 0), A(2, 221, 90), A(3, 221, 180, status: 2), A(4, 212, 270) }, 3, 3);
            // Intersections without signals: two residential roads; one road with a centerline and the rest narrow streets; a straight connection; or a road split into two by construction.
            Scenario("alleys-212", new[] { A(1, 212, 0), A(2, 212, 90), A(3, 212, 180), A(4, 212, 270) }, 0, 0);
            Scenario("dead-end-221", new[] { A(1, 221, 0), A(2, 212, 90), A(3, 211, 180), A(4, 212, 270) }, 0, 0);
            Scenario("joint-221", new[] { A(1, 221, 0), A(2, 221, 180) }, 0, 0);
            Scenario("one-centerline-left", new[] { A(1, 221, 0), A(2, 212, 90), A(3, 221, 180, status: 2) }, 0, 0);
            Scenario("oneway-221", new[] { A(1, 221, 0, 1), A(2, 221, 90, 1), A(3, 221, 180, 1), A(4, 221, 270, 1) }, 0, 0);
            SetTrafficHand(1);
            Scenario("cross-221-right", new[] { A(1, 221, 0), A(2, 221, 90), A(3, 221, 180), A(4, 221, 270) }, 4, 4, "cross-right");
            Scenario("tee-221-right", new[] { A(1, 221, 0), A(2, 221, 180), A(3, 221, 270) }, 3, 3);
            SetTrafficHand(0);
            File.WriteAllText(output + "/result.txt", "PASS " + checks + " checks: " + string.Join(", ", report));
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(output + "/result.txt", e.ToString());
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static IDictionary CreateManager(string name, string field)
    {
        Type type = asm.GetType(name);
        var component = new GameObject(name).AddComponent(type);
        type.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, component);
        return (IDictionary)type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component);
    }

    static void SetTrafficHand(int hand) => asm.GetType("CityInfoStore").GetProperty("TrafficHand").GetSetMethod(true).Invoke(null, new object[] { hand });

    static Material Flat(Color color)
    {
        var material = new Material(Shader.Find("Standard"));
        material.color = color;
        return material;
    }

    /// <summary>Check the origin, dimensions, and orientation of the actual FBX after import, and the automatically generated Resources wrapper.</summary>
    static void CheckModels()
    {
        foreach (string part in new[] { "pole", "arm", "head" })
        {
            Check(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/Props/traffic_signal_jp_" + part + ".prefab") != null, "missing generated wrapper " + part);
            var prefab = Resources.Load<GameObject>(Style + part);
            Check(prefab != null, "wrapper must load from Resources: " + part);
            var filter = prefab.GetComponentInChildren<MeshFilter>();
            Check(filter != null && filter.sharedMesh.isReadable, part + " mesh must be readable");
            var mesh = filter.sharedMesh;
            var bounds = ModelBounds(prefab);
            partVertices[part] = prefab.GetComponentsInChildren<MeshFilter>().Sum(f => f.sharedMesh.vertexCount);
            if (part == "pole")
                Check(Mathf.Abs(bounds.min.y + 1f) < .01f && Mathf.Abs(bounds.center.x) < .01f && Mathf.Abs(bounds.center.z) < .01f && bounds.max.y > 6.3f,
                    "pole must stand on its origin axis with 1m underground: " + bounds);
            if (part == "arm")
                Check(Mathf.Abs(bounds.min.z + .5f) < .005f && Mathf.Abs(bounds.max.z - .5f) < .005f && Mathf.Abs(bounds.center.x) < .01f,
                    "arm must span unit length along local Z: " + bounds);
            if (part == "head")
            {
                headBottom = bounds.min.y;
                Check(bounds.min.y >= 4.5f && Mathf.Abs(bounds.center.x) < .01f, "head clearance/origin: " + bounds);
                var materials = filter.GetComponent<MeshRenderer>().sharedMaterials;
                float Lens(string name)
                {
                    int index = Array.FindIndex(materials, m => m != null && m.name.StartsWith(name));
                    Check(index >= 0, "missing lens " + name);
                    var indices = mesh.GetTriangles(index);
                    Check(indices.All(i => mesh.normals[i].z > .99f), name + " must face local +Z");
                    return indices.Average(i => mesh.vertices[i].x);
                }
                float green = Lens("PROP_SIGNAL_GREEN"), yellow = Lens("PROP_SIGNAL_YELLOW"), red = Lens("PROP_SIGNAL_RED");
                // When viewed facing the display side (+Z), left is local +X. The Japanese order is blue, yellow, red from left to right.
                Check(green > yellow && yellow > red, $"lamp order must be green-yellow-red from the viewer's left ({green}, {yellow}, {red})");
            }
        }
        report.Add("models pole/arm/head " + string.Join("/", partVertices.Values));
    }

    static Bounds ModelBounds(GameObject prefab)
    {
        var filters = prefab.GetComponentsInChildren<MeshFilter>();
        Bounds result = default;
        bool first = true;
        foreach (var filter in filters)
            foreach (var v in filter.sharedMesh.vertices)
            {
                var p = prefab.transform.InverseTransformPoint(filter.transform.TransformPoint(v));
                if (first) { result = new Bounds(p, Vector3.zero); first = false; }
                else result.Encapsulate(p);
            }
        return result;
    }

    static IList Paths(Arm[] arms)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pathType));
        foreach (var arm in arms)
        {
            var line = new List<Vector3>();
            for (float s = 0; s <= 60f + 1e-3f; s += 1f) line.Add(arm.Outward * s + Vector3.up * .1f);
            if (arm.EndsAtNode) line.Reverse();
            points[arm.Id] = line;
            var path = Activator.CreateInstance(pathType);
            Set(path, "path_id", arm.Id); Set(path, "path_variant", arm.Variant); Set(path, "path_type", (int)sections[arm.Variant][6]);
            Set(path, "status", arm.Status); Set(path, "travel_direction", arm.Direction); Set(path, "curved", false);
            Set(path, "start_node_id", arm.EndsAtNode ? 100 + arm.Id : Node); Set(path, "end_node_id", arm.EndsAtNode ? Node : 100 + arm.Id);
            Set(path, "width", sections[arm.Variant][0]);
            Vector3 a = line[0], b = line[line.Count - 1];
            Set(path, "start_point", new List<object> { a.x, 0f, a.z });
            Set(path, "end_point", new List<object> { b.x, 0f, b.z });
            list.Add(path);
        }
        return list;
    }

    static void Scenario(string name, Arm[] arms, int expectedSignals, int expectedPoles, string image = null)
    {
        var paths = Paths(arms);
        var provider = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(pathType, typeof(List<Vector3>)),
            typeof(TrafficSignalsQA).GetMethod("Samples", Any).MakeGenericMethod(pathType));
        var generator = Activator.CreateInstance(asm.GetType("RoadIntersectionMeshGenerator"));
        var result = generator.GetType().GetMethod("GenerateWithSections").Invoke(generator, new object[] { Node, Vector3.zero, paths, 0f, provider, null });
        Check(result != null, name + ": intersection mesh missing");
        var signals = (IList)Get(result, "TrafficSignals");
        int count = signals?.Count ?? 0;
        Check(count == expectedSignals, $"{name}: expected {expectedSignals} signals, got {count}");
        var scene = BuildScene(paths, result);
        if (count > 0) CheckPlacements(name, arms, signals, expectedPoles, (IDictionary)Get(result, "ConnectionPoints"), scene);
        if (image != null) Shoot(image, arms);
        Object.DestroyImmediate(scene);
        report.Add(name + "=" + count);
    }

    static GameObject BuildScene(IList paths, object result)
    {
        var scene = new GameObject("Scene");
        var node = new GameObject("NodeMesh_1");
        node.transform.SetParent(scene.transform, false);
        node.AddComponent<MeshFilter>().sharedMesh = (Mesh)Get(result, "Mesh");
        node.AddComponent<MeshRenderer>().sharedMaterial = (Material)Get(result, "Material") ?? Flat(Color.gray);
        var sidewalk = new GameObject("Sidewalk");
        sidewalk.transform.SetParent(node.transform, false);
        var specs = (IList)Get(result, "SidewalkConnectors");
        var connector = asm.GetType("RoadPath").GetMethod("CreateSidewalkConnectorObject");
        if (specs != null) for (int i = 0; i < specs.Count; i++) connector.Invoke(null, new object[] { sidewalk.transform, specs[i], i });
        var cuts = (IDictionary)Get(result, "ConnectionPoints");
        var road = Activator.CreateInstance(asm.GetType("RoadPath"));
        var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
        foreach (var path in paths)
        {
            int id = (int)Get(path, "path_id");
            if (!cuts.Contains(id)) continue;
            var line = points[id];
            Vector3 cut = (Vector3)cuts[id];
            bool endsAtNode = (int)Get(path, "end_node_id") == Node;
            var piece = endsAtNode ? new List<Vector3> { line[0], cut } : new List<Vector3> { cut, line[line.Count - 1] };
            var generated = road.GetType().GetMethod("Generate").Invoke(road, new object[] { path, piece, setback, setback });
            var go = (GameObject)Get(generated, "GeneratedObject");
            go.transform.SetParent(scene.transform, true);
        }
        foreach (var component in scene.GetComponentsInChildren<Component>())
        {
            if (component == null || component.GetType().Name != "SplineMesh") continue;
            var rebuild = component.GetType().GetMethod("RebuildImmediate");
            rebuild.Invoke(component, rebuild.GetParameters().Length == 0 ? null : new object[] { true });
        }
        asm.GetType("TrafficSignalBuilder").GetMethod("Apply").Invoke(null, new object[] { node, Get(result, "TrafficSignals") });
        foreach (var filter in scene.GetComponentsInChildren<MeshFilter>())
            if (filter.sharedMesh != null && filter.name != "Signals") filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
        Physics.SyncTransforms();
        return scene;
    }

    /// <summary>Cast a ray straight down and report the first surface it hits other than the signal (height and name).</summary>
    static RaycastHit Surface(Vector2 xz)
    {
        var hits = Physics.RaycastAll(new Vector3(xz.x, 80f, xz.y), Vector3.down, 200f).OrderBy(h => h.distance).ToArray();
        Check(hits.Length > 0, "nothing under " + xz);
        return hits[0];
    }

    static void CheckPlacements(string name, Arm[] arms, IList signals, int expectedPoles, IDictionary cuts, GameObject scene)
    {
        bool left = (int)asm.GetType("CityInfoStore").GetProperty("TrafficHand").GetValue(null) == 0;
        int poles = 0;
        foreach (var signal in signals)
            Debug.Log($"[TrafficSignalsQA] {name} approach={Get(signal, "ApproachPathId")} pole={(Vector3)Get(signal, "PoleBase"):F3} head={(Vector2)Get(signal, "HeadPosition"):F3} reach={Get(signal, "Reach")} owns={Get(signal, "OwnsPole")}");
        foreach (var signal in signals)
        {
            var approach = arms.First(a => a.Id == (int)Get(signal, "ApproachPathId"));
            Vector3 foot = (Vector3)Get(signal, "PoleBase");
            Vector2 facing = (Vector2)Get(signal, "Facing");
            Vector2 head = (Vector2)Get(signal, "HeadPosition");
            float reach = (float)Get(signal, "Reach");
            bool ownsPole = (bool)Get(signal, "OwnsPole");
            if (ownsPole) poles++;
            Vector2 outward = new Vector2(approach.Outward.x, approach.Outward.z);
            Check(Vector2.Dot(facing, outward) > .999f, $"{name}: head of approach {approach.Id} must face the incoming traffic");
            Check(reach >= .9f - 1e-4f, $"{name}: arm too short for the head to clear the pole");
            // The pole stands on the sidewalk (or on the ground if there is no sidewalk), not on the carriageway.
            var ground = Surface(new Vector2(foot.x, foot.z));
            Check(ground.collider.name.Contains("sidewalk") || ground.collider.name == "Ground",
                $"{name}: pole of approach {approach.Id} stands on {ground.collider.name}");
            Check(Mathf.Abs(ground.point.y - foot.y) < .03f, $"{name}: pole of approach {approach.Id} floats/sinks {foot.y - ground.point.y:F3}m on {ground.collider.name}");
            // The bottom of the signal head is at least 4.5 m above the carriageway.
            var below = Surface(head);
            Check(foot.y + headBottom - below.point.y >= 4.5f, $"{name}: head of approach {approach.Id} is only {foot.y + headBottom - below.point.y:F2}m above {below.collider.name}");
            // If there is a straight-ahead arm, suspend the signal head above the center of that arm's outgoing lane group.
            var inward = -outward;
            var exit = arms.Where(a => a != approach).OrderByDescending(a => Vector2.Dot(inward, new Vector2(a.Outward.x, a.Outward.z))).First();
            Vector2 exitDirection = new Vector2(exit.Outward.x, exit.Outward.z);
            if (Vector2.Dot(inward, exitDirection) > .7071f && headOffsets.ContainsKey(exit.Variant))
            {
                Vector2 exitLeft = new Vector2(-exitDirection.y, exitDirection.x);
                float lateral = Vector2.Dot(head, exitLeft) * (left ? 1f : -1f);
                float expected = exit.Direction == 1 ? 0f : headOffsets[exit.Variant];
                Check(Mathf.Abs(lateral - expected) < .05f, $"{name}: head of approach {approach.Id} is {lateral:F2}m from the exit axis (expected {expected:F2})");
                Vector3 cut = (Vector3)cuts[exit.Id];
                Check(Vector2.Dot(head, exitDirection) > new Vector2(cut.x, cut.z).magnitude, $"{name}: head of approach {approach.Id} is inside the junction");
            }
            else
            {
                // Dead end: the signal head is on the extension of the centerline of the incoming lane group, on the far side of the intersection.
                Vector2 approachLeft = new Vector2(-outward.y, outward.x);
                float lateral = -Vector2.Dot(head, approachLeft) * (left ? 1f : -1f);
                float expected = approach.Direction == 0 && headOffsets.ContainsKey(approach.Variant) ? headOffsets[approach.Variant] : 0f;
                Check(Mathf.Abs(lateral - expected) < .1f, $"{name}: facing-corner head of approach {approach.Id} is {lateral:F2}m off its lanes (expected {expected:F2})");
                Check(Vector2.Dot(head, inward) > 0f, $"{name}: facing-corner head of approach {approach.Id} must stand across the junction");
            }
        }
        Check(poles == expectedPoles, $"{name}: expected {expectedPoles} poles, got {poles}");
        var child = scene.transform.Find("NodeMesh_1/Signals");
        Check(child != null, name + ": Signals child missing");
        var mesh = child.GetComponent<MeshFilter>().sharedMesh;
        int expectedVertices = poles * partVertices["pole"] + signals.Count * (partVertices["arm"] + partVertices["head"]);
        Check(mesh.vertexCount == expectedVertices, $"{name}: combined mesh has {mesh.vertexCount} vertices (expected {expectedVertices})");
        var renderer = child.GetComponent<MeshRenderer>();
        Check(renderer.sharedMaterials.Length == mesh.subMeshCount && renderer.sharedMaterials.All(m => m != null && m.name.StartsWith("PROP_")), name + ": combined materials");
        var lod = child.GetComponent<LODGroup>();
        Check(lod != null && lod.lodCount == 1 && Mathf.Abs(lod.GetLODs()[0].screenRelativeTransitionHeight - .03f) < 1e-4f, name + ": far culling");
        Check(child.GetComponent(asm.GetType("IntersectionMeshOwner")) != null, name + ": generated mesh owner");
    }

    static void Shoot(string image, Arm[] arms)
    {
        var camera = new GameObject("Camera").AddComponent<Camera>();
        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        light.shadows = LightShadows.Soft;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.55f, .66f, .8f);
        camera.fieldOfView = 40f;
        camera.farClipPlane = 500f;
        var texture = new RenderTexture(1400, 900, 24);
        camera.targetTexture = texture;
        void Save(string suffix, Vector3 position, Vector3 target)
        {
            camera.transform.position = position;
            camera.transform.LookAt(target);
            camera.Render();
            RenderTexture.active = texture;
            var pixels = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            pixels.Apply();
            File.WriteAllBytes($"{output}/{image}-{suffix}.png", pixels.EncodeToPNG());
            RenderTexture.active = null;
            Object.DestroyImmediate(pixels);
        }
        Save("overview", new Vector3(-34f, 30f, -40f), new Vector3(0f, 2f, 0f));
        // Driver's-eye view approaching the intersection from the first arm. Place it in the traffic-side lane.
        bool left = (int)asm.GetType("CityInfoStore").GetProperty("TrafficHand").GetValue(null) == 0;
        Vector3 from = arms[0].Outward * 32f + Vector3.up * 1.4f;
        Vector3 driverSide = Vector3.Cross(arms[0].Outward, Vector3.up).normalized * (left ? -1.6f : 1.6f);
        Save("driver", from + driverSide, new Vector3(0f, 4f, 0f) - arms[0].Outward * 10f);
        camera.targetTexture = null;
        Object.DestroyImmediate(texture);
        Object.DestroyImmediate(camera.gameObject);
        Object.DestroyImmediate(light.gameObject);
    }
}
