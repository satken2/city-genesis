/**
 * 移動統計タブコンポーネント
 *
 * GET /api/v1/stats/commute から通勤・通学時間、地区間の移動、地区別の自動車保有率を
 * 取得して表示する (同APIが返す地区別の昼夜間人口は人口タブが使う)。
 *
 * レイアウト (左右2:3分割):
 * - 左上 (1/3 高さ): 通勤・通学時間分布 (横向き積み上げ棒グラフ)。集計範囲 (都市全体／各地区) を
 *   パネル見出しのプルダウンで選ぶ。地区は市民の居住地区で、このパネルだけに効く
 * - 左下 (2/3 高さ): 地区別の自動車保有率の色分け地図
 * - 右 (全高): 地区間の移動 (Recharts Sankey ダイアグラム)。「合計」「通勤・通学」「買い物」のサブタブで切り替え、
 *   サブタブごとに色を変える。値は1日あたりの往復回数(通勤・通学は市民1人が1日1往復)。買い物は直近エポックの
 *   記録でセーブされないので、シミュレーション前とロード直後は「合計」「買い物」にデータが無い
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Sankey,
} from "recharts";
import {
  CityMapCanvas,
  getZoneFillRampStops,
  type CityMapLayerVisibility,
  type CityMapZoneFill,
} from "../../map/CityMapCanvas";
import { useEpochCachedStats } from "../useEpochCache";
import "./CommuteTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- 型定義 ---

type Summary = {
  worker_count: number;
  student_count: number;
  total_commuters: number;
};

type TimeDistPoint = {
  bucket: string;
  workers: number;
  students: number;
};

/** 通勤・通学時間分布の集計範囲。都市全体は zone_id・name とも null */
type CommuteTimeScope = {
  zone_id: number | null;
  name: string | null;
  distribution: TimeDistPoint[];
};

/** 地区別の自動車保有。母集団の住民世帯が0の地区は率が定義できないので null(欠測) */
type CarOwnershipZone = {
  zone_id: number;
  name: string;
  households: number;
  owners: number;
  wanters: number;
  rate: number | null;
  want_rate: number | null;
};

type CarOwnership = {
  households: number;
  owners: number;
  wanters: number;
  rate: number | null;
  want_rate: number | null;
  zones: CarOwnershipZone[];
};

type ODPoint = {
  from_zone_id: number;
  from_name: string;
  to_zone_id: number;
  to_name: string;
  count: number;
};

/** 地区間の移動の種類。total と shopping は買い物の記録が無いあいだ null */
type FlowKind = "total" | "commute" | "shopping";

type CommuteStats = {
  epoch: number;
  summary: Summary;
  commute_time_scopes: CommuteTimeScope[];
  od_flows: Record<FlowKind, ODPoint[] | null>;
  car_ownership: CarOwnership;
};

// --- 配色 ---
const COLOR_WORKER = "#228be6";      // 通勤者: 濃い青
const COLOR_STUDENT = "#a5d8ff";     // 通学者: 淡い青 (ユーザー要望の薄い色)
const FLOW_KINDS: FlowKind[] = ["total", "commute", "shopping"];
const FLOW_TAB_KEYS: Record<FlowKind, string> = {
  total: "statistics.flowTabTotal",
  commute: "statistics.flowTabCommute",
  shopping: "statistics.flowTabShopping",
};
/** Sankey の配色。出発地(左)のノードは濃く、目的地(右)のノードは淡く、リンクは半透明。サブタブの下線は出発地の色 */
const FLOW_COLORS: Record<FlowKind, { source: string; target: string; link: string }> = {
  total: { source: "#7048e8", target: "#b197fc", link: "rgba(151, 117, 250, 0.4)" },
  commute: { source: "#1c7ed6", target: "#74c0fc", link: "rgba(74, 158, 255, 0.4)" },
  shopping: { source: "#e8590c", target: "#ffa94d", link: "rgba(255, 146, 43, 0.4)" },
};

