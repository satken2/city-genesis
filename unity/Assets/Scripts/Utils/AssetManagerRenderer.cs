using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GeneratedModels;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Asset Manager render request (asset_manager:render). rows are tab rows: building/path variants, vehicle types, or prop types.</summary>
public sealed class AssetManagerRequest
{
    public string RequestId;
    public string PackId;
    public string Category;
    public int[] Rows;
}

/// <summary>Asset Manager interactive building preview request (asset_manager:preview).</summary>
public sealed class AssetManagerPreviewRequest
{
    public string RequestId;
    public string PackId;
    public int Row;
    public int Model;
    public float Yaw;
    public float Pitch;
    public float Zoom;
    public int Width;
    public int Height;
}

/// <summary>
/// Handle Asset Manager (Tauri) render requests. Mount the requested pack and return each row's models (source, unit, index, default weight)
/// via asset_manager:models, then capture each model and write PNGs to cache/asset_thumbnails/ under the user directory.
/// Notify writes via asset_manager:thumbnail; do not send images through MMF. Stop the previous capture on a new request or city load.
///
/// Also handles interactive building preview requests (asset_manager:preview / asset_manager:preview_close), caching the generated building
/// on a separate stage so orbit/zoom updates only reposition the camera and encode a JPEG without rebuilding the mesh.
///
/// Resolve captures exactly as rendering does. Buildings use the same generator with master default size and a representative floor count
/// (one-third of the way from min_floors to max_floors), weighting only the selected model. Vehicles use the type/index model; paths use
/// variant body (or type fallback); props use their type's model.
/// Temporarily replace lighting with a capture directional light and uniform ambient light, independent of scene time and weather.
/// </summary>
public sealed class AssetManagerRenderer : MonoBehaviour
{
    public const string CATEGORY_BUILDINGS = "buildings";
    public const string CATEGORY_PATHS = "paths";
    public const string CATEGORY_VEHICLES = "vehicles";
    public const string CATEGORY_PROPS = "props";
    private const string ThumbnailFolder = "cache/asset_thumbnails";
    private const int Width = 160;
    private const int Height = 120;
    private const int PreviewDefaultWidth = 720;
    private const int PreviewDefaultHeight = 540;
    private const int PreviewSlotCount = 4; // Rotating filenames so Rust never reads a slot being overwritten.
    private const float PreviewMinZoom = 0.5f;
    private const float PreviewMaxZoom = 3.2f;
    private const float FieldOfView = 30f;
    private const float FallbackSide = 20f; // Building site side length when master data has no default size (m).
    private static readonly Vector3 Stage = new Vector3(0f, -20000f, 0f); // Capture location away from the city and terrain.
    private static readonly Vector3 PreviewStage = new Vector3(0f, -25000f, 0f); // Isolated from Stage so thumbnail & preview captures never overlap.
    private static readonly Color Background = new Color(0.16f, 0.18f, 0.22f);
    private static readonly Color Ambient = new Color(0.45f, 0.47f, 0.5f);

    private int generation;
    private int previewGeneration;
    private int previewSlot;
    private string previewPackId;
    private int previewRow = -1;
    private int previewModel = -1;
    private GameObject previewTarget;
    private Mesh previewMesh;

    private struct Shot
    {
        public int Row;
        public int Model;
        public string Unit;
        public string Path; // Logical model names for path and prop samples.
    }

