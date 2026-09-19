/**
 * 経済主体のアラートカード一覧
 *
 * agent の alert ビットフラグ(ALERT_AGENT_*)を読み、立っているビットごとに
 * 「アイコン + タイトル + 説明文」のカードを縦に並べて表示する共通コンポーネント。
 * 経済主体詳細パネル(AgentDetailPanel)の上部に置かれる。
 *
 * - カードの定義は ALERT_CARDS のテーブル1つに集約されており、新しいアラート
 *   ビットを追加するときはここに1行足して i18n キー agent.alerts.<key> を用意する
 * - Unity のビルボードアイコン(ALERT_AGENT_ICON_BITS)とは対象が独立しており、
 *   ここでは詳細パネル専用の生産ボトルネック系ビットも含めた全ビットを表示する
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
} from "../../constants";
import shortageIcon from "../../assets/images/ui/agent_panel/bribery.png";
import fireIcon from "../../assets/images/ui/agent_panel/fire.png";
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

type AgentAlertsPanelProps = {
  /** agent の alert ビット和。未設定(undefined/null)は 0 として扱う */
  alert: number | undefined | null;
  /**
   * カードをクリックしたときに診断ウィンドウを開くハンドラ。
   * 指定するとカードがボタンになり、原因を掘り下げる診断(window_type="diagnosis")へ誘導する。
   */
  onDiagnose?: () => void;
};

export function AgentAlertsPanel({ alert, onDiagnose }: AgentAlertsPanelProps) {
  const { t } = useTranslation();
  const bits = alert ?? 0;
  const activeCards = ALERT_CARDS.filter((card) => (bits & card.bit) !== 0);
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
        if (!onDiagnose) {
          return (
            <div className="agent-detail-alert-card" key={card.key}>
              {body}
            </div>
          );
        }
        return (
          <button
            className="agent-detail-alert-card agent-detail-alert-card--clickable"
            key={card.key}
            type="button"
            title={t("agent.alerts.diagnoseHint")}
            onClick={onDiagnose}
          >
            {body}
            <div className="agent-detail-alert-hint">{t("agent.alerts.diagnoseHint")}</div>
          </button>
        );
      })}
    </div>
  );
}
