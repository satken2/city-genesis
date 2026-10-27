using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

/// <summary>Queue a render work operation joins. High runs before Normal every frame; Low uses only the budget Normal leaves over.</summary>
public enum RenderWorkPriority
{
    High,
    Normal,
    Low,
}

/// <summary>
/// Advance render preparation coroutines within a shared budget. Exclude communication/frame waits,
/// summing only synchronous processing during MoveNext. Split individual heavy generators into small work units separately.
/// </summary>
public static class RenderWorkScheduler
{
    private static int frame = -1;
    private static double usedMs;
    private static readonly LinkedList<Operation> waiting = new LinkedList<Operation>();
    /// <summary>Work preceding normal jobs every frame (terrain tile publication), ensuring terrain is ready before object generation.</summary>
    private static readonly LinkedList<Operation> priorityWaiting = new LinkedList<Operation>();
    /// <summary>Work following normal jobs (deferred site decorations), so it never delays building or road generation.</summary>
    private static readonly LinkedList<Operation> lowWaiting = new LinkedList<Operation>();
    private static readonly Dictionary<string, double> categoryMs = new Dictionary<string, double>();
    private sealed class TimingSample
    {
        public double cpuMs, maxMs;
        public long steps, queueFrames, underBudgetQueueFrames;
    }
    private static readonly Dictionary<Type, TimingSample> timingSamples = new Dictionary<Type, TimingSample>();
    private static readonly Dictionary<string, long> eventCounts = new Dictionary<string, long>();
    private static double lastReportTime;
    private static int lastReportFrame;

    /// <summary>Aggregate fixed-name counts into five-second logs. Exclude object IDs from names; collect nothing while disabled.</summary>
    public static void CountEvent(string name, long count = 1)
    {
        if (!LocalConstants.UNITY_LOG_TIMING || count == 0) return;
        eventCounts.TryGetValue(name, out long previous);
        eventCounts[name] = previous + count;
    }

    private static TimingSample GetTimingSample(IEnumerator routine)
    {
        if (!LocalConstants.UNITY_LOG_TIMING) return null;
        Type key = routine.GetType();
        if (!timingSamples.TryGetValue(key, out var sample))
            timingSamples[key] = sample = new TimingSample();
        return sample;
    }

