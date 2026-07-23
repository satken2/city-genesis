using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 地形の高さマップを一元管理するクラス。
/// TerrainManager が持つ各セルの TerrainData から高さを取得し、
/// CPU側で高速にルックアップ可能な2次元配列として保持する。
///
/// ワールド座標から地形高さを取得する際、Terrain.SampleHeight() や
/// Physics.Raycast() の代わりにこのクラスの SampleHeight() を使うことで、
/// FindAnyObjectByType() や Terrain コンポーネントアクセスのオーバーヘッドを
/// 完全に排除できる。
///
/// 将来的に Vertex Shader で GPU 地形追従を行う場合のテクスチャ生成も担う。
/// </summary>
public class HeightmapTextureManager : MonoBehaviour
{
    public static HeightmapTextureManager Instance { get; private set; }

    /// <summary>
    /// CPU側の高速ルックアップ用高さ配列。
    /// インデックス = (z * resolution + x)。ワールド座標から直接アクセスする。
    /// 解像度は1メートル刻みとし、配列の1要素が1x1メートルの高さを表す。
    /// </summary>
    private float[] cpuHeightmap;

    /// <summary>
    /// 高さ配列の幅（ワールドX方向の要素数）。
    /// </summary>
    private int heightmapWidth;

    /// <summary>
    /// 高さ配列の高さ（ワールドZ方向の要素数）。
    /// </summary>
    private int heightmapHeight;

    /// <summary>
    /// 高さ配列がカバーするワールド座標の原点（左下 Corner）。
    /// </summary>
    private Vector2 mapOriginXZ;

    /// <summary>
    /// 高さ配列がカバーするワールドサイズ（一辺）。
    /// </summary>
    private float mapWorldSize;

    /// <summary>
    /// 高さ配列の解像度（1要素あたりのメートル数）。
    /// </summary>
    private float heightmapResolution = 1f;

    /// <summary>
    /// 1メートル刻みのCPU配列が有効かどうか。
    /// 無効な場合はSampleHeightはフォールバックとして
    /// Utils.GetGroundHeight() を使用する。
    /// </summary>
    private bool cpuHeightmapValid = false;

    private TerrainManager terrainManager;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>
    /// TerrainManagerへの参照を設定する。CellDataLoader等から初期化時に呼ばれる。
    /// </summary>
    public void Initialize(TerrainManager tm)
    {
        terrainManager = tm;
    }

