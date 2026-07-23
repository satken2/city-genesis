using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

/// <summary>
/// 共通のセル管理ロジックをまとめた抽象クラス。
/// 継承先が LoadLodCells(LOD対応) または LoadCells(LOD非対応) をオーバーライドして
/// APIアクセス・描画を行い、範囲外セルの破棄は UnloadCell で行う。
/// HTTP取得は ApiClient(一括取得は ApiClient.GetBulk)に集約すること。
/// </summary>
public abstract class BaseApiDataManager : MonoBehaviour
{
    /// <summary>このマネージャーにおける「ロード済みセル」一覧</summary>
    protected HashSet<Vector2Int> loadedCells = new HashSet<Vector2Int>();
    
    // コンテナのキャッシュ
    private Dictionary<string, Transform> containerCache = new Dictionary<string, Transform>();

    /// <summary>
    /// GameScene内のコンテナオブジェクトを取得または作成する。
    /// </summary>
    protected Transform GetContainer(string name)
    {
        if (containerCache.TryGetValue(name, out Transform cachedContainer) && cachedContainer != null)
        {
            return cachedContainer;
        }

        // GameSceneがロードされているか確認
        Scene gameScene = SceneManager.GetSceneByName(SceneNames.Game);
        if (!gameScene.IsValid() || !gameScene.isLoaded)
        {
            // GameSceneがない場合はnullを返す（ルート生成）
            return null;
        }

        // コンテナを探す
        // ルートオブジェクトのみを検索対象とする
        GameObject[] rootObjects = gameScene.GetRootGameObjects();
        foreach (var obj in rootObjects)
        {
            if (obj.name == name)
            {
                containerCache[name] = obj.transform;
                return obj.transform;
            }
        }

        // 見つからなければ作成
        GameObject container = new GameObject(name);
        SceneManager.MoveGameObjectToScene(container, gameScene);
        containerCache[name] = container.transform;
        return container.transform;
    }

    /// <summary>
    /// 指定されたセル一覧に基づき、データのロード・アンロード・差分更新を行う。
    /// LOD非対応マネージャー(ZoneManager)用のフォールバック。
    /// LOD対応マネージャーは LoadLodCells を直接オーバーライドするため、
    /// この base 実装は通らない。
    /// </summary>
    /// <param name="neededCells">必要なセルのリスト</param>
    /// <param name="isForce">強制リロードを行うかどうか</param>
    public virtual void LoadCells(List<Vector2Int> neededCells, bool isForce)
    {
    }

    /// <summary>
    /// セル範囲を計算する共通メソッド
    /// </summary>
    protected void CalculateCellBounds(Vector2Int cellIndex, out float startX, out float endX, out float startZ, out float endZ)
    {
        float cellSize = TerrainManager.CellSize;
        startX = cellIndex.x * cellSize;
        endX = (cellIndex.x + 1) * cellSize;
        startZ = cellIndex.y * cellSize;
        endZ = (cellIndex.y + 1) * cellSize;
    }

    /// <summary>
    /// オブジェクトが指定セル範囲内にあるかどうかを判定する共通メソッド
    /// </summary>
    protected bool IsObjectInCells(GameObject obj, List<Vector2Int> cells)
    {
        Vector3 position = obj.transform.position;
        
        foreach (var cell in cells)
        {
            CalculateCellBounds(cell, out float startX, out float endX, out float startZ, out float endZ);
            
            if (position.x >= startX && position.x < endX && 
                position.z >= startZ && position.z < endZ)
            {
                return true;
            }
        }
        
        return false;
    }

    /// <summary>
    /// オブジェクトが指定座標範囲内にあるかどうかを判定する共通メソッド。
    /// セルインデックスではなく直接座標で判定するため、アンロード時のセル境界チェックなどで利用する。
    /// </summary>
    protected bool IsObjectInCell(GameObject obj, float startX, float endX, float startZ, float endZ)
    {
        Vector3 position = obj.transform.position;
        return position.x >= startX && position.x < endX && 
               position.z >= startZ && position.z < endZ;
    }

    /// <summary>
    /// LODティア別のセルセットを受け取ってロードする。
    /// LOD非対応マネージャーはnearCellsのみを使うデフォルト実装にフォールバックする。
    /// </summary>
    public virtual void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        LoadCells(cellSets.nearCells, isForce);
    }

    /// <summary>
    /// LODティアが変わったセルだけを3D更新の起点にするためのロード入口。
    /// カメラ移動時はセルのティア計算だけを先に行い、変化したセルがある場合だけ、
    /// そのセルに関係するオブジェクトを各マネージャー側でまとめて更新する。
    /// 個別のマネージャーが変更セルを使った最適化を持たない場合は、従来の全体判定にフォールバックする。
    /// </summary>
    public virtual void LoadLodCells(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce)
    {
        LoadLodCells(cellSets, isForce);
    }

    /// <summary>
    /// 現在のロードバッチ。CellDataLoader が UpdateManagers ごとに発行し、
    /// 新しいバッチの開始時に古いバッチを Cancel する。
    /// 各マネージャーのロードコルーチンは、yield をまたいだ後に IsCanceled を確認して
    /// 協調的に停止する(StopCoroutine は StartCoroutine で起動した子コルーチンを
    /// 停止できないため、フラグによる協調キャンセルが必要)。
    /// </summary>
    protected LoadBatch currentLoadBatch;

    /// <summary>
    /// ロードバッチを紐づけてロードを開始する入口。
    /// CellDataLoader はこのオーバーロードを使い、全マネージャーに同一のバッチを配る。
    /// </summary>
    public void LoadLodCells(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce, LoadBatch batch)
    {
        currentLoadBatch = batch;
        LoadLodCells(cellSets, tierChangedCells, isForce);
    }

    /// <summary>
    /// 複数セルのBBOXを結合して1つの大きなBBOXを計算する。
    /// セル群を1回のAPIコールで一括取得する際に使用する。
    /// </summary>
    protected void CalculateMergedBBox(List<Vector2Int> cells, out float minX, out float minZ, out float maxX, out float maxZ)
    {
        minX = float.MaxValue;
        minZ = float.MaxValue;
        maxX = float.MinValue;
        maxZ = float.MinValue;
        float cellSize = TerrainManager.CellSize;

        foreach (var cell in cells)
        {
            float sx = cell.x * cellSize;
            float sz = cell.y * cellSize;
            float ex = (cell.x + 1) * cellSize;
            float ez = (cell.y + 1) * cellSize;

            if (sx < minX) minX = sx;
            if (sz < minZ) minZ = sz;
            if (ex > maxX) maxX = ex;
            if (ez > maxZ) maxZ = ez;
        }
    }

    /// <summary>
    /// 指定位置が属するセルインデックスを計算する。
    /// </summary>
    protected Vector2Int PositionToCell(Vector3 position)
    {
        float cellSize = TerrainManager.CellSize;
        return new Vector2Int(Mathf.FloorToInt(position.x / cellSize), Mathf.FloorToInt(position.z / cellSize));
    }

    /// <summary>
    /// 全データをクリアする抽象メソッド
    /// </summary>
    public abstract void ClearAllCells();

    /// <summary>
    /// 指定セルのデータを破棄する。
    /// </summary>
    protected abstract void UnloadCell(Vector2Int cellIndex);
}
