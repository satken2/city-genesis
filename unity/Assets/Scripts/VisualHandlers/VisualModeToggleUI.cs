using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 画面左上に配置し、4種類のオブジェクト表示モードを循環切り替えする簡易ボタン群。
/// 各ボタンの画像はUnityエディタから依存性注入（SerializeField）で設定する。
/// 押下時に <see cref="VisualModeHandler"/> のモードを変更し、ボタン画像を即座に更新して現在状態を可視化する。
/// </summary>
public class VisualModeToggleUI : MonoBehaviour
{
    [Header("モードハンドラ（未設定時は Singleton.Instance を使用）")]
    [SerializeField] private VisualModeHandler visualModeHandler;

    [Header("各種ボタン（uGUI の Button をアタッチ）")]
    [SerializeField] private Button zoneButton;
    [SerializeField] private Button parcelButton;
    [SerializeField] private Button buildingButton;
    [SerializeField] private Button pathButton;

    [Header("地区ボタン画像")]
    [Tooltip("地区が表示状態のときに使うアイコン")]
    [SerializeField] private Sprite zoneVisibleSprite;
    [Tooltip("地区が非表示状態のときに使うアイコン")]
    [SerializeField] private Sprite zoneHiddenSprite;

    [Header("区画ボタン画像")]
    [Tooltip("区画が表示（境界表示）状態のときに使うアイコン")]
    [SerializeField] private Sprite parcelVisibleSprite;
    [Tooltip("区画が非表示状態のときに使うアイコン")]
    [SerializeField] private Sprite parcelHiddenSprite;

    [Header("建物ボタン画像")]
    [Tooltip("建物が通常表示状態のときに使うアイコン")]
    [SerializeField] private Sprite buildingVisibleSprite;
    [Tooltip("建物が計画表示状態のときに使うアイコン")]
    [SerializeField] private Sprite buildingPlanSprite;
    [Tooltip("建物が非表示状態のときに使うアイコン")]
    [SerializeField] private Sprite buildingHiddenSprite;

    [Header("道路ボタン画像")]
    [Tooltip("道路が通常表示状態のときに使うアイコン")]
    [SerializeField] private Sprite pathVisibleSprite;
    [Tooltip("道路が計画表示状態のときに使うアイコン")]
    [SerializeField] private Sprite pathPlanSprite;
    [Tooltip("道路が非表示状態のときに使うアイコン")]
    [SerializeField] private Sprite pathHiddenSprite;

    [Header("未開放セルグレーアウトボタン")]
    [SerializeField] private Button terrainUnlockButton;

    [Header("未開放セルグレーアウトボタン画像")]
    [Tooltip("グレーアウトが有効なときに使うアイコン")]
    [SerializeField] private Sprite terrainUnlockActiveSprite;
    [Tooltip("グレーアウトが無効なときに使うアイコン")]
    [SerializeField] private Sprite terrainUnlockInactiveSprite;

    [Header("アラートボタン")]
    [SerializeField] private Button alertButton;

    [Header("アラートボタン画像")]
    [Tooltip("アラートが表示状態のときに使うアイコン")]
    [SerializeField] private Sprite alertVisibleSprite;
    [Tooltip("アラートが非表示状態のときに使うアイコン")]
    [SerializeField] private Sprite alertHiddenSprite;

    private void Awake()
    {
        // インスペクタから注入されていない場合はシングルトンインスタンスをフォールバックとして使用する
        if (visualModeHandler == null)
        {
            visualModeHandler = VisualModeHandler.Instance;
        }
    }

    private void Start()
    {
        // 各ボタンのクリックイベントを登録する
        if (zoneButton != null)
            zoneButton.onClick.AddListener(OnZoneButtonClicked);

        if (parcelButton != null)
            parcelButton.onClick.AddListener(OnParcelButtonClicked);

        if (buildingButton != null)
            buildingButton.onClick.AddListener(OnBuildingButtonClicked);

        if (pathButton != null)
            pathButton.onClick.AddListener(OnPathButtonClicked);

        if (terrainUnlockButton != null)
            terrainUnlockButton.onClick.AddListener(OnTerrainUnlockButtonClicked);

        if (alertButton != null)
            alertButton.onClick.AddListener(OnAlertButtonClicked);

        // 初期状態を VisualModeHandler と同期し、ボタン画像を反映する
        RefreshAllButtonImages();
    }

