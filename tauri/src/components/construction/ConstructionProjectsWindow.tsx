/**
 * Construction projects window
 *
 * Group construction work by tags attached to buildings and roads (= construction projects), showing total
 * estimated construction cost and construction time (maximum remaining epochs).
 *
 * - Data comes from GET /api/v1/tags/projects. The backend aggregates remaining project costs and time; this
 *   panel only displays them, avoiding a second implementation of cost formulas.
 * - Construction time arrives in epochs, an internal unit hidden from players. Multiply by days per epoch
 *   (`simulation_days`), then divide by 30 days per month to display months.
 * - Select tags when placing construction in its panel, or add/remove them in detail panels before or during
 *   construction (`common/TagSelect`). Completed work retains its tags and appears as completed rows.
 *   This window handles viewing, visibility toggles, and tag deletion only.
 * - Visibility has two levels: per tag (heading eye icon) and per row (row eye icon). Unity keeps this state
 *   within the session (`useObjectVisibility`); it is not saved in the backend.
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";

import { DAYS_PER_MONTH } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { useObjectVisibility } from "../../hooks/useObjectVisibility";
import { buildApiUrl, fetchApi } from "../../utils/api";
import {
  TAGS_CHANGED_EVENT,
  deleteTag,
  fetchTagProjects,
  type TagProject,
  type TagProjectItem,
} from "../../utils/constructionTags";
import { formatCompactAmount, formatExactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";
import { requestConfirm } from "../../utils/confirmDialog";
import { StatusBadge } from "../common/StatusBadge";
import "./ConstructionProjectsWindow.css";

/** Window type, also used as the sender label for IPC to Unity. */
const WINDOW_TYPE = "construction_projects";

type ConstructionProjectsWindowProps = {
  apiBaseUrl: string;
};

