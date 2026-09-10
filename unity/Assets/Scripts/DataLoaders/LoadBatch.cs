/// <summary>
/// カメラ移動や強制リロードごとに発行される「ロードバッチ」。
/// CellDataLoader が1回の UpdateManagers につき1つ生成し、各マネージャーのロードコルーチンに渡す。
///
/// 役割:
/// - 新しいバッチが始まると古いバッチは Cancel され、
///   旧バッチのコルーチンは IsCanceled を見て協調的に停止する。
/// - Unity の StopCoroutine は「StartCoroutine で起動した子コルーチン」までは停止できないため、
///   親の停止だけでは旧バッチの子コルーチン(TerrainManager の並行HTTPロードなど)が走り続けてしまう。
///   子コルーチンは yield をまたぐたびに IsCanceled を確認して自発的に抜けることで、
///   旧バッチの残骸が新バッチの状態(terrainTiles / cellLodLevel 等)と競合したり、
///   範囲外に出たセルへの重いGPUアップロード(SetHeights)を続けたりするのを防ぐ。
/// </summary>
public class LoadBatch
{
    private static int nextId;
    private readonly int id = ++nextId;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>バッチ開始からの経過時間。各工程の待ち合わせと再開を同じIDで追跡する。</summary>
    public void Trace(string stage)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING,
            () => $"[TIMING] LoadBatch#{id} {stage} elapsed={elapsed.ElapsedMilliseconds}ms frame={UnityEngine.Time.frameCount}");
    }
    /// <summary>新しいバッチの開始によりキャンセルされたかどうか。</summary>
    public bool IsCanceled { get; private set; }

    public LoadBatch(LodCellSets cellSets, bool isForce)
    {
        Trace($"START cells={cellSets.farCells?.Count ?? 0} force={isForce}");
    }

    public void Cancel()
    {
        if (!IsCanceled) Trace("SUPERSEDED/RESET");
        IsCanceled = true;
    }
}
