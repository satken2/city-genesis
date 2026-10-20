using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// City asset pack (DLC models/materials). If the city's asset_pack is available from the core's GET /system/asset_packs,
/// open its bundle and index its contents; otherwise use standard assets only. ModelManager loads models on demand when rendering.
///
/// Packs use the same logical names as standard assets (Models/... and Materials/{name}). Resolution follows one rule: if the pack owns
/// a unit (UnitOf), search only inside the pack; otherwise search standard assets. Path variants use the pack if it has the variant
/// or type folder. For building prefixes (landmark, variant, building type) and vehicle-type fallbacks,
/// search the pack first (BuildingMeshUtils / VehicleModelResolver). ModelWeights stores building/vehicle model lists and weights.
/// While no city is open, Asset Manager mounts the pack being viewed through MountForViewing.
/// </summary>
public static class AssetPackStore
{
    public const string BUNDLE_DIR = "windows"; // Platform-specific bundle location under the pack's assets/ directory.
    public const string INDEX_FILE = "index.txt"; // Logical names in the bundle, one per line, written by the pack build.
    public const string MATERIAL_ROOT = "Materials/";
    private const string PATH_FOLDER_PREFIX = "Models/Paths/p";
    private static readonly string[] NameSuffixes = { "_close", "_far", "_right", "_left" };

    /// <summary>ID of the mounted pack, or null when using only standard assets.</summary>
    public static string MountedPackId { get; private set; }

    /// <summary>
    /// Notify before rendering a new city, before remounting its pack or refetching master data, and before Asset Manager switches packs.
    /// Caches holding resolved models, materials, or master values must release them here: even the same logical name or variant
    /// can refer to different resources or values in another city or pack.
    /// </summary>
    public static event Action CityChanging;

    private static AssetBundle bundle;
    private static readonly HashSet<string> names = new HashSet<string>();
    private static readonly HashSet<string> units = new HashSet<string>();

    [Serializable]
    private class CityEnvelope { public CityAssetPack city; }
    [Serializable]
    private class CityAssetPack { public string asset_pack; }
    [Serializable]
    private class AssetPackList { public List<AssetPackEntry> asset_packs; }
    [Serializable]
    private class AssetPackEntry { public string pack_id; public string path; }

    /// <summary>
    /// Call before rendering after a city switch. Invalidate resolved caches, reload player weights,
    /// and mount the new city's pack unless already mounted. Retain the previous pack if fetching fails.
    /// </summary>
    public static IEnumerator PrepareForCity()
    {
        CityChanging?.Invoke();
        ModelWeights.ReloadUser();
        string cityPack = null;
        bool cityLoaded = false;
        yield return ApiClient.Get<CityEnvelope>("/city/", env => { cityPack = env?.city?.asset_pack; cityLoaded = true; },
            err => Debug.LogWarning($"AssetPackStore: 都市のアセットパックを取得できません: {err}"));
        if (!cityLoaded) yield break;
        yield return Open(cityPack, null);
    }

    /// <summary>
    /// Mount the pack viewed by Asset Manager, or unmount for standard/unavailable packs. Invalidate resolved caches only
    /// when the mounted pack changes. PrepareForCity remounts the city's pack when opening a city.
    /// </summary>
    public static IEnumerator MountForViewing(string packId)
    {
        yield return Open(packId, () => CityChanging?.Invoke());
    }

    private static IEnumerator Open(string packId, Action beforeChange)
    {
        AssetPackList usable = null;
        yield return ApiClient.Get<AssetPackList>("/system/asset_packs", list => usable = list,
            err => Debug.LogWarning($"AssetPackStore: 使えるアセットパックを取得できません: {err}"));
        if (usable == null) yield break;

        AssetPackEntry entry = usable.asset_packs?.Find(pack => pack.pack_id == packId);
        if (entry?.pack_id == MountedPackId) yield break;

        beforeChange?.Invoke();
        Unmount();
        if (entry != null)
        {
            yield return Load(entry);
        }
    }

