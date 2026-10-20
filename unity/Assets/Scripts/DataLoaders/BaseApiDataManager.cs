using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

/// <summary>
/// Abstract class encapsulating shared cell management logic.
/// Subclasses override LoadLodCells (LOD-aware) or LoadCells (without LOD)
/// to access the API and render, and use UnloadCell to discard out-of-range cells.
/// Route all HTTP fetching through ApiClient (ApiClient.GetBulk for bulk fetching).
/// </summary>
public abstract class BaseApiDataManager : MonoBehaviour
{
    /// <summary>Cells currently loaded by this manager</summary>
    protected HashSet<Vector2Int> loadedCells = new HashSet<Vector2Int>();
    
    // Container cache
    private Dictionary<string, Transform> containerCache = new Dictionary<string, Transform>();

    /// <summary>
    /// Get or create a container object in GameScene.
    /// </summary>
    protected Transform GetContainer(string name)
    {
        if (containerCache.TryGetValue(name, out Transform cachedContainer) && cachedContainer != null)
        {
            return cachedContainer;
        }

        // Check whether GameScene is loaded
        Scene gameScene = SceneManager.GetSceneByName(SceneNames.Game);
        if (!gameScene.IsValid() || !gameScene.isLoaded)
        {
            // Return null if GameScene is unavailable (create at the root)
            return null;
        }

        // Find the container
        // Search root objects only
        GameObject[] rootObjects = gameScene.GetRootGameObjects();
        foreach (var obj in rootObjects)
        {
            if (obj.name == name)
            {
                containerCache[name] = obj.transform;
                return obj.transform;
            }
        }

        // Create it if not found
        GameObject container = new GameObject(name);
        SceneManager.MoveGameObjectToScene(container, gameScene);
        containerCache[name] = container.transform;
        return container.transform;
    }

    /// <summary>
    /// Load, unload, and incrementally update data using the specified cells.
    /// Fallback for managers without LOD support (ZoneManager).
    /// LOD-aware managers override LoadLodCells directly,
    /// so they do not pass through this base implementation.
    /// </summary>
    /// <param name="neededCells">List of required cells</param>
    /// <param name="isForce">Whether to force a reload</param>
    public virtual void LoadCells(List<Vector2Int> neededCells, bool isForce)
    {
    }

    /// <summary>
    /// Shared method for calculating cell bounds
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
    /// Shared method for checking whether an object is within the specified cell bounds
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
    /// Shared method for checking whether an object is within the specified coordinate bounds.
    /// Uses coordinates directly rather than cell indices, for checks such as cell boundaries during unloading.
    /// </summary>
    protected bool IsObjectInCell(GameObject obj, float startX, float endX, float startZ, float endZ)
    {
        Vector3 position = obj.transform.position;
        return position.x >= startX && position.x < endX && 
               position.z >= startZ && position.z < endZ;
    }

    /// <summary>
    /// Load cell sets grouped by LOD tier.
    /// Managers without LOD support fall back to the default implementation, which uses only nearCells.
    /// </summary>
    public virtual void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        LoadCells(cellSets.nearCells, isForce);
    }

    /// <summary>
    /// Load entry point that updates 3D objects only for cells whose LOD tier changed.
    /// When the camera moves, first calculate cell tiers; only if any have changed,
    /// let each manager batch-update the objects associated with those cells.
    /// Managers without optimizations based on changed cells fall back to checking the entire set.
    /// </summary>
    public virtual void LoadLodCells(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce)
    {
        LoadLodCells(cellSets, isForce);
    }

    /// <summary>
    /// Current load batch. CellDataLoader issues one per UpdateManagers call
    /// and cancels the previous batch when a new one starts.
    /// Each manager load coroutine checks IsCanceled after yielding
    /// and stops cooperatively (StopCoroutine cannot stop child coroutines launched with StartCoroutine,
    /// so flag-based cooperative cancellation is required).
    /// </summary>
    protected LoadBatch currentLoadBatch;

    /// <summary>
    /// Entry point that associates a load batch and starts loading.
    /// CellDataLoader uses this overload to pass the same batch to every manager.
    /// </summary>
    public void LoadLodCells(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce, LoadBatch batch)
    {
        currentLoadBatch = batch;
        LoadLodCells(cellSets, tierChangedCells, isForce);
    }

    /// <summary>
    /// Combine BBOXes for multiple cells into one large BBOX.
    /// Used when fetching a group of cells in a single API call.
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
    /// Abstract method for clearing all data
    /// </summary>
    public abstract void ClearAllCells();

    /// <summary>
    /// Discard data for the specified cell.
    /// </summary>
    protected abstract void UnloadCell(Vector2Int cellIndex);
}
