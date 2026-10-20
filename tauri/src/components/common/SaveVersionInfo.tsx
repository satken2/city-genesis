import { useTranslation } from "react-i18next";
import type { CommonSaveEntry } from "../../utils/saves";
import { RequiredPacks } from "./ScenarioSource";
import "./ScenarioSource.css";

/**
 * Save format version, required packs, and reasons loading is unavailable. Shared by save lists,
 * new game, scenario management, and introduction information.
 *
 * Show only the saving core's major version as "Format vN". Only major-version equality determines
 * loadability; minor/patch differences do not affect it. Keep full versions in tooltip history
 * for checking balance changes.
 */
export function SaveVersionInfo({ entry }: { entry: CommonSaveEntry }) {
  const { t } = useTranslation();
  return <div className="save-version-info">
    <span title={t("saveCompatibility.history", {
      created: entry.created_core_version ?? t("saveCompatibility.unknown"),
      simulated: entry.last_simulated_core_version ?? t("saveCompatibility.notSimulated"),
    })}>{t("saveCompatibility.savedVersion", { version: entry.saved_core_version?.split(".")[0] ?? t("saveCompatibility.unknown") })}</span>
    <RequiredPacks entry={entry} />
    {entry.compatibility?.loadable === false && <p role="status" className="scenario-error save-version-info-reason">
      {t(`saveCompatibility.${entry.compatibility.code}`, { defaultValue: t("saveCompatibility.SAVE_CORRUPT") })}
    </p>}
  </div>;
}