    private void OnDestroy()
    {
        // イベントリスナーを破棄してメモリリークを防ぐ
        if (zoneButton != null)
            zoneButton.onClick.RemoveListener(OnZoneButtonClicked);

        if (parcelButton != null)
            parcelButton.onClick.RemoveListener(OnParcelButtonClicked);

        if (buildingButton != null)
            buildingButton.onClick.RemoveListener(OnBuildingButtonClicked);

        if (pathButton != null)
            pathButton.onClick.RemoveListener(OnPathButtonClicked);

        if (terrainUnlockButton != null)
            terrainUnlockButton.onClick.RemoveListener(OnTerrainUnlockButtonClicked);

        if (alertButton != null)
            alertButton.onClick.RemoveListener(OnAlertButtonClicked);
    }

    /// <summary>
    /// 地区ボタン押下時の処理。
    /// 非表示 &lt;-&gt; 通常表示 を循環する。
    /// </summary>
    private void OnZoneButtonClicked()
    {
        int current = visualModeHandler.GetMode(InputObjectType.ZONE);
        int next = (current == VisualMode.MODE_HIDDEN)
            ? VisualMode.MODE_NORMAL
            : VisualMode.MODE_HIDDEN;

        visualModeHandler.SetMode(InputObjectType.ZONE, next);
        RefreshButtonImage(zoneButton, InputObjectType.ZONE);
    }

    /// <summary>
    /// 区画ボタン押下時の処理。
    /// 非表示 &lt;-&gt; 境界表示 を循環する。
    /// 区画の「表示」とは境界線を可視化する境界表示を意味する。
    /// </summary>
    private void OnParcelButtonClicked()
    {
        int current = visualModeHandler.GetMode(InputObjectType.PARCEL);
        int next = (current == VisualMode.MODE_BOUNDARY)
            ? VisualMode.MODE_HIDDEN
            : VisualMode.MODE_BOUNDARY;

        visualModeHandler.SetMode(InputObjectType.PARCEL, next);
        RefreshButtonImage(parcelButton, InputObjectType.PARCEL);
    }

    /// <summary>
    /// 建物ボタン押下時の処理。
    /// 「通常+計画中表示 → 通常のみ → 非表示」の3状態を循環する。
    /// </summary>
    private void OnBuildingButtonClicked()
    {
        CyclePlanVisibility(InputObjectType.BUILDING);
        RefreshButtonImage(buildingButton, InputObjectType.BUILDING);
    }

    /// <summary>
    /// 道路ボタン押下時の処理。
    /// 「通常+計画中表示 → 通常のみ → 非表示」の3状態を循環する。
    /// </summary>
    private void OnPathButtonClicked()
    {
        CyclePlanVisibility(InputObjectType.PATH);
        RefreshButtonImage(pathButton, InputObjectType.PATH);
    }

    /// <summary>
    /// 建物/道路ボタン共通の循環処理。
    /// 計画中の表示は色モードではなく独立フラグ(VisualFlag.SHOW_PLAN)なので、
    /// モードとフラグの両方を組み合わせて3状態を表現する。SHOW_PLAN 以外のフラグは維持する。
    /// </summary>
    private void CyclePlanVisibility(int objectType)
    {
        int mode = visualModeHandler.GetMode(objectType);
        int flags = visualModeHandler.GetFlags(objectType);
        bool showPlan = (flags & VisualFlag.SHOW_PLAN) != 0;

        if (mode != VisualMode.MODE_HIDDEN && showPlan)
        {
            // 通常+計画中 → 通常のみ
            visualModeHandler.SetFlags(objectType, flags & ~VisualFlag.SHOW_PLAN);
            visualModeHandler.SetMode(objectType, VisualMode.MODE_NORMAL);
        }
        else if (mode != VisualMode.MODE_HIDDEN)
        {
            // 通常のみ → 非表示
            visualModeHandler.SetMode(objectType, VisualMode.MODE_HIDDEN);
        }
        else
        {
            // 非表示 → 通常+計画中
            visualModeHandler.SetFlags(objectType, flags | VisualFlag.SHOW_PLAN);
            visualModeHandler.SetMode(objectType, VisualMode.MODE_NORMAL);
        }
    }

