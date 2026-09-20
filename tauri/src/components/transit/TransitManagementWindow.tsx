/**
 * 公共交通ウィンドウ
 *
 * window_type="transit_management" の画面中央ウィンドウ。
 * 現在稼働中の公共交通機関(路線)の一覧を表形式で表示し、各路線の
 * 収支・稼働率(混雑率)・乗車数・車両数・運賃・運行間隔を確認できる。
 * 運賃と運行間隔はインラインで編集して保存でき、路線の新設・削除も行える。
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
import { resolveTransitIcon } from "../../utils/pathIcon";
// 混雑率(稼働率)メーターは、統計ウィンドウの消防署/警察署の逼迫度メーターと同じ
// コンポーネントを再利用する。import により AccidentsTab.css(.usage-meter)も読み込まれる。
import { UsageMeter } from "../statistics/tabs/AccidentsTab";
import { STATUS_ACTIVE } from "../../constants";
import { resolveTransitColor } from "../../local-constants";
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
  // 運休理由コード(null / "route_disconnected" / "return_leg_blocked" / "no_underlay")。
  pause_reason: string | null;
};

// 運休理由コード → i18n キー。未知コードは汎用文言にフォールバックする。
const PAUSE_REASON_KEYS: Record<string, string> = {
  return_leg_blocked: "transit.pauseReason.returnLegBlocked",
  route_disconnected: "transit.pauseReason.routeDisconnected",
  no_underlay: "transit.pauseReason.noUnderlay",
};

export function TransitManagementWindow({
  apiBaseUrl,
  onClose,
}: {
  apiBaseUrl: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();

  const [lines, setLines] = useState<TransitLine[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  // 路線ごとの編集中の運賃・運行間隔(line_agent_id をキーにする)
  const [edits, setEdits] = useState<Record<number, { fare: number; headway: number }>>({});
  const [savingId, setSavingId] = useState<number | null>(null);
  const [deletingId, setDeletingId] = useState<number | null>(null);

  useEffect(() => {
    setHintbarText(t("transit.hint"));
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
    [apiBaseUrl, load],
  );

  const openCreation = useCallback(() => {
    void invoke("open_registered_window", { windowType: "transit_creation" });
    // 管理ウィンドウは閉じて、路線新設ウィンドウだけを表示する。
    onClose();
  }, [onClose]);

  const openDetail = useCallback((lineAgentId: number) => {
    void invoke("open_registered_window", {
      windowType: "transit_detail",
      entityId: String(lineAgentId),
    });
  }, []);

  const totals = useMemo(() => {
    if (!lines) return { revenues: 0, ridership: 0 };
    return lines.reduce(
      (acc, l) => ({
        revenues: acc.revenues + l.revenues,
        ridership: acc.ridership + l.ridership,
      }),
      { revenues: 0, ridership: 0 },
    );
  }, [lines]);

  if (error && !lines) {
    return <div className="transit-window__error">{error}</div>;
  }
  if (!lines) {
    return <div className="transit-window__loading">{t("common.loading")}</div>;
  }

  return (
    <div className="transit-window">
      {/* ヘッダー: 都市全体の集計と新設ボタン */}
      <div className="transit-window__header">
        <div className="transit-window__summary">
          <div className="transit-summary-card">
            <span className="transit-summary-card__label">{t("transit.lineCount")}</span>
            <span className="transit-summary-card__value">{lines.length}</span>
          </div>
          <div className="transit-summary-card">
            <span className="transit-summary-card__label">{t("transit.totalRidership")}</span>
            <span className="transit-summary-card__value">{Math.round(totals.ridership).toLocaleString()}</span>
          </div>
          <div className="transit-summary-card">
            <span className="transit-summary-card__label">{t("transit.totalRevenues")}</span>
            <span className="transit-summary-card__value">{Math.round(totals.revenues).toLocaleString()}</span>
          </div>
        </div>
        <button className="transit-window__new-btn" type="button" onClick={openCreation}>
          {t("transit.newLine")}
        </button>
      </div>

      {error && <div className="transit-window__error-inline">{error}</div>}

      {/* 路線一覧テーブル */}
      <div className="transit-table-wrapper">
        {lines.length === 0 ? (
          <div className="transit-window__empty">{t("transit.noLines")}</div>
        ) : (
          <table className="transit-table">
            <thead>
              <tr>
                <th>{t("transit.colName")}</th>
                <th className="transit-table__num">{t("transit.colVehicles")}</th>
                <th className="transit-table__util">{t("transit.colVehicleProcurement")}</th>
                <th className="transit-table__num">{t("transit.colRidership")}</th>
                <th className="transit-table__util">{t("transit.colUtilization")}</th>
                <th className="transit-table__num">{t("transit.colRevenues")}</th>
                <th className="transit-table__num">{t("transit.colFare")}</th>
                <th className="transit-table__num">{t("transit.colHeadway")}</th>
                <th className="transit-table__actions">{t("transit.colActions")}</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((line) => {
                const edit = edits[line.line_agent_id] ?? {
                  fare: line.fare_price,
                  headway: line.target_headway,
                };
                // 運行状態は路線エージェントの status で判定する(STATUS_ACTIVE=運行中)。
                // 車両不足は別途「調達率」列で示すため、車両0でも運休扱いにはしない。
                const running = line.status === STATUS_ACTIVE;
                // 運休バッジのツールチップに理由を出す(詳細を開かなくても原因が分かるように)。
                const pauseReasonText = running
                  ? undefined
                  : t(
                      (line.pause_reason && PAUSE_REASON_KEYS[line.pause_reason]) ||
                        "transit.pauseReason.generic",
                    );
                const procurementRatio =
                  line.required_vehicle_count > 0
                    ? line.vehicle_count / line.required_vehicle_count
                    : null;
                return (
                  <tr key={line.line_agent_id}>
                    <td className="transit-table__name">
                      <img src={resolveTransitIcon(line.path_variant)} alt="" className="transit-table__mode-icon" draggable={false} />
                      {/* 路線カラーの縦帯。3D の路線色帯・詳細パネルの帯と同じ色で路線を見分ける */}
                      <span
                        className="transit-table__color"
                        style={{ background: line.line_color || resolveTransitColor(line.path_variant) }}
                        aria-hidden="true"
                      />
                      <div className="transit-table__name-body">
                        <button
                          type="button"
                          className="transit-table__name-text entity-link"
                          title={line.name ?? ""}
                          onClick={() => openDetail(line.line_agent_id)}
                        >
                          {line.name ?? `#${line.line_agent_id}`}
                        </button>
                        <span
                          className={`transit-badge ${running ? "transit-badge--running" : "transit-badge--suspended"}`}
                          title={pauseReasonText}
                        >
                          {running ? t("transit.running") : t("transit.suspended")}
                        </span>
                      </div>
                    </td>
                    <td className="transit-table__num">
                      {procurementRatio === null
                        ? Math.round(line.vehicle_count)
                        : `${Math.round(line.vehicle_count)} / ${Math.round(line.required_vehicle_count)}`}
                    </td>
                    <td className="transit-table__util">
                      {procurementRatio === null ? (
                        "—"
                      ) : (
                        <UsageMeter usage_ratio={procurementRatio} goodHigh />
                      )}
                    </td>
                    <td className="transit-table__num">{Math.round(line.ridership).toLocaleString()}</td>
                    <td className="transit-table__util">
                      <UsageMeter usage_ratio={line.avg_utilization} />
                    </td>
                    <td className="transit-table__num">{Math.round(line.revenues).toLocaleString()}</td>
                    <td className="transit-table__num">
                      <input
                        type="number"
                        className="transit-table__input"
                        value={Math.round(edit.fare)}
                        min={0}
                        step={10}
                        onChange={(e) =>
                          handleEditChange(line.line_agent_id, "fare", Math.max(0, parseInt(e.target.value) || 0))
                        }
                      />
                    </td>
                    <td className="transit-table__num">
                      <input
                        type="number"
                        className="transit-table__input"
                        value={Math.round(edit.headway)}
                        min={1}
                        step={1}
                        onChange={(e) =>
                          handleEditChange(line.line_agent_id, "headway", Math.max(1, parseInt(e.target.value) || 1))
                        }
                      />
                    </td>
                    <td className="transit-table__actions">
                      <button
                        type="button"
                        className="transit-btn transit-btn--save"
                        disabled={!isDirty(line) || savingId === line.line_agent_id}
                        onClick={() => void handleSave(line)}
                      >
                        {savingId === line.line_agent_id ? t("common.processing") : t("transit.save")}
                      </button>
                      <button
                        type="button"
                        className="transit-btn transit-btn--delete"
                        disabled={deletingId === line.line_agent_id}
                        onClick={() => void handleDelete(line)}
                      >
                        {deletingId === line.line_agent_id ? t("common.processing") : t("transit.delete")}
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      <div className="transit-window__footer">
        <span className="transit-window__note">{t("transit.fareNote")}</span>
        <button className="transit-window__close-btn" type="button" onClick={onClose}>
          {t("common.close")}
        </button>
      </div>
    </div>
  );
}
