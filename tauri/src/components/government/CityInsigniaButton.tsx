/**
 * Emblem and city flag button
 *
 * Shows the city's emblem and flag side by side and opens the emblem and flag editor (city_insignia) on click.
 * An unset image shows an empty frame. The editor emits `city:insignia-changed` after saving, and the images are
 * fetched again.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

import { useApiImageUrl } from "../../hooks/useApiImageUrl";
import { buildApiUrl } from "../../utils/api";
import "./CityInsigniaButton.css";

export function CityInsigniaButton({ apiBaseUrl, disabled = false }: { apiBaseUrl: string; disabled?: boolean }) {
  const { t } = useTranslation();
  const [version, setVersion] = useState(0);
  const emblem = useApiImageUrl(buildApiUrl(apiBaseUrl, `/api/v1/city/insignia/emblem?v=${version}`));
  const flag = useApiImageUrl(buildApiUrl(apiBaseUrl, `/api/v1/city/insignia/flag?v=${version}`));

  useEffect(() => {
    const unlisten = listen("city:insignia-changed", () => setVersion((v) => v + 1));
    return () => {
      void unlisten.then((fn) => fn());
    };
  }, []);

  return (
    <button
      type="button"
      className="city-insignia-button"
      disabled={disabled}
      title={t("windowType.city_insignia")}
      aria-label={t("windowType.city_insignia")}
      onClick={() => void invoke("open_registered_window", { windowType: "city_insignia" }).catch(console.error)}
    >
      <span className="city-insignia-button__emblem">{emblem.src && <img src={emblem.src} alt="" onError={emblem.onError} />}</span>
      <span className="city-insignia-button__flag">{flag.src && <img src={flag.src} alt="" onError={flag.onError} />}</span>
    </button>
  );
}
