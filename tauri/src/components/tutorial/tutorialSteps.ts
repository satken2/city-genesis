/**
 * Tutorial list and display information
 *
 * Used for New Game choices, the opening introduction, and progress in the tutorial window.
 * `tutorial/definitions/*.json` owns step instructions, control restrictions, and completion checks; this module
 * only derives step order and translation keys.
 * Step titles use `tutorial.<scenarioId>.step<number>.title`; phase bodies use each phase's `textKey`.
 */

import { DEFINITIONS } from "../../tutorial/definitions";
import scenario4Thumbnail from "../../assets/images/scenarios/scenario4.png";
import tradeScenarioThumbnail from "../../assets/images/scenarios/trade.png";
import highwayScenarioThumbnail from "../../assets/images/scenarios/highway.png";
import miningScenarioThumbnail from "../../assets/images/scenarios/mining.png";
import tourismScenarioThumbnail from "../../assets/images/scenarios/tourism.png";
import reclamationScenarioThumbnail from "../../assets/images/scenarios/reclamation.png";
import acquisitionScenarioThumbnail from "../../assets/images/scenarios/acquisition.svg";
import controlsScenarioThumbnail from "../../assets/images/scenarios/controls.png";

/** Display information for one step in the tutorial window */
export interface TutorialStep {
  /** Step number, 1-based (legacyStep in the definition) */
  id: number;
  /** i18n key for the step title */
  titleKey: string;
  /** i18n key for the first phase's instructions */
  descriptionKey: string;
}

/** Tutorial scenario display information */
export interface TutorialScenario {
  id: string;
  titleKey: string;
  descriptionKey: string;
  /** Translation key explaining the tutorial's goals in the opening introduction */
  introKey: string;
  /** Thumbnail URL for the list, detail panel, and introduction window */
  image?: string;
  steps: TutorialStep[];
}

/** List order (learning sequence) */
const SCENARIO_ORDER = ["controls", "scenario4", "trade", "highway", "mining", "tourism", "reclamation", "acquisition"];

const THUMBNAILS: Record<string, string> = {
  controls: controlsScenarioThumbnail,
  scenario4: scenario4Thumbnail,
  trade: tradeScenarioThumbnail,
  highway: highwayScenarioThumbnail,
  mining: miningScenarioThumbnail,
  tourism: tourismScenarioThumbnail,
  reclamation: reclamationScenarioThumbnail,
  acquisition: acquisitionScenarioThumbnail,
};

/**
 * All tutorial scenario definitions
 */
export const TUTORIAL_SCENARIOS: TutorialScenario[] = SCENARIO_ORDER.map((id) => ({
  id,
  titleKey: `tutorial.${id}.title`,
  descriptionKey: `tutorial.${id}.description`,
  introKey: `tutorial.intro.${id}`,
  image: THUMBNAILS[id],
  steps: DEFINITIONS[id].steps.map((step) => ({
    id: step.legacyStep,
    titleKey: `tutorial.${id}.step${step.legacyStep}.title`,
    descriptionKey: step.phases[0].textKey,
  })),
}));

/** Acquisition is taught in railway construction. Retain the old lesson definition for resuming saved tutorials. */
export const NEW_GAME_TUTORIAL_SCENARIOS = TUTORIAL_SCENARIOS.filter((scenario) => scenario.id !== "acquisition");
