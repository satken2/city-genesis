/**
 * 道路の交通量プロファイル(道路詳細パネルの「交通量」タブ)
 *
 * 横軸をキロ程にとり、基準線と並行区間(上下線)の値を区間ごとの階段状グラフで描く。指標は切り替え式:
 *   - 交通量: 車種別の積み上げ(上下線の合計=断面交通量)+ 容量の線。上下線が分かれている道路では
 *     方向別の合計の線を重ねる(車種×方向の積み上げにすると系列が膨らみ読めないため、内訳は合計だけ)
 *   - 混雑度: 交通量÷容量(%。1で頭打ちにしない)。50/80/100% の参照線(軸は参照線まで必ず広げる)
 *   - 旅行速度: 混雑込み(区間長÷所要時間)と自由走行(破線)
 *   - 騒音・耐久度・路上駐車(駐車台数と容量の線)
 * 上下線が分かれている区間の下り/上りは別系列にする(双方向の区間は「双方向」の系列)。
 * 稼働中でない区間は交通が無いので値を出さない(耐久度だけは出す)。塊の途切れは線が切れる。
 *
 * ツールチップの位置合わせのため、行は区間の境目に加えてキロ程を細かく刻んだ点にも置く
 * (最寄りの行が隣の区間の値を指す範囲を刻み幅の半分に抑える)。クリックでその位置の区間の path_detail を開く。
 * recharts は CSS 変数を解決できないので色は直値の定数で持つ。
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import {
  Area,
  CartesianGrid,
  ComposedChart,
  Line,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { ROAD_VEHICLE_KEYS, STATUS_ACTIVE } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveIntlLocale } from "../../utils/locale";
import type { RoadDetail, RoadSegment, RoadSegmentDirection } from "./roadTypes";
import { ROAD_COLOR_VC_HIGH, ROAD_COLOR_VC_MID, formatKm, segmentSpeedKmh } from "./roadFormat";
import "./RoadTrafficProfile.css";
import { useRemPx } from "../../hooks/useRemPx";

type Metric = "traffic" | "vc" | "speed" | "noise" | "durability" | "parking";

const METRICS: Metric[] = ["traffic", "vc", "speed", "noise", "durability", "parking"];
const DIRECTIONS: RoadSegmentDirection[] = ["both", "down", "up"];
/** キロ程の刻みの数(行の細かさ) */
const SAMPLE_COUNT = 400;

const CHART_AXIS_TEXT = "#a4bccc";
const CHART_AXIS_LINE = "#2c3646";
const CHART_GRID_LINE = "#22303f";
const CAPACITY_COLOR = "#e2e8f0";
const REF_100_COLOR = "#fca5a5";
const DIRECTION_COLORS: Record<RoadSegmentDirection, string> = { both: "#60a5fa", down: "#f472b6", up: "#a78bfa" };
const VEHICLE_COLORS: Record<number, string> = {
  10: "#60a5fa",
  11: "#f59e0b",
  12: "#34d399",
  13: "#f472b6",
  14: "#a78bfa",
};
const VEHICLE_COLOR_FALLBACK = "#94a3b8";

type Row = Record<string, number | null>;

type Interval = { lo: number; hi: number; covering: RoadSegment[] };

type RoadTrafficProfileProps = {
  road: RoadDetail;
};

function openPath(pathId: number) {
  void invoke("open_registered_window", { windowType: "path_detail", entityId: String(pathId) });
}

/** 区間を方向別に1本選ぶ(同じ位置に同じ向きが複数あるときは交通量の多い方) */
function pickByDirection(covering: RoadSegment[], dir: RoadSegmentDirection): RoadSegment | null {
  let best: RoadSegment | null = null;
  for (const s of covering) {
    if (s.direction !== dir) continue;
    if (!best || s.road_traffic > best.road_traffic) best = s;
  }
  return best;
}

function sumOrNull(values: (number | null)[]): number | null {
  const valid = values.filter((v): v is number => v != null);
  return valid.length > 0 ? valid.reduce((a, b) => a + b, 0) : null;
}

