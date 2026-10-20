/**
 * Vertical strip line diagram
 *
 * A vertical version of subway diagrams above carriage doors: one straight line in line color,
 * to the right of crowding values, with stop circles and adjacent names in sequence.
 * Shows stop order only, without geographic relationships (TransitLineMap handles the real map).
 *
 * Use HTML/CSS rather than SVG so the browser handles wrapping, clickable names, and hover.
 * Each row owns an upper half-line, circle, and lower half-line. Adjacent halves represent one
 * section path. If suspended (status≠ACTIVE), draw both corresponding halves gray and dashed.
 *
 * Lines loop through stops and return to the start; arrangement depends on route_points turnback:
 * - Exactly two turnbacks (round trip): draw the one-way stretch between them with double-circle
 *   termini. Omit the return because it visits the same stations in reverse; for parallel single
 *   tracks, return stops are on the opposite-facing platform.
 * - Otherwise (loop services or repeated intermediate turnbacks): list all stops in traversal order;
 *   repeat the first name faintly below the last to show return. Mark turnback stops with a badge.
 *
 * Use stop names resolved from underlying paths by GET /api/v1/transit/lines/{id}.
 * Unnamed stops (bus stops, etc.) use "Stop N", where N follows stops order.
 * For stops with house_path_id, clicking the name opens station building path details (path_detail).
 *
 * Each row's right edge shows previous epoch boardings. Rows represent stations, so aggregate
 * this line's stops by station building, or by individual stop if unaffiliated. Include unshown
 * return-platform stops in the same station row. Rows containing only unrecorded stops show "-".
 *
 * At the left boundary to the next row, show section crowding using the same station grouping.
 * Use the most crowded section connecting the two stations in either direction,
 * including unshown return sections.
 */

