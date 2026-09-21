import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { getInitialLoadingReady, startGameFromTips } from "../../utils/ipc";
import "./TipsWindow.css";

import tip1 from "../../assets/images/tips/tip_1.jpg";
import tip2 from "../../assets/images/tips/tip_2.jpg";
import tip3 from "../../assets/images/tips/tip_3.jpg";
import tip4 from "../../assets/images/tips/tip_4.jpg";
import tip5 from "../../assets/images/tips/tip_5.jpg";
import tip7 from "../../assets/images/tips/tip_7.jpg";
import tip10 from "../../assets/images/tips/tip_10.jpg";
import tip11 from "../../assets/images/tips/tip_11.jpg";
import tip12 from "../../assets/images/tips/tip_12.jpg";
import tip14 from "../../assets/images/tips/tip_14.jpg";
import tip15 from "../../assets/images/tips/tip_15.jpg";

type TipItem = {
  id: string;
  image: string;
};

const TIPS_LIST: TipItem[] = [
  { id: "tip1", image: tip1 },
  { id: "tip2", image: tip2 },
  { id: "tip3", image: tip3 },
  { id: "tip4", image: tip4 },
  { id: "tip5", image: tip5 },
  { id: "tip6", image: tip1 },
  { id: "tip7", image: tip7 },
  { id: "tip8", image: tip3 },
  { id: "tip9", image: tip4 },
  { id: "tip10", image: tip10 },
  { id: "tip11", image: tip11 },
  { id: "tip12", image: tip12 },
  { id: "tip13", image: tip3 },
  { id: "tip14", image: tip14 },
  { id: "tip15", image: tip15 },
];

export function TipsWindow() {
  const { t } = useTranslation();
  const [currentIndex] = useState(() => Math.floor(Math.random() * TIPS_LIST.length));
  const [ready, setReady] = useState(false);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState(false);

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
  );
}