    internal static void ReportTimings()
    {
        if (!LocalConstants.UNITY_LOG_TIMING) return;
        double now = Time.realtimeSinceStartupAsDouble;
        if (now - lastReportTime < 5) return;
        var message = new System.Text.StringBuilder();
        message.Append($"[TIMING] Scheduler window={now - lastReportTime:F2}s frames={Time.frameCount - lastReportFrame} budget={PerformanceConfig.FrameBudgetMs:F2}ms active={operations.Count} waiting={WaitingCount}");
        var loader = CellDataLoader.Instance;
        if (loader != null)
            message.Append($" buildingsPending={loader.buildingManager?.PendingDetailCount ?? 0} pathsPending={loader.pathManager?.PendingTerrainRebuildCount ?? 0} pathBatch={loader.pathManager?.IsLodBatchActive ?? false}");
        foreach (var item in timingSamples)
        {
            var s = item.Value;
            if (s.steps == 0 && s.queueFrames == 0) continue;
            message.Append($"\n  {item.Key.FullName}: cpu={s.cpuMs:F2}ms maxStep={s.maxMs:F2}ms steps={s.steps} queueFrames={s.queueFrames} underBudgetQueueFrames={s.underBudgetQueueFrames}");
            s.cpuMs = s.maxMs = 0;
            s.steps = s.queueFrames = s.underBudgetQueueFrames = 0;
        }
        foreach (var item in eventCounts) message.Append($"\n  count/{item.Key}={item.Value}");
        eventCounts.Clear();
        // Avoid adding measurement overhead by capturing aggregate-log stack traces.
        UnityEngine.Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message.ToString());
        lastReportTime = now;
        lastReportFrame = Time.frameCount;
    }
    /// <summary>Cooperative generation yield. Resume after other workers if the same frame has budget remaining.</summary>
    public static readonly object Continue = new object();

    private sealed class Operation
    {
        public MonoBehaviour owner;
        public Coroutine handle, nativeWait;
        public readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
        public LinkedListNode<Operation> ticket;
        public string category;
        public int readyFrame;
        public EntityId ownerId;
        public bool blocked, done, stepping;
        public Func<bool> hasPendingWork;
    }
    private static readonly Dictionary<Coroutine, Operation> operations = new Dictionary<Coroutine, Operation>();
    // Index stopping only the relevant operations when GameObjects disable/destroy. Key is owner.gameObject instanceID at start.
    private static readonly Dictionary<EntityId, List<Operation>> operationsByObject = new Dictionary<EntityId, List<Operation>>();
    private static RenderWorkPump pump;
    private static bool quitting;
    private static int pumpedFrame = -1;
    public static double LastFrameMs { get; private set; }
    public static double MaximumStepMs { get; private set; }
    public static long Steps { get; private set; }
    public static int WaitingCount => waiting.Count + priorityWaiting.Count + lowWaiting.Count;
    public static bool HasPendingInitialWork
    {
        get
        {
            foreach (var operation in operations.Values)
                if (!operation.done && (operation.hasPendingWork == null || operation.hasPendingWork())) return true;
            return false;
        }
    }
    public static double UsedMs { get { BeginFrame(); return usedMs; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        foreach (var operation in new List<Operation>(operations.Values)) Finish(operation);
        operations.Clear();
        operationsByObject.Clear();
        quitting = false;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
        frame = pumpedFrame = -1;
        usedMs = LastFrameMs = MaximumStepMs = 0;
        Steps = 0;
        waiting.Clear();
        priorityWaiting.Clear();
        lowWaiting.Clear();
        categoryMs.Clear();
        timingSamples.Clear();
        eventCounts.Clear();
        lastReportTime = Time.realtimeSinceStartupAsDouble;
        lastReportFrame = Time.frameCount;
    }

    private static void BeginFrame()
    {
        if (frame == Time.frameCount) return;
        LastFrameMs = usedMs;
        usedMs = 0;
        frame = Time.frameCount;
    }

    public static double GetCategoryMilliseconds(string category) => categoryMs.TryGetValue(category, out double ms) ? ms : 0;

    public static Coroutine Start(MonoBehaviour owner, IEnumerator routine, string category, Func<bool> hasPendingWork = null, RenderWorkPriority priority = RenderWorkPriority.Normal)
    {
        EnsurePump();
        if (owner.GetComponent<RenderWorkLifetime>() == null) owner.gameObject.AddComponent<RenderWorkLifetime>();
        var operation = new Operation { owner = owner, category = category, hasPendingWork = hasPendingWork, readyFrame = Time.frameCount + 1, ownerId = owner.gameObject.GetEntityId() };
        operation.stack.Push(routine);
        operation.handle = owner.StartCoroutine(Lifetime(operation));
        if (operation.handle == null) { Finish(operation); return null; }
        operations.Add(operation.handle, operation);
        if (!operationsByObject.TryGetValue(operation.ownerId, out var list))
            operationsByObject[operation.ownerId] = list = new List<Operation>();
        list.Add(operation);
        operation.ticket = (priority == RenderWorkPriority.High ? priorityWaiting
            : priority == RenderWorkPriority.Low ? lowWaiting : waiting).AddLast(operation);
        return operation.handle;
    }

    private static void OnQuitting() => quitting = true;

    // Reset may run during tests; retain a live pump rather than recreating it, avoiding duplicates.
    // Do not create from destruction callbacks during shutdown, which would leave GameObjects in editor scenes after Play ends.
    private static void EnsurePump()
    {
        if (pump != null || quitting || !Application.isPlaying) return;
        var go = new GameObject("RenderWorkPump");
        UnityEngine.Object.DontDestroyOnLoad(go);
        pump = go.AddComponent<RenderWorkPump>();
    }

    // Retain a Unity handle for callers to yield return, while only the shared pump advances actual work.
    private static IEnumerator Lifetime(Operation operation)
    {
        try { while (!operation.done) yield return null; }
        finally { Finish(operation); }
    }

    public static void Stop(MonoBehaviour owner, Coroutine handle)
    {
        if (handle == null) return;
        if (operations.TryGetValue(handle, out var operation)) Finish(operation);
        if (owner != null) owner.StopCoroutine(handle);
    }

    /// <summary>Do nothing if owner is destroyed (Pump cleans operations with destroyed owners).</summary>
    public static void StopAll(MonoBehaviour owner)
    {
        if (owner == null || !operationsByObject.TryGetValue(owner.gameObject.GetEntityId(), out var list)) return;
        // Copy to protect enumeration if stopped iterators' finally blocks reenter Start/Stop and modify the index.
        foreach (var operation in list.ToArray())
            if (ReferenceEquals(operation.owner, owner)) Stop(owner, operation.handle);
    }

    internal static void StopGameObject(GameObject owner)
    {
        if (!operationsByObject.TryGetValue(owner.GetEntityId(), out var list)) return;
        foreach (var operation in list.ToArray()) Stop(operation.owner, operation.handle);
    }

    private static void Finish(Operation operation)
    {
        operation.done = true;
        if (operation.ticket?.List != null) operation.ticket.List.Remove(operation.ticket);
        operation.ticket = null;
        if (operation.handle != null) operations.Remove(operation.handle);
        // Allow operations absent from the index too (tests directly added them to dictionaries, or starting failed).
        if (operationsByObject.TryGetValue(operation.ownerId, out var list) && list.Remove(operation) && list.Count == 0)
            operationsByObject.Remove(operation.ownerId);
        if (operation.nativeWait != null && operation.owner != null) operation.owner.StopCoroutine(operation.nativeWait);
        operation.nativeWait = null;
        // For self-cancellation during MoveNext, finish executing the iterator before disposing its finally block.
        if (operation.stepping) return;
        while (operation.stack.Count > 0)
        {
            try { (operation.stack.Pop() as IDisposable)?.Dispose(); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }
    }

    private static IEnumerator WaitNative(Operation operation, object instruction)
    {
        yield return instruction;
        operation.blocked = false;
        operation.nativeWait = null;
    }

    internal static void Pump()
    {
        if (pumpedFrame == Time.frameCount) return;
        pumpedFrame = Time.frameCount;
        BeginFrame();
        long started = Stopwatch.GetTimestamp();
        PumpQueue(priorityWaiting, started);
        PumpQueue(waiting, started);
        PumpQueue(lowWaiting, started);
        if (LocalConstants.UNITY_LOG_TIMING)
        {
            CountQueueFrames(priorityWaiting);
            CountQueueFrames(waiting);
            CountQueueFrames(lowWaiting);
        }
    }

    private static void PumpQueue(LinkedList<Operation> queue, long started)
    {
        int skipped = 0;
        // Limit total pump time too, preventing unbounded waiting checks and iterator transitions.
        while (queue.Count > 0 && usedMs < PerformanceConfig.FrameBudgetMs &&
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency < PerformanceConfig.FrameBudgetMs)
        {
            Operation operation = queue.First.Value;
            queue.Remove(operation.ticket);
            queue.AddLast(operation.ticket);
            // Unity stops coroutines without finally when only the owner component is destroyed; release here.
            // Native waits stop too and blocked never clears, so check this before blocked.
            if (operation.owner == null) { Finish(operation); continue; }
            if (operation.blocked || operation.readyFrame > Time.frameCount)
            {
                if (++skipped >= queue.Count) break;
                continue;
            }
            skipped = 0;
            Step(operation);
        }
    }

    private static void CountQueueFrames(LinkedList<Operation> queue)
    {
        foreach (var operation in queue)
            if (!operation.blocked && operation.readyFrame <= Time.frameCount)
            {
                var sample = GetTimingSample(operation.stack.Peek());
                sample.queueFrames++;
                if (usedMs < PerformanceConfig.FrameBudgetMs) sample.underBudgetQueueFrames++;
            }
    }

    private static void Step(Operation operation)
    {
        IEnumerator current = operation.stack.Peek();
        TimingSample sample = GetTimingSample(current);
        long start = Stopwatch.GetTimestamp();
        bool more = false;
        operation.stepping = true;
        try { more = current.MoveNext(); }
        catch (Exception ex) { UnityEngine.Debug.LogException(ex); operation.done = true; }
        finally
        {
            operation.stepping = false;
            double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            usedMs += elapsed;
            MaximumStepMs = Math.Max(MaximumStepMs, elapsed);
            categoryMs[operation.category] = GetCategoryMilliseconds(operation.category) + elapsed;
            Steps++;
            if (elapsed > 50) UnityEngine.Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "[カメラフリーズ調査] 長い1ステップ {0:F0}ms {1} ({2})", elapsed, current.GetType().FullName, operation.category); // Remove after investigation
            if (sample != null)
            {
                sample.cpuMs += elapsed;
                sample.maxMs = Math.Max(sample.maxMs, elapsed);
                sample.steps++;
            }
        }
        if (operation.done) { Finish(operation); return; }
        if (!more)
        {
            operation.stack.Pop();
            try { (current as IDisposable)?.Dispose(); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); Finish(operation); return; }
            if (operation.stack.Count == 0) Finish(operation);
            return;
        }
        object yielded = current.Current;
        if (ReferenceEquals(yielded, Continue)) return;
        if (yielded == null) { operation.readyFrame = Time.frameCount + 1; return; }
        // CustomYieldInstruction is also IEnumerator, but follows Unity waiting conventions like communication waits.
        if (yielded is IEnumerator child && !(yielded is CustomYieldInstruction))
            operation.stack.Push(child);
        else
        {
            operation.blocked = true;
            operation.nativeWait = operation.owner.StartCoroutine(WaitNative(operation, yielded));
        }
    }
}

