import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { getInitialLoadingReady, startGameFromTips } from "../../utils/ipc";
import "./TipsWindow.css";

import tip1 from "../../assets/images/tips/tip_1.jpg";
import tip2 from "../../assets/images/tips/tip_2.jpg";
import tip3 from "../../assets/images/tips/tip_3.jpg";
import tip4 from "../../assets/images/tips/tip_4.jpg";
import tip5 from "../../assets/images/tips/tip_5.jpg";
import tip6 from "../../assets/images/tips/tip_6.jpg";
import tip7 from "../../assets/images/tips/tip_7.jpg";
import tip8 from "../../assets/images/tips/tip_8.jpg";
import tip9 from "../../assets/images/tips/tip_9.jpg";
import tip10 from "../../assets/images/tips/tip_10.jpg";
import tip11 from "../../assets/images/tips/tip_11.jpg";
import tip12 from "../../assets/images/tips/tip_12.jpg";
import tip13 from "../../assets/images/tips/tip_13.jpg";
import tip14 from "../../assets/images/tips/tip_14.jpg";
import tip15 from "../../assets/images/tips/tip_15.jpg";
import tip16 from "../../assets/images/tips/tip_16.jpg";
import tip17 from "../../assets/images/tips/tip_17.jpg";
import tip18 from "../../assets/images/tips/tip_18.jpg";
import tip19 from "../../assets/images/tips/tip_19.jpg";
import tip20 from "../../assets/images/tips/tip_20.jpg";
import tip21 from "../../assets/images/tips/tip_21.jpg";
import tip22 from "../../assets/images/tips/tip_22.jpg";
import tip23 from "../../assets/images/tips/tip_23.jpg";
import tip24 from "../../assets/images/tips/tip_24.jpg";
import tip25 from "../../assets/images/tips/tip_25.jpg";
import tip26 from "../../assets/images/tips/tip_26.jpg";
import tip27 from "../../assets/images/tips/tip_27.jpg";
import tip28 from "../../assets/images/tips/tip_28.jpg";
import tip29 from "../../assets/images/tips/tip_29.jpg";
import tip30 from "../../assets/images/tips/tip_30.jpg";
import tip31 from "../../assets/images/tips/tip_31.jpg";
import tip32 from "../../assets/images/tips/tip_32.jpg";
import tip33 from "../../assets/images/tips/tip_33.jpg";
import tip34 from "../../assets/images/tips/tip_34.jpg";
import reloadIcon from "../../assets/images/menu/tray_reload.png";

type TipItem = {
  id: string;
  image?: string;
};

const TIPS_LIST: TipItem[] = [
  { id: "tip1", image: tip1 },
  { id: "tip2", image: tip2 },
  { id: "tip3", image: tip3 },
  { id: "tip4", image: tip4 },
  { id: "tip5", image: tip5 },
  { id: "tip6", image: tip6 },
  { id: "tip7", image: tip7 },
  { id: "tip8", image: tip8 },
  { id: "tip9", image: tip9 },
  { id: "tip10", image: tip10 },
  { id: "tip11", image: tip11 },
  { id: "tip12", image: tip12 },
  { id: "tip13", image: tip13 },
  { id: "tip14", image: tip14 },
  { id: "tip15", image: tip15 },
  { id: "tip16", image: tip16 },
  { id: "tip17", image: tip17 },
  { id: "tip18", image: tip18 },
  { id: "tip19", image: tip19 },
  { id: "tip20", image: tip20 },
  { id: "tip21", image: tip21 },
  { id: "tip22", image: tip22 },
  { id: "tip23", image: tip23 },
  { id: "tip24", image: tip24 },
  { id: "tip25", image: tip25 },
  { id: "tip26", image: tip26 },
  { id: "tip27", image: tip27 },
  { id: "tip28", image: tip28 },
  { id: "tip29", image: tip29 },
  { id: "tip30", image: tip30 },
  { id: "tip31", image: tip31 },
  { id: "tip32", image: tip32 },
  { id: "tip33", image: tip33 },
  { id: "tip34", image: tip34 },
];

export function TipsWindow() {
  const { t } = useTranslation();
  const [currentIndex, setCurrentIndex] = useState(() => Math.floor(Math.random() * TIPS_LIST.length));
  const [ready, setReady] = useState(false);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState(false);

  const handleReload = () => {
    if (TIPS_LIST.length <= 1) return;
    setCurrentIndex((prev) => {
      let next: number;
      do {
        next = Math.floor(Math.random() * TIPS_LIST.length);
      } while (next === prev);
      return next;
    });
  };

  useEffect(() => {
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    const refresh = async () => {
      try {
        const value = await getInitialLoadingReady();
        if (!disposed) setReady(value);
      } catch (e) {
        console.error("Failed to read loading state:", e);
      } finally {
        if (!disposed) timer = setTimeout(refresh, 300);
      }
    };
    void refresh();
    return () => { disposed = true; clearTimeout(timer); };
  }, []);

  const handleStart = async () => {
    if (!ready || starting) return;
    setStarting(true);
    setError(false);
    try {
      await startGameFromTips();
    } catch (e) {
      console.error("Failed to start game:", e);
      setError(true);
      setStarting(false);
    }
  };

  const currentTip = TIPS_LIST[currentIndex];

  return (
    <div className="tips-window" data-tauri-drag-region>
      <div className="tips-header" data-tauri-drag-region>
        <span className="tips-fixed-label" data-tauri-drag-region>
          {t("tips.todaysTips")}
        </span>
      </div>

      <div className="tips-content" data-tauri-drag-region>
        {currentTip.image && (
          <img
            className="tips-image"
            src={currentTip.image}
            alt={currentTip.id}
            draggable={false}
          />
        )}
        <div className="tips-text-container" data-tauri-drag-region>
          <h2 className="tips-title" data-tauri-drag-region>
            {t(`tips.items.${currentTip.id}.title`)}
          </h2>
          <p className="tips-description" data-tauri-drag-region>
            {t(`tips.items.${currentTip.id}.description`)}
          </p>
        </div>
      </div>

      <div className="tips-footer">
        <button
          type="button"
          className="tips-reload-btn"
          onClick={handleReload}
          title="別のTipsを表示"
        >
          <img src={reloadIcon} alt="Reload" className="tips-reload-icon" draggable={false} />
        </button>
        <div className="tips-footer-right">
          {error && <span role="alert">{t("tips.startError")}</span>}
          {ready ? (
            <button className="tips-btn tips-btn--primary" onClick={handleStart} disabled={starting} type="button">
              {t("tips.startGame")}
            </button>
          ) : (
            <span className="tips-loading" role="status" aria-live="polite">
              <span className="tips-loading-spinner" aria-hidden="true" />
              <span className="tips-loading-text">{t("tips.nowLoading")}</span>
            </span>
          )}
        </div>
      </div>
    </div>
  );
}
