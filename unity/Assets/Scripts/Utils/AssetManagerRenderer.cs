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

/// <summary>
/// Handle Asset Manager (Tauri) render requests. Mount the requested pack and return each row's models (source, unit, index, default weight)
/// via asset_manager:models, then capture each model and write PNGs to cache/asset_thumbnails/ under the user directory.
/// Notify writes via asset_manager:thumbnail; do not send images through MMF. Stop the previous capture on a new request or city load.
///
/// Resolve captures exactly as rendering does. Buildings use the same generator with master default size and maximum floors,
/// weighting only the selected model. Vehicles use the type/index model; paths use variant body (or type fallback); props use their type's model.
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
    private const float FieldOfView = 30f;
    private const float FallbackSide = 20f; // Building site side length when master data has no default size (m).
    private static readonly Vector3 Stage = new Vector3(0f, -20000f, 0f); // Capture location away from the city and terrain.
    private static readonly Color Background = new Color(0.16f, 0.18f, 0.22f);
    private static readonly Color Ambient = new Color(0.45f, 0.47f, 0.5f);

    private int generation;

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
        IpcClient.Instance.OnGameLoading += Cancel;
    }

    private void OnDisable()
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.OnAssetManagerRender -= HandleRender;
        IpcClient.Instance.OnGameLoading -= Cancel;
    }

    private void Cancel() => generation++;

    private void HandleRender(AssetManagerRequest request)
    {
        generation++;
        StartCoroutine(Render(request, generation));
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

    /// <summary>Sample building: maximum floors on a rectangle with the master default dimensions (default_width x default_height).</summary>
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
            floors = Mathf.Max(1, master?.max_floors ?? 1),
            status = Constants.STATUS_ACTIVE,
            polygon = polygon,
            center_point = new List<object> { 0f, 0f },
            visual_variant = 0.5f,
            visual_variant2 = 0.5f,
            visual_variant3 = 0.5f,
        };
    }

    /// <summary>Empty and return the thumbnail directory, or null if no user directory is available.</summary>
    private static string PrepareFolder()
    {
        if (UserDataDir.Root == null) return null;
        string folder = Path.Combine(UserDataDir.Root, ThumbnailFolder);
        try
        {
            Directory.CreateDirectory(folder);
            foreach (string old in Directory.GetFiles(folder, "*.png")) File.Delete(old);
            return folder;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Debug.LogWarning($"AssetManagerRenderer: サムネイルのフォルダ {folder} を使えません: {e.Message}");
            return null;
        }
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
                    BuildingMeshResult result;
                    using (ModelWeights.Isolate(shot.Unit, shot.Model))
                    {
                        result = new BuildingMeshBuilder().Generate(SampleBuilding(shot.Row));
                    }
                    if (result?.Mesh == null) return null;
                    generated = result.Mesh;
                    if (result.ColliderMesh != null && result.ColliderMesh != generated) Destroy(result.ColliderMesh);
                    target = new GameObject("AssetThumbnailBuilding");
                    target.AddComponent<MeshFilter>().sharedMesh = generated;
                    target.AddComponent<MeshRenderer>().sharedMaterials = result.Materials;
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
            return Photograph(target, category == CATEGORY_VEHICLES ? new Vector3(0.9f, 0.55f, 1.3f) : new Vector3(0.9f, 0.75f, -1.3f));
        }
        finally
        {
            if (target != null) DestroyImmediate(target);
            if (generated != null) Destroy(generated);
        }
    }

    /// <summary>
    /// Capture along view from a distance fitting the bounding sphere vertically. Building fronts (vertex 0 -> 1) face -Z, while vehicle fronts face +Z,
    /// so capture each from diagonally above its front.
    /// </summary>
    private static byte[] Photograph(GameObject target, Vector3 view)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return null;
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
        float radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
        float distance = radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad);
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
            return CameraCaptureUtil.CapturePng(camera, Width, Height);
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
