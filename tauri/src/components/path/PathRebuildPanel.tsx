/**
 * Confirmation screen for rebuilding an existing path as another variant of the same type while preserving its shape.
 * Shares construction-panel cards but does not enter Unity placement mode.
 * Validate and estimate each candidate with dry_run; the API validates again on confirmation.
 */
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";
import { PATH_TRANSPORT, PLAY_MODE_SCENARIO, STATUS_ACTIVE } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { PathData } from "../../types";
import { fetchApi } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { Money } from "../common/Money";
import { notifyRoadsChanged } from "../road/roadApi";
import { buildCategoryVariants, buildablePathMaster } from "./pathBuildCategories";
import { PATH_REBUILD_CHANGED_EVENT, requestPathRebuild, type PathRebuildEstimate } from "./pathRebuildApi";
import { PathVariantCard } from "./PathVariantCard";
import "./PathRebuildPanel.css";

type Props = { apiBaseUrl: string; initialEntityId: string; onClose: () => void };

export function PathRebuildPanel({ apiBaseUrl, initialEntityId, onClose }: Props) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const [path, setPath] = useState<PathData | null>(null);
  const [selectedVariant, setSelectedVariant] = useState<number | null>(null);
  const [preview, setPreview] = useState<PathRebuildEstimate | null>(null);
  const [checking, setChecking] = useState(false);
  const [saving, setSaving] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submitting = useRef(false);

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<PathData>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${initialEntityId}`, {
      signal: controller.signal,
    }).then(data => {
      if (!controller.signal.aborted) setPath(data);
    }).catch((err: unknown) => {
      if (!controller.signal.aborted) setError(err instanceof Error ? err.message : String(err));
    });
    return () => controller.abort();
  }, [apiBaseUrl, initialEntityId]);

  useEffect(() => {
    if (!path || selectedVariant == null) return;
    const controller = new AbortController();
    setChecking(true);
    setError(null);
    requestPathRebuild(apiBaseUrl, path.path_id, selectedVariant, true, controller.signal)
      .then(data => {
        if (!controller.signal.aborted) setPreview(data);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!controller.signal.aborted) setChecking(false);
      });
    return () => controller.abort();
  }, [apiBaseUrl, path, selectedVariant]);

  const variants = path ? buildCategoryVariants(buildablePathMaster(masterData, scenarioMode), path.path_type)
    .filter(({ id }) => id !== path.path_variant) : [];
  const current = path && masterData?.paths[path.path_variant];
  const target = selectedVariant != null ? masterData?.paths[selectedVariant] : null;
  const eligible = path?.status === STATUS_ACTIVE && path.path_type !== PATH_TRANSPORT;
  // Do not use the previous candidate's estimate for confirmation, even on the render immediately after selection.
  const estimate = preview?.rebuild_variant === selectedVariant ? preview : null;
  const canSubmit = eligible && target && estimate?.affordable && !checking && !saving && !submitted;

  async function submit() {
    if (!canSubmit || !path || selectedVariant == null || submitting.current) return;
    submitting.current = true;
    setSaving(true);
    setError(null);
    try {
      await requestPathRebuild(apiBaseUrl, path.path_id, selectedVariant, false);
      // A failed notification after acceptance must not allow the same work to be ordered again.
      setSubmitted(true);
      const results = await Promise.allSettled([
        emit(PATH_REBUILD_CHANGED_EVENT, { path_id: path.path_id }),
        sendSessionReload(),
        notifyRoadsChanged(undefined, "path_rebuild"),
      ]);
      if (results.some(result => result.status === "rejected")) {
        setError(t("path.rebuild.refreshFailed"));
      } else {
        onClose();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      submitting.current = false;
      setSaving(false);
    }
  }

  return <div className="path-rebuild">
    <div className="path-rebuild__body">
      {!path || !masterData ? (
        !error && <p role="status">{t("common.loading")}</p>
      ) : <>
        <div className="path-rebuild__comparison">
          <section>
            <h3>{t("path.rebuild.current")}</h3>
            {current && <PathVariantCard variantId={path.path_variant} entry={current} />}
          </section>
          <section>
            <label className="path-rebuild__label" htmlFor="rebuild-variant">{t("path.rebuild.target")}</label>
            <select id="rebuild-variant" value={selectedVariant ?? ""} disabled={!eligible || saving || submitted}
              data-guide="path.rebuild.variant"
              onChange={event => {
                setSelectedVariant(event.target.value === "" ? null : Number(event.target.value));
                setPreview(null);
                setError(null);
              }}>
              <option value="" disabled>{t("path.rebuild.select")}</option>
              {variants.map(({ id, data }) => <option key={id} value={id}>{data.display_name}</option>)}
            </select>
            {target && <PathVariantCard variantId={selectedVariant!} entry={target} />}
            {variants.length === 0 && <p>{t("path.rebuild.noVariants")}</p>}
          </section>
        </div>
        {!eligible && <p role="alert">{t("path.rebuild.unavailable")}</p>}
        {path.under_maintenance && <p className="path-rebuild__hint">{t("path.rebuild.repairNotice")}</p>}
        {selectedVariant != null && <ConstructionInfoCard
          estimatedCost={estimate?.estimated_construction_cost ?? null}
          standardCost={null}
          estimatedDurationDays={estimate?.estimated_duration_days ?? null}
          isCostLoading={checking}
          availableFunds={estimate?.available_funds ?? undefined}
          requiredTotal={estimate?.required_total}
          leadingRows={estimate ? [{
            label: t("construction.constructionCostPerEpoch"),
            value: <Money value={estimate.cost_per_simulation_days} unit />,
          }] : undefined}
        />}
        {estimate && (estimate.removable_parcel_ids.length > 0 || estimate.shavable_parcel_ids.length > 0) &&
          <p className="path-rebuild__hint">{t("path.rebuild.parcelNotice", {
            removed: estimate.removable_parcel_ids.length, shaved: estimate.shavable_parcel_ids.length,
          })}</p>}
      </>}
      {submitted && <p role="status">{t("path.rebuild.accepted")}</p>}
      {error && <p className="path-rebuild__error" role="alert">{error}</p>}
    </div>
    <div className="path-rebuild__actions">
      <button type="button" disabled={saving} onClick={onClose}>{t("common.close")}</button>
      {!submitted && <button type="button" className="path-rebuild__confirm" data-guide="path.rebuild.confirm"
        disabled={!canSubmit} onClick={() => void submit()}>
        {saving ? t("path.topology.submitting") : t("path.rebuild.confirm")}
      </button>}
    </div>
  </div>;
}
