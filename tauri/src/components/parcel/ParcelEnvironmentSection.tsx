/**
 * Parcel environment section
 *
 * Displays parcel-specific environmental scores (sunlight, air pollution, noise, crime) as meters.
 * GET /api/v1/parcels/{id}/environment returns raw scores before living-environment multiplier
 * composition: each is a [0,1] measure of badness (0=ideal, 1=worst).
 * Sunlight is the proportion of time in shade, so invert it to display sunlight availability.
 *
 * Scores are calculated together during epochs; cities with no epoch run return null.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Meter } from "../common/Meter";
import { buildApiUrl, fetchApi } from "../../utils/api";

/** GET /api/v1/parcels/{id}/environment response. */
type ParcelEnvironment = {
  parcel_id: number;
  sunshine: number | null;
  air_pollution: number | null;
  noise: number | null;
  crime: number | null;
};

type ParcelEnvironmentSectionProps = {
  apiBaseUrl: string;
  parcelId: number;
};

export function ParcelEnvironmentSection({
  apiBaseUrl,
  parcelId,
}: ParcelEnvironmentSectionProps) {
  const { t } = useTranslation();
  const [environment, setEnvironment] = useState<ParcelEnvironment | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<ParcelEnvironment>(
      buildApiUrl(apiBaseUrl, `/api/v1/parcels/${parcelId}/environment`),
      { signal: controller.signal }
    )
      .then((res) => {
        if (controller.signal.aborted) return;
        setEnvironment(res);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        console.error("Failed to fetch parcel environment:", err);
        setEnvironment(null);
      });
    return () => controller.abort();
  }, [apiBaseUrl, parcelId]);

  // All four values are calculated in the same epoch processing, so they are uncalculated together.
  // Individual missing values occur for newly generated parcels not yet in the heatmap; handle them identically.
  const hasAnyValue =
    environment != null &&
    (environment.sunshine != null ||
      environment.air_pollution != null ||
      environment.noise != null ||
      environment.crime != null);

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">
        {t("parcel.environment", "環境")}
      </h3>
      {hasAnyValue ? (
        <div className="parcel-environment-list">
          {environment.sunshine != null && (
            <Meter
              label={t("parcel.envSunshine")}
              value={1 - environment.sunshine}
            />
          )}
          {environment.air_pollution != null && (
            <Meter
              label={t("parcel.envAirPollution")}
              value={environment.air_pollution}
              polarity="negative"
            />
          )}
          {environment.noise != null && (
            <Meter
              label={t("parcel.envNoise")}
              value={environment.noise}
              polarity="negative"
            />
          )}
          {environment.crime != null && (
            <Meter
              label={t("parcel.envCrime")}
              value={environment.crime}
              polarity="negative"
            />
          )}
        </div>
      ) : (
        <p className="agent-detail-muted">{t("parcel.envNoData")}</p>
      )}
    </section>
  );
}