/** 1区間分の行の値(区間の値をそのまま持つ) */
function intervalValues(covering: RoadSegment[], vehicleKeys: number[]): Row {
  const row: Row = {};
  const active = covering.filter((s) => s.status === STATUS_ACTIVE);
  if (active.length > 0) {
    for (const key of vehicleKeys) {
      row[`v_${key}`] = active.reduce((sum, s) => sum + (s.traffic[String(key)] ?? 0), 0);
    }
    row.cap = sumOrNull(active.map((s) => s.capacity));
    row.park = active.reduce((sum, s) => sum + s.parked_cars, 0);
    row.parkcap = active.reduce((sum, s) => sum + s.street_parking_capacity, 0);
  }
  for (const dir of DIRECTIONS) {
    const s = pickByDirection(covering, dir);
    if (!s) continue;
    row[`pid_${dir}`] = s.path_id;
    row[`dur_${dir}`] = s.durability * 100;
    if (s.status !== STATUS_ACTIVE) continue;
    row[`tr_${dir}`] = s.road_traffic;
    row[`vc_${dir}`] = s.vc != null ? s.vc * 100 : null;
    row[`spd_${dir}`] = segmentSpeedKmh(s, false);
    row[`spdf_${dir}`] = segmentSpeedKmh(s, true);
    row[`noise_${dir}`] = s.noise;
  }
  return row;
}

function buildRows(road: RoadDetail, vehicleKeys: number[]): { rows: Row[]; totalKm: number; dirs: RoadSegmentDirection[] } {
  const segs = road.segments.filter((s) => s.role !== "branch");
  const totalKm = Math.max(0.001, ...road.sections.map((s) => s.end_km), ...segs.map((s) => Math.max(s.from_km, s.to_km)));
  const bounds = new Set<number>([0, totalKm]);
  for (const s of segs) {
    bounds.add(Math.min(s.from_km, s.to_km));
    bounds.add(Math.max(s.from_km, s.to_km));
  }
  const breaks = [...bounds].sort((a, b) => a - b);
  const intervals: Interval[] = [];
  for (let i = 0; i + 1 < breaks.length; i++) {
    const lo = breaks[i];
    const hi = breaks[i + 1];
    if (hi - lo <= 1e-9) continue;
    const mid = (lo + hi) / 2;
    intervals.push({
      lo,
      hi,
      covering: segs.filter((s) => Math.min(s.from_km, s.to_km) < mid && Math.max(s.from_km, s.to_km) > mid),
    });
  }
  const values = intervals.map((iv) => intervalValues(iv.covering, vehicleKeys));

  const kms = new Set<number>(breaks);
  for (let i = 0; i <= SAMPLE_COUNT; i++) kms.add((totalKm * i) / SAMPLE_COUNT);
  const sortedKms = [...kms].sort((a, b) => a - b);
  const rows: Row[] = [];
  let j = 0;
  for (const km of sortedKms) {
    while (j + 1 < intervals.length && km >= intervals[j].hi) j++;
    rows.push({ km, ...(values[j] ?? {}) });
  }

  const dirs = DIRECTIONS.filter((d) => segs.some((s) => s.direction === d));
  return { rows, totalKm, dirs };
}

function directionLabel(dir: RoadSegmentDirection, t: TFunction): string {
  return t(`road.traffic.direction.${dir}`);
}

