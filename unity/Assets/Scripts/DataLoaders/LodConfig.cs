using System.Collections.Generic;
using UnityEngine;

public enum LodTier
{
    UltraFar = 0,
    Far = 1,
    Near = 2,
    Dynamic = 3
}

public struct LodCellSets
{
    public List<Vector2Int> farCells;
    public List<Vector2Int> nearCells;
    public List<Vector2Int> dynamicCells;
}

public static class LodConfig
{
    /// <summary>
    /// 超近景（Dynamic）と判定するカメラからの距離（メートル）。
    /// この距離以内のセルは最も高い詳細度で描画される。
    /// </summary>
    public const float DYNAMIC_DISTANCE = 400f;

    /// <summary>
    /// 近景（Near）と判定するカメラからの距離（メートル）。
    /// Dynamic距離を超え、この距離以内のセルは中程度の詳細度で描画される。
    /// </summary>
    public const float NEAR_DISTANCE = 1000f;

    /// <summary>
    /// 先読みマージン（メートル）。
    /// カメラがセル境界に近づいている場合、DYNAMIC_DISTANCE + この距離以内の
    /// Nearセルを先読みする。これにより境界を越えた瞬間のロード遅延を防ぐ。
    /// セル境界付近でティアが切り替わる際のカクつきを緩和する。
    /// </summary>
    public const float PRELOAD_MARGIN = 100f;

    /// <summary>
    /// 先読み用の拡張Dynamic距離。
    /// カメラがセル境界に近づいている場合、Dynamic + PRELOAD_MARGIN 以内の
    /// NearセルをDynamic相当として先読みする。
    /// </summary>
    public static readonly float DynamicPreloadDistance = DYNAMIC_DISTANCE + PRELOAD_MARGIN;

    /// <summary>
    /// 先読み用の拡張Near距離。
    /// </summary>
    public static readonly float NearPreloadDistance = NEAR_DISTANCE + PRELOAD_MARGIN;

    /// <summary>
    /// 水平方向の視野判定に使用する半角（度）。
    /// 150度とすることで、真後ろ（180度）のセルも含めて広範囲を
    /// 視野内とみなし、ティアダウンが極端に発生しないように緩めている。
    /// </summary>
    public const float FRUSTUM_HALF_ANGLE_DEG = 150f;

    public static readonly Dictionary<LodTier, int> RESOLUTIONS = new Dictionary<LodTier, int>
    {
        { LodTier.UltraFar, 33 },
        { LodTier.Far, 65 },
        { LodTier.Near, 257 },
        { LodTier.Dynamic, 513 }
    };

