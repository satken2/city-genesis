using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 本編のコンポーネントを独立したUnityで読み、モードとIPC差分の状態遷移を検証する。
[InitializeOnLoad]
public static class BuildingPlacementQA
{
    static Assembly source;
    static int checks;
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static BuildingPlacementQA()
    {
        if (SessionState.GetBool("BuildingPlacementQA.Pending", false)) EditorApplication.delayCall += Run;
    }
    static object Get(object o, string key) => o.GetType().GetField(key, Flags).GetValue(o);
    static void Set(object o, string key, object value) => o.GetType().GetField(key, Flags).SetValue(o, value);
    static object Property(object o, string key) => o.GetType().GetProperty(key).GetValue(o);
    static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Flags).Invoke(o, args);
    static void Check(bool pass, string message) { checks++; if (!pass) throw new Exception(message); }
    static Component Add(string name)
    {
        var component = new GameObject(name).AddComponent(source.GetType(name));
        if (component is Behaviour behaviour) behaviour.enabled = false;
        return component;
    }
    static void Mode(object manager, string name) => Call(manager, "SetPlacementMode", Enum.Parse(source.GetType("BuildingPlacementMode"), name));
    static void Command(object manager, string json)
    {
        var jsonType = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JObject");
        var token = jsonType.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { json });
        Call(manager, "OnPlacementCommand", token);
    }

    public static void Run()
    {
        if (!EditorApplication.isPlaying)
        {
            SessionState.SetBool("BuildingPlacementQA.Pending", true);
            EditorApplication.EnterPlaymode();
            return;
        }
        SessionState.SetBool("BuildingPlacementQA.Pending", false);
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        string report = root + "/Temp/BuildingPlacementQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (o, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                {
                    string path = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            source = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            var input = Add("InputHandler");
            Call(input, "SetModePreset", 3);
            var system = Add("InputSystemManager");
            Set(system, "inputHandler", input);
            var master = Add("MasterDataStore");
            var agent = Activator.CreateInstance(source.GetType("MasterDataStore+AgentMasterItem"));
            Set(agent, "default_width", (float?)24f);
            Set(agent, "default_height", (float?)18f);
            Set(agent, "resizable", (bool?)true);
            Set(agent, "type", (int?)1);
            Set(agent, "min_floors", (int?)1);
            ((IDictionary)Get(master, "agentMasterById"))[99999] = agent;
            var manager = Add("BuildingPlanManager");
            Call(manager, "SetBuildingVariant", 99999);
            Call(manager, "SetNewBuildingMode");
            Call(manager, "SetBuildingMode", true);
            Check((float)Property(manager, "PlacementWidth") == 24 && (float)Property(manager, "PlacementDepth") == 18,
                "master dimensions initialize before a placement is picked");
            Check(Property(manager, "PlacementMode").ToString() == "Parcel", "starts with explicit parcel selection");
            Mode(manager, "Free");
            Check((bool)Property(manager, "IsDirectPlacement"), "free mode activates direct placement");
            int parcelType = (int)source.GetType("InputObjectType").GetField("PARCEL").GetValue(null);
            Check((int)Call(input, "GetMode", parcelType) == 0, "direct placement passes through parcel colliders");
            Command(manager, "{\"variant\":99999,\"width\":31}");
            Check((float)Property(manager, "PlacementWidth") == 31 && (float)Property(manager, "PlacementDepth") == 18,
                "partial width command preserves depth");
            Command(manager, "{\"variant\":99998,\"depth\":55}");
            Check((float)Property(manager, "PlacementDepth") == 18, "old variant command is ignored");
            Mode(manager, "Roadside");
            Set(manager, "directCommitted", true);
            Set(manager, "roadAnchorActive", true);
            Set(manager, "roadFront", new Vector3(30, 0, 6));
            Set(manager, "roadOutward", Vector3.forward);
            Set(manager, "directCenter", new Vector3(30, 0, 15));
            Command(manager, "{\"variant\":99999,\"depth\":28,\"angle\":45}");
            Check(((Vector3)Property(manager, "PlacementCenter") - new Vector3(30, 0, 20)).sqrMagnitude < .001f,
                "panel resizing retains roadside front anchor");
            Check((float)Property(manager, "PlacementAngle") == 0, "roadside ignores manual rotation");
            int revision = (int)Get(manager, "placementRevision");
            Call(manager, "OnUpdateShapeFromIpc", null, 999, new float[] { 888, 888 }, 0f, revision - 1);
            Check((int)Get(manager, "currentFloors") == 1, "old preview is rejected before mutating state");
            Mode(manager, "Free");
            Check((bool)Property(manager, "PlacementCommitted") && !(bool)Property(manager, "HasRoadAnchor"),
                "mode switch keeps fixed position but removes road constraint");
            Call(manager, "ResetPlacementSelection", true);
            Check(!(bool)Property(manager, "PlacementCommitted") && (float)Property(manager, "PlacementDepth") == 28,
                "reposition preserves dimensions");
            Command(manager, "{\"variant\":99999,\"width\":1000,\"depth\":-4}");
            Check((float)Property(manager, "PlacementWidth") == 500 && (float)Property(manager, "PlacementDepth") == 1,
                "dimension limits match panel");
            Set(agent, "resizable", (bool?)false);
            Command(manager, "{\"variant\":99999,\"width\":20}");
            Check((float)Property(manager, "PlacementWidth") == 500, "fixed model ignores resize commands");
            Mode(manager, "Parcel");
            Check((int)Call(input, "GetMode", parcelType) == 3, "parcel mode restores parcel selection");
            manager.GetType().GetProperty("DirectAllowed").SetValue(manager, false);
            Mode(manager, "Free");
            Check(Property(manager, "PlacementMode").ToString() == "Parcel", "copy placement cannot leave parcel mode");
            Call(manager, "SetBuildingMode", false);
            Check(!(bool)Property(manager, "PlacementActive"), "ending construction disables placement controls");
            File.WriteAllText(report, $"PASS: {checks} Unity building placement state checks");
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
