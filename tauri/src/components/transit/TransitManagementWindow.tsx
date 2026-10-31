import { describeError } from "../../utils/errors";
/**
 * Public transport window
 *
 * Centered window_type="transit_management" window. Filter lines through type tabs
 * (All plus transit_master types); inspect vehicle counts, procurement fulfillment, boardings,
 * crowding, fare income, balance, fare, and headway. The list is read-only;
 * click anywhere on a row for details, where settings and closure are handled.
 *
 * Type tabs follow transit_master ID order. Match line path_variant to transit_path_id.
 * Show types without lines too, keeping tab positions stable when lines are added/removed.
 *
 * Fetch via GET /api/v1/transit/lines. Create through transit_creation;
 * its transit:lines_changed event refreshes this list after creation.
 *
 * Stop placement and other Unity graphics controls are outside this window's scope.
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
import { useMasterData } from "../../contexts/MasterDataContext";
import { Money } from "../common/Money";
// Reuse the same crowding/utilization meter component as fire/police workload pressure
// in the statistics window. Its import also loads AccidentsTab.css (.usage-meter).
import { UsageMeter } from "../statistics/tabs/AccidentsTab";
import { STATUS_ACTIVE } from "../../constants";
import { resolveTransitColor } from "../../local-constants";
import { pauseReasonKey } from "./transitPauseReason";
import "./TransitManagementWindow.css";

/** Response type for one line from GET /api/v1/transit/lines. */
type TransitLine = {
  line_agent_id: number;
  name: string | null;
  path_variant: number;
  status: number;
  fare_price: number;
  target_headway: number;
  // Line color ("#rrggbb"). Empty means unset, falling back to the type default.
  line_color: string;
  vehicle_count: number;
  required_vehicle_count: number;
  revenues: number;
  expenses: number;
  ridership: number; // Daily boardings.
  max_utilization: number; // Crowding (utilization of the busiest section).
  path_count: number;
  // Suspension reason code (null / "route_disconnected" / "return_leg_blocked" / "no_underlay" / "train_too_long").
  pause_reason: string | null;
};

/** Current tab. Values other than "all" are line types (line path_variant = transit_master transit_path_id). */
type TabKey = "all" | number;

type TabDef = {
  key: TabKey;
  label: string;
  /** Type icon. The "All" tab has none. */
  icon: string | null;
  count: number;
};

/** Line name. Unnamed lines use their ID, matching the line detail header. */
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

  useEffect(() => {
    setHintbarText(t("hint.transitManagement"));
    return () => {
      setHintbarText("");
    };
  }, [t]);

  // While viewing the list, use the public transport 3D preset (all lines plus planned roads/buildings).
  // STATE_NORMAL sent by Rust on window close restores the previous display,
  // so do not restore it here; closing does not unmount React.
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
        setError(null);
      } catch (err) {
        if (!signal?.aborted) {
          setError(describeError(err));
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

  // Refetch the list when creation/detail windows change lines.
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

  // The management window closes through an exclusive group; line creation may open in its reusable WebView.
  // Closing ourselves here could hit the new creation window and immediately hide it, so leave closing to Rust.
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
      {/* Left sidebar: create-line button and vertical line-type tabs. */}
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

      {/* Right main pane: error display and line table. */}
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
                <th className="transit-table__num">{t("transit.balance")}</th>
                <th className="transit-table__num">{t("transit.colFare")}</th>
                <th className="transit-table__num">{t("transit.colHeadway")}</th>
              </tr>
            </thead>
            <tbody>
              {visibleLines.map((line) => {
                const balance = line.revenues - line.expenses;
                // Determine operation state from the line agent status (STATUS_ACTIVE = operating).
                // Vehicle shortages are shown separately in procurement fulfillment; zero vehicles alone does not mean suspended.
                const running = line.status === STATUS_ACTIVE;
                const procurementRatio =
                  line.required_vehicle_count > 0
                    ? line.vehicle_count / line.required_vehicle_count
                    : null;
                const displayName = lineDisplayName(line);
                return (
                  <tr key={line.line_agent_id} onClick={() => openDetail(line.line_agent_id)}>
                    <td>
                      <div className={`transit-line${running ? "" : " transit-line--suspended"}`}>
                        {/* Vertical line-color band. Matches 3D bands/detail panel colors to distinguish lines. */}
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
                        >
                          {displayName}
                        </button>
                        {/*
 * Operating is normal, so mark only suspended lines. Explain the reason in a tooltip
 * so players can see the cause without opening details.
 */}
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
                        <UsageMeter usage_ratio={line.max_utilization} />
                      </div>
                    </td>
                    <td className="transit-table__num">
                      <Money value={Math.round(line.revenues)} />
                    </td>
                    <td className="transit-table__num">
                      <Money
                        value={Math.round(balance)}
                        className={balance < 0 ? "transit-table__balance--negative" : balance > 0 ? "transit-table__balance--positive" : undefined}
                      />
                    </td>
                    <td className="transit-table__num">
                      <Money value={line.fare_price} />
                    </td>
                    <td className="transit-table__num">
                      {line.target_headway.toLocaleString(locale)}
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

/** "+" create-line icon. Inline SVG following button text color. */
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

/** "All" tab icon. Inline SVG representing a list of all lines. */
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