export function ConstructionProjectsWindow({ apiBaseUrl }: ConstructionProjectsWindowProps) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);
  const [projects, setProjects] = useState<TagProject[] | null>(null);
  const [simulationDays, setSimulationDays] = useState(0);
  const [loadFailed, setLoadFailed] = useState(false);
  const visibility = useObjectVisibility(WINDOW_TYPE);

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      try {
        // Popups may survive city changes, so refetch days per epoch along with the list.
        const [list, cityRes] = await Promise.all([
          fetchTagProjects(apiBaseUrl, signal),
          fetchApi<{ city: { simulation_days: number } }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal }),
        ]);
        if (signal?.aborted) return;
        setProjects(list);
        setSimulationDays(cityRes.city.simulation_days);
        setLoadFailed(false);
      } catch (err) {
        if (!signal?.aborted) {
          console.error("failed to load construction projects", err);
          setLoadFailed(true);
        }
      }
    },
    [apiBaseUrl],
  );

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    void reload(controller.signal);
    return () => controller.abort();
  }, [apiBaseUrl, reload]);

  // Refetch when tags change in detail or construction panels, using events across windows.
  // Also refetch after an epoch completes, since remaining time, costs, and completion counts advance.
  useEffect(() => {
    const unlistenTags = listen(TAGS_CHANGED_EVENT, () => {
      void reload();
    });
    const unlistenEpoch = listen<{ executing: boolean }>("epoch:executing", (event) => {
      if (!event.payload.executing) void reload();
    });
    return () => {
      void unlistenTags.then((unlisten) => unlisten());
      void unlistenEpoch.then((unlisten) => unlisten());
    };
  }, [reload]);

  /** Open the detail window for the construction target (building or road). */
  async function openItemDetail(item: TagProjectItem) {
    try {
      await invoke("open_registered_window", {
        windowType: item.kind === "path" ? "path_detail" : "agent_detail",
        entityId: String(item.id),
      });
    } catch (error) {
      console.error("failed to open item detail", error);
    }
  }

  /** Delete a tag, also removing it from tagged buildings and roads. */
  async function handleDeleteTag(tag: string) {
    const confirmed = await requestConfirm({
      titleKey: "constructionProjects.deleteTag",
      message: t("constructionProjects.confirmDeleteTag", { tag }),
      confirmLabel: t("common.delete"),
      tone: "danger",
    });
    if (!confirmed) return;
    try {
      await deleteTag(apiBaseUrl, tag);
      await emit(TAGS_CHANGED_EVENT, { tag });
      await reload();
    } catch (error) {
      console.error("failed to delete tag", error);
    }
  }

  /** Construction type name (variant display name). Leave blank until master data is available. */
  function variantName(item: TagProjectItem): string {
    if (!masterData) return "";
    const entry = item.kind === "path"
      ? masterData.paths?.[item.variant]
      : masterData.agents?.[item.variant];
    return (entry as { display_name?: string } | undefined)?.display_name ?? "";
  }

  /** Display remaining epochs as months. null means construction cannot finish under current conditions. */
  function formatRemaining(epochs: number | null): string {
    if (epochs === null) return t("constructionProjects.stalled");
    const months = (epochs * simulationDays) / DAYS_PER_MONTH;
    return t("constructionProjects.monthsValue", { value: months.toFixed(1) });
  }

  function renderProject(project: TagProject) {
    const targets = project.items.map((item) => ({ kind: item.kind, id: item.id }));
    // Toggle to Hide All if any item is visible, or Show All if all are hidden.
    const allHidden = targets.every((target) => visibility.isHidden(target.kind, target.id));

    return (
      <section className="cp-project" key={project.tag}>
        <header className="cp-project-header">
          <button
            type="button"
            className={`cp-eye${allHidden ? " cp-eye--hidden" : ""}`}
            title={allHidden ? t("constructionProjects.showTag") : t("constructionProjects.hideTag")}
            onClick={() => visibility.setObjectsHidden(targets, !allHidden)}
          >
            {allHidden ? "🚫" : "👁"}
          </button>
          <h3 className="cp-project-title">{project.tag}</h3>
          <dl className="cp-project-summary">
            <div className="cp-summary-item">
              <dt>{t("constructionProjects.totalCost")}</dt>
              <dd title={formatExactAmount(project.total_remaining_cost, locale, true)}>
                {formatCompactAmount(project.total_remaining_cost, locale, true)}
              </dd>
            </div>
            <div className="cp-summary-item">
              <dt>{t("constructionProjects.duration")}</dt>
              <dd>{formatRemaining(project.in_progress_count > 0 ? project.max_remaining_epochs : 0)}</dd>
            </div>
            <div className="cp-summary-item">
              <dt>{t("constructionProjects.progress")}</dt>
              <dd>
                {t("constructionProjects.progressValue", {
                  done: project.completed_count,
                  total: project.items.length,
                })}
              </dd>
            </div>
          </dl>
          <button
            type="button"
            className="cp-delete-btn"
            title={t("constructionProjects.deleteTag")}
            onClick={() => void handleDeleteTag(project.tag)}
          >
            ×
          </button>
        </header>

        {project.stalled_count > 0 && (
          <p className="cp-warning">
            {t("constructionProjects.stalledWarning", { count: project.stalled_count })}
          </p>
        )}

        <table className="cp-table">
          <thead>
            <tr>
              <th className="cp-col-eye" />
              <th className="cp-col-name">{t("constructionProjects.columns.name")}</th>
              <th className="cp-col-status">{t("constructionProjects.columns.status")}</th>
              <th className="cp-col-cost">{t("constructionProjects.columns.cost")}</th>
              <th className="cp-col-epochs">{t("constructionProjects.columns.remaining")}</th>
            </tr>
          </thead>
          <tbody>
            {project.items.map((item) => {
              const hidden = visibility.isHidden(item.kind, item.id);
              return (
                <tr
                  key={`${item.kind}-${item.id}`}
                  className={`${item.completed ? "cp-row--done" : ""}${hidden ? " cp-row--hidden" : ""}`}
                >
                  <td className="cp-col-eye">
                    <button
                      type="button"
                      className={`cp-eye${hidden ? " cp-eye--hidden" : ""}`}
                      title={hidden ? t("constructionProjects.showObject") : t("constructionProjects.hideObject")}
                      onClick={() => visibility.toggleObject(item.kind, item.id)}
                    >
                      {hidden ? "🚫" : "👁"}
                    </button>
                  </td>
                  <td className="cp-col-name">
                    <button
                      type="button"
                      className="entity-link"
                      onClick={() => void openItemDetail(item)}
                    >
                      {item.name || t("constructionProjects.unnamed", { id: item.id })}
                    </button>
                    <span className="cp-variant">{variantName(item)}</span>
                  </td>
                  <td className="cp-col-status">
                    <StatusBadge status={item.status} />
                  </td>
                  <td className="cp-col-cost" title={formatExactAmount(item.remaining_cost, locale, true)}>
                    {item.completed ? "-" : formatCompactAmount(item.remaining_cost, locale, true)}
                  </td>
                  <td className="cp-col-epochs">
                    {item.completed ? t("constructionProjects.done") : formatRemaining(item.remaining_epochs)}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </section>
    );
  }

  return (
    <div className="cp-root">
      {visibility.hiddenCount > 0 && (
        <div className="cp-toolbar">
          <button type="button" className="cp-showall-btn" onClick={visibility.showAll}>
            {t("constructionProjects.showAll", { count: visibility.hiddenCount })}
          </button>
        </div>
      )}

      {projects === null ? (
        <p className="cp-empty">{loadFailed ? t("constructionProjects.loadFailed") : "…"}</p>
      ) : projects.length === 0 ? (
        <p className="cp-empty">{t("constructionProjects.empty")}</p>
      ) : (
        projects.map(renderProject)
      )}
    </div>
  );
}
