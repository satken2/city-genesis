using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Use actual FBXs and the main assembly in an isolated project to verify track gauge, placement, connections, and prefab references.
public static class LrtQA
{
    static Assembly asm;
    static IDictionary masters, models;
    static int checks;
    static string root;
    static void Check(bool ok, string text) { checks++; if (!ok) throw new Exception(text); }
    static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);
    static IDictionary Manager(string name, string field)
    {
        var type = asm.GetType(name);
        var component = new GameObject(name).AddComponent(type);
        type.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, component);
        if (name == "MasterDataStore") type.GetProperty("IsInitialized").SetValue(component, true);
        return (IDictionary)type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component);
    }
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string path = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            asm = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            masters = Manager("MasterDataStore", "pathMasterById");
            models = Manager("ModelManager", "_modelCache");
            foreach (string file in Directory.GetFiles("Assets/Paths", "*.fbx", SearchOption.AllDirectories))
            {
                string asset = file.Replace('\\', '/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                models["Models/Paths/" + asset.Substring("Assets/Paths/".Length).Replace(".fbx", "")] = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            }
            foreach (int id in new[] { 222, 242, 303, 431 })
            {
                Master(id);
                ModelDimensions(id);
                foreach (float slope in new[] { 0f, .08f, -.08f }) Road(id, slope, false);
                Road(id, 0, true);
                Underground(id);
            }
            foreach (int a in new[] { 222, 242, 303, 431 })
                foreach (int b in new[] { 222, 242, 303, 431 }) { Connections(a, b); Connections(a, b, true); }
            Terminations();
            StopIndex();
            PlatformLayout();
            foreach (int id in new[] { 222, 242, 303, 431 }) { PlatformModels(id); Platforms(id); }
            PlatformFallback();
            ElevatedPlatformWalls();
            PlatformClip();
            foreach (int id in new[] { 222, 242, 303 }) CornerPlatforms(id);
            NeighborPlatforms();
            foreach (int id in new[] { 303, 304, 305 }) Mall(id);
            ExportPrefabs();
            File.WriteAllText(root + "/Temp/LrtUnityQA/result.txt", "PASS LRT: " + checks + " checks; FBX, double track straight/curve/slope, all LRT connections, buffer stops, stop index, stop platforms, wrapper prefabs");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(root + "/Temp/LrtUnityQA/result.txt", e.ToString());
            Debug.LogException(e); EditorApplication.Exit(1);
        }
    }
    static void Underground(int id)
    {
        var path = PathData(id);
        var points = new List<Vector3> { new Vector3(0, -20, 0), new Vector3(0, -20, 80) };
        var generator = asm.GetType(id == 431 ? "TrackPath" : "RoadPath");
        var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
        object result;
        UseStops(Stop(.5f,22));
        try { result = generator.GetMethod("Generate").Invoke(Activator.CreateInstance(generator), new[] { path, points, setback, setback }); }
        finally { UseStops(); }
        var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        Check(go.GetComponentsInChildren<MeshFilter>().Count(f => f.name.EndsWith("/wall_underground")) == 2, "underground LRT walls " + id);
        Check(!go.GetComponentsInChildren<Transform>().Any(t => t.name == "LrtPole" || t.name.StartsWith("pole(") || t.name.StartsWith("beam(")), "underground LRT has no poles " + id);
        Check(!go.GetComponentsInChildren<Transform>().Any(t=>t.name=="LrtShelter"),"underground LRT has no platform shelter " + id);
        Check(go.GetComponentsInChildren<MeshFilter>().Count(f=>f.name.EndsWith("/ceiling_underground"))==1,"underground LRT has a tunnel ceiling " + id);
        string track = id == 431 ? "/track" : "/lrt_track", wire = id == 431 ? "/wire" : "/lrt_wire";
        Check(go.GetComponentsInChildren<MeshFilter>().Count(f => f.name.EndsWith(track)) == 2
            && go.GetComponentsInChildren<MeshFilter>().Count(f => f.name.EndsWith(wire)) == 2, "underground LRT keeps tracks and wires " + id);
        UnityEngine.Object.DestroyImmediate(go);
        UnityEngine.Object.DestroyImmediate((Mesh)result.GetType().GetProperty("Mesh").GetValue(result));
    }

    // Specification values. For both 303 and 431, the track centers are at ±1.75 m (3.5 m spacing), and the LRT vehicle half-width is 1.27 m.
    const float MallOffset = 1.75f, TramHalfWidth = 1.27f;
    static float ExpectedOffset(int id) => id == 222 ? 2.5625f : id == 242 ? 2.541667f : MallOffset;
    static readonly List<object> connected = new List<object>();
    static List<T> ConnectedPaths<T>(int node) => node == 1 ? connected.Cast<T>().ToList() : new List<T>();
    static void Master(int id)
    {
        var m = Activator.CreateInstance(asm.GetType("PathMasterItem"));
        Set(m, "width", id == 222 ? 24f : id == 242 ? 32f : id == 303 ? 12f : id == 431 ? 7f : 5f);
        Set(m, "lanes", id == 222 ? 4 : id == 242 ? 6 : id == 431 ? 2 : 1);
        Set(m, "sidewalk_width", id < 300 ? 3f : 0f);
        Set(m, "side_width", id < 300 ? .5f : 0f);
        Set(m, "center_width", id < 300 ? 1f : 0f);
        Set(m, "wire_spacing", 30f); Set(m, "follow_terrain", false);
        Set(m, "height", 7f);
        masters[id] = m;
    }
    static object PathData(int id, int key = 1, bool start = true)
    {
        var p = Activator.CreateInstance(asm.GetType("GeneratedModels.PathSummary"));
        Set(p, "path_id", key); Set(p, "path_variant", id);
        Set(p, "status", 1);
        Set(p, "path_type", id < 300 ? 1 : id == 303 ? 3 : 4);
        Set(p, "start_node_id", start ? 1 : key + 10); Set(p, "end_node_id", start ? key + 10 : 1);
        Set(p, "start_point", new List<object> { 0f, 0f, start ? 0f : -50f });
        Set(p, "end_point", new List<object> { 0f, 0f, start ? 50f : 0f });
        Set(p, "follow_terrain", 0); Set(p, "curved", true);
        return p;
    }
    static void ModelDimensions(int id)
    {
        string part = id == 431 ? "track" : "lrt_track";
        var model = (GameObject)models[$"Models/Paths/p{id}/{part}"];
        var mesh = model.GetComponent<MeshFilter>().sharedMesh;
        float top = id == 431 ? .355f : .025f;
        Check(Mathf.Abs(mesh.bounds.max.y - top) < .002f, "rail top " + id);
        var xs = mesh.vertices.Where(v => Mathf.Abs(v.y - top) < .002f).Select(v => v.x).ToArray();
        Check(xs.Any(x => x > .68f && x < .76f) && xs.Any(x => x < -.68f && x > -.76f), "rail gauge " + id);
        var parts = asm.GetType("LrtRoadParts");
        float Float(string method, object arg) => (float)parts.GetMethod(method).Invoke(null, new[] { arg });
        float offset = Float("Offset", PathData(id));
        Check(Mathf.Abs(offset - ExpectedOffset(id)) < .0001f, "track center offset " + id);
        var laterals = (List<float>)parts.GetMethod("TrackLaterals").Invoke(null, new[] { PathData(id) });
        Check(laterals.Count == 2 && Mathf.Abs(laterals[0] + offset) < .0001f && Mathf.Abs(laterals[1] - offset) < .0001f, "double track laterals " + id);
        // Rail running surface: for embedded track, spline + 0.1 (road surface) + 0.025; for 431, spline + 0.12 (bottom of track) + 0.355.
        Check(Mathf.Abs(Float("RailTopAboveSpline", id) - (id == 431 ? .475f : .125f)) < .0001f, "rail top above spline " + id);
        float width = id == 222 || id == 242 ? 1.2f : 2f;
        Check(Mathf.Abs(Float("PlatformInnerOffset", PathData(id)) - (ExpectedOffset(id) + 1.35f)) < .0001f, "platform inner edge " + id);
        Check(Mathf.Abs(Float("PlatformOuterOffset", PathData(id)) - (ExpectedOffset(id) + 1.35f + width)) < .0001f, "platform outer edge " + id);
        Check(Mathf.Abs(Float("PlatformSurfaceAboveSpline", id) - ((id == 431 ? .475f : .125f) + .3f)) < .0001f, "platform surface " + id);
        Check(Mathf.Abs(Float("PlatformRampLength", id) - (id == 431 ? 6f : 3f)) < .0001f, "platform ramp " + id);
        // Paving and track bed are authored to the full width of the double track (303=12 m, 431=7 m). RoadPath matches the width of the top face (within 2 mm of the highest point)
        // to the master width, so the top face also reaches its full width at the origin height and the rendering's lateral scale is 1.
        if (id == 303 || id == 431)
        {
            float full = id == 303 ? 12f : 7f;
            foreach (string body in id == 431 ? new[] { "body", "body_close" } : new[] { "body" })
            {
                var bodyMesh = ModelMesh(id, body);
                var surface = (Bounds)asm.GetType("RoadPath").GetMethod("GetBodySurfaceBounds").Invoke(null, new object[] { bodyMesh });
                Check(Mathf.Abs(bodyMesh.bounds.size.x - full) < .01f && Mathf.Abs(bodyMesh.bounds.center.x) < .01f, $"{body} width {id}: {bodyMesh.bounds.size.x}");
                Check(Mathf.Abs(surface.size.x - full) < .01f && Mathf.Abs(surface.center.x) < .01f && Mathf.Abs(bodyMesh.bounds.max.y) < .002f,
                    $"{body} surface {id}: width {surface.size.x} at {bodyMesh.bounds.max.y}");
            }
            DrawnBody(id, full);
        }
        if (id == 303) MallPole((float)parts.GetField("MALL_POLE_OFFSET").GetRawConstantValue());
        if (id == 431) return;
        mesh = ((GameObject)models[$"Models/Paths/p{id}/lrt_wire"]).GetComponent<MeshFilter>().sharedMesh;
        Check(Mathf.Abs(mesh.bounds.center.y - 5.3f) < .002f, "wire height " + id);
    }
    static void MallPole(float poleOffset)
    {
        // The bases of the poles on both sides of 303 are at ±5.4 m (positions reserved by RoadEquipmentGenerator), and the suspension fittings are directly above the nearer track (±1.75 m).
        Check(Mathf.Abs(poleOffset - 5.4f) < .0001f, "mall pole offset constant");
        var pole = (GameObject)models["Models/Paths/p303/lrt_pole"];
        var mesh = pole.GetComponent<MeshFilter>().sharedMesh;
        var vertices = mesh.vertices;
        var feet = vertices.Where(v => v.y < .05f).Select(v => v.x).ToArray();
        Check(feet.Any(x => x < 0) && feet.Any(x => x > 0) && feet.All(x => Mathf.Abs(Mathf.Abs(x) - poleOffset) < .25f), "mall pole feet");
        var materials = pole.GetComponent<MeshRenderer>().sharedMaterials;
        int hangerSubmeshes = 0;
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (!materials[sub].name.StartsWith("PATH_RAIL_METAL")) continue;
            hangerSubmeshes++;
            var xs = mesh.GetTriangles(sub).Select(i => vertices[i].x).ToArray();
            Check(xs.Any(x => x < 0) && xs.Any(x => x > 0) && xs.All(x => Mathf.Abs(Mathf.Abs(x) - MallOffset) < .05f), "mall pole hangers above tracks");
        }
        Check(hangerSubmeshes == 1, "mall pole hanger material");
    }
    static void Road(int id, float slope, bool curved)
    {
        var path = PathData(id);
        var points = new List<Vector3>();
        for (int i = 0; i <= 16; i++) points.Add(new Vector3(curved ? 8 * Mathf.Sin(i / 16f * Mathf.PI) : 0, slope * i * 5, i * 5));
        var type = asm.GetType(id == 431 ? "TrackPath" : "RoadPath");
        var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
        var result = type.GetMethod("Generate").Invoke(Activator.CreateInstance(type), new[] { path, points, setback, setback });
        var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        Check(go != null, "generated " + id);
        var filters = go.GetComponentsInChildren<MeshFilter>();
        foreach (var f in filters) if (f.sharedMesh != null)
        {
            Check(f.sharedMesh.vertexCount <= 60000, "vertex budget " + f.name);
            Check(f.sharedMesh.vertices.All(v => !float.IsNaN(v.x + v.y + v.z)), "finite " + f.name);
        }
        string trackPart = id == 431 ? "/track" : "/lrt_track", wirePart = id == 431 ? "/wire" : "/lrt_wire";
        // For each ground-level or elevated section, 431 lays one mesh per track.
        var tracks = filters.Where(f => f.name.EndsWith(trackPart) || (id == 431 && f.name.EndsWith("/track_elevated"))).ToArray();
        Check(id == 431 ? tracks.Length >= 2 && tracks.Length % 2 == 0 : tracks.Length == 2, "track count " + id);
        Check(filters.Count(f => f.name.EndsWith(wirePart)) == 2, "wire count " + id);
        if (!curved && slope == 0)
        {
            if (id != 431)
                foreach (var f in filters.Where(f => f.name.EndsWith("/body") || f.name.EndsWith("/sidewalk") || f.name.EndsWith("/lrt_track")))
                {
                    var mesh = f.sharedMesh; var v = mesh.vertices; var tri = mesh.triangles;
                    int internalCaps = 0, startCaps = 0, endCaps = 0;
                    for (int t = 0; t < tri.Length; t += 3)
                    {
                        var a = v[tri[t]]; var b = v[tri[t + 1]]; var c = v[tri[t + 2]];
                        if (Mathf.Abs(Vector3.Cross(b - a, c - a).normalized.z) < .999f) continue;
                        if (Mathf.Abs(a.z - mesh.bounds.min.z) < .001f) startCaps++;
                        else if (Mathf.Abs(a.z - mesh.bounds.max.z) < .001f) endCaps++;
                        else internalCaps++;
                    }
                    Check(internalCaps == 0 && startCaps > 0 && endCaps > 0, "closed ends without internal shadow caps " + id + " " + f.name);
                }
            // On a straight section, the lateral center of each track mesh matches the track center (symmetric ±offset).
            var centers = tracks.Select(f => f.GetComponent<Renderer>().bounds.center.x).ToArray();
            Check(centers.All(x => Mathf.Abs(Mathf.Abs(x) - ExpectedOffset(id)) < .02f) && centers.Any(x => x < 0) && centers.Any(x => x > 0), "track lateral " + id);
        }
        if (id != 431)
        {
            var poles = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtPole").ToArray();
            Check(poles.Length >= 2, "poles present " + id);
            Check(poles.All(t => Vector3.Dot(t.up, Vector3.up) > .9999f), "poles vertical " + id);
        }
        UnityEngine.Object.DestroyImmediate(go);
    }
    static void Connections(int a, int b, bool turn = false)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(asm.GetType("GeneratedModels.PathSummary")));
        list.Add(PathData(a, 1, false)); list.Add(PathData(b, 2, true));
        var cuts = new Dictionary<int, Vector2> { { 1, new Vector2(0, -12) }, { 2, new Vector2(0, 12) } };
        if (turn)
        {
            Set(list[1], "end_point", new List<object> { 50f, 0f, 0f });
            cuts[2] = new Vector2(12, 0);
        }
        var specs = (IList)asm.GetType("TrackIntersectionMeshGenerator").GetMethod("BuildRailConnectorSpecs").Invoke(null, new object[] { 1, Vector3.zero, true, list, cuts, null });
        if (turn && a == 431 && b == 431) { Check(specs == null, "dedicated track turn restriction"); return; }
        Check(specs != null && specs.Count == 2, $"connections {a}-{b}");
        foreach (var spec in specs)
        {
            var points = (List<Vector3>)spec.GetType().GetProperty("Points").GetValue(spec);
            float ao = ExpectedOffset(a), bo = ExpectedOffset(b);
            Check(Mathf.Abs(Mathf.Abs(points[0].x) - ao) < .0001f, "start rail alignment");
            Check(Mathf.Abs(Mathf.Abs(turn ? points[points.Count - 1].z : points[points.Count - 1].x) - bo) < .0001f, "end rail alignment");
            // Double tracks continuing straight do not swap the two tracks: the left track connects to the left and the right track to the right.
            if (!turn) Check(points[0].x * points[points.Count - 1].x > 0, $"straight connector keeps its side {a}-{b}");
            var go = new GameObject("ConnectorTest");
            asm.GetType("TrackPath").GetMethod("CreateRailConnectorObject").Invoke(null, new object[] { go.transform, spec, 0 });
            Check(go.GetComponentsInChildren<MeshFilter>().Length > 0, "connector mesh");
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
    static void Terminations()
    {
        var track = asm.GetType("TrackPath");
        var summary = asm.GetType("GeneratedModels.PathSummary");
        // 431 falls back to the shared p4 component for buffer stops. It is not available in the isolated project, so count a substitute.
        var stub = new GameObject("BufferStopStub");
        models["Models/Paths/p4/buffer_stop"] = stub;
        var provider = track.GetField("ConnectedPathsProvider");
        provider.SetValue(null, Delegate.CreateDelegate(provider.FieldType,
            typeof(LrtQA).GetMethod("ConnectedPaths", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(summary)));
        try
        {
            // Do not place buffer stops at 431 endpoints that continue into the LRT network; close both tracks at endpoints that connect only to conventional rail.
            foreach (int other in new[] { 222, 242, 303, 431, 411 })
            {
                var path = PathData(431, 1, true);
                connected.Clear(); connected.Add(path); connected.Add(PathData(other, 2, false));
                var parent = new GameObject("LrtEndsQA");
                track.GetMethod("CreateTerminations", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] {
                    parent, new List<Vector3> { Vector3.zero, new Vector3(0, 0, 50) }, null, path, 7f, 2, 0f, 0f, "Models/Paths/p431", "Models/Paths/p4" });
                int stops = parent.GetComponentsInChildren<Transform>().Count(t => t.name == "TrackBufferStop");
                Check(stops == (other == 411 ? 2 : 0), $"buffer stops at 431-{other}");
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }
        finally
        {
            provider.SetValue(null, null);
            models.Remove("Models/Paths/p4/buffer_stop");
            UnityEngine.Object.DestroyImmediate(stub);
            connected.Clear();
        }
    }
    static void Mall(int id)
    {
        Master(id);
        float width = id == 305 ? 20f : 12f;
        var m = masters[id];
        Set(m, "width", width); Set(m, "tree_spacing", 24f);
        if (id != 303) Set(m, "wire_spacing", 0f);
        string[] fields = { "streetlight_type", "street_tree_type", "bench_type", "bin_type", "planter_type", "advert_type" };
        foreach (string f in fields) Set(m, f + "_allowed", true);
        var path = PathData(id);
        Set(path, "path_type", 3);
        foreach (string f in fields) Set(path, f, 1);
        foreach (bool curved in new[] { false, true })
        {
            var points = new List<Vector3>();
            for (int i = 0; i <= 20; i++) points.Add(new Vector3(curved ? 4 * Mathf.Sin(i * Mathf.PI / 20) : 0, .02f*i*4, i*4));
            var type = asm.GetType("RoadPath");
            var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
            var result = type.GetMethod("Generate").Invoke(Activator.CreateInstance(type), new[] { path, points, setback, setback });
            var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
            foreach (string part in new[] { "bench", "bin", "planter", "advert", "tree" })
            {
                var placed = go.GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith(part + "(")).ToArray();
                Check(placed.Length > 0, "mall part present " + id + " " + part);
                foreach (var f in placed)
                {
                    Check(Vector3.Dot(f.transform.up, Vector3.up) > .9999f, "mall upright " + part);
                    Check(f.sharedMesh.vertices.All(v => !float.IsNaN(v.x+v.y+v.z)), "mall finite " + part);
                    if (!curved)
                    {
                        var b = f.GetComponent<Renderer>().bounds;
                        Check(b.min.x > -width/2 && b.max.x < width/2, "mall footprint " + id + " " + part);
                        Check(b.min.z > 1.5f && b.max.z < 78.5f, "mall end clearance " + part);
                        if (id == 304) Check(b.min.x >= -1.7f && b.max.x <= 1.7f, "central furniture island");
                        if (id == 305) Check(Mathf.Abs(f.transform.position.x) > 3.9f && Mathf.Abs(f.transform.position.x) < 4.1f, "two furniture rows");
                        if (id == 303)
                        {
                            // Equipment and tree canopies do not overhang the double-track vehicles (track centers ±1.75 m, half-width 1.27 m).
                            float envelope = MallOffset + TramHalfWidth;
                            Check(b.max.x < -envelope || b.min.x > envelope, "LRT clearance " + part);
                            // Do not place anything closer than the minimum spacing of 1.2 m to the bases of the catenary poles on both sides (±5.4 m, 5 m from the end and at 30 m intervals).
                            Vector3 p = f.transform.position;
                            foreach (float z in new[] { 5f, 35f, 65f })
                                foreach (float x in new[] { -5.4f, 5.4f })
                                    Check(new Vector2(p.x - x, p.z - z).magnitude > 1.15f, "LRT pole foot reserved " + part);
                        }
                    }
                }
            }
            UnityEngine.Object.DestroyImmediate(go);
        }
        if (id == 303)
        {
            // Do not place point equipment in the stop platform section (deck 29–51 m + 3 m slope + 1 m clearance); retain it outside that section.
            UseStops(Stop(.5f, 22));
            try
            {
                var points = new List<Vector3>();
                for (int i = 0; i <= 20; i++) points.Add(new Vector3(0, 0, i * 4));
                var go = Generate(303, path, points);
                string[] equipment = { "bench", "bin", "planter", "advert", "tree", "streetlight" };
                var placed = go.GetComponentsInChildren<Transform>().Where(t => equipment.Any(e => t.name.StartsWith(e + "("))).ToArray();
                Check(placed.Length > 0, "mall equipment outside platform");
                Check(placed.All(t => t.position.z < 25f || t.position.z > 55f), "mall equipment kept clear of platform");
                Check(go.GetComponentsInChildren<Transform>().Count(t => t.name == "LrtShelter") == 2, "mall platform shelters");
                UnityEngine.Object.DestroyImmediate(go);
                // A stop near the endpoint (deck 61–83 m) is rendered to the clipped end at 80 m, and the section without point equipment (from 57 m) follows it.
                UseStops(Stop(.9f, 22));
                go = Generate(303, path, points);
                var decks = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/lrt_platform")).ToArray();
                Check(decks.Length == 2 && decks.All(d => Mathf.Abs(d.bounds.min.z - 61) < .1f && Mathf.Abs(d.bounds.max.z - 80) < .1f), "mall platform clamped to cut end");
                placed = go.GetComponentsInChildren<Transform>().Where(t => equipment.Any(e => t.name.StartsWith(e + "("))).ToArray();
                Check(placed.Length > 0 && placed.All(t => t.position.z < 57f), "mall equipment kept clear of clamped platform");
                UnityEngine.Object.DestroyImmediate(go);
            }
            finally { UseStops(); }
        }
        foreach (string f in fields) Set(path, f, 0);
        var empty = new GameObject("NoEquipment");
        asm.GetType("RoadEquipmentGenerator").GetMethod("Generate").Invoke(null, new object[] { empty, null, path, width, 0f, 24f, null, null });
        Check(empty.transform.childCount == 0, "all equipment disabled " + id);
        UnityEngine.Object.DestroyImmediate(empty);
    }
    // Stop platform. StopProvider returns the stop only for path 1.
    static readonly List<object> stops = new List<object>();
    static IReadOnlyList<T> Stops<T>(int path) => path == 1 && stops.Count > 0 ? stops.Cast<T>().ToList() : null;
    static Type PlatformParts => asm.GetType("LrtPlatformParts");
    static Type StopType => asm.GetType("LrtPlatformParts+Stop");
    static object Stop(float fraction, float deck) => Activator.CreateInstance(StopType, fraction, deck);
    static IList StopList(params object[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(StopType));
        foreach (var item in items) list.Add(item);
        return list;
    }
    static void UseStops(params object[] items)
    {
        stops.Clear(); stops.AddRange(items);
        var provider = PlatformParts.GetField("StopProvider");
        provider.SetValue(null, items.Length == 0 ? null : Delegate.CreateDelegate(provider.FieldType,
            typeof(LrtQA).GetMethod("Stops", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(StopType)));
    }
    static void StopIndex()
    {
        // From stop_points in the line summary, collect only lines whose vehicle type is LRT, grouped by base path. Exclude stops whose base path is unresolved (null), and
        // the deck length is the number of cars × car length + 1 m at each end. The isolated project has no vehicle-type table, so inject LRT (1003→14) and bus (1→10).
        var types = asm.GetType("VehicleModelConfig").GetField("transitTypes", BindingFlags.Static | BindingFlags.NonPublic);
        object saved = types.GetValue(null);
        var definitions = (IDictionary)Activator.CreateInstance(types.FieldType);
        var definitionType = types.FieldType.GetGenericArguments()[1];
        foreach (var entry in new[] { (1, 10), (1003, 14) })
        {
            var definition = Activator.CreateInstance(definitionType, true);
            Set(definition, "vehicle_type", entry.Item2);
            definitions[entry.Item1] = definition;
        }
        types.SetValue(null, definitions);
        try
        {
            var summaryType = asm.GetType("GeneratedModels.TransitLineSummary");
            var pointType = asm.GetType("GeneratedModels.TransitStopPoint");
            object Point(int node, int? path, float? fraction)
            {
                var point = Activator.CreateInstance(pointType);
                Set(point, "node_id", node); Set(point, "station_path_id", path); Set(point, "fraction", fraction);
                return point;
            }
            object Summary(int variant, int cars, params object[] points)
            {
                var summary = Activator.CreateInstance(summaryType);
                Set(summary, "path_variant", variant); Set(summary, "cars_per_train", cars); Set(summary, "length_per_vehicle", 10f);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pointType));
                foreach (var point in points) list.Add(point);
                Set(summary, "stop_points", list);
                return summary;
            }
            var lines = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(summaryType));
            lines.Add(Summary(1003, 3, Point(4, 7, .75f)));
            lines.Add(Summary(1003, 2, Point(1, 7, .25f), Point(2, 8, .5f), Point(3, null, null)));
            lines.Add(Summary(1, 1, Point(5, 9, .5f)));
            var index = (IDictionary)asm.GetType("LrtStopPlatforms").GetMethod("Collect").Invoke(null, new object[] { lines });
            Check(index.Count == 2 && index.Contains(7) && index.Contains(8), "stop index keeps LRT underlays only");
            var shared = (IList)index[7];
            Check(shared.Count == 2 && Mathf.Abs(Number(shared[0], "Fraction") - .25f) < 1e-6f && Mathf.Abs(Number(shared[0], "DeckLength") - 22f) < 1e-4f
                && Mathf.Abs(Number(shared[1], "Fraction") - .75f) < 1e-6f && Mathf.Abs(Number(shared[1], "DeckLength") - 32f) < 1e-4f, "stop index sorted with deck length");
            Check(((IList)index[8]).Count == 1, "stop index per underlay");
        }
        finally { types.SetValue(null, saved); }
    }
    static List<Vector3> Line(float from, float to)
    {
        var line = new List<Vector3>();
        for (float z = from; z <= to + .001f; z += 1f) line.Add(new Vector3(0, 0, z));
        return line;
    }
    static IList Layout(IList stopList, List<Vector3> full, List<Vector3> trimmed, List<Vector3> drawn, float splineLength) =>
        (IList)PlatformParts.GetMethod("Layout").Invoke(null, new object[] { stopList, full, trimmed, drawn, splineLength });
    static float Number(object target, string field) => Convert.ToSingle(target.GetType().GetField(field).GetValue(target));
    static void ExpectSpan(IList spans, int index, float from, float to, bool rampFrom, bool rampTo, string label)
    {
        Check(spans.Count > index, label + ": missing span");
        object span = spans[index];
        Check(Mathf.Abs(Number(span, "From") - from) < .01f && Mathf.Abs(Number(span, "To") - to) < .01f,
            $"{label}: {Number(span, "From")}..{Number(span, "To")} != {from}..{to}");
        Check((bool)span.GetType().GetField("RampAtFrom").GetValue(span) == rampFrom
            && (bool)span.GetType().GetField("RampAtTo").GetValue(span) == rampTo, label + ": ramps");
    }
    static void PlatformLayout()
    {
        // Unclipped length 100 m; clipped section 10–90 m. With the stop center at 50 m and a 22 m deck, the deck spans 29–51 m from the start of the rendered point sequence (= clipped section).
        List<Vector3> full = Line(0, 100), trimmed = Line(10, 90);
        var spans = Layout(StopList(Stop(.5f, 22)), full, trimmed, trimmed, 80);
        Check(spans.Count == 1, "single platform"); ExpectSpan(spans, 0, 29, 51, true, true, "single platform");
        ExpectSpan(Layout(StopList(Stop(.5f, 22)), full, trimmed, trimmed, 160), 0, 58, 102, true, true, "spline length scale");
        // Merge overlapping sections and gaps shorter than 2 m; gaps of 2 m or more form separate platforms.
        spans = Layout(StopList(Stop(.5f, 22), Stop(.68f, 12)), full, trimmed, trimmed, 80);
        Check(spans.Count == 1, "merge gap < 2m"); ExpectSpan(spans, 0, 29, 64, true, true, "merge gap < 2m");
        Check(Layout(StopList(Stop(.5f, 22), Stop(.72f, 12)), full, trimmed, trimmed, 80).Count == 2, "separate gap >= 2m");
        spans = Layout(StopList(Stop(.5f, 32), Stop(.5f, 22)), full, trimmed, trimmed, 80);
        Check(spans.Count == 1, "shared stop"); ExpectSpan(spans, 0, 24, 56, true, true, "shared stop keeps longest");
        // Use the deck up to the ends of the clipped section (the slopes extend beyond the clipped ends); after fitting it, do not render it if it is shorter than 4 m.
        ExpectSpan(Layout(StopList(Stop(.1f, 22)), full, trimmed, trimmed, 80), 0, 0, 11, true, true, "clamped to cut start");
        ExpectSpan(Layout(StopList(Stop(.9f, 22)), full, trimmed, trimmed, 80), 0, 69, 80, true, true, "clamped to cut end");
        ExpectSpan(Layout(StopList(Stop(.05f, 22)), full, trimmed, trimmed, 80), 0, 0, 6, true, true, "short deck kept at cut");
        Check(Layout(StopList(Stop(.06f, 12)), full, trimmed, trimmed, 80).Count == 0, "too short after clamp");
        // For rendered sections separated by tunnels, render only within each section and do not place slopes at ends clipped by a section (place them at actual clipped ends).
        ExpectSpan(Layout(StopList(Stop(.5f, 22)), full, trimmed, Line(40, 90), 50), 0, 0, 21, false, true, "tunnel run");
        ExpectSpan(Layout(StopList(Stop(.9f, 22)), full, trimmed, Line(40, 90), 50), 0, 39, 50, true, true, "tunnel run clamped to cut end");
        ExpectSpan(Layout(StopList(Stop(.1f, 22)), full, trimmed, Line(10, 40), 30), 0, 0, 11, true, true, "run before tunnel clamped to cut start");
        ExpectSpan(Layout(StopList(Stop(.4f, 22)), full, trimmed, Line(10, 40), 30), 0, 19, 30, true, false, "run before tunnel");
        Check(Layout(StopList(Stop(.5f, 22)), full, trimmed, Line(70, 90), 20).Count == 0, "platform outside run");
        // Merge overlapping platform sections (deck + slopes + clearance) into one; at clipped ends, the deck extends beyond the spline.
        var zoneRanges = PlatformParts.GetMethod("ZoneRanges");
        var zones = (IList)zoneRanges.Invoke(null, new object[] { Layout(StopList(Stop(.5f, 22), Stop(.72f, 12)), full, trimmed, trimmed, 80), 3f, 1f });
        Check(zones.Count == 1 && Mathf.Abs(((Vector2)zones[0]).x - 25) < .01f && Mathf.Abs(((Vector2)zones[0]).y - 72) < .01f, "platform zones merged");
        zones = (IList)zoneRanges.Invoke(null, new object[] { Layout(StopList(Stop(.9f, 22)), full, trimmed, trimmed, 80), 3f, 1f });
        Check(zones.Count == 1 && Mathf.Abs(((Vector2)zones[0]).x - 65) < .01f && Mathf.Abs(((Vector2)zones[0]).y - 84) < .01f, "platform zone follows clamped deck");
    }
    static Mesh ModelMesh(int id, string part) => ((GameObject)models[$"Models/Paths/p{id}/{part}"]).GetComponent<MeshFilter>().sharedMesh;
    /// <summary>Verify that paving and track bed laid on a straight section render at the master width in both normal and close-up views, with the top face at spline + BODY_ABOVE_SPLINE.</summary>
    static void DrawnBody(int id, float width)
    {
        var context = asm.GetType("PathDrawContext");
        float surface = (float)asm.GetType("RoadPartHeights").GetField("BODY_ABOVE_SPLINE").GetRawConstantValue();
        foreach (string lod in new[] { "Normal", "Close" })
        {
            context.GetMethod("Begin").Invoke(null, new[] { Enum.Parse(asm.GetType("DrawLod"), lod), 0.0, 1.0 });
            try
            {
                var go = Generate(id, PathData(id), Straight());
                var bodies = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/body")).ToArray();
                Check(bodies.Length > 0 && bodies.All(r => Mathf.Abs(r.bounds.size.x - width) < .02f && Mathf.Abs(r.bounds.center.x) < .02f
                    && Mathf.Abs(r.bounds.max.y - surface) < .005f),
                    $"drawn body {id} {lod}: " + string.Join(", ", bodies.Select(r => $"{r.bounds.size.x}@{r.bounds.max.y}")));
                UnityEngine.Object.DestroyImmediate(go);
            }
            finally { context.GetMethod("End").Invoke(null, null); }
        }
    }
    static float PlatformWidth(int id) => id == 222 || id == 242 ? 1.2f : 2f;
    static void PlatformModels(int id)
    {
        float width = PlatformWidth(id), ramp = id == 431 ? 6f : 3f;
        // For the right side in Unity coordinates (+X points outward from the track). Boarding edge 1.35 m, outer edge 1.35+W, authored length 4 m, and top face at the rail running surface + 0.30 m.
        var deck = ModelMesh(id, "lrt_platform");
        Check(Mathf.Abs(deck.bounds.min.x - 1.35f) < .02f && Mathf.Abs(deck.bounds.max.x - (1.35f + width)) < .02f,
            $"platform model lateral {id}: {deck.bounds.min.x}..{deck.bounds.max.x}");
        Check(Mathf.Abs(deck.bounds.size.z - 4f) < .01f, "platform model length " + id);
        Check(deck.vertices.Any(v => Mathf.Abs(v.y - .3f) < .003f), "platform model surface " + id);
        // The slope is centered at the cross-section origin and symmetric; it descends toward +Z from 0.30 m at the deck edge (Z=0).
        var end = ModelMesh(id, "lrt_platform_end");
        Check(Mathf.Abs(end.bounds.min.x + width / 2) < .02f && Mathf.Abs(end.bounds.max.x - width / 2) < .02f, "ramp model width " + id);
        Check(end.bounds.min.z > -.02f && Mathf.Abs(end.bounds.max.z - ramp) < .05f, "ramp model length " + id);
        // At the tip, the top face is at the rail running surface + 0.01 m (road surface + 0.035 m) for embedded track, and at the spline surface (running surface - 0.475 m) for 431.
        // The lower ends of the side faces are embedded below ground in both cases.
        var roadParts = asm.GetType("LrtRoadParts");
        float railTop = (float)roadParts.GetMethod("RailTopAboveSpline").Invoke(null, new object[] { id });
        float ground = id == 431 ? -railTop : -(float)roadParts.GetField("EMBEDDED_RAIL_TOP").GetRawConstantValue();
        float foot = id == 431 ? ground : .01f;
        float footTop = end.vertices.Where(v => v.z > ramp - .05f).Max(v => v.y);
        Check(end.vertices.Where(v => v.z < .05f).Max(v => v.y) > .28f && Mathf.Abs(footTop - foot) < .002f, $"ramp model slope {id}: foot {footTop} != {foot}");
        Check(end.bounds.min.y < ground - .05f, $"ramp model buried {id}: {end.bounds.min.y}");
        // Keep the tip higher than the dashed lane divider that passes beneath the platform (the line is placed at spline + LINE_ABOVE_SPLINE + CENTER_LINE_EXTRA).
        if (models.Contains($"Models/Paths/p{id}/line"))
        {
            var heights = asm.GetType("RoadPartHeights");
            float dash = (float)heights.GetField("LINE_ABOVE_SPLINE").GetRawConstantValue()
                + (float)heights.GetField("CENTER_LINE_EXTRA").GetRawConstantValue() + ModelMesh(id, "line").bounds.max.y;
            Check(footTop > dash - railTop, $"ramp foot covers lane markings {id}: {footTop} <= {dash - railTop}");
        }
        // The canopy uses the placement-surface origin. The floor-contacting columns, legs, and back panel are only on the rear (+X) side, leaving the track side (-X) open.
        var shelter = ModelMesh(id, "lrt_shelter");
        Check(Mathf.Abs(shelter.bounds.min.y) < .02f && shelter.bounds.size.x <= width && shelter.bounds.max.z < 2.05f && shelter.bounds.min.z > -2.05f,
            $"shelter model footprint {id}: {shelter.bounds}");
        Check(shelter.vertices.Where(v => v.y < .1f).All(v => v.x > 0f), "shelter open to track " + id);
    }
    static GameObject Generate(int id, object path, List<Vector3> points)
    {
        var type = asm.GetType(id == 431 ? "TrackPath" : "RoadPath");
        var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
        var result = type.GetMethod("Generate").Invoke(Activator.CreateInstance(type), new[] { path, points, setback, setback });
        return (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
    }
    static List<Vector3> Straight(float height = 0f)
    {
        var points = new List<Vector3>();
        for (int i = 0; i <= 24; i++) points.Add(new Vector3(0, height, i * 5));
        return points;
    }
    static void Platforms(int id)
    {
        Master(id);
        // Two cars at the center of a 120 m straight section (22 m deck). The deck spans 49–71 m, with side platforms opposite each other.
        UseStops(Stop(.5f, 22));
        try
        {
            var go = Generate(id, PathData(id), Straight());
            float offset = ExpectedOffset(id), width = PlatformWidth(id), inner = offset + 1.35f, outer = inner + width;
            float rail = id == 431 ? .475f : .125f;
            var model = ModelMesh(id, "lrt_platform").bounds;
            var decks = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/lrt_platform")).ToArray();
            Check(decks.Length == 2, "platform decks " + id);
            foreach (var deck in decks)
            {
                var b = deck.bounds;
                float near = Mathf.Min(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x)), far = Mathf.Max(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x));
                Check(Mathf.Abs(near - (offset + model.min.x)) < .02f && Mathf.Abs(far - (offset + model.max.x)) < .02f, $"platform lateral {id}: {b}");
                Check(Mathf.Abs(b.min.z - 49) < .1f && Mathf.Abs(b.max.z - 71) < .1f, $"platform extent {id}: {b}");
                Check(Mathf.Abs(b.max.y - (rail + model.max.y)) < .02f && Mathf.Abs(b.min.y - (rail + model.min.y)) < .02f, $"platform height {id}: {b}");
            }
            Check(decks.Any(d => d.bounds.center.x > 0) && decks.Any(d => d.bounds.center.x < 0), "platforms on both tracks " + id);
            var ends = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtPlatformEnd").ToArray();
            Check(ends.Length == 4, "platform ramps " + id);
            foreach (var t in ends)
            {
                bool start = t.position.z < 60;
                Check(Mathf.Abs(Mathf.Abs(t.position.x) - (inner + width / 2)) < .02f && Mathf.Abs(t.position.y - rail) < .02f, "ramp position " + id);
                Check(Mathf.Abs(t.position.z - (start ? 49 : 71)) < .05f && Vector3.Dot(t.forward, start ? Vector3.back : Vector3.forward) > .999f, "ramp faces outward " + id);
                // At the tip, the top face is at the rail running surface + 0.01 m (road surface + 0.1 m + 0.035 m) for embedded track, and at the spline surface for 431.
                float foot = RampFootY(t, id == 431 ? 6f : 3f);
                Check(Mathf.Abs(foot - (id == 431 ? 0f : rail + .01f)) < .002f, $"ramp foot height {id}: {foot}");
            }
            float back = ModelMesh(id, "lrt_shelter").bounds.max.x;
            var shelters = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtShelter").ToArray();
            Check(shelters.Length == 2, "platform shelters " + id);
            foreach (var t in shelters)
            {
                Check(Mathf.Abs(Mathf.Abs(t.position.x) - (outer - back)) < .02f && Mathf.Abs(t.position.y - (rail + .3f)) < .02f && Mathf.Abs(t.position.z - 60) < .05f, "shelter position " + id);
                Check(Vector3.Dot(t.right, new Vector3(Mathf.Sign(t.position.x), 0, 0)) > .999f && Vector3.Dot(t.up, Vector3.up) > .9999f, "shelter back to outer edge " + id);
            }
            if (id == 431)
            {
                // Move catenary poles in the platform section (deck ± 6 m for slopes + 2 m = 41–79 m) to 0.5 m beyond the platform edge; outside it, keep them 0.5 m beyond the track-bed edge.
                var poles = go.GetComponentsInChildren<Transform>().Where(t => t.name == "pole(Clone)").ToArray();
                Check(poles.Any(t => t.position.z > 41 && t.position.z < 79) && poles.Any(t => t.position.z < 41 || t.position.z > 79), "431 poles around platform");
                foreach (var t in poles)
                {
                    bool zone = t.position.z > 41 && t.position.z < 79;
                    Check(Mathf.Abs(Mathf.Abs(t.position.x) - (zone ? outer + .5f : 4f)) < .02f, $"431 pole offset z={t.position.z} x={t.position.x}");
                }
            }
            UnityEngine.Object.DestroyImmediate(go);
            PlatformAtCutEnd(id);
            // Do not render a platform on a path with no stop.
            UseStops();
            go = Generate(id, PathData(id), Straight());
            Check(!go.GetComponentsInChildren<Transform>().Any(t => t.name == "LrtPlatformEnd" || t.name.EndsWith("/lrt_platform")), "no platform without stop " + id);
            UnityEngine.Object.DestroyImmediate(go);
        }
        finally { UseStops(); }
    }
    static float RampFootY(Transform ramp, float length) =>
        ramp.GetComponent<MeshFilter>().sharedMesh.vertices.Where(v => v.z > length - .05f).Max(v => ramp.TransformPoint(v).y);
    static void PlatformAtCutEnd(int id)
    {
        // Four cars near the end of a 120 m section (stop center 102 m, deck 81–123 m). The deck extends to the clipped end at 120 m, and the endpoint-side slope extends beyond the end.
        float ramp = id == 431 ? 6f : 3f;
        UseStops(Stop(.85f, 42));
        var go = Generate(id, PathData(id), Straight());
        try
        {
            var decks = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/lrt_platform")).ToArray();
            Check(decks.Length == 2 && decks.All(d => Mathf.Abs(d.bounds.min.z - 81) < .1f && Mathf.Abs(d.bounds.max.z - 120) < .1f),
                $"platform clamped to cut end {id}: " + string.Join(", ", decks.Select(d => d.bounds.min.z + ".." + d.bounds.max.z)));
            var ends = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name == "LrtPlatformEnd").ToArray();
            Check(ends.Length == 4, "platform ramps at cut end " + id);
            foreach (var r in ends)
            {
                bool start = r.transform.position.z < 100;
                Check(Mathf.Abs(r.transform.position.z - (start ? 81 : 120)) < .05f, $"ramp at clamped deck end {id}: {r.transform.position.z}");
                if (!start) Check(Mathf.Abs(r.bounds.max.z - (120 + ramp)) < .05f, $"ramp overhangs cut end {id}: {r.bounds.max.z}");
            }
            Check(go.GetComponentsInChildren<Transform>().Count(t => t.name == "LrtShelter" && Mathf.Abs(t.position.z - 100.5f) < .05f) == 2, "shelters on clamped deck " + id);
            if (id == 431)
            {
                // Move the catenary poles at 75 m and 105 m in the platform section (from 81−6−2=73 m) to 0.5 m beyond the outer edge; keep those at 15 m and 45 m at 0.5 m beyond the track-bed edge.
                float outer = ExpectedOffset(id) + 1.35f + PlatformWidth(id);
                var poles = go.GetComponentsInChildren<Transform>().Where(t => t.name == "pole(Clone)").ToArray();
                Check(poles.Any(t => t.position.z > 73) && poles.Any(t => t.position.z < 73), "431 poles around clamped platform");
                foreach (var t in poles)
                    Check(Mathf.Abs(Mathf.Abs(t.position.x) - (t.position.z > 73 ? outer + .5f : 4f)) < .02f, $"431 pole offset at cut end z={t.position.z} x={t.position.x}");
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(go); UseStops(); }
    }
    static void PlatformFallback()
    {
        // If lrt_platform is missing, use a simple slab as a substitute, from the inner edge to the outer edge, with its top at the running surface + 0.30 m.
        const int id = 242;
        Master(id);
        string key = $"Models/Paths/p{id}/lrt_platform";
        var model = models[key];
        // An empty registration means “no model” (do not let Resources pick up the wrapper prefab exported by the previous run).
        models[key] = null;
        UseStops(Stop(.5f, 22));
        try
        {
            var go = Generate(id, PathData(id), Straight());
            var slabs = go.GetComponentsInChildren<MeshFilter>().Where(f => f.name == "LrtPlatformSlab").ToArray();
            Check(slabs.Length == 2, "fallback slabs");
            float inner = ExpectedOffset(id) + 1.35f;
            foreach (var f in slabs)
            {
                var b = f.GetComponent<Renderer>().bounds;
                float near = Mathf.Min(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x)), far = Mathf.Max(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x));
                Check(Mathf.Abs(near - inner) < .02f && Mathf.Abs(far - (inner + 1.2f)) < .02f, "fallback slab lateral");
                Check(Mathf.Abs(b.max.y - .425f) < .02f && Mathf.Abs(b.min.y + .3f) < .02f && Mathf.Abs(b.min.z - 49) < .1f && Mathf.Abs(b.max.z - 71) < .1f, $"fallback slab extent {b}");
                // The winding order of the top-face triangles produces upward-facing normals.
                var mesh = f.sharedMesh; var v = mesh.vertices; var tri = mesh.triangles;
                bool upward = false, inverted = false;
                for (int i = 0; i < tri.Length; i += 3)
                {
                    Vector3 a = v[tri[i]], c = v[tri[i + 2]], n = Vector3.Cross(v[tri[i + 1]] - a, c - a).normalized;
                    if (Mathf.Abs(a.y - .425f) > .01f || Mathf.Abs(c.y - .425f) > .01f || Mathf.Abs(v[tri[i + 1]].y - .425f) > .01f) continue;
                    upward |= n.y > .99f; inverted |= n.y < -.99f;
                }
                Check(upward && !inverted, "fallback slab winding");
            }
            UnityEngine.Object.DestroyImmediate(go);
        }
        finally { models[key] = model; UseStops(); }
    }
    static void ElevatedPlatformWalls()
    {
        // For elevated 431, omit the guardrails on both sides of the platform section (41–79 m) and keep them as before outside that section.
        Master(431);
        Set(masters[431], "wall_width", .3f);
        UseStops(Stop(.5f, 22));
        try
        {
            var go = Generate(431, PathData(431), Straight(10f));
            var walls = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/wall")).ToArray();
            Check(walls.Any(w => w.bounds.max.z < 42f) && walls.Any(w => w.bounds.min.z > 78f), "elevated walls outside platform");
            Check(walls.All(w => w.bounds.max.z < 41.5f || w.bounds.min.z > 78.5f), "elevated walls omitted at platform");
            Check(go.GetComponentsInChildren<MeshRenderer>().Count(r => r.name.EndsWith("/lrt_platform")) == 2, "elevated platforms");
            UnityEngine.Object.DestroyImmediate(go);
            // With the deck extended to the clipped end (81–120 m), the section without guardrails also extends from 73 m to the endpoint.
            UseStops(Stop(.85f, 42));
            go = Generate(431, PathData(431), Straight(10f));
            walls = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/wall")).ToArray();
            Check(walls.Length > 0 && walls.All(w => w.bounds.max.z < 73.5f), "elevated walls omitted up to cut end");
            UnityEngine.Object.DestroyImmediate(go);
        }
        finally { UseStops(); Master(431); }
    }
    static object SpanOf(float from, float to, bool rampFrom = true, bool rampTo = true)
    {
        var span = Activator.CreateInstance(asm.GetType("LrtPlatformParts+Span"));
        Set(span, "From", from); Set(span, "To", to); Set(span, "RampAtFrom", rampFrom); Set(span, "RampAtTo", rampTo);
        return span;
    }
    static IList Clip(object span, List<Vector2> blocked, float ramp) =>
        (IList)PlatformParts.GetMethod("Clip").Invoke(null, new object[] { span, blocked, ramp });
    static void PlatformClip()
    {
        // Do not place slopes at ends clipped by other tracks, or at actual ends where the slope's running surface is blocked. Discard remaining fragments shorter than 4 m after these exclusions.
        var span = SpanOf(10, 40);
        ExpectSpan(Clip(span, new List<Vector2> { new Vector2(float.NegativeInfinity, 15) }, 6), 0, 15, 40, false, true, "clip start");
        var pieces = Clip(span, new List<Vector2> { new Vector2(20, 25) }, 6);
        Check(pieces.Count == 2, "clip middle");
        ExpectSpan(pieces, 0, 10, 20, true, false, "clip middle first"); ExpectSpan(pieces, 1, 25, 40, false, true, "clip middle second");
        ExpectSpan(Clip(span, new List<Vector2> { new Vector2(43, float.PositiveInfinity) }, 6), 0, 10, 40, true, false, "ramp footprint blocked");
        ExpectSpan(Clip(span, new List<Vector2> { new Vector2(47, float.PositiveInfinity) }, 6), 0, 10, 40, true, true, "ramp footprint clear");
        Check(Clip(span, new List<Vector2> { new Vector2(12, 38) }, 6).Count == 0, "clipped pieces too short");
        // If no sections are excluded, return sections shorter than 4 m that were clipped by tunnel-rendering sections unchanged.
        ExpectSpan(Clip(SpanOf(10, 13, false, true), null, 6), 0, 10, 13, false, true, "clip keeps short run piece");
    }
    // A polyline used to verify a road with a corner (turn 30° right over 0→60 m, then continue for another 60 m). CornerPoint maps distance to point; CornerAlong maps point to distance.
    static readonly Vector3 CornerDirection = new Vector3(Mathf.Sin(30f * Mathf.Deg2Rad), 0, Mathf.Cos(30f * Mathf.Deg2Rad));
    static Vector3 CornerPoint(float distance) => distance <= 60 ? new Vector3(0, 0, distance) : new Vector3(0, 0, 60) + CornerDirection * (distance - 60);
    static float CornerAlong(Vector3 p)
    {
        var flat = new Vector3(p.x, 0, p.z);
        float first = Mathf.Clamp(flat.z, 0, 60), second = Mathf.Clamp(Vector3.Dot(flat - CornerPoint(60), CornerDirection), 0, 60);
        return (flat - CornerPoint(first)).sqrMagnitude <= (flat - CornerPoint(60 + second)).sqrMagnitude ? first : 60 + second;
    }
    static void CornerPlatforms(int id)
    {
        // With curved=false, four cars crossing a corner on a road with a bend (stop center 66 m, deck 45–87 m) are not clipped at the corner; each side has one deck,
        // with slopes only at the actual ends and one canopy on each side. Place point equipment for 303 only outside the platform section calculated over the entire path (41–91 m)
        // (also avoid the section mapped to distance from the start of the segment after the corner).
        Master(id);
        var path = PathData(id);
        string[] fields = { "streetlight_type", "street_tree_type", "bench_type", "bin_type", "planter_type", "advert_type" };
        if (id == 303)
        {
            Set(masters[id], "tree_spacing", 24f);
            foreach (string f in fields) { Set(masters[id], f + "_allowed", true); Set(path, f, 1); }
        }
        Vector3 end = CornerPoint(120);
        Set(path, "curved", false);
        Set(path, "end_point", new List<object> { end.x, 0f, end.z });
        Set(path, "waypoints", new List<object> { new Dictionary<string, object> { { "position", new List<object> { 0f, 0f, 60f } } } });
        UseStops(Stop(.55f, 42));
        try
        {
            var go = Generate(id, path, new List<Vector3> { Vector3.zero, CornerPoint(60), end });
            var corner = go.transform.Find("Corner_0");
            Check(corner != null, "corner surface " + id);
            var cornerParts = corner.GetComponentsInChildren<MeshFilter>();
            foreach (string part in new[] { "/lrt_track", "/lrt_wire" })
            {
                var connectors = cornerParts.Where(f => f.name.EndsWith(part)).ToArray();
                Check(connectors.Length == 2 && connectors.All(f => f.sharedMesh != null && f.sharedMesh.vertexCount > 0),
                    $"corner double track continuity {id} {part}");
            }
            float rail = .125f, lateral = ExpectedOffset(id) + 1.35f + PlatformWidth(id) / 2;
            var decks = go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/lrt_platform")).ToArray();
            Check(decks.Length == 2 && decks.All(d => d.bounds.min.z < 45.5f && d.bounds.max.z > 80f),
                $"corner platform continuous {id}: " + string.Join(", ", decks.Select(d => d.bounds.ToString())));
            var ends = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtPlatformEnd").ToArray();
            Check(ends.Length == 4, $"corner platform ramps {id}: {ends.Length}");
            foreach (float distance in new[] { 45f, 87f })
            {
                Vector3 forward = distance < 60 ? Vector3.forward : CornerDirection, right = Vector3.Cross(Vector3.up, forward);
                foreach (int side in new[] { -1, 1 })
                {
                    Vector3 expected = CornerPoint(distance) + right * (side * lateral) + Vector3.up * rail;
                    Check(ends.Count(t => (t.position - expected).magnitude < .05f) == 1,
                        $"corner ramp at {distance} side {side} {id}: expected {expected}, got " + string.Join(", ", ends.Select(t => t.position.ToString("F3"))));
                }
            }
            var shelters = go.GetComponentsInChildren<Transform>().Where(t => t.name == "LrtShelter").ToArray();
            Check(shelters.Length == 2 && shelters.All(t => Mathf.Abs(CornerAlong(t.position) - 66) < .05f), $"corner platform shelters {id}: {shelters.Length}");
            if (id == 303)
            {
                string[] equipment = { "bench", "bin", "planter", "advert", "tree", "streetlight" };
                var placed = go.GetComponentsInChildren<Transform>().Where(t => equipment.Any(e => t.name.StartsWith(e + "("))).ToArray();
                Check(placed.Any(t => CornerAlong(t.position) < 41) && placed.Any(t => CornerAlong(t.position) > 91), "corner mall equipment outside platform");
                foreach (var t in placed)
                    Check(CornerAlong(t.position) < 41.05f || CornerAlong(t.position) > 90.95f, $"corner mall equipment kept clear: {t.name} at {CornerAlong(t.position)}");
            }
            UnityEngine.Object.DestroyImmediate(go);
        }
        finally { UseStops(); Master(id); }
    }
    // Adjacent track. TrackAdjacency.NeighborProvider returns the configured counterpart unchanged.
    static readonly List<object> trackNeighbors = new List<object>();
    static List<T> TrackNeighbors<TPath, T>(TPath path) => trackNeighbors.Cast<T>().ToList();
    static void UseTrackNeighbors(params object[] items)
    {
        trackNeighbors.Clear(); trackNeighbors.AddRange(items);
        var provider = asm.GetType("TrackAdjacency").GetField("NeighborProvider");
        provider.SetValue(null, items.Length == 0 ? null : Delegate.CreateDelegate(provider.FieldType,
            typeof(LrtQA).GetMethod("TrackNeighbors", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(asm.GetType("GeneratedModels.PathSummary"), asm.GetType("TrackNeighborInfo"))));
    }
    static object TrackNeighbor(int id, List<Vector3> points, List<Vector2> zones = null)
    {
        var info = Activator.CreateInstance(asm.GetType("TrackNeighborInfo"));
        Set(info, "PathId", id); Set(info, "Points", points); Set(info, "Width", 7f);
        if (zones != null) { Set(info, "LrtPlatformZones", zones); Set(info, "LrtPlatformOuter", ExpectedOffset(431) + 1.35f + PlatformWidth(431)); }
        return info;
    }
    static void NeighborPlatforms()
    {
        Master(431);
        float outer = ExpectedOffset(431) + 1.35f + PlatformWidth(431);
        List<Vector3> Offset(float x) => Straight().Select(p => p + new Vector3(x, 0, 0)).ToList();
        MeshRenderer[] Decks(GameObject go) => go.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.EndsWith("/lrt_platform")).ToArray();
        Transform[] Named(GameObject go, string name) => go.GetComponentsInChildren<Transform>().Where(t => t.name == name).ToArray();
        UseStops(Stop(.5f, 22));
        try
        {
            // On the 431 side running alongside with a 1 m gap between track beds, do not render a platform because the track bed is closer than 0.3 m beyond the platform edge.
            // The other side is as usual (deck 49–71 m, two slopes, one canopy). The corridor's main track (id=0) places the poles.
            UseTrackNeighbors(TrackNeighbor(0, Offset(-8)));
            var go = Generate(431, PathData(431), Straight());
            var decks = Decks(go);
            Check(decks.Length == 1 && decks[0].bounds.center.x > 0 && Mathf.Abs(decks[0].bounds.min.z - 49) < .1f && Mathf.Abs(decks[0].bounds.max.z - 71) < .1f,
                "platform omitted beside parallel track: " + string.Join(", ", decks.Select(d => d.bounds.ToString())));
            Check(Named(go, "LrtPlatformEnd").Length == 2 && Named(go, "LrtPlatformEnd").All(t => t.position.x > 0), "ramps only on free side");
            Check(Named(go, "LrtShelter").Length == 1 && Named(go, "LrtShelter")[0].position.x > 0, "shelter only on free side");
            Check(Named(go, "pole(Clone)").Length == 0, "coupled non-primary places no poles");
            UnityEngine.Object.DestroyImmediate(go);
            // With a 3 m gap, the other track bed is farther than 0.3 m beyond the platform edge, so render platforms on both sides.
            UseTrackNeighbors(TrackNeighbor(0, Offset(-10)));
            go = Generate(431, PathData(431), Straight());
            Check(Decks(go).Length == 2, "platforms kept beside distant parallel track");
            UnityEngine.Object.DestroyImmediate(go);
            // In the other track's platform section (41–79 m), the main track (id=0) places poles 0.5 m beyond the other platform's outer edge; outside that section, it places them 0.5 m beyond the other track bed's edge.
            UseStops();
            UseTrackNeighbors(TrackNeighbor(1, Offset(8), new List<Vector2> { new Vector2(41, 79) }));
            go = Generate(431, PathData(431, 0), Straight());
            var poles = Named(go, "pole(Clone)");
            Check(poles.Any(t => t.position.z > 41 && t.position.z < 79) && poles.Any(t => t.position.z < 41 || t.position.z > 79), "primary poles around neighbor platform");
            foreach (var t in poles)
            {
                bool zone = t.position.z > 41 && t.position.z < 79;
                float expected = t.position.x > 0 ? 8 + (zone ? outer : 3.5f) + .5f : -4f;
                Check(Mathf.Abs(t.position.x - expected) < .02f, $"primary pole clears neighbor platform z={t.position.z} x={t.position.x} expected {expected}");
            }
            UnityEngine.Object.DestroyImmediate(go);
            // On the 431 side diverging at 15° from a 7.5 m center spacing at the start, render it except where the track bed overlaps the platform band (up to 7.5 m),
            // and do not place a slope at the excluded end. On the other side, place slopes at both ends of the 1–23 m deck.
            UseStops(Stop(.1f, 22));
            Vector3 branch = new Vector3(Mathf.Sin(15f * Mathf.Deg2Rad), 0, Mathf.Cos(15f * Mathf.Deg2Rad));
            UseTrackNeighbors(TrackNeighbor(0, Enumerable.Range(0, 25).Select(i => new Vector3(7.5f, 0, 0) + branch * (i * 5)).ToList()));
            go = Generate(431, PathData(431), Straight());
            decks = Decks(go);
            Check(decks.Length == 2 && decks.Any(d => d.bounds.center.x > 0 && Mathf.Abs(d.bounds.min.z - 7.5f) < .1f && Mathf.Abs(d.bounds.max.z - 23) < .1f)
                && decks.Any(d => d.bounds.center.x < 0 && Mathf.Abs(d.bounds.min.z - 1) < .1f && Mathf.Abs(d.bounds.max.z - 23) < .1f),
                "platform trimmed beside diverging track: " + string.Join(", ", decks.Select(d => d.bounds.ToString())));
            var ends = Named(go, "LrtPlatformEnd");
            Check(ends.Length == 3 && ends.Count(t => t.position.x > 0) == 1 && ends.Where(t => t.position.x > 0).All(t => Mathf.Abs(t.position.z - 23) < .05f),
                "no ramp where diverging track trims platform: " + string.Join(", ", ends.Select(t => t.position.ToString())));
            UnityEngine.Object.DestroyImmediate(go);
        }
        finally { UseStops(); UseTrackNeighbors(); }
    }
    static void ExportPrefabs()
    {
        foreach (int id in new[] { 222, 242, 303, 304, 305, 431 })
            foreach (string file in Directory.GetFiles("Assets/Paths/p" + id, "*.fbx"))
            {
                string asset = file.Replace('\\', '/');
                string relative = "Paths/p" + id + "/" + Path.GetFileName(file);
                string fbx = root + "/Assets/Models/" + relative;
                string guid = AssetDatabase.AssetPathToGUID(asset);
                if (File.Exists(fbx + ".meta")) Check(File.ReadAllText(fbx + ".meta").Contains("guid: " + guid), "FBX GUID " + relative);
                else File.Copy(asset + ".meta", fbx + ".meta");
                string prefab = "Assets/Resources/Models/" + Path.ChangeExtension(relative, ".prefab");
                Check(ModelPrefabGenerator.CreateWrapperPrefab(asset, prefab), "prefab " + relative);
                string output = root + "/" + prefab;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                if (File.Exists(output)) Check(File.ReadAllText(output).Contains("guid: " + guid), "prefab GUID " + relative);
                else { File.Copy(prefab, output); File.Copy(prefab + ".meta", output + ".meta"); }
            }
    }
}
