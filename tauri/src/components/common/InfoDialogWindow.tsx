/**
 * Generic information window
 *
 * Shows the pages the opener passes in `params.extra`, in order. Every page but the last has Next at the lower
 * right; the last page's button (`buttonKey`) closes the window. The title bar's close button closes it on any page;
 * openers that wait for dismissal watch its hidden notification.
 *
 * Page kinds:
 * - text: image above; heading, subheading, and explanation below. Text is passed as translation keys so it follows
 *   the display language (development notice, tutorial introduction).
 * - scenario: the author's introduction (thumbnail, title, description, completion conditions), matching the scenario
 *   details on the New Game screen. Multilingual text resolves in the display language, falling back to English.
 * - start_settings: settings chosen after a new game's city appears (StartSettingsPage).
 */

import { useState } from "react";
import { openUrl } from "@tauri-apps/plugin-opener";
import { useTranslation } from "react-i18next";
import { resolveLocalizedText } from "../../utils/locale";
import { scenarioDisplayTitle, type ScenarioEntry } from "../../utils/saves";
import { ScenarioThumbnail } from "./ScenarioThumbnail";
import { StartSettingsPage } from "./StartSettingsPage";
import "./InfoDialogWindow.css";

/** Scenario list item fields shown on a scenario page */
export type ScenarioIntro = Pick<ScenarioEntry,
  "id" | "content_revision" | "scenario_title" | "scenario_city_name" | "scenario_description" | "scenario_objective">;

type TextPage = {
  kind: "text";
  image?: string;
  headingKey: string;
  subheadingKey: string;
  textKey: string;
  /** Shown after the explanation and opened in the default browser, because text in the WebView cannot be selected or copied. */
  url?: string;
};

export type InfoDialogPage = TextPage | { kind: "scenario"; scenario: ScenarioIntro } | { kind: "start_settings" };

export type InfoDialogContent = {
  /** Title bar text. Defaults to the window type's name. */
  titleKey?: string;
  pages: InfoDialogPage[];
  /** Label of the last page's button */
  buttonKey: string;
};

type InfoDialogWindowProps = {
  apiBaseUrl: string;
  content: InfoDialogContent;
  onClose: () => void;
};

export function InfoDialogWindow({ apiBaseUrl, content, onClose }: InfoDialogWindowProps) {
  const { t } = useTranslation();
  const [index, setIndex] = useState(0);
  const page = content.pages[index];
  const last = index === content.pages.length - 1;
  const buttonLabel = t(last ? content.buttonKey : "common.next");
  const done = last ? onClose : () => setIndex(index + 1);

  return (
    <div className="info-dialog">
      {page.kind === "start_settings" ? (
        <StartSettingsPage apiBaseUrl={apiBaseUrl} buttonLabel={buttonLabel} onDone={done} />
      ) : (
        <>
          {page.kind === "text" ? <TextPageView page={page} /> : <ScenarioPageView apiBaseUrl={apiBaseUrl} scenario={page.scenario} />}
          <button className="info-dialog-button" type="button" onClick={done}>
            {buttonLabel}
          </button>
        </>
      )}
    </div>
  );
}

function TextPageView({ page }: { page: TextPage }) {
  const { t } = useTranslation();
  return (
    <>
      {page.image && (
        <div className="info-dialog-image">
          <img src={page.image} alt="" />
        </div>
      )}
      <div className="info-dialog-content">
        <h2 className="info-dialog-heading">{t(page.headingKey)}</h2>
        <h3 className="info-dialog-subheading">{t(page.subheadingKey)}</h3>
        <p className="info-dialog-text">{t(page.textKey)}</p>
        {page.url && (
          <button className="info-dialog-link" type="button" onClick={() => void openUrl(page.url!)}>
            {page.url}
          </button>
        )}
      </div>
    </>
  );
}

function ScenarioPageView({ apiBaseUrl, scenario }: { apiBaseUrl: string; scenario: ScenarioIntro }) {
  const { t, i18n } = useTranslation();
  return (
    <>
      <div className="info-dialog-image">
        <ScenarioThumbnail
          apiBaseUrl={apiBaseUrl}
          scenarioId={scenario.id}
          revision={scenario.content_revision}
          className="info-dialog-thumbnail"
          placeholderModifier=""
        />
      </div>
      <div className="info-dialog-content">
        <h2 className="info-dialog-heading">{scenarioDisplayTitle(scenario, i18n.language)}</h2>
        <p className="info-dialog-text info-dialog-text--authored">
          {resolveLocalizedText(scenario.scenario_description, i18n.language) || t("newGame.detailNoDescription")}
        </p>
        <h3 className="info-dialog-subheading">{t("newGame.detailObjectiveLabel")}</h3>
        <p className="info-dialog-text info-dialog-text--authored">
          {resolveLocalizedText(scenario.scenario_objective, i18n.language) || t("newGame.detailNoObjective")}
        </p>
      </div>
    </>
  );
}
