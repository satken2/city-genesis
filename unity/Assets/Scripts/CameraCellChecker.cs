using UnityEngine;

public class CameraCellChecker : MonoBehaviour
{
    public static CameraCellChecker Instance { get; private set; }

    [SerializeField] private CellDataLoader cellDataLoader;
    [SerializeField] private TerrainManager terrainManager;
    [SerializeField] private Camera gameCamera;

    // 現在の「カメラが向いている地面のセルインデックス」
    public Vector2Int CurrentCellIndex { get; private set; }

    // カメラの水平面（y=0）におけるワールド座標。
    // LodConfig がセル境界判定に使用する。
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
    /// 視点変化の検出状態を未初期化へ戻す。次の Update ではカメラが静止していても必ず
    /// 「変化あり」となり、セル通知(ロードの起点)が走る。
    /// セッション切り替えで CellDataLoader の現在セルが未設定に戻った後、カメラを動かさなくても
    /// 新しい都市のロードが始まるようにするために、状態クリアとロード再開の両方から呼ぶ。
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
        CurrentCellIndex = ConvertToCellIndex(centerPoint);
        OnCellIndexChanged(CurrentCellIndex);
    }

    /// <summary>
    /// カメラ中心から発射したレイと数理地面平面 (y=0) の交点を計算する。
    /// Physics.Raycast による地形コライダー検出は行わない。
    /// 地形のロード状態に交点が左右されないことで、セル境界での
    /// 「地形リロード → コライダー変化 → レイキャスト結果変化 → 再リロード」
    /// というフィードバックループを根本的に断ち切る。
    /// </summary>
    private bool TryGetGroundHitPoint(out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;

        // カメラ中央からレイを発射
        Ray ray = gameCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

        // y=0 の数理平面との交点を求める
        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        float enter;
        if (groundPlane.Raycast(ray, out enter))
        {
            hitPoint = ray.GetPoint(enter);
            return true;
        }

        return false;
    }

    private Vector2Int ConvertToCellIndex(Vector3 worldPosition)
    {
        // バックエンドの距離をそのままメートルとして扱う
        float cellSizeInUnity = TerrainManager.CellSize;
        int cellX = Mathf.FloorToInt(worldPosition.x / cellSizeInUnity);
        int cellZ = Mathf.FloorToInt(worldPosition.z / cellSizeInUnity);
        return new Vector2Int(cellX, cellZ);
    }


    private void OnCellIndexChanged(Vector2Int newCellIndex)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_LOADER, () => "CameraCellChecker: view changed " + newCellIndex);
        // CellDataLoaderに通知
        if (cellDataLoader != null)
        {
            cellDataLoader.OnCameraCellChanged(newCellIndex);
        }
    }
}
