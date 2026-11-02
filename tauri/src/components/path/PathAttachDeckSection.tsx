/**
 * Section for building a lower deck under an existing path afterwards, opened from the path operation buttons.
 *
 * The request carries only the variant and the host path ID (attach_host_path_id). The core rebuilds the deck from
 * the host's current shape, creates it as a new construction (with the same funding check as new placement), and
 * links the pair.
 */
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { fetchApi } from "../../utils/api";
import { describeError } from "../../utils/errors";
import { sendSessionReload } from "../../utils/ipc";
import { useMasterData } from "../../contexts/MasterDataContext";
import { notifyRoadsChanged } from "../road/roadApi";
import "./PathConstructionPanel.css";
import "./PathTopologyEditPanel.css";

export function PathAttachDeckSection({ apiBaseUrl, hostPathId, choices, onClose, onCompleted }: {
  apiBaseUrl: string;
  hostPathId: number;
  choices: number[];
  onClose: () => void;
  onCompleted: () => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [variant, setVariant] = useState(choices[0]);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleConfirm() {
    setSubmitting(true);
    setError(null);
    try {
      await fetchApi(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          paths: [{ path_variant: variant, path_temp_id: "attached", attach_host_path_id: hostPathId }],
        }),
      });
      await sendSessionReload();
      await notifyRoadsChanged(undefined, "path_detail");
      onCompleted();
    } catch (err) {
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className="path-topology-section">
      <h3 className="path-topology-title">{t("path.action.attachDeck")}</h3>
      <label className="pc-settings-row">
        <span className="pc-settings-label">{t("path_construction.attachedVariant")}</span>
        <select className="pc-select" value={variant} disabled={submitting}
          onChange={(event) => setVariant(Number(event.target.value))}>
          {choices.map((id) => (
            <option key={id} value={id}>{masterData?.paths[id]?.display_name ?? id}</option>
          ))}
        </select>
      </label>
      {error && <div className="path-topology-error">{error}</div>}
      <div className="path-topology-actions">
        <button className="path-topology-btn path-topology-btn--confirm" type="button"
          data-guide="path.attachDeck.confirm" disabled={submitting} onClick={() => void handleConfirm()}>
          {t("path.attachDeck.confirm")}
        </button>
        <button className="path-topology-btn" type="button" disabled={submitting} onClick={onClose}>
          {t("path.attachDeck.cancel")}
        </button>
      </div>
    </section>
  );
}