/// <summary>Persistent GameObject advancing the shared pump and aggregate logs once per frame. Created automatically by RenderWorkScheduler.Start.</summary>
public sealed class RenderWorkPump : MonoBehaviour
{
    private void LateUpdate()
    {
        RenderWorkScheduler.Pump();
        RenderWorkScheduler.ReportTimings();
        FreezeProbe.Check(); // [Camera freeze investigation] Remove after investigation
        BuildingStageProbe.Check(); // [Building stage measurement] Remove after investigation
    }
}

/// <summary>
/// [Building stage measurement] Temporary totals of every "Building." / "Path." profiler marker, logged every five seconds while buildings or roads are generated.
/// Totals include nested markers. Remove the entire class after investigation.
/// </summary>
internal static class BuildingStageProbe
{
    private static readonly string[] Prefixes = { "Building.", "Path." };
    private const double ReportSeconds = 5;
    private const float LongFrameSeconds = 0.05f;

    private sealed class Stage
    {
        public string name;
        public Unity.Profiling.ProfilerRecorder recorder;
        public long calls;
        public double totalMs, maxFrameMs;
    }

    private static readonly List<Stage> stages = new();
    private static readonly HashSet<string> recordedNames = new();
    private static readonly List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle> handles = new();
    private static int refreshedFrame = -100000, frames, longFrames;
    private static double windowStart = -1, maxFrameMs;

