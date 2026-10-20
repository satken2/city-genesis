using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Siccity.GLTFUtility;

public class ModelManager : MonoBehaviour
{
    public static ModelManager Instance { get; private set; }

    // Prefix pack-resolved cache keys with PackKeyPrefix because the same path can resolve differently in different sources.
    private Dictionary<string, GameObject> _modelCache = new Dictionary<string, GameObject>();
    private HashSet<string> _knownMissing = new HashSet<string>();
    private const string PackKeyPrefix = "pack:";
    private bool _isInitialized = false;

    private static readonly string[] GltfExtensions = { ".glb", ".gltf" };

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Initialize();
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
        }
    }

    private void Initialize()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        AssetPackStore.CityChanging += ForgetPackModels;
        Debug.Log("ModelManager: Initialized (lazy loading mode).");
    }

    private void OnDestroy()
    {
        if (Instance == this) AssetPackStore.CityChanging -= ForgetPackModels;
    }

    /// <summary>
    /// Get a model by resource path. For pack-owned units (AssetPackStore.UsesPack), search only the pack;
    /// otherwise search glTF under persistentDataPath, then Resources.
    /// </summary>
    public GameObject GetModel(string resourcePath, bool suppressWarning = false)
        => GetModelFrom(resourcePath, AssetPackStore.UsesPack(resourcePath), suppressWarning);

    /// <summary>Get from a caller-selected source, supporting search orders such as vehicle-type fallback that searches the pack first.</summary>
    public GameObject GetModelFrom(string resourcePath, bool fromPack, bool suppressWarning = false)
    {
        if (string.IsNullOrEmpty(resourcePath)) return null;
        string key = fromPack ? PackKeyPrefix + resourcePath : resourcePath;

        if (_modelCache.TryGetValue(key, out GameObject cached))
        {
            return cached;
        }

        if (_knownMissing.Contains(key))
        {
            return null;
        }

        GameObject loaded = fromPack ? AssetPackStore.LoadModel(resourcePath)
            : TryLoadGltf(resourcePath) ?? Resources.Load<GameObject>(resourcePath);
        if (loaded != null)
        {
            _modelCache[key] = loaded;
            return loaded;
        }

        _knownMissing.Add(key);

        if (!suppressWarning)
        {
            Debug.LogWarning(fromPack ? $"ModelManager: Model not found in asset pack {AssetPackStore.MountedPackId}: {resourcePath}"
                : $"ModelManager: Model not found at Resources/{resourcePath}");
        }
        return null;
    }

    public bool HasModel(string resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath)) return false;
        if (AssetPackStore.UsesPack(resourcePath)) return AssetPackStore.Has(resourcePath);
        if (_modelCache.ContainsKey(resourcePath)) return true;
        if (_knownMissing.Contains(resourcePath)) return false;

        if (HasGltfFile(resourcePath))
        {
            return true;
        }

        GameObject loaded = Resources.Load<GameObject>(resourcePath);
        if (loaded != null)
        {
            _modelCache[resourcePath] = loaded;
            return true;
        }

        _knownMissing.Add(resourcePath);
        return false;
    }

    /// <summary>Discard pack-loaded model records on city switches; standard models remain reusable.</summary>
    private void ForgetPackModels()
    {
        foreach (string key in new List<string>(_modelCache.Keys))
        {
            if (key.StartsWith(PackKeyPrefix, System.StringComparison.Ordinal)) _modelCache.Remove(key);
        }
        _knownMissing.RemoveWhere(key => key.StartsWith(PackKeyPrefix, System.StringComparison.Ordinal));
    }

    private GameObject TryLoadGltf(string resourcePath)
    {
        string basePath = Path.Combine(Application.persistentDataPath, resourcePath);

        foreach (string ext in GltfExtensions)
        {
            string fullPath = basePath + ext;
            if (File.Exists(fullPath))
            {
                try
                {
                    GameObject result = Importer.LoadFromFile(fullPath);
                    if (result != null)
                    {
                        result.SetActive(false);
                        result.transform.SetParent(transform);
                        result.name = Path.GetFileName(resourcePath);
                        Debug.Log($"ModelManager: Loaded glTF from {fullPath}");
                        return result;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"ModelManager: Failed to load glTF {fullPath}: {e.Message}");
                }
            }
        }

        return null;
    }

    private bool HasGltfFile(string resourcePath)
    {
        string basePath = Path.Combine(Application.persistentDataPath, resourcePath);
        foreach (string ext in GltfExtensions)
        {
            if (File.Exists(basePath + ext))
            {
                return true;
            }
        }
        return false;
    }
}
