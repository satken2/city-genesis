using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Entry point for expensive calculations that should avoid blocking the main thread.
/// Desktop builds use the thread pool (Task.Run). Web builds lack C# threads, so execute inline
/// and return a completed Task. Callers simply await Task completion in either environment.
/// Work must not access Unity APIs because desktop builds execute it on another thread.
/// </summary>
public static class BackgroundWork
{
    public static Task Run(Action work, CancellationToken token = default)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return RunInline<object>(() => { work(); return null; }, token);
#else
        return Task.Run(work, token);
#endif
    }

    public static Task<T> Run<T>(Func<T> work, CancellationToken token = default)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return RunInline(work, token);
#else
        return Task.Run(work, token);
#endif
    }

    public static Task Run(Func<Task> work)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        try { return work(); }
        catch (Exception e) { return Task.FromException(e); }
#else
        return Task.Run(work);
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    /// <summary>As with Task.Run, skip work if already canceled and treat cancellation with the same token as task cancellation.</summary>
    private static Task<T> RunInline<T>(Func<T> work, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        try { return Task.FromResult(work()); }
        catch (OperationCanceledException e) when (e.CancellationToken == token) { return Task.FromCanceled<T>(token); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }
#endif
}
