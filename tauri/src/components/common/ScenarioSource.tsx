import { useTranslation } from "react-i18next";
import type { CommonSaveEntry, ScenarioEntry } from "../../utils/saves";
import "./ScenarioSource.css";

/** Scenario source badge and official signature. Terrains get no source badge: New Game lists them in their own tab. */
export function ScenarioOriginBadges({ entry }: { entry: ScenarioEntry }) {
  const { t } = useTranslation();
  const origin = entry.tag === "terrain" ? undefined : entry.origin;
  if (!origin && !entry.official) return null;
  return (
    <span className="scenario-badges">
      {entry.official && <span className="scenario-badge scenario-badge--official">{t("scenarios.official")}</span>}
      {origin && <span className={`scenario-badge scenario-badge--${origin}`}>{t(`scenarios.origin.${origin}`)}</span>}
    </span>
  );
}

/**
 * Packs recorded as required by the city and their ownership status. Core compatibility is
 * authoritative for loadability; this display only identifies missing packs by name.
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
