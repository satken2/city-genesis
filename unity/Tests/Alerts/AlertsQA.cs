using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class AlertsQA
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int checks;

    private static void Check(bool result, string message)
    {
        checks++;
        if (!result) throw new Exception(message);
    }

    private static object Item(Type type, params object[] fields)
    {
        object value = Activator.CreateInstance(type);
        for (int i = 0; i < fields.Length; i += 2)
            type.GetField((string)fields[i]).SetValue(value, fields[i + 1]);
        return value;
    }

    private static IList List(Type type, params object[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
        foreach (object value in items) list.Add(value);
        return list;
    }

    public static void Run()
    {
        if (!Application.isPlaying)
        {
            // 通常の破棄処理を検証するため、独立プロジェクトを再生モードへ切り替えます。
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.isPlaying = true;
            return;
        }
        try
        {
            var args = Environment.GetCommandLineArgs();
            string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string path = Path.Combine(root, "Library/ScriptAssemblies", new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var source = Assembly.LoadFrom(Path.GetFullPath("Runtime/Assembly-CSharp.dll"));
            var constants = source.GetType("Constants", true);
            Func<string, int> bit = name => (int)constants.GetField(name).GetRawConstantValue();
            int resourceBit = bit("ALERT_DISPLAY_RESOURCE_WARNING");
            int facilityBit = bit("ALERT_DISPLAY_FACILITY_WARNING");
            int sanitationBit = bit("ALERT_DISPLAY_SANITATION_WARNING");
            int infraBit = bit("ALERT_DISPLAY_INFRA_WARNING");
            int ruinBit = bit("ALERT_DISPLAY_RUIN");
            int crimeBit = bit("ALERT_AGENT_CRIME");

            var factoryType = source.GetType("AlertIconFactory", true);
            var factoryGO = new GameObject("AlertIconFactoryQA");
            factoryGO.SetActive(false);
            var factory = factoryGO.AddComponent(factoryType);
            var icons = new Dictionary<string, string>
            {
                ["resourceWarningIcon"] = "sand-clock",
                ["facilityWarningIcon"] = "bribery",
                ["sanitationWarningIcon"] = "sanitation",
                ["infraWarningIcon"] = "electricity",
                ["agentCrimeIcon"] = "bribery",
                ["agentCollapsedStatusIcon"] = "rubble",
            };
            string scene = File.ReadAllText(Path.Combine(root, "Assets/Scenes/GameScene.unity"));
            foreach (var icon in icons)
            {
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Images/" + icon.Value + ".png");
                Check(sprite != null, "アイコンのインポート: " + icon.Value);
                factoryType.GetField(icon.Key, Flags).SetValue(factory, sprite);
                if (icon.Key.EndsWith("WarningIcon", StringComparison.Ordinal))
                {
                    string guid = AssetDatabase.AssetPathToGUID("Assets/Images/" + icon.Value + ".png");
                    Check(scene.Contains(icon.Key + ": {fileID: 21300000, guid: " + guid), "本編シーンの警告アイコン参照: " + icon.Key);
                }
            }
            factoryGO.SetActive(true);
            var managerType = source.GetType("AlertManager", true);
            var managerGO = new GameObject("AlertManagerQA");
            var manager = managerGO.AddComponent(managerType);
            managerType.GetMethod("Awake", Flags).Invoke(manager, null);
            var responseType = source.GetType("AlertsResponse", true);
            var warningType = source.GetType("SpatialWarningItem", true);
            var itemType = source.GetType("AlertItem", true);
            object resource = Item(warningType, "entity_type", "resource", "id", 1, "center_point", new List<float> { 10, 20 }, "warning", resourceBit);
            object parcel = Item(warningType, "entity_type", "parcel", "id", 1, "center_point", new List<float> { 30, 40 }, "warning", sanitationBit);
            object building = Item(warningType, "entity_type", "agent", "id", 1, "center_point", new List<float> { 50, 60 }, "warning", facilityBit | infraBit);
            object agent = Item(itemType, "id", 1, "center_point", new List<float> { 50, 60 }, "status", bit("STATUS_COLLAPSED"), "alert", crimeBit);
            object response = Item(responseType, "agents", List(itemType, agent), "paths", List(itemType), "warnings", List(warningType, resource, parcel, building));
            var apply = managerType.GetMethod("ApplyAlerts", Flags);
            Action<object> applyResponse = value => apply.Invoke(manager, new[] { value });
            var entries = (IDictionary)managerType.GetField("_entries", Flags).GetValue(manager);
            Func<string, IList> iconList = key => (IList)entries[key].GetType().GetField("icons").GetValue(entries[key]);
            Func<string, Vector3> world = key => (Vector3)entries[key].GetType().GetField("world").GetValue(entries[key]);
            Action<int> setMask = mask => managerType.GetField("_filterMask", Flags).SetValue(manager, mask);
            int full = (int)managerType.GetField("DefaultFilterMask").GetValue(null);
            Check((full & (resourceBit | facilityBit | sanitationBit | infraBit)) == (resourceBit | facilityBit | sanitationBit | infraBit), "新しい4種類は既定で表示");

            applyResponse(response);
            Check(entries.Count == 3, "資源・区画・建物の同一IDは独立");
            Check(iconList("resource:1").Count == 1, "資源描画オブジェクトがなくても警告を表示");
            Check(iconList("parcel:1").Count == 1, "区画描画オブジェクトがなくても衛生警告を表示");
            Check(iconList("agent:1").Count == 4, "廃墟・既存アラート・公共施設警告・インフラ警告は同じ列で共存");
            object resourceIcon = iconList("resource:1")[0];
            warningType.GetField("center_point").SetValue(resource, new List<float> { 100, 200 });
            applyResponse(response);
            Check(ReferenceEquals(resourceIcon, iconList("resource:1")[0]), "位置変更ではアイコンを再生成しない");
            Check(world("resource:1").x == 100 && world("resource:1").z == 200, "資源の位置変更を反映");
            foreach (int hidden in new[] { resourceBit, facilityBit, sanitationBit, infraBit })
            {
                setMask(full & ~hidden);
                applyResponse(response);
                Check(entries.Contains("resource:1") == (hidden != resourceBit), "資源の表示フィルター");
                Check(entries.Contains("parcel:1") == (hidden != sanitationBit), "衛生の表示フィルター");
                Check(iconList("agent:1").Count == (hidden == facilityBit || hidden == infraBit ? 3 : 4), "施設の表示フィルターは既存アイコンを維持");
            }
            setMask(0);
            applyResponse(response);
            Check(entries.Count == 0, "マスク0ですべて非表示");
            setMask(full);
            applyResponse(response);
            managerType.GetProperty("IsVisible").SetValue(manager, false);
            Check(!((GameObject)managerType.GetField("_uiCanvasRoot", Flags).GetValue(manager)).activeSelf, "アラート全体の非表示");
            managerType.GetProperty("IsVisible").SetValue(manager, true);
            Check(((GameObject)managerType.GetField("_uiCanvasRoot", Flags).GetValue(manager)).activeSelf, "アラート全体の再表示");
            object oldResponse = Item(responseType, "agents", List(itemType, agent), "paths", List(itemType));
            applyResponse(oldResponse);
            Check(entries.Count == 1 && iconList("agent:1").Count == 2, "warningsのない応答は既存アラートだけ表示");
            applyResponse(response);
            managerType.GetMethod("ClearAll").Invoke(manager, null);
            Check(entries.Count == 0 && managerType.GetField("_lastResponse", Flags).GetValue(manager) == null, "セッション消去でバブルと応答を破棄");
            applyResponse(response);
            applyResponse(Item(responseType, "agents", List(itemType), "paths", List(itemType), "warnings", List(warningType)));
            Check(entries.Count == 0, "警告の解消でバブルを削除");
            UnityEngine.Object.DestroyImmediate(managerGO);
            UnityEngine.Object.DestroyImmediate(factoryGO);
            File.WriteAllText("result.txt", "PASS: spatial warning rendering, filters, cache, assets, lifecycle (" + checks + " checks)");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            File.WriteAllText("result.txt", "FAIL: " + error);
            Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        Run();
    }
}
