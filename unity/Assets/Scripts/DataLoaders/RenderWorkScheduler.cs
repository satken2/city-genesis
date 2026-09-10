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
    private static readonly Dictionary<string, double> categoryMs = new Dictionary<string, double>();
    private sealed class TimingSample
    {
        public double cpuMs, maxMs;
        public long steps, queueFrames, underBudgetQueueFrames;
    }
    private static readonly Dictionary<string, TimingSample> timingSamples = new Dictionary<string, TimingSample>();
    private static double lastReportTime;
    private static int lastReportFrame;

    private static TimingSample GetTimingSample(IEnumerator routine)
    {
        if (!LocalConstants.UNITY_LOG_TIMING) return null;
        string key = routine.GetType().FullName;
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
        message.Append($"[TIMING] Scheduler window={now - lastReportTime:F2}s frames={Time.frameCount - lastReportFrame} budget={PerformanceConfig.FrameBudgetMs:F2}ms active={operations.Count} waiting={waiting.Count}");
        var loader = CellDataLoader.Instance;
        if (loader != null)
            message.Append($" buildingsPending={loader.buildingManager?.PendingDetailCount ?? 0} pathsPending={loader.pathManager?.PendingTerrainRebuildCount ?? 0} pathBatch={loader.pathManager?.IsLodBatchActive ?? false}");
        foreach (var item in timingSamples)
        {
            var s = item.Value;
            if (s.steps == 0 && s.queueFrames == 0) continue;
            message.Append($"\n  {item.Key}: cpu={s.cpuMs:F2}ms maxStep={s.maxMs:F2}ms steps={s.steps} queueFrames={s.queueFrames} underBudgetQueueFrames={s.underBudgetQueueFrames}");
            s.cpuMs = s.maxMs = 0;
            s.steps = s.queueFrames = s.underBudgetQueueFrames = 0;
        }
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
        public bool blocked, done, stepping;
    }
    private static readonly Dictionary<Coroutine, Operation> operations = new Dictionary<Coroutine, Operation>();
    private static int pumpedFrame = -1;
    public static double LastFrameMs { get; private set; }
    public static double MaximumStepMs { get; private set; }
    public static long Steps { get; private set; }
    public static int WaitingCount => waiting.Count;
    public static double UsedMs { get { BeginFrame(); return usedMs; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        foreach (var operation in new List<Operation>(operations.Values)) Finish(operation);
        operations.Clear();
        frame = pumpedFrame = -1;
        usedMs = LastFrameMs = MaximumStepMs = 0;
        Steps = 0;
        waiting.Clear();
        categoryMs.Clear();
        timingSamples.Clear();
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

    public static Coroutine Start(MonoBehaviour owner, IEnumerator routine, string category)
    {
        if (owner.GetComponent<RenderWorkLifetime>() == null) owner.gameObject.AddComponent<RenderWorkLifetime>();
        var operation = new Operation { owner = owner, category = category, readyFrame = Time.frameCount + 1 };
        operation.stack.Push(routine);
        operation.handle = owner.StartCoroutine(Lifetime(operation));
        if (operation.handle == null) { Finish(operation); return null; }
        operations.Add(operation.handle, operation);
        operation.ticket = waiting.AddLast(operation);
        return operation.handle;
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

    public static void StopAll(MonoBehaviour owner)
    {
        foreach (var item in new List<KeyValuePair<Coroutine, Operation>>(operations))
            if (item.Value.owner == owner) Stop(owner, item.Key);
    }

    internal static void StopGameObject(GameObject owner)
    {
        foreach (var item in new List<KeyValuePair<Coroutine, Operation>>(operations))
            if (item.Value.owner == null || item.Value.owner.gameObject == owner) Stop(item.Value.owner, item.Key);
    }

    private static void Finish(Operation operation)
    {
        operation.done = true;
        if (operation.ticket?.List != null) waiting.Remove(operation.ticket);
        operation.ticket = null;
        if (operation.handle != null) operations.Remove(operation.handle);
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
        int skipped = 0;
        // 待機確認やiteratorの出入り自体も無制限に回さないよう、ポンプ全体の時間でも制限する。
        while (waiting.Count > 0 && usedMs < PerformanceConfig.FrameBudgetMs &&
            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency < PerformanceConfig.FrameBudgetMs)
        {
            Operation operation = waiting.First.Value;
            waiting.Remove(operation.ticket);
            waiting.AddLast(operation.ticket);
            if (operation.blocked || operation.readyFrame > Time.frameCount)
            {
                if (++skipped >= waiting.Count) break;
                continue;
            }
            skipped = 0;
            Step(operation);
        }
        if (LocalConstants.UNITY_LOG_TIMING)
            foreach (var operation in waiting)
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

/// <summary>GameObjectの非表示・破棄も明示的なコルーチン破棄へ変換する。</summary>
public sealed class RenderWorkLifetime : MonoBehaviour
{
    private void LateUpdate()
    {
        RenderWorkScheduler.Pump();
        RenderWorkScheduler.ReportTimings();
    }
    private void OnDisable() => RenderWorkScheduler.StopGameObject(gameObject);
    private void OnDestroy() => RenderWorkScheduler.StopGameObject(gameObject);
}
