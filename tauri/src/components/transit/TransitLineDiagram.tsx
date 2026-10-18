/**
 * 路線図(縦型ストリップ)
 *
 * 地下鉄の車内ドア上に掲示される路線図を縦向きにしたもの。混雑率の右に路線カラーの1本の直線を置き、
 * 停留所を順に丸で打ち、丸の横に停留所名を並べる。地理的な位置関係は表さず、
 * 「どの順で停まるか」だけを示す(実地図上の路線は TransitLineMap が担う)。
 *
 * 構造は HTML/CSS で組む(SVG ではなく)。停留所名の折り返し・クリックリンク・ホバーを
 * ブラウザのテキスト描画に任せられるため。各行が「上半分の線 + 丸 + 下半分の線」を持ち、
 * 隣接行の半分どうしが1つの区間パスを表す。区間が運休(status≠ACTIVE)なら、その区間に
 * あたる上下の半分を破線・灰色で描く。
 *
 * 路線は停留所を巡回順にたどって先頭へ戻るループで、並べ方は折り返し(route_points の turnback)で決める。
 *   - 折り返しがちょうど2か所(往復運行): 一方の折り返しからもう一方までの片道を1本の線で描き、両端を
 *     終端の二重丸にする。帰りは同じ駅を逆にたどる(単線を並べた複線では帰りの停留所が反対向きのホームにある)ので描かない。
 *   - それ以外(環状運転や、途中で何度も折り返す運行): 巡回順の全停留所を並べ、末尾の下に先頭停留所名を
 *     薄く再掲して「戻る」ことを示す。折り返す停留所には「折り返し」の札を付ける。
 *
 * 停留所名は GET /api/v1/transit/lines/{id} が下地パスから解決した name を使い、
 * 無名(バス停など)は「停留所 N」(N は stops の順番)で補う。駅舎に所属する停留所(house_path_id あり)は
 * 駅名をクリックで駅舎パスの詳細ウィンドウ(path_detail)を開く。
 *
 * 各行の右端には前エポックの乗車回数を出す。1行は駅を表すので、駅舎に所属する停留所は駅舎ごと、所属しない
 * 停留所は停留所ごとに、この路線の停留所の乗車回数を合計する(往復運行で描かない帰りのホームの停留所も
 * 同じ駅舎の行に入る)。記録の無い停留所だけの行は「-」。
 *
 * 各行の左端には、その行から次の行への区間の混雑率を行の境目の高さに出す。乗車回数と同じ駅の単位で、2つの駅を
 * 結ぶ区間パスのうち向きを問わず最も混む区間の値にする(往復運行で描かない帰りの区間も入る)。
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
  /** 路線カラー("#rrggbb") */
  color: string;
  /** 路線全体が運行中か。運休中は線全体を灰色にする */
  running: boolean;
};

/** 路線図の1行(停留所への1回の訪問) */
type Visit = { nodeId: number; turnback: boolean };

/** 行が表す駅のキー。駅舎に所属する停留所は駅舎、所属しない停留所は停留所そのもの。 */
function stationKey(stop: TransitStop): string {
  return stop.house_path_id ? `house:${stop.house_path_id}` : `stop:${stop.node_id}`;
}

/** 2つの駅を結ぶ区間のキー。向きを問わない。 */
function sectionKey(a: TransitStop, b: TransitStop): string {
  return [stationKey(a), stationKey(b)].sort().join("|");
}

export function TransitLineDiagram({ stops, routePoints, edges, color, running }: TransitLineDiagramProps) {
  const { t } = useTranslation();

  // 区間パスは進行方向の一方通行なので、出発→到着の向き付きで引く。
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

  // 往復運行なら片道(折り返し→折り返し)だけ、それ以外は1周の全訪問を並べる。
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
