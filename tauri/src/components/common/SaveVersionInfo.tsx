import { useTranslation } from "react-i18next";
import type { SaveEntry } from "../../utils/saves";

export function SaveVersionInfo({ entry }: { entry: SaveEntry }) {
  const { t } = useTranslation();
  return <div className="save-version-info">
    <span title={t("saveCompatibility.history", {
      created: entry.created_core_version ?? t("saveCompatibility.unknown"),
      simulated: entry.last_simulated_core_version ?? t("saveCompatibility.notSimulated"),
    })}>{t("saveCompatibility.savedVersion", { version: entry.saved_core_version ?? t("saveCompatibility.unknown") })}</span>
    {entry.compatibility?.loadable === false && <p role="status" className="scenario-error">
      {t(`saveCompatibility.${entry.compatibility.code}`, { defaultValue: t("saveCompatibility.SAVE_CORRUPT") })}
    </p>}
  </div>;
}
