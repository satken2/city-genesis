import { describeError } from "../../../utils/errors";
/**
 * Natural resources tab component.
 *
 * Content of the statistics window's "Natural resources" tab.
 * Lists the city's natural resources as wide cards. Each card shows a resource icon, name,
 * remaining quantity, and remaining-percentage meter, and opens that resource's details popup
 * when clicked.
 *
 * Data fetching:
 * - GET /api/v1/resources retrieves the resource list.
 * - Resource proportion is calculated as resource_amount / max_amount.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { buildApiUrl, fetchApi } from "../../../utils/api";
import { ResourceCard } from "../../common/ResourceCard";
import "./NaturalResourcesTab.css";

/** List element from GET /api/v1/resources. */
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
        setError(describeError(err));
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  /** Opens a details panel when a resource card is clicked. */
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
