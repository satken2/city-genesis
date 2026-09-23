/**
 * インターチェンジ(IC/JCT)詳細パネル
 *
 * window_type="intersection_detail" のポップアップウィンドウで表示する専用パネル。
 * インターチェンジはバックエンドがランプ群から自動検出するもので、プレイヤーが作る実体ではない。
 * 3Dビューのラベルクリックで開き、名前と種別を表示する。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { PATH_MOTORWAY } from "../../constants";
import type { InterchangeResponse, InterchangeSummary } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolvePathIcon } from "../../utils/pathIcon";
import { InfoHeader } from "../common/InfoHeader";
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
    </div>
  );
}
