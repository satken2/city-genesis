using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// メインスレッドを塞がずに済ませたい重い計算の実行窓口。
/// デスクトップはスレッドプール(Task.Run)で実行する。Web ビルドには C# のスレッドが無いので、呼んだその場で実行し、
/// 完了済みの Task を返す。呼び出し側は環境を問わず Task の完了を待つだけでよい。
/// 渡す処理は Unity API に触れないこと(デスクトップでは別スレッドで動くため)。
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
    /// <summary>Task.Run と同じく、取り消し済みなら実行せず、処理が同じトークンで取り消されたら取り消し扱いにする。</summary>
    private static Task<T> RunInline<T>(Func<T> work, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        try { return Task.FromResult(work()); }
        catch (OperationCanceledException e) when (e.CancellationToken == token) { return Task.FromCanceled<T>(token); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }
#endif
}
