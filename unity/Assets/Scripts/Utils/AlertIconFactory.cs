using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

/// Factory for retrieving icons by alert bit.
///
/// Use (entityType, bit) tuples as Dictionary keys so agent and path alerts
/// can use different icons even when their bit values match.
/// As with BuildingIconFactory / AgentIconFactory / PathIconFactory,
/// assign named [SerializeField] Sprites in the Inspector and build
/// the Dictionary in Awake.
///
/// See Constants.ALERT_AGENT_* / ALERT_PATH_* for alert-bit definitions.
/// To add an alert type, add its [SerializeField] Sprite field here
/// and register it in the Dictionary in InitializeAlertIcons().
/// The construction icon (GetIconForStatusConstructing) is not an alert bit;
/// it is used when status == STATUS_CONSTRUCTING.
///
/// ALERT_AGENT_ON_FIRE has no icon.
/// FireEffectManager / FireEffectFactory render it as 3D particles, so do not register it here.
/// </summary>
public class AlertIconFactory : MonoBehaviour
{
    public static AlertIconFactory Instance { get; private set; }

    [Header("Status Icons (by STATUS_*)")]
    [Tooltip("建設中アイコン。agent / path の status == STATUS_CONSTRUCTING 時に表示。")]
    [FormerlySerializedAs("agentUnderConstructionIcon")]
    [SerializeField] private Sprite agentConstructingStatusIcon;
    [Tooltip("建設中アイコン。path の status == STATUS_CONSTRUCTING 時に表示。")]
    [FormerlySerializedAs("pathUnderConstructionIcon")]
    [SerializeField] private Sprite pathConstructingStatusIcon;
    [Tooltip("倒壊アイコン。agent / path の status == STATUS_COLLAPSED 時に表示。")]
    [SerializeField] private Sprite agentCollapsedStatusIcon;
    [Tooltip("倒壊アイコン。path の status == STATUS_COLLAPSED 時に表示。")]
    [SerializeField] private Sprite pathCollapsedStatusIcon;

    [Header("Agent Alert Icons (by bit)")]
    [Tooltip("ALERT_AGENT_CONSTRUCTION_SHORTAGE ビット用アイコン。建設サービス財の調達不足に表示。")]
    [SerializeField] private Sprite agentConstructionShortageIcon;
    [Tooltip("ALERT_AGENT_CRIME ビット用アイコン。犯罪が発生した建物に表示。")]
    [SerializeField] private Sprite agentCrimeIcon;
    [Tooltip("ALERT_AGENT_INFRA_DISCONNECTED ビット用アイコン。インフラ網に未接続の稼働中インフラ施設(発電所・変電所など)に表示。")]
    [SerializeField] private Sprite agentInfraDisconnectedIcon;

    [Header("Path Alert Icons (by bit)")]
    [Tooltip("ALERT_PATH_CONSTRUCTION_SHORTAGE ビット用アイコン。建設サービス財の調達不足に表示。")]
    [SerializeField] private Sprite pathConstructionShortageIcon;
    [Tooltip("ALERT_PATH_ACCIDENT ビット用アイコン。道路上で事故が発生している場合に表示。")]
    [SerializeField] private Sprite pathAccidentIcon;
    [Tooltip("ALERT_PATH_NO_STATION_HOUSE ビット用アイコン。どの駅舎パスにも属していない駅ホームに表示。")]
    [SerializeField] private Sprite pathNoStationHouseIcon;

    [Header("Spatial Warning Icons (L1)")]
    [SerializeField] private Sprite resourceWarningIcon;
    [SerializeField] private Sprite facilityWarningIcon;
    [SerializeField] private Sprite sanitationWarningIcon;
    [SerializeField] private Sprite infraWarningIcon;

    // (entityType, bit) -> Sprite. entityType is "agent" or "path".
    private Dictionary<(string, int), Sprite> _alertIcons;
    private Dictionary<int, Sprite> _warningIcons;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        InitializeAlertIcons();
    }

    private void InitializeAlertIcons()
    {
        _alertIcons = new Dictionary<(string, int), Sprite>();
        _warningIcons = new Dictionary<int, Sprite>
        {
            [Constants.ALERT_DISPLAY_RESOURCE_WARNING] = resourceWarningIcon,
            [Constants.ALERT_DISPLAY_FACILITY_WARNING] = facilityWarningIcon,
            [Constants.ALERT_DISPLAY_SANITATION_WARNING] = sanitationWarningIcon,
            [Constants.ALERT_DISPLAY_INFRA_WARNING] = infraWarningIcon,
        };

        try
        {
            _alertIcons[("agent", Constants.ALERT_AGENT_CONSTRUCTION_SHORTAGE)] = agentConstructionShortageIcon;
            _alertIcons[("agent", Constants.ALERT_AGENT_CRIME)]                 = agentCrimeIcon;
            _alertIcons[("agent", Constants.ALERT_AGENT_INFRA_DISCONNECTED)]    = agentInfraDisconnectedIcon;
            _alertIcons[("path",  Constants.ALERT_PATH_CONSTRUCTION_SHORTAGE)] = pathConstructionShortageIcon;
            _alertIcons[("path",  Constants.ALERT_PATH_ACCIDENT)]              = pathAccidentIcon;
            _alertIcons[("path",  Constants.ALERT_PATH_NO_STATION_HOUSE)]      = pathNoStationHouseIcon;
        }
        catch (Exception e)
        {
            Debug.LogError($"AlertIconFactory: アイコンの登録に失敗しました: {e.Message}");
        }
    }

    /// <summary>
    /// Get the collapse icon for an agent/path with status == STATUS_COLLAPSED.
    /// Provided separately because visibility depends on status rather than alert bits.
    /// </summary>
    public Sprite GetIconForStatusCollapsed(string entityType)
    {
        return entityType == "agent" ? agentCollapsedStatusIcon : pathCollapsedStatusIcon;
    }

    /// <summary>
    /// Get the icon for an agent alert bit.
    /// </summary>
    /// <param name="bit">One of Constants.ALERT_AGENT_*.</param>
    /// <returns>The corresponding Sprite, or null if unassigned.</returns>
    public Sprite GetIconForAgent(int bit)
    {
        return GetIcon("agent", bit);
    }

    /// <summary>
    /// Get the icon for a path alert bit.
    /// </summary>
    /// <param name="bit">One of Constants.ALERT_PATH_*.</param>
    /// <returns>The corresponding Sprite, or null if unassigned.</returns>
    public Sprite GetIconForPath(int bit)
    {
        return GetIcon("path", bit);
    }

    public Sprite GetIconForWarning(int bit)
    {
        return _warningIcons != null && _warningIcons.TryGetValue(bit, out var sprite) ? sprite : null;
    }

    private Sprite GetIcon(string entityType, int bit)
    {
        if (_alertIcons != null && _alertIcons.TryGetValue((entityType, bit), out var sprite))
        {
            return sprite;
        }
        return null;
    }
}
