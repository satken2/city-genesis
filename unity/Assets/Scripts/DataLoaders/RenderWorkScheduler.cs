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
    private static readonly LinkedList<object> waiting = new LinkedList<object>();
    private static readonly Dictionary<string, double> categoryMs = new Dictionary<string, double>();
    private sealed class Operation
    {
        public MonoBehaviour owner;
        public IEnumerator routine;
    }
    private static readonly Dictionary<Coroutine, Operation> operations = new Dictionary<Coroutine, Operation>();
    public static double LastFrameMs { get; private set; }
    public static double MaximumStepMs { get; private set; }
    public static long Steps { get; private set; }
    public static int WaitingCount => waiting.Count;
    public static double UsedMs { get { BeginFrame(); return usedMs; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        foreach (var operation in new List<Operation>(operations.Values)) (operation.routine as IDisposable)?.Dispose();
        operations.Clear();
        frame = -1;
        usedMs = LastFrameMs = MaximumStepMs = 0;
        Steps = 0;
        waiting.Clear();
        categoryMs.Clear();
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
        Coroutine handle = null;
        IEnumerator scheduled = Run(routine, category, () => { if (handle != null) operations.Remove(handle); });
        handle = owner.StartCoroutine(scheduled);
        operations.Add(handle, new Operation { owner = owner, routine = scheduled });
        return handle;
    }

    public static void Stop(MonoBehaviour owner, Coroutine handle)
    {
        if (handle == null) return;
        if (owner != null) owner.StopCoroutine(handle);
        if (operations.TryGetValue(handle, out var operation))
        {
            operations.Remove(handle);
            (operation.routine as IDisposable)?.Dispose();
        }
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

    private static IEnumerator Run(IEnumerator routine, string category, Action completed)
    {
        // StartCoroutineの同期開始を避け、呼び出し元との二重計測・ハンドル代入前の完了を防ぐ。
        yield return null;
        var stack = new Stack<IEnumerator>();
        stack.Push(routine);
        LinkedListNode<object> ticket = null;
        try
        {
            while (stack.Count > 0)
            {
                ticket = waiting.AddLast(new object());
                while (true)
                {
                    BeginFrame();
                    if (ticket == waiting.First && usedMs < PerformanceConfig.FrameBudgetMs) break;
                    yield return null;
                }
                waiting.Remove(ticket);
                ticket = null;
                IEnumerator current = stack.Peek();
                long start = Stopwatch.GetTimestamp();
                bool more;
                try { more = current.MoveNext(); }
                finally
                {
                    double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    usedMs += elapsed;
                    MaximumStepMs = Math.Max(MaximumStepMs, elapsed);
                    categoryMs[category] = GetCategoryMilliseconds(category) + elapsed;
                    Steps++;
                }
                if (!more)
                {
                    stack.Pop();
                    (current as IDisposable)?.Dispose();
                }
                else if (current.Current is IEnumerator child)
                {
                    stack.Push(child);
                }
                else
                {
                    yield return current.Current;
                }
            }
        }
        finally
        {
            if (ticket != null && ticket.List != null) waiting.Remove(ticket);
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            completed();
        }
    }
}

/// <summary>GameObjectの非表示・破棄も明示的なコルーチン破棄へ変換する。</summary>
public sealed class RenderWorkLifetime : MonoBehaviour
{
    private void OnDisable() => RenderWorkScheduler.StopGameObject(gameObject);
    private void OnDestroy() => RenderWorkScheduler.StopGameObject(gameObject);
}
