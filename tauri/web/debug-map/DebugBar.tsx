/**
 * 開発用の地図ページの上部バー(コックピットの代わりに画面上端へ置く常駐パネル)
 *
 * 都市の日付と、エポックの実行数の入力・実行ボタンを1行に並べる。実行の流れはエポック実行確認ウィンドウと
 * 同じ executeEpochs で、確認の段だけを省く(何度も回して都市の推移を見る用途のため)。
 * 実行数の上限も確認ウィンドウと同じで、通常プレイは MAX_EPOCH_COUNT、テンプレートモードは TEMPLATE_MAX_EPOCH_COUNT。
 */
import { PLAY_MODE_SCEARIO } from "../../src/constants";

import { useState } from "react";
import { useTranslation } from "react-i18next";
import { executeEpochs, MAX_EPOCH_COUNT, TEMPLATE_MAX_EPOCH_COUNT } from "../../src/utils/epochExecution";
import { formatEpochDate } from "../../src/utils/date";
import { resolveIntlLocale } from "../../src/utils/locale";
import { iconUrl } from "../../src/components/hud/hudCommon";
import { useHudSession } from "../../src/components/hud/useHudSession";
import "../../src/components/hud/hud.css";
import "../../src/components/hud/Cockpit.css";
import "./DebugBar.css";

export function DebugBar() {
  const { t, i18n } = useTranslation();
  const { visible, apiBaseUrl, playMode, isLocked, currentDate, epochExecuting } = useHudSession();
  const maxCount = playMode === PLAY_MODE_SCEARIO ? TEMPLATE_MAX_EPOCH_COUNT : MAX_EPOCH_COUNT;
  const [countText, setCountText] = useState("1");
  const [running, setRunning] = useState(false);
  const count = Math.max(1, Math.min(maxCount, Number.parseInt(countText, 10) || 1));

  async function run() {
    if (running || epochExecuting) return;
    setRunning(true);
    try {
      await executeEpochs({ apiBaseUrl, count, t, language: i18n.language });
    } finally {
      setRunning(false);
    }
  }

  if (!visible) return null;

  const formattedDate = currentDate != null ? formatEpochDate(currentDate, resolveIntlLocale(i18n.language)) : "—";

  return (
    <div className={`hud-panel debug-bar ${epochExecuting ? "hud-disabled" : ""}`}>
      <span className="debug-bar__date">{formattedDate}</span>
      <input
        className="debug-bar__count"
        type="number"
        inputMode="numeric"
        min={1}
        max={maxCount}
        value={countText}
        aria-label={t("epochConfirm.epochCount")}
        title={t("epochConfirm.epochCount")}
        disabled={running}
        onChange={(event) => setCountText(event.target.value)}
        onBlur={() => setCountText(String(count))}
      />
      <button
        className="cockpit-execute debug-bar__execute"
        type="button"
        title={t("taskbar.menuExecute")}
        disabled={isLocked || running}
        onClick={() => void run()}
      >
        {t("taskbar.menuExecute")}
        <img src={iconUrl("menu_execute.png")} alt="" className="cockpit-execute-img" draggable={false} />
      </button>
    </div>
  );
}
