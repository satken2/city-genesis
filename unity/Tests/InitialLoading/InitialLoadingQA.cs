using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class InitialLoadingQA
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }

    public static void Run()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string path = Path.Combine(root, "Library/ScriptAssemblies", new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var source = Assembly.LoadFrom(Path.Combine(root, "Temp/InitialLoadingQA/Assembly-CSharp.dll"));
            var stateType = source.GetType("GameStateManager", true);
            var go = new GameObject("InitialLoadingStateQA");
            var state = go.AddComponent(stateType);
            Action<string, object[]> call = (name, values) => stateType.GetMethod(name, Flags).Invoke(state, values);
            Func<bool> visible = () => (bool)stateType.GetProperty("IsWorldVisible").GetValue(state);
            var readyFrames = stateType.GetField("readyFrames", Flags);

            Check(!visible(), "起動直後は黒画面");
            call("PrepareInitialLoad", new object[] { 1 });
            call("SetGameStarted", new object[] { true, null });
            Check(!visible(), "データ取得開始だけでは表示しない");
            call("RevealWorld", new object[] { 1 });
            Check(!visible(), "ロード未完了の開始操作は無効");
            readyFrames.SetValue(state, 3);
            call("RevealWorld", new object[] { 0 });
            Check(!visible(), "古いロードの開始操作は無効");
            call("RevealWorld", new object[] { 1 });
            Check(visible(), "完了済み世代の開始操作で表示");
            call("BeginLoading", Array.Empty<object>());
            Check(!visible(), "別セーブのロード時に再び黒画面");
            call("PrepareInitialLoad", new object[] { 2 });
            call("RevealWorld", new object[] { 1 });
            Check(!visible(), "前のセーブの開始通知で解除しない");
            UnityEngine.Object.DestroyImmediate(go);

            // Verify persistent-worker and finite-job classification using the actual scheduler state.
            var scheduler = source.GetType("RenderWorkScheduler", true);
            var operationType = scheduler.GetNestedType("Operation", BindingFlags.NonPublic);
            var operations = (IDictionary)scheduler.GetField("operations", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var operation = Activator.CreateInstance(operationType, true);
            var handle = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Coroutine));
            // A handle used only as a dictionary key has no native coroutine, so do not call Unity's release method.
            GC.SuppressFinalize(handle);
            operations.Add(handle, operation);
            Func<bool> pending = () => (bool)scheduler.GetProperty("HasPendingInitialWork").GetValue(null);
            Check(pending(), "有限ジョブは待機対象");
            bool queued = false;
            operationType.GetField("hasPendingWork").SetValue(operation, new Func<bool>(() => queued));
            Check(!pending(), "アイドル状態の常駐ワーカーは待たない");
            queued = true;
            Check(pending(), "常駐ワーカーに残る生成は待つ");
            operationType.GetField("done").SetValue(operation, true);
            Check(!pending(), "完了ジョブは待たない");
            operations.Clear();
            File.WriteAllText("result.txt", "PASS: startup visibility, stale generations, scheduler pending work (11 checks)");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            File.WriteAllText("result.txt", "FAIL: " + error);
            Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }
}
