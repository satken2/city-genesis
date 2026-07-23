/**
 * パス建設工事情報コンポーネント
 *
 * パスが計画中、建設中、解体中などのステータスの際に詳細パネルに表示する。
 * 推定建設コスト・標準コストに対する倍率・推定建設期間を表示する。
 * 建設中・解体中の場合は、1エポックあたりのコストや残りエポック数も取得して表示する。
 * 表示本体は建物詳細と共通の ConstructionInfoCard を用いる。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useConstructionBudgetRow } from "../../hooks/useConstructionBudgetRow";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import {
  STATUS_CONSTRUCTING,
  STATUS_DEMOLISHING,
} from "../../constants";

type PathConstructionInfoPanelProps = {
  path: PathData;
  apiBaseUrl: string;
  refreshKey: number;
};

export function PathConstructionInfoPanel({
  path,
  apiBaseUrl,
  refreshKey,
}: PathConstructionInfoPanelProps) {
  const { t } = useTranslation();
  const [costData, setCostData] = useState<{
    estimated_construction_cost: number | null;
    standard_construction_cost: number | null;
    estimated_duration_days: number | null;
  } | null>(null);
  const [loading, setLoading] = useState(false);

  // 推定建設コストの取得 (GET /api/v1/paths/{path_id}/construction-cost)
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);

    const url = buildApiUrl(apiBaseUrl, `/api/v1/paths/${path.path_id}/construction-cost`);
    fetchApi<{
      estimated_construction_cost: number | null;
      standard_construction_cost: number | null;
      estimated_duration_days: number | null;
    }>(url, { signal: controller.signal })
      .then((data) => {
        if (!controller.signal.aborted) {
          setCostData({
            estimated_construction_cost: data.estimated_construction_cost ?? null,
            standard_construction_cost: data.standard_construction_cost ?? null,
            estimated_duration_days: data.estimated_duration_days ?? null,
          });
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("Failed to fetch path construction cost:", err);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      });

    return () => controller.abort();
  }, [path.path_id, apiBaseUrl, refreshKey]);

  // 建設中・解体中のみ、1エポックのコストと残り建設期間を取得する
  const isCsOrDm = path.status === STATUS_CONSTRUCTING || path.status === STATUS_DEMOLISHING;
  const { costPerEpoch, remainingEpochs } = useConstructionBudgetRow(
    apiBaseUrl,
    "path",
    path.path_id,
    isCsOrDm,
    refreshKey,
  );

  return (
    <section className="agent-detail-section" style={{ borderTop: "1px solid var(--border)", paddingTop: 12 }}>
      <h3 className="agent-detail-section-title">{t("path.tabConstructionInfo", "建設工事")}</h3>
      <ConstructionInfoCard
        estimatedCost={costData?.estimated_construction_cost ?? null}
        standardCost={costData?.standard_construction_cost ?? null}
        costRatioHelp={t("path_construction.costRatioHelp")}
        estimatedDurationDays={costData?.estimated_duration_days ?? null}
        isCostLoading={loading}
        perEpoch={
          isCsOrDm
            ? {
                costPerEpoch,
                remainingEpochs,
                isDemolishing: path.status === STATUS_DEMOLISHING,
              }
            : undefined
        }
      />
    </section>
  );
}
