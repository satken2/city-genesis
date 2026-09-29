/**
 * 公共交通ウィンドウ
 *
 * window_type="transit_management" の画面中央ウィンドウ。
 * 路線の一覧を路線種別のタブ(すべて + transit_master の各種別)で絞り込んで表示し、各路線の
 * 車両数・調達率・乗車回数・混雑率・運賃収入を確認できる。運賃と運行間隔はインラインで編集して
 * 保存でき、路線の新設・削除も行える。
 *
 * 種別タブは transit_master の並び(マスターID順)で、路線の path_variant を transit_path_id と
 * 突き合わせて振り分ける。路線が1本も無い種別もタブは出す(路線の増減でタブの位置が動かないように)。
 *
 * データは GET /api/v1/transit/lines から取得する。運賃/運行間隔の更新は
 * PATCH /api/v1/transit/lines/{id}、削除は DELETE /api/v1/transit/lines/{id}。
 * 新設は transit_creation ウィンドウを開いて行い、作成完了時に "transit:lines_changed"
 * イベントで本ウィンドウが一覧を再取得する。
 *
 * ※ Unity(グラフィック)側での停留所配置などの操作は本ウィンドウのスコープ外。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { setHintbarText } from "../../utils/hintbar";
import { sendViewApplyPreset, VIEW_PRESET_TRANSIT } from "../../utils/ipc";
import { resolveIntlLocale } from "../../utils/locale";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { requestConfirm } from "../../utils/confirmDialog";
import { useMasterData } from "../../contexts/MasterDataContext";
import { Money } from "../common/Money";
// 混雑率(稼働率)メーターは、統計ウィンドウの消防署/警察署の逼迫度メーターと同じ
// コンポーネントを再利用する。import により AccidentsTab.css(.usage-meter)も読み込まれる。
import { UsageMeter } from "../statistics/tabs/AccidentsTab";
import { STATUS_ACTIVE } from "../../constants";
import { resolveTransitColor } from "../../local-constants";
import { pauseReasonKey } from "./transitPauseReason";
import "./TransitManagementWindow.css";

/** GET /api/v1/transit/lines の1路線分のレスポンス型 */
type TransitLine = {
  line_agent_id: number;
  name: string | null;
  path_variant: number;
  status: number;
  fare_price: number;
  target_headway: number;
  // 路線カラー("#rrggbb")。空文字は未設定で種別ごとの既定色にフォールバックする。
  line_color: string;
  vehicle_count: number;
  required_vehicle_count: number;
  revenues: number;
  expenses: number;
  ridership: number;
  avg_utilization: number;
  path_count: number;
  // 運休理由コード(null / "route_disconnected" / "return_leg_blocked" / "no_underlay" / "train_too_long")。
  pause_reason: string | null;
};

/** 表示中のタブ。"all" 以外は路線種別(路線の path_variant = transit_master の transit_path_id) */
type TabKey = "all" | number;

type TabDef = {
  key: TabKey;
  label: string;
  /** 種別アイコン。「すべて」タブは持たない */
  icon: string | null;
  count: number;
};

/** 路線名。未命名の路線は ID で示す(路線詳細パネルのヘッダと同じ表記) */
function lineDisplayName(line: TransitLine): string {
  return line.name ? line.name : `#${line.line_agent_id}`;
}