    /// <summary>
    /// 未開放セルグレーアウトボタン押下時の処理。
    /// TerrainManager のグレーアウト表示フラグをトグルする。
    /// 同時にセル境界線と座標ラベルの表示も連動して切り替わる。
    /// </summary>
    private void OnTerrainUnlockButtonClicked()
    {
        TerrainManager tm = FindAnyObjectByType<TerrainManager>();
        if (tm != null)
        {
            tm.ToggleUnlockOverlay();
        }
        RefreshTerrainUnlockButtonImage();
    }

    /// <summary>
    /// アラートボタン押下時の処理。
    /// アラートの表示/非表示をトグルする。
    /// </summary>
    private void OnAlertButtonClicked()
    {
        if (AlertManager.Instance != null)
        {
            AlertManager.Instance.ToggleVisibility();
        }
        RefreshAlertButtonImage();
    }

    /// <summary>
    /// 全ボタンの画像を現在の <see cref="VisualModeHandler"/> の状態に合わせて更新する。
    /// </summary>
    private void RefreshAllButtonImages()
    {
        RefreshButtonImage(zoneButton, InputObjectType.ZONE);
        RefreshButtonImage(parcelButton, InputObjectType.PARCEL);
        RefreshButtonImage(buildingButton, InputObjectType.BUILDING);
        RefreshButtonImage(pathButton, InputObjectType.PATH);
        RefreshTerrainUnlockButtonImage();
        RefreshAlertButtonImage();
    }

    /// <summary>
    /// 未開放セルグレーアウトボタンの画像を TerrainManager の現在状態に合わせて更新する。
    /// </summary>
    private void RefreshTerrainUnlockButtonImage()
    {
        if (terrainUnlockButton == null) return;
        if (terrainUnlockButton.targetGraphic is Image image)
        {
            TerrainManager tm = FindAnyObjectByType<TerrainManager>();
            bool isActive = tm != null && tm.IsUnlockOverlayActive;
            image.sprite = isActive ? terrainUnlockActiveSprite : terrainUnlockInactiveSprite;
        }
    }

    /// <summary>
    /// アラートボタンの画像を AlertManager の現在状態に合わせて更新する。
    /// </summary>
    private void RefreshAlertButtonImage()
    {
        if (alertButton == null) return;
        if (alertButton.targetGraphic is Image image)
        {
            bool isActive = AlertManager.Instance != null && AlertManager.Instance.IsVisible;
            image.sprite = isActive ? alertVisibleSprite : alertHiddenSprite;
        }
    }

    /// <summary>
    /// 指定したボタンの画像を、対象オブジェクトタイプの現在の表示モードに応じたスプライトに切り替える。
    /// </summary>
    /// <param name="button">対象の uGUI Button</param>
    /// <param name="objectType"><see cref="InputObjectType"/> に定義されたオブジェクトタイプ</param>
    private void RefreshButtonImage(Button button, int objectType)
    {
        if (button == null) return;

        // Button の targetGraphic は通常 Image コンポーネントである
        if (button.targetGraphic is Image image)
        {
            int mode = visualModeHandler.GetMode(objectType);
            Sprite sprite = ResolveSprite(objectType, mode);
            if (sprite != null)
            {
                image.sprite = sprite;
            }
        }
    }

    /// <summary>
    /// オブジェクトタイプと表示モードに対応するスプライトを解決する。
    /// </summary>
    private Sprite ResolveSprite(int objectType, int mode)
    {
        switch (objectType)
        {
            case InputObjectType.ZONE:
                return mode == VisualMode.MODE_NORMAL ? zoneVisibleSprite : zoneHiddenSprite;

            case InputObjectType.PARCEL:
                return mode == VisualMode.MODE_BOUNDARY ? parcelVisibleSprite : parcelHiddenSprite;

            case InputObjectType.BUILDING:
            {
                if (mode != VisualMode.MODE_NORMAL) return buildingHiddenSprite;
                bool showPlan = (visualModeHandler.GetFlags(objectType) & VisualFlag.SHOW_PLAN) != 0;
                return showPlan ? buildingPlanSprite : buildingVisibleSprite;
            }

            case InputObjectType.PATH:
            {
                if (mode != VisualMode.MODE_NORMAL) return pathHiddenSprite;
                bool showPlan = (visualModeHandler.GetFlags(objectType) & VisualFlag.SHOW_PLAN) != 0;
                return showPlan ? pathPlanSprite : pathVisibleSprite;
            }

            default:
                return null;
        }
    }
}
