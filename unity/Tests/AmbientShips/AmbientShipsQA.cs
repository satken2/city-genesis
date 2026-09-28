using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 環境演出の船舶の実FBX(通常・接写・遠景と数値ID別名)と、本編アセンブリの描画LODの選び方を独立したUnityプロジェクトで検証する。
public static class AmbientShipsQA
{
    static readonly string[] Ships = { "ambient_container_ship", "ambient_tanker", "ambient_bulk_carrier", "ambient_cruise_ship", "ambient_fishing_boat" };
    static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
    {
        { "304", "ambient_container_ship" }, { "305", "ambient_tanker" }, { "306", "ambient_cruise_ship" }, { "307", "ambient_fishing_boat" },
    };
    static readonly string[] Lods = { "", "_close", "_far" };
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const float TanHalfFov = 0.57735f;   // 本編カメラの縦画角60°
    static int checks;
    static readonly List<string> report = new List<string>();

    static void Check(bool valid, string message)
    {
        checks++;
        if (!valid) throw new Exception(message);
    }

    static GameObject Asset(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Models/Props/{name}.fbx");

    static Mesh LoadMesh(string name)
    {
        var model = Asset(name);
        Check(model != null, "missing FBX " + name);
        var filters = model.GetComponentsInChildren<MeshFilter>();
        Check(filters.Length == 1, "expected one mesh: " + name);
        Check(model.GetComponentsInChildren<MeshRenderer>().All(r => r.sharedMaterials.Length > 0 && r.sharedMaterials.All(m => m != null)), "missing material: " + name);
        return filters[0].sharedMesh;
    }

    static int Triangles(Mesh mesh) => Enumerable.Range(0, mesh.subMeshCount).Sum(i => (int)mesh.GetIndexCount(i)) / 3;

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-shipsSourceRoot") + 1];
        string output = Path.GetFullPath(".");
        try
        {
            AssetDatabase.Refresh();
            ModelImportSettings.EnableReadWriteAll();
            var meshes = new Dictionary<string, Mesh>();
            foreach (var name in Ships.Concat(Aliases.Keys))
                foreach (var suffix in Lods)
                    meshes[name + suffix] = LoadMesh(name + suffix);
            foreach (var name in Ships) CheckShip(name, meshes);
            foreach (var pair in Aliases)
                foreach (var suffix in Lods)
                {
                    var alias = meshes[pair.Key + suffix];
                    var source = meshes[pair.Value + suffix];
                    Check(alias.vertexCount == source.vertexCount && Triangles(alias) == Triangles(source), $"alias {pair.Key}{suffix} differs from {pair.Value}{suffix}");
                    Check((alias.bounds.min - source.bounds.min).magnitude < .01f && (alias.bounds.max - source.bounds.max).magnitude < .01f, $"alias bounds {pair.Key}{suffix}");
                }
            CheckLodSelection(root);
            Render(output);
            File.WriteAllText(output + "/result.txt", $"PASS ambient ships: {checks} checks\n" + string.Join("\n", report));
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(output + "/result.txt", "FAIL " + e + "\n" + string.Join("\n", report));
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    /// <summary>原点(喫水線・水平中心)、船首の向き(Unity +Z)、接写・遠景の外形と面数の大小。</summary>
    static void CheckShip(string name, Dictionary<string, Mesh> meshes)
    {
        var normal = meshes[name];
        var close = meshes[name + "_close"];
        var far = meshes[name + "_far"];
        Bounds b = normal.bounds;
        Check(Mathf.Abs(b.center.x) < .25f && Mathf.Abs(b.center.z) < .25f, name + " horizontal center");
        Check(b.min.y < -1f && b.max.y > 5f, name + " waterline origin (hull below, superstructure above)");
        for (int axis = 0; axis < 3; axis++)
        {
            Check(Mathf.Abs(close.bounds.min[axis] - b.min[axis]) < .05f && Mathf.Abs(close.bounds.max[axis] - b.max[axis]) < .05f, $"{name}_close bounds axis {axis}");
        }
        foreach (int axis in new[] { 0, 2 })
            Check(Mathf.Abs(far.bounds.min[axis] - b.min[axis]) < .3f && Mathf.Abs(far.bounds.max[axis] - b.max[axis]) < .3f, $"{name}_far horizontal bounds axis {axis}");
        Check(Mathf.Abs(far.bounds.max.y - b.max.y) < .3f, name + "_far top");
        Check(Triangles(far) < Triangles(normal) && Triangles(normal) < Triangles(close), name + " triangle order far < normal < close");
        // 船首(+Z)は尖り、船尾(-Z)はトランサムで幅がある。
        var verts = normal.vertices;
        float slice = b.size.z * .02f;
        float bow = verts.Where(v => v.z > b.max.z - slice && v.y > .5f).Select(v => Mathf.Abs(v.x)).DefaultIfEmpty(0).Max();
        float stern = verts.Where(v => v.z < b.min.z + slice && v.y > .5f).Select(v => Mathf.Abs(v.x)).DefaultIfEmpty(0).Max();
        Check(bow < stern * .5f, $"{name} bow must face Unity +Z (bow half width {bow:0.0} / stern {stern:0.0})");
        report.Add($"{name}: size {b.size.x:0.0} x {b.size.z:0.0} m, waterline {b.min.y:0.0}..{b.max.y:0.0} m, triangles far/normal/close {Triangles(far)}/{Triangles(normal)}/{Triangles(close)}, vertices {far.vertexCount}/{normal.vertexCount}/{close.vertexCount}");
    }

    /// <summary>本編の PropRenderModel と AmbientVehicleManager.SelectLod で、近づくとき・離れるときの切り替え距離を測る。</summary>
    static void CheckLodSelection(string root)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (o, e) =>
        {
            string file = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
            {
                string path = Path.Combine(root, dir, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        var asm = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
        var modelManagerType = asm.GetType("ModelManager");
        var manager = new GameObject("Models").AddComponent(modelManagerType);
        modelManagerType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, manager);
        var cache = (IDictionary)modelManagerType.GetField("_modelCache", Any).GetValue(manager);
        foreach (var name in Ships.Append("ambient_airliner"))
            foreach (var suffix in Lods)
                if (Asset(name + suffix) != null) cache["Models/Props/" + name + suffix] = Asset(name + suffix);
        var propModelType = asm.GetType("PropRenderModel");
        var ambientType = asm.GetType("AmbientVehicleManager");
        var vehicleType = ambientType.GetNestedType("Vehicle", Any);
        var select = ambientType.GetMethod("SelectLod", Any);
        var drawLod = asm.GetType("DrawLod");
        Check(vehicleType != null && select != null && drawLod != null, "AmbientVehicleManager.SelectLod not found");
        object Lod(string n) => Enum.Parse(drawLod, n);

        foreach (var name in Ships.Append("ambient_airliner"))
        {
            var model = Activator.CreateInstance(propModelType, new object[] { name, false });
            bool dedicatedClose = (bool)propModelType.GetProperty("DedicatedClose").GetValue(model);
            bool dedicatedFar = (bool)propModelType.GetProperty("DedicatedFar").GetValue(model);
            bool ship = name != "ambient_airliner";
            Check(dedicatedClose == ship && dedicatedFar == ship, $"{name} dedicated LOD flags {dedicatedClose}/{dedicatedFar}");
            foreach (var list in new[] { "normal", "close", "far" })
                Check(((IList)propModelType.GetField(list).GetValue(model)).Count > 0, $"{name} {list} parts");
            var vehicle = Activator.CreateInstance(vehicleType, true);
            vehicleType.GetField("model", Any).SetValue(vehicle, model);
            vehicleType.GetField("lod", Any).SetValue(vehicle, Lod("Normal"));

            // カメラが8km先から近づき、また離れる。各距離で前フレームのLODを引き継ぐ(昇格と降格のしきい値の差を測る)。
            // 船は遠景から、専用モデルの無い航空機は生成時と同じ Normal から始める。
            var entered = new Dictionary<string, float>();
            var left = new Dictionary<string, float>();
            string previous = ship ? "Far" : "Normal";
            vehicleType.GetField("lod", Any).SetValue(vehicle, Lod(previous));
            for (float d = 8000f; d >= 20f; d -= 2f) previous = Step(select, vehicle, vehicleType, d, previous, entered);
            for (float d = 20f; d <= 8000f; d += 2f) previous = Step(select, vehicle, vehicleType, d, previous, left);
            if (!ship)
            {
                Check(entered.Count == 0 && left.Count == 0 && previous == "Normal", "aircraft without dedicated LOD must stay Normal");
                report.Add($"{name}: no dedicated LOD, always Normal");
                continue;
            }
            Check(entered.ContainsKey("Normal") && entered.ContainsKey("Close") && left.ContainsKey("Normal") && left.ContainsKey("Far"), $"{name} LOD transitions {string.Join(",", entered.Keys)} / {string.Join(",", left.Keys)}");
            Check(entered["Close"] <= 150f + 1e-3f, $"{name} close must stay within 150 m ({entered["Close"]})");
            Check(left["Normal"] > entered["Close"] && left["Far"] > entered["Normal"], $"{name} hysteresis");
            report.Add($"{name}: approach Far->Normal {entered["Normal"]:0} m, Normal->Close {entered["Close"]:0} m; leave Close->Normal {left["Normal"]:0} m, Normal->Far {left["Far"]:0} m");
        }
    }

    static string Step(MethodInfo select, object vehicle, Type vehicleType, float distance, string previous, Dictionary<string, float> changes)
    {
        var lod = select.Invoke(null, new object[] { vehicle, distance, TanHalfFov, false });
        vehicleType.GetField("lod", Any).SetValue(vehicle, lod);
        string now = lod.ToString();
        if (now != previous && !changes.ContainsKey(now)) changes[now] = distance;
        return now;
    }

    /// <summary>通常・接写・遠景を横に並べ、海面(Y=0の不透明な板)を張って斜め上から撮る。シェーダーエラーの紫が無いことも見る。</summary>
    static void Render(string output)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(48, -35, 0);
        RenderSettings.ambientLight = new Color(.55f, .6f, .68f);
        var sea = GameObject.CreatePrimitive(PrimitiveType.Plane);
        sea.transform.localScale = Vector3.one * 400;
        var seaMat = new Material(Shader.Find("Standard")) { color = new Color(.05f, .16f, .26f) };
        seaMat.SetFloat("_Glossiness", .7f);
        sea.GetComponent<Renderer>().sharedMaterial = seaMat;
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.62f, .72f, .83f);
        camera.fieldOfView = 60;
        camera.farClipPlane = 5000;
        foreach (var name in Ships)
        {
            var placed = new List<GameObject>();
            float x = 0;
            foreach (var suffix in Lods)
            {
                var instance = (GameObject)UnityEngine.Object.Instantiate(Asset(name + suffix));
                var size = instance.GetComponentInChildren<MeshFilter>().sharedMesh.bounds.size;
                instance.transform.position = new Vector3(x, 0, 0);
                x += size.x * 1.8f;
                placed.Add(instance);
            }
            var bounds = new Bounds(placed[1].transform.position, Vector3.zero);
            foreach (var r in placed.SelectMany(p => p.GetComponentsInChildren<Renderer>())) bounds.Encapsulate(r.bounds);
            float radius = bounds.extents.magnitude;
            Shoot(camera, output + $"/{name}_lods.png", bounds.center + new Vector3(1.0f, .8f, 1.15f).normalized * radius * 1.9f, bounds.center);
            var closeBounds = placed[1].GetComponentInChildren<Renderer>().bounds;
            var bowTarget = new Vector3(closeBounds.center.x, closeBounds.max.y * .3f, closeBounds.max.z - closeBounds.size.z * .12f);
            Shoot(camera, output + $"/{name}_close_bow.png", bowTarget + new Vector3(1.0f, .55f, .9f).normalized * closeBounds.size.z * .3f, bowTarget);
            var aftTarget = new Vector3(closeBounds.center.x, closeBounds.max.y * .45f, closeBounds.min.z + closeBounds.size.z * .22f);
            Shoot(camera, output + $"/{name}_close_aft.png", aftTarget + new Vector3(1.0f, .45f, .7f).normalized * closeBounds.size.z * .32f, aftTarget);
            foreach (var p in placed) UnityEngine.Object.DestroyImmediate(p);
        }
    }

    static void Shoot(Camera camera, string path, Vector3 position, Vector3 target)
    {
        camera.transform.position = position;
        camera.transform.LookAt(target);
        var rt = new RenderTexture(1600, 900, 24);
        camera.targetTexture = rt;
        camera.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        int magenta = tex.GetPixels32().Count(c => c.r > 230 && c.g < 30 && c.b > 230);
        Check(magenta == 0, $"shader error pixels in {Path.GetFileName(path)}: {magenta}");
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = null;
        camera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(tex);
    }
}