    private void OnEnable()
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.OnAssetManagerRender += HandleRender;
        IpcClient.Instance.OnAssetManagerPreview += HandlePreview;
        IpcClient.Instance.OnAssetManagerPreviewClose += HandlePreviewClose;
        IpcClient.Instance.OnGameLoading += Cancel;
    }

    private void OnDisable()
    {
        ClearPreviewTarget();
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.OnAssetManagerRender -= HandleRender;
        IpcClient.Instance.OnAssetManagerPreview -= HandlePreview;
        IpcClient.Instance.OnAssetManagerPreviewClose -= HandlePreviewClose;
        IpcClient.Instance.OnGameLoading -= Cancel;
    }

    private void Cancel()
    {
        generation++;
        previewGeneration++;
        ClearPreviewTarget();
    }

    private void HandleRender(AssetManagerRequest request)
    {
        generation++;
        StartCoroutine(Render(request, generation));
    }

    private void HandlePreview(AssetManagerPreviewRequest request)
    {
        previewGeneration++;
        StartCoroutine(RenderPreview(request, previewGeneration));
    }

    private void HandlePreviewClose()
    {
        previewGeneration++;
        ClearPreviewTarget();
    }

    private void ClearPreviewTarget()
    {
        if (previewTarget != null) DestroyImmediate(previewTarget);
        if (previewMesh != null) Destroy(previewMesh);
        previewTarget = null;
        previewMesh = null;
        previewPackId = null;
        previewRow = -1;
        previewModel = -1;
    }

    private IEnumerator Render(AssetManagerRequest request, int current)
    {
        // Switching packs while rendering a city would change its appearance, so finish without returning rows.
        if (GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted())
        {
            IpcClient.Instance?.EmitAssetManagerModels(request.RequestId, new List<Dictionary<string, object>>());
            yield break;
        }
        yield return new WaitUntil(() => MasterDataStore.Instance == null || MasterDataStore.Instance.IsInitialized);
        if (current != generation) yield break;
        if (previewPackId != null && previewPackId != request.PackId) ClearPreviewTarget();
        yield return AssetPackStore.MountForViewing(request.PackId);
        if (current != generation) yield break;

        var shots = new List<Shot>();
        IpcClient.Instance?.EmitAssetManagerModels(request.RequestId, Describe(request, shots));
        string folder = PrepareFolder();
        foreach (Shot shot in shots)
        {
            yield return null;
            if (current != generation) yield break;
            string file = $"{request.Category}_{shot.Row}_{shot.Model}.png";
            bool written = false;
            try
            {
                byte[] png = folder != null ? Capture(request.Category, shot) : null;
                if (png != null)
                {
                    File.WriteAllBytes(Path.Combine(folder, file), png);
                    written = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AssetManagerRenderer: {request.Category} {shot.Row} のモデル {shot.Model} を撮れませんでした: {e.Message}");
            }
            IpcClient.Instance?.EmitAssetManagerThumbnail(request.RequestId, shot.Row, shot.Model, written ? file : null);
        }
    }

    private IEnumerator RenderPreview(AssetManagerPreviewRequest request, int current)
    {
        if (GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted())
        {
            IpcClient.Instance?.EmitAssetManagerPreview(request.RequestId, null);
            yield break;
        }
        if (MasterDataStore.Instance != null && !MasterDataStore.Instance.IsInitialized)
        {
            yield return new WaitUntil(() => MasterDataStore.Instance == null || MasterDataStore.Instance.IsInitialized);
            if (current != previewGeneration) yield break;
        }

        if (previewTarget == null || previewPackId != request.PackId || previewRow != request.Row || previewModel != request.Model)
        {
            ClearPreviewTarget();
            yield return AssetPackStore.MountForViewing(request.PackId);
            if (current != previewGeneration) yield break;
            try
            {
                string unit = ModelWeights.BUILDING_ROOT + BuildingMeshUtils.ResolveModelNaming(SampleBuilding(request.Row)).Prefix;
                previewTarget = BuildBuildingTarget(request.Row, request.Model, unit, out previewMesh);
                if (previewTarget != null)
                {
                    previewTarget.transform.position = PreviewStage;
                    previewTarget.SetActive(false);
                    previewPackId = request.PackId;
                    previewRow = request.Row;
                    previewModel = request.Model;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AssetManagerRenderer: 建物 {request.Row} のモデル {request.Model} のプレビューを作れませんでした: {e.Message}");
                ClearPreviewTarget();
            }
        }

        if (previewTarget == null)
        {
            IpcClient.Instance?.EmitAssetManagerPreview(request.RequestId, null);
            yield break;
        }

        string folder = EnsureFolder();
        string file = $"preview_{previewSlot}.jpg";
        previewSlot = (previewSlot + 1) % PreviewSlotCount;
        bool written = false;
        try
        {
            float yawRad = request.Yaw * Mathf.Deg2Rad;
            float pitchRad = Mathf.Clamp(request.Pitch, 0f, 85f) * Mathf.Deg2Rad;
            float cosPitch = Mathf.Cos(pitchRad);
            Vector3 view = new Vector3(cosPitch * Mathf.Sin(yawRad), Mathf.Sin(pitchRad), -cosPitch * Mathf.Cos(yawRad));
            float zoom = Mathf.Clamp(request.Zoom > 0f ? request.Zoom : 1f, PreviewMinZoom, PreviewMaxZoom);
            int width = Mathf.Clamp(request.Width > 0 ? request.Width : PreviewDefaultWidth, 160, 1280);
            int height = Mathf.Clamp(request.Height > 0 ? request.Height : PreviewDefaultHeight, 120, 960);

            previewTarget.SetActive(true);
            byte[] jpg = folder != null ? Photograph(previewTarget, view, zoom, width, height, jpeg: true) : null;
            if (jpg != null)
            {
                File.WriteAllBytes(Path.Combine(folder, file), jpg);
                written = true;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"AssetManagerRenderer: 建物 {request.Row} のモデル {request.Model} のプレビューを撮れませんでした: {e.Message}");
        }
        finally
        {
            if (previewTarget != null) previewTarget.SetActive(false);
        }
        IpcClient.Instance?.EmitAssetManagerPreview(request.RequestId, written ? file : null);
    }

    /// <summary>Build each row's source, unit, and models; append capture targets to shots. Paths and props have one model per row and null weights.</summary>
    private static List<Dictionary<string, object>> Describe(AssetManagerRequest request, List<Shot> shots)
    {
        var rows = new List<Dictionary<string, object>>();
        foreach (int row in request.Rows ?? Array.Empty<int>())
        {
            string unit = null;
            var models = new List<Dictionary<string, object>>();
            switch (request.Category)
            {
                case CATEGORY_BUILDINGS:
                case CATEGORY_VEHICLES:
                    unit = request.Category == CATEGORY_BUILDINGS
                        ? ModelWeights.BUILDING_ROOT + BuildingMeshUtils.ResolveModelNaming(SampleBuilding(row)).Prefix
                        : VehicleModelResolver.Unit(row);
                    foreach (ModelWeights.Model model in ModelWeights.Bundled(unit))
                    {
                        models.Add(new Dictionary<string, object> { ["id"] = model.Id, ["weight"] = model.Weight });
                        shots.Add(new Shot { Row = row, Model = model.Id, Unit = unit });
                    }
                    break;
                case CATEGORY_PATHS:
                case CATEGORY_PROPS:
                    string path = request.Category == CATEGORY_PATHS ? PathBody(row) : $"Models/Props/{row}";
                    if (path != null && ModelManager.Instance?.GetModel(path, suppressWarning: true) != null)
                    {
                        unit = AssetPackStore.UnitOf(path);
                        models.Add(new Dictionary<string, object> { ["id"] = 0, ["weight"] = null });
                        shots.Add(new Shot { Row = row, Model = 0, Unit = unit, Path = path });
                    }
                    break;
            }
            string source = unit != null && AssetPackStore.UsesPack(unit + "/") ? AssetPackStore.MountedPackId : Constants.ASSET_PACK_STANDARD;
            rows.Add(new Dictionary<string, object> { ["row"] = row, ["source"] = source, ["unit"] = unit, ["models"] = models });
        }
        return rows;
    }

    /// <summary>Sample path body: prefer the variant folder, then the type folder; null if neither has one.</summary>
    private static string PathBody(int variant)
    {
        string own = $"Models/Paths/p{variant}/body";
        if (ModelManager.Instance?.GetModel(own, suppressWarning: true) != null) return own;
        PathMasterItem master = null;
        MasterDataStore.Instance?.GetPathMasterDictionary().TryGetValue(variant, out master);
        return master != null ? $"Models/Paths/p{master.type}/body" : null;
    }

    /// <summary>Representative floor count: one-third of the way from min_floors to max_floors.</summary>
    private static int SampleFloors(MasterDataStore.AgentMasterItem master)
    {
        int minFloors = Mathf.Max(1, master?.min_floors ?? 1);
        int maxFloors = Mathf.Max(minFloors, master?.max_floors ?? minFloors);
        return Mathf.RoundToInt(minFloors + (maxFloors - minFloors) / 3f);
    }

    /// <summary>Sample building: representative floor count on a rectangle with the master default dimensions (default_width x default_height).</summary>
    private static BuildingSummary SampleBuilding(int variant)
    {
        MasterDataStore.AgentMasterItem master = null;
        MasterDataStore.Instance?.GetAgentMasterDictionary().TryGetValue(variant, out master);
        float halfWidth = (master?.default_width ?? FallbackSide) * 0.5f;
        float halfDepth = (master?.default_height ?? FallbackSide) * 0.5f;
        var polygon = new List<object>();
        foreach (Vector2 corner in new[] { new Vector2(-halfWidth, -halfDepth), new Vector2(halfWidth, -halfDepth),
            new Vector2(halfWidth, halfDepth), new Vector2(-halfWidth, halfDepth) })
        {
            polygon.Add(new List<object> { corner.x, corner.y });
        }
        return new BuildingSummary
        {
            variant = variant,
            bd_type = master?.bd_type,
            floors = SampleFloors(master),
            status = Constants.STATUS_ACTIVE,
            polygon = polygon,
            center_point = new List<object> { 0f, 0f },
            visual_variant = 0.5f,
            visual_variant2 = 0.5f,
            visual_variant3 = 0.5f,
        };
    }

    /// <summary>Create and return the thumbnail directory without clearing existing files, or null if no user directory is available.</summary>
    private static string EnsureFolder()
    {
        if (UserDataDir.Root == null) return null;
        string folder = Path.Combine(UserDataDir.Root, ThumbnailFolder);
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Debug.LogWarning($"AssetManagerRenderer: サムネイルのフォルダ {folder} を使えません: {e.Message}");
            return null;
        }
    }

    /// <summary>Empty and return the thumbnail directory, or null if no user directory is available.</summary>
    private static string PrepareFolder()
    {
        string folder = EnsureFolder();
        if (folder == null) return null;
        try
        {
            foreach (string pattern in new[] { "*.png", "*.jpg" })
            {
                foreach (string old in Directory.GetFiles(folder, pattern)) File.Delete(old);
            }
            return folder;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Debug.LogWarning($"AssetManagerRenderer: サムネイルのフォルダ {folder} を使えません: {e.Message}");
            return null;
        }
    }

    private static GameObject BuildBuildingTarget(int variant, int modelId, string unit, out Mesh generated)
    {
        generated = null;
        BuildingMeshResult result;
        using (ModelWeights.Isolate(unit, modelId))
        {
            result = new BuildingMeshBuilder().Generate(SampleBuilding(variant));
        }
        if (result?.Mesh == null) return null;
        generated = result.Mesh;
        if (result.ColliderMesh != null && result.ColliderMesh != generated) Destroy(result.ColliderMesh);
        var target = new GameObject("AssetThumbnailBuilding");
        target.AddComponent<MeshFilter>().sharedMesh = generated;
        target.AddComponent<MeshRenderer>().sharedMaterials = result.Materials;
        return target;
    }

    private static byte[] Capture(string category, Shot shot)
    {
        GameObject target = null;
        Mesh generated = null;
        try
        {
            switch (category)
            {
                case CATEGORY_BUILDINGS:
                    target = BuildBuildingTarget(shot.Row, shot.Model, shot.Unit, out generated);
                    if (target == null) return null;
                    break;
                case CATEGORY_VEHICLES:
                    target = VehicleModelResolver.CreateInstance(shot.Row, shot.Model, null);
                    break;
                default:
                    GameObject prefab = ModelManager.Instance?.GetModel(shot.Path, suppressWarning: true);
                    if (prefab == null) return null;
                    target = Instantiate(prefab);
                    break;
            }
            target.transform.position = Stage;
            target.SetActive(true);
            Vector3 view = category == CATEGORY_VEHICLES ? new Vector3(0.9f, 0.55f, 1.3f) : new Vector3(0.9f, 0.75f, -1.3f);
            return Photograph(target, view, 1f, Width, Height, jpeg: false);
        }
        finally
        {
            if (target != null) DestroyImmediate(target);
            if (generated != null) Destroy(generated);
        }
    }

    /// <summary>
    /// Capture along view from a distance fitting the bounding sphere vertically (divided by zoom). Building fronts (vertex 0 -> 1) face -Z,
    /// while vehicle fronts face +Z, so thumbnails capture each from diagonally above its front.
    /// </summary>
    private static byte[] Photograph(GameObject target, Vector3 view, float zoom, int width, int height, bool jpeg)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return null;
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
        float radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
        float distance = radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad) / zoom;
        Vector3 direction = view.normalized;

        var cameraObject = new GameObject("AssetThumbnailCamera");
        var lightObject = new GameObject("AssetThumbnailLight");
        Light sun = RenderSettings.sun;
        bool sunEnabled = sun != null && sun.enabled;
        bool fog = RenderSettings.fog;
        AmbientMode ambientMode = RenderSettings.ambientMode;
        SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;
        try
        {
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.fieldOfView = FieldOfView;
            camera.transform.position = bounds.center + direction * distance;
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = Mathf.Max(0.05f, distance - radius * 1.5f);
            camera.farClipPlane = distance + radius * 1.5f;

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            Vector3 from = Quaternion.Euler(0f, 25f, 0f) * direction + Vector3.up * 0.6f;
            lightObject.transform.rotation = Quaternion.LookRotation(-from.normalized);

            if (sun != null) sun.enabled = false;
            RenderSettings.sun = light;
            RenderSettings.fog = false;
            var ambient = new SphericalHarmonicsL2();
            ambient.AddAmbientLight(Ambient);
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = ambient;
            return jpeg ? CameraCaptureUtil.CaptureJpg(camera, width, height) : CameraCaptureUtil.CapturePng(camera, width, height);
        }
        finally
        {
            RenderSettings.sun = sun;
            if (sun != null) sun.enabled = sunEnabled;
            RenderSettings.fog = fog;
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientProbe = ambientProbe;
            DestroyImmediate(cameraObject);
            DestroyImmediate(lightObject);
        }
    }
}
