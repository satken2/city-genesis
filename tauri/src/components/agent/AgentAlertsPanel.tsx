/**
 * 経済主体のアラートカード一覧
 *
 * agentのalertと、建物・所属区画に付いた空間警告を読み、立っているビットごとに
 * 「アイコン + タイトル + 説明文」のカードを縦に並べて表示する共通コンポーネント。
 * 経済主体詳細パネル(AgentDetailPanel)の上部に置かれる。
 *
 * - agentのカードはALERT_CARDS、空間警告はWARNING_CARDSに定義し、
 *   文言にはi18nキー agent.alerts.<key> を使う
 * - Unity のビルボードアイコン(ALERT_AGENT_ICON_BITS)とは対象が独立しており、
 *   ここでは詳細パネル専用の生産ボトルネック系ビットも含めた全ビットを表示する
 * - 空間警告はUnityと同じAPIの判定結果を使い、alertとは別のビット領域で扱う
 * - 立っているビットが1つも無ければ何も描画しない
 */

import { useTranslation } from "react-i18next";
import {
  ALERT_AGENT_CONSTRUCTION_SHORTAGE,
  ALERT_AGENT_ON_FIRE,
  ALERT_AGENT_ACCIDENTAL_FIRE,
  ALERT_AGENT_CRIME,
  ALERT_AGENT_CRIME_SIGN,
  ALERT_AGENT_INFRA_DISCONNECTED,
  ALERT_AGENT_LABOR_SHORTAGE,
  ALERT_AGENT_MATERIAL_SHORTAGE,
  ALERT_AGENT_HIGH_SKILL_SHORTAGE,
  ALERT_AGENT_BUDGET_CAPPED,
  ALERT_DISPLAY_FACILITY_WARNING,
  ALERT_DISPLAY_SANITATION_WARNING,
  ALERT_DISPLAY_INFRA_WARNING,
} from "../../constants";
import shortageIcon from "../../assets/images/ui/agent_panel/bribery.png";
import fireIcon from "../../assets/images/ui/agent_panel/fire.png";
import facilityWarningIcon from "../../assets/images/alerts/bribery.png";
import sanitationWarningIcon from "../../assets/images/alerts/sanitation.png";
import infraWarningIcon from "../../assets/images/alerts/electricity.png";
import "./AgentDetailPanel.css";

type AlertCardDef = {
  bit: number;
  icon: string;
  /** i18n キーの末尾。agent.alerts.<key>.title / .description を参照する */
  key: string;
};

// ビット→カード定義の対応表。表示順はこの配列の順序に従う。
// 専用アイコン未制作のビットは、当面は不足系アイコン(bribery)を流用する。
const ALERT_CARDS: AlertCardDef[] = [
  { bit: ALERT_AGENT_CONSTRUCTION_SHORTAGE, icon: shortageIcon, key: "constructionShortage" },
  { bit: ALERT_AGENT_ON_FIRE, icon: fireIcon, key: "onFire" },
  { bit: ALERT_AGENT_ACCIDENTAL_FIRE, icon: fireIcon, key: "accidentalFire" },
  { bit: ALERT_AGENT_CRIME, icon: shortageIcon, key: "crime" },
  { bit: ALERT_AGENT_CRIME_SIGN, icon: shortageIcon, key: "crimeSign" },
  { bit: ALERT_AGENT_INFRA_DISCONNECTED, icon: shortageIcon, key: "infraDisconnected" },
  { bit: ALERT_AGENT_LABOR_SHORTAGE, icon: shortageIcon, key: "laborShortage" },
  { bit: ALERT_AGENT_MATERIAL_SHORTAGE, icon: shortageIcon, key: "materialShortage" },
  { bit: ALERT_AGENT_HIGH_SKILL_SHORTAGE, icon: shortageIcon, key: "highSkillShortage" },
  { bit: ALERT_AGENT_BUDGET_CAPPED, icon: shortageIcon, key: "budgetCapped" },
];

const WARNING_CARDS: AlertCardDef[] = [
  { bit: ALERT_DISPLAY_FACILITY_WARNING, icon: facilityWarningIcon, key: "facilityWarning" },
  { bit: ALERT_DISPLAY_SANITATION_WARNING, icon: sanitationWarningIcon, key: "sanitationWarning" },
  { bit: ALERT_DISPLAY_INFRA_WARNING, icon: infraWarningIcon, key: "infraWarning" },
];

type AgentAlertsPanelProps = {
  /** agent の alert ビット和。未設定(undefined/null)は 0 として扱う */
  alert: number | undefined | null;
  /** 建物自身と所属区画の空間警告。3D側の表示フィルターには依存しない。 */
  warning?: number | null;
};

export function AgentAlertsPanel({ alert, warning }: AgentAlertsPanelProps) {
  const { t } = useTranslation();
  const bits = alert ?? 0;
  const activeCards = [
    ...ALERT_CARDS.filter((card) => (bits & card.bit) !== 0),
    ...WARNING_CARDS.filter((card) => ((warning ?? 0) & card.bit) !== 0),
  ];
  if (activeCards.length === 0) return null;

  return (
    <div className="agent-detail-alerts">
      {activeCards.map((card) => {
        const body = (
          <>
            <div className="agent-detail-alert-title">
              <img src={card.icon} alt="" className="agent-detail-alert-icon" />
              {t(`agent.alerts.${card.key}.title`)}
            </div>
            <div className="agent-detail-alert-desc">
              {t(`agent.alerts.${card.key}.description`)}
            </div>
          </>
        );
        return (
          <div className="agent-detail-alert-card" key={card.key}>
            {body}
          </div>
        );
      })}
    </div>
  );
}
