import { useTranslation } from "react-i18next";
import type { WeatherState } from "../../utils/ipc";

const icons: Record<string, string> = {
  clear: "☀️", high_clouds: "🌤️", fair: "🌤️", broken_clouds: "⛅", overcast: "☁️",
  rain_clouds: "🌧️", fog: "🌫️", drizzle: "🌦️", rain: "🌧️", heavy_rain: "⛈️",
  light_snow: "🌨️", snow: "❄️", blizzard: "🌬️",
};

export function WeatherStatus({ weather, editable, failed, onSelect }: {
  weather: WeatherState | null;
  editable: boolean;
  failed: boolean;
  onSelect: (id: string | null) => Promise<void>;
}) {
  const { t } = useTranslation();
  const name = (id: string) => `${icons[id] ?? "🌡️"} ${t(`weather.names.${id}`, { defaultValue: t("weather.unknown") })}`;
  const label = weather ? name(weather.id) : t("weather.loading");
  const title = failed ? t("weather.failed") : editable ? t("weather.choose") : t("weather.label");
  if (!editable) return <span className="cockpit-weather" title={title}>{label}</span>;

  // 標準selectのポップアップはWebViewの外に開き、画面下端では上へ展開される。
  // absolute配置の自作メニューは高さ76pxのCockpitウィンドウに切り取られるため使わない。
  return <select
    className={`cockpit-weather cockpit-weather-select${failed ? " cockpit-weather--error" : ""}`}
    aria-label={t("weather.choose")}
    title={title}
    value={weather?.locked ? weather.id : "auto"}
    disabled={!weather}
    onChange={(event) => void onSelect(event.target.value === "auto" ? null : event.target.value)}
  >
    <option value="auto">{weather ? t("weather.automatic", { weather: label }) : label}</option>
    {weather?.presets.map(({ id }) => <option key={id} value={id}>{name(id)}</option>)}
  </select>;
}
