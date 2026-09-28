import { useTranslation } from "react-i18next";
import type { SaveEntry } from "../../utils/saves";
import { RequiredPacks } from "./ScenarioSource";
import "./ScenarioSource.css";

/** 保存版・必要パック・読み込めない理由。セーブ一覧・ニューゲーム・シナリオ管理で共用する。 */
export function SaveVersionInfo({ entry }: { entry: SaveEntry }) {
  const { t } = useTranslation();
  return <div className="save-version-info">
    <span title={t("saveCompatibility.history", {
      created: entry.created_core_version ?? t("saveCompatibility.unknown"),
      simulated: entry.last_simulated_core_version ?? t("saveCompatibility.notSimulated"),
    })}>{t("saveCompatibility.savedVersion", { version: entry.saved_core_version ?? t("saveCompatibility.unknown") })}</span>
    <RequiredPacks entry={entry} />
    {entry.compatibility?.loadable === false && <p role="status" className="scenario-error save-version-info-reason">
      {t(`saveCompatibility.${entry.compatibility.code}`, { defaultValue: t("saveCompatibility.SAVE_CORRUPT") })}
    </p>}
  </div>;
}
