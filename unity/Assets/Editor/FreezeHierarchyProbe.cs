using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// [Camera freeze investigation] Temporary. Remove the entire file after investigation.
/// Records the Profiler during Play and logs the hierarchy under PostLateUpdate.ScriptRunDelayedDynamicFrameRate of slow frames.
/// FreezeProbe (ProfilerRecorder) sees only flat per-marker totals, so it cannot tell what runs inside CoroutinesDelayedCalls.
/// Recording adds Profiler overhead to every frame while playing.
/// </summary>
[InitializeOnLoad]
internal static class FreezeHierarchyProbe
{
    private const double HitchMs = 50; // Same threshold as FreezeProbe.HitchSeconds.
    private const double MinItemMs = 0.5; // Children below this are summed into one line per level.
    private const int SearchDepth = 3; // The phase sits directly under PlayerLoop.
    private const string Target = "PostLateUpdate.ScriptRunDelayedDynamicFrameRate";
    private static bool active, previousEnabled, previousProfileEditor;
    private static int nextFrame;

    static FreezeHierarchyProbe()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.EnteredPlayMode && !active)
        {
            previousEnabled = ProfilerDriver.enabled;
            previousProfileEditor = ProfilerDriver.profileEditor;
            ProfilerDriver.profileEditor = false;
            ProfilerDriver.enabled = true;
            nextFrame = ProfilerDriver.lastFrameIndex + 1;
            active = true;
            EditorApplication.update += Scan;
        }
        else if (change == PlayModeStateChange.ExitingPlayMode && active)
        {
            EditorApplication.update -= Scan;
            ProfilerDriver.enabled = previousEnabled;
            ProfilerDriver.profileEditor = previousProfileEditor;
            active = false;
        }
    }

    // Frames older than the Profiler history are dropped, so resume from the oldest retained frame.
    private static void Scan()
    {
        int last = ProfilerDriver.lastFrameIndex;
        if (nextFrame < ProfilerDriver.firstFrameIndex) nextFrame = ProfilerDriver.firstFrameIndex;
        for (; nextFrame <= last; nextFrame++)
        {
            bool slow;
            using (var raw = ProfilerDriver.GetRawFrameDataView(nextFrame, 0))
                slow = raw.valid && raw.frameTimeMs >= HitchMs;
            if (slow) Dump(nextFrame);
        }
    }

    private static void Dump(int frame)
    {
        using var view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
            HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false);
        if (!view.valid) return;
        int target = Find(view, view.GetRootItemID(), 0);
        if (target < 0) return;
        var message = new StringBuilder();
        message.Append($"[カメラフリーズ調査] 階層 フレーム {view.frameTimeMs:F0}ms profilerFrame={frame}");
        Append(view, target, 0, message);
        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message.ToString());
    }

    private static int Find(HierarchyFrameDataView view, int id, int depth)
    {
        if (view.GetItemName(id) == Target) return id;
        if (depth >= SearchDepth) return -1;
        var items = new List<int>();
        view.GetItemChildren(id, items);
        foreach (int child in items)
        {
            int found = Find(view, child, depth + 1);
            if (found >= 0) return found;
        }
        return -1;
    }

    private static void Append(HierarchyFrameDataView view, int id, int depth, StringBuilder message)
    {
        message.Append('\n').Append(' ', 2 + depth * 2)
            .Append($"{view.GetItemName(id)} total={TotalMs(view, id):F1}ms self={view.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnSelfTime):F1}ms calls={view.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnCalls):F0}");
        var items = new List<int>();
        view.GetItemChildren(id, items);
        int omitted = 0;
        double omittedMs = 0;
        foreach (int child in items)
        {
            double ms = TotalMs(view, child);
            if (ms >= MinItemMs) Append(view, child, depth + 1, message);
            else { omitted++; omittedMs += ms; }
        }
        if (omitted > 0) message.Append('\n').Append(' ', 4 + depth * 2).Append($"他{omitted}件 計{omittedMs:F1}ms");
    }

    private static double TotalMs(HierarchyFrameDataView view, int id) =>
        view.GetItemColumnDataAsDouble(id, HierarchyFrameDataView.columnTotalTime);
}
