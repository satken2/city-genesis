/**
 * Overlay covering the whole cockpit from epoch start until the simulation returns. Shows the stage the core is
 * processing and, for multi-epoch runs, which epoch it is in. Covering the panel also blocks its controls.
 */
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";

/** GET /api/v1/epochs/progress. step is empty while idle and before the first stage of an epoch. */
type EpochProgress = {
  index: number;
  count: number;
  step: string;
};

const PROGRESS_POLL_INTERVAL_MS = 300;

export function EpochProgressOverlay({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const [progress, setProgress] = useState<EpochProgress | null>(null);

  // The core answers this during the execution POST.
  useEffect(() => {
    if (!apiBaseUrl) return;
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    const refresh = async () => {
      try {
        const value = await fetchApi<EpochProgress>(buildApiUrl(apiBaseUrl, "/api/v1/epochs/progress"));
        if (!disposed) setProgress(value);
      } catch (e) {
        console.error("Failed to read epoch progress:", e);
      } finally {
        if (!disposed) timer = setTimeout(refresh, PROGRESS_POLL_INTERVAL_MS);
      }
    };
    void refresh();
    return () => { disposed = true; clearTimeout(timer); };
  }, [apiBaseUrl]);

  const stepLabel = progress?.step ? t(`epochProgress.steps.${progress.step}`) : t("epochConfirm.executing");
  const label = progress && progress.count > 1
    ? t("epochProgress.counter", { step: stepLabel, index: progress.index, count: progress.count })
    : stepLabel;

  return (
    <div className="cockpit-progress" role="status" aria-live="polite">
      <span className="cockpit-progress-spinner" aria-hidden="true" />
      <span className="cockpit-progress-text">{label}</span>
    </div>
  );
}
