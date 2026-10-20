
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Constants identifying visual modes.
/// </summary>
public static class VisualMode
{
    public const int MODE_NORMAL = 0;           // Normal display.
    public const int MODE_HIDDEN = 1;           // Hidden.
    public const int MODE_BOUNDARY = 2;         // Boundary display for parcels.
    public const int MODE_HIGHLIGHT_OWNER = 3;  // Owner highlighting.
}

/// <summary>
/// Visibility-flag bits independent of color modes (VisualMode/VisualModeEx).
/// Planned-object visibility and public-facility highlights can be toggled independently of color modes,
/// so VisualModeHandler stores them separately as flags rather than mode values.
/// </summary>
public static class VisualFlag
{
    public const int NONE = 0;
    public const int SHOW_PLAN = 1 << 0;         // Show planned objects (TEMPORARY/PENDING/SCHEDULED).
    public const int HIGHLIGHT_PUBLIC = 1 << 1;  // Illuminate public-facility sites; BUILDING only, rendered by PublicFacilityGlowOverlay.
    public const int SPEED_LIMIT_SIGNS = 1 << 2; // Show signs for configured road/track speed limits.
}

/// <summary>
/// High-performance visual-mode manager supporting thousands of objects.
/// </summary>
public class VisualModeHandler : MonoBehaviour
{
    public static VisualModeHandler Instance { get; private set; }

    [Header("パフォーマンス設定")]
    [SerializeField] private int batchSize = 100;              // Objects processed per frame.
    [SerializeField] private float cleanupInterval = 10f;      // Invalid-object cleanup interval (seconds).
    [SerializeField] private bool enableDebugLog = false;      // Enable/disable debug logging.

    // Current visual mode per object type.
    // Initialize PARCEL as hidden to match Tauri's initial display settings.
    private Dictionary<int, int> objectTypeModes = new Dictionary<int, int>
    {
        { InputObjectType.BUILDING, VisualMode.MODE_NORMAL },
        { InputObjectType.PARCEL, VisualMode.MODE_HIDDEN },
        { InputObjectType.PATH, VisualMode.MODE_NORMAL },
        { InputObjectType.NODE, VisualMode.MODE_NORMAL },
        { InputObjectType.TERRAIN, VisualMode.MODE_NORMAL },
        { InputObjectType.ZONE, VisualMode.MODE_HIDDEN }
    };

    // Visibility flags per object type, combining VisualFlag bits.
    // Match the UI's initial display; enable planned objects through edit presets or player selection.
    private Dictionary<int, int> objectTypeFlags = new Dictionary<int, int>
    {
        { InputObjectType.BUILDING, VisualFlag.NONE },
        { InputObjectType.PARCEL, VisualFlag.NONE },
        { InputObjectType.PATH, VisualFlag.NONE },
        { InputObjectType.NODE, VisualFlag.NONE },
        { InputObjectType.TERRAIN, VisualFlag.NONE },
        { InputObjectType.ZONE, VisualFlag.NONE }
    };

    // Registered visual objects per type, using HashSet for speed.
    private Dictionary<int, HashSet<IVisualModeHandler>> registeredObjects = new Dictionary<int, HashSet<IVisualModeHandler>>
    {
        { InputObjectType.BUILDING, new HashSet<IVisualModeHandler>() },
        { InputObjectType.PARCEL, new HashSet<IVisualModeHandler>() },
        { InputObjectType.PATH, new HashSet<IVisualModeHandler>() },
        { InputObjectType.NODE, new HashSet<IVisualModeHandler>() },
        { InputObjectType.TERRAIN, new HashSet<IVisualModeHandler>() },
        { InputObjectType.ZONE, new HashSet<IVisualModeHandler>() }
    };

    // Previous modes for delta updates.
    // Initialize previous modes to match objectTypeModes, maintaining synchronization with the UI's initial settings.
    private Dictionary<int, int> previousModes = new Dictionary<int, int>
    {
        { InputObjectType.BUILDING, VisualMode.MODE_NORMAL },
        { InputObjectType.PARCEL, VisualMode.MODE_HIDDEN },
        { InputObjectType.PATH, VisualMode.MODE_NORMAL },
        { InputObjectType.NODE, VisualMode.MODE_NORMAL },
        { InputObjectType.TERRAIN, VisualMode.MODE_NORMAL },
        { InputObjectType.ZONE, VisualMode.MODE_HIDDEN }
    };

    // Active coroutine tracking.
    private Dictionary<int, Coroutine> activeUpdateCoroutines = new Dictionary<int, Coroutine>();

