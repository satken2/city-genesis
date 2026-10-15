import { useEffect, useState } from "react";
import { Menu } from "@tauri-apps/api/menu";
import { LogicalPosition } from "@tauri-apps/api/window";
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
  const [menu, setMenu] = useState<Menu | null>(null);
  const name = (id: string) => `${icons[id] ?? "🌡️"} ${t(`weather.names.${id}`, { defaultValue: t("weather.unknown") })}`;
  const label = weather ? name(weather.id) : t("weather.loading");
  const title = failed ? t("weather.failed") : editable ? t("weather.choose") : t("weather.label");

  useEffect(() => {
    if (!editable || !weather) return;
    let disposed = false;
    let created: Menu | undefined;
    // 候補は WebView の外へ開く。ブラウザ版も同じ API の差し替えを通して iframe の外へ表示する。
    void Menu.new({
      items: [
        { id: "auto", text: t("weather.automatic", { weather: label }) },
        ...weather.presets.map(({ id }) => ({ id, text: name(id) })),
      ].map(({ id, text }) => ({
        id: `weather:${id}`,
        text,
        checked: id === (weather.locked ? weather.id : "auto"),
        action: () => { void onSelect(id === "auto" ? null : id); },
      })),
    }).then(async value => {
      if (disposed) { await value.close(); return; }
      created = value;
      setMenu(value);
    }).catch(console.error);
    return () => {
      disposed = true;
      setMenu(null);
      void created?.close().catch(console.error);
    };
  }, [editable, weather, onSelect, t]);

  if (!editable) return <span className="cockpit-weather" title={title}>{label}</span>;

  return <button
    type="button"
    className={`cockpit-weather cockpit-weather-select${failed ? " cockpit-weather--error" : ""}`}
    aria-label={t("weather.choose")}
    aria-haspopup="menu"
    title={title}
    disabled={!weather || !menu}
    onClick={event => {
      const { left, bottom } = event.currentTarget.getBoundingClientRect();
      void menu?.popup(new LogicalPosition(left, bottom)).catch(console.error);
    }}
  >
    <span>{weather && !weather.locked ? t("weather.automatic", { weather: label }) : label}</span>
    <span aria-hidden="true">▾</span>
  </button>;
}
