import { useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
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
  console.log("[TipsWindow] 1: render start");
  const { t } = useTranslation();
  console.log("[TipsWindow] 2: useTranslation ok");
  const [currentIndex] = useState(() => {
    const idx = Math.floor(Math.random() * TIPS_LIST.length);
    console.log("[TipsWindow] 3: useState init, idx=", idx);
    return idx;
  });
  console.log("[TipsWindow] 4: after useState, currentIndex=", currentIndex);

  const handleClose = async () => {
    console.log("[TipsWindow] handleClose called");
    try {
      await invoke("close_tips_window");
    } catch (e) {
      console.error("Failed to close tips window:", e);
    }
  };

  const currentTip = TIPS_LIST[currentIndex];
  console.log("[TipsWindow] 5: currentTip=", currentTip);

  console.log("[TipsWindow] 6: entering JSX return");
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
        <button className="tips-btn tips-btn--primary" onClick={handleClose} type="button">
          {t("tips.gotIt")}
        </button>
      </div>
    </div>
  );
}
