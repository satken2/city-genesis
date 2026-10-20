import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { usePlayMode } from "../../hooks/usePlayMode";
import { PLAY_MODE_SCENARIO } from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { copyClimatePreset, moveWeatherBoundary, setWeatherPercentage, type ClimateSettings } from "../../utils/climate";
import { sendSessionReload } from "../../utils/ipc";
import { saveError } from "../../utils/saves";
import { ClimateChart, CLIMATE_COLORS, type ClimateMetric } from "./ClimateChart";
import "../common/SettingsForm.css";
import "./ClimateSettingsWindow.css";

const METRICS: ClimateMetric[] = ["temperature", "precipitation", "sunlight"];
const WEATHER_COLORS = ["#ebb74e", "#d7c288", "#97bd75", "#79aea1", "#9babbc", "#65798f", "#b6ada8",
  "#8bbcc9", "#4f9bcc", "#346ea5", "#d5dce9", "#a2bedc", "#758cad"];

export function ClimateSettingsWindow({ apiBaseUrl, onClose }: { apiBaseUrl: string; onClose: () => void }) {
  const { t } = useTranslation();
  const master = useMasterData();
  const playMode = usePlayMode(apiBaseUrl);
  const [climate, setClimate] = useState<ClimateSettings | null>(null);
  const [month, setMonth] = useState(0);
  const [metric, setMetric] = useState<ClimateMetric>("temperature");
  const [preset, setPreset] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const drag = useRef<{ month: number; left: string; right: string; startX: number; width: number; weather: Record<string, number> } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void fetchApi<{ city: { climate: ClimateSettings } }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal: controller.signal })
      .then(({ city }) => { if (!controller.signal.aborted) setClimate(city.climate); })
      .catch(e => { if (!controller.signal.aborted) setError(saveError(e, t)); });
    return () => controller.abort();
  }, [apiBaseUrl, t]);
  const disabled = busy || playMode !== PLAY_MODE_SCENARIO;
  const identifiers = Object.keys(master?.weather ?? {});
  const weatherName = (id: string) => master?.weather[id]?.display_name ?? id;
  function updateMonth(index: number, values: Partial<ClimateSettings["months"][number]>) {
    setPreset("");
    setClimate(current => current && ({ ...current, months: current.months.map((entry, position) => position === index ? { ...entry, ...values } : entry) }));
  }
  function updateMetric(index: number, key: ClimateMetric, value: number) {
    if (!Number.isFinite(value)) return;
    updateMonth(index, { [key]: key === "temperature" ? value : Math.max(0, key === "sunlight" ? Math.min(100, value) : value) });
  }
  async function apply() {
    if (!climate || disabled || !climate.name.trim()) return;
    setBusy(true); setError("");
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city/climate"), {
        method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...climate, name: climate.name.trim() }),
      });
      await sendSessionReload();
      onClose();
    } catch (e) { setError(saveError(e, t)); }
    finally { setBusy(false); }
  }
  return <form className="climate-settings" onSubmit={event => { event.preventDefault(); void apply(); }}>
    {climate && master && <div className="climate-settings-columns">
      <section className="climate-settings-left">
        <label className="climate-settings-field"><span>{t("climateSettings.name")}</span>
          <input className="settings-number climate-settings-name" value={climate.name} maxLength={120} disabled={disabled}
            onChange={event => { setPreset(""); setClimate({ ...climate, name: event.target.value }); }} /></label>
        <h3>{t("climateSettings.probabilities")}</h3>
        <p className="climate-settings-hint">{t("climateSettings.probabilityHint")}</p>
        <div className="climate-weather-months">
          {climate.months.map((entry, index) => {
            let cumulative = 0;
            const positions = identifiers.map(id => { cumulative += entry.weather[id]; return cumulative; });
            return <div className={`climate-weather-row${month === index ? " is-selected" : ""}`} key={index}>
              <button type="button" className="climate-month-button" onClick={() => setMonth(index)}>{t("climateSettings.month", { month: index + 1 })}</button>
              <div className="climate-weather-bar" onClick={() => setMonth(index)}>
                {identifiers.map((id, weatherIndex) => <span key={id} style={{ width: `${entry.weather[id]}%`, background: WEATHER_COLORS[weatherIndex % WEATHER_COLORS.length] }}
                  title={`${weatherName(id)} ${entry.weather[id]}%`} />)}
                {identifiers.slice(0, -1).map((id, boundary) => <button key={id} type="button" role="slider"
                  className="climate-weather-boundary" style={{ left: `${positions[boundary]}%` }} disabled={disabled}
                  tabIndex={month === index ? 0 : -1} aria-label={`${t("climateSettings.month", { month: index + 1 })} ${weatherName(id)} / ${weatherName(identifiers[boundary + 1])}`}
                  aria-valuenow={positions[boundary]} aria-valuemin={boundary ? positions[boundary - 1] : 0} aria-valuemax={positions[boundary + 1]}
                  onPointerDown={event => {
                    if (event.button !== 0) return;
                    event.preventDefault(); setMonth(index); event.currentTarget.setPointerCapture(event.pointerId);
                    drag.current = { month: index, left: id, right: identifiers[boundary + 1], startX: event.clientX,
                      width: event.currentTarget.parentElement!.getBoundingClientRect().width, weather: entry.weather };
                  }}
                  onPointerMove={event => {
                    const active = drag.current;
                    if (!active) return;
                    updateMonth(active.month, { weather: moveWeatherBoundary(active.weather, active.left, active.right, (event.clientX - active.startX) / active.width * 100) });
                  }}
                  onPointerUp={() => { drag.current = null; }} onPointerCancel={() => { drag.current = null; }}
                  onKeyDown={event => {
                    if (!["ArrowLeft", "ArrowRight"].includes(event.key)) return;
                    event.preventDefault(); setMonth(index);
                    updateMonth(index, { weather: moveWeatherBoundary(entry.weather, id, identifiers[boundary + 1], event.key === "ArrowRight" ? 1 : -1) });
                  }} />)}
              </div>
            </div>;
          })}
        </div>
        <h3>{t("climateSettings.month", { month: month + 1 })}</h3>
        <div className="climate-weather-values">
          {identifiers.map((id, index) => <label key={id}>
            <span><i style={{ background: WEATHER_COLORS[index % WEATHER_COLORS.length] }} />{weatherName(id)}</span>
            <input type="number" min={0} max={100} step={1} value={climate.months[month].weather[id]} disabled={disabled}
              onChange={event => { if (Number.isFinite(event.target.valueAsNumber)) updateMonth(month, { weather: setWeatherPercentage(climate.months[month].weather, id, event.target.valueAsNumber) }); }} />
            <span>%</span>
          </label>)}
        </div>
      </section>
      <section className="climate-settings-right">
        <label className="climate-settings-field"><span>{t("climateSettings.preset")}</span>
          <select value={preset} disabled={disabled} onChange={event => {
            const id = event.target.value; if (!id) return;
            setPreset(id); setClimate(copyClimatePreset(master.climates[Number(id)]));
          }}>
            <option value="">{t("climateSettings.current")}</option>
            {Object.entries(master.climates).map(([id, entry]) => <option key={id} value={id}>{entry.display_name}</option>)}
          </select></label>
        <div className="climate-chart-legend">
          {METRICS.map(key => <button type="button" key={key} className={metric === key ? "is-selected" : ""}
            aria-pressed={metric === key} onClick={() => setMetric(key)}><i style={{ background: CLIMATE_COLORS[key] }} />{t(`weather.${key}`)}</button>)}
        </div>
        <ClimateChart months={climate.months} metric={metric} selectedMonth={month} disabled={disabled} onSelect={setMonth} onChange={updateMetric} />
        <p className="climate-settings-hint">{t("climateSettings.chartHint")}</p>
        <h3>{t("climateSettings.month", { month: month + 1 })}</h3>
        <div className="climate-metric-values">
          {METRICS.map(key => <label key={key}><span style={{ color: CLIMATE_COLORS[key] }}>{t(`weather.${key}`)}</span>
            <input className="settings-number" type="number" step={key === "temperature" ? 0.1 : 1}
              min={key === "temperature" ? undefined : 0} max={key === "sunlight" ? 100 : undefined}
              value={climate.months[month][key]} disabled={disabled} onChange={event => updateMetric(month, key, event.target.valueAsNumber)} />
            <span>{key === "temperature" ? "°C" : key === "precipitation" ? "mm" : "%"}</span></label>)}
        </div>
        <p className="climate-settings-hint">{t("climateSettings.averageHint")}</p>
      </section>
    </div>}
    <footer className="climate-settings-footer">
      {error && <p role="alert" className="climate-settings-error">{error}</p>}
      <button className="settings-btn" type="button" onClick={onClose} disabled={busy}>{t("common.cancel")}</button>
      <button className="settings-btn climate-settings-apply" type="submit" disabled={disabled || !climate?.name.trim() || !master}
        data-guide="climate.apply">{busy ? t("common.saving") : t("climateSettings.apply")}</button>
    </footer>
  </form>;
}
