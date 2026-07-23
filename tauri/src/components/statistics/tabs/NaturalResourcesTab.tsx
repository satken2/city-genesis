/**
 * 天然資源タブコンポーネント
 *
 * 統計ウィンドウの「天然資源」タブの中身。
 * 都市内の天然資源を横長カードのリストとして一覧表示する。
 * カードには資源アイコン・資源名・現在残量・残量率メーターを表示し、
 * クリックすると該当資源の詳細パネルポップアップが開く。
 *
 * データ取得:
 * - GET /api/v1/resources で資源一覧を取得する。
 * - 資源量の割合は resource_amount / max_amount で計算する。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { buildApiUrl, fetchApi } from "../../../utils/api";
import { ResourceCard } from "../../common/ResourceCard";
import "./NaturalResourcesTab.css";

/** GET /api/v1/resources の一覧要素 */
type ResourceListItem = {
  resource_id: number;
  product_id: number;
  max_amount: number;
  resource_amount: number;
  recovery_rate: number;
};

export function NaturalResourcesTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();

  const [resources, setResources] = useState<ResourceListItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ resources: ResourceListItem[] }>(
      buildApiUrl(apiBaseUrl, "/api/v1/resources"),
      { signal: controller.signal }
    )
      .then((res) => {
        if (controller.signal.aborted) return;
        setResources(res.resources);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setError(err instanceof Error ? err.message : "Unknown error");
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  /** 資源カードをクリックして詳細パネルを開く */
  function openDetail(resourceId: number) {
    void invoke("open_registered_window", {
      windowType: "resource_detail",
      entityId: String(resourceId),
    });
  }

  if (error) {
    return (
      <div className="resource-tab__error">
        {t("common.fetchError")}: {error}
      </div>
    );
  }

  if (!resources) {
    return <div className="resource-tab__loading">{t("common.loading")}</div>;
  }

  if (resources.length === 0) {
    return <div className="resource-tab__empty">{t("statistics.noResources")}</div>;
  }

  return (
    <div className="resource-tab">
      <ul className="resource-tab__list">
        {resources.map((r) => (
          <li key={r.resource_id}>
            <ResourceCard
              resourceId={r.resource_id}
              productId={r.product_id}
              maxAmount={r.max_amount}
              resourceAmount={r.resource_amount}
              onClick={openDetail}
            />
          </li>
        ))}
      </ul>
    </div>
  );
}
