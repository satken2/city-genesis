import { describeError } from "../../utils/errors";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import type { PathData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { IPC_EVENT_PATH_APPEARANCES, queryPathAppearances, sendSessionReload, type PathAppearances } from "../../utils/ipc";
import { PATH_APPEARANCE_STANDARD } from "../../constants";

import "./PathDetailPanel.css";

/**
 * Appearance numbers the variant has in the open city (asked from Unity, which alone knows the models).
 * Empty until Unity replies, and stays empty when Unity is not connected.
 */
export function usePathAppearances(variant: number | undefined): number[] {
  const [appearances, setAppearances] = useState<number[]>([]);
  useEffect(() => {
    setAppearances([]);
    if (variant === undefined) return;
    let disposed = false;
    const unlisten = listen<PathAppearances>(IPC_EVENT_PATH_APPEARANCES, event => {
      if (!disposed && event.payload.variant === variant) setAppearances(event.payload.appearances);
    });
    void unlisten.then(() => disposed ? undefined : queryPathAppearances("path_detail", variant)).catch(() => undefined);
    return () => { disposed = true; void unlisten.then(off => off()); };
  }, [variant]);
  return appearances;
}

/**
 * Appearance row of the path detail basic information grid (label + value cells).
 *
 * Lists the standard appearance and the variant's appearances. A number the open city's models lack (another pack,
 * or a rebuilt variant) stays selectable so the player can see and change it; Unity draws it as standard.
 * The choice is saved immediately, and Unity redraws the path.
 */
export function PathAppearanceRow({ path, appearances, apiBaseUrl, onSaved }: {
  path: PathData; appearances: number[]; apiBaseUrl: string; onSaved: (path: PathData) => void;
}) {
  const { t } = useTranslation();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => setError(null), [path.path_id]);

  async function save(appearance: number) {
    setSaving(true); setError(null);
    try {
      const updated = await fetchApi<PathData>(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(path.path_id)), {
        method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ appearance }),
      });
      onSaved(updated);
      await sendSessionReload();
    } catch (e) { setError(describeError(e)); }
    finally { setSaving(false); }
  }

  const options = [...new Set([PATH_APPEARANCE_STANDARD, ...appearances, path.appearance])].sort((a, b) => a - b);
  return <>
    <label className="agent-detail-label" htmlFor="path-appearance">{t("path.appearance")}</label>
    <span className="agent-detail-value">
      <select id="path-appearance" className="zone-change-select" value={path.appearance} disabled={saving}
        onChange={e => void save(Number(e.target.value))}>
        {options.map(number => <option key={number} value={number}>
          {number === PATH_APPEARANCE_STANDARD ? t("path.appearanceStandard") : t("path.appearanceNumber", { number })}
        </option>)}
      </select>
      {error && <span className="zone-change-error" role="alert">{error}</span>}
    </span>
  </>;
}
