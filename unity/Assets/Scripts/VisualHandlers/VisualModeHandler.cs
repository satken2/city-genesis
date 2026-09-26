
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ビジュアルモードを示す定数
/// </summary>
public static class VisualMode
{
    public const int MODE_NORMAL = 0;           // 通常表示
    public const int MODE_HIDDEN = 1;           // 非表示
    public const int MODE_BOUNDARY = 2;         // 境界表示(パーセル用)
    public const int MODE_HIGHLIGHT_OWNER = 3;  // 所有者ハイライト表示
}

/// <summary>
/// 色モード(VisualMode/VisualModeEx)と直交する表示フラグのビット定数。
/// 「計画中の表示」「公共施設ハイライト」は色モードと独立にON/OFFできるため、
/// モード値ではなくフラグとして VisualModeHandler が別系統で保持する。
/// </summary>
public static class VisualFlag
{
    public const int NONE = 0;
    public const int SHOW_PLAN = 1 << 0;         // 計画中(TEMPORARY/PENDING/SCHEDULED)のオブジェクトを表示する
    public const int HIGHLIGHT_PUBLIC = 1 << 1;  // 公共施設の敷地を光らせる(BUILDINGのみ有効。描画は PublicFacilityGlowOverlay)
    public const int SPEED_LIMIT_SIGNS = 1 << 2; // 道路・線路の設定制限速度を標識で表示する
}

/// <summary>
/// 数千オブジェクトに対応した高パフォーマンスビジュアルモード管理ハンドラ
/// </summary>
public class VisualModeHandler : MonoBehaviour
{
    public static VisualModeHandler Instance { get; private set; }

    [Header("パフォーマンス設定")]
    [SerializeField] private int batchSize = 100;              // 1フレームで処理するオブジェクト数
    [SerializeField] private float cleanupInterval = 10f;      // 無効オブジェクトクリーンアップ間隔(秒)
    [SerializeField] private bool enableDebugLog = false;      // デバッグログの有効/無効

    // オブジェクトタイプごとの現在のビジュアルモード
    // UI側(Tauri)の初期表示設定(非表示)と状態を同期させるため、土地区画(PARCEL)の初期値は非表示に設定する。
    private Dictionary<int, int> objectTypeModes = new Dictionary<int, int>
    {
        { InputObjectType.BUILDING, VisualMode.MODE_NORMAL },
        { InputObjectType.PARCEL, VisualMode.MODE_HIDDEN },
        { InputObjectType.PATH, VisualMode.MODE_NORMAL },
        { InputObjectType.NODE, VisualMode.MODE_NORMAL },
        { InputObjectType.TERRAIN, VisualMode.MODE_NORMAL },
        { InputObjectType.ZONE, VisualMode.MODE_HIDDEN }
    };

    // オブジェクトタイプごとの表示フラグ(VisualFlag のビット和)。
    // UI側(Tauri)の初期表示設定(計画中=ON)と一致させるため、建物と道路は SHOW_PLAN を初期値にする。
    private Dictionary<int, int> objectTypeFlags = new Dictionary<int, int>
    {
        { InputObjectType.BUILDING, VisualFlag.SHOW_PLAN },
        { InputObjectType.PARCEL, VisualFlag.NONE },
        { InputObjectType.PATH, VisualFlag.SHOW_PLAN },
        { InputObjectType.NODE, VisualFlag.NONE },
        { InputObjectType.TERRAIN, VisualFlag.NONE },
        { InputObjectType.ZONE, VisualFlag.NONE }
    };

    // オブジェクトタイプごとの登録されたビジュアルオブジェクト（HashSetで高速化）
    private Dictionary<int, HashSet<IVisualModeHandler>> registeredObjects = new Dictionary<int, HashSet<IVisualModeHandler>>
    {
        { InputObjectType.BUILDING, new HashSet<IVisualModeHandler>() },
        { InputObjectType.PARCEL, new HashSet<IVisualModeHandler>() },
        { InputObjectType.PATH, new HashSet<IVisualModeHandler>() },
        { InputObjectType.NODE, new HashSet<IVisualModeHandler>() },
        { InputObjectType.TERRAIN, new HashSet<IVisualModeHandler>() },
        { InputObjectType.ZONE, new HashSet<IVisualModeHandler>() }
    };

