using UnityEngine;

public class CameraCellChecker : MonoBehaviour
{
    public static CameraCellChecker Instance { get; private set; }

    [SerializeField] private CellDataLoader cellDataLoader;
    [SerializeField] private TerrainManager terrainManager;
    [SerializeField] private Camera gameCamera;

    // Index of the ground cell currently viewed by the camera.
    public Vector2Int CurrentCellIndex { get; private set; }

    // Camera position projected onto the horizontal plane (y=0), in world coordinates.
    // Used by LodConfig for cell-boundary checks.
    public Vector3 CurrentWorldPosition { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private LodViewState viewState;
    private float nextCheck;

    /// <summary>
    /// Reset view-change detection to its uninitialized state. The next Update reports a change
    /// even if the camera is stationary, triggering the cell notification that starts loading.
    /// Called both when clearing state and resuming loading, so a new city starts loading without camera movement
    /// after a session switch resets CellDataLoader's current cell.
    /// </summary>
    public void ResetViewState()
    {
        viewState = default;
        nextCheck = 0f;
    }

    void Update()
    {
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsGameStarted()) return;
        if (Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.1f;
        if (!viewState.Update(gameCamera)) return;
        Vector3 centerPoint;
        if (!DriveMode.TryGetLoadCenter(out centerPoint) && !TryGetGroundHitPoint(out centerPoint)) centerPoint = gameCamera.transform.position;
        CurrentWorldPosition = centerPoint;
        CurrentCellIndex = TerrainManager.WorldToCell(centerPoint);
        OnCellIndexChanged(CurrentCellIndex);
    }

    /// <summary>
    /// Calculate the intersection of a ray from the camera center with the mathematical ground plane (y=0).
    /// Do not detect terrain colliders using Physics.Raycast.
    /// Keeping the intersection independent of terrain loading prevents the cell-boundary feedback loop
    /// "terrain reload -> collider change -> raycast result change -> reload"
    /// at its source.
    /// </summary>
    private bool TryGetGroundHitPoint(out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;

        // Cast a ray from the camera center.
        Ray ray = gameCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

        // Find the intersection with the mathematical y=0 plane.
        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        float enter;
        if (groundPlane.Raycast(ray, out enter))
        {
            hitPoint = ray.GetPoint(enter);
            return true;
        }

        return false;
    }

    private void OnCellIndexChanged(Vector2Int newCellIndex)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => "CameraCellChecker: view changed " + newCellIndex);
        // Notify CellDataLoader.
        if (cellDataLoader != null)
        {
            cellDataLoader.OnCameraCellChanged(newCellIndex);
        }
    }
}
