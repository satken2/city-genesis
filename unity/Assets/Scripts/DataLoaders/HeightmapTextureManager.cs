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

    // ================= GPU地形追従用の移動窓テクスチャ =================
    // シェーダー(区画データ可視化フィル等)がワールドXZ→地形高さを引くための、
    // カメラ追従の移動窓ハイトマップ。TerrainHeightmapConform.hlsl のグローバル
    // プロパティとして公開する。
    //
    // マップ全体を1枚のテクスチャにする方式は採らない: マップは最大100km四方あり、
    // 全マップを1m解像度で持つことは原理的に不可能で、CPU高さ配列と同じくセルサイズ
    // (数百m)解像度に落とすと実地形と大きく乖離して使い物にならないため。
    // 代わりに「カメラ周辺のロード済み地形タイルのGPUハイトマップ
    // (TerrainData.heightmapTexture)」からコンピュートシェーダーで1m解像度の窓へ
    // 切り出す。ソースが描画中の地形タイルそのものなので、見えている地形と厳密に一致する。
    //
    // テクセル(i,j)は「ワールド座標 (原点X+i, 原点Z+j) の標高」を保持する
    // (グリッド点規約。TerrainHeightmapConform.hlsl の半テクセル補正サンプリングと対応)。
    // ラスタライズを使わずコンピュートで書くため、プラットフォームごとの上下反転問題がない。

    /// <summary>窓テクスチャの一辺のテクセル数。テクセル=1mで一辺1536mをカバーする。</summary>
    private const int WINDOW_RES = 1536;

    /// <summary>窓テクスチャの1テクセルのワールドサイズ(m)。</summary>
    private const float WINDOW_TEXEL_M = 1f;

    /// <summary>
    /// カメラが窓中心からこの距離(m)を超えて離れたら窓を再センタリングする。
    /// 窓半径768mに対し、区画フィルの可視距離(480m)が常に窓内に収まる余裕を残した値。
    /// </summary>
    private const float WINDOW_RECENTER_DISTANCE = 250f;

    /// <summary>移動窓ハイトマップ(RFloat)。ワールド標高(m)を直接格納する。</summary>
    private RenderTexture windowTexture;

    /// <summary>窓のワールド原点(テクセル(0,0)が保持する高さのワールドXZ座標)。</summary>
    private Vector2 windowOriginXZ;

    /// <summary>窓に有効な内容(1タイル以上の切り出し)があるかどうか。_GpuTerrainConform に反映する。</summary>
    private bool windowHasContent = false;

    /// <summary>地形タイルの更新(OnTerrainTileReady)があり、窓の再構築が必要かどうか。</summary>
    private bool windowDirty = false;

    /// <summary>窓原点を一度でもカメラ位置へ設定したかどうか。</summary>
    private bool windowInitialized = false;

    /// <summary>窓切り出し用コンピュートシェーダー(Resources/Shaders/TerrainHeightWindow.compute)。</summary>
    private ComputeShader windowCompute;
    private int kernelBlitTile = -1;
    private int kernelClearWindow = -1;

    /// <summary>コンピュートシェーダー非対応環境などで窓機能を無効化したかどうか。</summary>
    private bool windowUnsupported = false;

    private static readonly int ShaderPropHeightmap = Shader.PropertyToID("_TerrainHeightmap");
    private static readonly int ShaderPropHeightmapSize = Shader.PropertyToID("_TerrainHeightmapSize");
    private static readonly int ShaderPropMapOriginXZ = Shader.PropertyToID("_MapOriginXZ");
    private static readonly int ShaderPropHeightScale = Shader.PropertyToID("_TerrainHeightScale");
    private static readonly int ShaderPropHeightOffset = Shader.PropertyToID("_TerrainHeightOffset");
    private static readonly int ShaderPropGpuConform = Shader.PropertyToID("_GpuTerrainConform");

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // 地形タイルのデータ適用完了ごとに移動窓を作り直す(次のLateUpdateで1回に合流)。
        TerrainManager.OnTerrainTileReady += OnTerrainTileReadyForWindow;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReadyForWindow;
        ReleaseWindowTexture();
    }

    /// <summary>地形タイルのデータ適用完了通知。窓の再構築を次のLateUpdateへ予約する。</summary>
    private void OnTerrainTileReadyForWindow(Vector2Int cellIndex, Terrain tile)
    {
        windowDirty = true;
    }

    /// <summary>
    /// 移動窓の維持。カメラが窓中心から離れたら再センタリングし、
    /// 地形タイルの更新(windowDirty)があれば窓を作り直す。
    /// 再構築はフレームあたり最大1回に合流される(タイル適用が同一フレームに複数あっても1回)。
    /// </summary>
    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        if (!EnsureWindowResources()) return;

        Vector2 camXZ = new Vector2(cam.transform.position.x, cam.transform.position.z);
        float half = WINDOW_RES * WINDOW_TEXEL_M * 0.5f;
        Vector2 windowCenter = windowOriginXZ + new Vector2(half, half);

        if (!windowInitialized || Vector2.Distance(camXZ, windowCenter) > WINDOW_RECENTER_DISTANCE)
        {
            // 原点はテクセルサイズのグリッドへスナップし、テクセルとワールド格子点の対応を保つ。
            windowOriginXZ = new Vector2(Mathf.Round(camXZ.x - half), Mathf.Round(camXZ.y - half));
            windowInitialized = true;
            RebuildWindow();
        }
        else if (windowDirty)
        {
            RebuildWindow();
        }
    }

    /// <summary>
    /// 窓用のRenderTextureとコンピュートシェーダーを確保する。非対応環境では false。
    /// </summary>
    private bool EnsureWindowResources()
    {
        if (windowUnsupported) return false;
        if (windowTexture != null && windowCompute != null) return true;

        if (!SystemInfo.supportsComputeShaders)
        {
            windowUnsupported = true;
            Debug.LogWarning("[HeightmapTextureManager] コンピュートシェーダー非対応のため、シェーダーの地形追従(移動窓ハイトマップ)を無効化します");
            return false;
        }

        if (windowCompute == null)
        {
            windowCompute = Resources.Load<ComputeShader>("Shaders/TerrainHeightWindow");
            if (windowCompute == null)
            {
                windowUnsupported = true;
                Debug.LogError("[HeightmapTextureManager] Resources/Shaders/TerrainHeightWindow.compute が見つかりません");
                return false;
            }
            kernelBlitTile = windowCompute.FindKernel("BlitTile");
            kernelClearWindow = windowCompute.FindKernel("ClearWindow");
        }

        if (windowTexture == null)
        {
            windowTexture = new RenderTexture(WINDOW_RES, WINDOW_RES, 0, RenderTextureFormat.RFloat);
            windowTexture.name = "TerrainHeightWindow";
            windowTexture.enableRandomWrite = true;
            windowTexture.filterMode = FilterMode.Bilinear;
            windowTexture.wrapMode = TextureWrapMode.Clamp;
            windowTexture.Create();
        }
        return true;
    }

    /// <summary>
    /// 窓の全テクセルを、ロード済み地形タイルのGPUハイトマップから切り出して作り直す。
    /// タイルが1枚も無い領域は標高0(海面)として残る。コストは最大でも
    /// 「クリア1回+窓に重なるタイル数(半径768mなら十数枚)のディスパッチ」で、GPU的に軽微。
    /// </summary>
    private void RebuildWindow()
    {
        windowDirty = false;
        if (terrainManager == null) terrainManager = FindAnyObjectByType<TerrainManager>();

        float windowSizeM = WINDOW_RES * WINDOW_TEXEL_M;
        int clearGroups = Mathf.CeilToInt(WINDOW_RES / 8f);
        windowCompute.SetInts("_WindowRes", WINDOW_RES, WINDOW_RES);
        windowCompute.SetTexture(kernelClearWindow, "_WindowTex", windowTexture);
        windowCompute.Dispatch(kernelClearWindow, clearGroups, clearGroups, 1);

        int tilesDrawn = 0;
        if (terrainManager != null)
        {
            foreach (var kvp in terrainManager.LoadedTerrainTiles)
            {
                Terrain tile = kvp.Value;
                if (tile == null || tile.terrainData == null) continue;

                Vector3 tilePos = tile.transform.position;
                Vector3 tileSize = tile.terrainData.size;

                // タイルと窓のワールド重なり範囲
                float ox0 = Mathf.Max(tilePos.x, windowOriginXZ.x);
                float oz0 = Mathf.Max(tilePos.z, windowOriginXZ.y);
                float ox1 = Mathf.Min(tilePos.x + tileSize.x, windowOriginXZ.x + windowSizeM);
                float oz1 = Mathf.Min(tilePos.z + tileSize.z, windowOriginXZ.y + windowSizeM);
                if (ox1 <= ox0 || oz1 <= oz0) continue;

                // テクセルpはワールド(原点+p)の標高を持つため、原点+pが重なり範囲内のテクセルを書く
                int px0 = Mathf.Max(0, Mathf.CeilToInt((ox0 - windowOriginXZ.x) / WINDOW_TEXEL_M));
                int pz0 = Mathf.Max(0, Mathf.CeilToInt((oz0 - windowOriginXZ.y) / WINDOW_TEXEL_M));
                int px1 = Mathf.Min(WINDOW_RES - 1, Mathf.FloorToInt((ox1 - windowOriginXZ.x) / WINDOW_TEXEL_M));
                int pz1 = Mathf.Min(WINDOW_RES - 1, Mathf.FloorToInt((oz1 - windowOriginXZ.y) / WINDOW_TEXEL_M));
                if (px1 < px0 || pz1 < pz0) continue;

                RenderTexture tileHeightmap = tile.terrainData.heightmapTexture;
                if (tileHeightmap == null) continue;

                windowCompute.SetTexture(kernelBlitTile, "_WindowTex", windowTexture);
                windowCompute.SetTexture(kernelBlitTile, "_TileHeightmap", tileHeightmap);
                windowCompute.SetInts("_DestOffset", px0, pz0);
                windowCompute.SetInts("_DestSize", px1 - px0 + 1, pz1 - pz0 + 1);
                windowCompute.SetFloats("_DestWorldOrigin",
                    windowOriginXZ.x + px0 * WINDOW_TEXEL_M,
                    windowOriginXZ.y + pz0 * WINDOW_TEXEL_M);
                windowCompute.SetFloat("_TexelSize", WINDOW_TEXEL_M);
                windowCompute.SetFloats("_TileWorldOrigin", tilePos.x, tilePos.z);
                windowCompute.SetFloats("_TileWorldSize", tileSize.x, tileSize.z);
                windowCompute.SetFloat("_TileHeightScale", tileSize.y);
                windowCompute.SetFloat("_TileBaseY", tilePos.y);
                windowCompute.SetInt("_TileHeightmapRes", tile.terrainData.heightmapResolution);
                windowCompute.Dispatch(kernelBlitTile,
                    Mathf.CeilToInt((px1 - px0 + 1) / 8f),
                    Mathf.CeilToInt((pz1 - pz0 + 1) / 8f), 1);
                tilesDrawn++;
            }
        }

        Shader.SetGlobalTexture(ShaderPropHeightmap, windowTexture);
        Shader.SetGlobalVector(ShaderPropHeightmapSize,
            new Vector4(windowSizeM, windowSizeM, WINDOW_RES, WINDOW_RES));
        Shader.SetGlobalVector(ShaderPropMapOriginXZ, new Vector4(windowOriginXZ.x, windowOriginXZ.y, 0f, 0f));
        Shader.SetGlobalFloat(ShaderPropHeightScale, 1f);
        Shader.SetGlobalFloat(ShaderPropHeightOffset, 0f);

        // タイルを1枚も切り出せていない間(セッション開始直後など)は追従を有効化しない
        // (ゼロクリアの窓に追従すると標高0へ張り付くため)。
        windowHasContent = tilesDrawn > 0;
        Shader.SetGlobalFloat(ShaderPropGpuConform, windowHasContent ? 1f : 0f);
    }

    /// <summary>移動窓のリソースを解放し、シェーダーの地形追従を無効化する。</summary>
    private void ReleaseWindowTexture()
    {
        Shader.SetGlobalFloat(ShaderPropGpuConform, 0f);
        windowHasContent = false;
        if (windowTexture != null)
        {
            windowTexture.Release();
            Destroy(windowTexture);
            windowTexture = null;
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
        // 移動窓も無効化する。地形タイルは別途破棄されるため、
        // 次のLateUpdateで(タイルが再ロードされるまで)空の窓として作り直される。
        Shader.SetGlobalFloat(ShaderPropGpuConform, 0f);
        windowHasContent = false;
        windowDirty = true;
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
    /// CPU配列が「1m解像度で確保できている」場合のみSampleHeightを使い、
    /// それ以外は Utils.GetGroundHeight(実際の地形タイルのサンプリング)にフォールバックする。
    ///
    /// 解像度ゲートが必須である理由: CPU配列は要素数上限(maxArraySize)を超えるマップでは
    /// セルサイズ刻み(数百m)へフォールバックする。本作のマップは100km四方・セル500mのため
    /// 常にこの粗い配列になり、これをバイリニア補間した高さは実地形と数十m単位で乖離する。
    /// 粗い配列を建物・道路の配置高さに使うと地形追従が全面的に破綻する(既往の回帰)。
    /// </summary>
    public static float GetTerrainHeight(Vector3 position)
    {
        if (Instance != null && Instance.cpuHeightmapValid && Instance.heightmapResolution <= 1f)
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