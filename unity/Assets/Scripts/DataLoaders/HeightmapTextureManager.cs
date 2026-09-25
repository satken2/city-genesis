using UnityEngine;
using System.Collections.Generic;

/// <summary>描画タイル由来のGPU高さ窓を管理する。CPU接地は表示中のTerrainへ統一する。</summary>
public class HeightmapTextureManager : MonoBehaviour
{
    public static HeightmapTextureManager Instance { get; private set; }

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

    /// <summary>地形タイルのデータ適用完了通知。窓に重なるセルなら、窓の再構築を次のLateUpdateへ予約する。</summary>
    private void OnTerrainTileReadyForWindow(Vector2Int cellIndex, Terrain tile)
    {
        MarkSurfaceChanged(cellIndex);
    }

    public void MarkSurfaceChanged() => windowDirty = true;

    /// <summary>セルの地形が変わった(公開・除去)ときに呼ぶ。窓に重ならない外縁セルの出入りでは作り直さない。</summary>
    public void MarkSurfaceChanged(Vector2Int cell)
    {
        if (CellOverlapsWindow(cell)) windowDirty = true;
    }

    /// <summary>
    /// セルの矩形が現在の窓に重なるか。窓が未初期化なら常に true。
    /// 窓の再センタリングは常に全体を作り直すので、ここで外したセルも移動後の窓には反映される。
    /// </summary>
    private bool CellOverlapsWindow(Vector2Int cell)
    {
        if (!windowInitialized) return true;
        float cellSize = TerrainManager.CellSize;
        float windowSizeM = WINDOW_RES * WINDOW_TEXEL_M;
        float minX = cell.x * cellSize, minZ = cell.y * cellSize;
        return minX <= windowOriginXZ.x + windowSizeM && minX + cellSize >= windowOriginXZ.x
            && minZ <= windowOriginXZ.y + windowSizeM && minZ + cellSize >= windowOriginXZ.y;
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

    /// <summary>セッション変更時に、前の地形を参照するGPU窓を無効化する。</summary>
    public void Invalidate()
    {
        Shader.SetGlobalFloat(ShaderPropGpuConform, 0f);
        windowHasContent = false;
        windowDirty = true;
        windowInitialized = false;
        Utils.InvalidateHeightCache();
    }
}
