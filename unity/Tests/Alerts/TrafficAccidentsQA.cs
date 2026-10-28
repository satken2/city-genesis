using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class TrafficAccidentsQA
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(Assembly source, Action<bool, string> check)
    {
        var constants = source.GetType("Constants", true);
        Func<string, int> bit = name => (int)constants.GetField(name).GetRawConstantValue();
        var managerType = source.GetType("TrafficAccidentEffectManager", true);
        var itemType = source.GetType("AlertItem", true);
        var accidentType = managerType.GetNestedType("Accident", BindingFlags.NonPublic);
        var go = new GameObject("TrafficAccidentsQA");
        var manager = go.AddComponent(managerType);
        if (managerType.GetProperty("Instance").GetValue(null) == null)
            managerType.GetMethod("Awake", InstanceFlags).Invoke(manager, null);
        var records = (IDictionary)managerType.GetField("accidents", InstanceFlags).GetValue(manager);
        object item = Activator.CreateInstance(itemType);
        itemType.GetField("id").SetValue(item, 17);
        itemType.GetField("status").SetValue(item, bit("STATUS_ACTIVE"));
        itemType.GetField("alert").SetValue(item, bit("ALERT_PATH_ACCIDENT"));
        var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
        items.Add(item);
        int mask = bit("ALERT_PATH_ACCIDENT") << bit("ALERT_PATH_MASK_SHIFT");
        Action<int, int> apply = (filter, epoch) => managerType.GetMethod("ApplyAlerts").Invoke(manager, new object[] { items, filter, epoch });
        apply(mask, 1);
        check(records.Count == 1, "事故フラグ1件につき事故車の状態1件");
        object original = records[17];
        apply(mask, 1);
        check(ReferenceEquals(original, records[17]), "再取得で抽選を繰り返さない");
        apply(bit("ALERT_AGENT_ON_FIRE"), 1);
        check(!(bool)managerType.GetField("filterEnabled", InstanceFlags).GetValue(manager), "道路アラートの名前空間を分離");
        check(ReferenceEquals(original, records[17]), "表示フィルターOFFでも外観を保持");
        apply(mask, 1);
        managerType.GetProperty("IsVisible").SetValue(manager, false);
        managerType.GetProperty("IsVisible").SetValue(manager, true);
        managerType.GetMethod("RefreshPath").Invoke(manager, new object[] { 17 });
        check(ReferenceEquals(original, records[17]), "カメラ移動・道路再生成・全体表示切り替えで外観を保持");
        apply(mask, 2);
        check(!ReferenceEquals(original, records[17]), "同じ道路の次エポックの事故は新しく抽選");

        var layoutType = source.GetType("RoadLaneLayout", true);
        var lineType = source.GetType("VehiclePolyline", true);
        object layout = Activator.CreateInstance(layoutType, new object[] { 10f, 1f, 0f, 0f, 2, bit("DIR_BOTH") });
        object line = Activator.CreateInstance(lineType, new object[] { new List<Vector3> { new Vector3(0, 20, 0), new Vector3(0, 25, 100) } });
        object accident = records[17];
        Action<string, object> set = (name, value) => accidentType.GetField(name).SetValue(accident, value);
        var bounds = new Bounds(new Vector3(0, .75f, 0), new Vector3(1.8f, 1.5f, 4.5f));
        set("Bounds", bounds); set("Progress", .5f); set("Lane", .5f); set("Forward", true); set("Yaw", 0f);
        var placement = managerType.GetMethod("TryPlacement", StaticFlags);
        foreach (float roll in new[] { 30f, 90f, -90f, 180f })
        {
            set("Roll", roll);
            object[] args = { line, layout, bit("DIR_BOTH"), bit("TRAFFIC_HAND_LEFT"), accident, null, null, null };
            check((bool)placement.Invoke(null, args), "傾斜・横転・転覆の配置が成立");
            var surface = (Vector3)args[5]; var position = (Vector3)args[6]; var rotation = (Quaternion)args[7];
            Vector3 tangent = new Vector3(0, 5, 100).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
            Vector3 normal = Vector3.Cross(tangent, right).normalized;
            float minimum = float.PositiveInfinity;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                var p = position + rotation * (bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z)));
                minimum = Mathf.Min(minimum, Vector3.Dot(p - surface, normal));
                check(Mathf.Abs(Vector3.Dot(p - surface, right)) <= 4.001f, "回転後も歩道にはみ出さない");
            }
            check(minimum >= -.001f && minimum < .2f, "高架・坂道で回転後の最下部が路面に接地");
        }
        object[] backward = { line, layout, bit("DIR_BACKWARD"), bit("TRAFFIC_HAND_RIGHT"), accident, null, null, null };
        placement.Invoke(null, backward);
        check(Vector3.Dot((Quaternion)backward[7] * Vector3.forward, new Vector3(0, 5, 100).normalized) < -.99f, "逆方向の一方通行に向きを合わせる");
        object shortLine = Activator.CreateInstance(lineType, new object[] { new List<Vector3> { Vector3.zero, Vector3.forward } });
        object[] shortArgs = { shortLine, layout, bit("DIR_BOTH"), bit("TRAFFIC_HAND_LEFT"), accident, null, null, null };
        check(!(bool)placement.Invoke(null, shortArgs), "車体を収められない短い道路には置かない");

        var pathType = source.GetType("GeneratedModels.PathSummary", true);
        object path = Activator.CreateInstance(pathType);
        pathType.GetField("start_point").SetValue(path, new List<object> { 0f, 0f, 0f });
        pathType.GetField("end_point").SetValue(path, new List<object> { 0f, 0f, 100f });
        var smokeAllowed = managerType.GetMethod("SmokeAllowed", StaticFlags);
        check((bool)smokeAllowed.Invoke(null, new[] { path, (object)new Vector3(0, 20, 50) }), "高架道路では煙を許可");
        check(!(bool)smokeAllowed.Invoke(null, new[] { path, (object)new Vector3(0, -20, 50) }), "相対高さ0でも地形下のトンネルでは煙なし");
        pathType.GetField("start_point").SetValue(path, new List<object> { 0f, -10f, 0f });
        check(!(bool)smokeAllowed.Invoke(null, new[] { path, (object)new Vector3(0, 1, 50) }), "地下道路の坑口側も煙なし");

        var root = new GameObject("AccidentRootQA"); root.transform.SetParent(go.transform);
        set("Root", root);
        var updateSmoke = managerType.GetMethod("UpdateSmoke", StaticFlags);
        var flipped = Quaternion.Euler(0, 0, 180);
        updateSmoke.Invoke(null, new object[] { accident, false, flipped });
        check(root.GetComponentsInChildren<ParticleSystem>(true).Length == 0, "地下の事故車には煙を生成しない");
        updateSmoke.Invoke(null, new object[] { accident, true, flipped });
        var smoke = (GameObject)accidentType.GetField("Smoke").GetValue(accident);
        check(Vector3.Dot(smoke.transform.up, Vector3.up) > .999f, "転覆した車からも煙は上向き");
        var particles = smoke.GetComponent<ParticleSystem>();
        particles.Simulate(3f, true, true, false);
        var buffer = new ParticleSystem.Particle[45];
        int count = particles.GetParticles(buffer);
        check(count > 0, "車サイズの煙パーティクルが発生");
        for (int i = 0; i < count; i++) check(buffer[i].position.y >= -.2f, "煙は上方へ昇る");
        updateSmoke.Invoke(null, new object[] { accident, true, flipped });
        check(ReferenceEquals(smoke, accidentType.GetField("Smoke").GetValue(accident)), "再配置で煙を再生成しない");
        updateSmoke.Invoke(null, new object[] { accident, false, flipped });
        check(!smoke.activeSelf, "地下へ移った道路の煙を止める");

        itemType.GetField("alert").SetValue(item, 0);
        apply(mask, 2);
        check(records.Count == 0 && !root.activeSelf, "事故解消で車体と煙を撤去");
        itemType.GetField("alert").SetValue(item, bit("ALERT_PATH_ACCIDENT"));
        apply(mask, 2);
        managerType.GetMethod("ClearAll").Invoke(manager, null);
        check(records.Count == 0, "都市切り替えで抽選結果を破棄");
        UnityEngine.Object.DestroyImmediate(go);
    }
}
