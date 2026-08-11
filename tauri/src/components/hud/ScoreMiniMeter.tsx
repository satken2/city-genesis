/**
 * 施政スコアミニメーター（コックピット常駐）
 *
 * 市政の評価(施政スコア)を常時コックピットに表示する小型メーター。
 * クリックで達成度ウィンドウ(スコアの内訳・推移を載せる画面)を開く。
 *
 * スコアの定義自体が見直し中のため、現状は値を持たない中立色の空メーターを描く。
 * 値の取得(ポーリング)と色分けは、定義が固まり次第 BudgetMiniMeter と同じ形で実装する。
 */

import { useTranslation } from "react-i18next";
import { Meter } from "../common/Meter";
import "./hud.css";

/** 値未取得時の塗り色。良し悪しの印象を与えないための中立色。 */
const PLACEHOLDER_COLOR = "#8ba3bb";

export function ScoreMiniMeter({ onOpen }: { onOpen: () => void }) {
  const { t } = useTranslation();

  return (
    <button
      type="button"
      className="hud-mini-meter"
      onClick={onOpen}
      title={t("taskbar.scoreMeterTooltip")}
    >
      <span className="hud-mini-meter__label">{t("taskbar.scoreMeterLabel")}</span>
      <div className="hud-mini-meter__bar">
        <Meter value={0} color={PLACEHOLDER_COLOR} />
      </div>
    </button>
  );
}