export function TransitManagementWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);

  const [lines, setLines] = useState<TransitLine[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<TabKey>("all");
  // 路線ごとの編集中の運賃・運行間隔(line_agent_id をキーにする)
  const [edits, setEdits] = useState<Record<number, { fare: number; headway: number }>>({});
  const [savingId, setSavingId] = useState<number | null>(null);
  const [deletingId, setDeletingId] = useState<number | null>(null);

  useEffect(() => {
    setHintbarText(t("hint.transitManagement"));
    return () => {
      setHintbarText("");
    };
  }, [t]);

  // 一覧を見ている間は3Dビューを公共交通向けの表示(全路線+計画中の道路・建物)にする。
  // 元の表示へ戻すのはウィンドウを閉じたときにRust側が送る STATE_NORMAL なので、
  // ここでは戻す処理を持たない(×で閉じるとReactのアンマウントは走らないため)。
  useEffect(() => {
    void sendViewApplyPreset(VIEW_PRESET_TRANSIT, "transit_management").catch(console.error);
  }, []);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const data = await fetchApi<{ lines: TransitLine[] }>(
          buildApiUrl(apiBaseUrl, "/api/v1/transit/lines"),
          { signal },
        );
        if (signal?.aborted) return;
        setLines(data.lines);
        // 編集中の値を最新値で初期化する(未編集の行のみ)。
        setEdits((prev) => {
          const next = { ...prev };
          for (const line of data.lines) {
            if (next[line.line_agent_id] === undefined) {
              next[line.line_agent_id] = {
                fare: line.fare_price,
                headway: line.target_headway,
              };
            }
          }
          return next;
        });
        setError(null);
      } catch (err) {
        if (!signal?.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      }
    },
    [apiBaseUrl],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  // 新設ウィンドウで路線が作成/削除されたら一覧を再取得する。
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen("transit:lines_changed", () => {
      void load();
    })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, [load]);

  const handleEditChange = useCallback(
    (lineId: number, field: "fare" | "headway", value: number) => {
      setEdits((prev) => ({
        ...prev,
        [lineId]: {
          fare: field === "fare" ? value : prev[lineId]?.fare ?? 0,
          headway: field === "headway" ? value : prev[lineId]?.headway ?? 0,
        },
      }));
    },
    [],
  );

  const isDirty = useCallback(
    (line: TransitLine): boolean => {
      const edit = edits[line.line_agent_id];
      if (!edit) return false;
      return edit.fare !== line.fare_price || edit.headway !== line.target_headway;
    },
    [edits],
  );

  const handleSave = useCallback(
    async (line: TransitLine) => {
      const edit = edits[line.line_agent_id];
      if (!edit) return;
      setSavingId(line.line_agent_id);
      setError(null);
      try {
        const res = await fetch(
          buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${line.line_agent_id}`),
          {
            method: "PATCH",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ fare_price: edit.fare, target_headway: edit.headway }),
          },
        );
        if (!res.ok) {
          // 線路容量の事前検証で拒否された場合、バックエンドは理由を message に入れて返す。
          const body = await res.json().catch(() => null);
          throw new Error(body?.message ?? `HTTP ${res.status}`);
        }
        await load();
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to save");
      } finally {
        setSavingId(null);
      }
    },
    [apiBaseUrl, edits, load],
  );

  const handleDelete = useCallback(
    async (line: TransitLine) => {
      // 停留所・区間ごと消えて取り消せないため、路線詳細パネルの廃止と同じく確認を挟む。
      const confirmed = await requestConfirm({
        titleKey: "transit.deleteTitle",
        message: `${lineDisplayName(line)}\n${t("transit.deleteDescription")}`,
        confirmLabel: t("transit.deleteConfirm"),
        tone: "danger",
      });
      if (!confirmed) return;
      setDeletingId(line.line_agent_id);
      setError(null);
      try {
        const res = await fetch(
          buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${line.line_agent_id}`),
          { method: "DELETE" },
        );
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        // 削除した行の編集状態も破棄する。
        setEdits((prev) => {
          const next = { ...prev };
          delete next[line.line_agent_id];
          return next;
        });
        await load();
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to delete");
      } finally {
        setDeletingId(null);
      }
    },
    [apiBaseUrl, load, t],
  );

  // 管理ウィンドウは排他グループで閉じられ、路線新設ウィンドウはその再利用待ちWebViewに開くことがある。
  // ここで自分を閉じると、閉じる要求が新設ウィンドウに当たって一瞬で消えるので、閉じるのはRust側に任せる。
  const openCreation = useCallback(() => {
    void invoke("open_registered_window", { windowType: "transit_creation" });
  }, []);

  const openDetail = useCallback((lineAgentId: number) => {
    void invoke("open_registered_window", {
      windowType: "transit_detail",
      entityId: String(lineAgentId),
    });
  }, []);

  const tabs = useMemo<TabDef[]>(() => {
    const all = lines ?? [];
    const typeTabs = masterData
      ? Object.values(masterData.transit).map((entry) => ({
          key: entry.transit_path_id,
          label: entry.display_name,
          icon: resolveTransitIcon(entry.transit_path_id),
          count: all.filter((line) => line.path_variant === entry.transit_path_id).length,
        }))
      : [];
    return [{ key: "all", label: t("transit.tabAll"), icon: null, count: all.length }, ...typeTabs];
  }, [lines, masterData, t]);

  const visibleLines = useMemo(() => {
    if (!lines) return [];
    return activeTab === "all" ? lines : lines.filter((line) => line.path_variant === activeTab);
  }, [lines, activeTab]);

  if (error && !lines) {
    return <div className="transit-window__error">{error}</div>;
  }
  if (!lines) {
    return <div className="transit-window__loading">{t("common.loading")}</div>;
  }

  return (
    <div className="transit-window">
      {/* 左サイドバー: 路線新設ボタンと路線種別の縦並びタブ */}
      <aside className="transit-sidebar">
        <button className="transit-window__new-btn" type="button" data-guide="transit.new" onClick={openCreation}>
          <PlusIcon />
          <span>{t("transit.newLine")}</span>
        </button>
        <div className="transit-tabs" role="tablist" aria-orientation="vertical">
          {tabs.map((tab) => {
            const active = tab.key === activeTab;
            return (
              <button
                key={tab.key}
                type="button"
                role="tab"
                aria-selected={active}
                className={`transit-tab${active ? " transit-tab--active" : ""}`}
                onClick={() => setActiveTab(tab.key)}
              >
                {tab.icon ? (
                  <img src={tab.icon} alt="" className="transit-tab__icon" draggable={false} />
                ) : (
                  <AllLinesIcon />
                )}
                <span className="transit-tab__label">{tab.label}</span>
                <span className="transit-tab__count">{tab.count}</span>
              </button>
            );
          })}
        </div>
      </aside>

      {/* 右メインペイン: エラー表示と路線一覧テーブル */}
      <section className="transit-main">
        {error && (
          <div className="transit-window__error-inline" role="alert">
            {error}
          </div>
        )}

        <div className="transit-table-wrapper" role="tabpanel">
        {visibleLines.length === 0 ? (
          <div className="transit-window__empty">{t("transit.noLines")}</div>
        ) : (
          <table className="transit-table">
            <thead>
              <tr>
                <th>{t("transit.colName")}</th>
                <th className="transit-table__num">{t("transit.colVehicles")}</th>
                <th>{t("transit.colVehicleProcurement")}</th>
                <th className="transit-table__num">{t("transit.colRidership")}</th>
                <th>{t("transit.colUtilization")}</th>
                <th className="transit-table__num">{t("transit.colRevenues")}</th>
                <th className="transit-table__num">{t("transit.colFare")}</th>
                <th className="transit-table__num">{t("transit.colHeadway")}</th>
                <th aria-hidden="true" />
              </tr>
            </thead>
            <tbody>
              {visibleLines.map((line) => {
                const edit = edits[line.line_agent_id] ?? {
                  fare: line.fare_price,
                  headway: line.target_headway,
                };
                const dirty = isDirty(line);
                const saving = savingId === line.line_agent_id;
                const deleting = deletingId === line.line_agent_id;
                // 運行状態は路線エージェントの status で判定する(STATUS_ACTIVE=運行中)。
                // 車両不足は別途「調達率」列で示すため、車両0でも運休扱いにはしない。
                const running = line.status === STATUS_ACTIVE;
                const procurementRatio =
                  line.required_vehicle_count > 0
                    ? line.vehicle_count / line.required_vehicle_count
                    : null;
                const displayName = lineDisplayName(line);
                return (
                  <tr key={line.line_agent_id}>
                    <td>
                      <div className={`transit-line${running ? "" : " transit-line--suspended"}`}>
                        {/* 路線カラーの縦帯。3D の路線色帯・詳細パネルの帯と同じ色で路線を見分ける */}
                        <span
                          className="transit-line__stripe"
                          style={{ background: line.line_color || resolveTransitColor(line.path_variant) }}
                          aria-hidden="true"
                        />
                        <img
                          src={resolveTransitIcon(line.path_variant)}
                          alt=""
                          className="transit-line__icon"
                          draggable={false}
                        />
                        <button
                          type="button"
                          className="transit-line__name entity-link"
                          title={displayName}
                          onClick={() => openDetail(line.line_agent_id)}
                        >
                          {displayName}
                        </button>
                        {/* 運行中が平常なので印は運休にだけ付け、理由はツールチップで示す
                            (詳細を開かなくても原因が分かるように) */}
                        {!running && (
                          <span
                            className="transit-badge transit-badge--suspended"
                            title={t(pauseReasonKey(line.pause_reason))}
                          >
                            {t("transit.suspended")}
                          </span>
                        )}
                      </div>
                    </td>
                    <td className="transit-table__num">
                      {Math.round(line.vehicle_count)}
                      {procurementRatio !== null && (
                        <span className="transit-table__sub"> / {Math.round(line.required_vehicle_count)}</span>
                      )}
                    </td>
                    <td>
                      <div className="transit-table__meter">
                        {procurementRatio === null ? (
                          <span className="transit-table__sub">—</span>
                        ) : (
                          <UsageMeter usage_ratio={procurementRatio} goodHigh />
                        )}
                      </div>
                    </td>
                    <td className="transit-table__num">{Math.round(line.ridership).toLocaleString(locale)}</td>
                    <td>
                      <div className="transit-table__meter">
                        <UsageMeter usage_ratio={line.avg_utilization} />
                      </div>
                    </td>
                    <td className="transit-table__num">
                      <Money value={Math.round(line.revenues)} />
                    </td>
                    <td className="transit-table__num">
                      <input
                        type="number"
                        className={`transit-table__input${edit.fare !== line.fare_price ? " transit-table__input--dirty" : ""}`}
                        value={Math.round(edit.fare)}
                        min={0}
                        step={10}
                        onChange={(e) =>
                          handleEditChange(line.line_agent_id, "fare", Math.max(0, parseInt(e.target.value) || 0))
                        }
                        onKeyDown={(e) => {
                          if (e.key === "Enter" && dirty && !saving) void handleSave(line);
                        }}
                      />
                    </td>
                    <td className="transit-table__num">
                      <input
                        type="number"
                        className={`transit-table__input transit-table__input--short${edit.headway !== line.target_headway ? " transit-table__input--dirty" : ""}`}
                        value={Math.round(edit.headway)}
                        min={1}
                        step={1}
                        onChange={(e) =>
                          handleEditChange(line.line_agent_id, "headway", Math.max(1, parseInt(e.target.value) || 1))
                        }
                        onKeyDown={(e) => {
                          if (e.key === "Enter" && dirty && !saving) void handleSave(line);
                        }}
                      />
                    </td>
                    <td>
                      <div className="transit-table__actions">
                        <button
                          type="button"
                          className={`transit-icon-btn transit-icon-btn--save${saving ? " transit-icon-btn--busy" : ""}`}
                          title={t("transit.save")}
                          aria-label={t("transit.save")}
                          disabled={!dirty || saving}
                          onClick={() => void handleSave(line)}
                        >
                          <SaveIcon />
                        </button>
                        <button
                          type="button"
                          className={`transit-icon-btn transit-icon-btn--delete${deleting ? " transit-icon-btn--busy" : ""}`}
                          title={t("transit.delete")}
                          aria-label={t("transit.delete")}
                          disabled={deleting}
                          onClick={() => void handleDelete(line)}
                        >
                          <DeleteIcon />
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
      </section>
    </div>
  );
}

/** 路線新設ボタンの「+」アイコン。インラインSVGでボタンの文字色に追従 */
function PlusIcon() {
  return (
    <svg
      viewBox="0 0 16 16"
      width="14"
      height="14"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <line x1="8" y1="3" x2="8" y2="13" />
      <line x1="3" y1="8" x2="13" y2="8" />
    </svg>
  );
}

/** 「すべて」タブ用のアイコン。全路線のリスト一覧を表すインラインSVG */
function AllLinesIcon() {
  return (
    <svg
      className="transit-tab__icon transit-tab__icon--all"
      viewBox="0 0 20 20"
      fill="currentColor"
      aria-hidden="true"
    >
      <rect x="2.5" y="4" width="15" height="2" rx="1" />
      <rect x="2.5" y="9" width="15" height="2" rx="1" />
      <rect x="2.5" y="14" width="15" height="2" rx="1" />
    </svg>
  );
}

/** 保存ボタンのチェック印。専用画像が無いためインラインSVGで描き、ボタンの文字色に追従させる */
function SaveIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true">
      <path
        d="M3 8.5l3.2 3.2L13 4.8"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

/** 削除ボタンのごみ箱。SaveIcon と同じくインラインSVG */
function DeleteIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true">
      <path
        d="M2.5 4.5h11M6.2 4.5V2.8h3.6v1.7M4 4.5l.8 9h6.4l.8-9M6.8 7v4M9.2 7v4"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