/**
 * 自動車保有率の地図レイヤー。自動車を持つかどうかは公共交通の充実度で決まるため、
 * 保有率の色分けと公共交通路線を重ねて読めるようにする(満足度タブの地図と違い transitLines をON)。
 * 再レンダリングでレイヤー変更effectを走らせないようモジュール定数として持つ。
 */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  transitLines: true,
  parcels: false,
  buildings: false,
  stations: true,
  zoneBorders: true,
  elevation: false,
  contours: false,
  grid: false,
};

/** 率(0〜1)を百分率の文字列にする。欠測は "—" */
function formatRate(rate: number | null | undefined): string {
  return rate == null ? "—" : `${(rate * 100).toFixed(1)}%`;
}

function scopeKey(scope: CommuteTimeScope): string {
  return scope.zone_id == null ? "city" : String(scope.zone_id);
}

/** Sankey のノード。地区内の移動が自己ループになると描けないため、同じ地区でも居住側(左)と目的地側(右)は別ノードにする */
type SankeyNode = { name: string; isSource: boolean };

/**
 * 地区別の自動車保有率の色分け地図。
 *
 * 塗りは地区間のmin〜maxを両端まで引き伸ばした相対値で、地区差が小さくても違いが読める
 * 代わりに絶対比較ができないため、凡例に実際の保有率を併記する(満足度タブの地図と同じ方式)。
 */
function CarOwnershipMap({
  apiBaseUrl,
  ownership,
}: {
  apiBaseUrl: string;
  ownership: CarOwnership;
}) {
  const { t } = useTranslation();

  // 母集団の住民世帯がいない地区は欠測として塗らない。
  const zoneValues = useMemo(
    () =>
      ownership.zones
        .filter((z) => typeof z.rate === "number")
        .map((z) => ({ zoneId: z.zone_id, value: z.rate as number })),
    [ownership.zones]
  );

  const zoneRange = useMemo(() => {
    if (!zoneValues.length) return null;
    let min = Infinity;
    let max = -Infinity;
    for (const { value } of zoneValues) {
      if (value < min) min = value;
      if (value > max) max = value;
    }
    return { min, max };
  }, [zoneValues]);

  const zoneFill = useMemo<CityMapZoneFill | undefined>(() => {
    if (!zoneRange) return undefined;
    const span = zoneRange.max - zoneRange.min;
    const values = new Map<number, number>();
    for (const { zoneId, value } of zoneValues) {
      values.set(zoneId, span > 0 ? (value - zoneRange.min) / span : 0.5);
    }
    return { values };
  }, [zoneValues, zoneRange]);

  const rampGradient = useMemo(
    () => `linear-gradient(90deg, ${getZoneFillRampStops().join(", ")})`,
    []
  );

  return (
    <div className="commute-car">
      <div className="commute-panel-title">{t("statistics.carOwnershipTitle")}</div>
      <div className="commute-car__summary">
        <span className="commute-car__metric">
          {t("statistics.carOwnershipRate")}
          <b>{formatRate(ownership.rate)}</b>
        </span>
        {/* 希望率との差が「買いたいが買えていない」量になるので並べて出す */}
        <span className="commute-car__metric commute-car__metric--sub">
          {t("statistics.carOwnershipWantRate")}
          <b>{formatRate(ownership.want_rate)}</b>
        </span>
      </div>
      <p className="commute-car__note">{t("statistics.carOwnershipNote")}</p>
      <div className="commute-car__map">
        <CityMapCanvas apiBaseUrl={apiBaseUrl} layers={MAP_LAYERS} zoneFill={zoneFill} />
        {zoneRange ? (
          <div className="commute-car__legend">
            <span>{formatRate(zoneRange.min)}</span>
            <span className="commute-car__legend-bar" style={{ background: rampGradient }} />
            <span>{formatRate(zoneRange.max)}</span>
          </div>
        ) : (
          <div className="commute-car__empty">{t("statistics.carOwnershipEmpty")}</div>
        )}
      </div>
    </div>
  );
}