    /// <summary>
    /// CPU側高さ配列を全量再構築する。初回ロード時・強制リロード時・マップ設定変更時に呼ぶ。
    /// 配列を確保し直した上で、TerrainManager がCPU側に保持しているハイトマップキャッシュから
    /// 全セルを書き戻す(<see cref="TerrainManager.ReplayHeightsToCpuHeightmap"/>)。
    /// TerrainData.GetHeights(GPU→CPUの同期readback)は一切行わないため、
    /// 旧実装のような数百ms〜数秒のブロックは発生しない。
    ///
    /// なお通常の差分更新は不要になった。地形タイルの生成・再縫合のたびに
    /// TerrainManager が <see cref="WriteCellHeights"/> で直接書き込むため、
    /// 配列は常に最新の状態に保たれる。
    /// </summary>
    public void RebuildCpuHeightmap()
    {
        if (terrainManager == null) return;

        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        AllocateCpuHeightmap();
        terrainManager.ReplayHeightsToCpuHeightmap(this);

        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] HeightmapTextureManager RebuildCpuHeightmap: {swTotal.ElapsedMilliseconds}ms (resolution={heightmapResolution}m, array={heightmapWidth}x{heightmapHeight})");
    }

    /// <summary>
    /// CPU高さ配列を無効化して破棄する。セッションリセット(セーブ切り替え・ゲーム終了)時に呼ぶ。
    /// 無効化しないと、別のセーブをロードした直後に前のマップの標高が GetTerrainHeight から
    /// 返り続け、新しい地形タイルが届くまでの間に生成される建物・道路が誤った高さで作られる。
    /// 無効化後は Utils.GetGroundHeight へのフォールバック(=新規起動時と同じ挙動)となり、
    /// 初回の地形追従の RebuildCpuHeightmap で再確保される。
    /// </summary>
    public void Invalidate()
    {
        cpuHeightmapValid = false;
        cpuHeightmap = null;
    }

    /// <summary>
    /// 現在のマップ設定に基づいてCPU高さ配列を確保する(既存データは破棄してゼロ初期化)。
    /// 1メートル刻みを基本とし、マップが大きい場合(要素数が maxArraySize 超)は
    /// セルサイズ刻みにフォールバックする。セル単位の解像度でも建物配置には
    /// 十分な精度(最大約±0.5mの誤差)となる。
    /// </summary>
    private void AllocateCpuHeightmap()
    {
        ComputeExpectedDimensions(out int width, out int height, out float resolution);

        heightmapWidth = width;
        heightmapHeight = height;
        mapWorldSize = Mathf.Max(terrainManager.GetMapWidth(), terrainManager.GetMapHeight());
        heightmapResolution = resolution;
        mapOriginXZ = new Vector2(-terrainManager.GetMapWidth() / 2f, -terrainManager.GetMapHeight() / 2f);

        cpuHeightmap = new float[width * height];
        cpuHeightmapValid = true;
    }

    /// <summary>
    /// 現在のマップ設定から「あるべき配列寸法・解像度」を算出する。
    /// </summary>
    private void ComputeExpectedDimensions(out int width, out int height, out float resolution)
    {
        int mapWidth = terrainManager.GetMapWidth();
        int mapHeight = terrainManager.GetMapHeight();
        float cellSize = TerrainManager.CellSize;

        const int maxArraySize = 2048 * 2048; // メモリ制約: 約16M要素（64MB float配列）

        if (mapWidth * mapHeight <= maxArraySize)
        {
            width = mapWidth;
            height = mapHeight;
            resolution = 1f;
        }
        else
        {
            width = Mathf.CeilToInt((float)mapWidth / cellSize);
            height = Mathf.CeilToInt((float)mapHeight / cellSize);
            resolution = cellSize;
        }
    }

    /// <summary>
    /// 配列が未確保、または現在のマップ設定と寸法・解像度・原点が一致しない場合に確保し直す。
    /// WriteCellHeights から遅延的に呼ばれるため、呼び出し側は配列の存在を意識しなくてよい。
    /// </summary>
    private bool EnsureCpuHeightmapAllocated()
    {
        if (terrainManager == null) return false;

        ComputeExpectedDimensions(out int expectedWidth, out int expectedHeight, out float expectedResolution);
        Vector2 expectedOrigin = new Vector2(-terrainManager.GetMapWidth() / 2f, -terrainManager.GetMapHeight() / 2f);

        bool dimsMatch = cpuHeightmapValid
            && cpuHeightmap != null
            && cpuHeightmap.Length == expectedWidth * expectedHeight
            && heightmapWidth == expectedWidth
            && heightmapHeight == expectedHeight
            && Mathf.Approximately(heightmapResolution, expectedResolution)
            && mapOriginXZ == expectedOrigin;

        if (!dimsMatch)
        {
            AllocateCpuHeightmap();
        }
        return true;
    }

    /// <summary>
    /// ワールド座標からCPU高さ配列を参照して地形高さを取得する。
    /// Utils.GetGroundHeight() の高速版として使用する。
    /// 配列が未構築の場合はフォールバック値0を返す。
    /// </summary>
    public float SampleHeight(float worldX, float worldZ)
    {
        if (!cpuHeightmapValid || cpuHeightmap == null) return 0f;

        // ワールド座標 → 配列インデックスに変換
        float fx = (worldX - mapOriginXZ.x) / heightmapResolution;
        float fy = (worldZ - mapOriginXZ.y) / heightmapResolution;

        // バイリニア補間で高さを取得
        int ix = Mathf.FloorToInt(fx);
        int iy = Mathf.FloorToInt(fy);

        // 範囲外の場合はクリップ
        if (ix < 0 || ix >= heightmapWidth - 1 || iy < 0 || iy >= heightmapHeight - 1)
        {
            // 境界上の場合はクリップして最近接点を返す
            ix = Mathf.Clamp(ix, 0, heightmapWidth - 1);
            iy = Mathf.Clamp(iy, 0, heightmapHeight - 1);
            return cpuHeightmap[iy * heightmapWidth + ix];
        }

        float fracX = fx - ix;
        float fracY = fy - iy;

        float h00 = cpuHeightmap[iy * heightmapWidth + ix];
        float h10 = cpuHeightmap[iy * heightmapWidth + (ix + 1)];
        float h01 = cpuHeightmap[(iy + 1) * heightmapWidth + ix];
        float h11 = cpuHeightmap[(iy + 1) * heightmapWidth + (ix + 1)];

        float h0 = Mathf.Lerp(h00, h10, fracX);
        float h1 = Mathf.Lerp(h01, h11, fracX);
        return Mathf.Lerp(h0, h1, fracY);
    }

    /// <summary>
    /// CPU高さ配列と一致する地形高さを返すヘルパー。
    /// CPU配列が利用可能な場合はSampleHeightを使い、
    /// 利用不可の場合はUtils.GetGroundHeightにフォールバックする。
    /// </summary>
    public static float GetTerrainHeight(Vector3 position)
    {
        if (Instance != null && Instance.cpuHeightmapValid)
        {
            float height = Instance.SampleHeight(position.x, position.z);
            if (height == 0f)
            {
                return Utils.GetGroundHeight(position);
            }
            return height;
        }

        return Utils.GetGroundHeight(position);
    }

    /// <summary>
    /// 指定セルの高さをCPU高さ配列に書き込む。
    /// TerrainManager が地形タイルの生成・再縫合を行うたびに、CPU側に保持している
    /// ハイトマップ(正規化0..1)をそのまま渡して呼び出す。
    /// TerrainData.GetHeights(GPU→CPUの同期readback)を使わないため軽量で、
    /// タイル生成と同一フレームで配列が最新化される。
    /// 配列が未確保・寸法不一致の場合はここで確保し直す。
    /// </summary>
    /// <param name="cellIndex">Unityセルインデックス</param>
    /// <param name="normalizedHeights">正規化ハイトマップ [行(=Z), 列(=X)]。TerrainData.SetHeights に渡すものと同じ配列</param>
    /// <param name="worldHeightScale">正規化高さ→ワールド高さ(メートル)への倍率(= terrainSize.y)</param>
    /// <param name="belowSeaSpan">
    /// 地形タイルの海面下オフセット(m)(= TerrainManager.SeaFloorDepthSpan)。
    /// 正規化ハイトマップは海面下オフセットを加えて作られており、タイルのベースYが
    /// <c>-belowSeaSpan</c> に置かれるため、ワールド高さは <c>normalized×scale - belowSeaSpan</c> = 実標高 となる。
    /// 海底(標高0未満)では負のワールド高さを返す。
    /// </param>
    public void WriteCellHeights(Vector2Int cellIndex, float[,] normalizedHeights, float worldHeightScale, float belowSeaSpan)
    {
        if (normalizedHeights == null) return;
        WriteCellHeightRows(cellIndex, normalizedHeights, worldHeightScale, belowSeaSpan, 0, normalizedHeights.GetLength(0));
    }

    /// <summary>
    /// <see cref="WriteCellHeights"/> の行範囲版。ハイトマップの行(=Z方向)
    /// [<paramref name="heightRowStart"/>, <paramref name="heightRowStart"/> + <paramref name="heightRowCount"/>)
    /// に対応するCPU高さ配列の行だけを書き込む。
    /// TerrainManager がハイトマップを複数フレームに分けてバンド単位で部分 SetHeights する際、
    /// 同じバンドに対応するCPU側の行を同期更新するために使う(セル全体の一括書き込みを
    /// 1フレームに集中させない)。<paramref name="normalizedHeights"/> にはセル全体の配列を渡すこと。
    /// </summary>
    public void WriteCellHeightRows(Vector2Int cellIndex, float[,] normalizedHeights, float worldHeightScale, float belowSeaSpan, int heightRowStart, int heightRowCount)
    {
        if (normalizedHeights == null) return;
        if (!EnsureCpuHeightmapAllocated()) return;

        int terrainRes = normalizedHeights.GetLength(0);
        if (terrainRes < 2 || normalizedHeights.GetLength(1) != terrainRes) return;

        float cellSize = TerrainManager.CellSize;
        float resolution = heightmapResolution;

        // セルのワールド座標範囲
        float cellWorldX = cellIndex.x * cellSize;
        float cellWorldZ = cellIndex.y * cellSize;

        // 配列の解像度に応じて書き込み先のインデックスを計算
        int startX = Mathf.RoundToInt((cellWorldX - mapOriginXZ.x) / resolution);
        int startY = Mathf.RoundToInt((cellWorldZ - mapOriginXZ.y) / resolution);

        // セル内の配列要素数
        int cellArraySize = Mathf.RoundToInt(cellSize / resolution);

        // ハイトマップの行範囲 [heightRowStart, heightRowEnd] に対応するCPU配列の行範囲を求める。
        // 下の本処理では ly → hy = Round(ly/cellArraySize*(terrainRes-1)) と写像するため、
        // その逆写像で ly の窓を計算する(丸めを跨ぐよう floor/ceil で広めに取る)。
        int heightRowEnd = Mathf.Min(heightRowStart + heightRowCount - 1, terrainRes - 1);
        int lyStart = Mathf.Max(0, Mathf.FloorToInt((heightRowStart - 0.5f) / (float)(terrainRes - 1) * cellArraySize));
        int lyEnd = Mathf.Min(cellArraySize - 1, Mathf.CeilToInt((heightRowEnd + 0.5f) / (float)(terrainRes - 1) * cellArraySize));

        for (int ly = lyStart; ly <= lyEnd; ly++)
        {
            for (int lx = 0; lx < cellArraySize; lx++)
            {
                int ax = startX + lx;
                int ay = startY + ly;

                if (ax < 0 || ax >= heightmapWidth || ay < 0 || ay >= heightmapHeight)
                    continue;

                // 配列インデックス → ハイトマップインデックスのマッピング
                float tx = (float)lx / cellArraySize;
                float ty = (float)ly / cellArraySize;

                int hx = Mathf.Clamp(Mathf.RoundToInt(tx * (terrainRes - 1)), 0, terrainRes - 1);
                int hy = Mathf.Clamp(Mathf.RoundToInt(ty * (terrainRes - 1)), 0, terrainRes - 1);

                // heights[y, x] 形式（UnityのTerrainDataは行優先）
                float normalizedHeight = normalizedHeights[hy, hx];
                // タイルのベースY(-belowSeaSpan)を反映して実標高のワールド高さに戻す。
                float worldHeight = normalizedHeight * worldHeightScale - belowSeaSpan;

                cpuHeightmap[ay * heightmapWidth + ax] = worldHeight;
            }
        }
    }
}