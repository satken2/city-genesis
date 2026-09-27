using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 独立したUnityへ本編アセンブリを読み込み、IPC受信と編集入力の接点を実行する。
[InitializeOnLoad]
public static class TutorialQA
{
    private static Assembly source;
    private static int checks;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    private static object Static(string method, params object[] args) => source.GetType("TutorialRuntime").GetMethod(method).Invoke(null, args);
    private static bool Property(string property) => (bool)source.GetType("TutorialRuntime").GetProperty(property).GetValue(null);
    private static void Check(bool pass, string label) { checks++; if (!pass) throw new Exception(label); }
    private static void Set(object target, string key, object value) => target.GetType().GetField(key, Flags).SetValue(target, value);
    private static object Get(object target, string key) => target.GetType().GetField(key, Flags).GetValue(target);
    private static void Apply(string run, int revision, bool active = true, string mode = "path")
    {
        Static("ApplyFromIpc", new Dictionary<string, object> {
            ["active"] = active, ["runId"] = run, ["revision"] = revision,
            ["world"] = new Dictionary<string, object> {
                ["mode"] = mode,
                ["path"] = new Dictionary<string, object> {
                    ["points"] = new[] { new[] { -100f, 0f, 0f }, new[] { 100f, 0f, 0f } },
                    ["radius"] = 12, ["variant"] = 221
                }
            }
        });
    }
    private static void ApplyCamera(string run, int revision, float pitch)
    {
        Static("ApplyFromIpc", new Dictionary<string, object> {
            ["active"] = true, ["runId"] = run, ["revision"] = revision,
            ["world"] = new Dictionary<string, object> {
                ["mode"] = "blocked",
                ["camera"] = new Dictionary<string, object> { ["target"] = new[] { 0f, 0f, 0f }, ["distance"] = 300f, ["yaw"] = 0f, ["pitch"] = pitch }
            }
        });
    }
    static TutorialQA() { if (SessionState.GetBool("TutorialQA.Pending", false)) EditorApplication.delayCall += Run; }
    public static void Run()
    {
        if (!EditorApplication.isPlaying) { SessionState.SetBool("TutorialQA.Pending", true); EditorApplication.EnterPlaymode(); return; }
        SessionState.SetBool("TutorialQA.Pending", false);
        var args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        string report = root + "/Temp/TutorialQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" }) {
                    string path = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            source = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            Check(!Property("PathRestricted"), "normal session starts unrestricted");
            Check((bool)Static("AllowRoadClick", Vector3.zero, 5, true, 1, true), "normal input unchanged");
            Apply("one", 1, mode: "blocked");
            Check(Property("BlocksWorldInput"), "blocked world directive applied");
            Check(!(bool)Static("AllowRoadClick", new Vector3(-100, 0, 0), 0, false, 221, true), "click blocked before path phase");
            Apply("one", 2);
            Check(!Property("BlocksWorldInput") && Property("PathRestricted"), "path phase releases click gate");
            Check(!(bool)Static("AllowRoadClick", Vector3.zero, 0, false, 221, true), "outside marker rejected");
            Check(!(bool)Static("AllowRoadClick", new Vector3(-100, 0, 0), 0, false, 201, true), "wrong road variant rejected");
            Check(!(bool)Static("AllowRoadClick", new Vector3(-100, 0, 0), 0, true, 221, true), "right click cannot start");
            object[] snapArgs = { new Vector3(-96, 0, 3), 0, Vector3.zero };
            Check((bool)Static("TrySnap", snapArgs), "nearby click snaps");
            var snapped = (Vector3)snapArgs[2];
            Check(snapped.x == -100 && snapped.z == 0, "snap uses exact anchor");
            var road = new GameObject("RoadQA").AddComponent(source.GetType("RoadPlanManager"));
            ((Behaviour)road).enabled = false;
            Set(road, "phase", Enum.Parse(source.GetType("RoadEditorPhase"), "Placing"));
            Set(road, "PathSubtype", 221); Set(road, "PathType", 1); Set(road, "Curved", true);
            Call(road, "ApplyTutorialPathSettings");
            Check(!(bool)Get(road, "Curved"), "tutorial forces straight editor mode");
            var points = (IList)Get(road, "points");
            Call(road, "OnTerrainLeftClick", Vector3.zero);
            Check(points.Count == 0, "actual editor rejects out-of-range click");
            Call(road, "OnTerrainLeftClick", new Vector3(-96, 0, 3));
            Check(points.Count == 1, "actual editor accepts start");
            Call(road, "OnTerrainRightClick", new Vector3(96, 0, 3));
            Check(points.Count == 1 && Get(road, "phase").ToString() == "Placing", "right click cannot place the endpoint");
            Call(road, "OnTerrainLeftClick", Vector3.zero);
            Check(points.Count == 1, "out-of-range endpoint rejected");
            Call(road, "OnTerrainLeftClick", new Vector3(96, 0, 3));
            Check(points.Count == 2 && Get(road, "phase").ToString() == "Placing", "left click places endpoint before confirmation");
            Call(road, "OnTerrainLeftClick", new Vector3(100, 0, 0));
            Check(points.Count == 2, "third left-click point rejected");
            Call(road, "OnTerrainRightClick", new Vector3(500, 0, 500));
            Check(points.Count == 2, "right click away from markers confirms without moving endpoints");
            Check(Get(road, "phase").ToString() == "Editing", "actual editor enters confirmation phase");
            Check((bool)Static("AcceptsPath", points, 221, false), "saved editor points satisfy constraint");
            Call(road, "OnTerrainRightClick", new Vector3(100, 0, 0));
            Check(points.Count == 2, "editing deletion is blocked");
            var changedPoint = points[1]; Set(changedPoint, "embankmentHeight", 1f); points[1] = changedPoint;
            Check(!(bool)Static("AcceptsPath", points, 221, false), "embankment bypass rejected");
            changedPoint = points[1]; Set(changedPoint, "embankmentHeight", 0f); points[1] = changedPoint;
            Check(!(bool)Static("AcceptsPath", points, 221, true), "curved bypass rejected");
            Apply("one", 1, mode: "blocked"); Check(!Property("BlocksWorldInput"), "stale revision ignored");
            Apply("two", 1); Apply("one", 3, false); Check(Property("PathRestricted"), "old cleanup cannot clear new run");
            Static("Clear"); Check(!Property("PathRestricted"), "session cleanup releases gate");
            Apply("two", 2); Check(!Property("PathRestricted"), "retired run cannot reactivate");
            Apply("three", 1); Apply("three", 2, false); Check(!Property("PathRestricted"), "explicit exit releases gate");
            ApplyCamera("camera", 1, 45f); Check(Property("BlocksWorldInput"), "camera guidance accepted with the directive");
            ApplyCamera("camera", 2, 0f); Check(!Property("BlocksWorldInput"), "invalid camera pitch rejects the directive");
            Apply("visual", 1);
            var camera = new GameObject("Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 220, -170); camera.transform.LookAt(Vector3.zero);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.08f, .12f, .16f);
            camera.targetTexture = new RenderTexture(800, 400, 24); camera.aspect = 2f;
            var runtime = (Component)source.GetType("TutorialRuntime").GetProperty("Instance").GetValue(null);
            // 隔離プロジェクトは標準レンダーパイプラインなので、同じ頂点色を描く検証用マテリアルを使う。
            source.GetType("OverlayMeshRenderer").GetField("overlayLineShader", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, Shader.Find("Hidden/TutorialQA"));
            Call(runtime, "LateUpdate");
            Check(runtime.GetComponentInChildren<MeshFilter>().sharedMesh.vertexCount > 0, "marker mesh generated");
            Check(runtime.GetComponentsInChildren<Collider>().Length == 0, "markers create no collider or selectable node");
            camera.Render(); RenderTexture.active = camera.targetTexture;
            var image = new Texture2D(800, 400, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 800, 400), 0, 0); image.Apply();
            int colored = 0; foreach (Color pixel in image.GetPixels()) if (pixel.g > .7f) colored++;
            Check(colored > 100, "rendered image contains visible markers");
            File.WriteAllBytes(root + "/Temp/TutorialQA/markers.png", image.EncodeToPNG());
            Static("Clear"); Check(!runtime.GetComponentInChildren<MeshFilter>(true).gameObject.activeSelf, "cleanup hides marker mesh");
            File.WriteAllText(report, $"PASS: {checks} tutorial Unity checks\n");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { File.WriteAllText(report, error.ToString()); Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