    internal static void Check()
    {
        // Markers register when their owning class first initializes, so periodically pick up new ones.
        if (Time.frameCount - refreshedFrame >= 300) Refresh();
        // Each recorder holds one per-frame sum. Read in LateUpdate, it describes the preceding frame (as in FreezeProbe).
        foreach (var stage in stages)
        {
            if (stage.recorder.Count == 0) continue;
            var sample = stage.recorder.GetSample(0);
            double ms = sample.Value * 1e-6;
            stage.calls += sample.Count;
            stage.totalMs += ms;
            stage.maxFrameMs = Math.Max(stage.maxFrameMs, ms);
        }
        double now = Time.realtimeSinceStartupAsDouble;
        if (windowStart < 0) windowStart = now;
        frames++;
        float dt = Time.unscaledDeltaTime;
        if (dt >= LongFrameSeconds) longFrames++;
        maxFrameMs = Math.Max(maxFrameMs, dt * 1000.0);
        if (now - windowStart < ReportSeconds) return;
        Report(now - windowStart);
        windowStart = now;
        frames = longFrames = 0;
        maxFrameMs = 0;
    }

    private static void Report(double seconds)
    {
        var active = stages.FindAll(stage => stage.totalMs > 0);
        if (active.Count > 0)
        {
            active.Sort((a, b) => b.totalMs.CompareTo(a.totalMs));
            var message = new System.Text.StringBuilder();
            message.Append($"[建物計測] 区間={seconds:F1}s フレーム={frames} 長いフレーム(50ms以上)={longFrames} 最長フレーム={maxFrameMs:F0}ms");
            foreach (var stage in active)
                message.Append($"\n  {stage.name}: 回数={stage.calls} 合計={stage.totalMs:F1}ms 平均={stage.totalMs / Math.Max(1, stage.calls):F2}ms 1フレーム最大={stage.maxFrameMs:F1}ms");
            UnityEngine.Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message.ToString());
        }
        foreach (var stage in stages)
        {
            stage.calls = 0;
            stage.totalMs = stage.maxFrameMs = 0;
        }
    }

    private static void Refresh()
    {
        refreshedFrame = Time.frameCount;
        handles.Clear();
        Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
        foreach (var handle in handles)
        {
            var description = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(handle);
            string name = description.Name;
            if (name == null || !Array.Exists(Prefixes, prefix => name.StartsWith(prefix, StringComparison.Ordinal)) || !recordedNames.Add(name)) continue;
            stages.Add(new Stage
            {
                name = name,
                recorder = new Unity.Profiling.ProfilerRecorder(handle, 1,
                    Unity.Profiling.ProfilerRecorderOptions.Default | Unity.Profiling.ProfilerRecorderOptions.StartImmediately),
            });
        }
    }
}

