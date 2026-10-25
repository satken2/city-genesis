/**
 * Climate window
 *
 * Opens when clicking the realized climate in Cockpit outside scenario authoring mode.
 * The top cards show the four realized climate factors. The table below lists production efficiency (the product
 * of factor multipliers) and individual multipliers for each facility variant with climate conditions.
 * The core calculates multipliers using the same formulas as production; this panel only displays them.
 * Show "-" for factors that do not affect a facility's production, and an em dash while the climate is
 * undetermined.
 */
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { useEpochPeriodUnit } from "../../hooks/useEpochPeriodUnit";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { Meter } from "../common/Meter";
import { CLIMATE_ELEMENTS, ClimateIcon, formatClimateValue, type ClimateElement, type ClimateValues } from "./climateDisplay";
import "./ClimateEffectsWindow.css";

type ClimateEffect = {
  variant: number;
  factors: Partial<Record<ClimateElement, number | null>>;
  factor: number | null;
};

type ClimateEffectsResponse = {
  climate: ClimateValues;
  variants: ClimateEffect[];
};

export function ClimateEffectsWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const master = useMasterData();
  const [data, setData] = useState<ClimateEffectsResponse | null>(null);
  const [error, setError] = useState("");
  const periodUnit = useEpochPeriodUnit(apiBaseUrl);

  useEffect(() => {
    const controller = new AbortController();
    void fetchApi<ClimateEffectsResponse>(buildApiUrl(apiBaseUrl, "/api/v1/city/climate-effects"), { signal: controller.signal })
      .then(response => { if (!controller.signal.aborted) setData(response); })
      .catch(e => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e)); });
    return () => controller.abort();
  }, [apiBaseUrl]);

  if (error) return <div className="climate-effects-message">{error}</div>;
  if (!data) return <div className="climate-effects-message">{t("common.loading")}</div>;

  const locale = resolveIntlLocale(i18n.language);
  const percent = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 0 });
  const format = (value: number | null) => value == null ? "—" : percent.format(value);

  return <div className="climate-effects">
    <section className="climate-effects-card">
      <h3>{t("climateEffects.currentClimate", { context: periodUnit })}</h3>
      <div className="climate-effects-values">
        {CLIMATE_ELEMENTS.map(({ key, label }) => <div key={key} className="climate-effects-value">
          <ClimateIcon element={key} />
          <div>
            <span className="climate-effects-value-label">{t(label)}</span>
            <span className="climate-effects-value-number">{formatClimateValue(key, data.climate[key], locale)}</span>
          </div>
        </div>)}
      </div>
    </section>
    <h3>{t("climateEffects.title")}</h3>
    <table className="climate-effects-table">
      <thead>
        <tr>
          <th>{t("climateEffects.facility")}</th>
          <th>{t("climateEffects.factor")}</th>
          {CLIMATE_ELEMENTS.map(({ key, label }) => <th key={key} className="climate-effects-number">{t(label)}</th>)}
        </tr>
      </thead>
      <tbody>
        {data.variants.map(effect => <tr key={effect.variant}>
          <td>{master?.agents[effect.variant]?.display_name ?? effect.variant}</td>
          <td>
            {effect.factor == null ? "—" : <div className="climate-effects-total">
              <Meter value={effect.factor} />
              <span>{format(effect.factor)}</span>
            </div>}
          </td>
          {CLIMATE_ELEMENTS.map(({ key }) => <td key={key} className="climate-effects-number">
            {key in effect.factors ? format(effect.factors[key] ?? null) : "-"}
          </td>)}
        </tr>)}
      </tbody>
    </table>
  </div>;
}