export function CommuteTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<CommuteStats>(
    apiBaseUrl,
    "/api/v1/stats/commute"
  );

  const [selectedKey, setSelectedKey] = useState<string>("city");
  const [flowKind, setFlowKind] = useState<FlowKind>("total");

  const timeScope = useMemo<CommuteTimeScope | null>(() => {
    const scopes = data?.commute_time_scopes ?? [];
    return scopes.find((s) => scopeKey(s) === selectedKey) ?? scopes[0] ?? null;
  }, [data, selectedKey]);

  // --- Sankey 用データの構築 ---
  const flows = data?.od_flows[flowKind] ?? null;
  const sankeyData = useMemo(() => {
    if (!flows || flows.length === 0) {
      return { nodes: [], links: [] };
    }

    const nodes: SankeyNode[] = [];
    const nodeMap = new Map<string, number>();
    const links: { source: number; target: number; value: number }[] = [];

    const nodeIndex = (zoneId: number, name: string, isSource: boolean): number => {
      const key = `${isSource ? "src" : "dst"}:${zoneId}`;
      let index = nodeMap.get(key);
      if (index === undefined) {
        index = nodes.length;
        nodeMap.set(key, index);
        nodes.push({ name, isSource });
      }
      return index;
    };

    flows.forEach((item) => {
      links.push({
        source: nodeIndex(item.from_zone_id, item.from_name, true),
        target: nodeIndex(item.to_zone_id, item.to_name, false),
        value: item.count,
      });
    });

    return { nodes, links };
  }, [flows]);

  if (loading) {
    return <div className="commute-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data) {
    return (
      <div className="commute-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  const peopleValue = (label: string, n: number) =>
    t("statistics.commutePeopleValue", { label, n });
  const tripsValue = (label: string, n: number) =>
    t("statistics.flowTripsValue", { label, n: n.toLocaleString(undefined, { maximumFractionDigits: 1 }) });
  const flowColors = FLOW_COLORS[flowKind];
  const flowEmptyMessage = flows == null
    ? t("statistics.flowNotSimulated")
    : t(flowKind === "commute" ? "statistics.commuteFlowEmpty" : "statistics.flowEmpty");

  // --- Sankey のカスタムノード描画 ---
  const renderSankeyNode = ({ x, y, width, height, index, payload }: any) => {
    // 居住（左側）か目的地（右側）かでテキストの位置を切り替える
    const isSource = (payload as SankeyNode).isSource;

    return (
      <g key={`node-${index}`}>
        <rect
          x={x}
          y={y}
          width={width}
          height={height}
          fill={isSource ? flowColors.source : flowColors.target}
          fillOpacity={0.8}
          rx={2}
        />
        <text
          x={isSource ? x - 6 : x + width + 6}
          y={y + height / 2}
          textAnchor={isSource ? "end" : "start"}
          dominantBaseline="middle"
          fontSize="0.93rem"
          fill="#c1c9d2"
        >
          {payload.name}
        </text>
      </g>
    );
  };

  // --- Sankey のカスタムツールチップ ---
  const CustomSankeyTooltip = ({ active, payload }: any) => {
    if (active && payload && payload.length) {
      // Recharts は対象を {name, value, payload} に包んで渡す。内側の payload がリンクまたはノード本体
      const { value, payload: item } = payload[0].payload;
      if (item.source && item.target) {
        // リンクホバー
        return (
          <div className="commute-tooltip">
            <p className="commute-tooltip__route">{`${item.source.name} ➔ ${item.target.name}`}</p>
            <p className="commute-tooltip__val">{tripsValue(t("statistics.commuteFlowMovers"), value)}</p>
          </div>
        );
      } else if (item.name) {
        // ノードホバー
        const role = t((item as SankeyNode).isSource ? "statistics.commuteFlowOrigin" : "statistics.commuteFlowDestination");
        return (
          <div className="commute-tooltip">
            <p className="commute-tooltip__route">{`${item.name} (${role})`}</p>
            <p className="commute-tooltip__val">{tripsValue(t("statistics.commuteFlowTotalMovers"), value)}</p>
          </div>
        );
      }
    }
    return null;
  };

  // --- 時間分布のカスタムツールチップ ---
  const CustomBarTooltip = ({ active, payload, label }: any) => {
    if (active && payload && payload.length) {
      const w = payload[0]?.value || 0;
      const s = payload[1]?.value || 0;
      return (
        <div className="commute-tooltip">
          <p className="commute-tooltip__time">{t("statistics.commuteMinutesRange", { range: label })}</p>
          <p className="commute-tooltip__item" style={{ color: COLOR_WORKER }}>
            {peopleValue(t("statistics.commuteWorkers"), w)}
          </p>
          <p className="commute-tooltip__item" style={{ color: COLOR_STUDENT }}>
            {peopleValue(t("statistics.commuteStudents"), s)}
          </p>
          <div className="commute-tooltip__divider" />
          <p className="commute-tooltip__total">
            {peopleValue(t("statistics.commuteTotal"), w + s)}
          </p>
        </div>
      );
    }
    return null;
  };

  return (
    <div className="commute-tab">
      {/* 左ペイン: 2/5 幅 */}
      <div className="commute-tab__left">
        {/* 左上: 通勤通学時間分布 (1/3 高さ) */}
        <div className="commute-distribution">
          <div className="commute-distribution__header">
            <div className="commute-panel-title">{t("statistics.commuteTimeTitle")}</div>
            <select
              className="commute-distribution__scope"
              aria-label={t("statistics.scopeLabel")}
              value={selectedKey}
              onChange={(e) => setSelectedKey(e.target.value)}
            >
              {data.commute_time_scopes.map((s) => (
                <option key={scopeKey(s)} value={scopeKey(s)}>
                  {s.zone_id == null ? t("statistics.cityWide") : s.name}
                </option>
              ))}
            </select>
          </div>
          <div className="commute-distribution__chart">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart
                data={timeScope?.distribution ?? []}
                layout="vertical"
                margin={{ top: 5, right: 15, left: 5, bottom: 5 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                <XAxis type="number" stroke="#8ba3bb" fontSize="0.83rem" />
                <YAxis dataKey="bucket" type="category" stroke="#8ba3bb" fontSize="0.83rem" width={remPx(3.7)} />
                <Tooltip content={<CustomBarTooltip />} cursor={{ fill: "rgba(255,255,255,0.05)" }} />
                <Bar dataKey="workers" stackId="a" fill={COLOR_WORKER} radius={[0, 0, 0, 0]} name={t("statistics.commuteWorkers")} />
                <Bar dataKey="students" stackId="a" fill={COLOR_STUDENT} radius={[0, 2, 2, 0]} name={t("statistics.commuteStudents")} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>

        {/* 左下: 地区別の自動車保有率 (2/3 高さ) */}
        <CarOwnershipMap apiBaseUrl={apiBaseUrl} ownership={data.car_ownership} />
      </div>

      {/* 右ペイン: 3/5 幅 */}
      <div className="commute-tab__right">
        {/* 右側全体: 地区間の移動 */}
        <div className="commute-flow">
          <div className="commute-panel-title">{t("statistics.commuteFlowTitle")}</div>
          <nav className="commute-flow__tabs">
            {FLOW_KINDS.map((kind) => (
              <button
                key={kind}
                type="button"
                className={`commute-flow__tab${kind === flowKind ? " commute-flow__tab--active" : ""}`}
                style={{ borderBottomColor: kind === flowKind ? FLOW_COLORS[kind].source : "transparent" }}
                onClick={() => setFlowKind(kind)}
              >
                {t(FLOW_TAB_KEYS[kind])}
              </button>
            ))}
          </nav>
          <div className="commute-flow__chart">
            {sankeyData.nodes.length === 0 ? (
              <div className="commute-flow__empty">{flowEmptyMessage}</div>
            ) : (
              <ResponsiveContainer width="100%" height="100%">
                <Sankey
                  key={flowKind}
                  data={sankeyData}
                  node={renderSankeyNode}
                  link={{ stroke: flowColors.link, fill: "none" }}
                  nodePadding={12}
                  margin={{ top: 10, right: 80, bottom: 10, left: 80 }}
                >
                  <Tooltip content={<CustomSankeyTooltip />} />
                </Sankey>
              </ResponsiveContainer>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
