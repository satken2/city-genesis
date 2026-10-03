import { useTranslation } from "react-i18next";
import type { CommonSaveEntry, ScenarioEntry } from "../../utils/saves";
import "./ScenarioSource.css";

/** シナリオの保存元(自作/コミュニティ/DLC)と公式署名の札。 */
export function ScenarioOriginBadges({ entry }: { entry: ScenarioEntry }) {
  const { t } = useTranslation();
  const origin = entry.origin;
  if (!origin && !entry.official) return null;
  return (
    <span className="scenario-badges">
      {entry.official && <span className="scenario-badge scenario-badge--official">{t("scenarios.official")}</span>}
      {origin && <span className={`scenario-badge scenario-badge--${origin}`}>{t(`scenarios.origin.${origin}`)}</span>}
    </span>
  );
}

/**
 * 都市に刻まれた必要パックと所有状況。読み込めるかの最終判定はコアの compatibility が正で、
 * ここは未所有のパックを名指しするための表示だけを担う。
 */
export function RequiredPacks({ entry }: { entry: CommonSaveEntry }) {
  const { t } = useTranslation();
  if (!entry.required_packs?.length) return null;
  return (
    <span className="required-packs">
      <span className="required-packs-label">{t("saveCompatibility.requiredPacks")}</span>
      {entry.required_packs.map(pack => {
        const status = entry.pack_status?.[pack];
        return (
          <span key={pack} className={`required-pack${status ? ` required-pack--${status}` : ""}`}>
            {status ? t("saveCompatibility.packWithStatus", { pack, status: t(`saveCompatibility.packStatus.${status}`) }) : pack}
          </span>
        );
      })}
    </span>
  );
}
