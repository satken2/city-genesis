/**
 * Governance score mini meter (persistent in the cockpit)
 *
 * Continuously shows an assessment of city governance. Click to open the achievements window with score
 * breakdown and history.
 *
 * Display the average per household (city_score_per_household), rather than the total city score.
 * The total grows with household count and can rise from population growth alone, so it does not measure
 * governance quality. A per-household value reflects residents' prosperity independently of population.
 *
 * - Full width = maximum per household (2.0); filled width = current score per household.
 * - >= 0.7: green / >= 0.4: yellow / < 0.4: red (Meter's default colors).
 * - Refresh through backend REST polling every 10 seconds, matching BudgetMiniMeter.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { Meter } from "../common/Meter";
import "./hud.css";

/** Backend polling interval in milliseconds */
const POLL_INTERVAL_MS = 10_000;

/**
 * Maximum contribution of one household to the city score.
 * The city score is twice the sum of household satisfaction (at most 1.0), so this constant follows from the
 * scoring formula.
 */
const MAX_SCORE_PER_HOUSEHOLD = 2.0;

type ScoreResponse = {
  latest?: {
    city_score_per_household?: number;
  };
};

export function ScoreMiniMeter({
  apiBaseUrl,
  onOpen,
}: {
  apiBaseUrl: string;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  const [perHousehold, setPerHousehold] = useState<number | null>(null);

  useEffect(() => {
    if (!apiBaseUrl) return;

    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const res = await fetchApi<ScoreResponse>(
          buildApiUrl(apiBaseUrl, "/api/v1/score/"),
          { signal: controller.signal },
        );
        if (cancelled) return;
        // Treat nonnumeric values (missing/null) as an empty meter. Since the cockpit is persistent,
        // a render exception would remove the whole HUD panel; establish the value's type here.
        const value = res.latest?.city_score_per_household;
        setPerHousehold(typeof value === "number" && Number.isFinite(value) ? value : null);
      } catch {
        // Keep the previous value on retrieval failure (including aborts) to avoid flicker, as in BudgetMiniMeter.
      }
    }

    void refresh();
    const timer = setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      clearInterval(timer);
    };
  }, [apiBaseUrl]);

  const hasData = perHousehold !== null;

  return (
    <button
      type="button"
      className="hud-mini-meter"
      data-guide="hud.score"
      onClick={onOpen}
      title={t("taskbar.scoreMeterTooltip")}
    >
      <span className="hud-mini-meter__label">{t("taskbar.scoreMeterLabel")}</span>
      {hasData && (
        <span className="hud-mini-meter__value">{perHousehold!.toFixed(2)}</span>
      )}
      <div className="hud-mini-meter__bar">
        {hasData ? (
          <Meter value={perHousehold! / MAX_SCORE_PER_HOUSEHOLD} />
        ) : (
          <span className="hud-mini-meter__placeholder">—</span>
        )}
      </div>
    </button>
  );
}
