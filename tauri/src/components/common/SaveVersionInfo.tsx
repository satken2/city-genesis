import { useTranslation } from "react-i18next";
import type { CommonSaveEntry } from "../../utils/saves";
import { RequiredPacks } from "./ScenarioSource";
import "./ScenarioSource.css";

/**
 * 保存形式の版・必要パック・読み込めない理由。セーブ一覧・ニューゲーム・シナリオ管理・紹介情報で共用する。
 *
 * 版は保存したコアのメジャー番号だけを「フォーマット vN」と出す。コアが読み込めるかを決めるのはメジャー番号の一致だけで、
 * マイナー・パッチの違いは読み込みに影響しないため(バランス変更の確認に使う完全な版はツールチップの履歴に残す)。
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
