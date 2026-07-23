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

    // 確定済みのセルインデックス（ヒステリシス判定用）
    private Vector2Int confirmedCellIndex = new Vector2Int(int.MinValue, int.MinValue);
    // 未確定の新セル候補とその継続フレーム数
    private Vector2Int candidateCellIndex;
    private int candidateFrameCount;
    // 新セルが連続して何フレーム検出されたら確定とみなすか
    private const int CELL_CHANGE_CONFIRM_FRAMES = 3;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    void Update()
    {
        // ゲーム未開始時はカメラ移動によるセル変更イベントを発火しない。
        // トップメニュー表示中に毎フレーム無駄なログが出力されるのを防ぐため。
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsGameStarted())
        {
            return;
        }



        // 1. カメラ中心から数理地面平面（y=0）との交点を計算する。
        //    地形コライダーに依存しないため、地形のロード/アンロードが
        //    レイキャスト結果を変えることによるフィードバックループを防ぐ。
        Vector3 centerPoint;
        if (TryGetGroundHitPoint(out centerPoint))
        {
            CurrentWorldPosition = centerPoint;

            // 2. ワールド座標 → セルインデックスへ変換
            Vector2Int cellIndex = ConvertToCellIndex(centerPoint);

            // 3. セルが変化したらヒステリシス判定を経て確定させる。
            //    単発のノイズによる切り替わりを防ぐため、連続Nフレーム同じ新セルが
            //    検出された場合に限り OnCellIndexChanged を発火する。
            if (cellIndex != confirmedCellIndex)
            {
                if (cellIndex != candidateCellIndex)
                {
                    candidateCellIndex = cellIndex;
                    candidateFrameCount = 1;
                }
                else
                {
                    candidateFrameCount++;
                    if (candidateFrameCount >= CELL_CHANGE_CONFIRM_FRAMES)
                    {
                        CurrentCellIndex = cellIndex;
                        confirmedCellIndex = cellIndex;
                        OnCellIndexChanged(cellIndex);
                        candidateFrameCount = 0;
                    }
                }
            }
            else
            {
                // 確定済みセルと同じ → 候補をリセット
                if (candidateCellIndex != confirmedCellIndex)
                {
                    candidateFrameCount = 0;
                }
            }
        }
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
        Debug.Log("CameraCellChecker: OnCellIndexChanged newCellIndex: " + newCellIndex);
        // CellDataLoaderに通知
        if (cellDataLoader != null)
        {
            cellDataLoader.OnCameraCellChanged(newCellIndex);
        }
    }
}