import { useMemo, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { STATUS_ACTIVE } from "../../constants";
import type { TransitStop, TransitEdge, TransitRoutePointSummary } from "./TransitDetailPanel";
import "./TransitLineDiagram.css";

type TransitLineDiagramProps = {
  stops: TransitStop[];
  routePoints: TransitRoutePointSummary[];
  edges: TransitEdge[];
  /** Line color ("#rrggbb"). */
  color: string;
  /** Whether the whole line is operating. Gray out the entire line while suspended. */
  running: boolean;
};

/** One diagram row (one visit to a stop). */
type Visit = { nodeId: number; turnback: boolean };

/** Station key represented by a row: station building for affiliated stops, otherwise the stop itself. */
function stationKey(stop: TransitStop): string {
  return stop.house_path_id ? `house:${stop.house_path_id}` : `stop:${stop.node_id}`;
}

/** Key for a section between two stations, independent of direction. */
function sectionKey(a: TransitStop, b: TransitStop): string {
  return [stationKey(a), stationKey(b)].sort().join("|");
}

export function TransitLineDiagram({ stops, routePoints, edges, color, running }: TransitLineDiagramProps) {
  const { t } = useTranslation();

  // Section paths are one-way along travel direction, so look up using directed departure → arrival.
  const edgeByLeg = useMemo(() => {
    const m = new Map<string, TransitEdge>();
    for (const e of edges) m.set(`${e.from_node_id}>${e.to_node_id}`, e);
    return m;
  }, [edges]);

  const stopIndex = useMemo(() => new Map(stops.map((s, i) => [s.node_id, i])), [stops]);

  const utilizationBySection = useMemo(() => {
    const byNode = new Map(stops.map((s) => [s.node_id, s]));
    const m = new Map<string, number>();
    for (const e of edges) {
      const from = byNode.get(e.from_node_id);
      const to = byNode.get(e.to_node_id);
      if (!from || !to) continue;
      const key = sectionKey(from, to);
      m.set(key, Math.max(m.get(key) ?? 0, e.utilization));
    }
    return m;
  }, [stops, edges]);

  const ridershipByStation = useMemo(() => {
    const m = new Map<string, number | null>();
    for (const s of stops) {
      const sum = m.get(stationKey(s)) ?? null;
      m.set(stationKey(s), s.ridership == null ? sum : (sum ?? 0) + s.ridership);
    }
    return m;
  }, [stops]);

  const visits: Visit[] = useMemo(
    () =>
      routePoints
        .filter((p) => p.kind === "stop" && p.node_id != null)
        .map((p) => ({ nodeId: p.node_id as number, turnback: p.turnback })),
    [routePoints],
  );

  // For round trips, list only one direction between turnbacks; otherwise list every visit in one loop.
  const { rows, isRoundTrip } = useMemo(() => {
    const turnbacks = visits.flatMap((v, i) => (v.turnback ? [i] : []));
    if (turnbacks.length !== 2) return { rows: visits, isRoundTrip: false };
    const [from, to] = turnbacks;
    return { rows: visits.slice(from, to + 1), isRoundTrip: true };
  }, [visits]);

  const last = rows.length - 1;
  const legActive = (a: Visit, b: Visit): boolean => {
    if (!running) return false;
    const e = edgeByLeg.get(`${a.nodeId}>${b.nodeId}`);
    return e ? e.status === STATUS_ACTIVE : true;
  };
  const loopBack = !isRoundTrip && rows.length >= 2;
  const loopActive = loopBack && legActive(rows[last], rows[0]);

  const stopOf = (v: Visit) => {
    const index = stopIndex.get(v.nodeId) ?? 0;
    return { stop: stops[index], index };
  };
  const sectionUtilization = (a: Visit, b: Visit): string => {
    const u = utilizationBySection.get(sectionKey(stopOf(a).stop, stopOf(b).stop));
    return u == null ? "-" : `${Math.round(u * 100)}%`;
  };
  const stopLabel = (v: Visit) => {
    const { stop, index } = stopOf(v);
    return stop?.name && stop.name !== "" ? stop.name : t("transit.stopFallbackName", { n: index + 1 });
  };

  const openStation = (housePathId: number) => {
    void invoke("open_registered_window", {
      windowType: "path_detail",
      entityId: String(housePathId),
    });
  };

  const segClass = (active: boolean) => `tld__seg${active ? "" : " tld__seg--suspended"}`;

  return (
    <div className="tld" style={{ "--tld-color": color } as CSSProperties}>
      <div className="tld__summary">
        <span>{t("transit.colUtilization")}</span>
        <span className="tld__summary-count">{t("transit.stopsCountValue", { count: stops.length })}</span>
        {rows.length >= 2 && (
          <span className="tld__summary-ends">
            {stopLabel(rows[0])}
            <span className="tld__summary-arrow">{isRoundTrip ? "⇄" : "↺"}</span>
            {isRoundTrip ? stopLabel(rows[last]) : null}
          </span>
        )}
        <span className="tld__summary-ridership">{t("transit.colRidership")}</span>
      </div>

      <ol className="tld__list">
        {rows.map((v, i) => {
          const isTerminal = isRoundTrip && (i === 0 || i === last);
          const upActive = i > 0 ? legActive(rows[i - 1], v) : true;
          const downActive = i < last ? legActive(v, rows[i + 1]) : loopActive;
          const { stop } = stopOf(v);
          const label = stopLabel(v);
          const ridership = stop ? ridershipByStation.get(stationKey(stop)) ?? null : null;
          const next = i < last ? rows[i + 1] : loopBack ? rows[0] : null;
          return (
            <li key={i} className="tld__row">
              <span className="tld__util">
                {next && <span className="tld__util-value">{sectionUtilization(v, next)}</span>}
              </span>
              <span className="tld__track" aria-hidden="true">
                {i > 0 && <span className={`${segClass(upActive)} tld__seg--up`} />}
                {(i < last || loopBack) && <span className={`${segClass(downActive)} tld__seg--down`} />}
                <span className={`tld__dot${isTerminal ? " tld__dot--terminal" : ""}`} />
              </span>
              {stop?.house_path_id ? (
                <button
                  type="button"
                  className="tld__name tld__name--link"
                  title={t("transit.openStation")}
                  onClick={() => openStation(stop.house_path_id as number)}
                >
                  {label}
                </button>
              ) : (
                <span className="tld__name">{label}</span>
              )}
              {!isRoundTrip && v.turnback && <span className="tld__badge">{t("transit.turnback")}</span>}
              <span className="tld__ridership">{ridership == null ? "-" : Math.round(ridership).toLocaleString()}</span>
            </li>
          );
        })}
        {loopBack && (
          <li className="tld__row tld__row--loop">
            <span className="tld__util" />
            <span className="tld__track" aria-hidden="true">
              <span className={`${segClass(loopActive)} tld__seg--up`} />
              <span className="tld__loop-mark">↺</span>
            </span>
            <span className="tld__name tld__name--loop">{t("transit.loopLine", { name: stopLabel(rows[0]) })}</span>
          </li>
        )}
      </ol>
    </div>
  );
}
