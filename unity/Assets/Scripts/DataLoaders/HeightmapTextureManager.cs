using UnityEngine;
using System.Collections.Generic;

/// <summary>Manage the GPU height window sourced from rendered tiles. CPU grounding consistently uses the displayed Terrain.</summary>
public class HeightmapTextureManager : MonoBehaviour
{
    public static HeightmapTextureManager Instance { get; private set; }

    private TerrainManager terrainManager;

    // ================= Moving window texture for GPU terrain following =================
    // Camera-following moving heightmap window for shaders (such as parcel visualization fills)
    // to query terrain height from world XZ. Published as global properties
    // in TerrainHeightmapConform.hlsl.
    //
    // Do not represent the entire map with one texture: maps can be up to 100km square,
    // making full-map 1m resolution infeasible; reducing resolution to cell size
    // (hundreds of meters), as with CPU height arrays, deviates too far from actual terrain to be usable.
    // Instead, use a compute shader to extract a 1m-resolution window from GPU heightmaps
    // of loaded terrain tiles around the camera (TerrainData.heightmapTexture).
    // The source is the rendered terrain itself, so the window exactly matches visible terrain.
    //
    // Texel (i,j) stores elevation at world coordinates (origin X+i, origin Z+j)
    // (grid-point convention, matching half-texel-corrected sampling in TerrainHeightmapConform.hlsl).
    // Compute writes avoid rasterization and platform-dependent vertical flipping.

    /// <summary>Texels per side of the window texture. At 1m per texel, each side covers 1536m.</summary>
    private const int WINDOW_RES = 1536;

    /// <summary>World size (m) of one window texture texel.</summary>
    private const float WINDOW_TEXEL_M = 1f;

    /// <summary>
    /// Recenter the window when the camera moves farther than this distance (m) from its center.
    /// With a 768m window radius, this leaves enough margin to keep the parcel fill visibility range (480m) inside.
    /// </summary>
    private const float WINDOW_RECENTER_DISTANCE = 250f;

    /// <summary>Moving heightmap window (RFloat), storing world elevation (m) directly.</summary>
    private RenderTexture windowTexture;

    /// <summary>Window world origin (world XZ position whose height is stored in texel (0,0)).</summary>
    private Vector2 windowOriginXZ;

    /// <summary>Whether the window contains valid data (at least one extracted tile). Reflected in _GpuTerrainConform.</summary>
    private bool windowHasContent = false;

    /// <summary>Whether terrain tile updates (OnTerrainTileReady) require rebuilding the window.</summary>
    private bool windowDirty = false;

    /// <summary>Whether the window origin has ever been initialized from the camera position.</summary>
    private bool windowInitialized = false;

    /// <summary>Compute shader for window extraction (Resources/Shaders/TerrainHeightWindow.compute).</summary>
    private ComputeShader windowCompute;
    private int kernelBlitTile = -1;
    private int kernelClearWindow = -1;

    /// <summary>Whether the window was disabled, such as because compute shaders are unsupported.</summary>
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

        // Rebuild the moving window after each terrain tile application (coalesce into one call in the next LateUpdate).
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

    /// <summary>Terrain tile data application completed. If the cell overlaps the window, schedule a rebuild for the next LateUpdate.</summary>
    private void OnTerrainTileReadyForWindow(Vector2Int cellIndex, Terrain tile)
    {
        MarkSurfaceChanged(cellIndex);
    }

    public void MarkSurfaceChanged() => windowDirty = true;

    /// <summary>Called when cell terrain changes (published/removed). Do not rebuild for edge cells entering or leaving outside the window.</summary>
    public void MarkSurfaceChanged(Vector2Int cell)
    {
        if (CellOverlapsWindow(cell)) windowDirty = true;
    }

    /// <summary>
    /// Whether the cell rectangle overlaps the current window. Always true before window initialization.
    /// Recentering always rebuilds the entire window, so excluded cells are reflected after movement too.
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
    /// Maintain the moving window. Recenter if the camera moves away from its center,
    /// and rebuild when terrain tile updates set windowDirty.
    /// Coalesce rebuilds to at most once per frame, even if multiple tiles are applied that frame.
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
            // Snap the origin to the texel-sized grid, preserving correspondence between texels and world grid points.
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
    /// Allocate the window RenderTexture and compute shader. False on unsupported platforms.
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
    /// Rebuild every window texel by extracting loaded terrain tile GPU heightmaps.
    /// Areas without any tiles remain at elevation 0 (sea level). Maximum cost is
    /// one clear plus dispatches for overlapping tiles (a dozen or so at 768m radius), negligible for the GPU.
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

                // World overlap bounds of the tile and window
                float ox0 = Mathf.Max(tilePos.x, windowOriginXZ.x);
                float oz0 = Mathf.Max(tilePos.z, windowOriginXZ.y);
                float ox1 = Mathf.Min(tilePos.x + tileSize.x, windowOriginXZ.x + windowSizeM);
                float oz1 = Mathf.Min(tilePos.z + tileSize.z, windowOriginXZ.y + windowSizeM);
                if (ox1 <= ox0 || oz1 <= oz0) continue;

                // Texel p holds elevation at world (origin+p), so write texels where origin+p lies within the overlap
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

        // Do not enable following until at least one tile is extracted (such as immediately after session start)
        // (following a cleared window would pin everything to elevation 0).
        windowHasContent = tilesDrawn > 0;
        Shader.SetGlobalFloat(ShaderPropGpuConform, windowHasContent ? 1f : 0f);
    }

    /// <summary>Release moving window resources and disable shader terrain following.</summary>
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
    /// Set the TerrainManager reference. Called during initialization by CellDataLoader or similar callers.
    /// </summary>
    public void Initialize(TerrainManager tm)
    {
        terrainManager = tm;
    }

    /// <summary>Invalidate the GPU window referencing previous terrain when the session changes.</summary>
    public void Invalidate()
    {
        Shader.SetGlobalFloat(ShaderPropGpuConform, 0f);
        windowHasContent = false;
        windowDirty = true;
        windowInitialized = false;
        Utils.InvalidateHeightCache();
    }
}
