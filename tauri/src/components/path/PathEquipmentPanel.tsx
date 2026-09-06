import { Fragment, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { PathData } from "../../types";
import { fetchApi, buildApiUrl } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";
import { PATH_EQUIPMENT_NONE, PATH_EQUIPMENT_STANDARD, PATH_UTILITY_UNDERGROUND } from "../../constants";

import "./PathDetailPanel.css";
import "./PathTrafficPanel.css";

export const pathEquipmentFields = ["streetlight_type", "fence_type", "street_tree_type", "utility_mode"] as const;
type Equipment = Pick<PathData, typeof pathEquipmentFields[number]>;
const selection = (path: PathData): Equipment => ({
  streetlight_type: path.streetlight_type,
  fence_type: path.fence_type,
  street_tree_type: path.street_tree_type,
  utility_mode: path.utility_mode,
});

export function PathEquipmentPanel({ path, apiBaseUrl, onSaved }: {
  path: PathData; apiBaseUrl: string; onSaved: (path: PathData) => void;
}) {
  const { t } = useTranslation();
  const master = useMasterData()?.paths[path.path_variant];
  const [draft, setDraft] = useState<Equipment>(() => selection(path));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setDraft(selection(path)); setError(null); }, [path]);
  const available = pathEquipmentFields.filter(field => master?.[`${field}_allowed`] || path[field] !== PATH_EQUIPMENT_NONE);
  if (!available.length) return null;
  const dirty = pathEquipmentFields.some(field => draft[field] !== path[field]);
  async function save() {
    setSaving(true); setError(null);
    try {
      const updated = await fetchApi<PathData>(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(path.path_id)), {
        method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(draft),
      });
      onSaved(updated);
      await sendSessionReload();
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  }
  return <section className="agent-detail-section path-equipment-section">
    <h3 className="agent-detail-section-title">{t("pathEquipment.title")}</h3>
    <div className="agent-detail-grid">
      {available.map(field => <Fragment key={field}>
        <label className="agent-detail-label" htmlFor={`path-equipment-${field}`}>{t(`pathEquipment.${field}`)}</label>
        <div className="agent-detail-value">
          <select id={`path-equipment-${field}`} className="zone-change-select" value={draft[field]} disabled={saving} onChange={e => setDraft(prev => ({ ...prev, [field]: Number(e.target.value) }))}>
            <option value={PATH_EQUIPMENT_NONE}>{t("pathEquipment.none")}</option>
            {master?.[`${field}_allowed`] && <option value={PATH_EQUIPMENT_STANDARD}>{t(field === "utility_mode" ? "pathEquipment.overhead" : "pathEquipment.standard")}</option>}
            {field === "utility_mode" && master?.utility_mode_allowed && <option value={PATH_UTILITY_UNDERGROUND}>{t("pathEquipment.underground")}</option>}
          </select>
        </div>
      </Fragment>)}
    </div>
    <div className="path-save-row">
      {error && <span className="path-save-error" role="alert">{error}</span>}
      <button className="path-save-btn" type="button" disabled={saving || !dirty} onClick={() => void save()}>{t("pathEquipment.apply")}</button>
    </div>
  </section>;
}
