using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

/// <summary>
/// 描画準備のコルーチンを共通予算で進める。通信待ちやフレーム待ちは計測に含めず、
/// MoveNext中の同期処理だけを合算する。個別の重い生成器は別途小さな処理単位に分ける。
/// </summary>
public static class RenderWorkScheduler
{
    private static int frame = -1;
    private static double usedMs;
    private static readonly LinkedList<Operation> waiting = new LinkedList<Operation>();
    /// <summary>毎フレーム通常の仕事より先に進める仕事(地形タイルの公開)。物体の生成より先に地形を揃えるため。</summary>
    private static readonly LinkedList<Operation> priorityWaiting = new LinkedList<Operation>();
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

    /// <summary>固定名の件数を5秒ログへ集約する。物体IDを名前に含めず、無効時は蓄積しない。</summary>
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
        // 集約ログ自身のスタック採取で計測対象へ負荷を加えない。
        UnityEngine.Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message.ToString());
        lastReportTime = now;
        lastReportFrame = Time.frameCount;
    }
    /// <summary>生成の協調中断。同じフレームの予算が残れば、他ワーカーの後で再開する。</summary>
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
    // GOの無効化・破棄時に該当operationだけを止めるための索引。キーは開始時のowner.gameObjectのinstanceID。
    private static readonly Dictionary<EntityId, List<Operation>> operationsByObject = new Dictionary<EntityId, List<Operation>>();
    private static RenderWorkPump pump;
    private static bool quitting;
    private static int pumpedFrame = -1;
    public static double LastFrameMs { get; private set; }
    public static double MaximumStepMs { get; private set; }
    public static long Steps { get; private set; }
    public static int WaitingCount => waiting.Count + priorityWaiting.Count;
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

    /// <param name="priority">真なら毎フレーム通常の仕事より先に進める。</param>
    public static Coroutine Start(MonoBehaviour owner, IEnumerator routine, string category, Func<bool> hasPendingWork = null, bool priority = false)
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
        operation.ticket = (priority ? priorityWaiting : waiting).AddLast(operation);
        return operation.handle;
    }

    private static void OnQuitting() => quitting = true;

    // Resetはテストの途中でも呼ばれるため、生きているポンプは作り直さず使い続ける(重複生成の防止)。
    // 終了処理中の破棄コールバックから作ると、エディタでは再生終了後のシーンへGOが残るため作らない。
    private static void EnsurePump()
    {
        if (pump != null || quitting || !Application.isPlaying) return;
        var go = new GameObject("RenderWorkPump");
        UnityEngine.Object.DontDestroyOnLoad(go);
        pump = go.AddComponent<RenderWorkPump>();
    }

    // 呼出側がyield returnできるUnityのハンドルを保ち、実処理は共通ポンプだけで進める。
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

    /// <summary>ownerが破棄済みなら何もしない(破棄済みownerのoperationはPumpが掃除する)。</summary>
    public static void StopAll(MonoBehaviour owner)
    {
        if (owner == null || !operationsByObject.TryGetValue(owner.gameObject.GetEntityId(), out var list)) return;
        // 停止時のiteratorのfinallyがStart/Stopを再入して索引を書き換えても列挙が壊れないよう複製する。
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
        // 索引に無いoperation(テストが辞書へ直接追加したもの・開始に失敗したもの)も許す。
        if (operationsByObject.TryGetValue(operation.ownerId, out var list) && list.Remove(operation) && list.Count == 0)
            operationsByObject.Remove(operation.ownerId);
        if (operation.nativeWait != null && operation.owner != null) operation.owner.StopCoroutine(operation.nativeWait);
        operation.nativeWait = null;
        // MoveNext中の自己取消しでは、iteratorを実行し終えてからfinallyを解放する。
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
        if (LocalConstants.UNITY_LOG_TIMING)
        {
            CountQueueFrames(priorityWaiting);
            CountQueueFrames(waiting);
        }
    }

    private static void PumpQueue(LinkedList<Operation> queue, long started)
    {
        int skipped = 0;
        // 待機確認やiteratorの出入り自体も無制限に回さないよう、ポンプ全体の時間でも制限する。
        while (queue.Count > 0 && usedMs < PerformanceConfig.FrameBudgetMs &&
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency < PerformanceConfig.FrameBudgetMs)
        {
            Operation operation = queue.First.Value;
            queue.Remove(operation.ticket);
            queue.AddLast(operation.ticket);
            // コンポーネントだけ破棄されたownerはUnityがコルーチンを止めるだけでfinallyを通らないため、ここで解放する。
            // ネイティブ待機も一緒に止まりblockedが解けないので、blockedの判定より前に確認する。
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
            if (elapsed > 50) UnityEngine.Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "[カメラフリーズ調査] 長い1ステップ {0:F0}ms {1} ({2})", elapsed, current.GetType().FullName, operation.category); // 調査後に削除
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
        // CustomYieldInstructionもIEnumeratorだが、通信待ちと同じUnityの待機規約を使う。
        if (yielded is IEnumerator child && !(yielded is CustomYieldInstruction))
            operation.stack.Push(child);
        else
        {
            operation.blocked = true;
            operation.nativeWait = operation.owner.StartCoroutine(WaitNative(operation, yielded));
        }
    }
}

