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
    /// <summary>新しいバッチの開始によりキャンセルされたかどうか。</summary>
    public bool IsCanceled { get; private set; }

    public LoadBatch(LodCellSets cellSets, bool isForce)
    {
    }

    public void Cancel()
    {
        IsCanceled = true;
    }
}
