/// <summary>
/// Load batch issued on camera movement or forced reload.
/// CellDataLoader creates one per UpdateManagers call and passes it to manager load coroutines.
///
/// Responsibilities:
/// - Starting a new batch cancels the old one;
///   old coroutines check IsCanceled and stop cooperatively.
/// - Unity StopCoroutine cannot stop child coroutines launched with StartCoroutine,
///   so stopping only the parent leaves old children running (such as TerrainManager parallel HTTP loads).
///   Children check IsCanceled after every yield and exit voluntarily,
///   preventing remnants of old batches from conflicting with new state (terrainTiles / cellLodLevel, etc.)
///   or continuing expensive GPU uploads (SetHeights) for cells that left the range.
/// </summary>
public class LoadBatch
{
    private static int nextId;
    private readonly int id = ++nextId;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Elapsed time since batch start. Track waits and resumptions in each stage under the same ID.</summary>
    public void Trace(string stage)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING,
            () => $"[TIMING] LoadBatch#{id} {stage} elapsed={elapsed.ElapsedMilliseconds}ms frame={UnityEngine.Time.frameCount}");
    }
    /// <summary>Whether a newer batch canceled this one.</summary>
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