    /// <summary>
    /// LODティア別のセルセットを計算する。
    /// カメラからの物理的な距離と水平方向の視野に基づいて各セルのティアを決定する。
    ///
    /// 距離によるベースティア:
    /// - DYNAMIC_DISTANCE (400m) 以内 → Dynamic
    /// - NEAR_DISTANCE (1000m) 以内 → Near
    /// - それ以遠で視野内 → Far
    ///
    /// acquiredCells を指定した場合、未開放（acquireされていない）セルは
    /// Dynamic にはならず、強制的に Near に抑制される。
    /// これにより、未開放セルに対して存在しない超近景地形データを要求するのを防ぐ。
    ///
    /// 視野外のセルはティアを1つ下げる。
    /// これにより、真後ろなど視界に入らないセルが突然Dynamicになることを防ぐ。
    ///
    /// 距離の計算は、セル中心ではなくセルのXZ平面上の四角形と標高範囲で構成される
    /// AABB（軸平行境界ボックス）に対するカメラ位置の最近接点を用いる。
    /// これにより、カメラがセル境界ぎりぎりに近い場合でも正確に判定できる。
    ///
    /// パフォーマンス最適化のため、カメラ周辺の有限範囲のみを走査する。
    /// </summary>
    /// <param name="centerCell">視点中心（カメラが向いている地面の交点）が属するセル（デバッグ表示等で使用）</param>
    /// <param name="viewCenterPosition">視点中心のワールド座標（y=0平面上）</param>
    /// <param name="cameraPosition">カメラの物理的なワールド座標（高さを含む）</param>
    /// <param name="cameraForward">カメラの前方方向ベクトル</param>
    /// <param name="terrainManager">セルの標高範囲を取得するためのTerrainManager</param>
    /// <param name="cellSize">セル1辺の長さ</param>
    /// <param name="mapWidth">マップの幅</param>
    /// <param name="mapHeight">マップの高さ</param>
    /// <param name="acquiredCells">開放済みセルのセット。nullの場合は全セル開放済みとみなす。</param>
    public static LodCellSets CalculateCellSets(
        Vector2Int centerCell,
        Vector3 viewCenterPosition,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        TerrainManager terrainManager,
        float cellSize,
        int mapWidth,
        int mapHeight,
        HashSet<Vector2Int> acquiredCells = null)
    {
        // カメラ位置のセル座標を計算する
        int camCellX = Mathf.FloorToInt(cameraPosition.x / cellSize);
        int camCellZ = Mathf.FloorToInt(cameraPosition.z / cellSize);

        // NEAR_DISTANCE を超えて視野外のセルは farCells にも入らないため、
        // 走査範囲は NEAR_DISTANCE + 1セル分の余裕に制限する。
        int maxScanCellDist = Mathf.CeilToInt(NEAR_DISTANCE / cellSize) + 1;

        // カメラ前方ベクトルを水平面に投影して正規化する
        Vector3 camForwardHorizontal = cameraForward;
        camForwardHorizontal.y = 0f;
        if (camForwardHorizontal.sqrMagnitude > 0.0001f)
        {
            camForwardHorizontal.Normalize();
        }
        else
        {
            // カメラが真上または真下を向いている場合は、+Z方向をデフォルトとする
            camForwardHorizontal = Vector3.forward;
        }

        List<Vector2Int> farCells = new List<Vector2Int>();
        List<Vector2Int> nearCells = new List<Vector2Int>();
        List<Vector2Int> dynamicCells = new List<Vector2Int>();

        // マップの外側（＝標高ソースが存在しない領域）にはセルを割り当てない。
        // 標高ソースはマップそのものであり、マップ外を要求すると Near は404・Far は遠景マップの
        // 端値クランプで「標高0mの平らな地面」を捏造してしまうため、走査段階で除外する。
        // マップは中心原点で、有効な Unity セル index は -halfCellsX <= x < halfCellsX（Zも同様）。
        // mapWidth/mapHeight は 2*cellSize の整数倍であることがバックエンドで保証されている。
        int halfCellsX = Mathf.RoundToInt(mapWidth / (2f * cellSize));
        int halfCellsZ = Mathf.RoundToInt(mapHeight / (2f * cellSize));

        for (int dx = -maxScanCellDist; dx <= maxScanCellDist; dx++)
        {
            for (int dz = -maxScanCellDist; dz <= maxScanCellDist; dz++)
            {
                Vector2Int cellIndex = new Vector2Int(camCellX + dx, camCellZ + dz);

                // マップ範囲外のセルはロード対象に含めない（下の海面板が海として見える）。
                if (cellIndex.x < -halfCellsX || cellIndex.x >= halfCellsX ||
                    cellIndex.y < -halfCellsZ || cellIndex.y >= halfCellsZ)
                {
                    continue;
                }

                // セルのXZ平面上の境界を計算する
                float cellMinX = cellIndex.x * cellSize;
                float cellMaxX = (cellIndex.x + 1) * cellSize;
                float cellMinZ = cellIndex.y * cellSize;
                float cellMaxZ = (cellIndex.y + 1) * cellSize;

                // セルの標高範囲を取得する。
                // TerrainManager の遠景高さマップからおおまかな minY/maxY を得る。
                float cellMinY = 0f;
                float cellMaxY = 0f;
                bool hasHeightRange = terrainManager != null && terrainManager.TryGetCellHeightRange(cellIndex, out cellMinY, out cellMaxY);
                if (!hasHeightRange)
                {
                    // 高さ情報がない場合は y=0 の平面として扱う
                    cellMinY = 0f;
                    cellMaxY = 0f;
                }

                // AABB（軸平行境界ボックス）上のカメラに最も近い点を計算する
                float closestX = Mathf.Clamp(cameraPosition.x, cellMinX, cellMaxX);
                float closestY = Mathf.Clamp(cameraPosition.y, cellMinY, cellMaxY);
                float closestZ = Mathf.Clamp(cameraPosition.z, cellMinZ, cellMaxZ);
                Vector3 closestPoint = new Vector3(closestX, closestY, closestZ);

                // カメラからAABB上の最近接点までの距離を計算する
                float distance = Vector3.Distance(cameraPosition, closestPoint);

                // NEAR_DISTANCE を超えて視野外のセルは farCells に入らないため、
                // 早期にスキップして判定コストを削減する。
                // ただし NEAR_DISTANCE 以内のセルは視野外でも tier が下がるだけなので
                // 必ず走査対象に含める。
                if (distance > NEAR_DISTANCE)
                {
                    // カメラから最近接点への水平方向ベクトルを計算する
                    Vector3 toClosest = closestPoint - cameraPosition;
                    toClosest.y = 0f;

                    // 水平方向の視野判定
                    float horizontalAngle = 0f;
                    if (toClosest.sqrMagnitude > 0.0001f)
                    {
                        horizontalAngle = Vector3.Angle(camForwardHorizontal, toClosest);
                    }

                    // NEAR_DISTANCE 以遠で視野外のセルは UltraFar になり farCells に入らない
                    if (horizontalAngle > FRUSTUM_HALF_ANGLE_DEG)
                    {
                        continue;
                    }
                }

                // ティア判定用の視野判定（最近接点方向で判定する）
                Vector3 toClosestForTier = closestPoint - cameraPosition;
                toClosestForTier.y = 0f;
                float horizontalAngleForTier = 0f;
                if (toClosestForTier.sqrMagnitude > 0.0001f)
                {
                    horizontalAngleForTier = Vector3.Angle(camForwardHorizontal, toClosestForTier);
                }
                bool isInView = horizontalAngleForTier <= FRUSTUM_HALF_ANGLE_DEG;

                // 距離に基づいてベースティアを決定する。
                // 先読みマージンを加算して、セル境界に近づいている場合に
                // 早めに上位ティアへ昇格させる。これにより境界越え時の
                // ロード遅延によるカクつきを防ぐ。
                LodTier tier;
                if (distance <= DynamicPreloadDistance)
                {
                    tier = LodTier.Dynamic;
                }
                else if (distance <= NearPreloadDistance)
                {
                    tier = LodTier.Near;
                }
                else
                {
                    tier = LodTier.Far;
                }

                // 未開放セルは Dynamic にしない。
                // acquiredCells が null の場合は全セル開放済みとみなす（後方互換）。
                if (tier == LodTier.Dynamic && acquiredCells != null && !acquiredCells.Contains(cellIndex))
                {
                    tier = LodTier.Near;
                }

                // 視野に入らないセルはティアを1つ下げる。
                // ただし、下限はUltraFarとする。
                if (!isInView)
                {
                    tier = (LodTier)Mathf.Max(0, (int)tier - 1);
                }

                switch (tier)
                {
                    case LodTier.Dynamic:
                        // farCells はロード対象セル全体を表す包含集合である。
                        // TerrainManager は farCells を desiredCells として使用するため、
                        // Dynamic/Near のセルも farCells に含める必要がある。
                        dynamicCells.Add(cellIndex);
                        nearCells.Add(cellIndex);
                        farCells.Add(cellIndex);
                        break;
                    case LodTier.Near:
                        nearCells.Add(cellIndex);
                        farCells.Add(cellIndex);
                        break;
                    case LodTier.Far:
                        farCells.Add(cellIndex);
                        break;
                    case LodTier.UltraFar:
                        break;
                }
            }
        }

        return new LodCellSets
        {
            farCells = farCells,
            nearCells = nearCells,
            dynamicCells = dynamicCells
        };
    }
}