/// <summary>
/// [Camera freeze investigation] Temporary profiler-marker breakdown of long frames. Remove the entire class after investigation.
/// LastValue, unscaledDeltaTime, and LastFrameMs read in LateUpdate all describe the preceding frame.
/// Records every time marker of every category, so internal work under a phase marker (such as deferred destruction) also appears.
/// The output is a flat list without nesting; parents and their children both appear.
/// </summary>
internal static class FreezeProbe
{
    private const float HitchSeconds = 0.05f;
    private static readonly List<(string name, Unity.Profiling.ProfilerRecorder recorder)> recorders = new();
    private static readonly HashSet<string> recordedNames = new();
    private static readonly List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle> handles = new();
    private static int refreshedFrame = -100000, lastGcCount, lastWarningCount;
    private static bool subscribed;
    public static int WarningCount;

    internal static void Check()
    {
        if (!subscribed)
        {
            Application.logMessageReceived += (condition, stack, type) => { if (type == LogType.Warning) WarningCount++; };
            subscribed = true;
        }
        // Some markers register only after executing, so periodically refresh the target list.
        if (Time.frameCount - refreshedFrame >= 300) Refresh();
        int gc = GC.CollectionCount(0), warnings = WarningCount;
        float dt = Time.unscaledDeltaTime;
        if (dt >= HitchSeconds)
        {
            var top = new List<(string name, double ms)>();
            foreach (var (name, recorder) in recorders)
            {
                if (!recorder.Valid) continue;
                double ms = recorder.LastValue * 1e-6;
                if (ms >= 5) top.Add((name, ms));
            }
            top.Sort((a, b) => b.ms.CompareTo(a.ms));
            var message = new System.Text.StringBuilder();
            message.Append($"[カメラフリーズ調査] 長いフレーム {dt * 1000:F0}ms 共通予算の使用={RenderWorkScheduler.LastFrameMs:F0}ms GC={gc - lastGcCount}回 警告ログ={warnings - lastWarningCount}件 frame={Time.frameCount - 1}");
            foreach (var (name, ms) in top) message.Append($"\n  {name}={ms:F1}ms");
            UnityEngine.Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message.ToString());
        }
        lastGcCount = gc;
        lastWarningCount = warnings;
    }

    private static void Refresh()
    {
        refreshedFrame = Time.frameCount;
        handles.Clear();
        Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
        foreach (var handle in handles)
        {
            var description = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(handle);
            if (description.UnitType != Unity.Profiling.ProfilerMarkerDataUnit.TimeNanoseconds) continue;
            string name = description.Name;
            if (string.IsNullOrEmpty(name) || !recordedNames.Add(name)) continue;
            recorders.Add((name, new Unity.Profiling.ProfilerRecorder(handle, 1,
                Unity.Profiling.ProfilerRecorderOptions.Default | Unity.Profiling.ProfilerRecorderOptions.StartImmediately)));
        }
    }
}

/// <summary>Convert GameObject hiding/destruction into explicit coroutine disposal too.</summary>
public sealed class RenderWorkLifetime : MonoBehaviour
{
    private void OnDisable() => RenderWorkScheduler.StopGameObject(gameObject);
    private void OnDestroy() => RenderWorkScheduler.StopGameObject(gameObject);
}
