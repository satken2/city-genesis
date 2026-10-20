/**
 * Interchange (IC/JCT) detail panel.
 *
 * A dedicated panel displayed in a popup window with window_type="intersection_detail".
 * Interchanges are automatically detected by the backend from groups of ramps; players do not create them as entities.
 * Clicking a label in the 3D view opens the name, type, and a connection diagram showing which roads can reach which others.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { PATH_MOTORWAY } from "../../constants";
import type { InterchangeArmDetail, InterchangeResponse, InterchangeSummary } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolvePathIcon } from "../../utils/pathIcon";
import { InfoHeader } from "../common/InfoHeader";
import { InterchangeConnectionDiagram } from "./InterchangeConnectionDiagram";
import { interchangeDisplayName, interchangeKindLabel } from "./interchangeName";

type IntersectionDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

export function IntersectionDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: IntersectionDetailPanelProps) {
  const { t } = useTranslation();
  const [interchange, setInterchange] = useState<InterchangeSummary | null>(null);
  const [arms, setArms] = useState<InterchangeArmDetail[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!initialEntityId) return;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchApi<InterchangeResponse>(buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId), {
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted) {
          setInterchange(data.interchange);
          setArms(data.arms ?? []);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [apiBaseUrl, pathTemplate, initialEntityId]);

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !interchange) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={resolvePathIcon(PATH_MOTORWAY)}
        variantName={interchangeKindLabel(interchange.kind, t)}
        name={interchangeDisplayName(interchange, t)}
        showStatus={false}
      />
      <section className="agent-detail-section">
        <InterchangeConnectionDiagram arms={arms} connections={interchange.connections ?? []} />
      </section>
    </div>
  );
}
