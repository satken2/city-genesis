/**
 * 建設プロジェクトウィンドウ
 *
 * 建物・道路に付けたタグ(=建設プロジェクト)ごとに、構成する工事を表にまとめ、
 * 建設費の見込み額合計と工期(残りエポックの最大値)を出す。
 *
 * - データ源は GET /api/v1/tags/projects。集計(残り事業費・残り工期)はバックエンドが行い、
 *   ここでは並べて表示するだけ(建設費の算式をUI側に二重実装しない)。
 * - タグの付け外しは各エンティティの詳細パネル(`common/TagSelect`)で行う。ここは閲覧と
 *   表示・非表示の切り替えに徹する。
 * - 表示・非表示はタグ単位(見出しの目アイコン)と行単位(各行の目アイコン)の2粒度。
 *   状態はバックエンドに保存されずUnityがセッション内で持つ(`useObjectVisibility`)。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";

import { useMasterData } from "../../contexts/MasterDataContext";
import { useObjectVisibility } from "../../hooks/useObjectVisibility";
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

/** このウィンドウのウィンドウタイプ。Unityへ送るIPCの送信元ラベルにも使う。 */
const WINDOW_TYPE = "construction_projects";

type ConstructionProjectsWindowProps = {
  apiBaseUrl: string;
};

export function ConstructionProjectsWindow({ apiBaseUrl }: ConstructionProjectsWindowProps) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);
  const [projects, setProjects] = useState<TagProject[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const visibility = useObjectVisibility(WINDOW_TYPE);

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const list = await fetchTagProjects(apiBaseUrl, signal);
        if (signal?.aborted) return;
        setProjects(list);
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

  // 詳細パネルでタグが付け替えられたら一覧を取り直す(ウィンドウをまたぐのでイベントで受ける)。
  useEffect(() => {
    const unlistenPromise = listen(TAGS_CHANGED_EVENT, () => {
      void reload();
    });
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [reload]);

  /** 工事の対象(建物・道路)の詳細ウィンドウを開く。 */
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

  /** タグを削除する(付いている建物・道路からも外れる)。 */
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

  /** 工事の種別名(バリアントの表示名)。マスター未取得時は空欄にする。 */
  function variantName(item: TagProjectItem): string {
    if (!masterData) return "";
    const entry = item.kind === "path"
      ? masterData.paths?.[item.variant]
      : masterData.agents?.[item.variant];
    return (entry as { display_name?: string } | undefined)?.display_name ?? "";
  }

  /** 残りエポック数の表示。null=このままでは終わらない工事。 */
  function formatEpochs(value: number | null): string {
    if (value === null) return t("constructionProjects.stalled");
    return t("constructionProjects.epochsValue", { value: value.toFixed(1) });
  }

  function renderProject(project: TagProject) {
    const targets = project.items.map((item) => ({ kind: item.kind, id: item.id }));
    // 1件でも表示中のものがあれば「全部隠す」、全部隠れていれば「全部出す」トグルにする。
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
              <dd>{formatEpochs(project.in_progress_count > 0 ? project.max_remaining_epochs : 0)}</dd>
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
                    {item.completed ? t("constructionProjects.done") : formatEpochs(item.remaining_epochs)}
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
      <div className="cp-toolbar">
        <p className="cp-description">{t("constructionProjects.description")}</p>
        {visibility.hiddenCount > 0 && (
          <button type="button" className="cp-showall-btn" onClick={visibility.showAll}>
            {t("constructionProjects.showAll", { count: visibility.hiddenCount })}
          </button>
        )}
      </div>

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
