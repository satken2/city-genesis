using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Siccity.GLTFUtility;

public class ModelManager : MonoBehaviour
{
    public static ModelManager Instance { get; private set; }

    private Dictionary<string, GameObject> _modelCache = new Dictionary<string, GameObject>();
    private HashSet<string> _knownMissing = new HashSet<string>();
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
        Debug.Log("ModelManager: Initialized (lazy loading mode).");
    }

    /// <summary>
    /// リソースパスでモデルを取得。persistentDataPath の glTF を優先し、なければ Resources.Load にフォールバック。
    /// </summary>
    public GameObject GetModel(string resourcePath, bool suppressWarning = false)
    {
        if (string.IsNullOrEmpty(resourcePath)) return null;

        if (_modelCache.TryGetValue(resourcePath, out GameObject cached))
        {
            return cached;
        }

        if (_knownMissing.Contains(resourcePath))
        {
            return null;
        }

        GameObject gltfResult = TryLoadGltf(resourcePath);
        if (gltfResult != null)
        {
            _modelCache[resourcePath] = gltfResult;
            return gltfResult;
        }

        GameObject loaded = Resources.Load<GameObject>(resourcePath);
        if (loaded != null)
        {
            _modelCache[resourcePath] = loaded;
            return loaded;
        }

        _knownMissing.Add(resourcePath);

        if (!suppressWarning)
        {
            Debug.LogWarning($"ModelManager: Model not found at Resources/{resourcePath}");
        }
        return null;
    }

    public bool HasModel(string resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath)) return false;
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