/// <summary>共通ポンプと集約ログを1フレームに1回進める常駐GO。RenderWorkScheduler.Startが自動生成する。</summary>
public sealed class RenderWorkPump : MonoBehaviour
{
    private void LateUpdate()
    {
        RenderWorkScheduler.Pump();
        RenderWorkScheduler.ReportTimings();
        FreezeProbe.Check(); // [カメラフリーズ調査] 調査後に削除
    }
}

/// <summary>
/// [カメラフリーズ調査] 長いフレームの内訳をプロファイラのマーカーから出す一時計測。調査後にクラスごと削除する。
/// LateUpdateで見るLastValue・unscaledDeltaTime・LastFrameMsはどれも直前のフレームの値。
/// </summary>
internal static class FreezeProbe
{
    private const float HitchSeconds = 0.2f;
    private static readonly List<(string name, Unity.Profiling.ProfilerRecorder recorder)> recorders = new();
    private static readonly HashSet<string> recordedNames = new();
    private static readonly List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle> handles = new();
    private static readonly string[] phasePrefixes = { "Initialization.", "EarlyUpdate.", "FixedUpdate.", "PreUpdate.", "Update.", "PreLateUpdate.", "PostLateUpdate." };
    private static readonly HashSet<string> categories = new() { "Scripts", "Physics", "GC", "Loading" };
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
        // 実行されて初めて登録されるマーカーがあるため、対象の一覧を定期的に取り直す。
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
            for (int i = 0; i < top.Count && i < 25; i++) message.Append($"\n  {top[i].name}={top[i].ms:F1}ms");
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
            if (string.IsNullOrEmpty(name) || recordedNames.Contains(name)) continue;
            bool wanted = name == "PlayerLoop" || name == "EditorLoop" || categories.Contains(description.Category.Name);
            foreach (var prefix in phasePrefixes) wanted |= name.StartsWith(prefix, StringComparison.Ordinal);
            if (!wanted) continue;
            recordedNames.Add(name);
            recorders.Add((name, new Unity.Profiling.ProfilerRecorder(handle, 1,
                Unity.Profiling.ProfilerRecorderOptions.Default | Unity.Profiling.ProfilerRecorderOptions.StartImmediately)));
        }
    }
}

/// <summary>GameObjectの非表示・破棄も明示的なコルーチン破棄へ変換する。</summary>
public sealed class RenderWorkLifetime : MonoBehaviour
{
    private void OnDisable() => RenderWorkScheduler.StopGameObject(gameObject);
    private void OnDestroy() => RenderWorkScheduler.StopGameObject(gameObject);
}
