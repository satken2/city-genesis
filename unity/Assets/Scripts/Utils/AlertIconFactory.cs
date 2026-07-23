using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

/// アラートビットごとのアイコンを取得するためのファクトリークラス。
///
/// agent と path でアラートビット値が同じでも
/// 別のアイコンを割り当てられるよう、Dictionary のキーを (entityType, bit) の
/// 組として保持する。BuildingIconFactory / AgentIconFactory / PathIconFactory と
/// 同じく Inspector から名前付き [SerializeField] Sprite を割り当て、Awake で
/// Dictionary を構築するパターンを採用する。
///
/// アラートビットの定義は Constants.ALERT_AGENT_* / ALERT_PATH_* を参照。
/// 新しいアラート種別を追加する場合は、本クラスに対応する [SerializeField] Sprite
/// フィールドを追加し、InitializeAlertIcons() で Dictionary に登録すること。
/// 建設中アイコン(GetIconForStatusConstructing)はアラートビットではなく
/// status == STATUS_CONSTRUCTING の場合に利用される。
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
    [Tooltip("ALERT_AGENT_ON_FIRE ビット用アイコン。火災発生中の建物に表示。")]
    [SerializeField] private Sprite agentOnFireIcon;
    [Tooltip("ALERT_AGENT_ACCIDENTAL_FIRE ビット用アイコン。失火（小火）の建物に表示。")]
    [SerializeField] private Sprite agentAccidentalFireIcon;
    [Tooltip("ALERT_AGENT_INFRA_DISCONNECTED ビット用アイコン。インフラ網に未接続の稼働中インフラ施設(発電所・変電所など)に表示。")]
    [SerializeField] private Sprite agentInfraDisconnectedIcon;

    [Header("Path Alert Icons (by bit)")]
    [Tooltip("ALERT_PATH_CONSTRUCTION_SHORTAGE ビット用アイコン。建設サービス財の調達不足に表示。")]
    [SerializeField] private Sprite pathConstructionShortageIcon;
    [Tooltip("ALERT_PATH_ACCIDENT ビット用アイコン。道路上で事故が発生している場合に表示。")]
    [SerializeField] private Sprite pathAccidentIcon;

    // (entityType, bit) → Sprite。entityType は "agent" または "path"。
    private Dictionary<(string, int), Sprite> _alertIcons;

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

        try
        {
            _alertIcons[("agent", Constants.ALERT_AGENT_CONSTRUCTION_SHORTAGE)] = agentConstructionShortageIcon;
            _alertIcons[("agent", Constants.ALERT_AGENT_ON_FIRE)]               = agentOnFireIcon;
            _alertIcons[("agent", Constants.ALERT_AGENT_ACCIDENTAL_FIRE)]       = agentAccidentalFireIcon != null ? agentAccidentalFireIcon : agentOnFireIcon;
            _alertIcons[("agent", Constants.ALERT_AGENT_INFRA_DISCONNECTED)]    = agentInfraDisconnectedIcon;
            _alertIcons[("path",  Constants.ALERT_PATH_CONSTRUCTION_SHORTAGE)] = pathConstructionShortageIcon;
            _alertIcons[("path",  Constants.ALERT_PATH_ACCIDENT)]              = pathAccidentIcon;
        }
        catch (Exception e)
        {
            Debug.LogError($"AlertIconFactory: アイコンの登録に失敗しました: {e.Message}");
        }
    }

    /// <summary>
    /// status == STATUS_COLLAPSED の agent/path 用の倒壊アイコンを取得する。
    /// アラートビットではなく status 値に基づく表示判定のため、別メソッドで提供する。
    /// </summary>
    public Sprite GetIconForStatusCollapsed(string entityType)
    {
        return entityType == "agent" ? agentCollapsedStatusIcon : pathCollapsedStatusIcon;
    }

    /// <summary>
    /// agent のアラートビットに対応するアイコンを取得する。
    /// </summary>
    /// <param name="bit">Constants.ALERT_AGENT_* のいずれか</param>
    /// <returns>対応するSprite。未設定時はnull</returns>
    public Sprite GetIconForAgent(int bit)
    {
        return GetIcon("agent", bit);
    }

    /// <summary>
    /// path のアラートビットに対応するアイコンを取得する。
    /// </summary>
    /// <param name="bit">Constants.ALERT_PATH_* のいずれか</param>
    /// <returns>対応するSprite。未設定時はnull</returns>
    public Sprite GetIconForPath(int bit)
    {
        return GetIcon("path", bit);
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
