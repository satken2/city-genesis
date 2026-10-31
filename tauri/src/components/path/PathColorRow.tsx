import { describeError } from "../../utils/errors";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { PathData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";

import "./PathDetailPanel.css";

/**
 * Color row of the path detail basic information grid (label + value cells).
 *
 * Only variants that use a color have one (API `color` is not null). Picking a color sets the path's own color;
 * "Reset" sends null so the path follows the variant default again. Unity paints the model's PATH_PAINT parts.
 * The color is sent once when the picker commits (native change), not while dragging, since every save makes Unity
 * redraw the path.
 */
export function PathColorRow({ path, apiBaseUrl, onSaved }: {
  path: PathData; apiBaseUrl: string; onSaved: (path: PathData) => void;
}) {
  const { t } = useTranslation();
  const defaultColor = useMasterData()?.paths[path.path_variant]?.default_color;
  const inputRef = useRef<HTMLInputElement>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => setError(null), [path.path_id]);

  const save = useCallback(async (color: string | null) => {
    setSaving(true); setError(null);
    try {
      const updated = await fetchApi<PathData>(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(path.path_id)), {
        method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ color }),
      });
      onSaved(updated);
      await sendSessionReload();
    } catch (e) { setError(describeError(e)); }
    finally { setSaving(false); }
  }, [apiBaseUrl, path.path_id, onSaved]);

  useEffect(() => {
    const input = inputRef.current;
    if (!input) return;
    const commit = () => {
      const color = input.value.toUpperCase();
      if (color !== path.color) void save(color);
    };
    input.addEventListener("change", commit);
    return () => input.removeEventListener("change", commit);
  }, [save, path.color]);

  if (path.color == null) return null;
  return <>
    <span className="agent-detail-label">{t("path.color")}</span>
    <span className="agent-detail-value">
      <span className="path-color-value">
        <input ref={inputRef} key={path.color} type="color" className="path-color-input" defaultValue={path.color.toLowerCase()}
          disabled={saving} aria-label={t("path.color")} />
        <button type="button" className="path-color-reset" disabled={saving || path.color === defaultColor}
          onClick={() => void save(null)}>{t("path.colorReset")}</button>
      </span>
      {error && <span className="zone-change-error" role="alert">{error}</span>}
    </span>
  </>;
}
