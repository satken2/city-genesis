/**
 * Scenario information section (band at the top of the achievements window)
 *
 * Displays the scenario title, description, and completion conditions.
 * These are multilingual introductions written by the scenario author and do not affect the simulation.
 * The content matches the scenario details on the New Game screen, allowing players to review their objectives
 * during play.
 * Use the UI language, falling back to English when that language has no text.
 *
 * Display only. Edit introductions exclusively in ScenarioMetadataWindow after saving as a scenario, keeping
 * multilingual inputs and starting settings together. Show directions there in scenario authoring mode.
 * During normal play, render nothing if all three fields are empty.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveLocalizedText, type LocalizedText } from "../../utils/locale";
import "./ScenarioInfoSection.css";

type ScenarioInfoSectionProps = {
  apiBaseUrl: string;
  /** Whether scenario authoring mode is active; when true, show where to edit the introduction. */
  scenarioMode: boolean;
};

/** Properties from GET /api/v1/city used by this section */
type CityScenarioFields = {
  city: {
    scenario_title?: LocalizedText;
    scenario_description?: LocalizedText;
    scenario_objective?: LocalizedText;
  };
};

export function ScenarioInfoSection({ apiBaseUrl, scenarioMode }: ScenarioInfoSectionProps) {
  const { t, i18n } = useTranslation();
  const [texts, setTexts] = useState<CityScenarioFields["city"]>({});
  const [imagePreview, setImagePreview] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      try {
        const res = await fetchApi<CityScenarioFields>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/"),
          { signal: controller.signal }
        );
        setTexts(res.city);

        // Try to retrieve the existing thumbnail image.
        try {
          const thumbUrl = buildApiUrl(apiBaseUrl, "/api/v1/scenario-tools/thumbnail");
          const blob = await fetchApi<Blob>(thumbUrl, { signal: controller.signal, responseType: "blob" });
          if (!controller.signal.aborted) {
            setImagePreview(URL.createObjectURL(blob));
          }
        } catch {
          /* Ignore a missing thumbnail. */
        }
      } catch {
        /* Display nothing if retrieval fails. */
      } finally {
        if (!controller.signal.aborted) setLoaded(true);
      }
    }

    void load();
    return () => controller.abort();
  }, [apiBaseUrl]);

  if (!loaded) return null;

  const title = resolveLocalizedText(texts.scenario_title, i18n.language);
  const description = resolveLocalizedText(texts.scenario_description, i18n.language);
  const objective = resolveLocalizedText(texts.scenario_objective, i18n.language);

  // During normal play, an empty section serves no purpose for a scenario with no introduction.
  if (!scenarioMode && !title && !description && !objective) return null;

  return (
    <div className="scenario-info">
      <h3 className="scenario-info__title">{t("achievements.scenarioSection")}</h3>
      {scenarioMode && <p className="scenario-info__read-text scenario-info__hint">{t("achievements.scenarioEditHint")}</p>}
      <div className="scenario-info__read">
        {imagePreview && (
          <img className="scenario-info__read-thumb" src={imagePreview} alt="" />
        )}
        {title && <div className="scenario-info__read-title">{title}</div>}
        {description && <p className="scenario-info__read-text">{description}</p>}
        {objective && (
          <div className="scenario-info__read-block">
            <span className="scenario-info__label">
              {t("achievements.scenarioObjectiveLabel")}
            </span>
            <p className="scenario-info__read-text">{objective}</p>
          </div>
        )}
      </div>
    </div>
  );
}
