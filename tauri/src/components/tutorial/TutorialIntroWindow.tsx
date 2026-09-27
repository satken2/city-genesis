/**
 * チュートリアル開始時の説明ウィンドウ
 *
 * 上半分にシナリオのサムネイル、下半分にタイトルと「このチュートリアルで何をするか」の説明を置く。
 * 開始ボタンもタイトルバーの×も同じくウィンドウを閉じるだけで、進行の再開は
 * チュートリアル本体(Tutorial.tsx)がこのウィンドウの非表示通知を受けて行う。
 */

import { useTranslation } from "react-i18next";
import { TUTORIAL_SCENARIOS } from "./tutorialSteps";
import "./TutorialIntroWindow.css";

type TutorialIntroWindowProps = {
  tutorialId: string;
  onStart: () => void;
};

export function TutorialIntroWindow({ tutorialId, onStart }: TutorialIntroWindowProps) {
  const { t } = useTranslation();
  const scenario = TUTORIAL_SCENARIOS.find((s) => s.id === tutorialId);
  if (!scenario) return null;
  return (
    <div className="tutorial-intro">
      <div className="tutorial-intro-image">
        {scenario.image && <img src={scenario.image} alt="" />}
      </div>
      <div className="tutorial-intro-content">
        <h2 className="tutorial-intro-title">{t(scenario.titleKey)}</h2>
        <h3 className="tutorial-intro-heading">{t("tutorial.intro.heading")}</h3>
        <p className="tutorial-intro-text">{t(scenario.introKey)}</p>
      </div>
      <button className="tutorial-intro-start" type="button" onClick={onStart}>
        {t("tutorial.startButton")}
      </button>
    </div>
  );
}
