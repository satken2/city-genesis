using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 本編アセンブリを独立したUnityへ読み込み、物理レイとInput Systemの実入力で検証する。
[InitializeOnLoad]
public static class CameraQA
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly source;
    private static Component controller, input;
    private static Camera camera;
    private static Type inputSystem, keyType, keyboardState;
    private static object keyboard;
    private static MethodInfo queueState;
    private static int checks;
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    private static bool Property(object target, string name) => (bool)target.GetType().GetProperty(name).GetValue(target);
    private static void Check(bool condition, string description)
    {
        checks++;
        if (!condition) throw new Exception(description);
    }
    private static void Near(Vector3 actual, Vector3 expected, string message) => Check(Vector3.Distance(actual, expected) < .003f, $"{message}: actual={actual}, expected={expected}");
    private static void SameRotation(Quaternion expected, string message) => Check(Quaternion.Angle(camera.transform.rotation, expected) < .05f, message);
    private static Component Add(string type, GameObject go = null)
    {
        var component = (go ?? new GameObject(type)).AddComponent(source.GetType(type));
        ((Behaviour)component).enabled = false;
        return component;
    }
    private static void Keys(params string[] names)
    {
        Array keys = Array.CreateInstance(keyType, names.Length);
        for (int i = 0; i < names.Length; i++) keys.SetValue(Enum.Parse(keyType, names[i]), i);
        object state = Activator.CreateInstance(keyboardState, new object[] { keys });
        queueState.Invoke(null, new[] { keyboard, state, (object)(-1d) });
        inputSystem.GetMethod("Update", Type.EmptyTypes).Invoke(null, null);
        foreach (string name in names)
            Check((bool)source.GetType("InputUtil").GetMethod("KeyPressed").Invoke(null, new[] { Enum.Parse(keyType, name) }),
                $"synthetic keyboard delivers {name}");
    }
    private static void Step(float seconds, params string[] keys)
    {
        Keys(keys);
        Call(controller, "HandleCinematicMovement", seconds);
    }
    private static void Tap(string key)
    {
        Step(0f);
        Step(0f, key);
        Step(0f);
    }
    private static GameObject Box(string tag, Vector3 position, Vector3 scale, bool ground = false)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.tag = tag;
        if (ground) go.layer = LayerMask.NameToLayer("Ground");
        go.transform.position = position;
        go.transform.localScale = scale;
        Physics.SyncTransforms();
        return go;
    }
    private static void View(Vector3 position, Quaternion rotation) => Call(controller, "SetView", position, rotation);
    private static void Preset(string name) => Call(input, "SetModePreset", source.GetType("InputModePreset").GetField(name).GetValue(null));
    private static Vector3 Pivot(bool filming)
    {
        object[] args = { Vector3.zero, filming };
        Check((bool)Call(controller, "TryGetRotationPivotPoint", args), "center ray finds a surface");
        return (Vector3)args[0];
    }

    static CameraQA()
    {
        if (SessionState.GetBool("CameraQA.Pending", false)) EditorApplication.delayCall += Run;
    }
    public static void Run()
    {
        if (!EditorApplication.isPlaying)
        {
            SessionState.SetBool("CameraQA.Pending", true);
            EditorApplication.EnterPlaymode();
            return;
        }
        SessionState.SetBool("CameraQA.Pending", false);
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        string report = root + "/Temp/CameraQA/result.txt";
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
            source = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            var inputAssembly = Assembly.Load("Unity.InputSystem");
            inputSystem = inputAssembly.GetType("UnityEngine.InputSystem.InputSystem");
            Application.runInBackground = true;
            object settings = inputSystem.GetProperty("settings").GetValue(null);
            var background = settings.GetType().GetProperty("backgroundBehavior");
            background.SetValue(settings, Enum.Parse(background.PropertyType, "IgnoreFocus"));
            var editorInput = settings.GetType().GetProperty("editorInputBehaviorInPlayMode");
            editorInput.SetValue(settings, Enum.Parse(editorInput.PropertyType, "AllDeviceInputAlwaysGoesToGameView"));
            keyType = inputAssembly.GetType("UnityEngine.InputSystem.Key");
            keyboardState = inputAssembly.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState");
            keyboard = inputSystem.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(null, new object[] { "Keyboard", "CameraQA", null });
            queueState = inputSystem.GetMethods().Single(m => m.Name == "QueueStateEvent" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(keyboardState);
            input = Add("InputHandler");
            camera = new GameObject("MainCamera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            controller = Add("CameraController", camera.gameObject);
            var ui = Assembly.Load("UnityEngine.UI");
            var eventSystem = new GameObject("EventSystem").AddComponent(ui.GetType("UnityEngine.EventSystems.EventSystem"));
            var button = new GameObject("MinimapToggleButton", typeof(RectTransform)).AddComponent(ui.GetType("UnityEngine.UI.Button"));
            Set(controller, "cinematicModeButton", button);
            Call(controller, "Start");
            Check(File.ReadAllText(root + "/Assets/Scenes/GameScene.unity").Contains("cinematicModeButton: {fileID: 778018351}"),
                "scene connects the reused minimap button");

            var ground = Box("Ground", new Vector3(0, -1, 0), new Vector3(1000, 2, 1000), true);
            var building = Box("BuildingObject", new Vector3(0, 20, 0), new Vector3(40, 40, 40));
            Box("ParcelObject", new Vector3(0, 65, 0), new Vector3(50, 2, 50));
            View(new Vector3(0, 100, -100), Quaternion.Euler(45, 0, 0));
            Vector3 filmingPivot = Pivot(true);
            Check(Mathf.Abs(filmingPivot.z + 20) < .01f, "building surface wins over ground");
            Check(Mathf.Abs(Pivot(false).y) < .01f, "normal right-drag still pivots on ground");
            Box("PathObject", new Vector3(0, 60, -60), new Vector3(20, 20, 20));
            Near(Pivot(true), filmingPivot, "road collider does not steal the filming pivot");

            Set(controller, "dragVelocity", new Vector3(100, 0, 50));
            Set(controller, "isPendingDrag", true);
            eventSystem.GetType().GetMethod("SetSelectedGameObject", new[] { typeof(GameObject) }).Invoke(eventSystem, new object[] { button.gameObject });
            var click = button.GetType().GetProperty("onClick").GetValue(button);
            click.GetType().GetMethod("Invoke", Type.EmptyTypes).Invoke(click, null);
            Check(Property(controller, "IsCinematicMode"), "button enters filming");
            Check(eventSystem.GetType().GetProperty("currentSelectedGameObject").GetValue(eventSystem) == null,
                "entry clears button selection so Space cannot submit again");
            Near((Vector3)Get(controller, "dragVelocity"), Vector3.zero, "entry clears inertia");
            Check(!(bool)Get(controller, "isPendingDrag"), "entry clears pending gestures");
            Vector3 start = camera.transform.position;
            Quaternion startRotation = camera.transform.rotation;
            float radius = Vector3.Distance(start, filmingPivot);
            Step(2f);
            Check(Mathf.Abs(Vector3.Distance(camera.transform.position, filmingPivot) - radius) < .003f, "orbit retains radius");
            Check(Mathf.Abs(camera.transform.position.y - start.y) < .003f, "orbit retains height");
            Vector3 screenCenter = camera.WorldToViewportPoint(filmingPivot);
            Check(Mathf.Abs(screenCenter.x - .5f) < .0001f && Mathf.Abs(screenCenter.y - .5f) < .0001f, "target remains centered");
            Tap("Backspace");
            Check((float)Get(controller, "cinematicDirection") == -1f, "Backspace reverses orbit direction");
            Step(2f);
            Near(camera.transform.position, start, "reverse retraces orbit");
            SameRotation(startRotation, "reverse restores original angle");
            Tap("Space");
            Step(1f, "R", "UpArrow", "A");
            Near(camera.transform.position, start, "mode 1 pause ignores travel-only controls");
            Set(input, "hasPendingClick", true);
            Call(input, "Update");
            Check(!(bool)Get(input, "hasPendingClick"), "filming blocks world clicks");

            Tap("LeftShift");
            Check(Get(controller, "cinematicMode").ToString() == "Travel", "Shift selects travel");
            Check((bool)Get(controller, "cinematicPaused"), "switch keeps pause state");
            Near(camera.transform.position, start, "switch preserves position");
            SameRotation(startRotation, "switch preserves angle");
            Step(1f, "UpArrow", "R");
            Near(camera.transform.position, start + new Vector3(0, 5, 20), "paused travel allows arrows and height adjustment");
            Step(1f, "DownArrow", "F");
            Near(camera.transform.position, start, "opposite manual movement returns exactly");
            Tap("Backspace");
            Tap("Space");
            Step(2f);
            Near(camera.transform.position, start + Vector3.forward * 10, "travel speed is 5 meters per second");
            Tap("D");
            Vector3 direction = (Vector3)Get(controller, "cinematicTravelDirection");
            Check(Mathf.Abs(Vector3.SignedAngle(Vector3.forward, direction, Vector3.up) - 5) < .001f, "D changes heading by five degrees");
            Step(0f, "D");
            Step(0f, "D");
            direction = (Vector3)Get(controller, "cinematicTravelDirection");
            Check(Mathf.Abs(Vector3.SignedAngle(Vector3.forward, direction, Vector3.up) - 10) < .001f, "holding D does not repeat");
            Tap("A");
            direction = (Vector3)Get(controller, "cinematicTravelDirection");
            Vector3 before = camera.transform.position;
            Step(1f);
            Near(camera.transform.position, before + direction * 5, "new heading changes only travel direction");
            SameRotation(startRotation, "steering leaves camera angle fixed");
            Check(Mathf.Abs(camera.transform.position.y - start.y) < .003f, "travel keeps absolute Y");
            Tap("Backspace");
            Step(1f);
            Near(camera.transform.position, before, "travel reverse retraces path");
            Tap("RightShift");
            Check(Get(controller, "cinematicMode").ToString() == "Orbit", "right Shift returns to orbit");
            Near(camera.transform.position, before, "return to orbit does not jump");
            Tap("Escape");
            Check(!Property(controller, "IsCinematicMode"), "Escape returns to normal");
            Near(camera.transform.position, before, "exit keeps current position");
            Check(Property(controller, "BlocksWorldInput"), "exit frame consumes world input");

            Call(controller, "ToggleCinematicMode");
            Call(controller, "SuspendCameraSync");
            Check(!Property(controller, "IsCinematicMode"), "session reset stops filming");
            Preset("ROAD_CONSTRUCTION");
            Call(controller, "ToggleCinematicMode");
            Check(!Property(controller, "IsCinematicMode"), "editing rejects filming entry");
            Preset("ALL_NORMAL");
            Check(Property(input, "IsNormalMode"), "normal preset permits filming");
            Call(controller, "ToggleCinematicMode");
            Preset("ROAD_CONSTRUCTION");
            Call(controller, "Update");
            Check(!Property(controller, "IsCinematicMode"), "starting editing ends filming");
            Preset("ALL_NORMAL");

            View(new Vector3(200, 50, 200), Quaternion.Euler(-20, 0, 0));
            Call(controller, "ToggleCinematicMode");
            Check(!(bool)Get(controller, "cinematicHasPivot"), "sky view waits without inventing a pivot");
            before = camera.transform.position;
            Step(3f);
            Near(camera.transform.position, before, "missing target is stationary");
            Tap("LeftShift");
            Step(1f);
            Near(camera.transform.position, before + Vector3.forward * 5, "missing target still allows travel");

            View(new Vector3(200, 5, 200), Quaternion.Euler(25, 60, 0));
            Call(controller, "ToggleCinematicMode");
            Tap("LeftShift");
            before = camera.transform.position;
            startRotation = camera.transform.rotation;
            Vector3 travelForward = Quaternion.Euler(0, 60, 0) * Vector3.forward;
            Step(1f);
            Vector3 oneStep = camera.transform.position;
            Near(oneStep, before + travelForward * 5, "travel uses horizontal view direction at nonzero yaw");
            camera.transform.position = before;
            for (int i = 0; i < 60; i++) Step(1f / 60f);
            Near(camera.transform.position, oneStep, "travel distance does not depend on frame count");
            Tap("Space");
            Box("Ground", new Vector3(200, 10, 200), new Vector3(100, 20, 100), true);
            before = camera.transform.position;
            Step(1f, "RightArrow");
            Near(camera.transform.position, before + (Quaternion.Euler(0, 60, 0) * Vector3.right) * 20,
                "manual strafe is camera-relative and does not follow raised ground");
            Step(2f, "F");
            Check(camera.transform.position.y == 0, "manual descent stops at Y zero");
            Step(1f, "R");
            Check(camera.transform.position.y == 5, "manual ascent works from Y zero");
            SameRotation(startRotation, "manual movements preserve view angle");

            View(new Vector3(200, 100, 200), Quaternion.Euler(90, 37, 0));
            Call(controller, "ToggleCinematicMode");
            Step(1f);
            Check(Quaternion.Angle(camera.transform.rotation, Quaternion.Euler(90, 40, 0)) < .05f, "vertical orbit retains yaw");
            Call(controller, "StopCinematicMode");
            building.SetActive(false);
            var sea = Box("SeaObject", new Vector3(300, 10, 300), new Vector3(50, 1, 50));
            View(new Vector3(300, 100, 300), Quaternion.Euler(90, 0, 0));
            Check(Mathf.Abs(Pivot(true).y - 10.5f) < .01f, "sea surface can be an orbit center");
            Check(Mathf.Abs(Pivot(false).y) < .01f, "normal orbit continues to ignore sea");
            sea.SetActive(false);
            ground.SetActive(false);
            Check(Vector3.Distance(Pivot(true), new Vector3(300, 0, 300)) < .02f, "height-field fallback resolves unloaded terrain within physics tolerance");
            object[] undergroundRay = { new Ray(new Vector3(200, 5, 200), Vector3.down), Vector3.zero };
            Check(!(bool)Call(controller, "RaycastTerrainHeightField", undergroundRay),
                "a downward ray inside terrain does not invent a target above the camera");
            File.WriteAllText(report, $"PASS: {checks} Unity camera checks");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(report, e.ToString());
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
