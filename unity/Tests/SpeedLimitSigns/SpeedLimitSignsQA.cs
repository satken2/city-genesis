using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class SpeedLimitSignsQA
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static Assembly runtime;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Private | BindingFlags.Public).Invoke(value, args);
    static object Field(object value, string name) => value.GetType().GetField(name, Private).GetValue(value);
    static Component Add(string type) => new GameObject(type).AddComponent(runtime.GetType(type));
    static Func<T, double, double, GameObject> SegmentFactory<T>(GameObject parent) => (lod, from, to) => {
        var shown = new GameObject("DetailSegment");
        shown.transform.SetParent(parent.transform, false);
        return shown;
    };

    public static void Run()
    {
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.EnterPlaymode();
    }

    private static void OnPlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        RunChecks();
    }

    private static void RunChecks()
    {
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        string output = root + "/Temp/SpeedLimitSignsQA";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (o, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string file = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            runtime = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            var dataType = runtime.GetType("GeneratedModels.PathSummary");
            var overlayType = runtime.GetType("PathSpeedLimitOverlay");
            var constants = runtime.GetType("Constants");
            int Constant(string name) => (int)constants.GetField(name).GetValue(null);
            object data = Activator.CreateInstance(dataType);
            void Set(string name, object value) => dataType.GetField(name).SetValue(data, value);
            bool Accept(object speed, string type)
            {
                Set("max_speed", speed); Set("path_type", Constant(type));
                object[] values = { data, 0f };
                bool accepted = (bool)overlayType.GetMethod("TryGetSpeedLimit").Invoke(null, values);
                if (accepted) Check((float)values[1] == Convert.ToSingle(speed), "configured speed changed");
                return accepted;
            }
            foreach (string type in new[] { "PATH_STREET", "PATH_MOTORWAY", "PATH_TRACK", "PATH_MONORAIL" })
                Check(Accept(73.5d, type), "physical path rejected: " + type);
            foreach (string type in new[] { "PATH_TRANSPORT", "PATH_WALKWAY", "PATH_ELECTRICITY", "PATH_STATION_HOUSE" })
                Check(!Accept(50f, type), "nonphysical speed included: " + type);
            foreach (object speed in new object[] { null, 0f, -1f, float.NaN, float.PositiveInfinity })
                Check(!Accept(speed, "PATH_STREET"), "invalid speed accepted");
            Check(Accept(50L, "PATH_STREET"), "JSON integer rejected");
            var parse = runtime.GetType("IpcViewCoordinator").GetMethod("ParseFlags", BindingFlags.Static | BindingFlags.NonPublic);
            Check((int)parse.Invoke(null, new object[] { "plan,speed_limit" }) == 5, "independent flag contract");

            var manager = Add("PathManager");
            var overlay = manager.GetComponent(overlayType) ?? manager.gameObject.AddComponent(overlayType);
            Call(overlay, "Awake");
            Set("path_id", 7); Set("center_point", new List<object> { 0d, 0d });
            Set("start_point", new List<object> { 0d, 12d, -10d });
            Set("end_point", new List<object> { 0d, 16d, 10d });
            ((IDictionary)Field(manager, "pathDataCache"))[7] = data;
            ((IDictionary)Field(manager, "pathPointsCache"))[7] = new List<Vector3> { new Vector3(0, 12, -10), new Vector3(0, 16, 10) };
            ((IDictionary)Field(manager, "pathIdsByCell"))[Vector2Int.zero] = new HashSet<int> { 7 };
            object[] center = { 7, Vector3.zero };
            Check((bool)Call(manager, "TryGetPathLabelPosition", center), "center missing");
            Check(Vector3.Distance((Vector3)center[1], new Vector3(0, 14.1f, 0)) < .001f, "bridge/slope center lost");

            var pathObject = new GameObject("Path_7");
            var lodType = runtime.GetType("DrawLod");
            object Lod(int value) => Enum.ToObject(lodType, value);
            var lodInfo = pathObject.AddComponent(runtime.GetType("DrawLodInfo"));
            void SetLod(int value) => lodInfo.GetType().GetField("drawLod").SetValue(lodInfo, Lod(value));
            var visual = pathObject.AddComponent(runtime.GetType("VisualModeObject"));
            Call(visual, "SetRenderersEnabled", true);
            ((IDictionary)Field(manager, "pathObjectsCache"))[7] = pathObject;
            var state = Add("GameStateManager");
            state.GetType().GetProperty("Instance").SetValue(null, state);
            state.GetType().GetProperty("IsWorldVisible").SetValue(state, true);
            var modes = Add("VisualModeHandler");
            modes.GetType().GetProperty("Instance").SetValue(null, modes);
            int pathType = (int)runtime.GetType("InputObjectType").GetField("PATH").GetValue(null);
            var flags = (IDictionary)Field(modes, "objectTypeFlags");
            var modeValues = (IDictionary)Field(modes, "objectTypeModes");
            flags[pathType] = 5;
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 80, -100);
            camera.transform.LookAt(new Vector3(0, 14, 0));
            Call(overlay, "LateUpdate");
            var canvas = (Canvas)Field(overlay, "canvas");
            var entries = (IDictionary)Field(overlay, "entries");
            Check(entries.Count == 1 && canvas.gameObject.activeSelf, "overlay did not start");
            var sign = canvas.GetComponentInChildren<Graphic>();
            Check(sign != null && !sign.raycastTarget, "sign blocks input");
            Vector3 anchor = sign.rectTransform.anchoredPosition;
            Call(overlay, "LateUpdate");
            Check(Vector3.Distance(anchor, sign.rectTransform.anchoredPosition) < .001f, "sign animation/movement");
            Set("max_speed", 73.5d); Call(overlay, "Refresh");
            Check(sign.GetComponentInChildren<Text>().text == "73.5", "same-version speed update ignored");
            modeValues[pathType] = 100; Call(overlay, "LateUpdate");
            Check(sign.gameObject.activeSelf, "color mode cannot coexist");
            flags[pathType] = 1; Call(overlay, "LateUpdate");
            Check(!canvas.gameObject.activeSelf, "toggle off failed");
            flags[pathType] = 5; modeValues[pathType] = 1; Call(overlay, "LateUpdate");
            Check(!canvas.gameObject.activeSelf, "hidden road shows signs");
            modeValues[pathType] = 0; Call(overlay, "LateUpdate");
            Call(visual, "SetRenderersEnabled", false); Call(overlay, "LateUpdate");
            Check(!sign.gameObject.activeSelf, "plan/visibility result ignored");
            Call(visual, "SetRenderersEnabled", true); Call(overlay, "LateUpdate");
            foreach (int lod in new[] { 1, 2, 3, 0, 2 })
            {
                SetLod(lod); Call(overlay, "LateUpdate");
                Check(sign.gameObject.activeSelf == (lod >= 2), "root LOD visibility: " + lod);
            }

            // 親がNormalでも中心区間だけFarになる長い道路を、実際の区間ワーカーで切り替える。
            var longRoad = new GameObject("LongPath_7");
            longRoad.AddComponent(runtime.GetType("DrawLodInfo"));
            var longVisual = longRoad.AddComponent(runtime.GetType("VisualModeObject"));
            Call(longVisual, "SetRenderersEnabled", true);
            var spline = runtime.GetType("RoadPath").GetMethod("BuildSpline").Invoke(null, new object[] {
                longRoad, new List<Vector3> { new Vector3(0,14,-1024), new Vector3(0,14,0), new Vector3(0,14,1024) }, false });
            var part = longRoad.AddComponent(runtime.GetType("SplinePartLod"));
            var factory = typeof(SpeedLimitSignsQA).GetMethod("SegmentFactory", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(lodType).Invoke(null, new object[] { longRoad });
            Call(part, "Configure", spline, 8f, 0f, 0f, true, null, 0d, 1d, factory);
            Call(part, "OnDisable");
            var cache = Activator.CreateInstance(runtime.GetType("RenderObjectCache"), 8L * 1024 * 1024);
            runtime.GetType("PathPresentation").GetMethod("Replace").Invoke(null, new object[] { longRoad, pathObject, cache });
            ((IDictionary)Field(manager, "pathObjectsCache"))[7] = longRoad;
            Call(overlay, "LateUpdate");
            Check(!sign.gameObject.activeSelf, "pending replacement shows sign");
            camera.farClipPlane = 5000;
            void MoveAlongRoad(Vector3 position)
            {
                camera.transform.position = position;
                camera.transform.LookAt(new Vector3(0,14,0));
                var worker = (IEnumerator)Call(part, "Worker");
                for (int i = 0; i < 40; i++) Check(worker.MoveNext(), "road worker stopped");
                ((IDisposable)worker).Dispose();
                Call(overlay, "LateUpdate");
            }
            MoveAlongRoad(new Vector3(0,80,-100));
            Check(sign.gameObject.activeSelf, "ready center detail hidden");
            MoveAlongRoad(new Vector3(0,80,-1000));
            Check(!sign.gameObject.activeSelf, "far center visible while road end is close");
            Check((int)Call(part, "GetDrawLodAtPosition", new Vector3(0,14,-1000)) == 3, "near end must remain close");
            MoveAlongRoad(new Vector3(0,80,-100));
            Check(sign.gameObject.activeSelf, "center detail did not reappear");
            MoveAlongRoad(new Vector3(0,2000,-100));
            Check(!sign.gameObject.activeSelf, "distant road sign remains");
            MoveAlongRoad(new Vector3(0,80,-100));
            Check(sign.gameObject.activeSelf, "returned road sign missing");
            ((IDictionary)Field(manager, "pathDataCache")).Remove(7); Call(overlay, "LateUpdate");
            Check(!sign.gameObject.activeSelf, "deleted road sign remains");
            Call(overlay, "Clear");
            Check(entries.Count == 0 && !canvas.gameObject.activeSelf, "session clear failed");

            // 本編の標識コンポーネントを専用Canvasで撮影し、二桁・三桁・小数と最小表示サイズを確認する。
            var target = new RenderTexture(900, 280, 24);
            camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.12f, .17f, .22f);
            var preview = new GameObject("Preview").AddComponent<Canvas>();
            preview.renderMode = RenderMode.ScreenSpaceCamera;
            preview.worldCamera = camera; preview.planeDistance = 1;
            var signType = runtime.GetType("SpeedLimitSign");
            float[] speeds = { 30, 50, 120, 300, 50.5f, 120 };
            for (int i = 0; i < speeds.Length; i++)
            {
                var graphic = (Graphic)signType.GetMethod("Create").Invoke(null, new object[] { preview.transform });
                Call(graphic, "SetSpeed", speeds[i]);
                graphic.rectTransform.anchoredPosition = new Vector2(80 + i * 145, 140);
                graphic.rectTransform.localScale = Vector3.one * (i == speeds.Length - 1 ? .3f : 1f);
                var text = graphic.GetComponentInChildren<Text>();
                text.font.RequestCharactersInTexture(text.text, 54, FontStyle.Bold);
            }
            Canvas.ForceUpdateCanvases();
            foreach (var graphic in preview.GetComponentsInChildren<Graphic>())
            {
                var mesh = graphic.canvasRenderer.GetMesh();
                Check(mesh != null && mesh.vertexCount > 0, "empty rendered mesh: " + graphic.name);
            }
            camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            int redPixels = 0, whitePixels = 0;
            foreach (Color pixel in image.GetPixels())
            {
                if (pixel.r > .6f && pixel.g < .3f && pixel.b < .3f) redPixels++;
                if (pixel.r > .9f && pixel.g > .9f && pixel.b > .9f) whitePixels++;
            }
            Check(redPixels > 5000 && whitePixels > 5000, "sign background was not rendered");
            File.WriteAllBytes(output + "/signs.png", image.EncodeToPNG());
            RenderTexture.active = null;
            camera.targetTexture = null;
            File.WriteAllText(output + "/result.txt", "PASS: physical types, transit exclusion, configured value, invalid speeds, flag combination, elevated center, toggle, fixed anchor, hidden/plan state, root LOD, pending replacement, long-road center LOD, camera departure/return, deletion, session clear, sign render");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { File.WriteAllText(output + "/result.txt", error.ToString()); EditorApplication.Exit(1); }
    }
}