    // Same types as registeredObjects keys. Keys never change after construction; RegisterObject adds only to existing keys.
    private static readonly int[] ObjectTypes =
    {
        InputObjectType.BUILDING, InputObjectType.PARCEL, InputObjectType.PATH,
        InputObjectType.NODE, InputObjectType.TERRAIN, InputObjectType.ZONE
    };
    private const int CleanupBatchSize = 2000;
    private readonly List<IVisualModeHandler> cleanupBuffer = new List<IVisualModeHandler>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        UndergroundView.Ensure();
    }

    private void Start()
    {
        // Start periodic invalid-object cleanup.
        StartCoroutine(PeriodicCleanup());
    }

    public void RegisterObject(int objectType, IVisualModeHandler visualObject)
    {
        if (registeredObjects.ContainsKey(objectType))
        {
            if (registeredObjects[objectType].Add(visualObject)) // HashSet.Add returns false for duplicates.
            {
                // Apply the current mode immediately on registration.
                int currentMode = GetMode(objectType);
                visualObject.ApplyVisualMode(currentMode);
                
                if (enableDebugLog)
                {
                    Debug.Log($"VisualModeHandler: {InputObjectType.GetName(objectType)} オブジェクトを登録 (現在のモード: {GetModeName(currentMode)}, 総数: {registeredObjects[objectType].Count})");
                }
            }
        }
    }

    public void UnregisterObject(int objectType, IVisualModeHandler visualObject)
    {
        if (registeredObjects.ContainsKey(objectType))
        {
            if (registeredObjects[objectType].Remove(visualObject))
            {
                if (enableDebugLog)
                {
                    Debug.Log($"VisualModeHandler: {InputObjectType.GetName(objectType)} オブジェクトの登録を解除 (残り: {registeredObjects[objectType].Count})");
                }
            }
        }
    }

    public void SetMode(int objectType, int mode)
    {
        if (!objectTypeModes.ContainsKey(objectType)) return;
        if (objectType == InputObjectType.TERRAIN)
        {
            bool changed = UndergroundView.IsActive != (mode == VisualModeTerrain.TRANSPARENT);
            UndergroundView.SetActive(mode == VisualModeTerrain.TRANSPARENT);
            if (changed)
            {
                RefreshObjects(InputObjectType.BUILDING);
                RefreshObjects(InputObjectType.PATH);
            }
        }

        // Skip unchanged modes.
        if (previousModes[objectType] == mode)
        {
            if (enableDebugLog) Debug.Log($"VisualModeHandler.SetMode: {InputObjectType.GetName(objectType)} を {GetModeName(mode)} に変更しようとしましたが、前回と同じモードのためスキップします");
            return;
        }

        // Stop the existing update.
        if (activeUpdateCoroutines.ContainsKey(objectType) && activeUpdateCoroutines[objectType] != null)
        {
            StopCoroutine(activeUpdateCoroutines[objectType]);
        }

        // Update the mode.
        objectTypeModes[objectType] = mode;
        previousModes[objectType] = mode;

        // Notify objects across multiple frames.
        activeUpdateCoroutines[objectType] = StartCoroutine(NotifyObjectsBatched(objectType, mode));

        if (enableDebugLog)
        {
            Debug.Log($"VisualModeHandler: {InputObjectType.GetName(objectType)} のモードを {GetModeName(mode)} に変更開始 (対象: {registeredObjects[objectType].Count} オブジェクト)");
        }
    }

    /// <summary>
    /// Renotify all registered objects of the current mode in batches without changing the color mode.
    /// Used when other display inputs change, such as underground view or coverage.
    /// </summary>
    public void RefreshObjects(int objectType)
    {
        if (activeUpdateCoroutines.TryGetValue(objectType, out var routine) && routine != null) StopCoroutine(routine);
        activeUpdateCoroutines[objectType] = StartCoroutine(NotifyObjectsBatched(objectType, GetMode(objectType)));
    }

    public int GetMode(int objectType)
    {
        return objectTypeModes.ContainsKey(objectType) ? objectTypeModes[objectType] : VisualMode.MODE_NORMAL;
    }

    /// <summary>
    /// Set visibility flags as combined VisualFlag bits.
    /// Flags are independent of color modes; on change, renotify all targets with their current color mode.
    /// VisualModeObject pulls GetFlags in ApplyVisualMode to determine visibility,
    /// so IVisualModeHandler needs no changes; PARCEL/ZONE are unaffected by these flags.
    /// </summary>
    public void SetFlags(int objectType, int flags)
    {
        if (!objectTypeFlags.ContainsKey(objectType)) return;

        // Skip unchanged flags.
        if (objectTypeFlags[objectType] == flags) return;

        objectTypeFlags[objectType] = flags;

        // Stop the existing update and renotify in batches with the current color mode.
        if (activeUpdateCoroutines.ContainsKey(objectType) && activeUpdateCoroutines[objectType] != null)
        {
            StopCoroutine(activeUpdateCoroutines[objectType]);
        }
        activeUpdateCoroutines[objectType] = StartCoroutine(NotifyObjectsBatched(objectType, GetMode(objectType)));

        if (enableDebugLog)
        {
            Debug.Log($"VisualModeHandler: {InputObjectType.GetName(objectType)} のフラグを {flags} に変更 (対象: {registeredObjects[objectType].Count} オブジェクト)");
        }
    }

    public int GetFlags(int objectType)
    {
        return objectTypeFlags.ContainsKey(objectType) ? objectTypeFlags[objectType] : VisualFlag.NONE;
    }

    private IEnumerator NotifyObjectsBatched(int objectType, int mode)
    {
        if (!registeredObjects.ContainsKey(objectType)) yield break;

        var objects = new List<IVisualModeHandler>(registeredObjects[objectType]);
        int processedCount = 0;
        int totalCount = objects.Count;

        for (int i = 0; i < objects.Count; i += batchSize)
        {
            int endIndex = Mathf.Min(i + batchSize, objects.Count);
            
            // Process one batch of objects.
            for (int j = i; j < endIndex; j++)
            {
                if (objects[j] != null)
                {
                    objects[j].ApplyVisualMode(mode);
                    processedCount++;
                }
            }

            // Wait until the next frame to spread CPU load.
            yield return null;
        }

        // Log completion.
        if (enableDebugLog)
        {
            Debug.Log($"VisualModeHandler: {InputObjectType.GetName(objectType)} の {processedCount}/{totalCount} オブジェクトのモード変更完了");
        }

        // Remove the completed coroutine from the dictionary.
        if (activeUpdateCoroutines.ContainsKey(objectType))
        {
            activeUpdateCoroutines[objectType] = null;
        }
    }

    private IEnumerator PeriodicCleanup()
    {
        while (true)
        {
            yield return new WaitForSeconds(cleanupInterval);

            // Split Unity null checks across frames rather than checking tens of thousands of registrations at once.
            // Scan a copy so registration/unregistration during yields cannot invalidate enumeration.
            int removedCount = 0;
            int budget = CleanupBatchSize;
            foreach (int objectType in ObjectTypes)
            {
                if (!registeredObjects.TryGetValue(objectType, out var set)) continue;
                cleanupBuffer.Clear();
                cleanupBuffer.AddRange(set);
                for (int i = 0; i < cleanupBuffer.Count; i++)
                {
                    var obj = cleanupBuffer[i];
                    if ((obj == null || (obj as MonoBehaviour) == null) && set.Remove(obj)) removedCount++;
                    if (--budget > 0) continue;
                    budget = CleanupBatchSize;
                    yield return null;
                }
            }
            cleanupBuffer.Clear();

            if (removedCount > 0 && enableDebugLog)
            {
                Debug.Log($"VisualModeHandler: 無効なオブジェクト参照 {removedCount} 個をクリーンアップしました");
            }
        }
    }

    public string GetModeName(int mode)
    {
        switch (mode)
        {
            case VisualMode.MODE_NORMAL: return "通常表示";
            case VisualMode.MODE_HIDDEN: return "非表示";
            case VisualMode.MODE_BOUNDARY: return "境界表示";
            case VisualMode.MODE_HIGHLIGHT_OWNER: return "ハイライト表示";
            case VisualModePath.UTILIZATION: return "交通量色分け";
            case VisualModePath.SPEED: return "通過速度色分け";
            case VisualModePath.PEDESTRIAN: return "歩行者量色分け";
            case VisualModePath.DURABILITY: return "耐久度色分け";
            case VisualModePath.ZONE_COLOR: return "所属地区色分け";
            case VisualModeBuilding.POPULATION: return "人口色分け";
            case VisualModeBuilding.RENT: return "家賃色分け";
            case VisualModeBuilding.DURABILITY: return "建物耐久度色分け";
            case VisualModeBuilding.SPACE_OTHER: return "その他スペース色分け";
            case VisualModeBuilding.SPACE_RESIDENCE: return "住居スペース色分け";
            case VisualModeBuilding.SPACE_RETAIL: return "小売スペース色分け";
            case VisualModeBuilding.SPACE_FACTORY: return "工場スペース色分け";
            case VisualModeBuilding.SPACE_OFFICE: return "オフィススペース色分け";
            case VisualModeBuilding.SPACE_FARM: return "農場スペース色分け";
            default: return "不明";
        }
    }

}