    // 前回のモード（差分更新用）
    // UI側の初期表示設定との同期を維持するため、前回のモードの初期値も objectTypeModes に合わせる。
    private Dictionary<int, int> previousModes = new Dictionary<int, int>
    {
        { InputObjectType.BUILDING, VisualMode.MODE_NORMAL },
        { InputObjectType.PARCEL, VisualMode.MODE_HIDDEN },
        { InputObjectType.PATH, VisualMode.MODE_NORMAL },
        { InputObjectType.NODE, VisualMode.MODE_NORMAL },
        { InputObjectType.TERRAIN, VisualMode.MODE_NORMAL },
        { InputObjectType.ZONE, VisualMode.MODE_HIDDEN }
    };

    // 処理中のコルーチン管理
    private Dictionary<int, Coroutine> activeUpdateCoroutines = new Dictionary<int, Coroutine>();

    // registeredObjects のキーと同じ種別。構築後にキーは増減しない(RegisterObject は既存キーにだけ追加する)。
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
    }

    private void Start()
    {
        // 定期的な無効オブジェクトクリーンアップを開始
        StartCoroutine(PeriodicCleanup());
    }

    public void RegisterObject(int objectType, IVisualModeHandler visualObject)
    {
        if (registeredObjects.ContainsKey(objectType))
        {
            if (registeredObjects[objectType].Add(visualObject)) // HashSet.Add は重複時false
            {
                // 登録時に現在のモードを即座に適用
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

        // 差分チェック：前回と同じモードなら何もしない
        if (previousModes[objectType] == mode)
        {
            if (enableDebugLog) Debug.Log($"VisualModeHandler.SetMode: {InputObjectType.GetName(objectType)} を {GetModeName(mode)} に変更しようとしましたが、前回と同じモードのためスキップします");
            return;
        }

        // 既存の更新処理を停止
        if (activeUpdateCoroutines.ContainsKey(objectType) && activeUpdateCoroutines[objectType] != null)
        {
            StopCoroutine(activeUpdateCoroutines[objectType]);
        }

        // モードを更新
        objectTypeModes[objectType] = mode;
        previousModes[objectType] = mode;

        // 分散処理でオブジェクトに通知
        activeUpdateCoroutines[objectType] = StartCoroutine(NotifyObjectsBatched(objectType, mode));

        if (enableDebugLog)
        {
            Debug.Log($"VisualModeHandler: {InputObjectType.GetName(objectType)} のモードを {GetModeName(mode)} に変更開始 (対象: {registeredObjects[objectType].Count} オブジェクト)");
        }
    }

    public int GetMode(int objectType)
    {
        return objectTypeModes.ContainsKey(objectType) ? objectTypeModes[objectType] : VisualMode.MODE_NORMAL;
    }

    /// <summary>
    /// 表示フラグ(VisualFlag のビット和)を設定する。
    /// 色モードとは独立した系統で、変更時は現在の色モードのまま全対象オブジェクトへ再通知する。
    /// VisualModeObject 側が ApplyVisualMode 内で GetFlags をプル参照して表示を決めるため、
    /// IVisualModeHandler インターフェースの変更は不要（フラグと無関係な PARCEL/ZONE には影響しない）。
    /// </summary>
    public void SetFlags(int objectType, int flags)
    {
        if (!objectTypeFlags.ContainsKey(objectType)) return;

        // 差分チェック：前回と同じフラグなら何もしない
        if (objectTypeFlags[objectType] == flags) return;

        objectTypeFlags[objectType] = flags;

        // 既存の更新処理を停止し、現在の色モードのまま分散処理で再通知する
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
            
            // 1バッチ分のオブジェクトを処理
            for (int j = i; j < endIndex; j++)
            {
                if (objects[j] != null)
                {
                    objects[j].ApplyVisualMode(mode);
                    processedCount++;
                }
            }

            // 次のフレームまで待機（CPU負荷分散）
            yield return null;
        }

        // 処理完了をログ出力
        if (enableDebugLog)
        {
            Debug.Log($"VisualModeHandler: {InputObjectType.GetName(objectType)} の {processedCount}/{totalCount} オブジェクトのモード変更完了");
        }

        // 完了したコルーチンを辞書から削除
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

            // 登録数万件の Unity null 判定を1フレームに集めないよう分割する。
            // 走査は写しに対して行うので、yield 中の登録・解除で列挙が壊れない。
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