export function RoadTrafficProfile({ road }: RoadTrafficProfileProps) {
  const { t, i18n } = useTranslation();
  const remPx = useRemPx();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);
  const [metric, setMetric] = useState<Metric>("traffic");

  const segmentById = useMemo(() => new Map(road.segments.map((s) => [s.path_id, s])), [road.segments]);

  // 積み上げる車種は、どこかの区間で交通がある道路車両だけ(常に0の車種の帯は凡例を増やすだけ)
  const vehicleKeys = useMemo(
    () => ROAD_VEHICLE_KEYS.filter((key) => road.segments.some((s) => (s.traffic[String(key)] ?? 0) > 0)),
    [road.segments],
  );
  const { rows, totalKm, dirs } = useMemo(() => buildRows(road, vehicleKeys), [road, vehicleKeys]);

  const vehicleName = (key: number) =>
    t(`path.vehicles.${key}`, { defaultValue: masterData?.vehicle_types?.[key]?.display_name ?? `ID:${key}` });

  const fmt = (v: number, digits = 0) => v.toLocaleString(locale, { maximumFractionDigits: digits });

  /** 指標ごとの系列(dataKey・表示名・色・破線か) */
  type Series = { key: string; name: string; color: string; dashed?: boolean; area?: boolean };
  const series: Series[] = (() => {
    const multi = dirs.length > 1;
    const dirName = (base: string, dir: RoadSegmentDirection) => (multi ? `${base}(${directionLabel(dir, t)})` : base);
    switch (metric) {
      case "traffic":
        return [
          ...vehicleKeys.map((key) => ({
            key: `v_${key}`,
            name: vehicleName(key),
            color: VEHICLE_COLORS[key] ?? VEHICLE_COLOR_FALLBACK,
            area: true,
          })),
          { key: "cap", name: t("road.traffic.capacity"), color: CAPACITY_COLOR, dashed: true },
          ...(multi
            ? dirs.map((d) => ({ key: `tr_${d}`, name: dirName(t("road.traffic.metric.traffic"), d), color: DIRECTION_COLORS[d] }))
            : []),
        ];
      case "vc":
        return dirs.map((d) => ({ key: `vc_${d}`, name: dirName(t("road.traffic.metric.vc"), d), color: DIRECTION_COLORS[d] }));
      case "speed":
        return dirs.flatMap((d) => [
          { key: `spd_${d}`, name: dirName(t("road.traffic.speedCongested"), d), color: DIRECTION_COLORS[d] },
          { key: `spdf_${d}`, name: dirName(t("road.traffic.speedFree"), d), color: DIRECTION_COLORS[d], dashed: true },
        ]);
      case "noise":
        return dirs.map((d) => ({ key: `noise_${d}`, name: dirName(t("road.traffic.metric.noise"), d), color: DIRECTION_COLORS[d] }));
      case "durability":
        return dirs.map((d) => ({ key: `dur_${d}`, name: dirName(t("road.traffic.metric.durability"), d), color: DIRECTION_COLORS[d] }));
      case "parking":
        return [
          { key: "park", name: t("road.traffic.parked"), color: "#60a5fa", area: true },
          { key: "parkcap", name: t("road.traffic.parkingCapacity"), color: CAPACITY_COLOR, dashed: true },
        ];
    }
  })();

  const unit = (() => {
    switch (metric) {
      case "traffic": return t("road.traffic.unit.traffic");
      case "vc":
      case "durability": return "%";
      case "speed": return "km/h";
      case "parking": return t("road.traffic.unit.parking");
      default: return "";
    }
  })();
  const valueDigits = metric === "noise" || metric === "parking" ? 1 : 0;

  const hasData = series.some((s) => rows.some((r) => r[s.key] != null && r[s.key] !== 0));

  const handleClick = (state: { activeIndex?: number | string | null } | null) => {
    const index = state?.activeIndex != null ? Number(state.activeIndex) : NaN;
    const row = Number.isFinite(index) ? rows[index] : undefined;
    if (!row) return;
    const pid = DIRECTIONS.map((d) => row[`pid_${d}`]).find((v) => v != null);
    if (pid != null) openPath(pid);
  };

  const renderTooltip = ({ active, payload }: { active?: boolean; payload?: readonly { payload?: Row }[] }) => {
    const row = active ? payload?.[0]?.payload : undefined;
    if (!row) return null;
    const segLines = DIRECTIONS.flatMap((d) => {
      const pid = row[`pid_${d}`];
      if (pid == null) return [];
      const seg = segmentById.get(pid);
      const name = seg && seg.name !== "" ? `${seg.name} (#${pid})` : t("road.pathWithId", { id: pid });
      return [{ d, name }];
    });
    const values = series.filter((s) => row[s.key] != null);
    return (
      <div className="road-traffic__tooltip">
        <div className="road-traffic__tooltip-title">{formatKm((row.km ?? 0) * 1000, locale)} km</div>
        {segLines.map(({ d, name }) => (
          <div key={d} className="road-traffic__tooltip-seg">
            {dirs.length > 1 && <span className="road-traffic__tooltip-dir">{directionLabel(d, t)}</span>}
            {name}
          </div>
        ))}
        {values.map((s) => (
          <div key={s.key} className="road-traffic__tooltip-row">
            <span className="road-traffic__tooltip-swatch" style={{ background: s.color }} />
            <span className="road-traffic__tooltip-name">{s.name}</span>
            <span className="road-traffic__tooltip-value">
              {fmt(Number(row[s.key]), valueDigits)}
              {unit !== "" && <span className="road-traffic__tooltip-unit">{unit}</span>}
            </span>
          </div>
        ))}
        {segLines.length === 0 && <div className="road-traffic__tooltip-seg">{t("road.traffic.noSegment")}</div>}
      </div>
    );
  };

  return (
    <div className="road-traffic">
      <div className="road-traffic__metrics" role="tablist">
        {METRICS.map((m) => (
          <button
            key={m}
            type="button"
            role="tab"
            aria-selected={metric === m}
            className={`road-traffic__metric${metric === m ? " road-traffic__metric--active" : ""}`}
            onClick={() => setMetric(m)}
          >
            {t(`road.traffic.metric.${m}`)}
          </button>
        ))}
      </div>
      {metric === "traffic" && dirs.length > 1 && <p className="road-traffic__note">{t("road.traffic.sumNote")}</p>}

      {hasData ? (
        <div className="road-traffic__chart">
          <ResponsiveContainer width="100%" height={260}>
            <ComposedChart data={rows} margin={{ top: 8, right: 12, left: 0, bottom: 4 }} onClick={handleClick}>
              <CartesianGrid strokeDasharray="3 3" stroke={CHART_GRID_LINE} vertical={false} />
              <XAxis
                dataKey="km"
                type="number"
                domain={[0, totalKm]}
                tick={{ fontSize: "0.93rem", fill: CHART_AXIS_TEXT }}
                tickLine={false}
                stroke={CHART_AXIS_LINE}
                tickFormatter={(v) => `${fmt(Number(v), totalKm < 2 ? 2 : 1)}`}
                unit=" km"
              />
              <YAxis
                tick={{ fontSize: "0.93rem", fill: CHART_AXIS_TEXT }}
                tickLine={false}
                width={remPx(4.44)}
                stroke={CHART_AXIS_LINE}
                domain={metric === "durability" ? [0, 100] : [0, "auto"]}
                tickFormatter={(v) => fmt(Number(v), valueDigits)}
              />
              <Tooltip content={renderTooltip} cursor={{ stroke: "rgba(255,255,255,0.3)" }} isAnimationActive={false} />
              {metric === "vc" && (
                <>
                  <ReferenceLine y={50} stroke={ROAD_COLOR_VC_MID} strokeDasharray="4 4" ifOverflow="extendDomain" />
                  <ReferenceLine y={80} stroke={ROAD_COLOR_VC_HIGH} strokeDasharray="4 4" ifOverflow="extendDomain" />
                  <ReferenceLine y={100} stroke={REF_100_COLOR} ifOverflow="extendDomain" />
                </>
              )}
              {series.map((s) =>
                s.area ? (
                  <Area
                    key={s.key}
                    dataKey={s.key}
                    name={s.name}
                    type="stepAfter"
                    stackId={metric === "traffic" ? "traffic" : undefined}
                    stroke={s.color}
                    fill={s.color}
                    fillOpacity={0.55}
                    strokeWidth={1}
                    connectNulls={false}
                    isAnimationActive={false}
                    dot={false}
                    activeDot={false}
                  />
                ) : (
                  <Line
                    key={s.key}
                    dataKey={s.key}
                    name={s.name}
                    type="stepAfter"
                    stroke={s.color}
                    strokeWidth={s.dashed ? 1.5 : 2}
                    strokeDasharray={s.dashed ? "5 4" : undefined}
                    connectNulls={false}
                    isAnimationActive={false}
                    dot={false}
                    activeDot={false}
                  />
                ),
              )}
            </ComposedChart>
          </ResponsiveContainer>
          <div className="road-traffic__legend">
            {series.map((s) => (
              <span key={s.key} className="road-traffic__legend-item">
                <i
                  className={s.dashed ? "road-traffic__legend-dashed" : undefined}
                  style={s.dashed ? { borderColor: s.color } : { background: s.color }}
                />
                {s.name}
              </span>
            ))}
          </div>
        </div>
      ) : (
        <p className="agent-detail-muted">{t("road.traffic.noData")}</p>
      )}
    </div>
  );
}
