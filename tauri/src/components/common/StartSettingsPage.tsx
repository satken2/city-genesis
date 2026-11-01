/**
 * Start settings page of the information window
 *
 * Shown once a new game's city appears: city name, emblem and flag, and simulation days. Language, ground cover, and
 * asset pack stay on the New Game screen because changing them after loading renames or redraws the whole city.
 * Settings the scenario author fixed (scenario_fixed_options) are hidden.
 *
 * The emblem and flag button opens the editor window, which saves on its own. The page button sends a changed name or
 * days through PATCH /api/v1/city/ before moving on; the core accepts days only before the first epoch. A blank name
 * keeps the current one. Closing the window from the title bar changes nothing.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import { SIMULATION_DAYS_CHOICES } from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { describeError } from "../../utils/errors";
import type { ScenarioStartOption } from "../../utils/saves";
import { CityInsigniaButton } from "../government/CityInsigniaButton";
import "../taskbar/NewGameWindow.css";

/** Properties from GET /api/v1/city/ used by this page */
type CityStartFields = {
  name: string;
  simulation_days: number;
  scenario_fixed_options?: ScenarioStartOption[] | null;
};

type StartSettingsPageProps = {
  apiBaseUrl: string;
  buttonLabel: string;
  onDone: () => void;
};

export function StartSettingsPage({ apiBaseUrl, buttonLabel, onDone }: StartSettingsPageProps) {
  const { t } = useTranslation();
  const [initial, setInitial] = useState<CityStartFields | null>(null);
  const [cityName, setCityName] = useState("");
  const [simulationDays, setSimulationDays] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ city: CityStartFields }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal: controller.signal, cache: "reload" })
      .then(({ city }) => {
        setInitial(city);
        setCityName(city.name);
        setSimulationDays(city.simulation_days);
      })
      .catch((e) => {
        if (!controller.signal.aborted) setError(describeError(e, t));
      });
    return () => controller.abort();
  }, [apiBaseUrl, t]);

  const fixed = new Set(initial?.scenario_fixed_options ?? []);

  async function apply() {
    if (!initial) {
      onDone();
      return;
    }
    const name = cityName.trim();
    const changes = {
      ...(name && name !== initial.name ? { name } : {}),
      ...(simulationDays !== initial.simulation_days ? { simulation_days: simulationDays } : {}),
    };
    if (Object.keys(changes).length > 0) {
      setBusy(true);
      setError("");
      try {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(changes),
        });
        if (changes.name) await emit("city:renamed", { name: changes.name });
      } catch (e) {
        setError(describeError(e, t));
        setBusy(false);
        return;
      }
    }
    onDone();
  }

  return (
    <>
      <div className="info-dialog-content">
        <h2 className="info-dialog-heading">{t("newGame.startSettingsHeading")}</h2>
        {initial && (
          <div className="new-game-confirm-fields">
            {!fixed.has("city_name") && (
              <div className="new-game-confirm-field" data-option="city_name">
                <label className="new-game-confirm-label" htmlFor="start-settings-city-name">{t("newGame.cityNameLabel")}</label>
                <input
                  id="start-settings-city-name"
                  className="new-game-confirm-input"
                  type="text"
                  value={cityName}
                  onChange={(e) => setCityName(e.target.value)}
                  disabled={busy}
                />
              </div>
            )}

            <div className="new-game-confirm-field" data-option="insignia">
              <span className="new-game-confirm-label">{t("newGame.insigniaLabel")}</span>
              <CityInsigniaButton apiBaseUrl={apiBaseUrl} disabled={busy} />
            </div>

            {!fixed.has("simulation_days") && (
              <div className="new-game-confirm-field" data-option="simulation_days">
                <span className="new-game-confirm-label">{t("newGame.simulationDaysLabel")}</span>
                <div className="new-game-confirm-days">
                  {SIMULATION_DAYS_CHOICES.map((days) => (
                    <button
                      key={days}
                      className={`new-game-confirm-days-btn ${simulationDays === days ? "active" : ""}`}
                      onClick={() => setSimulationDays(days)}
                      disabled={busy}
                      type="button"
                      aria-pressed={simulationDays === days}
                    >
                      {t("newGame.daysOption", { days })}
                    </button>
                  ))}
                </div>
                <span className="new-game-confirm-note">{t("newGame.simulationDaysNote")}</span>
              </div>
            )}
          </div>
        )}
        {error && <p className="info-dialog-error" role="alert">{error}</p>}
      </div>
      <button className="info-dialog-button" type="button" disabled={busy} onClick={() => void apply()}>
        {buttonLabel}
      </button>
    </>
  );
}