    private static IEnumerator Load(AssetPackEntry entry)
    {
        string directory = Path.Combine(entry.path, BUNDLE_DIR);
        string indexPath = Path.Combine(directory, INDEX_FILE);
        if (!File.Exists(indexPath))
        {
            Debug.LogWarning($"AssetPackStore: パック {entry.pack_id} の一覧 {indexPath} がありません。標準で描きます");
            yield break;
        }
        var request = AssetBundle.LoadFromFileAsync(Path.Combine(directory, entry.pack_id));
        yield return request;
        if (request.assetBundle == null)
        {
            Debug.LogWarning($"AssetPackStore: パック {entry.pack_id} のバンドルを開けません。標準で描きます");
            yield break;
        }
        bundle = request.assetBundle;
        foreach (string line in File.ReadAllLines(indexPath))
        {
            string name = line.Trim();
            if (name.Length == 0) continue;
            names.Add(name);
            units.Add(UnitOf(name));
        }
        string weightsPath = Path.Combine(directory, ModelWeights.FILE_NAME);
        try
        {
            ModelWeights.SetPack(File.Exists(weightsPath) ? File.ReadAllText(weightsPath) : null);
        }
        catch (Exception e) when (e is IOException || e is Newtonsoft.Json.JsonException)
        {
            Debug.LogWarning($"AssetPackStore: パック {entry.pack_id} の重み {weightsPath} を読めません: {e.Message}");
            ModelWeights.SetPack(null);
        }
        MountedPackId = entry.pack_id;
        Debug.Log($"AssetPackStore: パック {entry.pack_id} を開きました({names.Count} 件)");
    }

    private static void Unmount()
    {
        if (bundle != null) bundle.Unload(true);
        bundle = null;
        MountedPackId = null;
        names.Clear();
        units.Clear();
        ModelWeights.SetPack(null);
    }

    /// <summary>
    /// Unit used for pack matching. For Models/{category}/{folder}/..., use the folder; for Models/{category}/{name}, strip close-up,
    /// distant, and pose suffixes; for Materials/{name}, use that name.
    /// </summary>
    public static string UnitOf(string logicalPath)
    {
        if (!logicalPath.StartsWith("Models/", StringComparison.Ordinal)) return logicalPath;
        int category = logicalPath.IndexOf('/', "Models/".Length);
        if (category < 0) return logicalPath;
        int folder = logicalPath.IndexOf('/', category + 1);
        if (folder >= 0) return logicalPath.Substring(0, folder);
        foreach (string suffix in NameSuffixes)
        {
            if (logicalPath.EndsWith(suffix, StringComparison.Ordinal))
                return logicalPath.Substring(0, logicalPath.Length - suffix.Length);
        }
        return logicalPath;
    }

    /// <summary>Whether to search for this logical name inside the pack, based on unit ownership.</summary>
    public static bool UsesPack(string logicalPath)
    {
        if (bundle == null) return false;
        string unit = UnitOf(logicalPath);
        if (units.Contains(unit)) return true;
        return unit.StartsWith(PATH_FOLDER_PREFIX, StringComparison.Ordinal)
            && int.TryParse(unit.Substring(PATH_FOLDER_PREFIX.Length), out int variant)
            && MasterDataStore.Instance != null
            && MasterDataStore.Instance.GetPathMasterDictionary().TryGetValue(variant, out var master)
            && units.Contains(PATH_FOLDER_PREFIX + master.type);
    }

    /// <summary>Whether the pack owns this unit, such as a building-prefix folder.</summary>
    public static bool HasUnit(string unit) => bundle != null && units.Contains(unit);

    public static bool Has(string logicalPath) => bundle != null && names.Contains(logicalPath);

    public static GameObject LoadModel(string logicalPath)
        => Has(logicalPath) ? bundle.LoadAsset<GameObject>(logicalPath) : null;

    /// <summary>Resolve a named material from the pack if present, otherwise directly under Resources; null if absent.</summary>
    public static Material LoadNamedMaterial(string materialName)
    {
        string logicalPath = MATERIAL_ROOT + materialName;
        return Has(logicalPath) ? bundle.LoadAsset<Material>(logicalPath) : Resources.Load<Material>(materialName);
    }

}
