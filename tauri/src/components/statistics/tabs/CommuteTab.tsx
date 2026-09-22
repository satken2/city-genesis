/**
 * 移動統計タブコンポーネント
 *
 * GET /api/v1/stats/commute から通勤・通学時間、地区間流動、地区別の自動車保有率を
 * 取得して表示する (同APIが返す地区別の昼夜間人口は人口タブが使う)。
 *
 * レイアウト (左右2:3分割):
 * - 左上 (1/3 高さ): 通勤・通学時間分布 (横向き積み上げ棒グラフ)
 * - 左下 (2/3 高さ): 地区別の自動車保有率の色分け地図
 * - 右 (全高): 地区間人口移動 (Recharts Sankey ダイアグラム)
 */

import { useMemo } from "react";
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

type CommuteStats = {
  epoch: number;
  summary: Summary;
  commute_time_distribution: TimeDistPoint[];
  od_traffic: ODPoint[];
  car_ownership: CarOwnership;
};

// --- 配色 ---
const COLOR_WORKER = "#228be6";      // 通勤者: 濃い青
const COLOR_STUDENT = "#a5d8ff";     // 通学者: 淡い青 (ユーザー要望の薄い色)
const COLOR_LINK = "rgba(74, 158, 255, 0.4)"; // Sankey リンク色

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
  const { data, loading, error } = useEpochCachedStats<CommuteStats>(
    apiBaseUrl,
    "/api/v1/stats/commute"
  );

  // --- Sankey 用データの構築 ---
  const sankeyData = useMemo(() => {
    if (!data || !data.od_traffic || data.od_traffic.length === 0) {
      return { nodes: [], links: [] };
    }

    const nodes: { name: string }[] = [];
    const nodeMap = new Map<string, number>();
    const links: { source: number; target: number; value: number }[] = [];

    // 移動先の重なりや循環を防ぐため、出発地と目的地で別のサフィックスをつける
    data.od_traffic.forEach((item) => {
      // 地区名が取得できていない場合の代替
      const fromName = item.from_name || `Zone #${item.from_zone_id}`;
      const toName = item.to_name || `Zone #${item.to_zone_id}`;

      const srcName = `${fromName} (居住)`;
      const dstName = `${toName} (目的地)`;

      if (!nodeMap.has(srcName)) {
        nodeMap.set(srcName, nodes.length);
        nodes.push({ name: srcName });
      }

      if (!nodeMap.has(dstName)) {
        nodeMap.set(dstName, nodes.length);
        nodes.push({ name: dstName });
      }

      links.push({
        source: nodeMap.get(srcName)!,
        target: nodeMap.get(dstName)!,
        value: item.count,
      });
    });

    return { nodes, links };
  }, [data]);

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

  const { commute_time_distribution } = data;

  // --- Sankey のカスタムノード描画 ---
  const renderSankeyNode = ({ x, y, width, height, index, payload }: any) => {
    // 居住（左側）か目的地（右側）かでテキストの位置を切り替える
    const isSource = payload.name.endsWith("(居住)");
    const cleanName = payload.name.replace(" (居住)", "").replace(" (目的地)", "");

    return (
      <g key={`node-${index}`}>
        <rect
          x={x}
          y={y}
          width={width}
          height={height}
          fill={isSource ? "#1c7ed6" : "#74c0fc"}
          fillOpacity={0.8}
          rx={2}
        />
        <text
          x={isSource ? x - 6 : x + width + 6}
          y={y + height / 2}
          textAnchor={isSource ? "end" : "start"}
          dominantBaseline="middle"
          fontSize="10"
          fill="#c1c9d2"
        >
          {cleanName}
        </text>
      </g>
    );
  };

  // --- Sankey のカスタムツールチップ ---
  const CustomSankeyTooltip = ({ active, payload }: any) => {
    if (active && payload && payload.length) {
      const p = payload[0].payload;
      if (p.source && p.target) {
        // リンクホバー
        const srcName = p.source.name.replace(" (居住)", "");
        const dstName = p.target.name.replace(" (目的地)", "");
        return (
          <div className="commute-tooltip">
            <p className="commute-tooltip__route">{`${srcName} ➔ ${dstName}`}</p>
            <p className="commute-tooltip__val">{`移動者数: ${p.value} 人`}</p>
          </div>
        );
      } else if (p.name) {
        // ノードホバー
        const nodeName = p.name.replace(" (居住)", "").replace(" (目的地)", "");
        const role = p.name.endsWith("(居住)") ? "出発地" : "目的地";
        return (
          <div className="commute-tooltip">
            <p className="commute-tooltip__route">{`${nodeName} (${role})`}</p>
            <p className="commute-tooltip__val">{`総移動人数: ${p.value} 人`}</p>
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
          <p className="commute-tooltip__time">{`${label} 分`}</p>
          <p className="commute-tooltip__item" style={{ color: COLOR_WORKER }}>
            {`通勤者: ${w} 人`}
          </p>
          <p className="commute-tooltip__item" style={{ color: COLOR_STUDENT }}>
            {`通学者: ${s} 人`}
          </p>
          <div className="commute-tooltip__divider" />
          <p className="commute-tooltip__total">
            {`合計: ${w + s} 人`}
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
          <div className="commute-panel-title">通勤・通学時間</div>
          <div className="commute-distribution__chart">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart
                data={commute_time_distribution}
                layout="vertical"
                margin={{ top: 5, right: 15, left: 5, bottom: 5 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                <XAxis type="number" stroke="#8ba3bb" fontSize={9} />
                <YAxis dataKey="bucket" type="category" stroke="#8ba3bb" fontSize={9} width={40} />
                <Tooltip content={<CustomBarTooltip />} cursor={{ fill: "rgba(255,255,255,0.05)" }} />
                <Bar dataKey="workers" stackId="a" fill={COLOR_WORKER} radius={[0, 0, 0, 0]} name="通勤者" />
                <Bar dataKey="students" stackId="a" fill={COLOR_STUDENT} radius={[0, 2, 2, 0]} name="通学者" />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>

        {/* 左下: 地区別の自動車保有率 (2/3 高さ) */}
        <CarOwnershipMap apiBaseUrl={apiBaseUrl} ownership={data.car_ownership} />
      </div>

      {/* 右ペイン: 3/5 幅 */}
      <div className="commute-tab__right">
        {/* 右側全体: 地区間人口移動 */}
        <div className="commute-flow">
          <div className="commute-panel-title">地区間の人口移動</div>
          <div className="commute-flow__chart">
            {sankeyData.nodes.length === 0 ? (
              <div className="commute-flow__empty">移動データが存在しません（通勤・通学ペアがありません）。</div>
            ) : (
              <ResponsiveContainer width="100%" height="100%">
                <Sankey
                  data={sankeyData}
                  node={renderSankeyNode}
                  link={{ stroke: COLOR_LINK, fill: "none" }}
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
