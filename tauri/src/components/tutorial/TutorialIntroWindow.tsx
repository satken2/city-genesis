/**
 * Tutorial introduction window
 *
 * Scenario thumbnail above, title and explanation of the tutorial's goals below.
 * Start and the title bar's close button both simply close the window. Tutorial.tsx resumes progression after
 * receiving its hidden notification.
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
